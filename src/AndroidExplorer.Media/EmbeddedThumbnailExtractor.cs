using System.Buffers.Binary;

namespace AndroidExplorer.Media;

/// <summary>
/// Finds a thumbnail already inside an image file's header bytes.
/// </summary>
/// <remarks>
/// <para>
/// This is the single most important optimisation in the gallery (spec §21, §94). Virtually every photo a
/// phone camera takes carries a small JPEG preview in its EXIF IFD1 block, near the start of the file. A
/// 5 MB photo therefore yields a usable thumbnail from roughly 1% of its bytes — no full transfer, no
/// decode of a 12-megapixel image.
/// </para>
/// <para>
/// Two strategies, in order of trustworthiness: parse the EXIF structure properly, and if that finds
/// nothing, scan the header for a self-contained JPEG. The scan covers HEIC, DNG and other containers that
/// embed a preview without an EXIF IFD1 we can reach.
/// </para>
/// </remarks>
public static class EmbeddedThumbnailExtractor
{
    /// <summary>How much of a file's head is worth reading to look for a thumbnail.</summary>
    public const int RecommendedHeaderBytes = 192 * 1024;

    private const ushort JpegSoi = 0xFFD8;
    private const ushort JpegEoi = 0xFFD9;
    private const ushort JpegApp1 = 0xFFE1;

    /// <summary>Smallest byte count that could plausibly be a real thumbnail rather than noise.</summary>
    private const int MinimumThumbnailBytes = 1024;

    /// <summary>
    /// Returns embedded JPEG thumbnail bytes, or null when the header contains none.
    /// </summary>
    public static byte[]? TryExtract(ReadOnlySpan<byte> header)
    {
        if (header.Length < 64)
        {
            return null;
        }

        return TryExtractFromExif(header) ?? TryScanForEmbeddedJpeg(header);
    }

    /// <summary>
    /// Parses JPEG APP1/EXIF and returns the IFD1 (thumbnail) JPEG, the reliable path.
    /// </summary>
    private static byte[]? TryExtractFromExif(ReadOnlySpan<byte> header)
    {
        // A JPEG starts with SOI; anything else has no APP1 at a predictable place.
        if (ReadUInt16BigEndian(header, 0) != JpegSoi)
        {
            return null;
        }

        var position = 2;

        while (position + 4 <= header.Length)
        {
            var marker = ReadUInt16BigEndian(header, position);

            // Markers are 0xFFxx; anything else means we have lost the structure.
            if ((marker & 0xFF00) != 0xFF00)
            {
                return null;
            }

            var segmentLength = ReadUInt16BigEndian(header, position + 2);
            if (segmentLength < 2)
            {
                return null;
            }

            var payloadStart = position + 4;
            var payloadLength = segmentLength - 2;

            if (payloadStart + payloadLength > header.Length)
            {
                // The segment is truncated by our header window; nothing more to read here.
                return null;
            }

            if (marker == JpegApp1 && payloadLength > 6
                && header.Slice(payloadStart, 6).SequenceEqual("Exif\0\0"u8))
            {
                return TryReadIfd1Thumbnail(header.Slice(payloadStart + 6, payloadLength - 6));
            }

            // Start of scan: image data follows and there are no more metadata segments.
            if (marker == 0xFFDA)
            {
                return null;
            }

            position = payloadStart + payloadLength;
        }

        return null;
    }

    /// <summary>Walks a TIFF header to IFD1 and returns its JPEG thumbnail if present.</summary>
    private static byte[]? TryReadIfd1Thumbnail(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return null;
        }

        // TIFF byte order: "II" little-endian, "MM" big-endian.
        var littleEndian = tiff[0] == 'I' && tiff[1] == 'I';
        if (!littleEndian && !(tiff[0] == 'M' && tiff[1] == 'M'))
        {
            return null;
        }

        var ifd0Offset = ReadUInt32(tiff, 4, littleEndian);
        if (ifd0Offset + 2 > (uint)tiff.Length)
        {
            return null;
        }

        // IFD0 is the main image; the next-IFD pointer at its end is IFD1, the thumbnail.
        var ifd0EntryCount = ReadUInt16(tiff, (int)ifd0Offset, littleEndian);
        var nextIfdPointer = (int)ifd0Offset + 2 + (ifd0EntryCount * 12);

        if (nextIfdPointer + 4 > tiff.Length)
        {
            return null;
        }

        var ifd1Offset = ReadUInt32(tiff, nextIfdPointer, littleEndian);
        if (ifd1Offset == 0 || ifd1Offset + 2 > (uint)tiff.Length)
        {
            return null;
        }

        var entryCount = ReadUInt16(tiff, (int)ifd1Offset, littleEndian);
        long thumbnailOffset = -1;
        long thumbnailLength = -1;

        for (var i = 0; i < entryCount; i++)
        {
            var entry = (int)ifd1Offset + 2 + (i * 12);
            if (entry + 12 > tiff.Length)
            {
                return null;
            }

            var tag = ReadUInt16(tiff, entry, littleEndian);
            var value = ReadUInt32(tiff, entry + 8, littleEndian);

            switch (tag)
            {
                case 0x0201: // JPEGInterchangeFormat
                    thumbnailOffset = value;
                    break;
                case 0x0202: // JPEGInterchangeFormatLength
                    thumbnailLength = value;
                    break;
            }
        }

        if (thumbnailOffset <= 0 || thumbnailLength < MinimumThumbnailBytes
            || thumbnailOffset + thumbnailLength > tiff.Length)
        {
            return null;
        }

        var thumbnail = tiff.Slice((int)thumbnailOffset, (int)thumbnailLength);

        // Sanity check: it must actually be a JPEG.
        return ReadUInt16BigEndian(thumbnail, 0) == JpegSoi ? thumbnail.ToArray() : null;
    }

    /// <summary>
    /// Scans for a complete JPEG inside the header, skipping the outer file's own SOI.
    /// </summary>
    /// <remarks>
    /// A heuristic, used for containers whose preview we cannot address structurally — HEIC and DNG in
    /// particular. Bounded by the header window, and rejected unless the candidate is large enough to be a
    /// real preview and small enough to be a thumbnail rather than the full image.
    /// </remarks>
    private static byte[]? TryScanForEmbeddedJpeg(ReadOnlySpan<byte> header)
    {
        // Start past a leading SOI so a JPEG's own image data is not mistaken for its thumbnail.
        var searchFrom = ReadUInt16BigEndian(header, 0) == JpegSoi ? 2 : 0;

        for (var i = searchFrom; i + 4 < header.Length; i++)
        {
            if (header[i] != 0xFF || header[i + 1] != 0xD8 || header[i + 2] != 0xFF)
            {
                continue;
            }

            // Found a candidate SOI; look for its EOI.
            for (var j = i + 4; j + 1 < header.Length; j++)
            {
                if (header[j] != 0xFF || header[j + 1] != 0xD9)
                {
                    continue;
                }

                var length = j + 2 - i;
                if (length >= MinimumThumbnailBytes)
                {
                    return header.Slice(i, length).ToArray();
                }

                break;
            }
        }

        return null;
    }

    private static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> data, int offset)
        => offset + 2 <= data.Length
            ? BinaryPrimitives.ReadUInt16BigEndian(data[offset..])
            : (ushort)0;

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        if (offset + 2 > data.Length)
        {
            return 0;
        }

        return littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..])
            : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        if (offset + 4 > data.Length)
        {
            return 0;
        }

        return littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..])
            : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
    }
}
