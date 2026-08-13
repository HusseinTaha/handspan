using Handspan.Core.Models;

namespace Handspan.Core.Platform;

/// <summary>
/// Locates the ADB binary (spec §4.1, §72).
/// </summary>
/// <remarks>
/// One implementation per OS. Beyond path differences, macOS needs the quarantine attribute cleared
/// and the exec bit set on a downloaded binary or it will not launch.
/// </remarks>
public interface IAdbBinaryProvider
{
    /// <summary>
    /// Finds adb: bundled copy, then PATH, then ANDROID_HOME/ANDROID_SDK_ROOT, then the usual SDK
    /// locations, then the user-configured path. Null when nothing was found.
    /// </summary>
    Task<string?> LocateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Downloads Google's platform-tools into app data. Only ever called after explicit user
    /// consent — the app must work offline afterwards (spec §44).
    /// </summary>
    Task<string> DownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Makes a freshly extracted binary runnable (chmod +x, clear quarantine on macOS).</summary>
    Task PrepareForExecutionAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// OS file-manager and shell integration.
/// </summary>
public interface IShellIntegration
{
    /// <summary>Reveals a local path in Explorer or Finder.</summary>
    Task RevealInFileManagerAsync(string localPath);

    /// <summary>Opens a local path with the OS default handler.</summary>
    Task OpenAsync(string localPath);

    /// <summary>Shows the OS "open with" chooser.</summary>
    Task OpenWithAsync(string localPath);

    /// <summary>The user's Downloads folder, or the platform equivalent.</summary>
    string GetDefaultDownloadFolder();

    /// <summary>Per-user application data directory for settings, caches and the job journal.</summary>
    string GetAppDataFolder();
}

/// <summary>
/// Dragging device files out to Explorer or Finder (spec §31).
/// </summary>
/// <remarks>
/// The hard direction, because the files do not exist locally when the drag starts. Phase 3 stages
/// the selection into the cache and hands over real paths; phase 6 replaces that with on-demand
/// streaming — Windows CFSTR_FILEDESCRIPTORW, macOS NSFilePromiseProvider — behind this same
/// interface.
/// </remarks>
public interface IShellDragService
{
    /// <summary>True when this platform can stream files on demand rather than pre-staging them.</summary>
    bool SupportsOnDemandDrag { get; }

    /// <summary>
    /// Prepares a drag payload for the given device files, reporting progress while staging.
    /// </summary>
    Task<IReadOnlyList<string>> PrepareDragPayloadAsync(
        DeviceId deviceId,
        IReadOnlyList<DeviceEntry> entries,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Native notifications (spec §86).</summary>
public interface IPlatformNotifications
{
    Task ShowAsync(string title, string message);
}

/// <summary>
/// Sleep and wake notifications, so transfers pause before the machine suspends (spec §81).
/// </summary>
public interface IPowerEvents
{
    event EventHandler? Suspending;

    event EventHandler? Resumed;

    void Start();
}
