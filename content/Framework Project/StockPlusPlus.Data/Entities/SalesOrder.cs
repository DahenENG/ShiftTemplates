using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;

namespace StockPlusPlus.Data.Entities;

/// <summary>
/// Incident C sample: an order (the parent form) with lines (the child collection). <see cref="Total"/> is stored on
/// the order and recalculated whenever its lines change, so an action that changes a line also changes the order.
/// </summary>
[ShiftEntityKeyAndName(nameof(ID), nameof(CustomerName))]
public class SalesOrder : ShiftEntity<SalesOrder>
{
    public string CustomerName { get; set; } = default!;

    public string? Notes { get; set; }

    [Precision(18, 2)]
    public decimal Total { get; set; }

    public virtual ICollection<SalesOrderLine> Lines { get; set; } = new HashSet<SalesOrderLine>();

    /// <summary>The sum of the lines that are not cancelled.</summary>
    public void RecalculateTotal() => Total = Lines.Where(x => !x.IsCancelled).Sum(x => x.Quantity * x.UnitPrice);
}

public class SalesOrderLine : ShiftEntity<SalesOrderLine>
{
    public long SalesOrderID { get; set; }

    public virtual SalesOrder SalesOrder { get; set; } = default!;

    public string Description { get; set; } = default!;

    public int Quantity { get; set; }

    [Precision(18, 2)]
    public decimal UnitPrice { get; set; }

    public bool IsCancelled { get; set; }
}
