using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using StockPlusPlus.Data.DbContext;
using StockPlusPlus.Data.Entities.StockSync;
using StockPlusPlus.Shared.DTOs.StockSync;

namespace StockPlusPlus.Data.Sync;

/// <summary>
/// Incident A: a long sync that gets cancelled. Syncs a feed's <see cref="StockSyncInboxRow"/>s into
/// <see cref="SyncedStock"/>, two ways:
/// <list type="bullet">
/// <item><see cref="RunNaiveAsync"/>: the way the production job was written. Everything in one SaveChanges, so a
/// cancellation (SqlException "Operation cancelled by user") rolls back ALL of it and every retry starts from zero; one
/// bad row fails the whole run, every time.</item>
/// <item><see cref="RunAsync"/>: the fix. Batches, each committed with its rows' Done/Failed marks in one transaction.
/// Cancellation is honoured between batches only: a batch that has started writing always finishes, so a run shorter
/// than one batch still makes progress. A cancelled run reports progress and the next run continues at the first
/// Pending row; bad rows are marked Failed with a reason and skipped.</item>
/// </list>
/// <para>
/// Reading: a keyset cursor (<c>WHERE ID &gt; @lastId ORDER BY ID</c>, moved past every batch). Trade-off: the cursor
/// guarantees each run moves forward even when a row stays Pending, but a row left Pending is only retried by the
/// next run, and the key must be unique, ever-increasing and indexed. Reading "the first N Pending rows" again
/// instead retries such a row at once, but spins forever on one that never resolves; an OFFSET over the Pending rows
/// skips rows, because the set shrinks under the offset as the loop works. Both are kept in
/// <see cref="StockSyncPaging"/> to show this.
/// </para>
/// </summary>
public class StockSyncService
{
    public const int DefaultBatchSize = 500;
    public const int MaxBatchSize = 5000;
    private const int MaxReportedFailures = 20;

    private readonly DB db;

    public StockSyncService(DB db)
    {
        this.db = db;
    }

    // ─────────────────────────────────────── naive: the incident ───────────────────────────────────────

    /// <summary>
    /// The incident's shape, kept to reproduce it: load every pending row, apply every row, then ONE SaveChanges
    /// (one transaction) for all of them, cancellable mid-write. Returns how many rows it synced.
    /// </summary>
    public async Task<int> RunNaiveAsync(string feed, CancellationToken cancellationToken)
    {
        var rows = await PendingRows(feed).OrderBy(x => x.ID).ToListAsync(cancellationToken);
        var targets = await LoadTargetsAsync(feed, rows, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var row in rows)
            Apply(feed, row, targets, now); // no validation: whatever the feed sends is written

        await db.SaveChangesAsync(cancellationToken); // all or nothing: a cancel here loses every row

        return rows.Count;
    }

    // ─────────────────────────────────────── fixed: resumable ───────────────────────────────────────

    /// <summary>
    /// Syncs the feed in batches of <paramref name="batchSize"/> until no row is Pending or
    /// <paramref name="cancellationToken"/> fires. Each batch's results and its rows' Done/Failed marks are saved
    /// in ONE transaction, so a batch is either fully kept or fully rolled back (and retried by the next run).
    /// <paramref name="cancellationToken"/> is checked between batches and may stop a read, but never a batch's write.
    /// Cancellation is not an error: the result says <see cref="StockSyncRunStatus.Cancelled"/> and how much is left.
    /// <paramref name="progress"/> is reported after every committed batch.
    /// </summary>
    public async Task<StockSyncResult> RunAsync(
        string feed,
        int batchSize = DefaultBatchSize,
        CancellationToken cancellationToken = default,
        IProgress<StockSyncResult>? progress = null,
        StockSyncPaging paging = StockSyncPaging.Keyset,
        int? maxBatches = null)
    {
        batchSize = Math.Clamp(batchSize, 1, MaxBatchSize);
        var result = new StockSyncResult { Feed = feed };
        long cursor = 0;
        var rowsRead = 0;

        try
        {
            while (true)
            {
                // Checked between batches: a cancel that arrives now costs no work at all.
                cancellationToken.ThrowIfCancellationRequested();

                if (result.Batches == maxBatches)
                {
                    result.Status = StockSyncRunStatus.BatchLimitReached;
                    break;
                }

                db.ChangeTracker.Clear();
                var pending = PendingRows(feed);
                var page = paging switch
                {
                    StockSyncPaging.Keyset => pending.Where(x => x.ID > cursor).OrderBy(x => x.ID),
                    StockSyncPaging.FirstPending => pending.OrderBy(x => x.ID),
                    StockSyncPaging.Offset => pending.OrderBy(x => x.ID).Skip(rowsRead),
                    _ => throw new ArgumentOutOfRangeException(nameof(paging)),
                };
                var batch = await page.Take(batchSize).ToListAsync(cancellationToken);
                if (batch.Count == 0)
                {
                    result.Status = StockSyncRunStatus.Completed;
                    break;
                }

                result.Batches++;
                rowsRead += batch.Count;
                cursor = batch[^1].ID; // past every row read, whether it was committed or left Pending

                await CommitBatchAsync(feed, batch, result, cancellationToken);
                progress?.Report(result);
            }
        }
        catch (Exception e) when (cancellationToken.IsCancellationRequested && IsCancellation(e))
        {
            // Stopped between batches, or during a read (as OperationCanceledException, or SqlException "Operation
            // cancelled by user" when the read was in flight). No write is ever cut off, so every batch that started
            // saving is committed.
            result.Status = StockSyncRunStatus.Cancelled;
        }

        // Not the caller's token: a cancelled run must still be able to say where the feed stands.
        db.ChangeTracker.Clear();
        await FillTotalsAsync(result, CancellationToken.None);
        return result;
    }

    private async Task CommitBatchAsync(string feed, List<StockSyncInboxRow> batch, StockSyncResult result, CancellationToken cancellationToken)
    {
        var targets = await LoadTargetsAsync(feed, batch, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var row in batch)
        {
            var problem = Validate(row);
            if (problem is null)
                Apply(feed, row, targets, now);
            else
                MarkFailed(row, problem, now);
        }

        try
        {
            // Not the caller's token: a cancel that arrives mid-write would roll the batch back, and a run whose budget
            // is shorter than one batch would then lose that batch on every run (the incident again). The batch is
            // bounded by MaxBatchSize, and the loop stops at the next ThrowIfCancellationRequested.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException)
        {
            // The database refused the batch: a row validation could not foresee, or a temporary error. Failing the
            // batch would fail it on every run, so retry it one row at a time and judge each row on its own.
            db.ChangeTracker.Clear();
            await CommitRowByRowAsync(feed, batch.Select(x => x.ID).ToList(), result, cancellationToken);
            return;
        }

        Count(batch, result);
    }

    private async Task CommitRowByRowAsync(string feed, List<long> rowIds, StockSyncResult result, CancellationToken cancellationToken)
    {
        foreach (var id in rowIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            db.ChangeTracker.Clear();
            var row = await db.Set<StockSyncInboxRow>().SingleAsync(x => x.ID == id, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var problem = Validate(row);

            if (problem is null)
                Apply(feed, row, await LoadTargetsAsync(feed, [row], cancellationToken), now);
            else
                MarkFailed(row, problem, now);

            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (DbUpdateException e) when (PermanentReason(e) is { } reason)
            {
                // A retry can never fix this row: settle it as Failed so the sync can finish.
                db.ChangeTracker.Clear();
                row = await db.Set<StockSyncInboxRow>().SingleAsync(x => x.ID == id, CancellationToken.None);
                MarkFailed(row, reason, now);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (DbUpdateException)
            {
                // Anything else (a deadlock, a lock timeout, a dropped connection) may pass: leave the row Pending for
                // the next run. The keyset cursor has already moved past it, so this run doesn't read it again.
                db.ChangeTracker.Clear();
                result.LeftPendingThisRun++;
                continue;
            }

            Count([row], result);
        }
    }

    // ─────────────────────────────────────── shared pieces ───────────────────────────────────────

    private IQueryable<StockSyncInboxRow> PendingRows(string feed)
        => db.Set<StockSyncInboxRow>().Where(x => x.Feed == feed && x.Status == StockSyncRowStatus.Pending);

    private async Task<Dictionary<string, SyncedStock>> LoadTargetsAsync(string feed, IReadOnlyCollection<StockSyncInboxRow> rows, CancellationToken cancellationToken)
    {
        var codes = rows.Select(x => x.ExternalCode).Distinct().ToList();

        return await db.Set<SyncedStock>()
            .Where(x => x.Feed == feed && codes.Contains(x.ExternalCode))
            .ToDictionaryAsync(x => x.ExternalCode, cancellationToken);
    }

    // Insert or update the target, and mark the row Done in the same unit of work.
    private void Apply(string feed, StockSyncInboxRow row, Dictionary<string, SyncedStock> targets, DateTimeOffset now)
    {
        if (!targets.TryGetValue(row.ExternalCode, out var target))
        {
            target = new SyncedStock { Feed = feed, ExternalCode = row.ExternalCode };
            db.Add(target);
            targets[row.ExternalCode] = target;
        }

        target.Name = row.Name!;
        target.Quantity = row.Quantity;
        target.LastSyncedAt = now;

        row.Status = StockSyncRowStatus.Done;
        row.Error = null;
        row.ProcessedAt = now;
    }

    // Rules the feed must follow. A row that breaks one can never be processed, however often it is retried.
    private static string? Validate(StockSyncInboxRow row)
    {
        if (string.IsNullOrWhiteSpace(row.ExternalCode))
            return "ExternalCode is required.";
        if (string.IsNullOrWhiteSpace(row.Name))
            return "Name is required.";
        if (row.Quantity < 0)
            return "Quantity cannot be negative.";
        return null;
    }

    private static void MarkFailed(StockSyncInboxRow row, string reason, DateTimeOffset now)
    {
        row.Status = StockSyncRowStatus.Failed;
        row.Error = reason;
        row.ProcessedAt = now;
    }

    private static void Count(IEnumerable<StockSyncInboxRow> committed, StockSyncResult result)
    {
        foreach (var row in committed)
        {
            if (row.Status == StockSyncRowStatus.Done)
            {
                result.ProcessedThisRun++;
            }
            else if (row.Status == StockSyncRowStatus.Failed)
            {
                result.FailedThisRun++;
                if (result.Failures.Count < MaxReportedFailures)
                    result.Failures.Add(new StockSyncFailure { RowId = row.ID, ExternalCode = row.ExternalCode, Reason = row.Error! });
            }

            result.LastCommittedRowId = Math.Max(result.LastCommittedRowId ?? 0, row.ID);
        }
    }

    private async Task FillTotalsAsync(StockSyncResult result, CancellationToken cancellationToken)
    {
        var counts = await db.Set<StockSyncInboxRow>()
            .Where(x => x.Feed == result.Feed)
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int CountOf(StockSyncRowStatus status) => counts.FirstOrDefault(x => x.Status == status)?.Count ?? 0;

        result.Done = CountOf(StockSyncRowStatus.Done);
        result.Failed = CountOf(StockSyncRowStatus.Failed);
        result.Remaining = CountOf(StockSyncRowStatus.Pending);
    }

    /// <summary>
    /// A short reason when SQL Server refused the row for good, or null when a retry might succeed. Only these are
    /// permanent: duplicate key (2627, 2601), value too long (8152, and 2628, its newer form since SQL Server 2019),
    /// NULL into a NOT NULL column (515). The reason is returned to API callers, so it never carries the server's
    /// message with table and column names.
    /// </summary>
    private static string? PermanentReason(DbUpdateException e)
        => (Innermost(e) as SqlException)?.Number switch
        {
            2627 or 2601 => "Duplicate: another row already has this key.",
            8152 or 2628 => "A value is too long for its column.",
            515 => "A required value is missing.",
            _ => null,
        };

    private static bool IsCancellation(Exception e)
        => e is OperationCanceledException or SqlException || e.InnerException is SqlException or OperationCanceledException;

    private static Exception Innermost(Exception e)
    {
        while (e.InnerException is not null)
            e = e.InnerException;
        return e;
    }
}

/// <summary>How <see cref="StockSyncService.RunAsync"/> reads the next batch.</summary>
public enum StockSyncPaging
{
    /// <summary><c>WHERE ID &gt; @lastId ORDER BY ID</c>: every row is read at most once per run. The default.</summary>
    Keyset,

    /// <summary>
    /// The first N Pending rows, again and again (the Day 11 loop). Ends only when every row leaves Pending, so a row
    /// that stays Pending is read forever. Kept to show why the cursor is needed.
    /// </summary>
    FirstPending,

    /// <summary>
    /// <c>OFFSET rowsRead</c> over the Pending rows. Skips rows, because processed rows leave the set the offset counts
    /// in. Kept to show why OFFSET fails here.
    /// </summary>
    Offset,
}
