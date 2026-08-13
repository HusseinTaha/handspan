namespace AndroidExplorer.Core.Models;

/// <summary>
/// One queued or running transfer (spec §11).
/// </summary>
/// <remarks>
/// Jobs are journaled to storage so that resume survives an application crash, not merely a cable
/// pull (spec §13). <see cref="BytesTransferred"/> is authoritative for resume: it is the verified
/// length of the partial destination, not an optimistic counter.
/// </remarks>
public sealed record TransferJob
{
    public required Guid Id { get; init; }

    public required DeviceId DeviceId { get; init; }

    public required TransferDirection Direction { get; init; }

    /// <summary>Device-side path — the source when downloading, the destination when uploading.</summary>
    public required DevicePath RemotePath { get; init; }

    /// <summary>PC-side path — the destination when downloading, the source when uploading.</summary>
    public required string LocalPath { get; init; }

    public required long TotalBytes { get; init; }

    public long BytesTransferred { get; init; }

    public TransferStatus Status { get; init; } = TransferStatus.Queued;

    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.Ask;

    public VerificationMode Verification { get; init; } = VerificationMode.Size;

    public int RetryCount { get; init; }

    /// <summary>User-facing failure message (spec §48). Never a raw protocol string or exit code.</summary>
    public string? Error { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Set when this job is one file within a bulk operation, for grouped progress.</summary>
    public Guid? BatchId { get; init; }

    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesTransferred / TotalBytes, 0, 1) : 0;

    public bool IsTerminal => Status is TransferStatus.Completed
                                     or TransferStatus.Failed
                                     or TransferStatus.Cancelled;

    public bool IsResumable => Status is TransferStatus.Paused or TransferStatus.Failed
                               && BytesTransferred > 0;

    public string FileName => Direction == TransferDirection.Download
        ? RemotePath.Name
        : System.IO.Path.GetFileName(LocalPath);
}

/// <summary>
/// A progress sample for a running transfer (spec §11).
/// </summary>
/// <remarks>
/// Emitted through <see cref="IProgress{T}"/> and throttled before it reaches the UI: a 64 KB chunk
/// callback at full USB 3 speed fires over a thousand times a second, which will destroy frame rate
/// if forwarded naively.
/// </remarks>
public readonly record struct TransferProgress
{
    public required long BytesTransferred { get; init; }

    public required long TotalBytes { get; init; }

    /// <summary>Smoothed throughput in bytes per second.</summary>
    public double BytesPerSecond { get; init; }

    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesTransferred / TotalBytes, 0, 1) : 0;

    public TimeSpan? EstimatedRemaining
    {
        get
        {
            if (BytesPerSecond <= 0 || TotalBytes <= 0)
            {
                return null;
            }

            var remaining = TotalBytes - BytesTransferred;
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(remaining / BytesPerSecond);
        }
    }
}

/// <summary>
/// Totals for a bulk operation, shown in the transfer preview before anything moves (spec §34).
/// </summary>
public sealed record TransferPlan
{
    public required DeviceId DeviceId { get; init; }

    public required TransferDirection Direction { get; init; }

    public required int FileCount { get; init; }

    public required long TotalBytes { get; init; }

    public required string SourceDescription { get; init; }

    public required string DestinationDescription { get; init; }

    /// <summary>Destination paths that already exist and need a conflict decision (spec §35).</summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    /// <summary>True while enumeration is still running, so the UI can show counts climbing.</summary>
    public bool IsEstimateComplete { get; init; } = true;
}
