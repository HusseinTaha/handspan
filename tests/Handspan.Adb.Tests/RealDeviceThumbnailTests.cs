using System.Diagnostics;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Handspan.Adb.Tests;

/// <summary>
/// The thumbnail path against real photos (spec §21, §94).
/// </summary>
/// <remarks>
/// The gallery's whole design rests on one claim: a thumbnail can be produced from a small prefix of a photo
/// instead of the whole file. That claim has been verified against synthetic JPEGs built in a test; this
/// verifies it against whatever the phone actually shot, which is the only evidence that counts.
/// </remarks>
public class RealDeviceThumbnailTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static DeviceId Device =>
        new(AdbTestEnvironment.CliDevices.First(device => device.State == "device").Serial);

    /// <summary>Finds real photos, looking in the usual camera folders.</summary>
    private static async Task<IReadOnlyList<DeviceEntry>> FindPhotosAsync(
        IAdbFileLister lister,
        CancellationToken cancellationToken)
    {
        foreach (var folder in new[] { KnownPaths.Camera, KnownPaths.Dcim, KnownPaths.Pictures })
        {
            try
            {
                var entries = await lister.ListAsync(Device, folder, cancellationToken);

                var photos = entries
                    .Where(entry => !entry.IsDirectory
                                    && MediaTypes.FromPath(entry.Path) == MediaKind.Image
                                    && entry.Size > 64 * 1024)
                    .ToList();

                if (photos.Count > 0)
                {
                    return photos;
                }
            }
            catch (Core.Exceptions.DeviceException)
            {
                // Not every device has every folder.
            }
        }

        return [];
    }

    [RequiresOnlineDeviceFact]
    public async Task A_real_photo_yields_a_thumbnail_from_a_small_prefix_read()
    {
        await using var provider = AdbTestEnvironment.BuildProvider();
        var lister = provider.GetRequiredService<IAdbFileLister>();
        var fileSystem = provider.GetRequiredService<IAdbFileSystemFactory>()
            .Create(Device, FakeServerFixture.FullCapabilities);

        using var cancellation = new CancellationTokenSource(Timeout);
        var photos = await FindPhotosAsync(lister, cancellation.Token);

        Assert.NotEmpty(photos);

        var succeeded = 0;
        var bytesRead = 0L;
        var originalBytes = 0L;

        // A handful is enough to prove the mechanism without a long test.
        foreach (var photo in photos.Take(5))
        {
            var headerLength = (int)Math.Min(
                photo.Size, EmbeddedThumbnailExtractor.RecommendedHeaderBytes);

            var header = await fileSystem.ReadRangeAsync(
                photo.Path, 0, headerLength, cancellation.Token);

            bytesRead += header.Length;
            originalBytes += photo.Size;

            var embedded = EmbeddedThumbnailExtractor.TryExtract(header);
            if (embedded is null)
            {
                continue;
            }

            var thumbnail = ImageDecoder.CreateThumbnail(embedded, 320);
            if (thumbnail is not null)
            {
                succeeded++;

                // A real thumbnail, not a stub: WebP output of a camera preview is comfortably over 1 KB.
                Assert.True(thumbnail.Length > 1024,
                    $"thumbnail was only {thumbnail.Length} bytes");
            }
        }

        Assert.True(succeeded > 0,
            $"no embedded thumbnail could be extracted from {photos.Count} photos on this device");

        // The claim the gallery depends on: far less than the originals were transferred.
        Assert.True(bytesRead < originalBytes / 2,
            $"read {bytesRead:N0} bytes to thumbnail {originalBytes:N0} bytes of photos — "
            + "the prefix-read optimisation is not working");
    }

    [RequiresOnlineDeviceFact]
    public async Task Range_reads_return_the_same_bytes_as_a_full_read()
    {
        await using var provider = AdbTestEnvironment.BuildProvider();
        var lister = provider.GetRequiredService<IAdbFileLister>();
        var fileSystem = provider.GetRequiredService<IAdbFileSystemFactory>()
            .Create(Device, FakeServerFixture.FullCapabilities);

        using var cancellation = new CancellationTokenSource(Timeout);
        var photos = await FindPhotosAsync(lister, cancellation.Token);
        Assert.NotEmpty(photos);

        var photo = photos.First();

        // Pull the file properly, then check that ranges taken out of it agree. A wrong dd offset would
        // corrupt streamed video in a way that is very hard to notice.
        using var whole = new MemoryStream();
        await fileSystem.DownloadAsync(photo.Path, whole, null, cancellation.Token);
        var expected = whole.ToArray();

        Assert.Equal(photo.Size, expected.LongLength);

        foreach (var (offset, length) in new (long, int)[] { (0, 4096), (1024, 2048), (65_536, 8192) })
        {
            if (offset + length > expected.Length)
            {
                continue;
            }

            var range = await fileSystem.ReadRangeAsync(photo.Path, offset, length, cancellation.Token);

            Assert.Equal(length, range.Length);
            Assert.Equal(expected.AsSpan((int)offset, length).ToArray(), range);
        }
    }

    /// <summary>
    /// Measures real pull throughput — the number that justifies this project over MTP.
    /// </summary>
    [RequiresOnlineDeviceFact]
    public async Task Measures_pull_throughput_on_real_hardware()
    {
        await using var provider = AdbTestEnvironment.BuildProvider();
        var lister = provider.GetRequiredService<IAdbFileLister>();
        var fileSystem = provider.GetRequiredService<IAdbFileSystemFactory>()
            .Create(Device, FakeServerFixture.FullCapabilities);

        using var cancellation = new CancellationTokenSource(Timeout);

        // Prefer the largest thing available, so the measurement is not dominated by round-trip overhead.
        var candidates = new List<DeviceEntry>();
        foreach (var folder in new[] { KnownPaths.Camera, KnownPaths.Movies, KnownPaths.Download })
        {
            try
            {
                candidates.AddRange((await lister.ListAsync(Device, folder, cancellation.Token))
                    .Where(entry => !entry.IsDirectory && entry.IsSizeKnown));
            }
            catch (Core.Exceptions.DeviceException)
            {
            }
        }

        // Big enough that round-trip overhead does not dominate, small enough to keep the test quick and to
        // stay within what a single buffer could hold — phones routinely carry multi-gigabyte videos.
        const long minimumBytes = 2L * 1024 * 1024;
        const long maximumBytes = 200L * 1024 * 1024;

        var subject = candidates
            .Where(entry => entry.Size is >= minimumBytes and <= maximumBytes)
            .OrderByDescending(entry => entry.Size)
            .FirstOrDefault();

        if (subject is null)
        {
            // Not a failure: a device with nothing in that range simply cannot be measured this way.
            Console.WriteLine("THROUGHPUT: no file between 2 MB and 200 MB to measure with.");
            return;
        }

        // Stream to disk rather than memory, which is what a real download does anyway.
        var temporary = Path.Combine(Path.GetTempPath(), $"ae-throughput-{Guid.NewGuid():N}.bin");

        try
        {
            var stopwatch = Stopwatch.StartNew();

            await using (var sink = new FileStream(
                             temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                await fileSystem.DownloadAsync(subject.Path, sink, null, cancellation.Token);
            }

            stopwatch.Stop();

            Assert.Equal(subject.Size, new FileInfo(temporary).Length);

            var megabytes = subject.Size / (1024.0 * 1024.0);
            var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);

            // Recorded rather than asserted: throughput depends on cable, port and device, so a threshold
            // here would be a flaky test rather than a useful one.
            Console.WriteLine(
                $"THROUGHPUT: pulled {megabytes:N1} MB in {seconds:N2}s = {megabytes / seconds:N1} MB/s");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
