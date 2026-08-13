using AndroidExplorer.Core.Models;

namespace AndroidExplorer.Core.Interfaces;

/// <summary>
/// The virtual device filesystem — "the most important abstraction in the application" (spec §15).
/// </summary>
/// <remarks>
/// <para>
/// Nothing above this interface knows whether the bytes come from ADB, an Android companion app, or
/// anything else (spec §2). Keeping that true is what makes new transports an addition rather than
/// a rewrite.
/// </para>
/// <para>
/// Every method is asynchronous and cancellable (spec §46, §47), and every path is a
/// <see cref="DevicePath"/> (spec §75).
/// </para>
/// </remarks>
public interface IDeviceFileSystem
{
    DeviceId DeviceId { get; }

    /// <summary>Lists a directory's immediate children.</summary>
    Task<IReadOnlyList<DeviceEntry>> ListAsync(DevicePath path, CancellationToken cancellationToken);

    /// <summary>Stats a single path.</summary>
    Task<DeviceFileInfo> GetInfoAsync(DevicePath path, CancellationToken cancellationToken);

    /// <summary>True when the path exists, without throwing if it does not.</summary>
    Task<bool> ExistsAsync(DevicePath path, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a file for reading. The returned stream is seekable where
    /// <see cref="DeviceCapabilities.CanStream"/> is set, which the thumbnail and video paths rely on.
    /// </summary>
    Task<Stream> OpenReadAsync(DevicePath path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a byte range without transferring the whole file.
    /// </summary>
    /// <remarks>
    /// The basis of cheap thumbnails: a camera JPEG's embedded thumbnail lives in its first ~128 KB, so
    /// the gallery can show a 5 MB photo having moved about 1% of it (spec §21, §94). Returns fewer bytes
    /// than requested at end of file.
    /// </remarks>
    Task<byte[]> ReadRangeAsync(
        DevicePath path,
        long offset,
        int count,
        CancellationToken cancellationToken);

    /// <summary>Copies a local stream to the device.</summary>
    Task UploadAsync(
        Stream source,
        DevicePath destination,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>Copies a device file into a local stream.</summary>
    Task DownloadAsync(
        DevicePath source,
        Stream destination,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resumes an interrupted download, reading from <paramref name="startOffset"/> onward.
    /// </summary>
    /// <remarks>
    /// The offset must be 1 MiB-aligned: alignment lets the transport use only baseline
    /// <c>dd</c> semantics, which is what makes resume work across OEM toybox variations.
    /// </remarks>
    Task DownloadRangeAsync(
        DevicePath source,
        long startOffset,
        Stream destination,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>Resumes an interrupted upload, writing at <paramref name="startOffset"/> onward.</summary>
    Task UploadRangeAsync(
        Stream source,
        DevicePath destination,
        long startOffset,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken);

    Task CreateDirectoryAsync(DevicePath path, CancellationToken cancellationToken);

    /// <summary>Deletes a file, or a directory and its contents when <paramref name="recursive"/> is set.</summary>
    Task DeleteAsync(DevicePath path, bool recursive, CancellationToken cancellationToken);

    /// <summary>Renames or moves within the device. No data crosses the connection.</summary>
    Task RenameAsync(DevicePath source, DevicePath destination, CancellationToken cancellationToken);

    /// <summary>Copies within the device. No data crosses the connection.</summary>
    Task CopyAsync(DevicePath source, DevicePath destination, CancellationToken cancellationToken);

    /// <summary>Storage volumes visible on the device (spec §6).</summary>
    Task<IReadOnlyList<StorageInfo>> GetStorageAsync(CancellationToken cancellationToken);

    /// <summary>Computes a SHA-256 on the device, for optional transfer verification (spec §37).</summary>
    Task<string> ComputeSha256Async(DevicePath path, CancellationToken cancellationToken);
}
