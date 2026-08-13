using AndroidExplorer.Core.Models;

namespace AndroidExplorer.Core.Interfaces;

/// <summary>
/// Queues, schedules and recovers transfers (spec §11–§13).
/// </summary>
/// <remarks>
/// The UI calls this and never touches the transport (spec §10). Jobs are journaled, so an
/// application crash is just another resumable interruption (spec §13).
/// </remarks>
public interface ITransferManager
{
    DeviceId DeviceId { get; }

    /// <summary>All jobs, including completed history for the transfers page (spec §85).</summary>
    IReadOnlyList<TransferJob> Jobs { get; }

    /// <summary>Raised when a job is added, changes status, or reports progress.</summary>
    event EventHandler<TransferJobChangedEventArgs>? JobChanged;

    /// <summary>
    /// Enumerates what a bulk operation would do, for the confirmation preview (spec §34).
    /// Cancellable, and reports partial counts while it walks.
    /// </summary>
    Task<TransferPlan> PlanDownloadAsync(
        IReadOnlyList<DevicePath> sources,
        string localDestinationDirectory,
        IProgress<TransferPlan>? progress,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="PlanDownloadAsync"/>
    Task<TransferPlan> PlanUploadAsync(
        IReadOnlyList<string> localSources,
        DevicePath destinationDirectory,
        IProgress<TransferPlan>? progress,
        CancellationToken cancellationToken);

    /// <summary>Queues a download of files or directories.</summary>
    Task<IReadOnlyList<Guid>> EnqueueDownloadAsync(
        IReadOnlyList<DevicePath> sources,
        string localDestinationDirectory,
        ConflictPolicy conflictPolicy,
        CancellationToken cancellationToken);

    /// <summary>Queues an upload of files or directories.</summary>
    Task<IReadOnlyList<Guid>> EnqueueUploadAsync(
        IReadOnlyList<string> localSources,
        DevicePath destinationDirectory,
        ConflictPolicy conflictPolicy,
        CancellationToken cancellationToken);

    Task PauseAsync(Guid jobId);

    Task ResumeAsync(Guid jobId);

    Task CancelAsync(Guid jobId);

    Task RetryAsync(Guid jobId);

    /// <summary>Pauses everything, e.g. on disconnect or before the PC sleeps (spec §38, §81).</summary>
    Task PauseAllAsync(string reason);

    /// <summary>Resumes everything that was paused by an interruption.</summary>
    Task ResumeAllAsync();

    /// <summary>Removes terminal jobs from the history.</summary>
    Task ClearCompletedAsync();
}

public sealed class TransferJobChangedEventArgs(TransferJob job, TransferProgress? progress = null) : EventArgs
{
    public TransferJob Job { get; } = job;

    /// <summary>Present only for progress updates.</summary>
    public TransferProgress? Progress { get; } = progress;
}
