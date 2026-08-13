namespace AndroidExplorer.Core.Models;

/// <summary>Connection state reported by the ADB server (spec §5).</summary>
public enum DeviceState
{
    /// <summary>Not present.</summary>
    Disconnected,

    /// <summary>Connected and authorized — usable.</summary>
    Online,

    /// <summary>Connected, but the user has not accepted the ADB authorization prompt yet.</summary>
    Unauthorized,

    /// <summary>Known to the server but not responding.</summary>
    Offline,

    /// <summary>Still initializing (bootloader, recovery, sideload, or an unrecognized state).</summary>
    Unknown,
}

/// <summary>How a device is attached.</summary>
public enum ConnectionType
{
    Usb,
    Wireless,
}

/// <summary>Kind of directory entry.</summary>
public enum DeviceEntryKind
{
    File,
    Directory,

    /// <summary>A symbolic link whose target could not be resolved.</summary>
    Symlink,

    /// <summary>Socket, FIFO, block or character device — listed but not actionable.</summary>
    Other,
}

/// <summary>Broad media classification, used by the gallery and search filters (spec §20).</summary>
public enum MediaKind
{
    None,
    Image,
    Video,
    Audio,
    Document,
}

/// <summary>Direction of a transfer relative to the PC.</summary>
public enum TransferDirection
{
    /// <summary>Device to PC.</summary>
    Download,

    /// <summary>PC to device.</summary>
    Upload,
}

/// <summary>Lifecycle of a transfer job (spec §11).</summary>
public enum TransferStatus
{
    Queued,
    Preparing,
    Transferring,
    Paused,
    Completed,
    Failed,
    Cancelled,
    Retrying,
}

/// <summary>What to do when the destination already exists (spec §35).</summary>
public enum ConflictPolicy
{
    /// <summary>Ask the user. Never the effective policy at transfer time.</summary>
    Ask,
    Replace,
    Skip,

    /// <summary>Keep both, giving the incoming file a numbered suffix.</summary>
    Rename,
}

/// <summary>How thoroughly a completed transfer is checked (spec §37).</summary>
public enum VerificationMode
{
    /// <summary>Compare sizes. Always done; cheap.</summary>
    Size,

    /// <summary>Compare SHA-256 as well. Opt-in — hashing over USB is expensive.</summary>
    Sha256,
}
