namespace Handspan.Core.Models;

/// <summary>
/// Rich metadata for the properties dialog (spec §33).
/// </summary>
public sealed record FileMetadata
{
    public required DevicePath Path { get; init; }

    public string? MimeType { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public TimeSpan? Duration { get; init; }

    public string? VideoCodec { get; init; }

    public string? AudioCodec { get; init; }

    public int? BitrateKbps { get; init; }

    public ExifMetadata? Exif { get; init; }

    public string? Resolution => Width is { } w && Height is { } h ? $"{w} × {h}" : null;
}

/// <summary>
/// EXIF fields worth showing (spec §33).
/// </summary>
/// <remarks>
/// <see cref="HasGpsCoordinates"/> is exposed instead of the coordinates themselves being persisted
/// anywhere: GPS is sensitive, is never written to the index or the logs, and is only read on demand
/// for display (spec §33, §43).
/// </remarks>
public sealed record ExifMetadata
{
    public DateTimeOffset? DateTaken { get; init; }

    public string? CameraMake { get; init; }

    public string? CameraModel { get; init; }

    public string? LensModel { get; init; }

    public int? IsoSpeed { get; init; }

    public string? ExposureTime { get; init; }

    public double? FNumber { get; init; }

    public double? FocalLength { get; init; }

    public int? Orientation { get; init; }

    /// <summary>True when the file carries location data, without exposing or storing it.</summary>
    public bool HasGpsCoordinates { get; init; }

    /// <summary>
    /// Latitude and longitude, populated only when the user explicitly asks to see them. Never
    /// persisted to the index or logged.
    /// </summary>
    public (double Latitude, double Longitude)? GpsCoordinates { get; init; }
}
