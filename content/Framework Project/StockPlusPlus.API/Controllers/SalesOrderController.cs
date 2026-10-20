using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Web;
using ShiftSoftware.TypeAuth.Core;
using StockPlusPlus.Data.Repositories;
using StockPlusPlus.Shared.ActionTrees;
using StockPlusPlus.Shared.DTOs.SalesOrder;

namespace StockPlusPlus.API.Controllers;

/// <summary>Incident C sample: an order with lines, and an action that cancels a line.</summary>
[Route("api/[controller]")]
public class SalesOrderController : ShiftEntitySecureControllerAsync<SalesOrderRepository, Data.Entities.SalesOrder, SalesOrderListDTO, SalesOrderDTO>
{
    public SalesOrderController() : base(StockPlusPlusActionTree.SalesOrder)
    {
    }

    /// <summary>
    /// POST api/SalesOrder/{key}/lines/{lineKey}/cancel — cancels one line and recalculates the order's Total. It saves
    /// through the repository, which stamps every changed row: the line AND the order get a new LastSaveDate.
    /// <para>
    /// Answers with the ORDER as it now is (lines, Total, new LastSaveDate), in the same shape as GET, so the form that
    /// ran the action replaces its copy and its next save carries the current stamp. Answering 204 left the form with
    /// the stamp it loaded before the action, and its next save always got a 409 (Incident C).
    /// </para>
    /// </summary>
    [Authorize]
    [HttpPost("{key}/lines/{lineKey}/cancel")]
    public async Task<ActionResult<ShiftEntityResponse<SalesOrderDTO>>> CancelLine(
        string key,
        string lineKey,
        [FromServices] SalesOrderRepository repository,
        [FromServices] IHashIdService hashIds,
        [FromServices] ITypeAuthService typeAuth)
    {
        if (!typeAuth.CanWrite(StockPlusPlusActionTree.SalesOrder))
            return Forbid();

        var order = await repository.FindAsync(hashIds.Decode<SalesOrderDTO>(key));
        if (order is null)
            return NotFound();

        var lineId = hashIds.Decode<SalesOrderLineDTO>(lineKey);
        var line = order.Lines.SingleOrDefault(x => x.ID == lineId);
        if (line is null)
            return NotFound();

        line.IsCancelled = true;     // the child
        order.RecalculateTotal();    // ...and the parent

        await repository.SaveChangesAsync();

        return Ok(new ShiftEntityResponse<SalesOrderDTO>(await repository.ViewAsync(order)));
    }
}
