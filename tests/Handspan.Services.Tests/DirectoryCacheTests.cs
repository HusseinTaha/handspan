using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Handspan.Services.Tests;

/// <summary>
/// The directory cache against a real SQLite database (spec §29).
/// </summary>
/// <remarks>
/// Uses a temporary database file rather than a mock: the interesting failures here are SQL and
/// round-trip fidelity, which a mock would hide.
/// </remarks>
public sealed class DirectoryCacheTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"handspan-tests-{Guid.NewGuid():N}.db");

    private ICacheService _cache = null!;

    private static readonly DeviceId DeviceA = new("deviceA");
    private static readonly DeviceId DeviceB = new("deviceB");

    public Task InitializeAsync()
    {
        var database = new HandspanDatabase(
            _databasePath, NullLogger<HandspanDatabase>.Instance);

        _cache = new SqliteCacheService(database, NullLogger<SqliteCacheService>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // A stray temp file is not worth failing a test run over.
                }
            }
        }

        return Task.CompletedTask;
    }

    private static DeviceEntry Entry(
        DeviceId device,
        DevicePath parent,
        string name,
        bool isDirectory = false,
        long size = 1024,
        bool sizeKnown = true) => new()
    {
        DeviceId = device,
        Path = parent.Combine(name),
        Kind = isDirectory ? DeviceEntryKind.Directory : DeviceEntryKind.File,
        Size = size,
        IsSizeKnown = sizeKnown,
        Modified = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000),
        Mode = 0b110_100_100,
    };

    [Fact]
    public async Task A_folder_that_was_never_cached_returns_null()
    {
        // Null and empty must stay distinguishable: null means "ask the device", empty means
        // "the device says this folder is empty".
        Assert.Null(await _cache.GetListingAsync(DeviceA, KnownPaths.Camera, CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_folder_caches_as_empty_not_missing()
    {
        await _cache.SetListingAsync(DeviceA, KnownPaths.Camera, [], CancellationToken.None);

        var cached = await _cache.GetListingAsync(DeviceA, KnownPaths.Camera, CancellationToken.None);

        Assert.NotNull(cached);
        Assert.Empty(cached);
    }

    [Fact]
    public async Task Entries_round_trip_faithfully()
    {
        var entries = new[]
        {
            Entry(DeviceA, KnownPaths.Dcim, "Camera", isDirectory: true, size: 0),
            Entry(DeviceA, KnownPaths.Dcim, "IMG_0001.jpg", size: 4_812_345),
            Entry(DeviceA, KnownPaths.Dcim, "huge.mp4", size: uint.MaxValue, sizeKnown: false),
        };

        await _cache.SetListingAsync(DeviceA, KnownPaths.Dcim, entries, CancellationToken.None);
        var cached = await _cache.GetListingAsync(DeviceA, KnownPaths.Dcim, CancellationToken.None);

        Assert.NotNull(cached);
        Assert.Equal(3, cached.Count);

        var camera = cached.Single(entry => entry.Name == "Camera");
        Assert.True(camera.IsDirectory);
        Assert.Equal(KnownPaths.Camera, camera.Path);

        var photo = cached.Single(entry => entry.Name == "IMG_0001.jpg");
        Assert.Equal(4_812_345, photo.Size);
        Assert.True(photo.IsSizeKnown);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_760_000_000), photo.Modified);

        // The "size unknown" flag must survive, or the UI would show a truncated 32-bit size as fact.
        Assert.False(cached.Single(entry => entry.Name == "huge.mp4").IsSizeKnown);
    }

    [Theory]
    [InlineData("صور العائلة")]
    [InlineData("照片")]
    [InlineData("旅行 🌴.jpg")]
    [InlineData("it's mine.jpg")]
    [InlineData("new\nline.jpg")]
    [InlineData("back\\slash.jpg")]
    [InlineData("dollar$sign.jpg")]
    public async Task Unicode_and_awkward_names_round_trip_byte_exactly(string name)
    {
        var entries = new[] { Entry(DeviceA, KnownPaths.Camera, name) };

        await _cache.SetListingAsync(DeviceA, KnownPaths.Camera, entries, CancellationToken.None);
        var cached = await _cache.GetListingAsync(DeviceA, KnownPaths.Camera, CancellationToken.None);

        var entry = Assert.Single(cached!);
        Assert.Equal(name, entry.Name);
        Assert.Equal(KnownPaths.Camera.Combine(name), entry.Path);
    }

    [Fact]
    public async Task Two_devices_never_share_a_cache_entry()
    {
        // Spec §39: this collision is the reason DeviceId is in every key.
        await _cache.SetListingAsync(
            DeviceA, KnownPaths.Camera, [Entry(DeviceA, KnownPaths.Camera, "from-A.jpg")],
            CancellationToken.None);

        await _cache.SetListingAsync(
            DeviceB, KnownPaths.Camera, [Entry(DeviceB, KnownPaths.Camera, "from-B.jpg")],
            CancellationToken.None);

        var fromA = await _cache.GetListingAsync(DeviceA, KnownPaths.Camera, CancellationToken.None);
        var fromB = await _cache.GetListingAsync(DeviceB, KnownPaths.Camera, CancellationToken.None);

        Assert.Equal("from-A.jpg", Assert.Single(fromA!).Name);
        Assert.Equal("from-B.jpg", Assert.Single(fromB!).Name);
        Assert.Equal(DeviceA, Assert.Single(fromA!).DeviceId);
        Assert.Equal(DeviceB, Assert.Single(fromB!).DeviceId);
    }

    [Fact]
    public async Task Re_caching_a_folder_replaces_rather_than_accumulates()
    {
        await _cache.SetListingAsync(
            DeviceA, KnownPaths.Camera,
            [Entry(DeviceA, KnownPaths.Camera, "old.jpg")], CancellationToken.None);

        await _cache.SetListingAsync(
            DeviceA, KnownPaths.Camera,
            [Entry(DeviceA, KnownPaths.Camera, "new.jpg")], CancellationToken.None);

        var cached = await _cache.GetListingAsync(DeviceA, KnownPaths.Camera, CancellationToken.None);

        Assert.Equal("new.jpg", Assert.Single(cached!).Name);
    }

    [Fact]
    public async Task Invalidating_a_folder_returns_it_to_unknown()
    {
        await _cache.SetListingAsync(
            DeviceA, KnownPaths.Camera,
            [Entry(DeviceA, KnownPaths.Camera, "photo.jpg")], CancellationToken.None);

        await _cache.InvalidateAsync(DeviceA, KnownPaths.Camera, CancellationToken.None);

        // Back to null, not empty: the next navigation must read the device.
        Assert.Null(await _cache.GetListingAsync(DeviceA, KnownPaths.Camera, CancellationToken.None));
    }

    [Fact]
    public async Task Clearing_one_device_leaves_the_other_intact()
    {
        await _cache.SetListingAsync(
            DeviceA, KnownPaths.Camera,
            [Entry(DeviceA, KnownPaths.Camera, "a.jpg")], CancellationToken.None);
        await _cache.SetListingAsync(
            DeviceB, KnownPaths.Camera,
            [Entry(DeviceB, KnownPaths.Camera, "b.jpg")], CancellationToken.None);

        await _cache.InvalidateDeviceAsync(DeviceA, CancellationToken.None);

        Assert.Null(await _cache.GetListingAsync(DeviceA, KnownPaths.Camera, CancellationToken.None));
        Assert.NotNull(await _cache.GetListingAsync(DeviceB, KnownPaths.Camera, CancellationToken.None));
    }

    [Fact]
    public async Task Sibling_folders_are_cached_independently()
    {
        await _cache.SetListingAsync(
            DeviceA, KnownPaths.Camera,
            [Entry(DeviceA, KnownPaths.Camera, "in-camera.jpg")], CancellationToken.None);
        await _cache.SetListingAsync(
            DeviceA, KnownPaths.Download,
            [Entry(DeviceA, KnownPaths.Download, "in-download.pdf")], CancellationToken.None);

        Assert.Equal("in-camera.jpg",
            Assert.Single((await _cache.GetListingAsync(DeviceA, KnownPaths.Camera, CancellationToken.None))!).Name);
        Assert.Equal("in-download.pdf",
            Assert.Single((await _cache.GetListingAsync(DeviceA, KnownPaths.Download, CancellationToken.None))!).Name);
    }

    [Fact]
    public async Task A_large_listing_round_trips()
    {
        // Spec §45 expects folders with 10,000+ entries; caching must not choke on them.
        var entries = Enumerable.Range(0, 5_000)
            .Select(i => Entry(DeviceA, KnownPaths.Camera, $"IMG_{i:D5}.jpg", size: i))
            .ToArray();

        await _cache.SetListingAsync(DeviceA, KnownPaths.Camera, entries, CancellationToken.None);
        var cached = await _cache.GetListingAsync(DeviceA, KnownPaths.Camera, CancellationToken.None);

        Assert.Equal(5_000, cached!.Count);
        Assert.Equal(4_999, cached.Max(entry => entry.Size));
    }
}
