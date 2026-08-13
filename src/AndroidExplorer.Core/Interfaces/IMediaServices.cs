using AndroidExplorer.Core.Models;

namespace AndroidExplorer.Core.Interfaces;

/// <summary>
/// Produces and caches thumbnails (spec §21).
/// </summary>
/// <remarks>
/// Implementations must never pull a full-size file merely to draw a grid cell (spec §94). The
/// tiered strategy — embedded EXIF thumbnail from a partial read, then HEIC thumbnail item, then
/// bounded full decode, then video frame — is documented in docs/plan/04-gallery.md.
/// </remarks>
public interface IThumbnailService
{
    /// <summary>
    /// Returns thumbnail bytes, from cache when possible. Null when the file has no usable
    /// thumbnail, in which case the UI shows a type icon rather than stalling.
    /// </summary>
    Task<byte[]?> GetThumbnailAsync(
        MediaItem item,
        int maxEdgePixels,
        CancellationToken cancellationToken);

    /// <summary>Queues thumbnails for items about to become visible, at low priority.</summary>
    void Prefetch(IReadOnlyList<MediaItem> items, int maxEdgePixels);

    /// <summary>Drops queued work for items no longer visible. Essential during fast scrolling.</summary>
    void CancelPending(IReadOnlyList<MediaItem> items);

    /// <summary>Total bytes currently held in the on-disk thumbnail cache.</summary>
    Task<long> GetCacheSizeAsync(CancellationToken cancellationToken);

    Task ClearCacheAsync(DeviceId? deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// Builds the gallery's media view (spec §18–§26).
/// </summary>
public interface IGalleryService
{
    DeviceId DeviceId { get; }

    /// <summary>
    /// Media from the index, ordered newest first. Returns immediately from cache, so the gallery
    /// opens instantly and refreshes behind the user (spec §60).
    /// </summary>
    Task<IReadOnlyList<MediaItem>> GetTimelineAsync(
        MediaKind? filter,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>Virtual albums derived from directories that actually contain media (spec §26).</summary>
    Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaItem>> GetAlbumContentsAsync(
        DevicePath albumPath,
        CancellationToken cancellationToken);

    /// <summary>Rescans the configured gallery sources and updates the index.</summary>
    Task RefreshAsync(IProgress<int>? scannedCount, CancellationToken cancellationToken);

    /// <summary>Gallery scan roots. Configurable, never hard-coded to one OEM layout (spec §19).</summary>
    IReadOnlyList<DevicePath> Sources { get; set; }
}

/// <summary>
/// Reads file metadata, including EXIF (spec §33).
/// </summary>
public interface IMetadataService
{
    /// <summary>
    /// Reads metadata, transferring only the header bytes needed rather than the whole file.
    /// </summary>
    Task<FileMetadata> GetMetadataAsync(DevicePath path, CancellationToken cancellationToken);
}

/// <summary>
/// Serves media for preview without creating a permanent local copy (spec §57, §58).
/// </summary>
public interface IMediaPreviewService
{
    /// <summary>
    /// A loopback URL that streams the file with HTTP range support, so a player can seek without
    /// downloading. Token-scoped so other local processes cannot read the device through it.
    /// </summary>
    Task<Uri> GetStreamUrlAsync(DeviceId deviceId, DevicePath path, CancellationToken cancellationToken);

    /// <summary>Full-resolution image bytes for the viewer.</summary>
    Task<Stream> OpenImageAsync(DeviceId deviceId, DevicePath path, CancellationToken cancellationToken);
}
