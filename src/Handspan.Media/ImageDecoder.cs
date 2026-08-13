using SkiaSharp;

namespace Handspan.Media;

/// <summary>
/// Decoding and downscaling, via Skia (MIT — no LGPL exposure here).
/// </summary>
/// <remarks>
/// Skia covers JPEG, PNG, WebP, GIF and BMP. HEIC and AVIF are not decodable here and fall back to the
/// embedded-thumbnail path; full HEIC decoding needs the ffmpeg component planned for phase 4b.
/// </remarks>
public static class ImageDecoder
{
    /// <summary>Formats Skia can decode on every platform we ship.</summary>
    public static bool CanDecode(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" => true,
        _ => false,
    };

    /// <summary>
    /// Re-encodes image bytes as a WebP thumbnail bounded by <paramref name="maxEdgePixels"/>.
    /// Returns null when the bytes are not a decodable image.
    /// </summary>
    public static byte[]? CreateThumbnail(ReadOnlySpan<byte> imageBytes, int maxEdgePixels, int quality = 80)
    {
        using var data = SKData.CreateCopy(imageBytes.ToArray());
        using var codec = SKCodec.Create(data);

        if (codec is null)
        {
            return null;
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            return null;
        }

        // Ask the codec for a reduced size up front: decoding a 12-megapixel JPEG only to shrink it
        // wastes both time and memory.
        var scale = Math.Min(1f, (float)maxEdgePixels / Math.Max(info.Width, info.Height));
        var supported = codec.GetScaledDimensions(scale);

        using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(supported.Width, supported.Height));
        if (bitmap is null)
        {
            return null;
        }

        var targetWidth = Math.Max(1, (int)Math.Round(info.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(info.Height * scale));

        using var resized = bitmap.Width == targetWidth && bitmap.Height == targetHeight
            ? bitmap.Copy()
            : bitmap.Resize(new SKImageInfo(targetWidth, targetHeight), new SKSamplingOptions(
                SKFilterMode.Linear, SKMipmapMode.Linear));

        if (resized is null)
        {
            return null;
        }

        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, quality);

        return encoded?.ToArray();
    }

    /// <summary>Reads pixel dimensions from header bytes, without decoding the image.</summary>
    public static (int Width, int Height)? ReadDimensions(ReadOnlySpan<byte> imageBytes)
    {
        using var data = SKData.CreateCopy(imageBytes.ToArray());
        using var codec = SKCodec.Create(data);

        if (codec is null || codec.Info.Width <= 0)
        {
            return null;
        }

        return (codec.Info.Width, codec.Info.Height);
    }
}
