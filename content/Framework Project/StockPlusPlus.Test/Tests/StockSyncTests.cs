using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StockPlusPlus.Data.DbContext;
using StockPlusPlus.Data.Entities.StockSync;
using StockPlusPlus.Data.Sync;
using StockPlusPlus.Shared.DTOs.StockSync;
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

        // Cancel shortly after the single big SaveChanges has started, while SQL Server is writing.
        var stage = new InlineProgress<string>(s => { if (s == "saving") cts.CancelAfter(TimeSpan.FromMilliseconds(100)); });

        var error = await Record.ExceptionAsync(() => WithSync(sync => sync.RunNaiveAsync(feed, cts.Token, stage)));

        Assert.NotNull(error);
        Assert.True(IsCancellation(error), $"Expected a cancellation, got {error!.GetType().Name}: {error.Message}");
        Log($"Naive run stopped with {error.GetType().Name}: {Innermost(error).Message}");

        // One transaction: nothing of the run survived, every row is still pending.
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
    public async Task Fixed_CancelledWhileSqlServerIsWriting_RollsBackOnlyThatBatch()
    {
        const int batchSize = 2_500;
        var feed = await SeedAsync(IncidentSize);
        using var cts = new CancellationTokenSource();

        // After the first batch commits, cancel 50 ms later: the next batch's SaveChanges is usually in flight.
        var progress = new InlineProgress<StockSyncResult>(r => { if (r.ProcessedThisRun == batchSize) cts.CancelAfter(50); });
        var first = await WithSync(sync => sync.RunAsync(feed, batchSize, cts.Token, progress));
        Log($"First run: {first.Status}, done {first.Done}, remaining {first.Remaining}");

        // Wherever the cancel landed, the database never holds part of a batch.
        Assert.True(first.Status == StockSyncRunStatus.Cancelled || first.Remaining == 0);
        Assert.Equal(0, first.Done % batchSize);
        Assert.Equal(first.Done, await CountTargetsAsync(feed));
        Assert.Equal(IncidentSize - first.Done, first.Remaining);

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
        Assert.Contains(reasons, r => r.StartsWith("Rejected by the database:"));

        // Failed rows are settled, not retried forever: the next run has nothing to do.
        var again = await WithSync(sync => sync.RunAsync(feed, batchSize: 100, CancellationToken.None));
        Assert.Equal(StockSyncRunStatus.Completed, again.Status);
        Assert.Equal(0, again.ProcessedThisRun);
        Assert.Equal(0, again.FailedThisRun);
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
}
