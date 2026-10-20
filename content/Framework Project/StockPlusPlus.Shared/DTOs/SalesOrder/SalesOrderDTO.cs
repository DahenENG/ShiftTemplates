using System.ComponentModel.DataAnnotations;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace StockPlusPlus.Shared.DTOs.SalesOrder;

public class SalesOrderDTO : ShiftEntityViewAndUpsertDTO
{
    public override string? ID { get; set; }

    [Required]
    public string CustomerName { get; set; } = default!;

    public string? Notes { get; set; }

    /// <summary>Calculated by the server from the lines that are not cancelled.</summary>
    public decimal Total { get; set; }

    public List<SalesOrderLineDTO> Lines { get; set; } = new();
}

public class SalesOrderLineDTO : ShiftEntityViewAndUpsertDTO
{
    public override string? ID { get; set; }

    [Required]
    public string Description { get; set; } = default!;

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public bool IsCancelled { get; set; }
}

[ShiftEntityKeyAndName(nameof(ID), nameof(CustomerName))]
public class SalesOrderListDTO : ShiftEntityListDTO
{
    public override string? ID { get; set; }

    public string CustomerName { get; set; } = default!;

    public decimal Total { get; set; }
}
