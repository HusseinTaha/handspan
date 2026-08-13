namespace AndroidExplorer.Core.Models;

/// <summary>
/// Well-known locations in Android shared storage (spec §16).
/// </summary>
/// <remarks>
/// These are conventions, not guarantees: OEMs and apps place media in their own directories, so
/// treat every one of these as "check whether it exists" rather than "assume it exists". Nothing
/// here grants access to protected areas — see <see cref="Protected"/> (spec §17).
/// </remarks>
public static class KnownPaths
{
    /// <summary>Shared/emulated user storage, usually a symlink to /storage/emulated/0.</summary>
    public static DevicePath InternalStorage { get; } = DevicePath.Parse("/sdcard");

    /// <summary>Mount point under which removable volumes (SD card, USB OTG) appear.</summary>
    public static DevicePath StorageRoot { get; } = DevicePath.Parse("/storage");

    public static DevicePath Dcim { get; } = DevicePath.Parse("/sdcard/DCIM");

    public static DevicePath Camera { get; } = DevicePath.Parse("/sdcard/DCIM/Camera");

    public static DevicePath Pictures { get; } = DevicePath.Parse("/sdcard/Pictures");

    public static DevicePath Screenshots { get; } = DevicePath.Parse("/sdcard/Pictures/Screenshots");

    public static DevicePath Movies { get; } = DevicePath.Parse("/sdcard/Movies");

    public static DevicePath Music { get; } = DevicePath.Parse("/sdcard/Music");

    public static DevicePath Download { get; } = DevicePath.Parse("/sdcard/Download");

    public static DevicePath Documents { get; } = DevicePath.Parse("/sdcard/Documents");

    /// <summary>App-private storage. Large, permission-fraught, and skipped by the indexer by default.</summary>
    public static DevicePath AndroidData { get; } = DevicePath.Parse("/sdcard/Android/data");

    /// <summary>Default gallery scan roots (spec §19). Configurable — never hard-code only these.</summary>
    public static IReadOnlyList<DevicePath> DefaultGallerySources { get; } =
    [
        Dcim,
        Pictures,
        Movies,
        Download,
    ];

    /// <summary>
    /// Areas the app must not present as browsable user storage (spec §17). Without root these are
    /// inaccessible anyway; the point is to never promise "browse the entire Android filesystem".
    /// </summary>
    public static IReadOnlyList<DevicePath> Protected { get; } =
    [
        DevicePath.Parse("/system"),
        DevicePath.Parse("/data"),
        DevicePath.Parse("/vendor"),
        DevicePath.Parse("/apex"),
        DevicePath.Parse("/proc"),
        DevicePath.Parse("/sys"),
    ];

    /// <summary>True when <paramref name="path"/> is inside an area we treat as protected.</summary>
    public static bool IsProtected(DevicePath path)
        => Protected.Any(root => path == root || root.IsAncestorOf(path));
}
