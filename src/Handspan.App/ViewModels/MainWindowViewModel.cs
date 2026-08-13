using System.Collections.ObjectModel;
using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Core.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Handspan.App.ViewModels;

/// <summary>
/// The application shell: navigation between pages, and the bridge from device events to the pages
/// that care about them (spec §82, §83).
/// </summary>
/// <remarks>
/// Device changes arrive on the ADB tracking thread and are marshalled onto the UI thread here. When a
/// device becomes usable, a session is opened and handed to the Explorer; when it stops being usable,
/// the Explorer is detached so it can never show a folder from a phone that is gone (spec §38).
/// </remarks>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IDeviceManager _devices;
    private readonly ISettingsService _settings;
    private readonly IPowerEvents _power;
    private readonly ILogger<MainWindowViewModel> _logger;

    /// <summary>Live sessions, one per connected device, whether or not it is on screen (spec §39, §70).</summary>
    private readonly Dictionary<DeviceId, IDeviceSession> _sessions = [];

    [ObservableProperty]
    private string _connectionSummary = "Starting device detection…";

    [ObservableProperty]
    private NavigationItem _selectedPage;

    /// <summary>The device the device-scoped pages are currently pointed at.</summary>
    [ObservableProperty]
    private DeviceId? _activeDeviceId;

    public MainWindowViewModel(
        IDeviceManager devices,
        HomeViewModel home,
        ExplorerViewModel explorer,
        TransfersViewModel transfers,
        GalleryViewModel gallery,
        SearchViewModel search,
        StorageViewModel storage,
        SettingsViewModel settings,
        BackupViewModel backup,
        ISettingsService settingsService,
        IPowerEvents power,
        ILogger<MainWindowViewModel> logger)
    {
        _devices = devices;
        _settings = settingsService;
        _power = power;
        _logger = logger;

        Settings = settings;
        Backup = backup;

        Home = home;
        Explorer = explorer;
        Transfers = transfers;
        Gallery = gallery;
        Search = search;
        Storage = storage;

        Pages =
        [
            new NavigationItem("Home", isEnabled: true),
            new NavigationItem("Explorer", isEnabled: false),
            new NavigationItem("Gallery", isEnabled: false),
            new NavigationItem("Search", isEnabled: false),
            new NavigationItem("Storage", isEnabled: false),
            new NavigationItem("Backup", isEnabled: false),
            new NavigationItem("Transfers", isEnabled: true),
            new NavigationItem("Settings", isEnabled: true),
        ];

        _selectedPage = Pages[0];

        _devices.DeviceChanged += OnDeviceChanged;

        _ = InitializeAsync();
    }

    public HomeViewModel Home { get; }

    public ExplorerViewModel Explorer { get; }

    public TransfersViewModel Transfers { get; }

    public GalleryViewModel Gallery { get; }

    public SearchViewModel Search { get; }

    public StorageViewModel Storage { get; }

    public SettingsViewModel Settings { get; }

    public BackupViewModel Backup { get; }

    public ObservableCollection<NavigationItem> Pages { get; }

    public ObservableCollection<DeviceTabViewModel> ConnectedSessions { get; } = [];

    /// <summary>The switcher only earns its space when there is something to switch between.</summary>
    public bool HasMultipleDevices => ConnectedSessions.Count > 1;

    public bool IsHomeVisible => SelectedPage.Title == "Home";

    public bool IsExplorerVisible => SelectedPage.Title == "Explorer";

    public bool IsTransfersVisible => SelectedPage.Title == "Transfers";

    public bool IsGalleryVisible => SelectedPage.Title == "Gallery";

    public bool IsSearchVisible => SelectedPage.Title == "Search";

    public bool IsStorageVisible => SelectedPage.Title == "Storage";

    public bool IsSettingsVisible => SelectedPage.Title == "Settings";

    public bool IsBackupVisible => SelectedPage.Title == "Backup";

    private async Task InitializeAsync()
    {
        await _settings.LoadAsync(CancellationToken.None).ConfigureAwait(true);

        // Spec §81: a USB connection does not survive sleep, so pause before it happens and keep the
        // partial files. Resuming is left to the user's reconnect rather than assumed on wake.
        _power.Suspending += (_, _) => _ = _devices.PauseAllTransfersAsync(
            "Paused because this computer went to sleep.");
        _power.Resumed += (_, _) => _ = OnResumedAsync();
        _power.Start();

        await Home.DetectAdbAsync().ConfigureAwait(true);

        if (!Home.IsAdbFound)
        {
            ConnectionSummary = "adb was not found. Download it on the Home page to detect devices.";
            return;
        }

        await StartTrackingAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task StartTrackingAsync()
    {
        try
        {
            await _devices.StartAsync(CancellationToken.None).ConfigureAwait(true);
            await _devices.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            UpdateSummary();
        }
        catch (DeviceException ex)
        {
            ConnectionSummary = ex.UserMessage;
            _logger.LogWarning("Could not start device detection: {Reason}", ex.UserMessage);
        }
    }

    /// <summary>
    /// On wake, re-check which devices are actually present before resuming anything.
    /// </summary>
    /// <remarks>
    /// A phone unplugged during sleep must not have its transfers resumed against a device that is gone; the
    /// refresh settles that first, and only still-connected devices continue.
    /// </remarks>
    private async Task OnResumedAsync()
    {
        try
        {
            await _devices.RefreshAsync(CancellationToken.None).ConfigureAwait(true);

            if (_devices.Devices.Any(device => device.IsUsable))
            {
                await _devices.ResumeAllTransfersAsync().ConfigureAwait(true);
            }
        }
        catch (DeviceException ex)
        {
            ConnectionSummary = ex.UserMessage;
        }
    }

    [RelayCommand]
    private void Navigate(NavigationItem? page)
    {
        if (page is { IsEnabled: true })
        {
            SelectedPage = page;
        }
    }

    partial void OnSelectedPageChanged(NavigationItem value)
    {
        OnPropertyChanged(nameof(IsHomeVisible));
        OnPropertyChanged(nameof(IsExplorerVisible));
        OnPropertyChanged(nameof(IsTransfersVisible));
        OnPropertyChanged(nameof(IsGalleryVisible));
        OnPropertyChanged(nameof(IsSearchVisible));
        OnPropertyChanged(nameof(IsStorageVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(IsBackupVisible));
    }

    private void OnDeviceChanged(object? sender, DeviceChangedEventArgs e)
        => Dispatcher.UIThread.Post(() => _ = ApplyChangeAsync(e));

    private async Task ApplyChangeAsync(DeviceChangedEventArgs e)
    {
        var existing = Home.ConnectedDevices.FirstOrDefault(row => row.Id == e.Device.Id);

        if (e.Kind == DeviceChangeKind.Removed)
        {
            if (existing is not null)
            {
                Home.ConnectedDevices.Remove(existing);
            }

            // The session teardown happens below, which also picks the next device to show.
        }
        else if (existing is null)
        {
            Home.ConnectedDevices.Add(new DeviceRowViewModel(e.Device));
        }
        else
        {
            existing.Update(e.Device);
        }

        Home.NotifyDevicesChanged();
        UpdateSummary();

        // Spec §43: counts and states only — never a serial or a path.
        _logger.LogInformation(
            "Device list changed ({Kind}); {Count} shown, states: {States}",
            e.Kind,
            Home.ConnectedDevices.Count,
            string.Join(", ", Home.ConnectedDevices.Select(row => row.StateText)));

        if (e.Kind == DeviceChangeKind.Removed || !e.Device.IsUsable)
        {
            await CloseSessionAsync(e.Device.Id).ConfigureAwait(true);
        }
        else
        {
            await OpenSessionAsync(e.Device).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Opens a session for a usable device and makes it the active one if nothing else is (spec §39).
    /// </summary>
    /// <remarks>
    /// Every connected device gets its own session — and therefore its own queue, caches and index — whether
    /// or not it is the one on screen. A transfer on a background device keeps running while the user browses
    /// another phone.
    /// </remarks>
    private async Task OpenSessionAsync(DeviceInfo device)
    {
        if (_sessions.ContainsKey(device.Id))
        {
            return;
        }

        try
        {
            var session = await _devices.ConnectAsync(device.Id, CancellationToken.None)
                .ConfigureAwait(true);

            _sessions[device.Id] = session;

            ConnectedSessions.Add(new DeviceTabViewModel(device));
            OnPropertyChanged(nameof(HasMultipleDevices));

            _logger.LogInformation("Opened a session; {Count} now connected.", _sessions.Count);

            if (ActiveDeviceId is null)
            {
                await ActivateAsync(device.Id).ConfigureAwait(true);
            }
        }
        catch (DeviceException ex)
        {
            ConnectionSummary = ex.UserMessage;
            _logger.LogWarning("Could not open a session: {Reason}", ex.UserMessage);
        }
    }

    private async Task CloseSessionAsync(DeviceId deviceId)
    {
        if (!_sessions.Remove(deviceId))
        {
            return;
        }

        var tab = ConnectedSessions.FirstOrDefault(candidate => candidate.Id == deviceId);
        if (tab is not null)
        {
            ConnectedSessions.Remove(tab);
        }

        OnPropertyChanged(nameof(HasMultipleDevices));

        if (ActiveDeviceId != deviceId)
        {
            // A background device going away must not disturb what the user is looking at.
            return;
        }

        DetachPages();

        // Fall through to whatever is still connected rather than dumping the user on Home.
        var next = _sessions.Keys.FirstOrDefault();
        if (next != default)
        {
            await ActivateAsync(next).ConfigureAwait(true);
        }
        else
        {
            ActiveDeviceId = null;
            SetDevicePagesEnabled(false);

            if (!IsHomeVisible && !IsTransfersVisible && !IsSettingsVisible)
            {
                SelectedPage = Pages[0];
            }
        }
    }

    /// <summary>Points every device-scoped page at one session (spec §70).</summary>
    [RelayCommand]
    private async Task ActivateAsync(DeviceId deviceId)
    {
        if (!_sessions.TryGetValue(deviceId, out var session))
        {
            return;
        }

        ActiveDeviceId = deviceId;

        foreach (var tab in ConnectedSessions)
        {
            tab.IsActive = tab.Id == deviceId;
        }

        Transfers.Attach(session.Transfers);
        await Explorer.AttachAsync(session).ConfigureAwait(true);

        // These scan in the background; do not block switching on them.
        _ = Gallery.AttachAsync(session);
        _ = Search.AttachAsync(session);
        _ = Storage.AttachAsync(session);

        _ = Backup.AttachAsync(session);

        SetDevicePagesEnabled(true);
        UpdateSummary();
    }

    [RelayCommand]
    private async Task SwitchDeviceAsync(DeviceTabViewModel? tab)
    {
        if (tab is not null && tab.Id != ActiveDeviceId)
        {
            await ActivateAsync(tab.Id).ConfigureAwait(true);
        }
    }

    private void DetachPages()
    {
        Explorer.Detach();
        Transfers.Detach();
        Gallery.Detach();
        Search.Detach();
        Storage.Detach();
        Backup.Detach();
    }

    private void SetDevicePagesEnabled(bool enabled)
    {
        foreach (var title in new[] { "Explorer", "Gallery", "Search", "Storage", "Backup" })
        {
            var page = Pages.First(candidate => candidate.Title == title);
            page.IsEnabled = enabled;
            page.Hint = enabled ? null : "connect a device";
        }
    }

    private void UpdateSummary()
    {
        var active = ConnectedSessions.FirstOrDefault(tab => tab.IsActive);

        ConnectionSummary = Home.ConnectedDevices.Count switch
        {
            0 => "No device detected. Connect your phone by USB and enable USB debugging.",
            1 => Home.ConnectedDevices[0].Guidance,
            var count when active is not null =>
                $"{count} devices connected · browsing {active.DisplayName}",
            var count => $"{count} devices connected.",
        };
    }
}

/// <summary>One connected device in the switcher (spec §39).</summary>
public sealed partial class DeviceTabViewModel(DeviceInfo device) : ViewModelBase
{
    [ObservableProperty]
    private bool _isActive;

    public DeviceId Id { get; } = device.Id;

    public string DisplayName { get; } = device.DisplayName;

    public string ConnectionGlyph { get; } =
        device.ConnectionType == ConnectionType.Wireless ? "wifi" : "usb";
}

/// <summary>An entry in the navigation rail (spec §83).</summary>
public sealed partial class NavigationItem(string title, bool isEnabled, string? hint = null)
    : ViewModelBase
{
    [ObservableProperty]
    private bool _isEnabled = isEnabled;

    [ObservableProperty]
    private string? _hint = hint;

    public string Title { get; } = title;
}

