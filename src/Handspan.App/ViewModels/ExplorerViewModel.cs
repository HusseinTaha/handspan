using System.Collections.ObjectModel;
using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.App.Platform;
using Handspan.Core.Platform;
using Handspan.Data;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Handspan.App.ViewModels;

/// <summary>How the entry list is ordered (spec §8).</summary>
public enum ExplorerSortColumn
{
    Name,
    Type,
    Size,
    Modified,
}

/// <summary>
/// The Explorer: navigation, listing and file operations (spec §7–§9).
/// </summary>
/// <remarks>
/// Loading is cache-first: the cached listing renders immediately, then the device is re-read and the
/// list is replaced. That two-step is what makes revisiting a folder feel instant (spec §29, §45).
/// No ADB call is awaited on the UI thread, and every load is cancellable (spec §46, §47).
/// </remarks>
public sealed partial class ExplorerViewModel : ViewModelBase
{
    private readonly ICacheService _cache;
    private readonly IShellIntegration _shell;
    private readonly ISettingsService _settings;
    private readonly IDeviceProfileStore _favorites;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ExplorerViewModel> _logger;
    private readonly List<DevicePath> _back = [];
    private readonly List<DevicePath> _forward = [];

    private IDeviceSession? _session;
    private CancellationTokenSource? _load;
    private List<DeviceEntry> _lastListing = [];
    private bool _refreshPending;

    [ObservableProperty]
    private string _pathText = KnownPaths.InternalStorage.Value;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isFromCache;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private EntryRowViewModel? _selectedEntry;

    /// <summary>Multi-selection, kept in sync from the list's own selection (spec §31).</summary>
    private readonly List<EntryRowViewModel> _selection = [];

    [ObservableProperty]
    private string _selectionSummary = string.Empty;

    [ObservableProperty]
    private bool _isPropertiesOpen;

    [ObservableProperty]
    private PropertiesViewModel? _properties;

    [ObservableProperty]
    private bool _showHiddenFiles;

    [ObservableProperty]
    private ExplorerSortColumn _sortColumn = ExplorerSortColumn.Name;

    [ObservableProperty]
    private bool _sortAscending = true;

    // --- inline prompt (rename / new folder) ---

    [ObservableProperty]
    private bool _isPromptOpen;

    [ObservableProperty]
    private string _promptTitle = string.Empty;

    [ObservableProperty]
    private string _promptValue = string.Empty;

    [ObservableProperty]
    private string? _promptError;

    // --- inline confirmation (delete) ---

    [ObservableProperty]
    private bool _isConfirmOpen;

    [ObservableProperty]
    private string _confirmMessage = string.Empty;

    // --- transfer preview (spec §34) ---

    [ObservableProperty]
    private bool _isPlanOpen;

    [ObservableProperty]
    private string _planTitle = string.Empty;

    [ObservableProperty]
    private string _planDetails = string.Empty;

    [ObservableProperty]
    private string _planConflicts = string.Empty;

    private Func<string, Task>? _promptAction;
    private Func<Task>? _confirmAction;
    private Func<Task>? _planAction;

    public ExplorerViewModel(
        ICacheService cache,
        IShellIntegration shell,
        ISettingsService settings,
        IDeviceProfileStore favorites,
        IUiDispatcher dispatcher,
        ILogger<ExplorerViewModel> logger)
    {
        _cache = cache;
        _shell = shell;
        _settings = settings;
        _favorites = favorites;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public ObservableCollection<EntryRowViewModel> Entries { get; } = [];

    public ObservableCollection<BreadcrumbViewModel> Breadcrumbs { get; } = [];

    /// <summary>Pinned folders for the current device (spec §65, §66).</summary>
    public ObservableCollection<FavoriteViewModel> Favorites { get; } = [];

    public bool HasFavorites => Favorites.Count > 0;

    public IReadOnlyList<EntryRowViewModel> SelectedEntries => _selection;

    /// <summary>True once more than one item is chosen, which is when the bulk actions become useful.</summary>
    public bool HasMultiSelection => _selection.Count > 1;

    public bool HasAnySelection => _selection.Count > 0;

    /// <summary>True when the folder on screen is pinned, so the button can offer the opposite action.</summary>
    public bool IsCurrentFolderPinned =>
        Favorites.Any(favorite => favorite.Path == CurrentPath);

    public DevicePath CurrentPath { get; private set; } = KnownPaths.InternalStorage;

    public bool HasSession => _session is not null;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public bool CanGoUp => !CurrentPath.IsRoot && CurrentPath != KnownPaths.InternalStorage;

    /// <summary>Binds this Explorer to a device session and shows its shared storage.</summary>
    public async Task AttachAsync(IDeviceSession session)
    {
        Detach();

        _session = session;
        _back.Clear();
        _forward.Clear();
        OnPropertyChanged(nameof(HasSession));

        // Transfers land asynchronously, so the folder on screen has to be refreshed when they finish
        // rather than when they are queued.
        session.Transfers.JobChanged += OnTransferChanged;

        await LoadFavoritesAsync().ConfigureAwait(true);
        await NavigateToAsync(KnownPaths.InternalStorage).ConfigureAwait(true);
    }

    /// <summary>
    /// Refreshes the folder on screen when a transfer writes into it (spec §29, §52).
    /// </summary>
    /// <remarks>
    /// Queuing a transfer returns immediately — the bytes arrive later — so refreshing at enqueue time shows
    /// the folder as it was before the upload. Refreshes are coalesced because a bulk upload completes many
    /// jobs in quick succession, and re-listing the device once per file would be both slow and pointless.
    /// </remarks>
    private void OnTransferChanged(object? sender, TransferJobChangedEventArgs e)
    {
        if (e.Job.Status != TransferStatus.Completed
            || e.Job.Direction != TransferDirection.Upload)
        {
            return;
        }

        if (!AffectsCurrentListing(e.Job.RemotePath))
        {
            return;
        }

        _dispatcher.Post(() => _ = ScheduleRefreshAsync());
    }

    /// <summary>
    /// True when a transfer to <paramref name="written"/> would change what the current folder shows.
    /// </summary>
    /// <remarks>
    /// Being precise here matters. Refreshing on any descendant would re-list a large folder because a file
    /// landed five levels down inside it, which changes nothing visible. But refreshing only on direct
    /// children would miss the case that matters most — uploading a folder writes
    /// <c>current/newfolder/file</c>, whose parent is <c>newfolder</c>, yet <c>newfolder</c> itself is new and
    /// must appear. So: refresh when something lands directly here, or when the branch it landed in is not
    /// already on screen.
    /// </remarks>
    private bool AffectsCurrentListing(DevicePath written)
    {
        if (written.Parent == CurrentPath)
        {
            return true;
        }

        if (!CurrentPath.IsAncestorOf(written))
        {
            return false;
        }

        // The segment of the written path that sits directly under the folder being viewed.
        var relative = written.Value[(CurrentPath.IsRoot ? 1 : CurrentPath.Value.Length + 1)..];
        var branch = relative.Split(DevicePath.Separator)[0];

        return Entries.All(row => row.Name != branch);
    }

    private async Task ScheduleRefreshAsync()
    {
        if (_refreshPending)
        {
            return;
        }

        _refreshPending = true;

        // A short settle window: one refresh after a burst of completions, not one per file.
        await Task.Delay(400).ConfigureAwait(true);

        _refreshPending = false;

        if (_session is null)
        {
            return;
        }

        await _cache.InvalidateAsync(_session.DeviceId, CurrentPath, CancellationToken.None)
            .ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Loads this device's pinned folders, seeding sensible defaults the first time it is seen (spec §66).
    /// </summary>
    /// <remarks>
    /// Defaults are only offered for folders the device actually has: suggesting WhatsApp on a phone without
    /// it would be noise, and §26 warns against assuming any particular layout.
    /// </remarks>
    private async Task LoadFavoritesAsync()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var profile = await _settings.GetProfileAsync(_session.DeviceId, CancellationToken.None)
                .ConfigureAwait(true);

            var pinned = profile.Favorites.ToList();

            if (pinned.Count == 0)
            {
                foreach (var candidate in new[]
                         {
                             KnownPaths.Camera, KnownPaths.Download, KnownPaths.Pictures, KnownPaths.Movies,
                         })
                {
                    if (await _session.FileSystem.ExistsAsync(candidate, CancellationToken.None)
                            .ConfigureAwait(true))
                    {
                        await _favorites.AddFavoriteAsync(_session.DeviceId, candidate,
                            CancellationToken.None).ConfigureAwait(true);
                        pinned.Add(candidate);
                    }
                }
            }

            Favorites.Clear();
            foreach (var path in pinned)
            {
                Favorites.Add(new FavoriteViewModel(path));
            }

            NotifyFavoritesChanged();
        }
        catch (DeviceException ex)
        {
            _logger.LogDebug("Could not load favourites: {Reason}", ex.UserMessage);
        }
    }

    private void NotifyFavoritesChanged()
    {
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(IsCurrentFolderPinned));
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (_session is null)
        {
            return;
        }

        var existing = Favorites.FirstOrDefault(favorite => favorite.Path == CurrentPath);

        if (existing is not null)
        {
            await _favorites.RemoveFavoriteAsync(_session.DeviceId, CurrentPath, CancellationToken.None)
                .ConfigureAwait(true);
            Favorites.Remove(existing);
        }
        else
        {
            await _favorites.AddFavoriteAsync(_session.DeviceId, CurrentPath, CancellationToken.None)
                .ConfigureAwait(true);
            Favorites.Add(new FavoriteViewModel(CurrentPath));
        }

        NotifyFavoritesChanged();
    }

    [RelayCommand]
    private async Task GoToFavoriteAsync(FavoriteViewModel? favorite)
    {
        if (favorite is not null)
        {
            await NavigateToAsync(favorite.Path).ConfigureAwait(true);
        }
    }

    public void Detach()
    {
        if (_session is not null)
        {
            // Leaving this subscribed would refresh against a device that is gone.
            _session.Transfers.JobChanged -= OnTransferChanged;
        }

        _session = null;
        _load?.Cancel();
        Entries.Clear();
        Breadcrumbs.Clear();
        Favorites.Clear();
        StatusText = string.Empty;
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(HasFavorites));
    }

    public async Task NavigateToAsync(DevicePath path, bool recordHistory = true)
    {
        if (_session is null)
        {
            return;
        }

        if (recordHistory && path != CurrentPath)
        {
            _back.Add(CurrentPath);
            _forward.Clear();
        }

        CurrentPath = path;
        PathText = path.Value;
        ErrorMessage = null;
        UpdateBreadcrumbs();
        NotifyNavigationState();
        OnPropertyChanged(nameof(IsCurrentFolderPinned));

        await LoadAsync().ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        if (_session is null)
        {
            return;
        }

        // A new navigation supersedes any load still in flight.
        _load?.Cancel();
        _load?.Dispose();
        _load = new CancellationTokenSource();
        var cancellationToken = _load.Token;

        var path = CurrentPath;
        IsLoading = true;
        IsFromCache = false;

        try
        {
            // 1. Cached listing, rendered immediately (spec §29).
            var cached = await _cache.GetListingAsync(_session.DeviceId, path, cancellationToken)
                .ConfigureAwait(true);

            if (cached is not null && !cancellationToken.IsCancellationRequested)
            {
                Apply(cached);
                IsFromCache = true;
            }

            // 2. Fresh listing from the device, replacing it.
            var fresh = await _session.FileSystem.ListAsync(path, cancellationToken).ConfigureAwait(true);

            if (!cancellationToken.IsCancellationRequested)
            {
                Apply(fresh);
                IsFromCache = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer navigation; the newer load owns the UI now.
        }
        catch (DeviceException ex)
        {
            ErrorMessage = ex.UserMessage;
            _logger.LogWarning("Could not list a folder: {Reason}", ex.UserMessage);

            if (!IsFromCache)
            {
                Entries.Clear();
                StatusText = string.Empty;
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private void Apply(IReadOnlyList<DeviceEntry> entries)
    {
        _lastListing = entries.ToList();
        Rebuild();
    }

    /// <summary>Applies the hidden-file filter and the sort, then rebuilds the visible rows.</summary>
    private void Rebuild()
    {
        var visible = _lastListing.Where(entry => ShowHiddenFiles || !entry.IsHidden);

        // Directories first is the Explorer convention users expect, regardless of sort column.
        var ordered = SortColumn switch
        {
            ExplorerSortColumn.Size => Order(visible, entry => entry.Size),
            ExplorerSortColumn.Modified => Order(visible, entry => entry.Modified),
            ExplorerSortColumn.Type => Order(visible, entry => entry.Extension),
            _ => Order(visible, entry => entry.Name),
        };

        Entries.Clear();
        foreach (var entry in ordered)
        {
            Entries.Add(new EntryRowViewModel(entry));
        }

        var files = _lastListing.Count(entry => !entry.IsDirectory);
        var folders = _lastListing.Count - files;
        var bytes = _lastListing.Where(entry => entry.IsSizeKnown).Sum(entry => entry.Size);

        StatusText = $"{folders} folder{(folders == 1 ? "" : "s")}, "
                     + $"{files} file{(files == 1 ? "" : "s")} · {FormatSize.Bytes(bytes)}";
    }

    private IEnumerable<DeviceEntry> Order<TKey>(
        IEnumerable<DeviceEntry> source,
        Func<DeviceEntry, TKey> key)
    {
        var byFolderFirst = source.OrderByDescending(entry => entry.IsDirectory);

        return SortAscending
            ? byFolderFirst.ThenBy(key).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            : byFolderFirst.ThenByDescending(key).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();

        var segments = CurrentPath.Segments;
        var accumulated = DevicePath.Root;

        for (var i = 0; i < segments.Length; i++)
        {
            accumulated = accumulated.Combine(segments[i]);

            // /sdcard is presented as "Internal Storage" (spec §16).
            var label = accumulated == KnownPaths.InternalStorage ? "Internal Storage" : segments[i];
            Breadcrumbs.Add(new BreadcrumbViewModel(label, accumulated, isLast: i == segments.Length - 1));
        }
    }

    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
    }

    partial void OnShowHiddenFilesChanged(bool value) => Rebuild();

    partial void OnSortColumnChanged(ExplorerSortColumn value) => Rebuild();

    partial void OnSortAscendingChanged(bool value) => Rebuild();

    // --- navigation commands (spec §7, §87) ---

    [RelayCommand]
    private async Task OpenAsync(EntryRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.IsDirectory)
        {
            await NavigateToAsync(row.Path).ConfigureAwait(true);
        }

        // Opening a file means downloading or previewing it, which arrives with the transfer manager
        // in phase 3 and the viewers in phase 4.
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (CanGoUp)
        {
            await NavigateToAsync(CurrentPath.Parent).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (_back.Count == 0)
        {
            return;
        }

        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(CurrentPath);

        await NavigateToAsync(target, recordHistory: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoForwardAsync()
    {
        if (_forward.Count == 0)
        {
            return;
        }

        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(CurrentPath);

        await NavigateToAsync(target, recordHistory: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoToPathAsync()
    {
        if (!DevicePath.TryParse(PathText, out var path))
        {
            ErrorMessage = "That is not a valid Android path. Paths start with '/', for example /sdcard/DCIM.";
            return;
        }

        await NavigateToAsync(path).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_session is not null)
        {
            await _cache.InvalidateAsync(_session.DeviceId, CurrentPath, CancellationToken.None)
                .ConfigureAwait(true);
        }

        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task NavigateToBreadcrumbAsync(BreadcrumbViewModel? crumb)
    {
        if (crumb is not null)
        {
            await NavigateToAsync(crumb.Path).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void SortBy(string column)
    {
        var parsed = Enum.TryParse<ExplorerSortColumn>(column, out var value) ? value : ExplorerSortColumn.Name;

        if (SortColumn == parsed)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = parsed;
            SortAscending = true;
        }
    }

    // --- file operations (spec §9, §32) ---

    [RelayCommand]
    private void NewFolder()
    {
        OpenPrompt("New folder name", "New folder", async name =>
        {
            if (_session is null)
            {
                return;
            }

            await _session.FileSystem
                .CreateDirectoryAsync(CurrentPath.Combine(name), CancellationToken.None)
                .ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        });
    }

    [RelayCommand]
    private void Rename(EntryRowViewModel? row)
    {
        var target = row ?? SelectedEntry;
        if (target is null)
        {
            return;
        }

        OpenPrompt($"Rename \"{target.Name}\"", target.Name, async name =>
        {
            if (_session is null)
            {
                return;
            }

            await _session.FileSystem
                .RenameAsync(target.Path, CurrentPath.Combine(name), CancellationToken.None)
                .ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        });
    }

    /// <summary>
    /// Deletes the selection, or one row when invoked from its context menu (spec §9, §51).
    /// </summary>
    /// <remarks>
    /// Deleting a folder is recursive and permanent — Android has no recycle bin and this app does not fake
    /// one — so the confirmation says how many folders are involved rather than just counting items.
    /// </remarks>
    [RelayCommand]
    private void Delete(EntryRowViewModel? row)
    {
        // A context-menu click on an unselected row targets that row; otherwise the whole selection.
        var targets = row is not null && !_selection.Contains(row)
            ? [row]
            : _selection.Count > 0
                ? _selection.ToList()
                : row is not null
                    ? new List<EntryRowViewModel> { row }
                    : [];

        if (_session is null || targets.Count == 0)
        {
            return;
        }

        var folders = targets.Count(entry => entry.IsDirectory);

        ConfirmMessage = targets.Count == 1
            ? targets[0].IsDirectory
                ? $"Delete the folder \"{targets[0].Name}\" and everything inside it? This cannot be undone."
                : $"Delete \"{targets[0].Name}\"? This cannot be undone."
            : folders > 0
                ? $"Delete {targets.Count} items, including {folders} folder"
                  + $"{(folders == 1 ? "" : "s")} and everything inside? This cannot be undone."
                : $"Delete {targets.Count} files? This cannot be undone.";

        _confirmAction = async () =>
        {
            var deleted = 0;
            var failures = new List<string>();

            foreach (var target in targets)
            {
                try
                {
                    await _session.FileSystem
                        .DeleteAsync(target.Path, target.IsDirectory, CancellationToken.None)
                        .ConfigureAwait(true);
                    deleted++;
                }
                catch (DeviceException ex)
                {
                    // A protected entry must not abandon the rest of the batch (spec §78).
                    failures.Add(ex.UserMessage);
                }
            }

            if (failures.Count > 0)
            {
                ErrorMessage = $"Deleted {deleted} of {targets.Count}. {failures[0]}";
            }

            await LoadAsync().ConfigureAwait(true);
        };

        IsConfirmOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        IsConfirmOpen = false;
        var action = _confirmAction;
        _confirmAction = null;

        if (action is null)
        {
            return;
        }

        try
        {
            await action().ConfigureAwait(true);
        }
        catch (DeviceException ex)
        {
            ErrorMessage = ex.UserMessage;
        }
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirmOpen = false;
        _confirmAction = null;
    }

    [RelayCommand]
    private async Task SubmitPromptAsync()
    {
        var name = PromptValue.Trim();

        if (!DevicePath.IsValidFileName(name))
        {
            PromptError = name.Length == 0
                ? "Enter a name."
                : "That name cannot be used on Android. Names cannot contain '/' and must be at most 255 bytes.";
            return;
        }

        var action = _promptAction;
        IsPromptOpen = false;
        _promptAction = null;
        PromptError = null;

        if (action is null)
        {
            return;
        }

        try
        {
            await action(name).ConfigureAwait(true);
        }
        catch (DeviceException ex)
        {
            ErrorMessage = ex.UserMessage;
        }
    }

    [RelayCommand]
    private void CancelPrompt()
    {
        IsPromptOpen = false;
        _promptAction = null;
        PromptError = null;
    }

    // --- multi-selection (spec §31) ---

    /// <summary>
    /// Replaces the tracked selection from the list control.
    /// </summary>
    /// <remarks>
    /// Driven from the view's SelectionChanged rather than a two-way binding to <c>SelectedItems</c>, which is
    /// awkward to bind reliably; the list owns selection and this mirrors it.
    /// </remarks>
    public void UpdateSelection(IEnumerable<EntryRowViewModel> selected)
    {
        _selection.Clear();
        _selection.AddRange(selected);

        OnPropertyChanged(nameof(SelectedEntries));
        OnPropertyChanged(nameof(HasMultiSelection));
        OnPropertyChanged(nameof(HasAnySelection));

        var folders = _selection.Count(row => row.IsDirectory);
        var files = _selection.Count - folders;

        // Folder sizes are unknown until they are walked, so the summary counts them rather than guessing.
        var bytes = _selection.Where(row => !row.IsDirectory && row.Entry.IsSizeKnown)
            .Sum(row => row.Entry.Size);

        SelectionSummary = _selection.Count switch
        {
            0 => string.Empty,
            _ when folders == 0 => $"{files} file{(files == 1 ? "" : "s")} · {FormatSize.Bytes(bytes)}",
            _ when files == 0 => $"{folders} folder{(folders == 1 ? "" : "s")} selected",
            _ => $"{files} file{(files == 1 ? "" : "s")} and {folders} folder{(folders == 1 ? "" : "s")} · "
                 + $"{FormatSize.Bytes(bytes)} plus folder contents",
        };
    }

    // --- transfers (spec §9, §31, §34) ---

    /// <summary>
    /// Downloads everything selected, preserving the folder structure (spec §31, §34).
    /// </summary>
    /// <remarks>
    /// Selected folders are walked recursively and recreated under the destination, so exporting
    /// <c>DCIM/Trip</c> produces <c>Trip/Day1/photo.jpg</c> locally rather than a flattened heap. The whole
    /// selection becomes one batch on the transfer queue, so it is scheduled and resumable like any transfer.
    /// </remarks>
    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        if (_session is null || _selection.Count == 0)
        {
            return;
        }

        var sources = _selection.Select(row => row.Path).ToList();
        var destination = Path.Combine(_shell.GetDefaultDownloadFolder(), "Handspan");

        try
        {
            // Enumerate first: a folder's true file count and size are only known after walking it (§34).
            var plan = await _session.Transfers
                .PlanDownloadAsync(sources, destination, null, CancellationToken.None)
                .ConfigureAwait(true);

            ShowPlan(
                sources.Count == 1
                    ? $"Download \"{_selection[0].Name}\"?"
                    : $"Download {sources.Count} selected items?",
                plan,
                destination,
                async () => await _session.Transfers.EnqueueDownloadAsync(
                        sources, destination, ConflictPolicy.Rename, CancellationToken.None)
                    .ConfigureAwait(true));
        }
        catch (DeviceException ex)
        {
            ErrorMessage = ex.UserMessage;
        }
    }

    /// <summary>Downloads the selection to the default download folder.</summary>
    [RelayCommand]
    private async Task DownloadAsync(EntryRowViewModel? row)
    {
        var target = row ?? SelectedEntry;
        if (_session is null || target is null)
        {
            return;
        }

        var destination = Path.Combine(_shell.GetDefaultDownloadFolder(), "Handspan");

        try
        {
            // Enumerate first so the user sees what they are about to move (spec §34).
            var plan = await _session.Transfers
                .PlanDownloadAsync([target.Path], destination, null, CancellationToken.None)
                .ConfigureAwait(true);

            ShowPlan(
                target.IsDirectory ? $"Download \"{target.Name}\"?" : $"Download \"{target.Name}\"?",
                plan,
                destination,
                async () => await _session.Transfers.EnqueueDownloadAsync(
                        [target.Path], destination, ConflictPolicy.Rename, CancellationToken.None)
                    .ConfigureAwait(true));
        }
        catch (DeviceException ex)
        {
            ErrorMessage = ex.UserMessage;
        }
    }

    /// <summary>Uploads dropped files and folders into the current directory (spec §31).</summary>
    public async Task UploadAsync(IReadOnlyList<string> localPaths)
    {
        if (_session is null || localPaths.Count == 0)
        {
            return;
        }

        try
        {
            var plan = await _session.Transfers
                .PlanUploadAsync(localPaths, CurrentPath, null, CancellationToken.None)
                .ConfigureAwait(true);

            ShowPlan(
                $"Copy to {CurrentPath.Name}?",
                plan,
                CurrentPath.Value,
                async () =>
                {
                    await _session.Transfers.EnqueueUploadAsync(
                            localPaths, CurrentPath, ConflictPolicy.Rename, CancellationToken.None)
                        .ConfigureAwait(true);

                    await LoadAsync().ConfigureAwait(true);
                });
        }
        catch (DeviceException ex)
        {
            ErrorMessage = ex.UserMessage;
        }
    }

    private void ShowPlan(string title, TransferPlan plan, string destination, Func<Task> action)
    {
        if (plan.FileCount == 0)
        {
            ErrorMessage = "There was nothing to transfer.";
            return;
        }

        PlanTitle = title;
        PlanDetails = $"{plan.FileCount} file{(plan.FileCount == 1 ? "" : "s")} · "
                      + $"{FormatSize.Bytes(plan.TotalBytes)}\nTo: {destination}";

        // Conflicts are resolved by keeping both copies; the user is told rather than surprised (§35).
        PlanConflicts = plan.Conflicts.Count > 0
            ? $"{plan.Conflicts.Count} file{(plan.Conflicts.Count == 1 ? "" : "s")} already exist "
              + "and will be kept alongside the new copies."
            : string.Empty;

        _planAction = action;
        IsPlanOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmPlanAsync()
    {
        IsPlanOpen = false;
        var action = _planAction;
        _planAction = null;

        if (action is null)
        {
            return;
        }

        try
        {
            await action().ConfigureAwait(true);
        }
        catch (DeviceException ex)
        {
            ErrorMessage = ex.UserMessage;
        }
    }

    [RelayCommand]
    private void CancelPlan()
    {
        IsPlanOpen = false;
        _planAction = null;
    }

    // --- properties (spec §33) ---

    /// <summary>
    /// Shows details for one entry, reading only the header bytes needed (spec §33).
    /// </summary>
    /// <remarks>
    /// GPS is deliberately two-stage: whether a photo carries a location is shown immediately, because that is
    /// worth knowing before sharing it, but the coordinates are only fetched if asked for and never logged
    /// (spec §43).
    /// </remarks>
    [RelayCommand]
    private async Task ShowPropertiesAsync(EntryRowViewModel? row)
    {
        var target = row ?? SelectedEntry;
        if (_session is null || target is null)
        {
            return;
        }

        Properties = new PropertiesViewModel(target, _session);
        IsPropertiesOpen = true;

        await Properties.LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CloseProperties()
    {
        IsPropertiesOpen = false;
        Properties = null;
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    private void OpenPrompt(string title, string initialValue, Func<string, Task> action)
    {
        PromptTitle = title;
        PromptValue = initialValue;
        PromptError = null;
        _promptAction = action;
        IsPromptOpen = true;
    }
}

/// <summary>One pinned folder in Quick Access (spec §66).</summary>
public sealed class FavoriteViewModel(DevicePath path)
{
    public DevicePath Path { get; } = path;

    /// <summary>The folder's own name, except at a storage root where that would read as "sdcard".</summary>
    public string Label { get; } = path == KnownPaths.InternalStorage ? "Internal Storage" : path.Name;

    public string Tooltip { get; } = path.Value;
}

/// <summary>One clickable breadcrumb segment (spec §30).</summary>
public sealed class BreadcrumbViewModel(string label, DevicePath path, bool isLast)
{
    public string Label { get; } = label;

    public DevicePath Path { get; } = path;

    public bool IsLast { get; } = isLast;
}

/// <summary>One row in the entry list.</summary>
public sealed class EntryRowViewModel(DeviceEntry entry)
{
    public DeviceEntry Entry { get; } = entry;

    public DevicePath Path => Entry.Path;

    public string Name => Entry.Name;

    public bool IsDirectory => Entry.IsDirectory;

    public string Icon => Entry.Kind switch
    {
        DeviceEntryKind.Directory => "📁",
        _ => MediaTypes.FromPath(Entry.Path) switch
        {
            MediaKind.Image => "🖼",
            MediaKind.Video => "🎬",
            MediaKind.Audio => "🎵",
            MediaKind.Document => "📄",
            _ => "•",
        },
    };

    public string TypeText => Entry.IsDirectory
        ? "Folder"
        : Entry.Extension.Length > 1
            ? Entry.Extension[1..].ToUpperInvariant() + " file"
            : "File";

    public string SizeText => Entry.IsDirectory
        ? string.Empty
        : Entry.IsSizeKnown
            ? FormatSize.Bytes(Entry.Size)
            : "unknown";

    /// <summary>Local time, converted from the protocol's UTC seconds (spec §25).</summary>
    public string ModifiedText => Entry.Modified.ToLocalTime().ToString("g");
}

/// <summary>Byte formatting shared by the views.</summary>
public static class FormatSize
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
