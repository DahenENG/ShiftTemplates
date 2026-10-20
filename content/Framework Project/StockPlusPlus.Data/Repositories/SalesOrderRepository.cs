using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using StockPlusPlus.Data.DbContext;
using StockPlusPlus.Data.Entities;
using StockPlusPlus.Shared.DTOs.SalesOrder;

namespace StockPlusPlus.Data.Repositories;

/// <summary>
/// Incident C sample. Maps automatically (lines nest like Invoice's); the write direction replaces the lines, so the
/// old ones are removed first, as InvoiceRepository does. <see cref="SalesOrder.Total"/> is the server's: recalculated
/// on every save from the lines, never taken from the request.
/// </summary>
public class SalesOrderRepository : ShiftRepository<DB, SalesOrder, SalesOrderListDTO, SalesOrderDTO>
{
    private static readonly Action<ShiftRepositoryOptions<SalesOrder, SalesOrderListDTO, SalesOrderDTO>> IncludeOptions =
        option => option.IncludeRelatedEntitiesWithFindAsync(x => x.Include(entity => entity.Lines));

    public SalesOrderRepository(DB db) : base(db, IncludeOptions)
    {
    }

    public override async ValueTask<SalesOrder> UpsertAsync(SalesOrder entity, SalesOrderDTO dto, ActionTypes actionType, long? userId, Guid? idempotencyKey, bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
    {
        if (actionType == ActionTypes.Update)
            db.SalesOrderLines.RemoveRange(entity.Lines.ToList());

        var upserted = await base.UpsertAsync(entity, dto, actionType, userId, idempotencyKey, disableDefaultDataLevelAccess, disableGlobalFilters);
        upserted.RecalculateTotal();
        return upserted;
    }
}
