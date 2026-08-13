using System.Text;
using AndroidExplorer.Core.Models;

namespace AndroidExplorer.Adb;

/// <summary>Runs commands on the device.</summary>
public interface IAdbShellClient
{
    /// <summary>
    /// Runs a command and returns its output as text.
    /// </summary>
    /// <param name="mergeStandardError">
    /// Appends <c>2>&amp;1</c>. Needed when a failure message matters, because the raw
    /// <c>exec:</c> service does not separate the streams.
    /// </param>
    Task<string> ExecuteAsync(
        DeviceId device,
        string command,
        bool mergeStandardError,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a command and exposes its raw stdout as a stream, without line-ending translation.
    /// </summary>
    /// <remarks>
    /// The basis of resumable pulls and range reads: <c>dd</c> writes binary here and any PTY
    /// translation would corrupt it, which is why plain <c>shell:</c> is never used.
    /// </remarks>
    Task<Stream> OpenExecStreamAsync(DeviceId device, string command, CancellationToken cancellationToken);

    /// <summary>Runs a command, returning true when it produced no error output.</summary>
    Task<bool> TryExecuteAsync(DeviceId device, string command, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a command that writes to the device, translating any output into a typed exception.
    /// </summary>
    /// <remarks>
    /// Device write commands are silent on success, so any output at all indicates failure. That is
    /// how "permission denied" or "read-only file system" becomes a sentence the user can act on
    /// (spec §48).
    /// </remarks>
    Task ExecuteExpectingSilenceAsync(
        DeviceId device,
        string command,
        DevicePath path,
        CancellationToken cancellationToken);
}

internal sealed class AdbShellClient(IAdbConnectionFactory connections) : IAdbShellClient
{
    public async Task<string> ExecuteAsync(
        DeviceId device,
        string command,
        bool mergeStandardError,
        CancellationToken cancellationToken)
    {
        var effective = mergeStandardError ? command + " 2>&1" : command;

        await using var socket = await connections
            .OpenServiceAsync(device, AdbProtocol.Exec(effective), cancellationToken)
            .ConfigureAwait(false);

        var output = new MemoryStream();
        var buffer = new byte[8192];

        while (true)
        {
            var read = await socket.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    public async Task<Stream> OpenExecStreamAsync(
        DeviceId device,
        string command,
        CancellationToken cancellationToken)
    {
        var socket = await connections
            .OpenServiceAsync(device, AdbProtocol.Exec(command), cancellationToken)
            .ConfigureAwait(false);

        return new AdbExecStream(socket);
    }

    public async Task<bool> TryExecuteAsync(
        DeviceId device,
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            var output = await ExecuteAsync(device, command, mergeStandardError: true, cancellationToken)
                .ConfigureAwait(false);
            return output.Trim().Length == 0;
        }
        catch (Core.Exceptions.DeviceException)
        {
            return false;
        }
    }

    public async Task ExecuteExpectingSilenceAsync(
        DeviceId device,
        string command,
        DevicePath path,
        CancellationToken cancellationToken)
    {
        var output = await ExecuteAsync(device, command, mergeStandardError: true, cancellationToken)
            .ConfigureAwait(false);

        var message = output.Trim();
        if (message.Length > 0)
        {
            throw AdbFailure.Translate(message, path);
        }
    }
}

/// <summary>
/// Bidirectional stream over an <c>exec:</c> service, owning the socket.
/// </summary>
/// <remarks>
/// Writing matters as much as reading: a resumed upload streams its remaining bytes into
/// <c>dd</c>'s stdin and then calls <see cref="CompleteWriting"/>, whose half-close is the only way
/// to tell <c>dd</c> the input has ended (spec §13).
/// </remarks>
internal sealed class AdbExecStream(AdbSocket socket) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
        => await socket.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
        => await socket.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

    /// <summary>Half-closes the sending side so the remote command sees end of input.</summary>
    public void CompleteWriting() => socket.ShutdownSend();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            socket.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await socket.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
