using Handspan.Core.Models;

namespace Handspan.Core.Tests;

public class DeviceIdTests
{
    [Fact]
    public void Wireless_serials_are_recognized()
    {
        Assert.True(new DeviceId("192.168.1.42:5555").IsWireless);
        Assert.False(new DeviceId("R5CT30XXXXX").IsWireless);
    }

    [Fact]
    public void Cache_key_is_filesystem_safe()
    {
        // A wireless serial contains a colon, which is not legal in a Windows path component.
        Assert.Equal("192_168_1_42_5555", new DeviceId("192.168.1.42:5555").ToCacheKey());
        Assert.Equal("R5CT30ABCDE", new DeviceId("R5CT30ABCDE").ToCacheKey());
    }

    [Fact]
    public void Distinct_devices_never_share_identity()
    {
        // Spec §39: cache collisions between two connected phones are the bug this prevents.
        Assert.NotEqual(new DeviceId("deviceA"), new DeviceId("deviceB"));
        Assert.NotEqual(new DeviceId("deviceA").ToCacheKey(), new DeviceId("deviceB").ToCacheKey());
    }
}

public class MediaTypesTests
{
    [Theory]
    [InlineData(".jpg", MediaKind.Image)]
    [InlineData(".JPEG", MediaKind.Image)]
    [InlineData(".heic", MediaKind.Image)]
    [InlineData(".avif", MediaKind.Image)]
    [InlineData(".mp4", MediaKind.Video)]
    [InlineData(".mkv", MediaKind.Video)]
    [InlineData(".3gp", MediaKind.Video)]
    [InlineData(".opus", MediaKind.Audio)]
    [InlineData(".flac", MediaKind.Audio)]
    [InlineData(".pdf", MediaKind.Document)]
    [InlineData(".apk", MediaKind.None)]
    [InlineData("", MediaKind.None)]
    public void Classifies_by_extension(string extension, MediaKind expected)
        => Assert.Equal(expected, MediaTypes.FromExtension(extension));

    [Fact]
    public void Camera_photos_are_candidates_for_cheap_thumbnail_extraction()
    {
        // Spec §4.1 tier T1/T2: these formats usually carry an embedded thumbnail, which is what
        // keeps the gallery from pulling full-size files.
        Assert.True(MediaTypes.MayHaveEmbeddedThumbnail(".jpg"));
        Assert.True(MediaTypes.MayHaveEmbeddedThumbnail(".HEIC"));
        Assert.False(MediaTypes.MayHaveEmbeddedThumbnail(".png"));
        Assert.False(MediaTypes.MayHaveEmbeddedThumbnail(".mp4"));
    }
}

public class TransferJobTests
{
    private static TransferJob Job(long total, long done, TransferStatus status) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = new DeviceId("test"),
        Direction = TransferDirection.Download,
        RemotePath = KnownPaths.Camera.Combine("IMG_0001.jpg"),
        LocalPath = @"C:\Pictures\IMG_0001.jpg",
        TotalBytes = total,
        BytesTransferred = done,
        Status = status,
    };

    [Fact]
    public void Fraction_is_clamped_and_safe_at_zero_length()
    {
        Assert.Equal(0.5, Job(1000, 500, TransferStatus.Transferring).Fraction);
        Assert.Equal(0, Job(0, 0, TransferStatus.Queued).Fraction);
        Assert.Equal(1, Job(1000, 5000, TransferStatus.Completed).Fraction);
    }

    [Fact]
    public void Terminal_states()
    {
        Assert.True(Job(1, 1, TransferStatus.Completed).IsTerminal);
        Assert.True(Job(1, 0, TransferStatus.Failed).IsTerminal);
        Assert.True(Job(1, 0, TransferStatus.Cancelled).IsTerminal);
        Assert.False(Job(1, 0, TransferStatus.Paused).IsTerminal);
        Assert.False(Job(1, 0, TransferStatus.Retrying).IsTerminal);
    }

    [Fact]
    public void Only_partially_transferred_jobs_are_resumable()
    {
        Assert.True(Job(1000, 400, TransferStatus.Paused).IsResumable);
        Assert.False(Job(1000, 0, TransferStatus.Paused).IsResumable);
        Assert.False(Job(1000, 400, TransferStatus.Completed).IsResumable);
    }

    [Fact]
    public void Progress_estimates_remaining_time()
    {
        var progress = new TransferProgress
        {
            BytesTransferred = 1_000_000,
            TotalBytes = 3_000_000,
            BytesPerSecond = 1_000_000,
        };

        Assert.Equal(TimeSpan.FromSeconds(2), progress.EstimatedRemaining);
        Assert.Null(new TransferProgress { BytesTransferred = 0, TotalBytes = 100 }.EstimatedRemaining);
    }
}

public class DeviceInfoTests
{
    [Theory]
    [InlineData("Samsung", "Galaxy S25", "Samsung Galaxy S25")]
    [InlineData("Google", "Pixel 10", "Google Pixel 10")]
    [InlineData("samsung", "samsung SM-S931B", "samsung SM-S931B")] // avoids "samsung samsung ..."
    [InlineData(null, "Pixel 10", "Pixel 10")]
    [InlineData("Google", null, "Google")]
    public void Display_name_combines_manufacturer_and_model(string? manufacturer, string? model, string expected)
    {
        var info = new DeviceInfo
        {
            Id = new DeviceId("serial"),
            State = DeviceState.Online,
            Manufacturer = manufacturer,
            Model = model,
        };

        Assert.Equal(expected, info.DisplayName);
    }

    [Fact]
    public void Falls_back_to_serial_then_honours_user_override()
    {
        var info = new DeviceInfo { Id = new DeviceId("R5CT30ABCDE"), State = DeviceState.Online };
        Assert.Equal("R5CT30ABCDE", info.DisplayName);

        Assert.Equal("Work phone", (info with { DisplayNameOverride = "Work phone" }).DisplayName);
    }

    [Theory]
    [InlineData(DeviceState.Online, true)]
    [InlineData(DeviceState.Unauthorized, false)]
    [InlineData(DeviceState.Offline, false)]
    [InlineData(DeviceState.Unknown, false)]
    public void Only_online_devices_are_usable(DeviceState state, bool usable)
        => Assert.Equal(usable, new DeviceInfo { Id = new DeviceId("s"), State = state }.IsUsable);

    [Fact]
    public void Storage_reports_usage()
    {
        var storage = new StorageInfo
        {
            Root = KnownPaths.InternalStorage,
            TotalBytes = 256_000_000_000,
            FreeBytes = 82_000_000_000,
        };

        Assert.Equal(174_000_000_000, storage.UsedBytes);
        Assert.InRange(storage.UsedFraction, 0.67, 0.69);
    }
}

public class DeviceFileInfoTests
{
    [Theory]
    [InlineData(0b111_101_101, "rwxr-xr-x")]
    [InlineData(0b110_100_100, "rw-r--r--")]
    [InlineData(0b111_111_111, "rwxrwxrwx")]
    [InlineData(0, "---------")]
    public void Formats_posix_permissions(int mode, string expected)
    {
        var info = new DeviceFileInfo
        {
            DeviceId = new DeviceId("s"),
            Path = KnownPaths.Camera,
            Kind = DeviceEntryKind.Directory,
            Mode = mode,
        };

        Assert.Equal(expected, info.PermissionString);
    }

    [Fact]
    public void Entries_flag_unknown_sizes_rather_than_lying()
    {
        // Spec: a 32-bit size field saturates above 4 GiB, and showing the truncated value would be
        // worse than showing nothing.
        var entry = new DeviceEntry
        {
            DeviceId = new DeviceId("s"),
            Path = KnownPaths.Movies.Combine("holiday-4k.mp4"),
            Kind = DeviceEntryKind.File,
            Size = uint.MaxValue,
            IsSizeKnown = false,
        };

        Assert.False(entry.IsSizeKnown);
        Assert.False(entry.IsDirectory);
        Assert.Equal(".mp4", entry.Extension);
    }

    [Fact]
    public void Dotfiles_are_hidden()
    {
        DeviceEntry Entry(string name) => new()
        {
            DeviceId = new DeviceId("s"),
            Path = KnownPaths.InternalStorage.Combine(name),
            Kind = DeviceEntryKind.File,
        };

        Assert.True(Entry(".nomedia").IsHidden);
        Assert.False(Entry("photo.jpg").IsHidden);
    }
}
