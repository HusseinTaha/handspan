using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Data;
using Microsoft.Extensions.Logging;

namespace Handspan.Search;

/// <summary>
/// Explains what is using the device's storage (spec §62, §63).
/// </summary>
/// <remarks>
/// Everything comes from the index, so it is instant after a crawl. The one honest complication is that the
/// index only covers storage we can read: the difference between the volume's used bytes and the indexed
/// total is reported as unaccounted rather than attributed to a guess.
/// </remarks>
public sealed class StorageAnalyzer(
    DeviceId device,
    IDeviceFileSystem fileSystem,
    IFileIndexStore index,
    ILogger<StorageAnalyzer> logger) : IStorageAnalyzer
{
    public async Task<StorageBreakdown> AnalyzeAsync(CancellationToken cancellationToken)
    {
        var categories = await index.AggregateByKindAsync(device, cancellationToken).ConfigureAwait(false);
        var (bytes, files) = await index.TotalsAsync(device, cancellationToken).ConfigureAwait(false);

        StorageInfo? volume = null;
        try
        {
            var volumes = await fileSystem.GetStorageAsync(cancellationToken).ConfigureAwait(false);
            volume = volumes.FirstOrDefault(candidate => !candidate.IsRemovable);
        }
        catch (DeviceException ex)
        {
            // Without volume totals the breakdown still works; it just cannot show the unaccounted share.
            logger.LogDebug("Volume capacity unavailable: {Reason}", ex.UserMessage);
        }

        return new StorageBreakdown
        {
            DeviceId = device,
            Volume = volume,
            Categories = categories,
            IndexedBytes = bytes,
            IndexedFiles = files,
        };
    }

    public Task<IReadOnlyList<DeviceEntry>> GetLargestFilesAsync(
        int count,
        long minimumBytes,
        CancellationToken cancellationToken)
        => index.LargestFilesAsync(device, count, minimumBytes, cancellationToken);

    public Task<IReadOnlyList<StorageFolder>> GetFolderBreakdownAsync(
        DevicePath parent,
        CancellationToken cancellationToken)
        => index.FolderBreakdownAsync(device, parent, cancellationToken);
}
