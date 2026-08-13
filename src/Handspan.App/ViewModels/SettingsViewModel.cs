using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Handspan.App.ViewModels;

/// <summary>
/// Settings, wireless pairing and diagnostics (spec §40, §49, §50).
/// </summary>
/// <remarks>
/// Every value here is read by its consumer at the point of use, so a change applies to the next transfer or
/// thumbnail without restarting anything.
/// </remarks>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IDeviceManager _devices;
    private readonly IShellIntegration _shell;
    private bool _loading;

    [ObservableProperty]
    private string _status = string.Empty;

    // --- General (spec §50) ---

    [ObservableProperty]
    private string _downloadFolder = string.Empty;

    [ObservableProperty]
    private bool _confirmDeletes = true;

    [ObservableProperty]
    private bool _confirmLargeTransfers = true;

    // --- Explorer ---

    [ObservableProperty]
    private bool _showHiddenFiles;

    // --- Transfers ---

    [ObservableProperty]
    private int _maxSmallTransfers = 4;

    [ObservableProperty]
    private int _maxLargeTransfers = 2;

    [ObservableProperty]
    private int _retryCount = 3;

    [ObservableProperty]
    private bool _verifyWithHash;

    // --- Gallery ---

    [ObservableProperty]
    private int _thumbnailSize = 320;

    [ObservableProperty]
    private int _thumbnailCacheCapMegabytes = 2048;

    // --- Connection ---

    [ObservableProperty]
    private string _adbPath = string.Empty;

    [ObservableProperty]
    private bool _allowWirelessAdb;

    // --- Diagnostics ---

    [ObservableProperty]
    private bool _verboseDiagnostics;

    // --- Wireless pairing (spec §40) ---

    [ObservableProperty]
    private string _pairAddress = string.Empty;

    [ObservableProperty]
    private string _pairPort = string.Empty;

    [ObservableProperty]
    private string _pairCode = string.Empty;

    [ObservableProperty]
    private string _connectPort = "5555";

    [ObservableProperty]
    private string _wirelessStatus = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public SettingsViewModel(
        ISettingsService settings,
        IDeviceManager devices,
        IShellIntegration shell)
    {
        _settings = settings;
        _devices = devices;
        _shell = shell;

        Apply(settings.Current);
        settings.Changed += (_, updated) => Apply(updated);
    }

    /// <summary>Pushes stored settings into the bound properties without triggering a save loop.</summary>
    private void Apply(AppSettings settings)
    {
        _loading = true;
        try
        {
            DownloadFolder = settings.DefaultDownloadFolder ?? _shell.GetDefaultDownloadFolder();
            ConfirmDeletes = settings.ConfirmDeletes;
            ConfirmLargeTransfers = settings.ConfirmLargeTransfers;
            ShowHiddenFiles = settings.ShowHiddenFiles;
            MaxSmallTransfers = settings.MaxConcurrentSmallTransfers;
            MaxLargeTransfers = settings.MaxConcurrentLargeTransfers;
            RetryCount = settings.RetryCount;
            VerifyWithHash = settings.Verification == VerificationMode.Sha256;
            ThumbnailSize = settings.ThumbnailMaxEdgePixels;
            ThumbnailCacheCapMegabytes = (int)(settings.ThumbnailCacheCapBytes / (1024 * 1024));
            AdbPath = settings.AdbExecutablePath ?? string.Empty;
            AllowWirelessAdb = settings.AllowWirelessAdb;
            VerboseDiagnostics = settings.VerboseDiagnostics;
        }
        finally
        {
            _loading = false;
        }
    }

    private AppSettings Compose() => _settings.Current with
    {
        DefaultDownloadFolder = string.IsNullOrWhiteSpace(DownloadFolder) ? null : DownloadFolder,
        ConfirmDeletes = ConfirmDeletes,
        ConfirmLargeTransfers = ConfirmLargeTransfers,
        ShowHiddenFiles = ShowHiddenFiles,
        MaxConcurrentSmallTransfers = MaxSmallTransfers,
        MaxConcurrentLargeTransfers = MaxLargeTransfers,
        RetryCount = RetryCount,
        Verification = VerifyWithHash ? VerificationMode.Sha256 : VerificationMode.Size,
        ThumbnailMaxEdgePixels = ThumbnailSize,
        ThumbnailCacheCapBytes = (long)ThumbnailCacheCapMegabytes * 1024 * 1024,
        AdbExecutablePath = string.IsNullOrWhiteSpace(AdbPath) ? null : AdbPath,
        AllowWirelessAdb = AllowWirelessAdb,
        VerboseDiagnostics = VerboseDiagnostics,
    };

    [RelayCommand]
    private async Task SaveAsync()
    {
        await _settings.SaveAsync(Compose(), CancellationToken.None).ConfigureAwait(true);
        Status = $"Saved at {DateTime.Now:t}.";
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        await _settings.SaveAsync(
                new AppSettings { DefaultDownloadFolder = _shell.GetDefaultDownloadFolder() },
                CancellationToken.None)
            .ConfigureAwait(true);

        Status = "Settings reset to defaults.";
    }

    // --- Diagnostics (spec §49) ---

    [RelayCommand]
    private async Task RestartAdbAsync()
    {
        IsBusy = true;
        try
        {
            await _devices.RestartAdbAsync(CancellationToken.None).ConfigureAwait(true);
            Status = "The Android connection service was restarted.";
        }
        catch (DeviceException ex)
        {
            Status = ex.UserMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RescanDevicesAsync()
    {
        IsBusy = true;
        try
        {
            await _devices.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            Status = $"{_devices.Devices.Count} device(s) found.";
        }
        catch (DeviceException ex)
        {
            Status = ex.UserMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenLogsAsync()
        => _shell.RevealInFileManagerAsync(Path.Combine(_shell.GetAppDataFolder(), "logs"));

    // --- Wireless pairing (spec §40) ---

    [RelayCommand]
    private async Task PairAsync()
    {
        if (!int.TryParse(PairPort, out var port) || string.IsNullOrWhiteSpace(PairAddress))
        {
            WirelessStatus = "Enter the IP address and port shown under \"Pair device with pairing code\".";
            return;
        }

        IsBusy = true;
        try
        {
            await _devices.PairWirelessAsync(PairAddress.Trim(), port, PairCode.Trim(),
                CancellationToken.None).ConfigureAwait(true);

            WirelessStatus = "Paired. Now connect using the port from the wireless debugging screen "
                             + "(usually different from the pairing port).";
        }
        catch (DeviceException ex)
        {
            WirelessStatus = ex.UserMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConnectWirelessAsync()
    {
        if (!int.TryParse(ConnectPort, out var port) || string.IsNullOrWhiteSpace(PairAddress))
        {
            WirelessStatus = "Enter the device's IP address and its wireless debugging port.";
            return;
        }

        IsBusy = true;
        try
        {
            await _devices.ConnectWirelessAsync(PairAddress.Trim(), port, CancellationToken.None)
                .ConfigureAwait(true);

            WirelessStatus = "Connected. The device appears on the Home page.";
        }
        catch (DeviceException ex)
        {
            WirelessStatus = ex.UserMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Auto-save the toggles: a checkbox that needs a separate Save click is a papercut.
    partial void OnConfirmDeletesChanged(bool value) => AutoSave();

    partial void OnConfirmLargeTransfersChanged(bool value) => AutoSave();

    partial void OnShowHiddenFilesChanged(bool value) => AutoSave();

    partial void OnVerifyWithHashChanged(bool value) => AutoSave();

    partial void OnAllowWirelessAdbChanged(bool value) => AutoSave();

    partial void OnVerboseDiagnosticsChanged(bool value) => AutoSave();

    private void AutoSave()
    {
        if (!_loading)
        {
            _ = SaveAsync();
        }
    }
}
