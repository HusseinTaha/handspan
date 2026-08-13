using System.Diagnostics;
using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using AndroidExplorer.Data;
using AndroidExplorer.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AndroidExplorer.Adb.Tests;

/// <summary>
/// The gallery pipeline end to end against a real phone (spec §18–§26, §94).
/// </summary>
/// <remarks>
/// The pieces have been tested separately — extraction against hand-built JPEGs, scanning against a fake
/// filesystem — but the pipeline as a whole had only ever been exercised by a person looking at the screen,
/// which is how three bugs reached the user. This covers scan, index, cache and thumbnail generation together
/// on real photos.
/// </remarks>
public sealed class RealDeviceGalleryTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ae-gallery-{Guid.NewGuid():N}");

    private ServiceProvider? _provider;
    private IDeviceFileSystem? _fileSystem;
    private IMediaIndexStore? _index;
    private ThumbnailCache? _cache;

    private bool Available => _provider is not null;

    public Task InitializeAsync()
    {
        if (!AdbTestEnvironment.HasOnlineDevice)
        {
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(_root);
        _provider = AdbTestEnvironment.BuildProvider();

        var device = new DeviceId(
            AdbTestEnvironment.CliDevices.First(candidate => candidate.State == "device").Serial);

        _fileSystem = _provider.GetRequiredService<IAdbFileSystemFactory>()
            .Create(device, FakeServerFixture.FullCapabilities);

        var database = new AndroidExplorerDatabase(
            Path.Combine(_root, "gallery.db"), NullLogger<AndroidExplorerDatabase>.Instance);

        _index = new SqliteMediaIndexStore(database, NullLogger<SqliteMediaIndexStore>.Instance);
        _cache = new ThumbnailCache(
            Path.Combine(_root, "thumbnails"), NullLogger<ThumbnailCache>.Instance);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DeviceId Device => _fileSystem!.DeviceId;

    private GalleryService CreateGallery()
        => new(Device, _fileSystem!, _index!, NullLogger<GalleryService>.Instance);

    private ThumbnailService CreateThumbnails(AppSettings? settings = null)
        => new(Device, _fileSystem!, _cache!, new FixedSettings(settings ?? new AppSettings()),
            NullLogger<ThumbnailService>.Instance);

    /// <summary>
    /// Scans the phone's real media folders and checks what lands in the index.
    /// </summary>
    [RequiresOnlineDeviceFact]
    public async Task Scanning_real_media_folders_populates_the_timeline()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);
        var gallery = CreateGallery();

        var stopwatch = Stopwatch.StartNew();
        await gallery.RefreshAsync(null, cancellation.Token);
        stopwatch.Stop();

        var indexed = await _index!.CountAsync(Device, cancellation.Token);
        Console.WriteLine(
            $"GALLERY SCAN: {indexed:N0} media items in {stopwatch.Elapsed.TotalSeconds:N1}s");

        Assert.True(indexed > 0, "the scan found no media in DCIM, Pictures, Movies or Download");

        // The timeline must come back newest-first, which is what the date grouping relies on.
        var timeline = await gallery.GetTimelineAsync(null, 0, 500, cancellation.Token);
        Assert.NotEmpty(timeline);

        var dates = timeline.Select(item => item.DateTaken ?? item.Modified).ToList();
        Assert.True(dates.SequenceEqual(dates.OrderByDescending(date => date)),
            "the timeline is not ordered newest first");

        // Every item must be classified and carry the device that produced it (spec §39).
        Assert.All(timeline, item =>
        {
            Assert.NotEqual(MediaKind.None, item.Kind);
            Assert.Equal(Device, item.DeviceId);
        });

        // The filters the UI offers must actually narrow the set.
        var photos = await gallery.GetTimelineAsync(MediaKind.Image, 0, 100, cancellation.Token);
        Assert.All(photos, item => Assert.Equal(MediaKind.Image, item.Kind));
    }

    [RequiresOnlineDeviceFact]
    public async Task Albums_are_derived_from_folders_that_hold_media()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);
        var gallery = CreateGallery();

        await gallery.RefreshAsync(null, cancellation.Token);

        var albums = await gallery.GetAlbumsAsync(cancellation.Token);

        if (albums.Count == 0)
        {
            return;
        }

        Console.WriteLine($"ALBUMS: {string.Join(" | ", albums.Take(8).Select(album => album.Name))}");

        Assert.All(albums, album =>
        {
            Assert.False(string.IsNullOrWhiteSpace(album.Name));
            Assert.True(album.ItemCount > 0);
        });

        // Real phones put the same folder name in several places — this device has Telegram under both
        // Pictures and Movies, and Screenshots under both DCIM and Pictures. Identical entries in the list
        // would leave the user unable to tell them apart.
        var duplicates = albums
            .GroupBy(album => album.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ({string.Join(", ", group.Select(album => album.Path.Value))})")
            .ToList();

        Assert.True(duplicates.Count == 0,
            "album names must be distinguishable; these collided: " + string.Join("; ", duplicates));

        // Camera is where a phone puts its photos; if the derivation works at all it should appear.
        var contents = await gallery.GetAlbumContentsAsync(albums[0].Path, cancellation.Token);
        Assert.NotEmpty(contents);
    }

    /// <summary>
    /// Generates thumbnails for real photos through the full service, including the cache.
    /// </summary>
    /// <remarks>
    /// This is the closest thing to what the gallery does when a tile scrolls into view, and it is where the
    /// per-tile bug hid: the pipeline worked while nothing ever asked it to run.
    /// </remarks>
    [RequiresOnlineDeviceFact]
    public async Task Thumbnails_are_produced_and_cached_for_real_photos()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);
        var gallery = CreateGallery();
        var thumbnails = CreateThumbnails();

        await gallery.RefreshAsync(null, cancellation.Token);

        var photos = (await gallery.GetTimelineAsync(MediaKind.Image, 0, 40, cancellation.Token))
            .Where(item => item.Size > 32 * 1024)
            .Take(12)
            .ToList();

        Assert.NotEmpty(photos);

        var produced = 0;
        var stopwatch = Stopwatch.StartNew();

        foreach (var photo in photos)
        {
            var bytes = await thumbnails.GetThumbnailAsync(photo, 320, cancellation.Token);

            if (bytes is { Length: > 0 })
            {
                produced++;

                // WebP output of a real preview is comfortably over a kilobyte; anything less is a stub.
                Assert.True(bytes.Length > 512, $"thumbnail was only {bytes.Length} bytes");
            }
        }

        stopwatch.Stop();

        Console.WriteLine(
            $"THUMBNAILS: {produced}/{photos.Count} in {stopwatch.Elapsed.TotalSeconds:N1}s "
            + $"({stopwatch.Elapsed.TotalMilliseconds / photos.Count:N0} ms each)");

        Assert.True(produced > 0,
            $"no thumbnails could be produced from {photos.Count} real photos — the gallery would show "
            + "placeholder icons for everything");

        // The second pass must come from the cache, which is what keeps scrolling affordable (spec §21).
        var cachedTimer = Stopwatch.StartNew();
        foreach (var photo in photos)
        {
            await thumbnails.GetThumbnailAsync(photo, 320, cancellation.Token);
        }

        cachedTimer.Stop();

        Console.WriteLine(
            $"THUMBNAILS (cached): {cachedTimer.Elapsed.TotalMilliseconds:N0} ms for {photos.Count}");

        Assert.True(cachedTimer.Elapsed < stopwatch.Elapsed,
            "the cached pass was not faster than generation, so the cache is not being used");

        Assert.True(_cache!.GetSizeBytes() > 0, "nothing was written to the thumbnail cache");
    }

    /// <summary>
    /// A changed file must invalidate its thumbnail, since the cache key includes size and mtime (spec §21).
    /// </summary>
    [RequiresOnlineDeviceFact]
    public async Task A_changed_file_gets_a_different_cache_key()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);
        var gallery = CreateGallery();

        await gallery.RefreshAsync(null, cancellation.Token);

        var photo = (await gallery.GetTimelineAsync(MediaKind.Image, 0, 5, cancellation.Token))
            .FirstOrDefault();

        if (photo is null)
        {
            return;
        }

        var edited = photo with { Size = photo.Size + 1 };
        var touched = photo with { Modified = photo.Modified.AddSeconds(1) };

        Assert.NotEqual(photo.ThumbnailKey, edited.ThumbnailKey);
        Assert.NotEqual(photo.ThumbnailKey, touched.ThumbnailKey);

        Assert.NotEqual(
            _cache!.GetPath(Device, photo.ThumbnailKey, 320),
            _cache.GetPath(Device, edited.ThumbnailKey, 320));
    }

    /// <summary>
    /// Streams a real video over the loopback server and checks the bytes match the device (spec §58).
    /// </summary>
    [RequiresOnlineDeviceFact]
    public async Task A_real_video_streams_over_the_loopback_server()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);
        var gallery = CreateGallery();

        await gallery.RefreshAsync(null, cancellation.Token);

        var video = (await gallery.GetTimelineAsync(MediaKind.Video, 0, 20, cancellation.Token))
            .FirstOrDefault(item => item.Size > 256 * 1024);

        if (video is null)
        {
            Console.WriteLine("STREAM: no video large enough on this device to test with.");
            return;
        }

        await using var server = new DeviceStreamServer(NullLogger<DeviceStreamServer>.Instance);
        server.Start();

        var url = server.Register(_fileSystem!, video.Path, video.Size);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };

        // A player's first move: probe with HEAD, then read a range rather than the whole file.
        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        var headResponse = await client.SendAsync(head, cancellation.Token);

        Assert.True(headResponse.IsSuccessStatusCode);
        Assert.Equal(video.Size, headResponse.Content.Headers.ContentLength);
        Assert.Contains("bytes", headResponse.Headers.AcceptRanges);

        using var ranged = new HttpRequestMessage(HttpMethod.Get, url);
        ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 65_535);

        var rangeResponse = await client.SendAsync(ranged, cancellation.Token);
        var streamed = await rangeResponse.Content.ReadAsByteArrayAsync(cancellation.Token);

        Assert.Equal(65_536, streamed.Length);

        // The streamed bytes must match what a direct device read returns.
        var direct = await _fileSystem!.ReadRangeAsync(video.Path, 0, 65_536, cancellation.Token);
        Assert.Equal(direct, streamed);

        Console.WriteLine(
            $"STREAM: served a range from a {video.Size / (1024.0 * 1024):N1} MB video without downloading it");
    }

    private sealed class FixedSettings(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; } = settings;

        public event EventHandler<AppSettings>? Changed;

        public Task LoadAsync(CancellationToken cancellationToken)
        {
            _ = Changed;
            return Task.CompletedTask;
        }

        public Task SaveAsync(AppSettings updated, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<DeviceProfile> GetProfileAsync(DeviceId deviceId, CancellationToken cancellationToken)
            => Task.FromResult(new DeviceProfile { DeviceId = deviceId });

        public Task SaveProfileAsync(DeviceProfile profile, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
