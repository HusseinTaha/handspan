using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AndroidExplorer.Core.Models;

namespace AndroidExplorer.Adb.Tests;

/// <summary>
/// A loopback server that speaks the real ADB host and sync protocols against an in-memory
/// filesystem, with injectable faults.
/// </summary>
/// <remarks>
/// <para>
/// This exists because real hardware cannot be asked to drop its connection at byte 3,355,443,200 on
/// demand. Resume, cancellation and recovery are only testable reproducibly against a server we
/// control (spec §81).
/// </para>
/// <para>
/// It is deliberately checked against the real server too: the protocol tests in
/// <c>RealAdbServerTests</c> assert the same behaviours, so a misreading of the protocol here cannot
/// quietly become the definition of correct.
/// </para>
/// </remarks>
internal sealed class FakeAdbServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<TcpClient> _trackers = [];
    private readonly Task _acceptLoop;

    private FakeAdbServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>The emulated device's serial.</summary>
    public string Serial { get; set; } = "FAKE0123456789";

    /// <summary>The state reported by the device list; changing it pushes to tracking clients.</summary>
    public string DeviceState { get; private set; } = "device";

    public FakeFileSystem Files { get; } = FakeFileSystem.WithTypicalAndroidLayout();

    public FaultOptions Faults { get; } = new();

    public HashSet<string> Features { get; } = ["stat_v2", "ls_v2", "shell_v2", "sendrecv_v2", "cmd"];

    /// <summary>Commands the client has issued, for asserting which protocol path was taken.</summary>
    public List<string> ExecutedCommands { get; } = [];

    public static FakeAdbServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new FakeAdbServer(listener);
    }

    /// <summary>Changes the device state and pushes the new list to tracking clients (spec §38).</summary>
    public async Task SetDeviceStateAsync(string state)
    {
        DeviceState = state;

        List<TcpClient> trackers;
        lock (_trackers)
        {
            trackers = [.. _trackers];
        }

        foreach (var tracker in trackers)
        {
            try
            {
                await WriteLengthPrefixedAsync(tracker.GetStream(), DeviceListPayload(), _shutdown.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                lock (_trackers)
                {
                    _trackers.Remove(tracker);
                }
            }
        }
    }

    private string DeviceListPayload()
        => DeviceState == "absent" ? string.Empty : $"{Serial}\t{DeviceState}\n";

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                                           or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var service = await ReadRequestAsync(stream, _shutdown.Token).ConfigureAwait(false);

            if (service.StartsWith("host:", StringComparison.Ordinal)
                || service.StartsWith("host-serial:", StringComparison.Ordinal))
            {
                var keepOpen = await ServeHostServiceAsync(client, stream, service).ConfigureAwait(false);
                if (keepOpen)
                {
                    return; // tracking sockets stay open and are owned by _trackers
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
                                       or OperationCanceledException or EndOfStreamException)
        {
            // A disconnected client is normal here, including when we caused it deliberately.
        }
        finally
        {
            bool tracked;
            lock (_trackers)
            {
                tracked = _trackers.Contains(client);
            }

            if (!tracked)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>Returns true when the socket must stay open (device tracking).</summary>
    private async Task<bool> ServeHostServiceAsync(TcpClient client, NetworkStream stream, string service)
    {
        var token = _shutdown.Token;

        switch (service)
        {
            case "host:version":
                await WriteOkayAsync(stream, token).ConfigureAwait(false);
                await WriteLengthPrefixedAsync(stream, "0029", token).ConfigureAwait(false);
                return false;

            case "host:devices" or "host:devices-l":
                await WriteOkayAsync(stream, token).ConfigureAwait(false);
                await WriteLengthPrefixedAsync(stream, DeviceListPayload(), token).ConfigureAwait(false);
                return false;

            case "host:track-devices":
                await WriteOkayAsync(stream, token).ConfigureAwait(false);
                await WriteLengthPrefixedAsync(stream, DeviceListPayload(), token).ConfigureAwait(false);
                lock (_trackers)
                {
                    _trackers.Add(client);
                }

                return true;

            case "host:kill":
                await WriteOkayAsync(stream, token).ConfigureAwait(false);
                return false;
        }

        if (service.StartsWith("host-serial:", StringComparison.Ordinal))
        {
            if (service.EndsWith(":features", StringComparison.Ordinal))
            {
                await WriteOkayAsync(stream, token).ConfigureAwait(false);
                await WriteLengthPrefixedAsync(stream, string.Join(',', Features), token)
                    .ConfigureAwait(false);
                return false;
            }

            if (service.EndsWith(":get-state", StringComparison.Ordinal))
            {
                if (DeviceState == "unauthorized")
                {
                    await WriteFailAsync(stream, "device unauthorized.", token).ConfigureAwait(false);
                    return false;
                }

                await WriteOkayAsync(stream, token).ConfigureAwait(false);
                await WriteLengthPrefixedAsync(stream, DeviceState, token).ConfigureAwait(false);
                return false;
            }
        }

        if (service.StartsWith("host:transport:", StringComparison.Ordinal))
        {
            if (DeviceState == "unauthorized")
            {
                await WriteFailAsync(stream, "device unauthorized.", token).ConfigureAwait(false);
                return false;
            }

            if (DeviceState == "absent")
            {
                await WriteFailAsync(stream, "device not found", token).ConfigureAwait(false);
                return false;
            }

            await WriteOkayAsync(stream, token).ConfigureAwait(false);

            // The socket now belongs to the device: one local service follows.
            var local = await ReadRequestAsync(stream, token).ConfigureAwait(false);

            if (local == "sync:")
            {
                await WriteOkayAsync(stream, token).ConfigureAwait(false);
                await ServeSyncAsync(stream, token).ConfigureAwait(false);
                return false;
            }

            if (local.StartsWith("exec:", StringComparison.Ordinal))
            {
                await WriteOkayAsync(stream, token).ConfigureAwait(false);
                await ServeExecAsync(stream, local[5..], token).ConfigureAwait(false);
                return false;
            }

            await WriteFailAsync(stream, $"unknown service {local}", token).ConfigureAwait(false);
            return false;
        }

        await WriteFailAsync(stream, $"unknown host service {service}", token).ConfigureAwait(false);
        return false;
    }

    // ---------------- sync protocol ----------------

    private async Task ServeSyncAsync(NetworkStream stream, CancellationToken token)
    {
        while (true)
        {
            var id = await ReadAsciiAsync(stream, 4, token).ConfigureAwait(false);
            if (id is null or "QUIT")
            {
                return;
            }

            var length = await ReadInt32Async(stream, token).ConfigureAwait(false);
            var payload = length > 0
                ? Encoding.UTF8.GetString(await ReadExactlyAsync(stream, length, token).ConfigureAwait(false))
                : string.Empty;

            switch (id)
            {
                case "LIST" or "LIS2":
                    await ServeListAsync(stream, payload, id == "LIS2", token).ConfigureAwait(false);
                    break;

                case "STAT" or "STA2" or "LST2":
                    await ServeStatAsync(stream, payload, id != "STAT", token).ConfigureAwait(false);
                    break;

                case "RECV":
                    await ServeRecvAsync(stream, payload, token).ConfigureAwait(false);
                    break;

                case "SEND":
                    await ServeSendAsync(stream, payload, token).ConfigureAwait(false);
                    break;

                default:
                    await WriteSyncFailAsync(stream, $"unknown sync id {id}", token).ConfigureAwait(false);
                    return;
            }
        }
    }

    private async Task ServeListAsync(
        NetworkStream stream,
        string path,
        bool v2,
        CancellationToken token)
    {
        if (Faults.FailingPaths.TryGetValue(FakeFileSystem.Canonical(path), out var reason))
        {
            await WriteSyncFailAsync(stream, reason, token).ConfigureAwait(false);
            return;
        }

        // Real servers include "." and ".."; the client must filter them.
        var children = Files.Children(path).ToList();

        foreach (var name in new[] { ".", ".." })
        {
            await WriteDentAsync(stream, v2, name, Files.Get(path) ?? FakeNode.Directory(), token)
                .ConfigureAwait(false);
        }

        foreach (var (name, node) in children)
        {
            await WriteDentAsync(stream, v2, name, node, token).ConfigureAwait(false);
        }

        // DONE carries an empty body of the same shape as an entry.
        await WriteAsciiAsync(stream, "DONE", token).ConfigureAwait(false);
        await stream.WriteAsync(new byte[v2 ? 72 : 16], token).ConfigureAwait(false);
    }

    private async Task WriteDentAsync(
        NetworkStream stream,
        bool v2,
        string name,
        FakeNode node,
        CancellationToken token)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);

        if (v2)
        {
            await WriteAsciiAsync(stream, "DNT2", token).ConfigureAwait(false);
            var body = new byte[72];
            WriteStatV2Body(body, node);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(68), nameBytes.Length);
            await stream.WriteAsync(body, token).ConfigureAwait(false);
        }
        else
        {
            await WriteAsciiAsync(stream, "DENT", token).ConfigureAwait(false);
            var body = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(body, node.Mode);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),
                (uint)Math.Min(node.Length, uint.MaxValue));
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)node.ModifiedUnix);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12), nameBytes.Length);
            await stream.WriteAsync(body, token).ConfigureAwait(false);
        }

        await stream.WriteAsync(nameBytes, token).ConfigureAwait(false);
    }

    private async Task ServeStatAsync(
        NetworkStream stream,
        string path,
        bool v2,
        CancellationToken token)
    {
        var node = Files.Resolve(path);

        if (v2)
        {
            await WriteAsciiAsync(stream, "STA2", token).ConfigureAwait(false);
            var body = new byte[68];

            if (node is null)
            {
                BinaryPrimitives.WriteInt32LittleEndian(body, 2); // ENOENT
            }
            else
            {
                WriteStatV2Body(body, node);
            }

            await stream.WriteAsync(body, token).ConfigureAwait(false);
            return;
        }

        await WriteAsciiAsync(stream, "STAT", token).ConfigureAwait(false);
        var v1 = new byte[12];
        if (node is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(v1, node.Mode);
            BinaryPrimitives.WriteUInt32LittleEndian(v1.AsSpan(4),
                (uint)Math.Min(node.Length, uint.MaxValue));
            BinaryPrimitives.WriteUInt32LittleEndian(v1.AsSpan(8), (uint)node.ModifiedUnix);
        }

        await stream.WriteAsync(v1, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Lays out a packed stat_v2 body: error, dev, ino, mode, nlink, uid, gid, size, atime, mtime, ctime.
    /// </summary>
    private static void WriteStatV2Body(Span<byte> body, FakeNode node)
    {
        BinaryPrimitives.WriteInt32LittleEndian(body, 0);                       // error
        BinaryPrimitives.WriteInt64LittleEndian(body[4..], 1);                  // dev
        BinaryPrimitives.WriteInt64LittleEndian(body[12..], 2);                 // ino
        BinaryPrimitives.WriteInt32LittleEndian(body[20..], node.Mode);         // mode
        BinaryPrimitives.WriteInt32LittleEndian(body[24..], 1);                 // nlink
        BinaryPrimitives.WriteInt32LittleEndian(body[28..], 1000);              // uid
        BinaryPrimitives.WriteInt32LittleEndian(body[32..], 1000);              // gid
        BinaryPrimitives.WriteInt64LittleEndian(body[36..], node.Length);       // size
        BinaryPrimitives.WriteInt64LittleEndian(body[44..], node.ModifiedUnix); // atime
        BinaryPrimitives.WriteInt64LittleEndian(body[52..], node.ModifiedUnix); // mtime
        BinaryPrimitives.WriteInt64LittleEndian(body[60..], node.ModifiedUnix); // ctime
    }

    private async Task ServeRecvAsync(NetworkStream stream, string path, CancellationToken token)
    {
        var node = Files.Resolve(path);
        if (node is null || node.IsDirectory)
        {
            await WriteSyncFailAsync(stream, "no such file or directory", token).ConfigureAwait(false);
            return;
        }

        if (Faults.FailingPaths.TryGetValue(FakeFileSystem.Canonical(path), out var reason))
        {
            await WriteSyncFailAsync(stream, reason, token).ConfigureAwait(false);
            return;
        }

        var sent = 0L;
        var content = node.Content;

        while (sent < content.Length)
        {
            var chunk = (int)Math.Min(64 * 1024, content.Length - sent);

            // Cable-pull simulation: close the socket mid-stream (spec §81).
            if (Faults.DropAfterBytes is { } limit && sent + chunk > limit)
            {
                var partial = (int)Math.Max(0, limit - sent);
                if (partial > 0)
                {
                    await WriteAsciiAsync(stream, "DATA", token).ConfigureAwait(false);
                    await WriteInt32Async(stream, partial, token).ConfigureAwait(false);
                    await stream.WriteAsync(content.AsMemory((int)sent, partial), token)
                        .ConfigureAwait(false);
                }

                stream.Socket.Close();
                return;
            }

            await WriteAsciiAsync(stream, "DATA", token).ConfigureAwait(false);
            await WriteInt32Async(stream, chunk, token).ConfigureAwait(false);
            await stream.WriteAsync(content.AsMemory((int)sent, chunk), token).ConfigureAwait(false);
            sent += chunk;
        }

        await WriteAsciiAsync(stream, "DONE", token).ConfigureAwait(false);
        await WriteInt32Async(stream, 0, token).ConfigureAwait(false);
    }

    private async Task ServeSendAsync(NetworkStream stream, string spec, CancellationToken token)
    {
        // The payload is "path,mode".
        var comma = spec.LastIndexOf(',');
        var path = comma > 0 ? spec[..comma] : spec;

        if (Faults.FailingPaths.TryGetValue(FakeFileSystem.Canonical(path), out var reason))
        {
            await WriteSyncFailAsync(stream, reason, token).ConfigureAwait(false);
            return;
        }

        var buffer = new MemoryStream();

        while (true)
        {
            var id = await ReadAsciiAsync(stream, 4, token).ConfigureAwait(false);
            if (id is null)
            {
                return; // client vanished mid-upload
            }

            var length = await ReadInt32Async(stream, token).ConfigureAwait(false);

            if (id == "DONE")
            {
                Files.WriteFile(path, buffer.ToArray(), length);
                await WriteAsciiAsync(stream, "OKAY", token).ConfigureAwait(false);
                await WriteInt32Async(stream, 0, token).ConfigureAwait(false);
                return;
            }

            if (id != "DATA")
            {
                await WriteSyncFailAsync(stream, $"unexpected {id} during send", token).ConfigureAwait(false);
                return;
            }

            var data = await ReadExactlyAsync(stream, length, token).ConfigureAwait(false);
            buffer.Write(data);

            if (Faults.DropAfterBytes is { } limit && buffer.Length > limit)
            {
                // Persist what arrived, as a real device would have, then vanish.
                Interlocked.Increment(ref Faults.DropsTriggered);
                Files.WriteFile(path, buffer.ToArray()[..(int)limit], 0);
                stream.Socket.Close();
                return;
            }
        }
    }

    // ---------------- exec: service ----------------

    private async Task ServeExecAsync(NetworkStream stream, string command, CancellationToken token)
    {
        lock (ExecutedCommands)
        {
            ExecutedCommands.Add(command);
        }

        var normalized = command
            .Replace("2>/dev/null", string.Empty, StringComparison.Ordinal)
            .Replace("2>&1", string.Empty, StringComparison.Ordinal)
            .Trim();

        var output = await FakeShell.ExecuteAsync(this, normalized, stream, token).ConfigureAwait(false);

        if (output is not null)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(output), token).ConfigureAwait(false);
        }

        stream.Socket.Shutdown(SocketShutdown.Send);
    }

    // ---------------- framing helpers ----------------

    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var header = await ReadExactlyAsync(stream, 4, token).ConfigureAwait(false);
        var length = int.Parse(Encoding.ASCII.GetString(header), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);

        var payload = await ReadExactlyAsync(stream, length, token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    private static Task WriteOkayAsync(Stream stream, CancellationToken token)
        => stream.WriteAsync(Encoding.ASCII.GetBytes("OKAY"), token).AsTask();

    private static async Task WriteFailAsync(Stream stream, string reason, CancellationToken token)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes("FAIL"), token).ConfigureAwait(false);
        await WriteLengthPrefixedAsync(stream, reason, token).ConfigureAwait(false);
    }

    private static async Task WriteLengthPrefixedAsync(
        Stream stream,
        string payload,
        CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await stream.WriteAsync(
                Encoding.ASCII.GetBytes(bytes.Length.ToString("x4", CultureInfo.InvariantCulture)), token)
            .ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    /// <summary>Sync-mode failures use a binary length, not the host protocol's hex digits.</summary>
    private static async Task WriteSyncFailAsync(Stream stream, string reason, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(reason);
        await WriteAsciiAsync(stream, "FAIL", token).ConfigureAwait(false);
        await WriteInt32Async(stream, bytes.Length, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken token)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(text), token).AsTask();

    private static Task WriteInt32Async(Stream stream, int value, CancellationToken token)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        return stream.WriteAsync(buffer, token).AsTask();
    }

    private static async Task<int> ReadInt32Async(Stream stream, CancellationToken token)
        => BinaryPrimitives.ReadInt32LittleEndian(
            await ReadExactlyAsync(stream, 4, token).ConfigureAwait(false));

    private static async Task<string?> ReadAsciiAsync(Stream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        var read = 0;

        while (read < count)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read), token).ConfigureAwait(false);
            if (got == 0)
            {
                return null;
            }

            read += got;
        }

        return Encoding.ASCII.GetString(buffer);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        lock (_trackers)
        {
            foreach (var tracker in _trackers)
            {
                tracker.Dispose();
            }

            _trackers.Clear();
        }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _shutdown.Dispose();
    }
}

/// <summary>Faults the fake server can inject (spec §81).</summary>
internal sealed class FaultOptions
{
    /// <summary>Close the connection once this many payload bytes have moved.</summary>
    public long? DropAfterBytes { get; set; }

    /// <summary>Paths that answer with a protocol failure, keyed by path to the reason text.</summary>
    public Dictionary<string, string> FailingPaths { get; } = [];

    /// <summary>How many times a drop actually fired, so tests can assert the fault was exercised.</summary>
    public int DropsTriggered;

    /// <summary>Emulate a device whose <c>dd</c> does not accept <c>conv=notrunc</c>.</summary>
    public bool DdRejectsNoTrunc { get; set; }

    /// <summary>Emulate a device without <c>sha256sum</c>.</summary>
    public bool NoSha256Sum { get; set; }
}
