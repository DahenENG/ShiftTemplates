using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RichardSzalay.MockHttp;
using ShiftSoftware.ShiftBlazor.Components;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.TypeAuth.Core;
using StockPlusPlus.Shared.ActionTrees;
using StockPlusPlus.Shared.DTOs.SalesOrder;
using StockPlusPlus.Web.Pages.SalesOrder;
using Xunit;

namespace StockPlusPlus.Web.Tests;

/// <summary>
/// Incident C, the Blazor side: after "Cancel line", the form must hold the order as it now is, so the user's next save
/// carries the new LastSaveDate. (The API side is covered by SalesOrderStaleStampTests.)
/// </summary>
public class SalesOrderFormTests : ShiftBlazorTestContext
{
    private static readonly DateTimeOffset LoadedStamp = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StampAfterAction = LoadedStamp.AddMinutes(1);

    [Fact]
    public async Task AfterCancelLine_TheFormTakesTheActionsAnswer_AndItsNextSaveCarriesTheNewStamp()
    {
        var mock = Services.AddMockHttpClient();
        Services.RemoveAll<ITypeAuthService>();
        var grants = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [nameof(StockPlusPlusActionTree)] = new Dictionary<string, object>
            { [nameof(StockPlusPlusActionTree.SalesOrder)] = new[] { Access.Read, Access.Write } }
        });
        Services.AddSingleton<ITypeAuthService>(new TypeAuthContextBuilder().AddAccessTree(grants)
            .AddActionTree<StockPlusPlusActionTree>().Build());

        // The server: GET answers with the order as it is at that moment; the action cancels "Delivery".
        var cancelled = false;
        var gets = 0;
        mock.When(HttpMethod.Get, "http://localhost/SalesOrder/7")
            .RespondJson(() => { gets++; return new ShiftEntityResponse<SalesOrderDTO>(Order(cancelled)); });
        mock.When(HttpMethod.Post, "http://localhost/SalesOrder/7/lines/22/cancel")
            .RespondJson(() => { cancelled = true; return new ShiftEntityResponse<SalesOrderDTO>(Order(cancelled: true)); });

        string? putBody = null;
        mock.When(HttpMethod.Put, "http://localhost/SalesOrder/7").Respond(async request =>
        {
            putBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new ShiftEntityResponse<SalesOrderDTO>(Order(cancelled: true))),
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var cut = Render<SalesOrderForm>(p => p.Add(x => x.Key, "7"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("button").Count(b => b.TextContent.Contains("Cancel line"))));
        Assert.Equal(1, gets);

        // The user cancels the "Delivery" line (found by its text, not its position).
        cut.FindAll("tr").Single(r => r.TextContent.Contains("Delivery")).QuerySelector("button")!.Click();

        var form = cut.FindComponent<ShiftEntityForm<SalesOrderDTO>>();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(StampAfterAction, form.Instance.Value.LastSaveDate);
            Assert.True(form.Instance.Value.Lines.Single(l => l.Description == "Delivery").IsCancelled);
            Assert.Contains("Cancelled", cut.Markup);
        });
        Assert.Equal(1, gets); // the form took the action's answer; no second load

        // Edit, then Cancel: the restored copy is the current version, not the one loaded before the action.
        await cut.InvokeAsync(() => form.Instance.EditItem());
        await cut.InvokeAsync(() => form.Instance.CancelChanges());
        cut.WaitForAssertion(() => Assert.Equal(StampAfterAction, form.Instance.Value.LastSaveDate));

        // The user's next edit: its PUT carries the stamp the action produced, not the one loaded before it.
        await cut.InvokeAsync(() => form.Instance.EditItem());
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.NotNull(putBody));
        var sent = JsonSerializer.Deserialize<SalesOrderDTO>(putBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(StampAfterAction, sent.LastSaveDate);
    }

    private static SalesOrderDTO Order(bool cancelled) => new()
    {
        ID = "7",
        CustomerName = "Synthetic customer",
        Total = cancelled ? 20m : 25m,
        LastSaveDate = cancelled ? StampAfterAction : LoadedStamp,
        Lines =
        [
            new() { ID = "21", Description = "Chairs", Quantity = 2, UnitPrice = 10m },
            new() { ID = "22", Description = "Delivery", Quantity = 1, UnitPrice = 5m, IsCancelled = cancelled },
        ],
    };
}
