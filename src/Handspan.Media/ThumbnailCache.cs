using System.Security.Cryptography;
using System.Text;
using Handspan.Core.Models;
using Microsoft.Extensions.Logging;

namespace Handspan.Media;

/// <summary>
/// On-disk thumbnail cache, keyed by device, path, size and modified time (spec §21).
/// </summary>
/// <remarks>
/// Including size and modified time in the key means an edited or replaced photo regenerates its
/// thumbnail automatically rather than showing a stale one — no invalidation logic needed anywhere else.
/// Files are laid out per device so clearing one device leaves the others intact (spec §39).
/// </remarks>
public sealed class ThumbnailCache(string rootDirectory, ILogger<ThumbnailCache> logger)
{
    public string RootDirectory { get; } = rootDirectory;

    /// <summary>Cache file path for an item at a given size. The size is part of the key.</summary>
    public string GetPath(DeviceId device, string thumbnailKey, int maxEdgePixels)
    {
        var hash = Convert.ToHexString(
            SHA1.HashData(Encoding.UTF8.GetBytes($"{thumbnailKey}|{maxEdgePixels}"))).ToLowerInvariant();

        return Path.Combine(RootDirectory, device.ToCacheKey(), $"{hash}.webp");
    }

    public async Task<byte[]?> TryReadAsync(
        DeviceId device,
        string thumbnailKey,
        int maxEdgePixels,
        CancellationToken cancellationToken)
    {
        var path = GetPath(device, thumbnailKey, maxEdgePixels);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // Touch the file so eviction can approximate least-recently-used.
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);

            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A damaged cache entry is regenerated, never fatal.
            logger.LogDebug(ex, "Could not read a cached thumbnail.");
            return null;
        }
    }

    public async Task WriteAsync(
        DeviceId device,
        string thumbnailKey,
        int maxEdgePixels,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var path = GetPath(device, thumbnailKey, maxEdgePixels);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write via a temporary file so a crash cannot leave a truncated thumbnail behind.
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not write a cached thumbnail.");
        }
    }

    public long GetSizeBytes()
    {
        try
        {
            return Directory.Exists(RootDirectory)
                ? new DirectoryInfo(RootDirectory)
                    .EnumerateFiles("*.webp", SearchOption.AllDirectories)
                    .Sum(file => file.Length)
                : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Evicts least-recently-used entries until the cache is under its cap (spec §50).</summary>
    public void Trim(long capBytes)
    {
        try
        {
            if (!Directory.Exists(RootDirectory))
            {
                return;
            }

            var files = new DirectoryInfo(RootDirectory)
                .EnumerateFiles("*.webp", SearchOption.AllDirectories)
                .OrderBy(file => file.LastAccessTimeUtc)
                .ToList();

            var total = files.Sum(file => file.Length);
            var removed = 0;

            foreach (var file in files)
            {
                if (total <= capBytes)
                {
                    break;
                }

                total -= file.Length;

                try
                {
                    file.Delete();
                    removed++;
                }
                catch (IOException)
                {
                    total += file.Length;
                }
            }

            if (removed > 0)
            {
                logger.LogInformation("Evicted {Count} cached thumbnails to stay under the cache cap.",
                    removed);
            }
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not trim the thumbnail cache.");
        }
    }

    public void Clear(DeviceId? device)
    {
        try
        {
            var target = device is { } id
                ? Path.Combine(RootDirectory, id.ToCacheKey())
                : RootDirectory;

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not clear the thumbnail cache.");
        }
    }
}
