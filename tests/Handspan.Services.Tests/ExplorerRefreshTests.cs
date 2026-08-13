using System.Collections.Concurrent;
using Handspan.App.ViewModels;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Core.Platform;
using Handspan.Data;
using Handspan.App.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace Handspan.Services.Tests;

/// <summary>
/// The Explorer refreshing itself when a transfer lands in the folder on screen (spec §29, §52).
/// </summary>
/// <remarks>
/// Queuing a transfer returns before any bytes move, so refreshing at that moment shows the folder as it was.
/// ADB has no filesystem watcher, which means completion events are the only signal available — and if they
/// are not wired up, an upload appears to do nothing until the user presses F5.
/// </remarks>
public sealed class ExplorerRefreshTests : IDisposable
{
    /// <summary>Long enough to cover the coalescing delay in the view model, with margin.</summary>
    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(3);

    private readonly TestDispatcher _dispatcher = new();

    public void Dispose() => _dispatcher.Dispose();

    private (ExplorerViewModel Explorer, StubFileSystem Files, StubTransfers Transfers) Create()
    {
        var files = new StubFileSystem();
        var transfers = new StubTransfers();

        var explorer = new ExplorerViewModel(
            new StubCache(),
            new StubShell(),
            new StubSettings(),
            new StubProfiles(),
            _dispatcher,
            NullLogger<ExplorerViewModel>.Instance);

        return (explorer, files, transfers);
    }

    private async Task<(ExplorerViewModel, StubFileSystem, StubTransfers)> AttachAsync()
    {
        var (explorer, files, transfers) = Create();
        await explorer.AttachAsync(new StubSession(files, transfers));
        return (explorer, files, transfers);
    }

    /// <summary>Waits for the listing count to rise, or gives up after <see cref="SettleWindow"/>.</summary>
    /// <remarks>
    /// The count is incremented on the dispatcher thread and read here, hence the volatile read in
    /// <see cref="StubFileSystem.ListCalls"/>. Observing the count is not enough to assert on
    /// <c>Entries</c> though: the listing is counted before the collection is repopulated, so callers
    /// must drain the dispatcher first.
    /// </remarks>
    private static async Task<bool> WaitForListingsAsync(StubFileSystem files, int atLeast)
    {
        var deadline = DateTime.UtcNow + SettleWindow;

        while (DateTime.UtcNow < deadline)
        {
            if (files.ListCalls >= atLeast)
            {
                return true;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Waits out the coalescing window, for the tests that assert nothing happens.</summary>
    private async Task SettleAsync()
    {
        await Task.Delay(SettleWindow / 2).ConfigureAwait(false);
        await _dispatcher.DrainAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task A_completed_upload_into_the_open_folder_refreshes_it()
    {
        var (explorer, files, transfers) = await AttachAsync();
        var before = files.ListCalls;

        // The device now holds a file the view has never seen.
        files.Add(KnownPaths.InternalStorage.Combine("arrived.bin"));

        transfers.RaiseCompletedUpload(KnownPaths.InternalStorage.Combine("arrived.bin"));

        Assert.True(await WaitForListingsAsync(files, before + 1),
            "the Explorer never re-listed the folder after the upload completed");

        // The listing is counted before Entries is repopulated, so the collection is only stable
        // once the dispatcher has finished the work that fills it.
        await _dispatcher.DrainAsync();

        Assert.Contains(explorer.Entries, row => row.Name == "arrived.bin");
    }

    [Fact]
    public async Task An_upload_into_a_subfolder_also_refreshes()
    {
        var (explorer, files, transfers) = await AttachAsync();
        var before = files.ListCalls;

        // Uploading a folder writes files below the current one; the new folder still needs to appear.
        var deep = KnownPaths.InternalStorage.Combine("newfolder").Combine("inside.bin");
        files.Add(KnownPaths.InternalStorage.Combine("newfolder"), isDirectory: true);

        transfers.RaiseCompletedUpload(deep);

        Assert.True(await WaitForListingsAsync(files, before + 1));
        await _dispatcher.DrainAsync();
        Assert.Contains(explorer.Entries, row => row.Name == "newfolder");
    }

    [Fact]
    public async Task An_upload_outside_the_open_folder_does_not_disturb_it()
    {
        var (_, files, transfers) = await AttachAsync();
        var before = files.ListCalls;

        // Somewhere else entirely: the folder on screen cannot have changed.
        transfers.RaiseCompletedUpload(DevicePath.Parse("/storage/other/unrelated.mp4"));
        await SettleAsync();

        Assert.Equal(before, files.ListCalls);
    }

    [Fact]
    public async Task An_upload_deep_inside_an_existing_subfolder_does_not_refresh()
    {
        var (explorer, files, transfers) = Create();

        // "Movies" is already on screen, so a file landing inside it changes nothing visible here.
        files.Add(KnownPaths.Movies, isDirectory: true);
        await explorer.AttachAsync(new StubSession(files, transfers));

        var before = files.ListCalls;
        Assert.Contains(explorer.Entries, row => row.Name == "Movies");

        transfers.RaiseCompletedUpload(KnownPaths.Movies.Combine("clip").Combine("deep.mp4"));
        await SettleAsync();

        // Re-listing a large folder for an invisible change is wasted device traffic.
        Assert.Equal(before, files.ListCalls);
    }

    [Fact]
    public async Task A_completed_download_does_not_trigger_a_refresh()
    {
        var (_, files, transfers) = await AttachAsync();
        var before = files.ListCalls;

        // Downloads write to the PC, so nothing on the device changed.
        transfers.RaiseCompleted(
            KnownPaths.InternalStorage.Combine("pulled.bin"), TransferDirection.Download);
        await SettleAsync();

        Assert.Equal(before, files.ListCalls);
    }

    [Fact]
    public async Task An_unfinished_upload_does_not_trigger_a_refresh()
    {
        var (_, files, transfers) = await AttachAsync();
        var before = files.ListCalls;

        // Progress and status changes fire constantly; only completion means the file is there.
        foreach (var status in new[]
                 {
                     TransferStatus.Queued, TransferStatus.Transferring, TransferStatus.Paused,
                     TransferStatus.Retrying,
                 })
        {
            transfers.RaiseJob(
                KnownPaths.InternalStorage.Combine("partial.bin"), TransferDirection.Upload, status);
        }
        await SettleAsync();

        Assert.Equal(before, files.ListCalls);
    }

    [Fact]
    public async Task A_burst_of_completions_causes_one_refresh_not_many()
    {
        var (_, files, transfers) = await AttachAsync();
        var before = files.ListCalls;

        // A bulk upload finishes many jobs at once; re-listing per file would be slow and pointless.
        for (var i = 0; i < 40; i++)
        {
            transfers.RaiseCompletedUpload(KnownPaths.InternalStorage.Combine($"file{i}.bin"));
        }

        Assert.True(await WaitForListingsAsync(files, before + 1));
        await SettleAsync();

        Assert.True(files.ListCalls - before <= 2,
            $"40 completions caused {files.ListCalls - before} listings; they should have been coalesced");
    }

    [Fact]
    public async Task Detaching_stops_listening_so_a_departed_device_is_not_queried()
    {
        var (explorer, files, transfers) = await AttachAsync();

        explorer.Detach();
        var after = files.ListCalls;

        transfers.RaiseCompletedUpload(KnownPaths.InternalStorage.Combine("late.bin"));
        await SettleAsync();

        Assert.Equal(after, files.ListCalls);
    }

    // ---------------- stubs ----------------

    /// <summary>Runs posted work immediately, so a test can observe the result.</summary>
    /// <summary>
    /// Stands in for Avalonia's dispatcher: one thread that owns a <see cref="SynchronizationContext"/>.
    /// </summary>
    /// <remarks>
    /// Running posted work inline is simpler and is how this started, but it leaves whatever context the
    /// test runner installed as the ambient one. <c>ScheduleRefreshAsync</c> awaits its coalescing delay
    /// with <c>ConfigureAwait(true)</c> — correct in the app, where it resumes on the UI thread — so inline
    /// posting made it capture xUnit's limited-concurrency context instead. The continuation then queued
    /// behind every other test class running in parallel and the wait window expired under load, which is
    /// exactly the flake this replaces. Owning a thread makes resumption deterministic and models the real
    /// dispatcher rather than bypassing it.
    /// </remarks>
    private sealed class TestDispatcher : IUiDispatcher, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public TestDispatcher()
        {
            _thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "test-ui-dispatcher",
            };
            _thread.Start();
        }

        public void Post(Action action) => Enqueue(action);

        /// <summary>Completes once everything already queued has run.</summary>
        /// <remarks>
        /// The queue is FIFO on a single thread, so a sentinel posted now runs after all work posted
        /// before it. That is what makes it safe to assert on a bound collection from the test thread.
        /// </remarks>
        public Task DrainAsync()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Enqueue(() => done.TrySetResult()))
            {
                done.TrySetResult();
            }

            return done.Task;
        }

        private bool Enqueue(Action action)
        {
            try
            {
                _queue.Add(action);
                return true;
            }
            catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
            {
                // Disposed, or racing a shutdown. Dropping the work is correct here.
                return false;
            }
        }

        private void Pump()
        {
            SynchronizationContext.SetSynchronizationContext(new QueueContext(this));

            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch
                {
                    // A dispatcher that kills the process on a handler fault would mask the real
                    // assertion. Failures surface through the assertions instead.
                }
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }

        /// <summary>Sends continuations back to the dispatcher thread, as Avalonia's context does.</summary>
        private sealed class QueueContext(TestDispatcher owner) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state) => owner.Enqueue(() => d(state));

            public override void Send(SendOrPostCallback d, object? state) => d(state);
        }
    }

    private sealed class StubFileSystem : IDeviceFileSystem
    {
        private readonly Dictionary<DevicePath, bool> _entries = [];
        private int _listCalls;

        /// <summary>Incremented on the dispatcher thread, read from the test thread.</summary>
        public int ListCalls => Volatile.Read(ref _listCalls);

        public DeviceId DeviceId { get; } = new("refreshDevice");

        public void Add(DevicePath path, bool isDirectory = false) => _entries[path] = isDirectory;

        public Task<IReadOnlyList<DeviceEntry>> ListAsync(
            DevicePath path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _listCalls);

            var children = _entries
                .Where(pair => pair.Key.Parent == path)
                .Select(pair => new DeviceEntry
                {
                    DeviceId = DeviceId,
                    Path = pair.Key,
                    Kind = pair.Value ? DeviceEntryKind.Directory : DeviceEntryKind.File,
                    Size = 1024,
                    Modified = DateTimeOffset.UnixEpoch,
                })
                .ToList();

            return Task.FromResult<IReadOnlyList<DeviceEntry>>(children);
        }

        public Task<bool> ExistsAsync(DevicePath path, CancellationToken cancellationToken)
            => Task.FromResult(_entries.ContainsKey(path));

        public Task<DeviceFileInfo> GetInfoAsync(DevicePath path, CancellationToken cancellationToken)
            => Task.FromResult(new DeviceFileInfo
            {
                DeviceId = DeviceId,
                Path = path,
                Kind = DeviceEntryKind.File,
            });

        public Task<Stream> OpenReadAsync(DevicePath path, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<byte[]> ReadRangeAsync(
            DevicePath path, long offset, int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task UploadAsync(
            Stream source, DevicePath destination, IProgress<TransferProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DownloadAsync(
            DevicePath source, Stream destination, IProgress<TransferProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DownloadRangeAsync(
            DevicePath source, long startOffset, Stream destination,
            IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task UploadRangeAsync(
            Stream source, DevicePath destination, long startOffset,
            IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task CreateDirectoryAsync(DevicePath path, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteAsync(DevicePath path, bool recursive, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RenameAsync(
            DevicePath source, DevicePath destination, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task CopyAsync(
            DevicePath source, DevicePath destination, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<StorageInfo>> GetStorageAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<StorageInfo>>([]);

        public Task<string> ComputeSha256Async(DevicePath path, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class StubTransfers : ITransferManager
    {
        public DeviceId DeviceId { get; } = new("refreshDevice");

        public IReadOnlyList<TransferJob> Jobs => [];

        public event EventHandler<TransferJobChangedEventArgs>? JobChanged;

        public void RaiseCompletedUpload(DevicePath remotePath)
            => RaiseCompleted(remotePath, TransferDirection.Upload);

        public void RaiseCompleted(DevicePath remotePath, TransferDirection direction)
            => RaiseJob(remotePath, direction, TransferStatus.Completed);

        public void RaiseJob(DevicePath remotePath, TransferDirection direction, TransferStatus status)
            => JobChanged?.Invoke(this, new TransferJobChangedEventArgs(new TransferJob
            {
                Id = Guid.NewGuid(),
                DeviceId = DeviceId,
                Direction = direction,
                RemotePath = remotePath,
                LocalPath = @"C:\somewhere\file.bin",
                TotalBytes = 1024,
                BytesTransferred = 1024,
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow,
            }));

        public Task<TransferPlan> PlanDownloadAsync(
            IReadOnlyList<DevicePath> sources, string localDestinationDirectory,
            IProgress<TransferPlan>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<TransferPlan> PlanUploadAsync(
            IReadOnlyList<string> localSources, DevicePath destinationDirectory,
            IProgress<TransferPlan>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> EnqueueDownloadAsync(
            IReadOnlyList<DevicePath> sources, string localDestinationDirectory,
            ConflictPolicy conflictPolicy, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> EnqueueUploadAsync(
            IReadOnlyList<string> localSources, DevicePath destinationDirectory,
            ConflictPolicy conflictPolicy, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task PauseAsync(Guid jobId) => Task.CompletedTask;

        public Task ResumeAsync(Guid jobId) => Task.CompletedTask;

        public Task CancelAsync(Guid jobId) => Task.CompletedTask;

        public Task RetryAsync(Guid jobId) => Task.CompletedTask;

        public Task PauseAllAsync(string reason) => Task.CompletedTask;

        public Task ResumeAllAsync() => Task.CompletedTask;

        public Task ClearCompletedAsync() => Task.CompletedTask;
    }

    private sealed class StubSession(IDeviceFileSystem files, ITransferManager transfers) : IDeviceSession
    {
        public DeviceId DeviceId { get; } = new("refreshDevice");

        public DeviceInfo Info { get; } = new()
        {
            Id = new DeviceId("refreshDevice"),
            State = DeviceState.Online,
        };

        public DeviceCapabilities Capabilities { get; } = new();

        public IDeviceFileSystem FileSystem { get; } = files;

        public ITransferManager Transfers { get; } = transfers;

        public IThumbnailService Thumbnails => throw new NotSupportedException();

        public IGalleryService Gallery => throw new NotSupportedException();

        public ISearchService Search => throw new NotSupportedException();

        public IStorageAnalyzer Storage => throw new NotSupportedException();

        public IDuplicateFinder Duplicates => throw new NotSupportedException();

        public IBackupService Backup => throw new NotSupportedException();


        public IMetadataService Metadata => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubCache : ICacheService
    {
        public Task<IReadOnlyList<DeviceEntry>?> GetListingAsync(
            DeviceId deviceId, DevicePath path, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DeviceEntry>?>(null);

        public Task SetListingAsync(
            DeviceId deviceId, DevicePath path, IReadOnlyList<DeviceEntry> entries,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task InvalidateAsync(DeviceId deviceId, DevicePath path, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task InvalidateDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class StubProfiles : IDeviceProfileStore
    {
        public Task<DeviceProfile> GetAsync(DeviceId device, CancellationToken cancellationToken)
            => Task.FromResult(new DeviceProfile { DeviceId = device });

        public Task SaveAsync(DeviceProfile profile, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DevicePath>> GetFavoritesAsync(
            DeviceId device, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DevicePath>>([]);

        public Task AddFavoriteAsync(DeviceId device, DevicePath path, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RemoveFavoriteAsync(
            DeviceId device, DevicePath path, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public event EventHandler<AppSettings>? Changed;

        public Task LoadAsync(CancellationToken cancellationToken)
        {
            _ = Changed;
            return Task.CompletedTask;
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<DeviceProfile> GetProfileAsync(DeviceId deviceId, CancellationToken cancellationToken)
            => Task.FromResult(new DeviceProfile { DeviceId = deviceId });

        public Task SaveProfileAsync(DeviceProfile profile, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class StubShell : IShellIntegration
    {
        public Task RevealInFileManagerAsync(string localPath) => Task.CompletedTask;

        public Task OpenAsync(string localPath) => Task.CompletedTask;

        public Task OpenWithAsync(string localPath) => Task.CompletedTask;

        public string GetDefaultDownloadFolder() => Path.GetTempPath();

        public string GetAppDataFolder() => Path.GetTempPath();
    }
}



