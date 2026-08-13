using System.Collections.Concurrent;
using AndroidExplorer.Core.Exceptions;
using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Media;

/// <summary>Creates a thumbnail service per device session.</summary>
public interface IThumbnailServiceFactory
{
    IThumbnailService Create(DeviceId device, IDeviceFileSystem fileSystem);
}

public sealed class ThumbnailServiceFactory(
    ThumbnailCache cache,
    ISettingsService settings,
    ILoggerFactory loggers) : IThumbnailServiceFactory
{
    public IThumbnailService Create(DeviceId device, IDeviceFileSystem fileSystem)
        => new ThumbnailService(device, fileSystem, cache, settings,
            loggers.CreateLogger<ThumbnailService>());
}

/// <summary>
/// Produces thumbnails using the cheapest tier that works (spec §21, §94).
/// </summary>
/// <remarks>
/// <para>
/// The rule this class exists to enforce: <b>never pull a full-size file just to draw a grid cell.</b>
/// Tier 1 reads a bounded header and extracts the embedded preview — a 5 MB photo costs tens of KB. Tier 2
/// pulls the whole file, but only below a configurable threshold. Anything larger gets a type icon, so one
/// pathological file cannot stall the grid.
/// </para>
/// <para>
/// Video frames (tier 3 in the plan) need ffmpeg and land in phase 4b; until then videos report null and
/// the UI shows a film icon.
/// </para>
/// </remarks>
public sealed class ThumbnailService(
    DeviceId device,
    IDeviceFileSystem fileSystem,
    ThumbnailCache cache,
    ISettingsService settingsService,
    ILogger<ThumbnailService> logger) : IThumbnailService
{
    /// <summary>Read per use so a changed thumbnail size or cache cap applies without a restart.</summary>
    private AppSettings settings => settingsService.Current;

    /// <summary>Bounded so the grid cannot queue thousands of concurrent device reads.</summary>
    private readonly SemaphoreSlim _workers = new(4, 4);

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();

    /// <summary>Items known to have no usable thumbnail, so the work is not repeated on every scroll.</summary>
    private readonly ConcurrentDictionary<string, bool> _hopeless = new();

    public async Task<byte[]?> GetThumbnailAsync(
        MediaItem item,
        int maxEdgePixels,
        CancellationToken cancellationToken)
    {
        var key = item.ThumbnailKey;

        var cached = await cache.TryReadAsync(device, key, maxEdgePixels, cancellationToken)
            .ConfigureAwait(false);

        if (cached is not null)
        {
            return cached;
        }

        if (_hopeless.ContainsKey(key))
        {
            return null;
        }

        var registration = new CancellationTokenSource();
        _pending[key] = registration;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, registration.Token);

        await _workers.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            // Another worker may have produced it while this one queued.
            cached = await cache.TryReadAsync(device, key, maxEdgePixels, linked.Token)
                .ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            var generated = await GenerateAsync(item, maxEdgePixels, linked.Token).ConfigureAwait(false);

            if (generated is null)
            {
                _hopeless[key] = true;
                return null;
            }

            await cache.WriteAsync(device, key, maxEdgePixels, generated, linked.Token)
                .ConfigureAwait(false);

            return generated;
        }
        catch (OperationCanceledException)
        {
            // Scrolled out of view, or the caller gave up. Not an error.
            return null;
        }
        catch (DeviceException ex)
        {
            // Informational, not debug: if every thumbnail is failing the user needs the log to say why.
            logger.LogInformation("Could not build a thumbnail: {Reason}", ex.UserMessage);
            return null;
        }
        finally
        {
            _workers.Release();
            _pending.TryRemove(key, out _);
            registration.Dispose();
        }
    }

    private async Task<byte[]?> GenerateAsync(
        MediaItem item,
        int maxEdgePixels,
        CancellationToken cancellationToken)
    {
        if (item.Kind is not (MediaKind.Image or MediaKind.Video))
        {
            return null;
        }

        // Tier 1: the embedded preview inside a bounded header read. The whole point of the design.
        if (item.Kind == MediaKind.Image
            && MediaTypes.MayHaveEmbeddedThumbnail(item.Path.Extension))
        {
            var headerLength = (int)Math.Min(item.Size, EmbeddedThumbnailExtractor.RecommendedHeaderBytes);

            var header = await fileSystem
                .ReadRangeAsync(item.Path, 0, headerLength, cancellationToken)
                .ConfigureAwait(false);

            if (EmbeddedThumbnailExtractor.TryExtract(header) is { } embedded)
            {
                // Re-encode so every cache entry is a bounded WebP regardless of the source preview size.
                var reduced = ImageDecoder.CreateThumbnail(embedded, maxEdgePixels);
                if (reduced is not null)
                {
                    logger.LogTrace("Thumbnail from embedded preview: {Bytes} header bytes read.",
                        header.Length);
                    return reduced;
                }
            }
        }

        // Tier 2: decode the whole file, but only when it is small enough to be worth it.
        if (item.Kind == MediaKind.Image
            && ImageDecoder.CanDecode(item.Path.Extension)
            && item.Size <= settings.FullDecodeThresholdBytes)
        {
            using var buffer = new MemoryStream();
            await fileSystem.DownloadAsync(item.Path, buffer, null, cancellationToken)
                .ConfigureAwait(false);

            var decoded = ImageDecoder.CreateThumbnail(buffer.ToArray(), maxEdgePixels);
            if (decoded is not null)
            {
                return decoded;
            }

            // Reached the most expensive path and still got nothing — worth saying so.
            logger.LogInformation(
                "Could not decode a {Extension} image of {Size} bytes for a thumbnail.",
                item.Path.Extension, item.Size);

            return null;
        }

        // Spec §43: extension and size only, never the filename or path.
        logger.LogInformation(
            "No thumbnail available for a {Extension} file of {Size} bytes ({Reason}).",
            item.Path.Extension,
            item.Size,
            item.Kind == MediaKind.Video
                ? "video frames need the media decoder from phase 4b"
                : item.Size > settings.FullDecodeThresholdBytes
                    ? "larger than the full-decode threshold and has no embedded preview"
                    : "no embedded preview and the format cannot be decoded here");

        return null;
    }

    public void Prefetch(IReadOnlyList<MediaItem> items, int maxEdgePixels)
    {
        foreach (var item in items)
        {
            if (_pending.ContainsKey(item.ThumbnailKey) || _hopeless.ContainsKey(item.ThumbnailKey))
            {
                continue;
            }

            _ = GetThumbnailAsync(item, maxEdgePixels, CancellationToken.None);
        }
    }

    /// <summary>
    /// Abandons work for items no longer visible. Without this, fast scrolling through 10,000 photos
    /// queues 10,000 device reads and the grid never catches up (spec §22).
    /// </summary>
    public void CancelPending(IReadOnlyList<MediaItem> items)
    {
        foreach (var item in items)
        {
            if (_pending.TryGetValue(item.ThumbnailKey, out var registration))
            {
                registration.Cancel();
            }
        }
    }

    public Task<long> GetCacheSizeAsync(CancellationToken cancellationToken)
        => Task.Run(cache.GetSizeBytes, cancellationToken);

    public Task ClearCacheAsync(DeviceId? deviceId, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cache.Clear(deviceId);
            _hopeless.Clear();
        }, cancellationToken);

    /// <summary>Enforces the configured cache cap. Called after a gallery scan.</summary>
    public Task TrimCacheAsync(CancellationToken cancellationToken)
        => Task.Run(() => cache.Trim(settings.ThumbnailCacheCapBytes), cancellationToken);
}
