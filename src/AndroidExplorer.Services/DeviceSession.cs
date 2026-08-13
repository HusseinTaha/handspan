using AndroidExplorer.Adb;
using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using AndroidExplorer.Media;
using AndroidExplorer.Search;

namespace AndroidExplorer.Services;

/// <summary>
/// Everything scoped to one connected device (spec §70).
/// </summary>
/// <remarks>
/// Phase 1 provides identity, capabilities and structured listing. The filesystem, transfer,
/// thumbnail, gallery, search and metadata services arrive in phases 2 to 5; until then their members
/// throw rather than pretending to work, so a premature caller fails loudly instead of silently
/// doing nothing.
/// </remarks>
internal sealed class DeviceSession : IDeviceSession
{
    public DeviceSession(
        DeviceInfo info,
        IAdbFileSystemFactory fileSystemFactory,
        ICacheService cache,
        ITransferManagerFactory transfers,
        IThumbnailServiceFactory thumbnails,
        IGalleryServiceFactory gallery,
        ISearchServiceFactory search)
    {
        Info = info;

        // Caching decorator on the outside so listings are cached and mutations invalidate them.
        FileSystem = new CachedDeviceFileSystem(
            fileSystemFactory.Create(info.Id, info.Capabilities), cache);

        Transfers = transfers.Create(info.Id, FileSystem);
        Thumbnails = thumbnails.Create(info.Id, FileSystem);
        Gallery = gallery.Create(info.Id, FileSystem);
        Search = search.CreateSearch(info.Id, FileSystem);
        Storage = search.CreateStorageAnalyzer(info.Id, FileSystem);
        Duplicates = search.CreateDuplicateFinder(info.Id, FileSystem);
    }

    public DeviceId DeviceId => Info.Id;

    public DeviceInfo Info { get; private set; }

    public DeviceCapabilities Capabilities => Info.Capabilities;

    public IDeviceFileSystem FileSystem { get; }

    public ITransferManager Transfers { get; }

    public IThumbnailService Thumbnails { get; }

    public IGalleryService Gallery { get; }

    public ISearchService Search { get; }

    public IStorageAnalyzer Storage { get; }

    public IDuplicateFinder Duplicates { get; }

    public IMetadataService Metadata =>
        throw new NotSupportedException("Metadata arrives in phase 4 (docs/plan/04-gallery.md).");

    internal void UpdateInfo(DeviceInfo updated) => Info = updated;

    /// <summary>Restores journalled transfers for this device (spec §13).</summary>
    internal async Task RestoreTransfersAsync(CancellationToken cancellationToken)
    {
        if (Transfers is TransferManager manager)
        {
            await manager.RestoreAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Transfers is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}
