using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using Microsoft.Extensions.Logging;
using Directory = MetadataExtractor.Directory;

namespace Handspan.Media;

public interface IMetadataServiceFactory
{
    IMetadataService Create(DeviceId device, IDeviceFileSystem fileSystem);
}

public sealed class MetadataServiceFactory(ILoggerFactory loggers) : IMetadataServiceFactory
{
    public IMetadataService Create(DeviceId device, IDeviceFileSystem fileSystem)
        => new MetadataService(fileSystem, loggers.CreateLogger<MetadataService>());
}

/// <summary>
/// Reads file metadata, including EXIF, for the properties dialog (spec §33).
/// </summary>
/// <remarks>
/// <para>
/// Only the header is transferred. Metadata lives near the start of every format that carries it, so reading a
/// bounded prefix over a range read costs a fraction of the file — the same reasoning as the thumbnail path
/// (spec §21, §94). A 60 MB video's dimensions should not cost 60 MB.
/// </para>
/// <para>
/// GPS is treated as sensitive (spec §33, §43). Whether a file carries coordinates is reported freely, because
/// that is useful to know before sharing a photo, but the coordinates themselves are only read when explicitly
/// asked for, and are never written to the index or the log.
/// </para>
/// </remarks>
public sealed class MetadataService(
    IDeviceFileSystem fileSystem,
    ILogger<MetadataService> logger) : IMetadataService
{
    /// <summary>
    /// Header bytes read for metadata.
    /// </summary>
    /// <remarks>
    /// Generous enough for a full EXIF block with an embedded thumbnail, and for the <c>moov</c> atom of an MP4
    /// written with its index at the front — which most phone cameras do.
    /// </remarks>
    private const int HeaderBytes = 320 * 1024;

    public Task<FileMetadata> GetMetadataAsync(DevicePath path, CancellationToken cancellationToken)
        => GetMetadataAsync(path, includeGpsCoordinates: false, cancellationToken);

    /// <summary>
    /// Reads metadata, optionally including GPS coordinates.
    /// </summary>
    /// <param name="includeGpsCoordinates">
    /// Only true when the user has asked to see the location. Coordinates are otherwise left out entirely
    /// rather than read and discarded, so they cannot leak into a log or a crash dump (spec §43).
    /// </param>
    public async Task<FileMetadata> GetMetadataAsync(
        DevicePath path,
        bool includeGpsCoordinates,
        CancellationToken cancellationToken)
    {
        var kind = MediaTypes.FromPath(path);
        var metadata = new FileMetadata { Path = path, MimeType = GuessMimeType(path) };

        if (kind is MediaKind.None or MediaKind.Document)
        {
            return metadata;
        }

        try
        {
            var header = await fileSystem
                .ReadRangeAsync(path, 0, HeaderBytes, cancellationToken)
                .ConfigureAwait(false);

            if (header.Length < 32)
            {
                return metadata;
            }

            using var stream = new MemoryStream(header, writable: false);

            // MetadataExtractor throws on a truncated file, which a bounded header read always is once the
            // metadata has been passed. Whatever it managed to parse before that point is still usable.
            IReadOnlyList<Directory> directories;
            try
            {
                directories = ImageMetadataReader.ReadMetadata(stream);
            }
            catch (Exception ex) when (ex is ImageProcessingException or IOException
                                           or IndexOutOfRangeException or ArgumentException)
            {
                logger.LogDebug("Metadata could not be parsed from a {Extension} header.", path.Extension);
                return metadata;
            }

            return kind == MediaKind.Video
                ? ReadVideo(metadata, directories)
                : ReadImage(metadata, directories, includeGpsCoordinates);
        }
        catch (DeviceException ex)
        {
            // Spec §43: the extension, never the path.
            logger.LogDebug("Could not read a {Extension} header: {Reason}", path.Extension, ex.UserMessage);
            return metadata;
        }
    }

    private static FileMetadata ReadImage(
        FileMetadata metadata,
        IReadOnlyList<Directory> directories,
        bool includeGpsCoordinates)
    {
        var (width, height) = ReadDimensions(directories);

        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();

        var exif = new ExifMetadata
        {
            DateTaken = TryGetDate(subIfd, ExifDirectoryBase.TagDateTimeOriginal)
                        ?? TryGetDate(ifd0, ExifDirectoryBase.TagDateTime),
            CameraMake = TryGetString(ifd0, ExifDirectoryBase.TagMake),
            CameraModel = TryGetString(ifd0, ExifDirectoryBase.TagModel),
            LensModel = TryGetString(subIfd, ExifDirectoryBase.TagLensModel),
            IsoSpeed = TryGetInt(subIfd, ExifDirectoryBase.TagIsoEquivalent),
            ExposureTime = FormatExposure(TryGetDouble(subIfd, ExifDirectoryBase.TagExposureTime)),
            FNumber = TryGetDouble(subIfd, ExifDirectoryBase.TagFNumber),
            FocalLength = TryGetDouble(subIfd, ExifDirectoryBase.TagFocalLength),
            Orientation = TryGetInt(ifd0, ExifDirectoryBase.TagOrientation),

            // Presence is safe to report; the values are not read unless asked for.
            HasGpsCoordinates = gps?.GetGeoLocation() is { IsZero: false },
            GpsCoordinates = includeGpsCoordinates && gps?.GetGeoLocation() is { IsZero: false } location
                ? (location.Latitude, location.Longitude)
                : null,
        };

        return metadata with { Width = width, Height = height, Exif = exif };
    }

    private static FileMetadata ReadVideo(FileMetadata metadata, IReadOnlyList<Directory> directories)
    {
        var track = directories.OfType<QuickTimeTrackHeaderDirectory>().FirstOrDefault();
        var movie = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();

        var width = TryGetInt(track, QuickTimeTrackHeaderDirectory.TagWidth);
        var height = TryGetInt(track, QuickTimeTrackHeaderDirectory.TagHeight);

        TimeSpan? duration = null;
        if (movie is not null
            && movie.TryGetInt32(QuickTimeMovieHeaderDirectory.TagDuration, out var units)
            && movie.TryGetInt32(QuickTimeMovieHeaderDirectory.TagTimeScale, out var scale)
            && scale > 0)
        {
            duration = TimeSpan.FromSeconds((double)units / scale);
        }

        return metadata with
        {
            Width = width,
            Height = height,
            Duration = duration,
        };
    }

    private static (int? Width, int? Height) ReadDimensions(IReadOnlyList<Directory> directories)
    {
        // Several directories can carry dimensions; the first that has both is good enough.
        foreach (var (widthTag, heightTag) in new[]
                 {
                     (ExifDirectoryBase.TagImageWidth, ExifDirectoryBase.TagImageHeight),
                     (ExifDirectoryBase.TagExifImageWidth, ExifDirectoryBase.TagExifImageHeight),
                 })
        {
            foreach (var directory in directories)
            {
                if (directory.TryGetInt32(widthTag, out var width)
                    && directory.TryGetInt32(heightTag, out var height)
                    && width > 0 && height > 0)
                {
                    return (width, height);
                }
            }
        }

        // PNG, WebP and friends expose dimensions under their own tag numbers, which the base tags miss.
        foreach (var directory in directories)
        {
            var width = directory.Tags.FirstOrDefault(tag =>
                tag.Name.Equals("Image Width", StringComparison.OrdinalIgnoreCase));
            var height = directory.Tags.FirstOrDefault(tag =>
                tag.Name.Equals("Image Height", StringComparison.OrdinalIgnoreCase));

            if (width is not null && height is not null
                && TryParseLeadingInt(width.Description, out var w)
                && TryParseLeadingInt(height.Description, out var h))
            {
                return (w, h);
            }
        }

        return (null, null);
    }

    /// <summary>Descriptions arrive as "4032 pixels", so the number has to be taken off the front.</summary>
    private static bool TryParseLeadingInt(string? description, out int value)
    {
        value = 0;

        if (string.IsNullOrEmpty(description))
        {
            return false;
        }

        var digits = new string(description.TakeWhile(char.IsAsciiDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out value);
    }

    /// <summary>Renders a shutter speed the way a camera does: 1/250 rather than 0.004.</summary>
    private static string? FormatExposure(double? seconds) => seconds switch
    {
        null or <= 0 => null,
        >= 1 => $"{seconds:0.#}s",
        _ => $"1/{Math.Round(1 / seconds.Value)}s",
    };

    private static DateTimeOffset? TryGetDate(Directory? directory, int tag)
        => directory is not null && directory.TryGetDateTime(tag, out var value)
            ? new DateTimeOffset(value, TimeSpan.Zero)
            : null;

    private static string? TryGetString(Directory? directory, int tag)
    {
        var value = directory?.GetString(tag)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? TryGetInt(Directory? directory, int tag)
        => directory is not null && directory.TryGetInt32(tag, out var value) ? value : null;

    private static double? TryGetDouble(Directory? directory, int tag)
        => directory is not null && directory.TryGetDouble(tag, out var value) ? value : null;

    private static string? GuessMimeType(DevicePath path) => path.Extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".heic" or ".heif" => "image/heic",
        ".avif" => "image/avif",
        ".dng" => "image/x-adobe-dng",
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".3gp" => "video/3gpp",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".ogg" or ".opus" => "audio/ogg",
        ".pdf" => "application/pdf",
        _ => null,
    };
}
