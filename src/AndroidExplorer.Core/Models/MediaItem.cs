namespace AndroidExplorer.Core.Models;

/// <summary>
/// A photo, video or audio file surfaced in the gallery (spec §59, §76).
/// </summary>
public sealed record MediaItem
{
    public required DeviceId DeviceId { get; init; }

    public required DevicePath Path { get; init; }

    public required MediaKind Kind { get; init; }

    public required long Size { get; init; }

    /// <summary>File modification time. Used for timeline grouping until EXIF date-taken is read.</summary>
    public required DateTimeOffset Modified { get; init; }

    /// <summary>EXIF date taken, when known — preferred over <see cref="Modified"/> for grouping (spec §25).</summary>
    public DateTimeOffset? DateTaken { get; init; }

    public string? MimeType { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>Duration for video and audio.</summary>
    public TimeSpan? Duration { get; init; }

    public string Name => Path.Name;

    /// <summary>The date a timeline groups this item under, normalized to local time (spec §25).</summary>
    public DateTime TimelineDate => (DateTaken ?? Modified).ToLocalTime().Date;

    /// <summary>
    /// Cache identity for this item's thumbnail: device, path, size and modified time (spec §21).
    /// Changing any of them regenerates the thumbnail rather than serving a stale one.
    /// </summary>
    public string ThumbnailKey => $"{DeviceId.Serial}|{Path.Value}|{Size}|{Modified.ToUnixTimeSeconds()}";
}

/// <summary>
/// A virtual album, derived from a directory (spec §26).
/// </summary>
/// <remarks>
/// Derived by inspecting what is actually on the device, never by assuming an OEM layout — vendors
/// and messaging apps scatter media widely.
/// </remarks>
public sealed record Album
{
    public required DeviceId DeviceId { get; init; }

    public required DevicePath Path { get; init; }

    public required string Name { get; init; }

    public int ItemCount { get; init; }

    public long TotalBytes { get; init; }

    /// <summary>Item used as the album's cover image.</summary>
    public MediaItem? Cover { get; init; }

    public DateTimeOffset? NewestItem { get; init; }
}

/// <summary>
/// Media type detection (spec §20).
/// </summary>
/// <remarks>
/// Extension matching is the fast path; MIME sniffing overrides it where reliable, because files
/// with wrong or missing extensions are common on phones.
/// </remarks>
public static class MediaTypes
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".heic", ".heif", ".avif", ".bmp",
        ".dng", ".tif", ".tiff",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".webm", ".avi", ".3gp", ".m4v", ".ts", ".flv",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".opus", ".aac", ".amr", ".mid",
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf",
        ".odt", ".ods", ".epub", ".csv", ".md",
    };

    /// <summary>Classifies by extension. Returns <see cref="MediaKind.None"/> when unrecognized.</summary>
    public static MediaKind FromExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return MediaKind.None;
        }

        if (ImageExtensions.Contains(extension))
        {
            return MediaKind.Image;
        }

        if (VideoExtensions.Contains(extension))
        {
            return MediaKind.Video;
        }

        if (AudioExtensions.Contains(extension))
        {
            return MediaKind.Audio;
        }

        return DocumentExtensions.Contains(extension) ? MediaKind.Document : MediaKind.None;
    }

    /// <summary>Classifies a path by its extension.</summary>
    public static MediaKind FromPath(DevicePath path) => FromExtension(path.Extension);

    /// <summary>True for formats whose thumbnails can usually be extracted from a partial read (spec §4.1 tier T1/T2).</summary>
    public static bool MayHaveEmbeddedThumbnail(string extension)
        => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".dng", StringComparison.OrdinalIgnoreCase);
}
