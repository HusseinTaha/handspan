using System.Text.Json;
using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using AndroidExplorer.Core.Platform;
using AndroidExplorer.Data;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Services;

/// <summary>
/// Application and per-device settings (spec §50, §67).
/// </summary>
/// <remarks>
/// <para>
/// Settings live in a JSON file in the platform's app-data folder, written atomically via a temporary file so
/// a crash mid-save cannot leave an unreadable configuration. A corrupt or partially-written file falls back
/// to defaults rather than preventing the app from starting.
/// </para>
/// <para>
/// Consumers read <see cref="Current"/> at the point of use rather than capturing it, so a changed setting
/// takes effect on the next operation without restarting anything.
/// </para>
/// </remarks>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly IDeviceProfileStore _profiles;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SettingsService(
        IShellIntegration shell,
        IDeviceProfileStore profiles,
        ILogger<SettingsService> logger)
    {
        _profiles = profiles;
        _logger = logger;
        _path = Path.Combine(shell.GetAppDataFolder(), "settings.json");

        // Defaults until LoadAsync runs, so nothing has to await settings just to construct.
        Current = new AppSettings { DefaultDownloadFolder = shell.GetDefaultDownloadFolder() };
    }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            await using var stream = File.OpenRead(_path);
            var loaded = await JsonSerializer
                .DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (loaded is not null)
            {
                Current = Sanitize(loaded);
                Changed?.Invoke(this, Current);
                _logger.LogInformation("Settings loaded.");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Defaults are always a working configuration; a bad file must never block startup.
            _logger.LogWarning(ex, "Could not read settings; using defaults.");
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var sanitized = Sanitize(settings);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporary = _path + ".tmp";

            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer
                    .SerializeAsync(stream, sanitized, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, _path, overwrite: true);

            Current = sanitized;
            Changed?.Invoke(this, Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save settings.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<DeviceProfile> GetProfileAsync(DeviceId deviceId, CancellationToken cancellationToken)
        => _profiles.GetAsync(deviceId, cancellationToken);

    public Task SaveProfileAsync(DeviceProfile profile, CancellationToken cancellationToken)
        => _profiles.SaveAsync(profile, cancellationToken);

    /// <summary>
    /// Clamps values that would break the app if a hand-edited file contained nonsense.
    /// </summary>
    /// <remarks>
    /// The settings file is user-visible, so it will be hand-edited. Zero concurrent transfers would stall
    /// the queue forever and a zero thumbnail size would divide by zero — both are cheaper to clamp here
    /// than to defend against at every use.
    /// </remarks>
    private static AppSettings Sanitize(AppSettings settings) => settings with
    {
        MaxConcurrentSmallTransfers = Math.Clamp(settings.MaxConcurrentSmallTransfers, 1, 16),
        MaxConcurrentLargeTransfers = Math.Clamp(settings.MaxConcurrentLargeTransfers, 1, 8),
        LargeFileThresholdBytes = Math.Max(64 * 1024, settings.LargeFileThresholdBytes),
        RetryCount = Math.Clamp(settings.RetryCount, 0, 10),
        ThumbnailMaxEdgePixels = Math.Clamp(settings.ThumbnailMaxEdgePixels, 64, 1024),
        ThumbnailCacheCapBytes = Math.Max(64L * 1024 * 1024, settings.ThumbnailCacheCapBytes),
        FullDecodeThresholdBytes = Math.Max(0, settings.FullDecodeThresholdBytes),
        ConnectionTimeoutSeconds = Math.Clamp(settings.ConnectionTimeoutSeconds, 3, 120),
    };
}
