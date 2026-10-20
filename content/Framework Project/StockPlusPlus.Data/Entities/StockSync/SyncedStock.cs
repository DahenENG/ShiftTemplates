using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace StockPlusPlus.Data.Entities.StockSync;

/// <summary>Incident A sample: the sync's target. One row per (Feed, ExternalCode), inserted or updated.</summary>
[Index(nameof(Feed), nameof(ExternalCode), IsUnique = true)]
public class SyncedStock
{
    public long ID { get; set; }

    [MaxLength(100)]
    public string Feed { get; set; } = default!;

    [MaxLength(100)]
    public string ExternalCode { get; set; } = default!;

    /// <summary>Limited on purpose: a longer name is rejected by SQL Server, the sample's "row the database refuses".</summary>
    [MaxLength(100)]
    public string Name { get; set; } = default!;

    public int Quantity { get; set; }

    public DateTimeOffset LastSyncedAt { get; set; }
}
