using AndroidExplorer.Core.Models;

namespace AndroidExplorer.Core.Interfaces;

/// <summary>
/// Discovers devices and owns their sessions (spec §69).
/// </summary>
public interface IDeviceManager : IAsyncDisposable
{
    /// <summary>Currently known devices, in any state.</summary>
    IReadOnlyList<DeviceInfo> Devices { get; }

    /// <summary>
    /// Raised when a device appears, disappears or changes state. Driven by the ADB server's
    /// device-tracking stream, so detection needs no polling and works identically on Windows and
    /// macOS (spec §38, §45).
    /// </summary>
    event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

    /// <summary>Starts device tracking. Safe to call once per application lifetime.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Forces a re-enumeration, for the diagnostics page (spec §49).</summary>
    Task RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Opens (or returns the existing) session for a device.</summary>
    Task<IDeviceSession> ConnectAsync(DeviceId deviceId, CancellationToken cancellationToken);

    /// <summary>Closes a session and releases its resources.</summary>
    Task DisconnectAsync(DeviceId deviceId, CancellationToken cancellationToken);

    /// <summary>Restarts the ADB server, for the diagnostics page (spec §49).</summary>
    Task RestartAdbAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Pairs and connects a device over Wi-Fi (spec §40).
    /// </summary>
    /// <remarks>
    /// Optional and never the default: USB is faster and more predictable for large transfers, and the UI
    /// says so.
    /// </remarks>
    Task PairWirelessAsync(string host, int port, string code, CancellationToken cancellationToken);

    Task ConnectWirelessAsync(string host, int port, CancellationToken cancellationToken);

    /// <summary>
    /// Pauses all transfers on every device, e.g. before the machine sleeps (spec §81).
    /// </summary>
    Task PauseAllTransfersAsync(string reason);

    /// <summary>Resumes transfers paused by an interruption.</summary>
    Task ResumeAllTransfersAsync();
}

/// <summary>What changed about a device.</summary>
public sealed class DeviceChangedEventArgs(DeviceInfo device, DeviceChangeKind kind) : EventArgs
{
    public DeviceInfo Device { get; } = device;

    public DeviceChangeKind Kind { get; } = kind;
}

public enum DeviceChangeKind
{
    Added,
    Removed,
    StateChanged,
    InfoUpdated,
}

/// <summary>
/// Everything scoped to one connected device (spec §70).
/// </summary>
/// <remarks>
/// Sessions exist so that per-device state never becomes global state, and so that two connected
/// phones cannot share caches or queues (spec §39).
/// </remarks>
public interface IDeviceSession : IAsyncDisposable
{
    DeviceId DeviceId { get; }

    DeviceInfo Info { get; }

    DeviceCapabilities Capabilities { get; }

    IDeviceFileSystem FileSystem { get; }

    ITransferManager Transfers { get; }

    IThumbnailService Thumbnails { get; }

    IGalleryService Gallery { get; }

    ISearchService Search { get; }

    IStorageAnalyzer Storage { get; }

    IDuplicateFinder Duplicates { get; }

    IMetadataService Metadata { get; }
}
