using System.Buffers.Binary;
using System.Text;
using Handspan.Core.Exceptions;
using Handspan.Core.Models;

namespace Handspan.Adb;

/// <summary>
/// The ADB sync protocol: structured listings, stat, and file transfer.
/// </summary>
/// <remarks>
/// <para>
/// Listings arrive as fixed-layout records, so filenames containing spaces, quotes, newlines, emoji
/// and RTL text are safe <em>by construction</em> — this is exactly why the spec forbids parsing
/// <c>ls -la</c> output (spec §73, §74).
/// </para>
/// <para>
/// Note that sync-mode lengths are 32-bit little-endian binary, unlike the host protocol's four
/// ASCII hex digits. Mixing the two is a silent corruption bug, so sync framing lives here.
/// </para>
/// </remarks>
internal sealed class AdbSyncClient(AdbSocket socket) : IAsyncDisposable
{
    public static async Task<AdbSyncClient> OpenAsync(
        IAdbConnectionFactory connections,
        DeviceId device,
        CancellationToken cancellationToken)
    {
        var socket = await connections
            .OpenServiceAsync(device, AdbProtocol.SyncService, cancellationToken)
            .ConfigureAwait(false);

        return new AdbSyncClient(socket);
    }

    /// <summary>Lists a directory. Uses the 64-bit-safe v2 form when the device supports it.</summary>
    public async Task<IReadOnlyList<DeviceEntry>> ListAsync(
        DeviceId device,
        DevicePath path,
        bool useV2,
        CancellationToken cancellationToken)
    {
        await socket.WriteSyncRequestAsync(
            useV2 ? SyncId.ListV2 : SyncId.List, path.Value, cancellationToken).ConfigureAwait(false);

        var entries = new List<DeviceEntry>();

        while (true)
        {
            var id = await socket.ReadSyncIdAsync(cancellationToken).ConfigureAwait(false);

            if (id == SyncId.Done)
            {
                // The terminator carries an empty body of the same shape as an entry.
                await SkipAsync(useV2 ? SyncId.DentV2BodyLength : SyncId.DentV1BodyLength, cancellationToken)
                    .ConfigureAwait(false);
                break;
            }

            if (id == SyncId.Fail)
            {
                throw AdbFailure.Translate(await ReadSyncStringAsync(cancellationToken).ConfigureAwait(false), path);
            }

            var entry = id switch
            {
                SyncId.DentV2 when useV2 => await ReadDentV2Async(device, path, cancellationToken)
                    .ConfigureAwait(false),
                SyncId.Dent when !useV2 => await ReadDentV1Async(device, path, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new AdbProtocolException($"unexpected sync id '{id}' while listing"),
            };

            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>Stats one path. Uses the v2 form when available, which alone reports 64-bit sizes.</summary>
    public async Task<DeviceFileInfo> StatAsync(
        DeviceId device,
        DevicePath path,
        bool useV2,
        CancellationToken cancellationToken)
    {
        if (useV2)
        {
            await socket.WriteSyncRequestAsync(SyncId.StatV2, path.Value, cancellationToken)
                .ConfigureAwait(false);

            var id = await socket.ReadSyncIdAsync(cancellationToken).ConfigureAwait(false);
            if (id != SyncId.StatV2)
            {
                throw new AdbProtocolException($"expected {SyncId.StatV2}, got '{id}'");
            }

            var body = new byte[SyncId.StatV2BodyLength];
            await socket.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);

            var stat = StatV2.Parse(body);
            if (stat.Error != 0)
            {
                throw TranslateErrno(stat.Error, path);
            }

            return new DeviceFileInfo
            {
                DeviceId = device,
                Path = path,
                Kind = PosixMode.ToKind(stat.Mode),
                Size = stat.Size,
                IsSizeKnown = true,
                Modified = DateTimeOffset.FromUnixTimeSeconds(stat.Mtime),
                Accessed = SafeFromUnixSeconds(stat.Atime),
                Created = SafeFromUnixSeconds(stat.Ctime),
                Mode = stat.Mode & PosixMode.PermissionMask,
                OwnerUserId = stat.Uid,
                OwnerGroupId = stat.Gid,
                IsSymlink = (stat.Mode & PosixMode.TypeMask) == PosixMode.Symlink,
            };
        }

        await socket.WriteSyncRequestAsync(SyncId.Stat, path.Value, cancellationToken).ConfigureAwait(false);

        var v1Id = await socket.ReadSyncIdAsync(cancellationToken).ConfigureAwait(false);
        if (v1Id != SyncId.Stat)
        {
            throw new AdbProtocolException($"expected {SyncId.Stat}, got '{v1Id}'");
        }

        var v1Body = new byte[12];
        await socket.ReadExactlyAsync(v1Body, cancellationToken).ConfigureAwait(false);

        var mode = BinaryPrimitives.ReadInt32LittleEndian(v1Body);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(v1Body.AsSpan(4));
        var mtime = BinaryPrimitives.ReadUInt32LittleEndian(v1Body.AsSpan(8));

        // v1 has no error field: mode 0 is how a missing path is reported.
        if (mode == 0)
        {
            throw new PathNotFoundException(path, "stat returned mode 0");
        }

        return new DeviceFileInfo
        {
            DeviceId = device,
            Path = path,
            Kind = PosixMode.ToKind(mode),
            Size = size,
            IsSizeKnown = size != uint.MaxValue,
            Modified = DateTimeOffset.FromUnixTimeSeconds(mtime),
            Mode = mode & PosixMode.PermissionMask,
            IsSymlink = (mode & PosixMode.TypeMask) == PosixMode.Symlink,
        };
    }

    /// <summary>Checks existence without throwing when the path is absent.</summary>
    public async Task<bool> ExistsAsync(
        DeviceId device,
        DevicePath path,
        bool useV2,
        CancellationToken cancellationToken)
    {
        try
        {
            await StatAsync(device, path, useV2, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (PathNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads a whole file, reporting byte-exact progress.
    /// </summary>
    /// <remarks>
    /// This is the fast path. Resuming from an offset needs a different mechanism entirely, because
    /// RECV always starts at zero — see <c>docs/plan/03-transfers.md</c>.
    /// </remarks>
    public async Task ReceiveAsync(
        DevicePath source,
        Stream destination,
        long totalBytes,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        await socket.WriteSyncRequestAsync(SyncId.Recv, source.Value, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[AdbProtocol.SyncDataMax];
        long received = 0;

        while (true)
        {
            var id = await socket.ReadSyncIdAsync(cancellationToken).ConfigureAwait(false);

            if (id == SyncId.Done)
            {
                await SkipAsync(4, cancellationToken).ConfigureAwait(false);
                break;
            }

            if (id == SyncId.Fail)
            {
                throw AdbFailure.Translate(
                    await ReadSyncStringAsync(cancellationToken).ConfigureAwait(false), source);
            }

            if (id != SyncId.Data)
            {
                throw new AdbProtocolException($"unexpected sync id '{id}' during download");
            }

            var length = await ReadSyncLengthAsync(cancellationToken).ConfigureAwait(false);
            if (length is < 0 or > AdbProtocol.SyncDataMax)
            {
                throw new AdbProtocolException($"invalid data chunk length {length}");
            }

            await socket.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);

            received += length;
            progress?.Report(new TransferProgress { BytesTransferred = received, TotalBytes = totalBytes });
        }
    }

    /// <summary>Uploads a whole file, preserving its modification time.</summary>
    public async Task SendAsync(
        Stream source,
        DevicePath destination,
        int mode,
        DateTimeOffset modified,
        long totalBytes,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        // The SEND payload is "path,mode" — the comma is the protocol's own separator.
        await socket.WriteSyncRequestAsync(
                SyncId.Send,
                $"{destination.Value},{mode}",
                cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[AdbProtocol.SyncDataMax];
        long sent = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await socket.WriteSyncHeaderAsync(SyncId.Data, read, cancellationToken).ConfigureAwait(false);
            await socket.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            sent += read;
            progress?.Report(new TransferProgress { BytesTransferred = sent, TotalBytes = totalBytes });
        }

        await socket.WriteSyncHeaderAsync(
                SyncId.Done,
                (int)modified.ToUnixTimeSeconds(),
                cancellationToken)
            .ConfigureAwait(false);

        var id = await socket.ReadSyncIdAsync(cancellationToken).ConfigureAwait(false);
        if (id == SyncId.Fail)
        {
            throw AdbFailure.Translate(
                await ReadSyncStringAsync(cancellationToken).ConfigureAwait(false), destination);
        }

        if (id != SyncId.Okay)
        {
            throw new AdbProtocolException($"unexpected sync id '{id}' after upload");
        }

        await SkipAsync(4, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeviceEntry?> ReadDentV1Async(
        DeviceId device,
        DevicePath parent,
        CancellationToken cancellationToken)
    {
        var body = new byte[SyncId.DentV1BodyLength];
        await socket.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);

        var mode = BinaryPrimitives.ReadInt32LittleEndian(body);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
        var mtime = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
        var nameLength = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(12));

        var name = await ReadNameAsync(nameLength, cancellationToken).ConfigureAwait(false);
        if (!TryBuildPath(parent, name, out var path))
        {
            return null;
        }

        return new DeviceEntry
        {
            DeviceId = device,
            Path = path,
            Kind = PosixMode.ToKind(mode),
            Size = size,

            // A v1 size field is 32 bits, so 4 GiB - 1 is indistinguishable from "saturated".
            // Reporting it as unknown beats displaying a wrong number.
            IsSizeKnown = size != uint.MaxValue,
            Modified = DateTimeOffset.FromUnixTimeSeconds(mtime),
            Mode = mode & PosixMode.PermissionMask,
            IsSymlink = (mode & PosixMode.TypeMask) == PosixMode.Symlink,
        };
    }

    private async Task<DeviceEntry?> ReadDentV2Async(
        DeviceId device,
        DevicePath parent,
        CancellationToken cancellationToken)
    {
        var body = new byte[SyncId.DentV2BodyLength];
        await socket.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);

        var stat = StatV2.Parse(body);
        var nameLength = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(SyncId.StatV2BodyLength));

        var name = await ReadNameAsync(nameLength, cancellationToken).ConfigureAwait(false);
        if (!TryBuildPath(parent, name, out var path))
        {
            return null;
        }

        return new DeviceEntry
        {
            DeviceId = device,
            Path = path,
            Kind = PosixMode.ToKind(stat.Mode),
            Size = stat.Size,
            IsSizeKnown = true,
            Modified = DateTimeOffset.FromUnixTimeSeconds(stat.Mtime),
            Mode = stat.Mode & PosixMode.PermissionMask,
            IsSymlink = (stat.Mode & PosixMode.TypeMask) == PosixMode.Symlink,
        };
    }

    private async Task<string> ReadNameAsync(int nameLength, CancellationToken cancellationToken)
    {
        if (nameLength is < 0 or > 4096)
        {
            throw new AdbProtocolException($"invalid entry name length {nameLength}");
        }

        if (nameLength == 0)
        {
            return string.Empty;
        }

        var nameBytes = new byte[nameLength];
        await socket.ReadExactlyAsync(nameBytes, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(nameBytes);
    }

    /// <summary>
    /// Builds a child path, filtering the "." and ".." entries the protocol includes and skipping
    /// anything unrepresentable rather than failing the whole listing.
    /// </summary>
    private static bool TryBuildPath(DevicePath parent, string name, out DevicePath path)
    {
        path = default;

        if (name is "" or "." or "..")
        {
            return false;
        }

        if (!DevicePath.IsValidFileName(name))
        {
            return false;
        }

        path = parent.Combine(name);
        return true;
    }

    private async Task<int> ReadSyncLengthAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4];
        await socket.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private async Task<string> ReadSyncStringAsync(CancellationToken cancellationToken)
    {
        var length = await ReadSyncLengthAsync(cancellationToken).ConfigureAwait(false);
        if (length is <= 0 or > 65536)
        {
            return string.Empty;
        }

        var payload = new byte[length];
        await socket.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    private async Task SkipAsync(int count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return;
        }

        var scratch = new byte[count];
        await socket.ReadExactlyAsync(scratch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps a v2 stat errno onto a typed exception (spec §48).</summary>
    private static DeviceException TranslateErrno(int errno, DevicePath path) => errno switch
    {
        2 => new PathNotFoundException(path, "ENOENT"),        // No such file or directory
        13 => new AccessDeniedException(path, "EACCES"),       // Permission denied
        1 => new AccessDeniedException(path, "EPERM"),         // Operation not permitted
        20 => new PathNotFoundException(path, "ENOTDIR"),      // Not a directory
        _ => new AdbProtocolException($"stat failed with errno {errno}"),
    };

    /// <summary>Guards against devices reporting nonsensical timestamps.</summary>
    private static DateTimeOffset? SafeFromUnixSeconds(long seconds)
        => seconds is > 0 and < 253_402_300_799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await socket.WriteSyncHeaderAsync(SyncId.Quit, 0, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DeviceException)
        {
            // The connection is going away regardless.
        }

        await socket.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The packed <c>stat_v2</c> body: error, dev, ino, mode, nlink, uid, gid, size, atime, mtime,
    /// ctime — 68 bytes after the four-byte identifier.
    /// </summary>
    private readonly record struct StatV2(
        int Error,
        int Mode,
        long Size,
        long Atime,
        long Mtime,
        long Ctime,
        int Uid,
        int Gid)
    {
        public static StatV2 Parse(ReadOnlySpan<byte> body) => new(
            Error: BinaryPrimitives.ReadInt32LittleEndian(body),
            // dev at 4 (8 bytes) and ino at 12 (8 bytes) are not surfaced.
            Mode: BinaryPrimitives.ReadInt32LittleEndian(body[20..]),
            // nlink at 24.
            Uid: BinaryPrimitives.ReadInt32LittleEndian(body[28..]),
            Gid: BinaryPrimitives.ReadInt32LittleEndian(body[32..]),
            Size: BinaryPrimitives.ReadInt64LittleEndian(body[36..]),
            Atime: BinaryPrimitives.ReadInt64LittleEndian(body[44..]),
            Mtime: BinaryPrimitives.ReadInt64LittleEndian(body[52..]),
            Ctime: BinaryPrimitives.ReadInt64LittleEndian(body[60..]));
    }
}
