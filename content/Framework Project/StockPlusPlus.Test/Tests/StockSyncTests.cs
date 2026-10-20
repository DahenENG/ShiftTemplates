using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using StockPlusPlus.Data.DbContext;
using StockPlusPlus.Data.Entities.StockSync;
using StockPlusPlus.Data.Sync;
using StockPlusPlus.Shared.DTOs.StockSync;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;

namespace StockPlusPlus.Test.Tests;

/// <summary>
/// Incident A: a long sync that gets cancelled. Runs against SQL Server (the test factory's database). Every test uses
/// its own feed, so tests sharing the database never see each other's rows.
/// </summary>
[Collection("API Collection")]
public class StockSyncTests
{
    private const int IncidentSize = 10_000;
    private readonly CustomWebApplicationFactory factory;

    public StockSyncTests(CustomWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    // ───────────────────────────── the incident, reproduced (naive) ─────────────────────────────

    // The production message, explained: "Operation cancelled by user" is not a person. It is SQL Server's answer when
    // the CancellationToken fires while one of its commands is still running (here a 5-second WAITFOR, cancelled after
    // 0.2 s). EF sends a big SaveChanges as many short commands, so locally the token is usually noticed between them
    // and surfaces as TaskCanceledException instead; on Azure SQL, long in-flight commands make the SqlException likely.
    [Fact]
    public async Task OperationCancelledByUser_IsWhatSqlServerSaysWhenTheTokenFiresDuringACommand()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DB>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var error = await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync("WAITFOR DELAY '00:00:05'", cts.Token));

        var sql = Assert.IsType<SqlException>(Innermost(error!));
        Log($"SQL Server said: {sql.Message}");
        Assert.Contains("Operation cancelled by user", sql.Message);
    }

    [Fact]
    public async Task Naive_CancelledWhileSaving_LosesEverything_AndTheRetryStartsFromZero()
    {
        var feed = await SeedAsync(IncidentSize);
        using var cts = new CancellationTokenSource();

        // Cancel the moment SQL Server has executed the save's first write command: rows really were written inside the
        // transaction when the cancel arrives. Deterministic: no timer, the interceptor decides the exact point.
        var cancelAfterFirstWrite = new CancelAfterFirstWriteInterceptor(cts);

        var error = await Record.ExceptionAsync(() => WithSync(cancelAfterFirstWrite, sync => sync.RunNaiveAsync(feed, cts.Token)));

        Assert.NotNull(error);
        Assert.True(IsCancellation(error), $"Expected a cancellation, got {error!.GetType().Name}: {error.Message}");
        Assert.Equal(1, cancelAfterFirstWrite.WritesBeforeCancel);
        Log($"Naive run stopped with {error.GetType().Name}: {Innermost(error).Message}");

        // One transaction: the rows already written were rolled back with the rest, every row is still pending.
        Assert.Equal(0, await CountTargetsAsync(feed));
        Assert.Equal(IncidentSize, await CountRowsAsync(feed, StockSyncRowStatus.Pending));

        // So the retry starts again from zero: it has to process all 10,000 rows.
        var processedByRetry = await WithSync(sync => sync.RunNaiveAsync(feed, CancellationToken.None));
        Assert.Equal(IncidentSize, processedByRetry);
    }

    [Fact]
    public async Task Naive_OneRowTheDatabaseRefuses_FailsTheWholeSync_OnEveryRun()
    {
        var feed = await SeedAsync(1_000, (row, i) => { if (i == 500) row.Name = null; }); // Name is NOT NULL in SyncedStock

        for (var run = 1; run <= 2; run++)
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => WithSync(sync => sync.RunNaiveAsync(feed, CancellationToken.None)));
            Assert.Equal(0, await CountTargetsAsync(feed));
            Assert.Equal(1_000, await CountRowsAsync(feed, StockSyncRowStatus.Pending));
        }
    }

    // ───────────────────────────── the fix ─────────────────────────────

    [Fact]
    public async Task Fixed_CancelledMidRun_KeepsFinishedBatches_ReportsWhatIsLeft_AndTheNextRunResumes()
    {
        var feed = await SeedAsync(IncidentSize);
        var rowIds = await RowIdsAsync(feed);
        using var cts = new CancellationTokenSource();

        // Cancel right after the 4th batch is committed.
        var progress = new InlineProgress<StockSyncResult>(r => { if (r.ProcessedThisRun == 2_000) cts.Cancel(); });
        var first = await WithSync(sync => sync.RunAsync(feed, batchSize: 500, cts.Token, progress));

        // The cancelled run is an answer, not an error: how far it got and what is left.
        Assert.Equal(StockSyncRunStatus.Cancelled, first.Status);
        Assert.Equal(2_000, first.ProcessedThisRun);
        Assert.Equal(2_000, first.Done);
        Assert.Equal(8_000, first.Remaining);
        Assert.Equal(rowIds[1_999], first.LastCommittedRowId);

        // Work finished before the cancellation is not lost.
        Assert.Equal(2_000, await CountTargetsAsync(feed));

        // The next run continues where the last one stopped: only the 8,000 left are processed.
        var second = await WithSync(sync => sync.RunAsync(feed, batchSize: 500, CancellationToken.None));

        Assert.Equal(StockSyncRunStatus.Completed, second.Status);
        Assert.Equal(8_000, second.ProcessedThisRun);
        Assert.Equal(IncidentSize, second.Done);
        Assert.Equal(0, second.Remaining);
        Assert.Equal(rowIds[^1], second.LastCommittedRowId);
        Assert.Equal(IncidentSize, await CountTargetsAsync(feed));
        Assert.Equal(IncidentSize, first.ProcessedThisRun + second.ProcessedThisRun); // no row processed twice
    }

    [Fact]
    public async Task Fixed_CancelledWhileABatchIsBeingSaved_KeepsThatBatch_AndStopsBeforeTheNext()
    {
        const int batchSize = 2_500;
        var feed = await SeedAsync(IncidentSize);
        var rowIds = await RowIdsAsync(feed);
        using var cts = new CancellationTokenSource();

        // Cancel exactly as the 2nd batch's SaveChanges starts. A batch whose write is cut off would be rolled back on
        // every run that is shorter than one batch (the endpoint's time budget): the original incident again. So the
        // write finishes, and the run stops before the 3rd batch.
        var cancelOnSecondSave = new CancelOnSaveInterceptor(cts, onSave: 2);
        var first = await WithSync(cancelOnSecondSave, sync => sync.RunAsync(feed, batchSize, cts.Token));
        Log($"First run: {first.Status}, done {first.Done}, remaining {first.Remaining}");

        Assert.True(cancelOnSecondSave.Cancelled);
        Assert.Equal(StockSyncRunStatus.Cancelled, first.Status);
        Assert.Equal(2 * batchSize, first.ProcessedThisRun);
        Assert.Equal(2 * batchSize, first.Done);
        Assert.Equal(IncidentSize - 2 * batchSize, first.Remaining);
        Assert.Equal(rowIds[2 * batchSize - 1], first.LastCommittedRowId);
        Assert.Equal(2 * batchSize, await CountTargetsAsync(feed));

        var second = await WithSync(sync => sync.RunAsync(feed, batchSize, CancellationToken.None));
        Assert.Equal(StockSyncRunStatus.Completed, second.Status);
        Assert.Equal(IncidentSize, second.Done);
        Assert.Equal(IncidentSize, await CountTargetsAsync(feed));
    }

    [Fact]
    public async Task Fixed_RowsThatCanNeverBeProcessed_AreSkippedWithAReason_AndTheSyncFinishes()
    {
        var feed = await SeedAsync(1_000, (row, i) =>
        {
            if (i == 10) row.Name = null;                   // fails validation
            if (i == 20) row.Quantity = -5;                 // fails validation
            if (i == 30) row.ExternalCode = "";             // fails validation
            if (i == 40) row.Name = new string('x', 300);   // passes validation, refused by SQL Server (max 100)
        });

        var result = await WithSync(sync => sync.RunAsync(feed, batchSize: 100, CancellationToken.None));

        Assert.Equal(StockSyncRunStatus.Completed, result.Status);
        Assert.Equal(996, result.ProcessedThisRun);
        Assert.Equal(4, result.FailedThisRun);
        Assert.Equal(996, result.Done);
        Assert.Equal(4, result.Failed);
        Assert.Equal(0, result.Remaining);
        Assert.Equal(996, await CountTargetsAsync(feed));

        var reasons = result.Failures.Select(f => f.Reason).ToList();
        Assert.Contains("Name is required.", reasons);
        Assert.Contains("Quantity cannot be negative.", reasons);
        Assert.Contains("ExternalCode is required.", reasons);
        Assert.Contains("A value is too long for its column.", reasons);

        // Failures go back to API callers: a short reason, never SQL Server's message with table and column names.
        Assert.DoesNotContain(reasons, r => r.Contains("SyncedStocks") || r.Contains("truncated"));

        // Failed rows are settled, not retried forever: the next run has nothing to do.
        var again = await WithSync(sync => sync.RunAsync(feed, batchSize: 100, CancellationToken.None));
        Assert.Equal(StockSyncRunStatus.Completed, again.Status);
        Assert.Equal(0, again.ProcessedThisRun);
        Assert.Equal(0, again.FailedThisRun);
    }

    [Fact]
    public async Task Fixed_ATemporaryDatabaseError_LeavesTheRowPending_AndTheNextRunFinishesIt()
    {
        var feed = await SeedAsync(1_000);

        // A deadlock or lock timeout can't be produced on cue, so the interceptor makes SQL Server raise an error
        // (THROW 50001) for every write that touches SKU-00500. Only duplicate key, truncation and NULL-into-NOT-NULL
        // are permanent; any other error must leave the row Pending, not mark it Failed forever.
        var temporaryFailure = new TemporaryFailureInterceptor("SKU-00500");
        var first = await WithSync(temporaryFailure, sync => sync.RunAsync(feed, batchSize: 100, CancellationToken.None));

        Assert.Equal(StockSyncRunStatus.Completed, first.Status);
        Assert.Equal(999, first.ProcessedThisRun);
        Assert.Equal(0, first.FailedThisRun);
        Assert.Equal(1, first.LeftPendingThisRun);
        Assert.Equal(0, first.Failed);
        Assert.Equal(1, first.Remaining);
        Assert.Equal(2, temporaryFailure.Raised); // the batch save, then the row on its own: tried once in this run
        Assert.Equal(1, await CountRowsAsync(feed, StockSyncRowStatus.Pending));

        // The error has passed: the next run picks the row up.
        var second = await WithSync(sync => sync.RunAsync(feed, batchSize: 100, CancellationToken.None));

        Assert.Equal(StockSyncRunStatus.Completed, second.Status);
        Assert.Equal(1, second.ProcessedThisRun);
        Assert.Equal(1_000, second.Done);
        Assert.Equal(0, second.Remaining);
    }

    // Why the run reads with a keyset cursor. The same 1,000 rows, where SKU-00500 keeps hitting a temporary error and
    // so stays Pending, read three ways:
    //   - "the first 100 Pending rows" again and again re-reads that row forever: only the batch cap stops it;
    //   - OFFSET over the Pending rows ends, but has skipped half the feed: processed rows leave the set it counts in;
    //   - the keyset cursor reads every row once and finishes with only the stuck row left for the next run.
    [Fact]
    public async Task Paging_ARowThatStaysPending_SpinsTheFirstPendingLoop_OffsetSkipsRows_KeysetFinishes()
    {
        var queueFeed = await SeedAsync(1_000);
        var queue = await WithSync(new TemporaryFailureInterceptor("SKU-00500"),
            sync => sync.RunAsync(queueFeed, batchSize: 100, CancellationToken.None, paging: StockSyncPaging.FirstPending, maxBatches: 50));
        Log($"First-pending loop: {queue.Status} after {queue.Batches} batches, done {queue.Done}, remaining {queue.Remaining}");

        Assert.Equal(StockSyncRunStatus.BatchLimitReached, queue.Status);
        Assert.Equal(50, queue.Batches);              // 11 batches do all the work; 39 more read only the stuck row
        Assert.Equal(999, queue.Done);
        Assert.Equal(1, queue.Remaining);

        var offsetFeed = await SeedAsync(1_000);
        var offset = await WithSync(new TemporaryFailureInterceptor("SKU-00500"),
            sync => sync.RunAsync(offsetFeed, batchSize: 100, CancellationToken.None, paging: StockSyncPaging.Offset, maxBatches: 50));
        Log($"Offset loop: {offset.Status} after {offset.Batches} batches, done {offset.Done}, remaining {offset.Remaining}");

        Assert.Equal(StockSyncRunStatus.Completed, offset.Status);
        Assert.Equal(5, offset.Batches);
        Assert.Equal(500, offset.Done);               // rows 100-199, 300-399, 500-599, 700-799, 900-999 never read
        Assert.Equal(500, offset.Remaining);

        var keysetFeed = await SeedAsync(1_000);
        var keyset = await WithSync(new TemporaryFailureInterceptor("SKU-00500"),
            sync => sync.RunAsync(keysetFeed, batchSize: 100, CancellationToken.None, maxBatches: 50));
        Log($"Keyset: {keyset.Status} after {keyset.Batches} batches, done {keyset.Done}, remaining {keyset.Remaining}");

        Assert.Equal(StockSyncRunStatus.Completed, keyset.Status);
        Assert.Equal(10, keyset.Batches);
        Assert.Equal(999, keyset.Done);
        Assert.Equal(1, keyset.LeftPendingThisRun);
        Assert.Equal(1, keyset.Remaining);
    }

    [Fact]
    public async Task Endpoint_RunsTheSync_AndAnswersWithTheResult()
    {
        var feed = await SeedAsync(300);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync($"api/stock-sync/{feed}?batchSize=100", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<StockSyncResult>(TestContext.Current.CancellationToken);
        Assert.Equal(StockSyncRunStatus.Completed, result!.Status);
        Assert.Equal(300, result.Done);
        Assert.Equal(0, result.Remaining);
    }

    [Fact]
    public async Task Endpoint_TheNaiveSyncIsNotExposed()
    {
        var feed = await SeedAsync(10);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync($"api/stock-sync/{feed}/naive", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(10, await CountRowsAsync(feed, StockSyncRowStatus.Pending));
    }

    // ───────────────────────────── helpers ─────────────────────────────

    // A fresh scope (and DbContext) per call, like a request: no state leaks between runs.
    private async Task<T> WithSync<T>(Func<StockSyncService, Task<T>> run)
    {
        using var scope = factory.Services.CreateScope();
        return await run(scope.ServiceProvider.GetRequiredService<StockSyncService>());
    }

    // The same, with one extra EF interceptor on the sync's DbContext (the app's own options, plus the interceptor).
    private async Task<T> WithSync<T>(IInterceptor interceptor, Func<StockSyncService, Task<T>> run)
    {
        using var scope = factory.Services.CreateScope();
        var options = new DbContextOptionsBuilder<DB>(scope.ServiceProvider.GetRequiredService<DbContextOptions<DB>>())
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new DB(options);
        return await run(new StockSyncService(db));
    }

    private async Task<string> SeedAsync(int count, Action<StockSyncInboxRow, int>? customize = null)
    {
        var feed = "test-" + Guid.NewGuid().ToString("N")[..12];

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DB>();
        var rows = Enumerable.Range(0, count).Select(i =>
        {
            var row = new StockSyncInboxRow { Feed = feed, ExternalCode = $"SKU-{i:D5}", Name = $"Item {i}", Quantity = i % 50 };
            customize?.Invoke(row, i);
            return row;
        });

        db.StockSyncInbox.AddRange(rows);
        await db.SaveChangesAsync();
        return feed;
    }

    private async Task<int> CountTargetsAsync(string feed)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DB>().SyncedStocks.CountAsync(x => x.Feed == feed);
    }

    private async Task<int> CountRowsAsync(string feed, StockSyncRowStatus status)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DB>().StockSyncInbox.CountAsync(x => x.Feed == feed && x.Status == status);
    }

    private async Task<List<long>> RowIdsAsync(string feed)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DB>().StockSyncInbox
            .Where(x => x.Feed == feed).OrderBy(x => x.ID).Select(x => x.ID).ToListAsync();
    }

    private static bool IsCancellation(Exception e)
        => e is OperationCanceledException
        || Innermost(e) is OperationCanceledException
        || (Innermost(e) is SqlException sql && sql.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase));

    private static Exception Innermost(Exception e)
    {
        while (e.InnerException is not null)
            e = e.InnerException;
        return e;
    }

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    // Progress<T> posts to the thread pool; tests need the callback to run before the sync continues.
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    // Cancels as the Nth SaveChanges starts (before any of its commands is sent).
    private sealed class CancelOnSaveInterceptor(CancellationTokenSource cts, int onSave) : SaveChangesInterceptor
    {
        private int saves;

        public bool Cancelled { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (++saves == onSave)
            {
                cts.Cancel();
                Cancelled = true;
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    // Makes SQL Server refuse every write that carries the given ExternalCode with a non-permanent error number, the
    // way a deadlock or lock timeout would. THROW runs on the server, so the sync sees a real SqlException.
    private sealed class TemporaryFailureInterceptor(string externalCode) : DbCommandInterceptor
    {
        public int Raised { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Inject(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Inject(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Inject(DbCommand command)
        {
            var isWrite = command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("MERGE", StringComparison.OrdinalIgnoreCase);

            if (isWrite && command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, externalCode)))
            {
                command.CommandText = "THROW 50001, N'Simulated temporary failure', 1;" + command.CommandText;
                Raised++;
            }
        }
    }

    // Cancels right after SQL Server has executed the first INSERT/UPDATE command: the save is part-way through its
    // transaction when the token fires.
    private sealed class CancelAfterFirstWriteInterceptor(CancellationTokenSource cts) : DbCommandInterceptor
    {
        public int WritesBeforeCancel { get; private set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            AfterCommand(command);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            AfterCommand(command);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        private void AfterCommand(DbCommand command)
        {
            var isWrite = command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("MERGE", StringComparison.OrdinalIgnoreCase);

            if (isWrite && !cts.IsCancellationRequested)
            {
                WritesBeforeCancel++;
                cts.Cancel();
            }
        }
    }
}
