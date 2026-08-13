using AndroidExplorer.Core.Models;

namespace AndroidExplorer.Core.Interfaces;

/// <summary>
/// Indexed search over a device (spec §27, §28).
/// </summary>
/// <remarks>
/// Queries hit a local index, never a recursive device scan (spec §28). The device is touched only
/// to keep the index fresh.
/// </remarks>
public interface ISearchService
{
    DeviceId DeviceId { get; }

    Task<IReadOnlyList<DeviceEntry>> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken);

    /// <summary>Crawls the device and updates the index incrementally.</summary>
    Task IndexAsync(IProgress<IndexProgress>? progress, CancellationToken cancellationToken);

    /// <summary>When the index was last refreshed, or null if never.</summary>
    Task<DateTimeOffset?> GetLastIndexedAsync(CancellationToken cancellationToken);

    Task<int> GetIndexedCountAsync(CancellationToken cancellationToken);
}

/// <summary>Search terms and filters (spec §27).</summary>
public sealed record SearchQuery
{
    public required string Text { get; init; }

    /// <summary>Restrict to these kinds. Empty means all.</summary>
    public IReadOnlyList<MediaKind> Kinds { get; init; } = [];

    public long? MinSize { get; init; }

    public long? MaxSize { get; init; }

    public DateTimeOffset? ModifiedAfter { get; init; }

    public DateTimeOffset? ModifiedBefore { get; init; }

    /// <summary>Restrict to a subtree. Null searches the whole indexed device.</summary>
    public DevicePath? Under { get; init; }

    public bool IncludeDirectories { get; init; } = true;

    public int Limit { get; init; } = 500;
}

public readonly record struct IndexProgress(int FilesIndexed, int DirectoriesScanned, DevicePath Current);

/// <summary>
/// Storage breakdown and largest-file analysis (spec §62, §63).
/// </summary>
/// <remarks>
/// Computed from the search index, so it is instant after the first crawl and needs no extra device work.
/// </remarks>
public interface IStorageAnalyzer
{
    Task<StorageBreakdown> AnalyzeAsync(CancellationToken cancellationToken);

    /// <summary>Largest indexed files, biggest first.</summary>
    Task<IReadOnlyList<DeviceEntry>> GetLargestFilesAsync(
        int count,
        long minimumBytes,
        CancellationToken cancellationToken);

    /// <summary>Immediate children of a directory with their recursive sizes, for drill-down.</summary>
    Task<IReadOnlyList<StorageFolder>> GetFolderBreakdownAsync(
        DevicePath parent,
        CancellationToken cancellationToken);
}

/// <summary>What is using the device's storage (spec §62).</summary>
public sealed record StorageBreakdown
{
    public required DeviceId DeviceId { get; init; }

    /// <summary>Volume totals, when the device reported them.</summary>
    public StorageInfo? Volume { get; init; }

    public required IReadOnlyList<StorageCategory> Categories { get; init; }

    /// <summary>Total of all indexed files.</summary>
    public long IndexedBytes { get; init; }

    public int IndexedFiles { get; init; }

    /// <summary>
    /// Space the volume reports as used but the index cannot account for — app data and areas Android
    /// does not let us read. Presented honestly rather than attributed to a guess (spec §62).
    /// </summary>
    public long UnaccountedBytes => Volume is { } volume
        ? Math.Max(0, volume.UsedBytes - IndexedBytes)
        : 0;
}

public sealed record StorageCategory(MediaKind Kind, string Label, int FileCount, long Bytes);

public sealed record StorageFolder(DevicePath Path, string Name, int FileCount, long Bytes);

/// <summary>
/// Finds duplicate files in increasing cost order (spec §61).
/// </summary>
public interface IDuplicateFinder
{
    /// <summary>
    /// Groups duplicates: identical size, then a partial hash, then a full device-side hash — stopping as
    /// early as each candidate group allows.
    /// </summary>
    Task<IReadOnlyList<DuplicateGroup>> FindAsync(
        DuplicateSearchOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

public sealed record DuplicateSearchOptions
{
    /// <summary>Files below this size are not worth grouping.</summary>
    public long MinimumBytes { get; init; } = 64 * 1024;

    /// <summary>Restrict to a subtree; null searches everything indexed.</summary>
    public DevicePath? Under { get; init; }

    /// <summary>
    /// Confirm candidates with a full device-side hash. Off by default: hashing whole files over USB is
    /// expensive, and a matching size plus partial hash is already a strong signal (spec §36).
    /// </summary>
    public bool VerifyWithFullHash { get; init; }

    public int MaxGroups { get; init; } = 200;
}

/// <summary>A set of files believed identical (spec §61).</summary>
public sealed record DuplicateGroup
{
    public required long Size { get; init; }

    public required IReadOnlyList<DevicePath> Paths { get; init; }

    /// <summary>How the group was confirmed, so the UI can say how sure it is.</summary>
    public required DuplicateConfidence Confidence { get; init; }

    /// <summary>Bytes reclaimable by keeping one copy.</summary>
    public long ReclaimableBytes => Size * (Paths.Count - 1);
}

public enum DuplicateConfidence
{
    /// <summary>Same size only — weak, never presented as certain.</summary>
    SizeOnly,

    /// <summary>Same size and same head/tail sample.</summary>
    PartialHash,

    /// <summary>Same size and same full hash.</summary>
    FullHash,
}

/// <summary>
/// Cached directory listings, so navigation feels instant (spec §29).
/// </summary>
/// <remarks>
/// ADB offers no filesystem watcher for shared storage, so the model is cache, then refresh, then
/// diff (spec §52). Every key includes the device (spec §39).
/// </remarks>
public interface ICacheService
{
    Task<IReadOnlyList<DeviceEntry>?> GetListingAsync(
        DeviceId deviceId,
        DevicePath path,
        CancellationToken cancellationToken);

    Task SetListingAsync(
        DeviceId deviceId,
        DevicePath path,
        IReadOnlyList<DeviceEntry> entries,
        CancellationToken cancellationToken);

    /// <summary>Drops a directory's cached listing after we modify its contents.</summary>
    Task InvalidateAsync(DeviceId deviceId, DevicePath path, CancellationToken cancellationToken);

    Task InvalidateDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// Application and per-device settings (spec §50, §67).
/// </summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    Task LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);

    Task<DeviceProfile> GetProfileAsync(DeviceId deviceId, CancellationToken cancellationToken);

    Task SaveProfileAsync(DeviceProfile profile, CancellationToken cancellationToken);
}

/// <summary>Settings from spec §50. Defaults are deliberately conservative.</summary>
public sealed record AppSettings
{
    // General
    public string? DefaultDownloadFolder { get; init; }

    public bool ConfirmDeletes { get; init; } = true;

    public bool ConfirmLargeTransfers { get; init; } = true;

    public bool MinimizeToTray { get; init; }

    // Explorer
    public bool ShowHiddenFiles { get; init; }

    public bool ShowFileExtensions { get; init; } = true;

    // Transfers
    public int MaxConcurrentSmallTransfers { get; init; } = 4;

    public int MaxConcurrentLargeTransfers { get; init; } = 2;

    /// <summary>Byte threshold separating "small" from "large" for scheduling (spec §12).</summary>
    public long LargeFileThresholdBytes { get; init; } = 8L * 1024 * 1024;

    public int RetryCount { get; init; } = 3;

    public VerificationMode Verification { get; init; } = VerificationMode.Size;

    public bool NotifyOnComplete { get; init; } = true;

    // Gallery
    public int ThumbnailMaxEdgePixels { get; init; } = 320;

    public long ThumbnailCacheCapBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public bool GenerateVideoThumbnails { get; init; } = true;

    /// <summary>Above this size, a file with no embedded thumbnail gets an icon instead of a decode.</summary>
    public long FullDecodeThresholdBytes { get; init; } = 12L * 1024 * 1024;

    // Connection
    public string? AdbExecutablePath { get; init; }

    public bool AllowWirelessAdb { get; init; }

    public int ConnectionTimeoutSeconds { get; init; } = 15;

    // Diagnostics — off by default because verbose logs record file paths (spec §43)
    public bool VerboseDiagnostics { get; init; }
}

/// <summary>Per-device preferences (spec §67).</summary>
public sealed record DeviceProfile
{
    public required DeviceId DeviceId { get; init; }

    public string? DisplayName { get; init; }

    public DateTimeOffset? LastConnected { get; init; }

    public IReadOnlyList<DevicePath> Favorites { get; init; } = [];

    public IReadOnlyList<DevicePath> GallerySources { get; init; } = [];

    public string? PreferredView { get; init; }

    public string? SortOrder { get; init; }

    /// <summary>Measured best concurrency for this device, from the transfer benchmark (spec §12).</summary>
    public int? BenchmarkedConcurrency { get; init; }
}
