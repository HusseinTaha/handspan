using System.Collections.ObjectModel;
using Handspan.App.Platform;
using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Handspan.App.ViewModels;

/// <summary>
/// Home: adb status, the device dashboard, and the connection walkthrough (spec §6, §84).
/// </summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly IDeviceManager _devices;
    private readonly IAdbBinaryProvider _adbBinaryProvider;
    private readonly IShellIntegration _shell;
    private readonly ILogger<HomeViewModel> _logger;

    [ObservableProperty]
    private string _adbStatus = "Looking for adb…";

    [ObservableProperty]
    private bool _isAdbFound;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isDownloadingAdb;

    [ObservableProperty]
    private double _downloadProgress;

    public HomeViewModel(
        IDeviceManager devices,
        IAdbBinaryProvider adbBinaryProvider,
        IShellIntegration shell,
        ILogger<HomeViewModel> logger)
    {
        _devices = devices;
        _adbBinaryProvider = adbBinaryProvider;
        _shell = shell;
        _logger = logger;

        Platform = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "macOS"
            : "unsupported";

        AppDataFolder = shell.GetAppDataFolder();
        DownloadFolder = shell.GetDefaultDownloadFolder();
    }

    public string Platform { get; }

    public string AppDataFolder { get; }

    public string DownloadFolder { get; }

    /// <summary>
    /// Explains the folder above when it is not the ordinary per-user one, or when it was meant to be
    /// portable and could not be. Null in the normal installed case, which needs no explanation.
    /// </summary>
    public string? DataLocationNote => PortableMode.IsEnabled
        ? "Portable: settings, cache and logs are kept beside the application, not on this PC."
        : PortableMode.FallbackReason is { } reason
            ? $"Portable mode is not active — {reason}."
            : null;

    public bool HasDataLocationNote => DataLocationNote is not null;

    public string RuntimeVersion => Environment.Version.ToString();

    public ObservableCollection<DeviceRowViewModel> ConnectedDevices { get; } = [];

    public bool HasDevices => ConnectedDevices.Count > 0;

    public void NotifyDevicesChanged() => OnPropertyChanged(nameof(HasDevices));

    [RelayCommand]
    public async Task DetectAdbAsync()
    {
        IsBusy = true;
        try
        {
            var path = await _adbBinaryProvider.LocateAsync(CancellationToken.None).ConfigureAwait(true);

            IsAdbFound = path is not null;
            AdbStatus = path ?? "Not found. Download it below, or set the path in Settings.";

            // Spec §43: record the outcome, never the path.
            _logger.LogInformation("adb discovery completed, found: {Found}", IsAdbFound);
        }
        catch (Exception ex)
        {
            IsAdbFound = false;
            AdbStatus = "Could not search for adb.";
            _logger.LogWarning(ex, "adb discovery failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Downloads Google's platform-tools, only ever on this explicit user action (spec §44).
    /// </summary>
    [RelayCommand]
    private async Task DownloadAdbAsync()
    {
        IsDownloadingAdb = true;
        DownloadProgress = 0;

        try
        {
            var progress = new Progress<double>(fraction => DownloadProgress = fraction * 100);
            var path = await _adbBinaryProvider.DownloadAsync(progress, CancellationToken.None)
                .ConfigureAwait(true);

            IsAdbFound = true;
            AdbStatus = path;
            _logger.LogInformation("Downloaded platform-tools.");
        }
        catch (Exception ex)
        {
            AdbStatus = "The download failed. Check your internet connection, or install platform-tools manually.";
            _logger.LogWarning(ex, "platform-tools download failed");
        }
        finally
        {
            IsDownloadingAdb = false;
        }
    }

    [RelayCommand]
    private async Task RestartAdbAsync()
    {
        IsBusy = true;
        try
        {
            await _devices.RestartAdbAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (DeviceException ex)
        {
            AdbStatus = ex.UserMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenAppDataFolderAsync() => _shell.RevealInFileManagerAsync(AppDataFolder);
}

/// <summary>One device, with the guidance appropriate to its state (spec §5, §6, §84).</summary>
public sealed partial class DeviceRowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _stateText;

    [ObservableProperty]
    private string _guidance;

    [ObservableProperty]
    private string _details;

    [ObservableProperty]
    private string _storageText;

    [ObservableProperty]
    private double _storageUsedFraction;

    [ObservableProperty]
    private bool _isUsable;

    [ObservableProperty]
    private bool _needsAuthorization;

    public DeviceRowViewModel(DeviceInfo device)
    {
        Id = device.Id;
        _displayName = device.DisplayName;
        _stateText = string.Empty;
        _guidance = string.Empty;
        _details = string.Empty;
        _storageText = string.Empty;
        Update(device);
    }

    public DeviceId Id { get; }

    public string Serial => Id.Serial;

    public void Update(DeviceInfo device)
    {
        DisplayName = device.DisplayName;
        IsUsable = device.IsUsable;
        NeedsAuthorization = device.State == DeviceState.Unauthorized;

        StateText = device.State switch
        {
            DeviceState.Online => "Connected",
            DeviceState.Unauthorized => "Not authorized",
            DeviceState.Offline => "Not responding",
            DeviceState.Disconnected => "Disconnected",
            _ => "Unavailable",
        };

        Guidance = device.State switch
        {
            DeviceState.Online => $"{device.DisplayName} is connected and ready to browse.",
            DeviceState.Unauthorized =>
                "Unlock the phone and tap \"Allow\" on the USB debugging prompt. Tick "
                + "\"Always allow from this computer\" so it is not asked again.",
            DeviceState.Offline =>
                "The device is connected but not responding. Try another cable or USB port, or "
                + "unplug and reconnect it.",
            _ => "This device cannot be browsed — it may be in bootloader or recovery mode.",
        };

        var parts = new List<string>();
        if (device.AndroidVersion is { } version)
        {
            parts.Add($"Android {version}");
        }

        if (device.ApiLevel is { } api)
        {
            parts.Add($"API {api}");
        }

        parts.Add(device.ConnectionType == ConnectionType.Wireless ? "Wi-Fi" : "USB");

        if (device.BatteryPercent is { } battery)
        {
            parts.Add($"battery {battery}%");
        }

        Details = string.Join(" · ", parts);

        if (device.Storage is { } storage)
        {
            StorageText = $"{FormatSize.Bytes(storage.UsedBytes)} used of "
                          + $"{FormatSize.Bytes(storage.TotalBytes)} · "
                          + $"{FormatSize.Bytes(storage.FreeBytes)} free";
            StorageUsedFraction = storage.UsedFraction * 100;
        }
        else
        {
            StorageText = string.Empty;
            StorageUsedFraction = 0;
        }
    }
}
