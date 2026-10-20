using StockPlusPlus.Shared.DTOs.SalesOrder;
using System.Net;
using System.Net.Http.Json;

namespace StockPlusPlus.Test.Tests;

/// <summary>
/// Incident C: a stale version stamp after a nested save. A user has an order open (the form holds its LastSaveDate),
/// runs an action that saves a line AND changes the order (cancel a line → the order's Total is recalculated). The
/// action stamps the order with a new LastSaveDate. A form that still holds the old stamp gets a 409 on its next edit,
/// although nobody else touched the order. Each test plays the form over HTTP against SQL Server.
/// </summary>
[Collection("API Collection")]
public class SalesOrderStaleStampTests
{
    private readonly CustomWebApplicationFactory factory;

    public SalesOrderStaleStampTests(CustomWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    /// <summary>
    /// The trap, documented: this test passes before AND after the fix (IsSuccessStatusCode accepts the old 204 and the
    /// new 200). It is a guard, not the red-then-green test: the old stamp IS out of date (the order changed), so the
    /// server must keep refusing it. A stale form that were accepted would send the line back uncancelled and undo the
    /// action. It first proves its own premise: after the action the stored stamp differs from the form's.
    /// </summary>
    [Fact]
    public async Task Reproduction_AFormThatKeepsItsCopyAfterTheLineAction_Gets409OnItsNextEdit()
    {
        using var client = factory.CreateClient();
        var form = await CreateOrderAsync(client);
        var formStamp = form.LastSaveDate;

        using (var action = await client.PostAsync(CancelUrl(form, "Delivery"), null, Ct))
            Assert.True(action.IsSuccessStatusCode, $"The action failed: {(int)action.StatusCode}");

        var stored = await Entity<SalesOrderDTO>(await client.GetAsync($"api/SalesOrder/{form.ID}", Ct), HttpStatusCode.OK);
        Assert.NotEqual(formStamp, stored.LastSaveDate); // the action really moved the order's stamp

        // The user keeps editing the copy the form loaded before the action.
        form.Notes = "Deliver on Monday";
        using var save = await client.PutAsJsonAsync($"api/SalesOrder/{form.ID}", form, Ct);

        Assert.Equal(HttpStatusCode.Conflict, save.StatusCode); // nobody else touched the order
    }

    /// <summary>
    /// What must hold: the action answers with the order as it now is (lines, Total, new LastSaveDate), the form takes
    /// that version, and the user's next edit succeeds and keeps the action's result.
    /// </summary>
    [Fact]
    public async Task AfterTheLineAction_TheFormHoldsTheCurrentOrder_SoTheNextEditSucceeds()
    {
        using var client = factory.CreateClient();
        var form = await CreateOrderAsync(client);
        var loadedStamp = form.LastSaveDate;

        // The action answers with the current order, and the form replaces its copy with it.
        form = await Entity<SalesOrderDTO>(await client.PostAsync(CancelUrl(form, "Delivery"), null, Ct), HttpStatusCode.OK);
        Assert.True(form.LastSaveDate > loadedStamp);
        Assert.True(Line(form, "Delivery").IsCancelled);
        Assert.Equal(20m, form.Total);

        form.Notes = "Deliver on Monday";
        var saved = await Entity<SalesOrderDTO>(await client.PutAsJsonAsync($"api/SalesOrder/{form.ID}", form, Ct), HttpStatusCode.OK);

        Assert.Equal("Deliver on Monday", saved.Notes);
        Assert.True(Line(saved, "Delivery").IsCancelled); // the action's change survived the edit
        Assert.Equal(20m, saved.Total);
    }

    /// <summary>The fix does not weaken conflict detection: a real edit by someone else after the action still gets a 409.</summary>
    [Fact]
    public async Task AfterTheLineAction_AnEditBySomeoneElse_StillCausesA409()
    {
        using var client = factory.CreateClient();
        var form = await CreateOrderAsync(client);

        form = await Entity<SalesOrderDTO>(await client.PostAsync(CancelUrl(form, "Delivery"), null, Ct), HttpStatusCode.OK);

        var otherTab = await Entity<SalesOrderDTO>(await client.GetAsync($"api/SalesOrder/{form.ID}", Ct), HttpStatusCode.OK);
        otherTab.Notes = "Changed in another tab";
        await Entity<SalesOrderDTO>(await client.PutAsJsonAsync($"api/SalesOrder/{form.ID}", otherTab, Ct), HttpStatusCode.OK);

        form.Notes = "Deliver on Monday";
        using var save = await client.PutAsJsonAsync($"api/SalesOrder/{form.ID}", form, Ct);
        Assert.Equal(HttpStatusCode.Conflict, save.StatusCode);
    }

    // ─────────────── helpers ───────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CancelUrl(SalesOrderDTO order, string line) => $"api/SalesOrder/{order.ID}/lines/{Line(order, line).ID}/cancel";

    // Lines are found by their description: a PUT re-adds them with new IDs, and the query has no OrderBy.
    private static SalesOrderLineDTO Line(SalesOrderDTO order, string description) => order.Lines.Single(x => x.Description == description);

    // An order with two lines: 2 × 10 and 1 × 5, Total 25. Returned as the form loads it.
    private static async Task<SalesOrderDTO> CreateOrderAsync(HttpClient client)
    {
        var created = await Entity<SalesOrderDTO>(await client.PostAsJsonAsync("api/SalesOrder", new SalesOrderDTO
        {
            CustomerName = "Customer " + Guid.NewGuid().ToString("N")[..8],
            Lines =
            [
                new SalesOrderLineDTO { Description = "Chairs", Quantity = 2, UnitPrice = 10m },
                new SalesOrderLineDTO { Description = "Delivery", Quantity = 1, UnitPrice = 5m },
            ],
        }, Ct), HttpStatusCode.Created);

        var form = await Entity<SalesOrderDTO>(await client.GetAsync($"api/SalesOrder/{created.ID}", Ct), HttpStatusCode.OK);
        Assert.Equal(25m, form.Total);
        Assert.Equal(2, form.Lines.Count);
        return form;
    }

    private static async Task<T> Entity<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
            return JsonSerializer.Deserialize<ShiftEntityResponse<T>>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Entity!;
        }
    }
}
