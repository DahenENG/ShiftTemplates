using Microsoft.AspNetCore.Mvc;
using StockPlusPlus.Data.Sync;

namespace StockPlusPlus.API.Endpoints;

/// <summary>Incident A sample: run the stock sync over HTTP. See <see cref="StockSyncService"/>.</summary>
public static class StockSyncEndpoints
{
    public static IEndpointRouteBuilder MapStockSyncEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("api/stock-sync").RequireAuthorization();

        // POST api/stock-sync/{feed}?batchSize=500&maxSeconds=20
        // Runs until the feed is done, the client disconnects, or the optional time budget runs out. A run stopped by
        // the budget answers 200 with Status = Cancelled, Done and Remaining: call again to continue. The budget keeps
        // a run shorter than a gateway or command timeout, so it ends with an answer instead of an error.
        group.MapPost("{feed}", async (
            string feed,
            [FromQuery] int? batchSize,
            [FromQuery] int? maxSeconds,
            StockSyncService sync,
            CancellationToken requestAborted) =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            if (maxSeconds is > 0)
                budget.CancelAfter(TimeSpan.FromSeconds(maxSeconds.Value));

            var result = await sync.RunAsync(feed, batchSize ?? StockSyncService.DefaultBatchSize, budget.Token);
            return Results.Ok(result);
        });

        // POST api/stock-sync/{feed}/naive — the incident's version, kept to reproduce it. Do not copy.
        group.MapPost("{feed}/naive", async (string feed, StockSyncService sync, CancellationToken requestAborted) =>
            Results.Ok(new { Processed = await sync.RunNaiveAsync(feed, requestAborted) }));

        return endpoints;
    }
}
