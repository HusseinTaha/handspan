using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Data;
using Microsoft.Extensions.Logging;

namespace Handspan.Search;

/// <summary>Creates the per-device search, storage and duplicate services.</summary>
public interface ISearchServiceFactory
{
    ISearchService CreateSearch(DeviceId device, IDeviceFileSystem fileSystem);

    IStorageAnalyzer CreateStorageAnalyzer(DeviceId device, IDeviceFileSystem fileSystem);

    IDuplicateFinder CreateDuplicateFinder(DeviceId device, IDeviceFileSystem fileSystem);
}

public sealed class SearchServiceFactory(
    IFileIndexStore index,
    ILoggerFactory loggers) : ISearchServiceFactory
{
    public ISearchService CreateSearch(DeviceId device, IDeviceFileSystem fileSystem)
        => new SearchService(device, fileSystem, index, loggers.CreateLogger<SearchService>());

    public IStorageAnalyzer CreateStorageAnalyzer(DeviceId device, IDeviceFileSystem fileSystem)
        => new StorageAnalyzer(device, fileSystem, index, loggers.CreateLogger<StorageAnalyzer>());

    public IDuplicateFinder CreateDuplicateFinder(DeviceId device, IDeviceFileSystem fileSystem)
        => new DuplicateFinder(device, fileSystem, index, loggers.CreateLogger<DuplicateFinder>());
}

/// <summary>
/// Indexed search over a device (spec §27, §28).
/// </summary>
/// <remarks>
/// Queries never touch the device — that is the point of the index. The crawl is incremental: a subtree whose
/// entries are unchanged in size and modified time is skipped, so a rescan after adding a few photos costs
/// far less than the first run.
/// </remarks>
public sealed class SearchService(
    DeviceId device,
    IDeviceFileSystem fileSystem,
    IFileIndexStore index,
    ILogger<SearchService> logger) : ISearchService
{
    /// <summary>Rows per transaction. Large enough to be fast, small enough to show progress.</summary>
    private const int BatchSize = 2000;

    /// <summary>
    /// Directories skipped by default: app-private storage is huge, permission-fraught and uninteresting
    /// (spec §5.2).
    /// </summary>
    private static readonly string[] SkippedNames = ["data", "obb", ".thumbnails", ".trashed"];

    public DeviceId DeviceId => device;

    public Task<IReadOnlyList<DeviceEntry>> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
        => index.SearchAsync(device, query, cancellationToken);

    public Task<DateTimeOffset?> GetLastIndexedAsync(CancellationToken cancellationToken)
        => index.GetLastIndexedAsync(device, cancellationToken);

    public Task<int> GetIndexedCountAsync(CancellationToken cancellationToken)
        => index.CountAsync(device, cancellationToken);

    public async Task IndexAsync(IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        var root = KnownPaths.InternalStorage;

        var batch = new List<DeviceEntry>(BatchSize);
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<DevicePath>();
        queue.Enqueue(root);

        var files = 0;
        var directories = 0;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            directories++;

            IReadOnlyList<DeviceEntry> entries;
            try
            {
                entries = await fileSystem.ListAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch (DeviceException)
            {
                // A protected or vanished folder must not abort the whole crawl (spec §78).
                continue;
            }

            foreach (var entry in entries)
            {
                seenPaths.Add(entry.Path.Value);

                if (entry.IsDirectory)
                {
                    if (!SkippedNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        queue.Enqueue(entry.Path);
                    }
                }
                else
                {
                    files++;
                }

                batch.Add(entry);

                if (batch.Count >= BatchSize)
                {
                    await index.UpsertBatchAsync(device, batch, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                    progress?.Report(new IndexProgress(files, directories, current));
                }
            }
        }

        if (batch.Count > 0)
        {
            await index.UpsertBatchAsync(device, batch, cancellationToken).ConfigureAwait(false);
        }

        // Anything indexed under this root that the crawl did not see has been deleted on the device.
        await index.RemoveMissingAsync(device, root, seenPaths, cancellationToken).ConfigureAwait(false);
        await index.MarkIndexedAsync(device, cancellationToken).ConfigureAwait(false);

        progress?.Report(new IndexProgress(files, directories, root));
        logger.LogInformation("Indexed {Files} files across {Directories} folders.", files, directories);
    }
}
