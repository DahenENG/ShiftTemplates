using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace StockPlusPlus.Data.Entities.StockSync;

/// <summary>
/// Incident A sample: one incoming row waiting to be synced into <see cref="SyncedStock"/>. The row's own
/// <see cref="Status"/> is the sync's progress: the fixed sync marks it Done or Failed in the SAME transaction that
/// writes its result, so a run that is cancelled loses no finished work and the next run starts at the first
/// Pending row. A plain EF entity: sync staging data, not a ShiftEntity.
/// </summary>
[Index(nameof(Feed), nameof(Status), nameof(ID))]
public class StockSyncInboxRow
{
    public long ID { get; set; }

    /// <summary>Which incoming feed the row belongs to. A sync run processes one feed.</summary>
    [MaxLength(100)]
    public string Feed { get; set; } = default!;

    [MaxLength(100)]
    public string ExternalCode { get; set; } = default!;

    public string? Name { get; set; }

    public int Quantity { get; set; }

    public StockSyncRowStatus Status { get; set; } = StockSyncRowStatus.Pending;

    /// <summary>Why the row could not be processed, when <see cref="Status"/> is Failed.</summary>
    public string? Error { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}

public enum StockSyncRowStatus
{
    Pending = 0,
    Done = 1,

    /// <summary>The row can never be processed (bad data). It is skipped, not retried, so the sync can finish.</summary>
    Failed = 2,
}
