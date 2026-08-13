using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Handspan.Core.Exceptions;
using Handspan.Core.Models;

namespace Handspan.Adb;

/// <summary>
/// One connection to the ADB server, and the framing rules that govern it.
/// </summary>
/// <remarks>
/// <para>
/// Requests are four hex length digits followed by the payload; responses are <c>OKAY</c> or
/// <c>FAIL</c> plus a length-prefixed reason. The length counts <b>UTF-8 bytes</b>, not characters —
/// getting that wrong breaks every non-ASCII path, which is most of the interesting ones (spec §74).
/// </para>
/// <para>
/// A socket carries one service. After a transport switch it belongs to that device until closed, so
/// callers open a socket per operation.
/// </para>
/// </remarks>
internal sealed class AdbSocket : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    private AdbSocket(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public Stream Stream => _stream;

    public static async Task<AdbSocket> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(AdbProtocol.LocalHost, port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            client.Dispose();

            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw AdbServerException.StartFailed($"could not reach the ADB server on port {port}");
        }

        return new AdbSocket(client);
    }

    /// <summary>Sends a service request and consumes the OKAY, throwing a typed exception on FAIL.</summary>
    public async Task SendServiceAsync(string service, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(service);
        if (payload.Length > 0xFFFF)
        {
            throw new AdbProtocolException($"service request too long ({payload.Length} bytes)");
        }

        var buffer = new byte[4 + payload.Length];
        Encoding.ASCII.GetBytes(payload.Length.ToString("x4", CultureInfo.InvariantCulture), buffer);
        payload.CopyTo(buffer, 4);

        await WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await ReadOkayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a status word, translating FAIL into a typed exception (spec §48).</summary>
    public async Task ReadOkayAsync(CancellationToken cancellationToken, DevicePath? path = null)
    {
        var status = new byte[4];
        await ReadExactlyAsync(status, cancellationToken).ConfigureAwait(false);

        var text = Encoding.ASCII.GetString(status);
        switch (text)
        {
            case "OKAY":
                return;
            case "FAIL":
                var reason = await ReadLengthPrefixedStringAsync(cancellationToken).ConfigureAwait(false);
                throw AdbFailure.Translate(reason, path);
            default:
                throw new AdbProtocolException($"expected OKAY or FAIL, got '{Sanitize(text)}'");
        }
    }

    /// <summary>Reads a four-hex-digit length followed by that many UTF-8 bytes.</summary>
    public async Task<string> ReadLengthPrefixedStringAsync(CancellationToken cancellationToken)
    {
        var length = await ReadHexLengthAsync(cancellationToken).ConfigureAwait(false);
        if (length == 0)
        {
            return string.Empty;
        }

        var payload = new byte[length];
        await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    public async Task<int> ReadHexLengthAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        var text = Encoding.ASCII.GetString(header);
        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var length)
            || length < 0)
        {
            throw new AdbProtocolException($"invalid length prefix '{Sanitize(text)}'");
        }

        return length;
    }

    /// <summary>Writes a sync packet: a four-character identifier and a 32-bit little-endian value.</summary>
    public async Task WriteSyncHeaderAsync(string id, int value, CancellationToken cancellationToken)
    {
        var buffer = new byte[8];
        Encoding.ASCII.GetBytes(id, buffer);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), value);
        await WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a sync packet whose payload is a device path, encoded as UTF-8.</summary>
    public async Task WriteSyncRequestAsync(string id, string payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await WriteSyncHeaderAsync(id, bytes.Length, cancellationToken).ConfigureAwait(false);
        await WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a four-character sync identifier.</summary>
    public async Task<string> ReadSyncIdAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4];
        await ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Encoding.ASCII.GetString(buffer);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw new DeviceDisconnectedException(technicalDetail: ex.Message, inner: ex);
        }
    }

    /// <summary>
    /// Fills the buffer, treating a short read as a disconnect rather than a protocol error — mid-transfer
    /// EOF means the cable was pulled, which is a recoverable condition (spec §13, §38).
    /// </summary>
    public async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await _stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new DeviceDisconnectedException(
                technicalDetail: $"connection closed after {ex.Message}", inner: ex);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw new DeviceDisconnectedException(technicalDetail: ex.Message, inner: ex);
        }
    }

    /// <summary>Reads whatever is available, returning 0 at end of stream.</summary>
    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw new DeviceDisconnectedException(technicalDetail: ex.Message, inner: ex);
        }
    }

    /// <summary>
    /// Half-closes the sending side, which is how a command reading stdin — <c>dd</c> during a
    /// resumed push — is told the input has ended.
    /// </summary>
    public void ShutdownSend()
    {
        try
        {
            _client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
            // Already torn down; nothing useful to do.
        }
    }

    /// <summary>Strips control characters so a malformed response cannot corrupt a log line.</summary>
    private static string Sanitize(string value)
        => new(value.Select(c => char.IsControl(c) ? '?' : c).ToArray());

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
