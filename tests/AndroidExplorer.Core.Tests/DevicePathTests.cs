using AndroidExplorer.Core.Models;

namespace AndroidExplorer.Core.Tests;

/// <summary>
/// <see cref="DevicePath"/> exists to make the C:\sdcard\DCIM class of bug impossible (spec §75),
/// and to carry filenames that Windows paths cannot (spec §74). These tests pin both properties.
/// </summary>
public class DevicePathTests
{
    [Theory]
    [InlineData("/sdcard", "/sdcard")]
    [InlineData("/sdcard/", "/sdcard")]
    [InlineData("/sdcard//DCIM///Camera", "/sdcard/DCIM/Camera")]
    [InlineData("/", "/")]
    [InlineData("///", "/")]
    [InlineData("/sdcard/./DCIM", "/sdcard/DCIM")]
    [InlineData("/sdcard/DCIM/../Pictures", "/sdcard/Pictures")]
    [InlineData("/sdcard/DCIM/Camera/../..", "/sdcard")]
    public void Parse_normalizes(string input, string expected)
        => Assert.Equal(expected, DevicePath.Parse(input).Value);

    [Theory]
    [InlineData(@"C:\sdcard\DCIM")]      // a Windows path — the whole point of the type
    [InlineData(@"C:/sdcard/DCIM")]
    [InlineData(@"\\server\share")]
    [InlineData("sdcard/DCIM")]          // relative
    [InlineData("./DCIM")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("/sdcard/../..")]        // escapes above the root
    [InlineData("/sdcard/DCIM\\Camera")] // backslash separator
    [InlineData("/sdcard/\0evil")]
    public void TryParse_rejects_invalid(string? input)
    {
        Assert.False(DevicePath.TryParse(input, out _));
        if (input is not null)
        {
            Assert.Throws<FormatException>(() => DevicePath.Parse(input));
        }
    }

    [Fact]
    public void Default_value_is_root()
    {
        DevicePath uninitialized = default;

        Assert.Equal("/", uninitialized.Value);
        Assert.True(uninitialized.IsRoot);
        Assert.Equal(DevicePath.Root, uninitialized);
    }

    [Fact]
    public void Root_is_its_own_parent()
    {
        Assert.Equal(DevicePath.Root, DevicePath.Root.Parent);
        Assert.Equal(string.Empty, DevicePath.Root.Name);
        Assert.Equal(0, DevicePath.Root.Depth);
        Assert.Empty(DevicePath.Root.Segments);
    }

    [Theory]
    [InlineData("/sdcard/DCIM/Camera/IMG_0001.jpg", "IMG_0001.jpg", ".jpg", "/sdcard/DCIM/Camera")]
    [InlineData("/sdcard/DCIM", "DCIM", "", "/sdcard")]
    [InlineData("/sdcard", "sdcard", "", "/")]
    [InlineData("/sdcard/.nomedia", ".nomedia", "", "/sdcard")]
    [InlineData("/sdcard/archive.tar.gz", "archive.tar.gz", ".gz", "/sdcard")]
    public void Name_extension_and_parent(string input, string name, string extension, string parent)
    {
        var path = DevicePath.Parse(input);

        Assert.Equal(name, path.Name);
        Assert.Equal(extension, path.Extension);
        Assert.Equal(parent, path.Parent.Value);
    }

    // Spec §74: Android filenames are not ASCII, and they are not tame.
    [Theory]
    [InlineData("صور العائلة")]
    [InlineData("照片")]
    [InlineData("日本語のファイル")]
    [InlineData("한국어")]
    [InlineData("Русский")]
    [InlineData("旅行 🌴")]
    [InlineData("file with spaces.jpg")]
    [InlineData("it's mine.jpg")]
    [InlineData("quote\".jpg")]
    [InlineData("semi;colon.jpg")]
    [InlineData("dollar$sign.jpg")]
    [InlineData("back`tick.jpg")]
    [InlineData("new\nline.jpg")]
    [InlineData("back\\slash.jpg")]
    [InlineData("-leading-dash")]
    public void Combine_accepts_real_android_filenames(string fileName)
    {
        var path = KnownPaths.Camera.Combine(fileName);

        Assert.Equal(fileName, path.Name);
        Assert.Equal(KnownPaths.Camera, path.Parent);
        Assert.Equal($"/sdcard/DCIM/Camera/{fileName}", path.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("has/slash")]
    [InlineData("has\0nul")]
    public void Combine_rejects_invalid_names(string fileName)
    {
        Assert.False(DevicePath.IsValidFileName(fileName));
        Assert.Throws<ArgumentException>(() => KnownPaths.Camera.Combine(fileName));
    }

    [Fact]
    public void Names_are_limited_to_255_utf8_bytes()
    {
        Assert.True(DevicePath.IsValidFileName(new string('a', 255)));
        Assert.False(DevicePath.IsValidFileName(new string('a', 256)));

        // Multi-byte characters count as their UTF-8 length, matching the filesystem limit.
        Assert.True(DevicePath.IsValidFileName(new string('م', 127)));   // 254 bytes
        Assert.False(DevicePath.IsValidFileName(new string('م', 128)));  // 256 bytes
    }

    [Fact]
    public void Combine_chains_segments()
    {
        var path = DevicePath.Root.Combine("sdcard", "DCIM", "Camera");

        Assert.Equal("/sdcard/DCIM/Camera", path.Value);
        Assert.Equal(3, path.Depth);
        Assert.Equal(["sdcard", "DCIM", "Camera"], path.Segments);
    }

    [Fact]
    public void Ancestry()
    {
        var camera = DevicePath.Parse("/sdcard/DCIM/Camera");
        var dcim = DevicePath.Parse("/sdcard/DCIM");

        Assert.True(dcim.IsAncestorOf(camera));
        Assert.True(camera.IsDescendantOf(dcim));
        Assert.True(DevicePath.Root.IsAncestorOf(camera));

        Assert.False(camera.IsAncestorOf(dcim));
        Assert.False(camera.IsAncestorOf(camera));
        Assert.False(DevicePath.Root.IsAncestorOf(DevicePath.Root));

        // A shared string prefix is not ancestry: /sdcard/DCIM must not "contain" /sdcard/DCIM2.
        Assert.False(dcim.IsAncestorOf(DevicePath.Parse("/sdcard/DCIM2")));
    }

    [Fact]
    public void Comparison_is_ordinal_and_case_sensitive()
    {
        // Android's underlying filesystems are case-sensitive, so these are distinct paths.
        Assert.NotEqual(DevicePath.Parse("/sdcard/DCIM"), DevicePath.Parse("/sdcard/dcim"));

        Assert.Equal(DevicePath.Parse("/sdcard/DCIM"), DevicePath.Parse("/sdcard//DCIM/"));
        Assert.Equal(
            DevicePath.Parse("/sdcard/DCIM").GetHashCode(),
            DevicePath.Parse("/sdcard//DCIM/").GetHashCode());
    }

    [Fact]
    public void Usable_as_a_dictionary_key()
    {
        var map = new Dictionary<DevicePath, int>
        {
            [DevicePath.Parse("/sdcard/DCIM")] = 1,
        };

        Assert.Equal(1, map[DevicePath.Parse("/sdcard//DCIM/")]);
        Assert.False(map.ContainsKey(DevicePath.Parse("/sdcard/dcim")));
    }

    [Fact]
    public void Protected_areas_are_recognized()
    {
        Assert.True(KnownPaths.IsProtected(DevicePath.Parse("/data")));
        Assert.True(KnownPaths.IsProtected(DevicePath.Parse("/data/data/com.example")));
        Assert.True(KnownPaths.IsProtected(DevicePath.Parse("/system/build.prop")));

        Assert.False(KnownPaths.IsProtected(KnownPaths.InternalStorage));
        Assert.False(KnownPaths.IsProtected(KnownPaths.Camera));
    }
}
