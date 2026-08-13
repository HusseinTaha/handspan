using AndroidExplorer.Core.Exceptions;
using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Adb;

/// <summary>Creates a filesystem bound to one device.</summary>
public interface IAdbFileSystemFactory
{
    IDeviceFileSystem Create(DeviceId device, DeviceCapabilities capabilities);
}

internal sealed class AdbFileSystemFactory(
    IAdbFileLister lister,
    IAdbConnectionFactory connections,
    IAdbShellClient shell,
    ILoggerFactory loggers) : IAdbFileSystemFactory
{
    public IDeviceFileSystem Create(DeviceId device, DeviceCapabilities capabilities)
        => new AdbFileSystem(
            device, capabilities, lister, connections, shell, loggers.CreateLogger<AdbFileSystem>());
}

/// <summary>
/// The ADB implementation of the virtual device filesystem (spec §15).
/// </summary>
/// <remarks>
/// Reads and transfers go through the sync protocol; mutations go through quoted shell commands,
/// because the sync protocol has no rename, delete or mkdir. Every path reaching a command line
/// passes through <see cref="ShellQuote"/> (spec §71).
/// </remarks>
internal sealed class AdbFileSystem(
    DeviceId device,
    DeviceCapabilities capabilities,
    IAdbFileLister lister,
    IAdbConnectionFactory connections,
    IAdbShellClient shell,
    ILogger<AdbFileSystem> logger) : IDeviceFileSystem
{
    /// <summary>Default permissions for uploaded files; shared storage synthesizes its own anyway.</summary>
    private const int DefaultFileMode = 0b110_100_100; // 0644

    public DeviceId DeviceId => device;

    public Task<IReadOnlyList<DeviceEntry>> ListAsync(DevicePath path, CancellationToken cancellationToken)
    {
        GuardProtected(path);
        return lister.ListAsync(device, path, cancellationToken);
    }

    public Task<DeviceFileInfo> GetInfoAsync(DevicePath path, CancellationToken cancellationToken)
    {
        GuardProtected(path);
        return lister.StatAsync(device, path, cancellationToken);
    }

    public Task<bool> ExistsAsync(DevicePath path, CancellationToken cancellationToken)
        => lister.ExistsAsync(device, path, cancellationToken);

    /// <summary>
    /// Opens a file for sequential reading, streaming it rather than buffering it locally.
    /// </summary>
    /// <remarks>
    /// Uses <c>cat</c> over <c>exec:</c> so the caller can start consuming immediately. The seekable
    /// variant needed by thumbnails and video arrives with <c>AdbRangeStream</c> in phase 4.
    /// </remarks>
    public async Task<Stream> OpenReadAsync(DevicePath path, CancellationToken cancellationToken)
    {
        GuardProtected(path);
        return await shell
            .OpenExecStreamAsync(device, $"cat {ShellQuote.Quote(path)}", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a byte range using the cheapest command that fits (spec §21).
    /// </summary>
    /// <remarks>
    /// A prefix read uses <c>head -c</c>, which every toybox has. An interior read uses <c>dd</c> with
    /// 1 KiB blocks, rounding outward and trimming locally — that keeps to baseline <c>dd</c> semantics
    /// rather than relying on <c>iflag=skip_bytes</c>, which varies by device.
    /// </remarks>
    public async Task<byte[]> ReadRangeAsync(
        DevicePath path,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        GuardProtected(path);

        if (count <= 0)
        {
            return [];
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        string command;
        int skipInBlock;

        if (offset == 0)
        {
            command = $"head -c {count} {ShellQuote.Quote(path)}";
            skipInBlock = 0;
        }
        else
        {
            const int block = 1024;
            var startBlock = offset / block;
            skipInBlock = (int)(offset - (startBlock * block));
            var blocks = (skipInBlock + count + block - 1) / block;

            command = $"dd if={ShellQuote.Quote(path)} bs={block} skip={startBlock} "
                      + $"count={blocks} 2>/dev/null";
        }

        await using var stream = await shell.OpenExecStreamAsync(device, command, cancellationToken)
            .ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var scratch = new byte[16 * 1024];
        int read;

        while ((read = await stream.ReadAsync(scratch, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(scratch, 0, read);

            if (buffer.Length >= skipInBlock + count)
            {
                break;
            }
        }

        var bytes = buffer.ToArray();

        if (skipInBlock >= bytes.Length)
        {
            return [];
        }

        var available = Math.Min(count, bytes.Length - skipInBlock);
        return bytes.AsSpan(skipInBlock, available).ToArray();
    }

    public async Task UploadAsync(
        Stream source,
        DevicePath destination,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        GuardProtected(destination);
        Require(capabilities.CanUpload, nameof(capabilities.CanUpload));

        var total = source.CanSeek ? source.Length - source.Position : 0;

        await using var sync = await AdbSyncClient.OpenAsync(connections, device, cancellationToken)
            .ConfigureAwait(false);

        await sync.SendAsync(
                source,
                destination,
                DefaultFileMode,
                DateTimeOffset.UtcNow,
                total,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DownloadAsync(
        DevicePath source,
        Stream destination,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        GuardProtected(source);
        Require(capabilities.CanDownload, nameof(capabilities.CanDownload));

        var info = await GetInfoAsync(source, cancellationToken).ConfigureAwait(false);

        await using var sync = await AdbSyncClient.OpenAsync(connections, device, cancellationToken)
            .ConfigureAwait(false);

        await sync.ReceiveAsync(source, destination, info.Size, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a download from a 1 MiB-aligned offset (spec §13).
    /// </summary>
    /// <remarks>
    /// The sync protocol's RECV always starts at zero, so resume goes through <c>dd</c>. Alignment
    /// means only <c>bs</c> and <c>skip</c> are needed — no reliance on <c>iflag=skip_bytes</c> or
    /// <c>tail -c +N</c>, which vary across the toybox builds on real phones.
    /// </remarks>
    public async Task DownloadRangeAsync(
        DevicePath source,
        long startOffset,
        Stream destination,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        GuardProtected(source);
        GuardAlignment(startOffset);

        if (startOffset == 0)
        {
            await DownloadAsync(source, destination, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var info = await GetInfoAsync(source, cancellationToken).ConfigureAwait(false);
        var blocks = startOffset / AdbProtocol.ResumeAlignment;

        var command = $"dd if={ShellQuote.Quote(source)} bs={AdbProtocol.ResumeAlignment} "
                      + $"skip={blocks} 2>/dev/null";

        await using var stream = await shell.OpenExecStreamAsync(device, command, cancellationToken)
            .ConfigureAwait(false);

        await CopyWithProgressAsync(stream, destination, startOffset, info.Size, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes an upload at a 1 MiB-aligned offset (spec §13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The remainder is sent to a sibling temp file with the ordinary sync protocol and then appended with
    /// <c>cat</c>. Only primitives that are already proven are involved: sync <c>SEND</c>, which is
    /// byte-exact and used by every other transfer, and shell append, which every device has.
    /// </para>
    /// <para>
    /// The obvious alternative — piping into <c>dd seek=N conv=notrunc</c> — was tried first and is
    /// <b>wrong on real hardware</b>. <c>dd</c> issues one <c>read()</c> per block, and a read from a socket
    /// returns only what has arrived, so it writes short blocks and loses data: resuming a 3 MiB upload on a
    /// Galaxy S24 Ultra produced a 2.69 MiB file. Making that safe would need <c>iflag=fullblock</c>, which
    /// is exactly the sort of non-baseline option this project avoids depending on.
    /// </para>
    /// </remarks>
    public async Task UploadRangeAsync(
        Stream source,
        DevicePath destination,
        long startOffset,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        GuardProtected(destination);
        GuardAlignment(startOffset);
        Require(capabilities.CanUpload, nameof(capabilities.CanUpload));

        if (startOffset == 0)
        {
            await UploadAsync(source, destination, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Callers differ in what they hand over: the transfer manager seeks a full-file stream to the resume
        // point, while a caller may equally pass a stream holding only the remainder. Measuring what is
        // actually left to read covers both, where source.Length alone would be wrong for one of them.
        var remaining = source.CanSeek ? source.Length - source.Position : -1;
        var expectedTotal = remaining >= 0 ? startOffset + remaining : 0;

        var remainder = DevicePath.Parse(destination.Value + ".aeresume");

        // Report progress against the whole file, not just the remainder, so the UI stays continuous.
        var offsetProgress = progress is null
            ? null
            : new Progress<TransferProgress>(sample => progress.Report(new TransferProgress
            {
                BytesTransferred = startOffset + sample.BytesTransferred,
                TotalBytes = expectedTotal,
            }));

        try
        {
            await using (var sync = await AdbSyncClient
                             .OpenAsync(connections, device, cancellationToken).ConfigureAwait(false))
            {
                await sync.SendAsync(
                        source,
                        remainder,
                        DefaultFileMode,
                        DateTimeOffset.UtcNow,
                        remaining >= 0 ? remaining : 0,
                        offsetProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // Append, then verify the join landed where it should before removing the temp file.
            await shell.ExecuteExpectingSilenceAsync(
                    device,
                    $"cat {ShellQuote.Quote(remainder)} >> {ShellQuote.Quote(destination)}",
                    destination,
                    cancellationToken)
                .ConfigureAwait(false);

            if (expectedTotal > 0)
            {
                var joined = await GetInfoAsync(destination, cancellationToken).ConfigureAwait(false);
                if (joined.Size != expectedTotal)
                {
                    throw new AdbProtocolException(
                        $"resumed upload produced {joined.Size} bytes, expected {expectedTotal}");
                }
            }
        }
        finally
        {
            // Never leave the fragment behind, even if the append failed.
            try
            {
                await DeleteAsync(remainder, recursive: false, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (DeviceException)
            {
            }
        }
    }

    public async Task CreateDirectoryAsync(DevicePath path, CancellationToken cancellationToken)
    {
        GuardProtected(path);
        Require(capabilities.CanCreateDirectory, nameof(capabilities.CanCreateDirectory));

        await shell.ExecuteExpectingSilenceAsync(
                device, $"mkdir -p {ShellQuote.Quote(path)}", path, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(DevicePath path, bool recursive, CancellationToken cancellationToken)
    {
        GuardProtected(path);
        Require(capabilities.CanDelete, nameof(capabilities.CanDelete));

        if (path.IsRoot || path == KnownPaths.InternalStorage)
        {
            throw new AccessDeniedException(path, "refusing to delete a storage root");
        }

        var command = recursive
            ? $"rm -rf {ShellQuote.Quote(path)}"
            : $"rm -f {ShellQuote.Quote(path)}";

        await shell.ExecuteExpectingSilenceAsync(device, command, path, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RenameAsync(
        DevicePath source,
        DevicePath destination,
        CancellationToken cancellationToken)
    {
        GuardProtected(source);
        GuardProtected(destination);
        Require(capabilities.CanRename, nameof(capabilities.CanRename));

        // mv within the device moves no data across the connection, which is why moves are instant.
        await shell.ExecuteExpectingSilenceAsync(
                device,
                $"mv -f {ShellQuote.Quote(source)} {ShellQuote.Quote(destination)}",
                source,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CopyAsync(
        DevicePath source,
        DevicePath destination,
        CancellationToken cancellationToken)
    {
        GuardProtected(source);
        GuardProtected(destination);
        Require(capabilities.CanUpload, nameof(capabilities.CanUpload));

        await shell.ExecuteExpectingSilenceAsync(
                device,
                $"cp -r {ShellQuote.Quote(source)} {ShellQuote.Quote(destination)}",
                source,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Enumerates storage volumes: shared storage plus anything mounted under /storage (spec §6).</summary>
    public async Task<IReadOnlyList<StorageInfo>> GetStorageAsync(CancellationToken cancellationToken)
    {
        var volumes = new List<StorageInfo>();

        var internalStorage = await ReadVolumeAsync(KnownPaths.InternalStorage, false, cancellationToken)
            .ConfigureAwait(false);
        if (internalStorage is not null)
        {
            volumes.Add(internalStorage);
        }

        try
        {
            var mounts = await lister.ListAsync(device, KnownPaths.StorageRoot, cancellationToken)
                .ConfigureAwait(false);

            foreach (var mount in mounts.Where(entry => entry.IsDirectory))
            {
                // "emulated" is shared storage under another name, and "self" is a symlink to it.
                if (mount.Name is "emulated" or "self")
                {
                    continue;
                }

                var removable = await ReadVolumeAsync(mount.Path, true, cancellationToken)
                    .ConfigureAwait(false);
                if (removable is not null)
                {
                    volumes.Add(removable with { Label = mount.Name });
                }
            }
        }
        catch (DeviceException)
        {
            logger.LogDebug("Could not enumerate removable volumes; reporting shared storage only.");
        }

        return volumes;
    }

    public async Task<string> ComputeSha256Async(DevicePath path, CancellationToken cancellationToken)
    {
        if (!capabilities.HasSha256Sum)
        {
            throw new CapabilityNotSupportedException(nameof(capabilities.HasSha256Sum));
        }

        var output = await shell
            .ExecuteAsync(device, $"sha256sum {ShellQuote.Quote(path)}", true, cancellationToken)
            .ConfigureAwait(false);

        var text = output.Trim();
        if (text.Length < 64 || !text[..64].All(Uri.IsHexDigit))
        {
            throw AdbFailure.Translate(text.Length == 0 ? "sha256sum produced no output" : text, path);
        }

        return text[..64].ToLowerInvariant();
    }

    private async Task<StorageInfo?> ReadVolumeAsync(
        DevicePath root,
        bool removable,
        CancellationToken cancellationToken)
    {
        var output = await shell
            .ExecuteAsync(device, $"stat -f -c '%b %a %S' {ShellQuote.Quote(root)}", false, cancellationToken)
            .ConfigureAwait(false);

        var fields = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3
            || !long.TryParse(fields[0], out var totalBlocks)
            || !long.TryParse(fields[1], out var availableBlocks)
            || !long.TryParse(fields[2], out var blockSize)
            || totalBlocks <= 0)
        {
            return null;
        }

        return new StorageInfo
        {
            Root = root,
            TotalBytes = totalBlocks * blockSize,
            FreeBytes = availableBlocks * blockSize,
            IsRemovable = removable,
        };
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long alreadyTransferred,
        long totalBytes,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[AdbProtocol.SyncDataMax];
        var transferred = alreadyTransferred;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            transferred += read;
            progress?.Report(new TransferProgress
            {
                BytesTransferred = transferred,
                TotalBytes = totalBytes,
            });
        }
    }

    /// <summary>Refuses to operate inside areas Android protects (spec §17, §78).</summary>
    private static void GuardProtected(DevicePath path)
    {
        if (KnownPaths.IsProtected(path))
        {
            throw new AccessDeniedException(path, "path is inside a protected Android area");
        }
    }

    private static void GuardAlignment(long offset)
    {
        if (offset < 0 || offset % AdbProtocol.ResumeAlignment != 0)
        {
            throw new ArgumentException(
                $"Resume offsets must be a non-negative multiple of {AdbProtocol.ResumeAlignment} bytes; "
                + $"got {offset}. Alignment is what keeps resume working across OEM dd implementations.",
                nameof(offset));
        }
    }

    private static void Require(bool capability, string name)
    {
        if (!capability)
        {
            throw new CapabilityNotSupportedException(name);
        }
    }
}
