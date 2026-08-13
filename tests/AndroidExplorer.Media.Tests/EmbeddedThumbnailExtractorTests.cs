using System.Buffers.Binary;
using AndroidExplorer.Media;

namespace AndroidExplorer.Media.Tests;

/// <summary>
/// The embedded-thumbnail parser (spec §21, §94).
/// </summary>
/// <remarks>
/// This is the single optimisation the gallery's feel depends on: without it, drawing a grid means pulling
/// full-size photos. The fixtures here are built byte by byte so the EXIF structure is exactly what a real
/// camera writes, rather than whatever a library happens to emit.
/// </remarks>
public class EmbeddedThumbnailExtractorTests
{
    /// <summary>A minimal but structurally valid JPEG, used as the fake "thumbnail" payload.</summary>
    private static byte[] BuildTinyJpeg(int padding)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xDB };   // SOI + DQT marker

        // A DQT segment large enough to make the whole thing exceed the parser's noise floor.
        var segmentLength = padding + 2;
        bytes.Add((byte)(segmentLength >> 8));
        bytes.Add((byte)(segmentLength & 0xFF));
        bytes.AddRange(Enumerable.Repeat((byte)0x42, padding));

        bytes.AddRange([0xFF, 0xD9]);                            // EOI
        return [.. bytes];
    }

    /// <summary>
    /// Builds a JPEG whose APP1/EXIF block carries an IFD1 thumbnail, exactly as a phone camera does.
    /// </summary>
    private static byte[] BuildJpegWithExifThumbnail(byte[] thumbnail, bool littleEndian = true)
    {
        // TIFF: header, IFD0 (one dummy entry) then IFD1 describing the thumbnail.
        var tiff = new List<byte>();

        tiff.AddRange(littleEndian ? "II"u8.ToArray() : "MM"u8.ToArray());
        AppendUInt16(tiff, 42, littleEndian);           // magic
        AppendUInt32(tiff, 8, littleEndian);            // IFD0 at offset 8

        // IFD0: one entry, then a pointer to IFD1.
        AppendUInt16(tiff, 1, littleEndian);            // entry count
        AppendUInt16(tiff, 0x011A, littleEndian);       // XResolution (arbitrary)
        AppendUInt16(tiff, 3, littleEndian);            // type SHORT
        AppendUInt32(tiff, 1, littleEndian);            // count
        AppendUInt32(tiff, 72, littleEndian);           // value

        var ifd1Offset = 8 + 2 + 12 + 4;
        AppendUInt32(tiff, (uint)ifd1Offset, littleEndian);

        // IFD1: two entries describing where the thumbnail lives.
        var thumbnailOffset = ifd1Offset + 2 + (12 * 2) + 4;

        AppendUInt16(tiff, 2, littleEndian);            // entry count

        AppendUInt16(tiff, 0x0201, littleEndian);       // JPEGInterchangeFormat
        AppendUInt16(tiff, 4, littleEndian);            // type LONG
        AppendUInt32(tiff, 1, littleEndian);
        AppendUInt32(tiff, (uint)thumbnailOffset, littleEndian);

        AppendUInt16(tiff, 0x0202, littleEndian);       // JPEGInterchangeFormatLength
        AppendUInt16(tiff, 4, littleEndian);
        AppendUInt32(tiff, 1, littleEndian);
        AppendUInt32(tiff, (uint)thumbnail.Length, littleEndian);

        AppendUInt32(tiff, 0, littleEndian);            // no IFD2

        tiff.AddRange(thumbnail);

        // Wrap the TIFF block in a JPEG APP1 segment.
        var exif = new List<byte>("Exif\0\0"u8.ToArray());
        exif.AddRange(tiff);

        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1 };
        var app1Length = exif.Count + 2;
        jpeg.Add((byte)(app1Length >> 8));
        jpeg.Add((byte)(app1Length & 0xFF));
        jpeg.AddRange(exif);

        // Then the outer image's own data.
        jpeg.AddRange([0xFF, 0xDA, 0x00, 0x08, 1, 2, 3, 4, 5, 6]);
        jpeg.AddRange([0xFF, 0xD9]);

        return [.. jpeg];

        static void AppendUInt16(List<byte> target, ushort value, bool little)
        {
            var buffer = new byte[2];
            if (little)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
            }

            target.AddRange(buffer);
        }

        static void AppendUInt32(List<byte> target, uint value, bool little)
        {
            var buffer = new byte[4];
            if (little)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            }

            target.AddRange(buffer);
        }
    }

    [Fact]
    public void Extracts_the_exif_thumbnail_from_a_little_endian_jpeg()
    {
        var thumbnail = BuildTinyJpeg(2000);
        var photo = BuildJpegWithExifThumbnail(thumbnail);

        var extracted = EmbeddedThumbnailExtractor.TryExtract(photo);

        Assert.NotNull(extracted);
        Assert.Equal(thumbnail, extracted);
    }

    [Fact]
    public void Extracts_the_exif_thumbnail_from_a_big_endian_jpeg()
    {
        // Motorola byte order is legal and some cameras use it; a parser that assumes Intel order fails
        // silently on those files.
        var thumbnail = BuildTinyJpeg(1500);
        var photo = BuildJpegWithExifThumbnail(thumbnail, littleEndian: false);

        Assert.Equal(thumbnail, EmbeddedThumbnailExtractor.TryExtract(photo));
    }

    [Fact]
    public void Extraction_needs_only_the_header_not_the_whole_file()
    {
        // The point of the design: a large photo yields its thumbnail from a bounded prefix read.
        var thumbnail = BuildTinyJpeg(3000);
        var header = BuildJpegWithExifThumbnail(thumbnail);

        var wholeFile = new byte[5 * 1024 * 1024];
        header.CopyTo(wholeFile, 0);

        // Only the first 128 KB is passed in, as the thumbnail service does.
        var extracted = EmbeddedThumbnailExtractor.TryExtract(wholeFile.AsSpan(0, 128 * 1024));

        Assert.NotNull(extracted);
        Assert.Equal(thumbnail, extracted);
        Assert.True(extracted!.Length < header.Length);
    }

    [Fact]
    public void Falls_back_to_scanning_for_an_embedded_jpeg()
    {
        // Models a HEIC or DNG: a container we cannot address structurally, with a JPEG preview inside.
        var preview = BuildTinyJpeg(2500);

        var container = new List<byte>();
        container.AddRange("\0\0\0\x18ftypheic"u8.ToArray());
        container.AddRange(Enumerable.Repeat((byte)0x11, 400));
        container.AddRange(preview);
        container.AddRange(Enumerable.Repeat((byte)0x22, 400));

        var extracted = EmbeddedThumbnailExtractor.TryExtract(container.ToArray());

        Assert.NotNull(extracted);
        Assert.Equal(preview, extracted);
    }

    [Fact]
    public void A_jpeg_without_a_thumbnail_yields_nothing()
    {
        // Plain JPEG: SOI, a small comment segment, scan data, EOI. The parser must not mistake the
        // image's own data for a thumbnail.
        var plain = new List<byte> { 0xFF, 0xD8, 0xFF, 0xFE, 0x00, 0x06, 1, 2, 3, 4 };
        plain.AddRange([0xFF, 0xDA, 0x00, 0x08]);
        plain.AddRange(Enumerable.Repeat((byte)0x77, 5000));
        plain.AddRange([0xFF, 0xD9]);

        Assert.Null(EmbeddedThumbnailExtractor.TryExtract(plain.ToArray()));
    }

    [Fact]
    public void Tiny_candidates_are_rejected_as_noise()
    {
        // A few stray bytes that happen to look like SOI/EOI are not a thumbnail.
        var noise = new byte[] { 0x00, 0x01, 0xFF, 0xD8, 0xFF, 0xD9, 0x02, 0x03 };

        Assert.Null(EmbeddedThumbnailExtractor.TryExtract(noise));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(63)]
    public void Truncated_input_is_handled_without_throwing(int length)
    {
        var truncated = BuildJpegWithExifThumbnail(BuildTinyJpeg(1200)).AsSpan(0, length).ToArray();

        Assert.Null(EmbeddedThumbnailExtractor.TryExtract(truncated));
    }

    [Fact]
    public void A_declared_thumbnail_beyond_the_header_window_is_ignored()
    {
        // A file claiming its thumbnail lives past what we read must not cause an out-of-range read.
        var photo = BuildJpegWithExifThumbnail(BuildTinyJpeg(4000));

        // Cut the file in half, leaving the IFD pointing outside the buffer.
        var half = photo.AsSpan(0, photo.Length / 2).ToArray();

        Assert.Null(EmbeddedThumbnailExtractor.TryExtract(half));
    }
}
