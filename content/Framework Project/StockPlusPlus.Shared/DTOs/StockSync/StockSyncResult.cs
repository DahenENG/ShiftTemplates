namespace StockPlusPlus.Shared.DTOs.StockSync;

/// <summary>
/// What a sync run tells its caller: how the run ended, what it did, and where the whole feed stands now.
/// A cancelled run is a normal answer, not an error: <see cref="Remaining"/> rows are picked up by the next run.
/// </summary>
public class StockSyncResult
{
    public string Feed { get; set; } = default!;

    public StockSyncRunStatus Status { get; set; }

    /// <summary>Rows this run synced.</summary>
    public int ProcessedThisRun { get; set; }

    /// <summary>Rows this run marked as impossible to process.</summary>
    public int FailedThisRun { get; set; }

    /// <summary>Rows a temporary database error (a deadlock, a lock timeout) left Pending. The next run retries them.</summary>
    public int LeftPendingThisRun { get; set; }

    /// <summary>Batches this run read.</summary>
    public int Batches { get; set; }

    /// <summary>Rows of the feed synced so far, by every run.</summary>
    public int Done { get; set; }

    /// <summary>Rows of the feed that can never be processed (see <see cref="Failures"/> for the first few).</summary>
    public int Failed { get; set; }

    /// <summary>Rows of the feed still waiting, including rows a temporary error left for the next run.</summary>
    public int Remaining { get; set; }

    /// <summary>The last row this run committed, or null when it committed none.</summary>
    public long? LastCommittedRowId { get; set; }

    /// <summary>Up to 20 of this run's failed rows, with a short reason (never the raw database message).</summary>
    public List<StockSyncFailure> Failures { get; set; } = new();
}

public class StockSyncFailure
{
    public long RowId { get; set; }

    public string ExternalCode { get; set; } = default!;

    public string Reason { get; set; } = default!;
}

public enum StockSyncRunStatus
{
    /// <summary>The run went through every Pending row. <see cref="StockSyncResult.Remaining"/> counts rows a temporary
    /// error left for the next run.</summary>
    Completed,

    /// <summary>Stopped by its cancellation token. Finished batches are saved; call again to continue.</summary>
    Cancelled,

    /// <summary>Stopped after the caller's maximum number of batches.</summary>
    BatchLimitReached,
}
