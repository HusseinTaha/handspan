using System.Security.Cryptography;
using AndroidExplorer.Core.Exceptions;
using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using AndroidExplorer.Data;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Search;

/// <summary>
/// Finds duplicates in increasing cost order (spec §61).
/// </summary>
/// <remarks>
/// The ordering is the design: group by size from the index (free), then compare a small head-and-tail
/// sample fetched by range read (cheap), and only then hash whole files on the device (expensive, opt-in).
/// Hashing everything over USB would take hours on a full phone, which is why the spec calls the cost order
/// out explicitly.
/// </remarks>
public sealed class DuplicateFinder(
    DeviceId device,
    IDeviceFileSystem fileSystem,
    IFileIndexStore index,
    ILogger<DuplicateFinder> logger) : IDuplicateFinder
{
    /// <summary>Bytes sampled from each end of a file for the partial hash.</summary>
    private const int SampleBytes = 64 * 1024;

    public async Task<IReadOnlyList<DuplicateGroup>> FindAsync(
        DuplicateSearchOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        // Pass 1 — free: anything unique by size cannot be a duplicate.
        var sameSize = await index.FindSameSizeGroupsAsync(
                device, options.MinimumBytes, options.Under, options.MaxGroups, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report($"{sameSize.Count} groups share a size");

        var results = new List<DuplicateGroup>();

        foreach (var (size, paths) in sameSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Pass 2 — cheap: a head-and-tail sample separates almost all coincidental size matches.
            var bySample = new Dictionary<string, List<DevicePath>>(StringComparer.Ordinal);

            foreach (var path in paths)
            {
                var sample = await TrySampleHashAsync(path, size, cancellationToken).ConfigureAwait(false);
                if (sample is null)
                {
                    continue;
                }

                if (!bySample.TryGetValue(sample, out var bucket))
                {
                    bucket = [];
                    bySample[sample] = bucket;
                }

                bucket.Add(path);
            }

            foreach (var bucket in bySample.Values.Where(bucket => bucket.Count > 1))
            {
                if (!options.VerifyWithFullHash)
                {
                    results.Add(new DuplicateGroup
                    {
                        Size = size,
                        Paths = bucket,
                        Confidence = DuplicateConfidence.PartialHash,
                    });
                    continue;
                }

                // Pass 3 — expensive and opt-in: full device-side hashes.
                progress?.Report($"Hashing {bucket.Count} candidates of {FormatBytes(size)}");

                var byFullHash = new Dictionary<string, List<DevicePath>>(StringComparer.Ordinal);

                foreach (var path in bucket)
                {
                    try
                    {
                        var hash = await fileSystem.ComputeSha256Async(path, cancellationToken)
                            .ConfigureAwait(false);

                        if (!byFullHash.TryGetValue(hash, out var confirmed))
                        {
                            confirmed = [];
                            byFullHash[hash] = confirmed;
                        }

                        confirmed.Add(path);
                    }
                    catch (DeviceException ex)
                    {
                        // A device without sha256sum falls back to the partial-hash verdict rather than
                        // failing the whole search.
                        logger.LogDebug("Full hash unavailable: {Reason}", ex.UserMessage);
                        byFullHash.Clear();
                        break;
                    }
                }

                if (byFullHash.Count == 0)
                {
                    results.Add(new DuplicateGroup
                    {
                        Size = size,
                        Paths = bucket,
                        Confidence = DuplicateConfidence.PartialHash,
                    });
                    continue;
                }

                foreach (var confirmed in byFullHash.Values.Where(group => group.Count > 1))
                {
                    results.Add(new DuplicateGroup
                    {
                        Size = size,
                        Paths = confirmed,
                        Confidence = DuplicateConfidence.FullHash,
                    });
                }
            }
        }

        logger.LogInformation("Duplicate scan found {Count} groups.", results.Count);
        return results.OrderByDescending(group => group.ReclaimableBytes).ToList();
    }

    /// <summary>
    /// Hashes the first and last <see cref="SampleBytes"/> of a file.
    /// </summary>
    /// <remarks>
    /// Both ends matter: files that share a header — the same camera, the same container format — are common,
    /// and sampling only the start would group them wrongly.
    /// </remarks>
    private async Task<string?> TrySampleHashAsync(
        DevicePath path,
        long size,
        CancellationToken cancellationToken)
    {
        try
        {
            var head = await fileSystem
                .ReadRangeAsync(path, 0, (int)Math.Min(size, SampleBytes), cancellationToken)
                .ConfigureAwait(false);

            byte[] tail = [];
            if (size > SampleBytes * 2)
            {
                // Align the tail read to a kilobyte so the range read stays on the cheap path.
                var tailOffset = (size - SampleBytes) / 1024 * 1024;
                tail = await fileSystem
                    .ReadRangeAsync(path, tailOffset, SampleBytes, cancellationToken)
                    .ConfigureAwait(false);
            }

            var buffer = new byte[head.Length + tail.Length + 8];
            head.CopyTo(buffer, 0);
            tail.CopyTo(buffer, head.Length);
            BitConverter.TryWriteBytes(buffer.AsSpan(head.Length + tail.Length), size);

            return Convert.ToHexString(SHA256.HashData(buffer));
        }
        catch (DeviceException ex)
        {
            logger.LogDebug("Could not sample a candidate: {Reason}", ex.UserMessage);
            return null;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };
}
