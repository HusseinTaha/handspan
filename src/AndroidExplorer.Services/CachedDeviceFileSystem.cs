using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;

namespace AndroidExplorer.Services;

/// <summary>
/// Wraps a device filesystem so that listings are cached and mutations invalidate them (spec §29).
/// </summary>
/// <remarks>
/// Invalidation lives here rather than at the call sites on purpose: a forgotten invalidation shows
/// the user a folder that no longer matches the device, and that bug is nearly invisible in review.
/// Reads still go to the device — the instant-render path is <see cref="ICacheService"/>, which the
/// UI consults first and then patches from the fresh listing.
/// </remarks>
internal sealed class CachedDeviceFileSystem(IDeviceFileSystem inner, ICacheService cache)
    : IDeviceFileSystem
{
    public DeviceId DeviceId => inner.DeviceId;

    public async Task<IReadOnlyList<DeviceEntry>> ListAsync(
        DevicePath path,
        CancellationToken cancellationToken)
    {
        var entries = await inner.ListAsync(path, cancellationToken).ConfigureAwait(false);
        await cache.SetListingAsync(DeviceId, path, entries, cancellationToken).ConfigureAwait(false);
        return entries;
    }

    public Task<DeviceFileInfo> GetInfoAsync(DevicePath path, CancellationToken cancellationToken)
        => inner.GetInfoAsync(path, cancellationToken);

    public Task<bool> ExistsAsync(DevicePath path, CancellationToken cancellationToken)
        => inner.ExistsAsync(path, cancellationToken);

    public Task<Stream> OpenReadAsync(DevicePath path, CancellationToken cancellationToken)
        => inner.OpenReadAsync(path, cancellationToken);

    public Task<byte[]> ReadRangeAsync(
        DevicePath path,
        long offset,
        int count,
        CancellationToken cancellationToken)
        => inner.ReadRangeAsync(path, offset, count, cancellationToken);

    public async Task UploadAsync(
        Stream source,
        DevicePath destination,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        await inner.UploadAsync(source, destination, progress, cancellationToken).ConfigureAwait(false);
        await InvalidateParentAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public Task DownloadAsync(
        DevicePath source,
        Stream destination,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
        => inner.DownloadAsync(source, destination, progress, cancellationToken);

    public Task DownloadRangeAsync(
        DevicePath source,
        long startOffset,
        Stream destination,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
        => inner.DownloadRangeAsync(source, startOffset, destination, progress, cancellationToken);

    public async Task UploadRangeAsync(
        Stream source,
        DevicePath destination,
        long startOffset,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        await inner.UploadRangeAsync(source, destination, startOffset, progress, cancellationToken)
            .ConfigureAwait(false);
        await InvalidateParentAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(DevicePath path, CancellationToken cancellationToken)
    {
        await inner.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        await InvalidateParentAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(DevicePath path, bool recursive, CancellationToken cancellationToken)
    {
        await inner.DeleteAsync(path, recursive, cancellationToken).ConfigureAwait(false);

        // Both the parent listing and anything cached beneath a deleted directory are now wrong.
        await InvalidateParentAsync(path, cancellationToken).ConfigureAwait(false);
        await cache.InvalidateAsync(DeviceId, path, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameAsync(
        DevicePath source,
        DevicePath destination,
        CancellationToken cancellationToken)
    {
        await inner.RenameAsync(source, destination, cancellationToken).ConfigureAwait(false);

        await InvalidateParentAsync(source, cancellationToken).ConfigureAwait(false);
        await InvalidateParentAsync(destination, cancellationToken).ConfigureAwait(false);
        await cache.InvalidateAsync(DeviceId, source, cancellationToken).ConfigureAwait(false);
    }

    public async Task CopyAsync(
        DevicePath source,
        DevicePath destination,
        CancellationToken cancellationToken)
    {
        await inner.CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
        await InvalidateParentAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<StorageInfo>> GetStorageAsync(CancellationToken cancellationToken)
        => inner.GetStorageAsync(cancellationToken);

    public Task<string> ComputeSha256Async(DevicePath path, CancellationToken cancellationToken)
        => inner.ComputeSha256Async(path, cancellationToken);

    private Task InvalidateParentAsync(DevicePath path, CancellationToken cancellationToken)
        => cache.InvalidateAsync(DeviceId, path.Parent, cancellationToken);
}
