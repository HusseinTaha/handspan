using Handspan.App.ViewModels;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Core.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace Handspan.Services.Tests;

/// <summary>
/// Gallery multi-selection (spec §31).
/// </summary>
/// <remarks>
/// The rules here are the kind that look obvious and are easy to get subtly wrong — range selection across
/// date groups, and what a plain click should do when a selection already exists. Both are cheap to test and
/// expensive to discover by hand.
/// </remarks>
public sealed class GallerySelectionTests
{
    private static async Task<GalleryViewModel> CreateGalleryAsync(int itemCount)
    {
        var settings = new StubSettings();

        var items = Enumerable.Range(0, itemCount)
            .Select(i => new MediaItem
            {
                DeviceId = new DeviceId("selectionDevice"),
                Path = KnownPaths.Camera.Combine($"IMG_{i:D4}.jpg"),
                Kind = MediaKind.Image,
                Size = 1_000_000 + i,

                // Two per day, so the timeline has several groups and ranges must cross them.
                Modified = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000 - (i / 2 * 86_400)),
            })
            .ToList();

        var gallery = new GalleryViewModel(
            settings,
            new StubPreviews(),
            new StubShell(),
            NullLogger<GalleryViewModel>.Instance);

        await gallery.AttachAsync(new StubSession(items));
        return gallery;
    }

    [Fact]
    public async Task Nothing_is_selected_to_begin_with()
    {
        var gallery = await CreateGalleryAsync(6);

        Assert.False(gallery.HasSelection);
        Assert.Empty(gallery.SelectedItems);
        Assert.Equal(string.Empty, gallery.SelectionSummary);
    }

    [Fact]
    public async Task Control_click_toggles_a_single_item()
    {
        var gallery = await CreateGalleryAsync(6);
        var first = gallery.Groups[0].Items[0];

        gallery.HandleTap(first, control: true, shift: false);

        Assert.True(first.IsSelected);
        Assert.True(gallery.HasSelection);
        Assert.Single(gallery.SelectedItems);

        gallery.HandleTap(first, control: true, shift: false);

        Assert.False(first.IsSelected);
        Assert.False(gallery.HasSelection);
    }

    [Fact]
    public async Task Shift_click_selects_a_range_across_date_groups()
    {
        var gallery = await CreateGalleryAsync(8);
        var flat = gallery.Groups.SelectMany(group => group.Items).ToList();

        // Two items per group, so 0..5 spans three separate days.
        gallery.HandleTap(flat[0], control: true, shift: false);
        gallery.HandleTap(flat[5], control: false, shift: true);

        Assert.Equal(6, gallery.SelectedItems.Count);
        Assert.All(flat.Take(6), item => Assert.True(item.IsSelected));
        Assert.All(flat.Skip(6), item => Assert.False(item.IsSelected));
        Assert.True(gallery.Groups.Count > 1, "the fixture should span several date groups");
    }

    [Fact]
    public async Task Shift_click_works_backwards_too()
    {
        var gallery = await CreateGalleryAsync(8);
        var flat = gallery.Groups.SelectMany(group => group.Items).ToList();

        gallery.HandleTap(flat[5], control: true, shift: false);
        gallery.HandleTap(flat[2], control: false, shift: true);

        // Anchor after the target: the range must still be inclusive in both directions.
        Assert.Equal(4, gallery.SelectedItems.Count);
        Assert.All(flat.Skip(2).Take(4), item => Assert.True(item.IsSelected));
    }

    [Fact]
    public async Task A_plain_click_with_no_selection_opens_instead_of_selecting()
    {
        var gallery = await CreateGalleryAsync(6);
        var first = gallery.Groups[0].Items[0];

        gallery.HandleTap(first, control: false, shift: false);

        // Opening is the expected behaviour, so nothing becomes selected.
        Assert.False(gallery.HasSelection);
        Assert.True(gallery.IsViewerOpen);
    }

    [Fact]
    public async Task A_plain_click_during_a_selection_reduces_it_rather_than_opening()
    {
        var gallery = await CreateGalleryAsync(6);
        var flat = gallery.Groups.SelectMany(group => group.Items).ToList();

        gallery.HandleTap(flat[0], control: true, shift: false);
        gallery.HandleTap(flat[1], control: true, shift: false);
        Assert.Equal(2, gallery.SelectedItems.Count);

        gallery.HandleTap(flat[3], control: false, shift: false);

        // Throwing away a selection because of one stray click would be infuriating, so the click is
        // interpreted as adjusting the selection instead.
        Assert.False(gallery.IsViewerOpen);
        Assert.Single(gallery.SelectedItems);
        Assert.True(flat[3].IsSelected);
        Assert.False(flat[0].IsSelected);
    }

    [Fact]
    public async Task Select_all_and_clear_cover_every_item()
    {
        var gallery = await CreateGalleryAsync(10);

        gallery.SelectAllCommand.Execute(null);
        Assert.Equal(10, gallery.SelectedItems.Count);

        gallery.ClearSelectionCommand.Execute(null);
        Assert.False(gallery.HasSelection);
        Assert.Empty(gallery.SelectedItems);
    }

    [Fact]
    public async Task The_summary_reports_the_count_and_total_size()
    {
        var gallery = await CreateGalleryAsync(6);
        var flat = gallery.Groups.SelectMany(group => group.Items).ToList();

        gallery.HandleTap(flat[0], control: true, shift: false);
        Assert.Contains("1 selected", gallery.SelectionSummary, StringComparison.Ordinal);

        gallery.HandleTap(flat[1], control: true, shift: false);
        Assert.Contains("2 selected", gallery.SelectionSummary, StringComparison.Ordinal);

        // Sizes are summed, so the user knows what they are about to move (spec §34).
        Assert.Contains("MB", gallery.SelectionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exporting_asks_for_confirmation_before_queueing_anything()
    {
        var gallery = await CreateGalleryAsync(6);
        var session = (StubSession)gallery.GetType()
            .GetField("_session", System.Reflection.BindingFlags.NonPublic
                                  | System.Reflection.BindingFlags.Instance)!
            .GetValue(gallery)!;

        gallery.SelectAllCommand.Execute(null);
        gallery.DownloadSelectedCommand.Execute(null);

        // Nothing may be queued until the user confirms (spec §34).
        Assert.True(gallery.IsExportOpen);
        Assert.Empty(((StubTransfers)session.Transfers).Queued);
        Assert.Contains("6 items", gallery.ExportPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirming_queues_every_selected_item_as_one_batch()
    {
        var gallery = await CreateGalleryAsync(6);
        var session = (StubSession)gallery.GetType()
            .GetField("_session", System.Reflection.BindingFlags.NonPublic
                                  | System.Reflection.BindingFlags.Instance)!
            .GetValue(gallery)!;

        gallery.SelectAllCommand.Execute(null);
        gallery.DownloadSelectedCommand.Execute(null);

        await gallery.ConfirmExportCommand.ExecuteAsync(null);

        var transfers = (StubTransfers)session.Transfers;
        var batch = Assert.Single(transfers.Queued);

        Assert.Equal(6, batch.Count);
        Assert.False(gallery.IsExportOpen);

        // The selection is cleared once queued, so a second click cannot silently copy everything again.
        Assert.False(gallery.HasSelection);
    }

    [Fact]
    public async Task Cancelling_the_export_queues_nothing_and_keeps_the_selection()
    {
        var gallery = await CreateGalleryAsync(6);
        var session = (StubSession)gallery.GetType()
            .GetField("_session", System.Reflection.BindingFlags.NonPublic
                                  | System.Reflection.BindingFlags.Instance)!
            .GetValue(gallery)!;

        gallery.SelectAllCommand.Execute(null);
        gallery.DownloadSelectedCommand.Execute(null);
        gallery.CancelExportCommand.Execute(null);

        Assert.False(gallery.IsExportOpen);
        Assert.Empty(((StubTransfers)session.Transfers).Queued);
        Assert.Equal(6, gallery.SelectedItems.Count);
    }

    // ---------------- albums and delete ----------------

    [Fact]
    public async Task Opening_an_album_narrows_the_timeline_to_it()
    {
        var gallery = await CreateGalleryAsync(8);
        var session = Session(gallery);
        var albumPath = KnownPaths.Camera.Combine("Sub");

        // Two of the eight items live in the album.
        ((StubGallery)session.Gallery).AlbumContents[albumPath] =
        [
            ItemAt(albumPath, "in-album-1.jpg"),
            ItemAt(albumPath, "in-album-2.jpg"),
        ];

        await gallery.OpenAlbumCommand.ExecuteAsync(
            new AlbumViewModel(new Album
            {
                DeviceId = new DeviceId("selectionDevice"),
                Path = albumPath,
                Name = "Sub",
            }));

        var shown = gallery.Groups.SelectMany(group => group.Items).ToList();

        Assert.Equal(2, shown.Count);
        Assert.All(shown, item => Assert.Equal(albumPath, item.Item.Path.Parent));
        Assert.NotNull(gallery.CurrentAlbum);
    }

    [Fact]
    public async Task Leaving_an_album_restores_the_whole_timeline()
    {
        var gallery = await CreateGalleryAsync(8);
        var session = Session(gallery);
        var albumPath = KnownPaths.Camera.Combine("Sub");

        ((StubGallery)session.Gallery).AlbumContents[albumPath] = [ItemAt(albumPath, "only.jpg")];

        await gallery.OpenAlbumCommand.ExecuteAsync(new AlbumViewModel(new Album
        {
            DeviceId = new DeviceId("selectionDevice"),
            Path = albumPath,
            Name = "Sub",
        }));

        Assert.Single(gallery.Groups.SelectMany(group => group.Items));

        await gallery.ShowAllCommand.ExecuteAsync(null);

        Assert.Null(gallery.CurrentAlbum);
        Assert.Equal(8, gallery.Groups.SelectMany(group => group.Items).Count());
    }

    [Fact]
    public async Task Deleting_asks_first_and_removes_nothing_until_confirmed()
    {
        var gallery = await CreateGalleryAsync(6);
        var session = Session(gallery);
        var files = (StubFileSystem)session.FileSystem;

        gallery.SelectAllCommand.Execute(null);
        gallery.DeleteSelectedCommand.Execute(null);

        // Deletion is permanent (spec §51), so nothing may happen before the user agrees.
        Assert.True(gallery.IsDeleteOpen);
        Assert.Empty(files.Deleted);
        Assert.Contains("6 items", gallery.DeletePrompt, StringComparison.Ordinal);
        Assert.Contains("Permanently", gallery.DeletePrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_a_delete_removes_nothing_and_keeps_the_selection()
    {
        var gallery = await CreateGalleryAsync(6);
        var files = (StubFileSystem)Session(gallery).FileSystem;

        gallery.SelectAllCommand.Execute(null);
        gallery.DeleteSelectedCommand.Execute(null);
        gallery.CancelDeleteCommand.Execute(null);

        Assert.False(gallery.IsDeleteOpen);
        Assert.Empty(files.Deleted);
        Assert.Equal(6, gallery.SelectedItems.Count);
    }

    [Fact]
    public async Task Confirming_deletes_every_selected_item()
    {
        var gallery = await CreateGalleryAsync(4);
        var files = (StubFileSystem)Session(gallery).FileSystem;

        gallery.SelectAllCommand.Execute(null);
        gallery.DeleteSelectedCommand.Execute(null);
        await gallery.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal(4, files.Deleted.Count);
        Assert.False(gallery.HasSelection);
        Assert.Contains("Deleted 4", gallery.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_protected_file_does_not_abandon_the_rest_of_the_batch()
    {
        var gallery = await CreateGalleryAsync(5);
        var files = (StubFileSystem)Session(gallery).FileSystem;

        // A file Android refuses to remove sits in the middle of the selection (spec §78).
        var flat = gallery.Groups.SelectMany(group => group.Items).ToList();
        files.RefuseToDelete.Add(flat[2].Item.Path);

        gallery.SelectAllCommand.Execute(null);
        gallery.DeleteSelectedCommand.Execute(null);
        await gallery.ConfirmDeleteCommand.ExecuteAsync(null);

        // The other four must still have gone, and the failure must be reported rather than swallowed.
        Assert.Equal(4, files.Deleted.Count);
        Assert.DoesNotContain(flat[2].Item.Path, files.Deleted);
        Assert.Contains("could not be removed", gallery.Status, StringComparison.Ordinal);
    }

    private static MediaItem ItemAt(DevicePath folder, string name) => new()
    {
        DeviceId = new DeviceId("selectionDevice"),
        Path = folder.Combine(name),
        Kind = MediaKind.Image,
        Size = 500_000,
        Modified = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000),
    };

    private static StubSession Session(GalleryViewModel gallery)
        => (StubSession)gallery.GetType()
            .GetField("_session", System.Reflection.BindingFlags.NonPublic
                                  | System.Reflection.BindingFlags.Instance)!
            .GetValue(gallery)!;

    // ---------------- stubs ----------------

    /// <summary>Records deletions and can refuse specific paths, as a protected location would.</summary>
    private sealed class StubFileSystem : IDeviceFileSystem
    {
        public List<DevicePath> Deleted { get; } = [];

        public HashSet<DevicePath> RefuseToDelete { get; } = [];

        public DeviceId DeviceId { get; } = new("selectionDevice");

        public Task DeleteAsync(DevicePath path, bool recursive, CancellationToken cancellationToken)
        {
            if (RefuseToDelete.Contains(path))
            {
                throw new Core.Exceptions.AccessDeniedException(path);
            }

            Deleted.Add(path);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DeviceEntry>> ListAsync(
            DevicePath path, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DeviceEntry>>([]);

        public Task<DeviceFileInfo> GetInfoAsync(DevicePath path, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> ExistsAsync(DevicePath path, CancellationToken cancellationToken)
            => Task.FromResult(true);

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

    private sealed class StubSession(IReadOnlyList<MediaItem> items) : IDeviceSession
    {
        public DeviceId DeviceId { get; } = new("selectionDevice");

        public DeviceInfo Info { get; } = new()
        {
            Id = new DeviceId("selectionDevice"),
            State = DeviceState.Online,
        };

        public DeviceCapabilities Capabilities { get; } = new();

        public IDeviceFileSystem FileSystem { get; } = new StubFileSystem();

        public ITransferManager Transfers { get; } = new StubTransfers();

        public IThumbnailService Thumbnails { get; } = new StubThumbnails();

        public IGalleryService Gallery { get; } = new StubGallery(items);

        public ISearchService Search => throw new NotSupportedException();

        public IStorageAnalyzer Storage => throw new NotSupportedException();

        public IDuplicateFinder Duplicates => throw new NotSupportedException();

        public IBackupService Backup => throw new NotSupportedException();


        public IMetadataService Metadata => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubGallery(IReadOnlyList<MediaItem> items) : IGalleryService
    {
        /// <summary>Contents per album path, so opening an album can be observed.</summary>
        public Dictionary<DevicePath, IReadOnlyList<MediaItem>> AlbumContents { get; } = [];
        public DeviceId DeviceId { get; } = new("selectionDevice");

        public IReadOnlyList<DevicePath> Sources { get; set; } = [];

        public Task<IReadOnlyList<MediaItem>> GetTimelineAsync(
            MediaKind? filter, int skip, int take, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MediaItem>>(items
                .Where(item => filter is null || item.Kind == filter)
                .Skip(skip)
                .Take(take)
                .ToList());

        public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Album>>([]);

        public Task<IReadOnlyList<MediaItem>> GetAlbumContentsAsync(
            DevicePath albumPath, CancellationToken cancellationToken)
            => Task.FromResult(AlbumContents.GetValueOrDefault(albumPath, []));

        public Task RefreshAsync(IProgress<int>? scannedCount, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class StubTransfers : ITransferManager
    {
        public List<IReadOnlyList<DevicePath>> Queued { get; } = [];

        public DeviceId DeviceId { get; } = new("selectionDevice");

        public IReadOnlyList<TransferJob> Jobs => [];

        public event EventHandler<TransferJobChangedEventArgs>? JobChanged;

        public Task<IReadOnlyList<Guid>> EnqueueDownloadAsync(
            IReadOnlyList<DevicePath> sources,
            string localDestinationDirectory,
            ConflictPolicy conflictPolicy,
            CancellationToken cancellationToken)
        {
            _ = JobChanged;
            Queued.Add(sources);
            return Task.FromResult<IReadOnlyList<Guid>>(
                sources.Select(_ => Guid.NewGuid()).ToList());
        }

        public Task<TransferPlan> PlanDownloadAsync(
            IReadOnlyList<DevicePath> sources, string localDestinationDirectory,
            IProgress<TransferPlan>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<TransferPlan> PlanUploadAsync(
            IReadOnlyList<string> localSources, DevicePath destinationDirectory,
            IProgress<TransferPlan>? progress, CancellationToken cancellationToken)
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

    private sealed class StubThumbnails : IThumbnailService
    {
        public Task<byte[]?> GetThumbnailAsync(
            MediaItem item, int maxEdgePixels, CancellationToken cancellationToken)
            => Task.FromResult<byte[]?>(null);

        public void Prefetch(IReadOnlyList<MediaItem> items, int maxEdgePixels)
        {
        }

        public void CancelPending(IReadOnlyList<MediaItem> items)
        {
        }

        public Task<long> GetCacheSizeAsync(CancellationToken cancellationToken) => Task.FromResult(0L);

        public Task ClearCacheAsync(DeviceId? deviceId, CancellationToken cancellationToken)
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

    private sealed class StubPreviews : IMediaPreviewService
    {
        public Task<Uri> GetStreamUrlAsync(
            DeviceId deviceId, DevicePath path, CancellationToken cancellationToken)
            => Task.FromResult(new Uri("http://127.0.0.1/stub"));

        public Task<Stream> OpenImageAsync(
            DeviceId deviceId, DevicePath path, CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new MemoryStream());
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


