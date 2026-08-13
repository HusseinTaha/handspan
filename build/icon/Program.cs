using System.Buffers.Binary;
using SkiaSharp;

namespace Handspan.IconGen;

/// <summary>
/// Draws the Handspan mark and packs it into every container the app needs.
/// </summary>
/// <remarks>
/// The mark is two piers with a deck overhanging them: a span across supports, which is what
/// the name means, and which also happens to be an H. It was chosen over a literal bridge
/// because a literal bridge — towers, catenary, deck — turns to mud below about 32 px, and the
/// size that actually matters is 16 px in a taskbar.
/// </remarks>
internal static class Program
{
    // Deck and piers are stroked rather than filled so weights stay proportional at every size.
    private const float PierWeight = 0.150f;
    private const float DeckWeight = 0.130f;
    private const float PierLeft = 0.315f;
    private const float PierRight = 0.685f;
    private const float DeckOverhang = 0.075f;
    private const float CornerRadius = 0.22f;

    private static readonly SKColor GradientFrom = new(0x0D, 0x94, 0x88); // teal 600
    private static readonly SKColor GradientTo = new(0x06, 0xB6, 0xD4);   // cyan 500

    // Windows wants these in the .ico; 256 is the one Explorer's large-icon view uses.
    private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    // macOS icns entry types paired with the pixel size each one expects.
    private static readonly (string Type, int Size)[] IcnsEntries =
    [
        ("ic04", 16), ("ic05", 32), ("ic11", 32), ("ic12", 64),
        ("ic07", 128), ("ic13", 256), ("ic08", 256), ("ic14", 512),
        ("ic09", 512), ("ic10", 1024),
    ];

    private static readonly int[] LoosePngSizes = [16, 32, 48, 64, 128, 256, 512, 1024];

    private static int Main(string[] args)
    {
        var repoRoot = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        var appAssets = Path.Combine(repoRoot, "src", "Handspan.App", "Assets");
        var buildDir = Path.Combine(repoRoot, "build");
        var docsAssets = Path.Combine(repoRoot, "docs", "assets");

        foreach (var dir in new[] { appAssets, buildDir, docsAssets })
        {
            Directory.CreateDirectory(dir);
        }

        Console.WriteLine($"repo root: {repoRoot}");

        // Windows: executable and Explorer icon.
        var ico = Path.Combine(appAssets, "handspan.ico");
        File.WriteAllBytes(ico, BuildIco(IcoSizes));
        Report(ico);

        // Avalonia window icon. Skia decodes PNG, so the window icon is a PNG rather than
        // the .ico even though Windows itself is happy with either.
        foreach (var size in LoosePngSizes)
        {
            var png = Path.Combine(appAssets, $"handspan-{size}.png");
            File.WriteAllBytes(png, EncodePng(size));
            Report(png);
        }

        // macOS: Info.plist already declares CFBundleIconFile = AppIcon.
        var icns = Path.Combine(buildDir, "AppIcon.icns");
        File.WriteAllBytes(icns, BuildIcns());
        Report(icns);

        // README.
        var logo = Path.Combine(docsAssets, "logo.png");
        File.WriteAllBytes(logo, EncodePng(512));
        Report(logo);

        Console.WriteLine("done");
        return 0;
    }

    private static void Report(string path)
    {
        var info = new FileInfo(path);
        Console.WriteLine($"  {info.Name,-24} {info.Length,9:N0} bytes");
    }

    /// <summary>Renders the tile and mark at one pixel size.</summary>
    private static SKBitmap Render(int size)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var s = (float)size;

        // Below about 24 px the rounded corners eat too much of the tile and the strokes look
        // thin, so both are nudged. Above it the proportions are left alone.
        var small = size <= 24;
        var radius = small ? 0.18f : CornerRadius;
        var pierWeight = small ? PierWeight * 1.08f : PierWeight;
        var deckWeight = small ? DeckWeight * 1.10f : DeckWeight;

        using var background = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(s, s),
                [GradientFrom, GradientTo],
                [0f, 1f],
                SKShaderTileMode.Clamp),
        };
        canvas.DrawRoundRect(new SKRect(0, 0, s, s), s * radius, s * radius, background);

        using var pier = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = s * pierWeight,
            StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawLine(s * PierLeft, s * 0.225f, s * PierLeft, s * 0.775f, pier);
        canvas.DrawLine(s * PierRight, s * 0.225f, s * PierRight, s * 0.775f, pier);

        using var deck = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = s * deckWeight,
            StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawLine(
            s * (PierLeft - DeckOverhang), s * 0.5f,
            s * (PierRight + DeckOverhang), s * 0.5f,
            deck);

        return bitmap;
    }

    private static byte[] EncodePng(int size)
    {
        using var bitmap = Render(size);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Writes a Vista-or-later .ico, where each entry is a whole PNG rather than a DIB.
    /// </summary>
    private static byte[] BuildIco(int[] sizes)
    {
        var images = sizes.Select(EncodePng).ToArray();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);             // reserved
        writer.Write((ushort)1);             // 1 = icon
        writer.Write((ushort)sizes.Length);

        // Directory entries are fixed width, so the first image starts after all of them.
        var offset = 6 + sizes.Length * 16;
        for (var i = 0; i < sizes.Length; i++)
        {
            // 256 is stored as 0: the field is a single byte.
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)0);           // palette size, 0 for truecolour
            writer.Write((byte)0);           // reserved
            writer.Write((ushort)1);         // colour planes
            writer.Write((ushort)32);        // bits per pixel
            writer.Write((uint)images[i].Length);
            writer.Write((uint)offset);
            offset += images[i].Length;
        }

        foreach (var image in images)
        {
            writer.Write(image);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Writes an .icns: the magic, a big-endian total length, then typed PNG entries.
    /// </summary>
    private static byte[] BuildIcns()
    {
        var entries = IcnsEntries
            .Select(entry => (entry.Type, Png: EncodePng(entry.Size)))
            .ToArray();

        var total = 8 + entries.Sum(entry => 8 + entry.Png.Length);

        using var stream = new MemoryStream(total);
        stream.Write("icns"u8);

        Span<byte> be = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(be, (uint)total);
        stream.Write(be);

        foreach (var (type, png) in entries)
        {
            stream.Write(System.Text.Encoding.ASCII.GetBytes(type));
            BinaryPrimitives.WriteUInt32BigEndian(be, (uint)(8 + png.Length));
            stream.Write(be);
            stream.Write(png);
        }

        return stream.ToArray();
    }
}
