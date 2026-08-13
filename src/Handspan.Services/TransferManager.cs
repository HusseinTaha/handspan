using System.Collections.Concurrent;
using System.Diagnostics;
using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Data;
using Microsoft.Extensions.Logging;

namespace Handspan.Services;

/// <summary>Creates a transfer manager per device session.</summary>
public interface ITransferManagerFactory
{
    ITransferManager Create(DeviceId device, IDeviceFileSystem fileSystem);
}

internal sealed class TransferManagerFactory(
    ITransferJobStore store,
    ISettingsService settings,
    ILoggerFactory loggers) : ITransferManagerFactory
{
    public ITransferManager Create(DeviceId device, IDeviceFileSystem fileSystem)
        => new TransferManager(device, fileSystem, store, settings, loggers.CreateLogger<TransferManager>());
}

/// <summary>
/// The transfer queue, scheduler and recovery engine (spec §11–§13).
/// </summary>
/// <remarks>
/// <para>
/// Three properties are load-bearing. Every transfer writes to a <c>.part</c> file and is moved into
/// place only on success, so a partial file never looks complete. Resume offsets are aligned to 1 MiB,
/// which is what lets the resumable paths rely only on baseline <c>dd</c> semantics. And every state
/// change is journalled, so killing the application is just another interruption.
/// </para>
/// <para>
/// Progress is throttled before it leaves this class: a 64 KiB chunk callback at USB 3 speed fires
/// over a thousand times a second, which would destroy the UI's frame rate.
/// </para>
/// </remarks>
internal sealed class TransferManager : ITransferManager, IAsyncDisposable
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(125);
    private const string PartialSuffix = ".aepart";

    private readonly DeviceId _device;
    private readonly IDeviceFileSystem _fileSystem;
    private readonly ITransferJobStore _store;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<TransferManager> _logger;

    private readonly ConcurrentDictionary<Guid, JobState> _jobs = new();
    private readonly SemaphoreSlim _pumpGate = new(1, 1);
    private bool _disposed;

    public TransferManager(
        DeviceId device,
        IDeviceFileSystem fileSystem,
        ITransferJobStore store,
        ISettingsService settings,
        ILogger<TransferManager> logger)
    {
        _device = device;
        _fileSystem = fileSystem;
        _store = store;
        _settingsService = settings;
        _logger = logger;
    }

    /// <summary>
    /// Read per operation rather than captured, so changing concurrency or retry limits in settings takes
    /// effect on the next job instead of needing a restart.
    /// </summary>
    private AppSettings _settings => _settingsService.Current;

    public DeviceId DeviceId => _device;

    public IReadOnlyList<TransferJob> Jobs =>
        _jobs.Values.Select(state => state.Job).OrderBy(job => job.CreatedAt).ToList();

    public event EventHandler<TransferJobChangedEventArgs>? JobChanged;

    /// <summary>
    /// Restores journalled jobs after a restart. Unfinished work comes back as
    /// <see cref="TransferStatus.Paused"/> rather than restarting on its own (spec §13).
    /// </summary>
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        foreach (var job in await _store.LoadAsync(_device, cancellationToken).ConfigureAwait(false))
        {
            var restored = job.IsTerminal
                ? job
                : job with { Status = TransferStatus.Paused, Error = "Interrupted when the app closed." };

            _jobs[restored.Id] = new JobState(restored);

            if (!ReferenceEquals(restored, job))
            {
                await _store.SaveAsync(restored, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Restored {Count} journalled transfers.", _jobs.Count);
    }

    // ---------------- planning (spec §34) ----------------

    public async Task<TransferPlan> PlanDownloadAsync(
        IReadOnlyList<DevicePath> sources,
        string localDestinationDirectory,
        IProgress<TransferPlan>? progress,
        CancellationToken cancellationToken)
    {
        var files = new List<(DevicePath Source, string Local)>();
        var conflicts = new List<string>();
        long total = 0;

        foreach (var source in sources)
        {
            await foreach (var file in EnumerateRemoteAsync(source, localDestinationDirectory,
                               cancellationToken).ConfigureAwait(false))
            {
                files.Add((file.Source, file.Local));
                total += file.Size;

                if (File.Exists(file.Local))
                {
                    conflicts.Add(file.Local);
                }

                if (files.Count % 200 == 0)
                {
                    progress?.Report(BuildPlan(false));
                }
            }
        }

        return BuildPlan(true);

        TransferPlan BuildPlan(bool complete) => new()
        {
            DeviceId = _device,
            Direction = TransferDirection.Download,
            FileCount = files.Count,
            TotalBytes = total,
            SourceDescription = string.Join(", ", sources.Select(path => path.Name)),
            DestinationDescription = localDestinationDirectory,
            Conflicts = conflicts,
            IsEstimateComplete = complete,
        };
    }

    public async Task<TransferPlan> PlanUploadAsync(
        IReadOnlyList<string> localSources,
        DevicePath destinationDirectory,
        IProgress<TransferPlan>? progress,
        CancellationToken cancellationToken)
    {
        var files = EnumerateLocal(localSources, destinationDirectory).ToList();
        var conflicts = new List<string>();
        long total = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += new FileInfo(file.Local).Length;

            if (await _fileSystem.ExistsAsync(file.Remote, cancellationToken).ConfigureAwait(false))
            {
                conflicts.Add(file.Remote.Value);
            }
        }

        return new TransferPlan
        {
            DeviceId = _device,
            Direction = TransferDirection.Upload,
            FileCount = files.Count,
            TotalBytes = total,
            SourceDescription = string.Join(", ", localSources.Select(Path.GetFileName)),
            DestinationDescription = destinationDirectory.Value,
            Conflicts = conflicts,
        };
    }

    // ---------------- enqueueing ----------------

    public async Task<IReadOnlyList<Guid>> EnqueueDownloadAsync(
        IReadOnlyList<DevicePath> sources,
        string localDestinationDirectory,
        ConflictPolicy conflictPolicy,
        CancellationToken cancellationToken)
    {
        var batch = sources.Count > 1 ? Guid.NewGuid() : (Guid?)null;
        var ids = new List<Guid>();

        foreach (var source in sources)
        {
            await foreach (var file in EnumerateRemoteAsync(source, localDestinationDirectory,
                               cancellationToken).ConfigureAwait(false))
            {
                var local = file.Local;

                if (File.Exists(local))
                {
                    switch (conflictPolicy)
                    {
                        case ConflictPolicy.Skip:
                            continue;
                        case ConflictPolicy.Rename:
                            local = NextFreeLocalName(local);
                            break;
                        case ConflictPolicy.Replace:
                        case ConflictPolicy.Ask:
                        default:
                            break;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(local)!);

                ids.Add(await AddAsync(new TransferJob
                {
                    Id = Guid.NewGuid(),
                    DeviceId = _device,
                    Direction = TransferDirection.Download,
                    RemotePath = file.Source,
                    LocalPath = local,
                    TotalBytes = file.Size,
                    ConflictPolicy = conflictPolicy,
                    Verification = _settings.Verification,
                    CreatedAt = DateTimeOffset.UtcNow,
                    BatchId = batch,
                }, cancellationToken).ConfigureAwait(false));
            }
        }

        Pump();
        return ids;
    }

    public async Task<IReadOnlyList<Guid>> EnqueueUploadAsync(
        IReadOnlyList<string> localSources,
        DevicePath destinationDirectory,
        ConflictPolicy conflictPolicy,
        CancellationToken cancellationToken)
    {
        var batch = localSources.Count > 1 ? Guid.NewGuid() : (Guid?)null;
        var ids = new List<Guid>();

        foreach (var file in EnumerateLocal(localSources, destinationDirectory))
        {
            var remote = file.Remote;

            if (await _fileSystem.ExistsAsync(remote, cancellationToken).ConfigureAwait(false))
            {
                switch (conflictPolicy)
                {
                    case ConflictPolicy.Skip:
                        continue;
                    case ConflictPolicy.Rename:
                        remote = await NextFreeRemoteNameAsync(remote, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case ConflictPolicy.Replace:
                    case ConflictPolicy.Ask:
                    default:
                        break;
                }
            }

            await _fileSystem.CreateDirectoryAsync(remote.Parent, cancellationToken).ConfigureAwait(false);

            ids.Add(await AddAsync(new TransferJob
            {
                Id = Guid.NewGuid(),
                DeviceId = _device,
                Direction = TransferDirection.Upload,
                RemotePath = remote,
                LocalPath = file.Local,
                TotalBytes = new FileInfo(file.Local).Length,
                ConflictPolicy = conflictPolicy,
                Verification = _settings.Verification,
                CreatedAt = DateTimeOffset.UtcNow,
                BatchId = batch,
            }, cancellationToken).ConfigureAwait(false));
        }

        Pump();
        return ids;
    }

    // ---------------- control ----------------

    public async Task PauseAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            return;
        }

        state.PauseRequested = true;

        if (state.Cancellation is { } cancellation)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        else if (state.Job.Status == TransferStatus.Queued)
        {
            await UpdateAsync(state, job => job with { Status = TransferStatus.Paused })
                .ConfigureAwait(false);
        }
    }

    public async Task ResumeAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var state)
            || state.Job.Status is not (TransferStatus.Paused or TransferStatus.Failed))
        {
            return;
        }

        state.PauseRequested = false;
        await UpdateAsync(state, job => job with { Status = TransferStatus.Queued, Error = null })
            .ConfigureAwait(false);

        Pump();
    }

    public async Task CancelAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            return;
        }

        state.PauseRequested = false;
        state.CancelRequested = true;

        if (state.Cancellation is { } cancellation)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            return;
        }

        await UpdateAsync(state, job => job with { Status = TransferStatus.Cancelled })
            .ConfigureAwait(false);
        DeletePartial(state.Job);
    }

    public async Task RetryAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            return;
        }

        await UpdateAsync(state, job => job with
        {
            Status = TransferStatus.Queued,
            RetryCount = 0,
            Error = null,
        }).ConfigureAwait(false);

        Pump();
    }

    /// <summary>Pauses everything, e.g. on disconnect or before the machine sleeps (spec §38, §81).</summary>
    public async Task PauseAllAsync(string reason)
    {
        foreach (var state in _jobs.Values)
        {
            if (state.Job.IsTerminal)
            {
                continue;
            }

            state.PauseRequested = true;
            state.PauseReason = reason;

            if (state.Cancellation is { } cancellation)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            else
            {
                await UpdateAsync(state, job => job with
                {
                    Status = TransferStatus.Paused,
                    Error = reason,
                }).ConfigureAwait(false);
            }
        }
    }

    public async Task ResumeAllAsync()
    {
        foreach (var state in _jobs.Values.Where(s => s.Job.Status == TransferStatus.Paused))
        {
            state.PauseRequested = false;
            state.PauseReason = null;
            await UpdateAsync(state, job => job with { Status = TransferStatus.Queued, Error = null })
                .ConfigureAwait(false);
        }

        Pump();
    }

    public async Task ClearCompletedAsync()
    {
        foreach (var state in _jobs.Values.Where(s => s.Job.IsTerminal).ToList())
        {
            _jobs.TryRemove(state.Job.Id, out _);
        }

        await _store.DeleteTerminalAsync(_device, CancellationToken.None).ConfigureAwait(false);
    }

    // ---------------- scheduling (spec §12) ----------------

    /// <summary>
    /// Starts as many queued jobs as the concurrency limits allow, classified by size: many small
    /// files in parallel, few large ones. More parallelism is not automatically faster over USB.
    /// </summary>
    private void Pump()
    {
        if (_disposed)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await _pumpGate.WaitAsync().ConfigureAwait(false);
            try
            {
                while (true)
                {
                    var running = _jobs.Values.Where(state => state.Cancellation is not null).ToList();
                    var smallRunning = running.Count(state => IsSmall(state.Job));
                    var largeRunning = running.Count - smallRunning;

                    // `Cancellation is null` is what proves a job is not already running. Filtering on
                    // status alone is not enough: a started job stays Queued until its worker task gets
                    // scheduled, so the pump could pick it a second time and have two writers race on the
                    // same destination file.
                    var next = _jobs.Values
                        .Where(state => state.Cancellation is null
                                        && state.Job.Status == TransferStatus.Queued)
                        .OrderBy(state => state.Job.CreatedAt)
                        .FirstOrDefault(state => IsSmall(state.Job)
                            ? smallRunning < _settings.MaxConcurrentSmallTransfers
                            : largeRunning < _settings.MaxConcurrentLargeTransfers);

                    if (next is null)
                    {
                        return;
                    }

                    next.Cancellation = new CancellationTokenSource();
                    next.Run = Task.Run(() => RunAsync(next), CancellationToken.None);
                }
            }
            finally
            {
                _pumpGate.Release();
            }
        });
    }

    private bool IsSmall(TransferJob job) => job.TotalBytes < _settings.LargeFileThresholdBytes;

    private async Task RunAsync(JobState state)
    {
        var cancellationToken = state.Cancellation!.Token;

        try
        {
            await UpdateAsync(state, job => job with
            {
                Status = TransferStatus.Transferring,
                StartedAt = job.StartedAt ?? DateTimeOffset.UtcNow,
                Error = null,
            }).ConfigureAwait(false);

            if (state.Job.Direction == TransferDirection.Download)
            {
                await RunDownloadAsync(state, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunUploadAsync(state, cancellationToken).ConfigureAwait(false);
            }

            await UpdateAsync(state, job => job with
            {
                Status = TransferStatus.Completed,
                BytesTransferred = job.TotalBytes,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = null,
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (state.CancelRequested)
            {
                await UpdateAsync(state, job => job with { Status = TransferStatus.Cancelled })
                    .ConfigureAwait(false);
                DeletePartial(state.Job);
            }
            else
            {
                // Paused: the partial file is kept precisely so the transfer can resume.
                var transferred = await MeasurePartialAsync(state.Job).ConfigureAwait(false);

                await UpdateAsync(state, job => job with
                {
                    Status = TransferStatus.Paused,
                    BytesTransferred = transferred,
                    Error = state.PauseReason,
                }).ConfigureAwait(false);
            }
        }
        catch (DeviceException ex)
        {
            await HandleFailureAsync(state, ex).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            await UpdateAsync(state, job => job with
            {
                Status = TransferStatus.Failed,
                Error = "Could not write to this computer: " + ex.Message,
            }).ConfigureAwait(false);
        }
        finally
        {
            state.Cancellation?.Dispose();
            state.Cancellation = null;
            Pump();
        }
    }

    private async Task HandleFailureAsync(JobState state, DeviceException exception)
    {
        // Measure what actually landed. A transfer that dies inside the progress-throttle window would
        // otherwise journal zero bytes, which loses the resume point and makes IsResumable lie.
        var transferred = await MeasurePartialAsync(state.Job).ConfigureAwait(false);

        var shouldRetry = exception.IsTransient && state.Job.RetryCount < _settings.RetryCount;

        if (!shouldRetry)
        {
            await UpdateAsync(state, job => job with
            {
                Status = TransferStatus.Failed,
                BytesTransferred = transferred,
                Error = exception.UserMessage,
            }).ConfigureAwait(false);
            return;
        }

        var attempt = state.Job.RetryCount + 1;

        await UpdateAsync(state, job => job with
        {
            Status = TransferStatus.Retrying,
            RetryCount = attempt,
            BytesTransferred = transferred,
            Error = exception.UserMessage,
        }).ConfigureAwait(false);

        // Exponential backoff, so a flapping cable does not spin.
        var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));
        await Task.Delay(delay).ConfigureAwait(false);

        await UpdateAsync(state, job => job with { Status = TransferStatus.Queued }).ConfigureAwait(false);
    }

    private async Task RunDownloadAsync(JobState state, CancellationToken cancellationToken)
    {
        var job = state.Job;
        var partial = job.LocalPath + PartialSuffix;

        // Align the existing partial down to a 1 MiB boundary and resume from there. Discarding under
        // a megabyte is the price of needing only baseline dd semantics on the device (spec §13).
        var resumeFrom = 0L;
        if (File.Exists(partial))
        {
            var length = new FileInfo(partial).Length;
            resumeFrom = length - length % AlignmentBytes;

            if (resumeFrom != length)
            {
                await using var truncate = new FileStream(partial, FileMode.Open, FileAccess.Write);
                truncate.SetLength(resumeFrom);
            }
        }

        var progress = CreateProgress(state);

        await using (var destination = new FileStream(
                         partial,
                         resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                         FileAccess.Write,
                         FileShare.None))
        {
            if (resumeFrom > 0)
            {
                _logger.LogInformation("Resuming a download from an aligned offset.");
                await _fileSystem.DownloadRangeAsync(
                    job.RemotePath, resumeFrom, destination, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _fileSystem.DownloadAsync(job.RemotePath, destination, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await VerifyAndCommitDownloadAsync(state, partial, cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyAndCommitDownloadAsync(
        JobState state,
        string partial,
        CancellationToken cancellationToken)
    {
        var job = state.Job;
        var actual = new FileInfo(partial).Length;

        // Size is always verified (spec §37).
        if (job.TotalBytes > 0 && actual != job.TotalBytes)
        {
            throw new AdbSizeMismatchException(job.TotalBytes, actual);
        }

        if (job.Verification == VerificationMode.Sha256)
        {
            var expected = await _fileSystem.ComputeSha256Async(job.RemotePath, cancellationToken)
                .ConfigureAwait(false);
            var actualHash = await ComputeLocalSha256Async(partial, cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(expected, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new AdbHashMismatchException();
            }
        }

        // Only now does the file take its real name, so a partial download never looks complete.
        File.Move(partial, job.LocalPath, overwrite: true);
    }

    private async Task RunUploadAsync(JobState state, CancellationToken cancellationToken)
    {
        var job = state.Job;
        var partial = DevicePath.Parse(job.RemotePath.Value + PartialSuffix);

        var resumeFrom = 0L;
        try
        {
            var existing = await _fileSystem.GetInfoAsync(partial, cancellationToken).ConfigureAwait(false);
            resumeFrom = existing.Size - existing.Size % AlignmentBytes;
        }
        catch (PathNotFoundException)
        {
            // Nothing to resume; a fresh upload.
        }

        var progress = CreateProgress(state);

        await using (var source = new FileStream(job.LocalPath, FileMode.Open, FileAccess.Read))
        {
            if (resumeFrom > 0)
            {
                _logger.LogInformation("Resuming an upload from an aligned offset.");
                source.Seek(resumeFrom, SeekOrigin.Begin);
                await _fileSystem.UploadRangeAsync(
                    source, partial, resumeFrom, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _fileSystem.UploadAsync(source, partial, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var uploaded = await _fileSystem.GetInfoAsync(partial, cancellationToken).ConfigureAwait(false);
        if (job.TotalBytes > 0 && uploaded.Size != job.TotalBytes)
        {
            throw new AdbSizeMismatchException(job.TotalBytes, uploaded.Size);
        }

        if (job.Verification == VerificationMode.Sha256)
        {
            var expected = await ComputeLocalSha256Async(job.LocalPath, cancellationToken)
                .ConfigureAwait(false);
            var actual = await _fileSystem.ComputeSha256Async(partial, cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                await _fileSystem.DeleteAsync(partial, false, cancellationToken).ConfigureAwait(false);
                throw new AdbHashMismatchException();
            }
        }

        await _fileSystem.RenameAsync(partial, job.RemotePath, cancellationToken).ConfigureAwait(false);
    }

    private static long AlignmentBytes => 1024 * 1024;

    /// <summary>
    /// Measures how much of a transfer actually survives on the destination, which is the only
    /// trustworthy resume point — a progress counter can be stale or never have fired.
    /// </summary>
    private async Task<long> MeasurePartialAsync(TransferJob job)
    {
        try
        {
            if (job.Direction == TransferDirection.Download)
            {
                var partial = job.LocalPath + PartialSuffix;
                return File.Exists(partial) ? new FileInfo(partial).Length : 0;
            }

            var remotePartial = DevicePath.Parse(job.RemotePath.Value + PartialSuffix);
            var info = await _fileSystem.GetInfoAsync(remotePartial, CancellationToken.None)
                .ConfigureAwait(false);
            return info.Size;
        }
        catch (Exception ex) when (ex is DeviceException or IOException)
        {
            // Nothing measurable means nothing to resume from.
            return 0;
        }
    }

    private IProgress<TransferProgress> CreateProgress(JobState state)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var lastBytes = 0L;
        var speed = 0d;

        return new Progress<TransferProgress>(sample =>
        {
            var elapsed = stopwatch.Elapsed;
            var sinceLast = elapsed - lastReport;

            if (sinceLast < ProgressInterval && sample.BytesTransferred < sample.TotalBytes)
            {
                return;
            }

            if (sinceLast.TotalSeconds > 0)
            {
                var instant = (sample.BytesTransferred - lastBytes) / sinceLast.TotalSeconds;

                // Exponentially weighted so the displayed rate does not jitter (spec §11).
                speed = speed == 0 ? instant : (speed * 0.7) + (instant * 0.3);
            }

            lastReport = elapsed;
            lastBytes = sample.BytesTransferred;

            state.Job = state.Job with { BytesTransferred = sample.BytesTransferred };

            JobChanged?.Invoke(this, new TransferJobChangedEventArgs(state.Job, sample with
            {
                BytesPerSecond = speed,
            }));
        });
    }

    private async Task<Guid> AddAsync(TransferJob job, CancellationToken cancellationToken)
    {
        _jobs[job.Id] = new JobState(job);
        await _store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        JobChanged?.Invoke(this, new TransferJobChangedEventArgs(job));
        return job.Id;
    }

    private async Task UpdateAsync(JobState state, Func<TransferJob, TransferJob> update)
    {
        state.Job = update(state.Job);
        await _store.SaveAsync(state.Job, CancellationToken.None).ConfigureAwait(false);
        JobChanged?.Invoke(this, new TransferJobChangedEventArgs(state.Job));
    }

    private void DeletePartial(TransferJob job)
    {
        if (job.Direction != TransferDirection.Download)
        {
            return;
        }

        var partial = job.LocalPath + PartialSuffix;
        if (File.Exists(partial))
        {
            try
            {
                File.Delete(partial);
            }
            catch (IOException)
            {
                // Leaving a stray partial file behind is not worth failing over.
            }
        }
    }

    // ---------------- enumeration and conflicts ----------------

    private async IAsyncEnumerable<(DevicePath Source, string Local, long Size)> EnumerateRemoteAsync(
        DevicePath source,
        string localRoot,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var info = await _fileSystem.GetInfoAsync(source, cancellationToken).ConfigureAwait(false);

        if (!info.IsDirectory)
        {
            yield return (source, Path.Combine(localRoot, SanitizeForLocalFileSystem(source.Name)), info.Size);
            yield break;
        }

        // Breadth-first so the first files start moving before a deep tree is fully walked.
        var queue = new Queue<(DevicePath Path, string Local)>();
        queue.Enqueue((source, Path.Combine(localRoot, SanitizeForLocalFileSystem(source.Name))));

        while (queue.Count > 0)
        {
            var (current, currentLocal) = queue.Dequeue();

            IReadOnlyList<DeviceEntry> entries;
            try
            {
                entries = await _fileSystem.ListAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch (AccessDeniedException)
            {
                // A protected subfolder should not abort a whole bulk copy.
                continue;
            }

            foreach (var entry in entries)
            {
                var local = Path.Combine(currentLocal, SanitizeForLocalFileSystem(entry.Name));

                if (entry.IsDirectory)
                {
                    queue.Enqueue((entry.Path, local));
                }
                else
                {
                    yield return (entry.Path, local, entry.Size);
                }
            }
        }
    }

    private static IEnumerable<(string Local, DevicePath Remote)> EnumerateLocal(
        IReadOnlyList<string> sources,
        DevicePath destinationDirectory)
    {
        foreach (var source in sources)
        {
            if (File.Exists(source))
            {
                yield return (source, destinationDirectory.Combine(Path.GetFileName(source)));
                continue;
            }

            if (!Directory.Exists(source))
            {
                continue;
            }

            var root = new DirectoryInfo(source);

            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root.FullName, file.FullName);
                var remote = destinationDirectory.Combine(root.Name);

                foreach (var segment in relative.Split(Path.DirectorySeparatorChar,
                             Path.AltDirectorySeparatorChar))
                {
                    remote = remote.Combine(segment);
                }

                yield return (file.FullName, remote);
            }
        }
    }

    /// <summary>
    /// Makes an Android filename usable on the local filesystem.
    /// </summary>
    /// <remarks>
    /// Android permits characters Windows forbids — <c>: * ? " &lt; &gt; |</c> and a trailing dot are
    /// all legal in a POSIX filename. Without this, downloading such a file fails with an obscure IO
    /// error (spec §74).
    /// </remarks>
    private static string SanitizeForLocalFileSystem(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

        sanitized = sanitized.TrimEnd(' ', '.');
        return sanitized.Length == 0 ? "_" : sanitized;
    }

    private static string NextFreeLocalName(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private async Task<DevicePath> NextFreeRemoteNameAsync(
        DevicePath path,
        CancellationToken cancellationToken)
    {
        var extension = path.Extension;
        var stem = extension.Length > 0 ? path.Name[..^extension.Length] : path.Name;

        for (var i = 1; i < 1_000; i++)
        {
            var candidate = path.Parent.Combine($"{stem} ({i}){extension}");
            if (!await _fileSystem.ExistsAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return path.Parent.Combine($"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private static async Task<string> ComputeLocalSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        foreach (var state in _jobs.Values)
        {
            if (state.Cancellation is { } cancellation)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
        }

        foreach (var state in _jobs.Values.Where(s => s.Run is not null))
        {
            try
            {
                await state.Run!.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutdown; failures here are not actionable.
            }
        }

        _pumpGate.Dispose();
    }

    /// <summary>Mutable runtime state for one job.</summary>
    private sealed class JobState(TransferJob job)
    {
        public TransferJob Job { get; set; } = job;

        public CancellationTokenSource? Cancellation { get; set; }

        public Task? Run { get; set; }

        public bool PauseRequested { get; set; }

        public bool CancelRequested { get; set; }

        public string? PauseReason { get; set; }
    }
}

/// <summary>The transferred size did not match the source (spec §37).</summary>
public sealed class AdbSizeMismatchException(long expected, long actual)
    : DeviceException(
        "The transfer finished with the wrong size, so it was not kept. Handspan will retry.",
        $"expected {expected} bytes, got {actual}")
{
    public override bool IsTransient => true;
}

/// <summary>Optional hash verification failed (spec §37).</summary>
public sealed class AdbHashMismatchException()
    : DeviceException(
        "The transferred file did not match the original, so it was not kept. Handspan will retry.",
        "sha-256 mismatch")
{
    public override bool IsTransient => true;
}
