namespace Handspan.Core.Models;

/// <summary>
/// Everything shown on the device dashboard (spec §6).
/// </summary>
/// <remarks>
/// Every field beyond <see cref="Id"/> and <see cref="State"/> is best-effort and populated
/// progressively: a phone that won't report its battery level must never block browsing (spec §6).
/// </remarks>
public sealed record DeviceInfo
{
    public required DeviceId Id { get; init; }

    public required DeviceState State { get; init; }

    public ConnectionType ConnectionType { get; init; } = ConnectionType.Usb;

    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    /// <summary>Android release string, e.g. "16".</summary>
    public string? AndroidVersion { get; init; }

    /// <summary>Android API level, e.g. 36.</summary>
    public int? ApiLevel { get; init; }

    /// <summary>User-assigned name from the device profile, overriding <see cref="Model"/> in the UI.</summary>
    public string? DisplayNameOverride { get; init; }

    public StorageInfo? Storage { get; init; }

    public int? BatteryPercent { get; init; }

    public bool? IsCharging { get; init; }

    /// <summary>Negotiated USB speed where detectable, e.g. "USB 3.1".</summary>
    public string? UsbSpeed { get; init; }

    /// <summary>Version string of the ADB server this device is reached through.</summary>
    public string? AdbVersion { get; init; }

    public DeviceCapabilities Capabilities { get; init; } = new();

    /// <summary>Best available human name for the device.</summary>
    public string DisplayName => DisplayNameOverride
                                 ?? (Manufacturer, Model) switch
                                 {
                                     (null, null) => Id.Serial,
                                     (null, var model) => model!,
                                     (var manufacturer, null) => manufacturer!,
                                     var (manufacturer, model) =>
                                         model!.StartsWith(manufacturer!, StringComparison.OrdinalIgnoreCase)
                                             ? model
                                             : $"{manufacturer} {model}",
                                 };

    /// <summary>True when the device can actually be used for file operations.</summary>
    public bool IsUsable => State == DeviceState.Online;
}

/// <summary>Capacity of one storage volume (spec §6, §62).</summary>
public sealed record StorageInfo
{
    public required DevicePath Root { get; init; }

    public required long TotalBytes { get; init; }

    public required long FreeBytes { get; init; }

    /// <summary>Volume label for removable storage; null for internal shared storage.</summary>
    public string? Label { get; init; }

    public bool IsRemovable { get; init; }

    public long UsedBytes => TotalBytes - FreeBytes;

    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;
}

/// <summary>
/// What a specific device actually supports (spec §77).
/// </summary>
/// <remarks>
/// Determined by probing cheap operations at session start, never by inferring from the Android
/// version — OEM behavior varies more than version numbers suggest. The UI disables unsupported
/// operations rather than letting them fail at the point of use.
/// </remarks>
public sealed record DeviceCapabilities
{
    public bool CanBrowseSharedStorage { get; init; }

    public bool CanUpload { get; init; }

    public bool CanDownload { get; init; }

    public bool CanDelete { get; init; }

    public bool CanRename { get; init; }

    public bool CanCreateDirectory { get; init; }

    /// <summary>Supports seekable range reads, needed for streaming and thumbnail extraction.</summary>
    public bool CanStream { get; init; }

    public bool CanWirelessAdb { get; init; }

    // --- negotiated ADB features (internal plumbing, not shown in the UI) ---

    /// <summary>64-bit sizes and error codes on stat. Without it, sizes above 4 GiB are unreliable.</summary>
    public bool HasStatV2 { get; init; }

    /// <summary>64-bit sizes in directory listings.</summary>
    public bool HasLsV2 { get; init; }

    /// <summary>Separated stderr and a real exit code from shell commands.</summary>
    public bool HasShellV2 { get; init; }

    /// <summary>Compressed sync transfers.</summary>
    public bool HasSendRecvV2 { get; init; }

    /// <summary><c>sha256sum</c> is present, enabling optional hash verification.</summary>
    public bool HasSha256Sum { get; init; }

    /// <summary><c>dd</c> accepts <c>conv=notrunc</c>, enabling the primary resumable-push path.</summary>
    public bool HasDdNoTrunc { get; init; }

    /// <summary>The companion app is installed and responding (phase 7).</summary>
    public bool HasCompanion { get; init; }
}
