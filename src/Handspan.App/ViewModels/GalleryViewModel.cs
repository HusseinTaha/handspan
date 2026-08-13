using System.Collections.ObjectModel;
using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Core.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Handspan.App.ViewModels;

/// <summary>
/// The gallery: a date-grouped timeline of device media (spec §18, §25).
/// </summary>
/// <remarks>
/// Items come from the index so the page opens instantly, then a background scan refreshes it (spec §60).
/// Thumbnails load per item through <see cref="IThumbnailService"/>, which reads only a file's header where
/// it can — the grid never pulls full-size photos (spec §94).
/// </remarks>
public sealed partial class GalleryViewModel : ViewModelBase
{
    /// <summary>
    /// Upper bound on items pulled from the index in one load.
    /// </summary>
    /// <remarks>
    /// High enough to show a whole phone — the spec's target is 50,000+ items (§45) — while still bounding
    /// memory if an index is somehow enormous. Tiles are realized lazily by the view, so the cost of a large
    /// number here is the view-model objects, not decoded images.
    /// </remarks>
    private const int MaxItems = 100_000;

    private readonly ISettingsService _settings;
    private readonly IMediaPreviewService _previews;
    private readonly IShellIntegration _shell;
    private readonly ILogger<GalleryViewModel> _logger;

    private IDeviceSession? _session;
    private CancellationTokenSource? _scan;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _status = "Connect a device to browse its photos.";

    [ObservableProperty]
    private MediaKind? _filter;

    /// <summary>The album being viewed, or null for the whole timeline (spec §26).</summary>
    [ObservableProperty]
    private AlbumViewModel? _currentAlbum;

    [ObservableProperty]
    private bool _isDeleteOpen;

    [ObservableProperty]
    private string _deletePrompt = string.Empty;

    [ObservableProperty]
    private GalleryItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isViewerOpen;

    // --- multi-select and bulk export (spec §31, §34) ---

    [ObservableProperty]
    private string _selectionSummary = string.Empty;

    [ObservableProperty]
    private bool _isExportOpen;

    [ObservableProperty]
    private string _exportPrompt = string.Empty;

    [ObservableProperty]
    private string _exportDestination = string.Empty;

    /// <summary>Anchor for shift-click range selection.</summary>
    private GalleryItemViewModel? _selectionAnchor;

    public GalleryViewModel(
        ISettingsService settings,
        IMediaPreviewService previews,
        IShellIntegration shell,
        ILogger<GalleryViewModel> logger)
    {
        _settings = settings;
        _previews = previews;
        _shell = shell;
        _logger = logger;
    }

    /// <summary>Date-grouped sections, newest first (spec §25).</summary>
    public ObservableCollection<GalleryGroupViewModel> Groups { get; } = [];

    public ObservableCollection<AlbumViewModel> Albums { get; } = [];

    public bool HasItems => Groups.Count > 0;

    public bool HasSession => _session is not null;

    /// <summary>Every tile in timeline order, which is what range selection and stepping work over.</summary>
    private IReadOnlyList<GalleryItemViewModel> FlatItems =>
        Groups.SelectMany(group => group.Items).ToList();

    public IReadOnlyList<GalleryItemViewModel> SelectedItems =>
        FlatItems.Where(item => item.IsSelected).ToList();

    public bool HasSelection => FlatItems.Any(item => item.IsSelected);

    public async Task AttachAsync(IDeviceSession session)
    {
        _session = session;
        OnPropertyChanged(nameof(HasSession));

        await LoadFromIndexAsync().ConfigureAwait(true);

        // An empty index on first connect means the device has never been scanned.
        if (!HasItems)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    public void Detach()
    {
        _scan?.Cancel();
        _session = null;
        Groups.Clear();
        Albums.Clear();
        Status = "Connect a device to browse its photos.";
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasSession));
    }

    private async Task LoadFromIndexAsync()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            // Viewing one album narrows the timeline to it; otherwise the whole device is shown.
            var items = CurrentAlbum is { } openAlbum
                ? Filtered(await _session.Gallery
                    .GetAlbumContentsAsync(openAlbum.Path, CancellationToken.None).ConfigureAwait(true))
                : await _session.Gallery
                    .GetTimelineAsync(Filter, 0, MaxItems, CancellationToken.None)
                    .ConfigureAwait(true);

            Rebuild(items);

            Albums.Clear();
            foreach (var album in await _session.Gallery.GetAlbumsAsync(CancellationToken.None)
                         .ConfigureAwait(true))
            {
                Albums.Add(new AlbumViewModel(album));
            }
        }
        catch (DeviceException ex)
        {
            Status = ex.UserMessage;
        }
    }

    private void Rebuild(IReadOnlyList<MediaItem> items)
    {
        // Rebuilding replaces the tiles, so any selection referred to objects that no longer exist.
        _selectionAnchor = null;
        Groups.Clear();

        foreach (var group in items.GroupBy(item => item.TimelineDate).OrderByDescending(g => g.Key))
        {
            Groups.Add(new GalleryGroupViewModel(
                group.Key,
                group.Select(item => new GalleryItemViewModel(item, _session!, _settings))));
        }

        OnPropertyChanged(nameof(HasItems));
        NotifySelectionChanged();

        var count = items.Count;
        Status = count switch
        {
            0 => "No photos or videos found in the scanned folders.",
            1 => "1 item",
            MaxItems => $"{count:N0} items (the most recent {MaxItems:N0})",
            _ => $"{count:N0} items in {Groups.Count:N0} day{(Groups.Count == 1 ? "" : "s")}",
        };
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_session is null || IsScanning)
        {
            return;
        }

        _scan?.Cancel();
        _scan = new CancellationTokenSource();

        IsScanning = true;
        Status = "Scanning for photos and videos…";

        try
        {
            var progress = new Progress<int>(found =>
                Dispatcher.UIThread.Post(() => Status = $"Scanning… {found} items found"));

            await _session.Gallery.RefreshAsync(progress, _scan.Token).ConfigureAwait(true);
            await LoadFromIndexAsync().ConfigureAwait(true);

            if (_session.Thumbnails is Media.ThumbnailService service)
            {
                await service.TrimCacheAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled.";
        }
        catch (DeviceException ex)
        {
            Status = ex.UserMessage;
            _logger.LogWarning("Gallery scan failed: {Reason}", ex.UserMessage);
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>Applies the photo/video filter to an album's contents, which the index query does not.</summary>
    private IReadOnlyList<MediaItem> Filtered(IReadOnlyList<MediaItem> items)
        => Filter is { } kind ? items.Where(item => item.Kind == kind).ToList() : items;

    [RelayCommand]
    private async Task SetFilterAsync(string? kind)
    {
        Filter = kind switch
        {
            "Image" => MediaKind.Image,
            "Video" => MediaKind.Video,
            _ => null,
        };

        await LoadFromIndexAsync().ConfigureAwait(true);
    }

    /// <summary>Opens an album, narrowing the timeline to its contents (spec §26).</summary>
    [RelayCommand]
    private async Task OpenAlbumAsync(AlbumViewModel? album)
    {
        if (album is null || _session is null)
        {
            return;
        }

        ClearSelection();
        CurrentAlbum = album;
        await LoadFromIndexAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ShowAllAsync()
    {
        ClearSelection();
        CurrentAlbum = null;
        await LoadFromIndexAsync().ConfigureAwait(true);
    }

    // --- selection (spec §31) ---

    /// <summary>
    /// Handles a tap, interpreting keyboard modifiers the way a file manager does.
    /// </summary>
    /// <remarks>
    /// Ctrl toggles one tile, Shift extends from the last anchor, and a plain tap opens the item — except
    /// while a selection exists, where opening would throw the selection away for what looks like a
    /// mis-click. There, a plain tap adjusts the selection instead.
    /// </remarks>
    public void HandleTap(GalleryItemViewModel item, bool control, bool shift)
    {
        if (control)
        {
            item.IsSelected = !item.IsSelected;
            _selectionAnchor = item;
            NotifySelectionChanged();
            return;
        }

        if (shift && _selectionAnchor is not null)
        {
            SelectRange(_selectionAnchor, item);
            NotifySelectionChanged();
            return;
        }

        if (HasSelection)
        {
            // Reduce to just this tile rather than opening, so an existing selection is never lost silently.
            foreach (var other in FlatItems)
            {
                other.IsSelected = false;
            }

            item.IsSelected = true;
            _selectionAnchor = item;
            NotifySelectionChanged();
            return;
        }

        _selectionAnchor = item;
        OpenCommand.Execute(item);
    }

    private void SelectRange(GalleryItemViewModel from, GalleryItemViewModel to)
    {
        var flat = FlatItems.ToList();
        var start = flat.IndexOf(from);
        var end = flat.IndexOf(to);

        if (start < 0 || end < 0)
        {
            return;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        for (var i = start; i <= end; i++)
        {
            flat[i].IsSelected = true;
        }
    }

    private void NotifySelectionChanged()
    {
        var selected = SelectedItems;

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedItems));

        SelectionSummary = selected.Count switch
        {
            0 => string.Empty,
            1 => $"1 selected · {FormatSize.Bytes(selected[0].Item.Size)}",
            var count => $"{count} selected · {FormatSize.Bytes(selected.Sum(item => item.Item.Size))}",
        };
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in FlatItems)
        {
            item.IsSelected = true;
        }

        NotifySelectionChanged();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in FlatItems)
        {
            item.IsSelected = false;
        }

        _selectionAnchor = null;
        NotifySelectionChanged();
    }

    /// <summary>
    /// Queues every selected item for download in one go (spec §11, §34).
    /// </summary>
    /// <remarks>
    /// The whole selection becomes one batch on the transfer queue, so it is scheduled, resumable and
    /// retried like any other transfer rather than being a separate copy path.
    /// </remarks>
    [RelayCommand]
    private void DownloadSelected()
    {
        var selected = SelectedItems;
        if (_session is null || selected.Count == 0)
        {
            return;
        }

        var bytes = selected.Sum(item => item.Item.Size);

        ExportDestination = Path.Combine(_shell.GetDefaultDownloadFolder(), "Handspan");
        ExportPrompt = $"Copy {selected.Count} item{(selected.Count == 1 ? "" : "s")} to this computer?\n"
                       + $"{FormatSize.Bytes(bytes)} in total.";

        IsExportOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmExportAsync()
    {
        IsExportOpen = false;

        var selected = SelectedItems;
        if (_session is null || selected.Count == 0)
        {
            return;
        }

        try
        {
            var paths = selected.Select(item => item.Item.Path).ToList();

            await _session.Transfers.EnqueueDownloadAsync(
                    paths, ExportDestination, ConflictPolicy.Rename, CancellationToken.None)
                .ConfigureAwait(true);

            Status = $"Queued {paths.Count} item{(paths.Count == 1 ? "" : "s")} — "
                     + "follow them on the Transfers page.";

            ClearSelection();
        }
        catch (DeviceException ex)
        {
            Status = ex.UserMessage;
        }
    }

    [RelayCommand]
    private void CancelExport() => IsExportOpen = false;

    // --- delete (spec §9, §51) ---

    /// <summary>
    /// Deletes the selected items from the device, after confirmation.
    /// </summary>
    /// <remarks>
    /// There is no recycle bin on Android and this app does not fake one (spec §51), so the confirmation says
    /// plainly that the files are gone. The count and total size are shown because deleting 200 photos by
    /// accident is not recoverable.
    /// </remarks>
    [RelayCommand]
    private void DeleteSelected()
    {
        var selected = SelectedItems;
        if (_session is null || selected.Count == 0)
        {
            return;
        }

        var bytes = selected.Sum(item => item.Item.Size);

        DeletePrompt = selected.Count == 1
            ? $"Permanently delete \"{selected[0].Name}\" from the device?"
            : $"Permanently delete {selected.Count} items ({FormatSize.Bytes(bytes)}) from the device?";

        IsDeleteOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        IsDeleteOpen = false;

        var selected = SelectedItems;
        if (_session is null || selected.Count == 0)
        {
            return;
        }

        var deleted = 0;
        var failures = new List<string>();

        foreach (var item in selected)
        {
            try
            {
                await _session.FileSystem
                    .DeleteAsync(item.Item.Path, recursive: false, CancellationToken.None)
                    .ConfigureAwait(true);
                deleted++;
            }
            catch (DeviceException ex)
            {
                // One protected or vanished file must not abandon the rest of the batch.
                failures.Add(ex.UserMessage);
            }
        }

        var outcome = failures.Count == 0
            ? $"Deleted {deleted} item{(deleted == 1 ? "" : "s")}."
            : $"Deleted {deleted}; {failures.Count} could not be removed. {failures[0]}";

        ClearSelection();

        // The index still lists the deleted files, so a rescan is what makes the gallery honest again.
        await RefreshAsync().ConfigureAwait(true);

        // Reported after the rescan, which otherwise replaces the status with its own item count and
        // leaves the user with no confirmation that anything was deleted.
        Status = outcome;
    }

    [RelayCommand]
    private void CancelDelete() => IsDeleteOpen = false;

    [RelayCommand]
    private void Open(GalleryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        // Video is streamed to an external player rather than shown here: an in-app player needs the
        // LGPL media bundle, and streaming already works without it (spec §24, §58).
        if (item.IsVideo)
        {
            _ = PlayAsync(item);
            return;
        }

        SelectedItem = item;
        IsViewerOpen = true;
        _ = item.LoadFullImageAsync();
    }

    /// <summary>
    /// Opens a video in the system's default player, streamed from the device (spec §24).
    /// </summary>
    /// <remarks>
    /// The player receives a loopback URL with range support, so it seeks without the file ever being
    /// downloaded. An in-app player arrives with the media bundle in phase 4b.
    /// </remarks>
    [RelayCommand]
    private async Task PlayAsync(GalleryItemViewModel? item)
    {
        if (item is null || _session is null)
        {
            return;
        }

        try
        {
            var url = await _previews
                .GetStreamUrlAsync(_session.DeviceId, item.Item.Path, CancellationToken.None)
                .ConfigureAwait(true);

            await _shell.OpenAsync(url.ToString()).ConfigureAwait(true);
            Status = $"Streaming {item.Name} to your default player.";
        }
        catch (Exception ex) when (ex is DeviceException or InvalidOperationException)
        {
            Status = ex is DeviceException device
                ? device.UserMessage
                : "Could not start streaming this file.";
        }
    }

    [RelayCommand]
    private void CloseViewer()
    {
        IsViewerOpen = false;
        SelectedItem?.ReleaseFullImage();
    }

    /// <summary>Moves through the flattened timeline, so navigation crosses date groups (spec §23).</summary>
    [RelayCommand]
    private void Step(string direction)
    {
        var flat = Groups.SelectMany(group => group.Items).ToList();
        if (SelectedItem is null || flat.Count == 0)
        {
            return;
        }

        var index = flat.IndexOf(SelectedItem);
        if (index < 0)
        {
            return;
        }

        var next = direction == "next" ? index + 1 : index - 1;
        if (next < 0 || next >= flat.Count)
        {
            return;
        }

        SelectedItem.ReleaseFullImage();
        SelectedItem = flat[next];
        _ = SelectedItem.LoadFullImageAsync();
    }
}

/// <summary>One date section of the timeline.</summary>
public sealed class GalleryGroupViewModel(DateTime date, IEnumerable<GalleryItemViewModel> items)
{
    public DateTime Date { get; } = date;

    public string Header { get; } = date == DateTime.Today
        ? "Today"
        : date == DateTime.Today.AddDays(-1)
            ? "Yesterday"
            : date.ToString("D");

    public IReadOnlyList<GalleryItemViewModel> Items { get; } = items.ToList();
}

/// <summary>One media tile, loading its thumbnail on demand.</summary>
public sealed partial class GalleryItemViewModel : ViewModelBase
{
    private readonly IDeviceSession _session;
    private readonly ISettingsService _settings;
    private bool _thumbnailRequested;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private Bitmap? _fullImage;

    [ObservableProperty]
    private bool _isLoadingFullImage;

    /// <summary>Part of the current multi-selection (spec §31).</summary>
    [ObservableProperty]
    private bool _isSelected;

    public GalleryItemViewModel(MediaItem item, IDeviceSession session, ISettingsService settings)
    {
        Item = item;
        _session = session;
        _settings = settings;
    }

    public MediaItem Item { get; }

    public string Name => Item.Name;

    public string SizeText => FormatSize.Bytes(Item.Size);

    public bool IsVideo => Item.Kind == MediaKind.Video;

    /// <summary>Shown until a thumbnail arrives, and permanently for formats we cannot yet decode.</summary>
    public string Placeholder => Item.Kind switch
    {
        MediaKind.Video => "🎬",
        MediaKind.Audio => "🎵",
        _ => "🖼",
    };

    /// <summary>Requested when the tile scrolls into view; safe to call repeatedly.</summary>
    public async Task LoadThumbnailAsync()
    {
        if (_thumbnailRequested)
        {
            return;
        }

        _thumbnailRequested = true;

        try
        {
            var bytes = await _session.Thumbnails
                .GetThumbnailAsync(Item, _settings.Current.ThumbnailMaxEdgePixels, CancellationToken.None)
                .ConfigureAwait(true);

            if (bytes is { Length: > 0 })
            {
                using var stream = new MemoryStream(bytes);
                Thumbnail = new Bitmap(stream);
            }
        }
        catch (Exception ex) when (ex is DeviceException or ArgumentException or InvalidOperationException)
        {
            // A tile without a thumbnail keeps its placeholder rather than breaking the grid.
            _thumbnailRequested = true;
        }
    }

    /// <summary>Loads the full image for the viewer, streaming it rather than saving a copy (spec §58).</summary>
    public async Task LoadFullImageAsync()
    {
        if (FullImage is not null || Item.Kind != MediaKind.Image)
        {
            return;
        }

        IsLoadingFullImage = true;
        try
        {
            using var buffer = new MemoryStream();
            await _session.FileSystem.DownloadAsync(Item.Path, buffer, null, CancellationToken.None)
                .ConfigureAwait(true);

            buffer.Position = 0;
            FullImage = new Bitmap(buffer);
        }
        catch (Exception ex) when (ex is DeviceException or ArgumentException)
        {
            FullImage = null;
        }
        finally
        {
            IsLoadingFullImage = false;
        }
    }

    public void ReleaseFullImage()
    {
        FullImage?.Dispose();
        FullImage = null;
    }
}

/// <summary>One virtual album (spec §26).</summary>
public sealed class AlbumViewModel(Album album)
{
    public Album Album { get; } = album;

    public string Name => Album.Name;

    public DevicePath Path => Album.Path;

    public string Summary => $"{Album.ItemCount} item{(Album.ItemCount == 1 ? "" : "s")} · "
                             + FormatSize.Bytes(Album.TotalBytes);
}
