using Handspan.Core.Models;

namespace Handspan.Adb;

/// <summary>
/// Opens ADB sockets, ensuring the server is running first.
/// </summary>
/// <remarks>
/// Every socket carries exactly one service, so each operation opens its own. That is the protocol's
/// design, not an inefficiency: sockets are cheap and it keeps concurrent operations independent.
/// </remarks>
internal interface IAdbConnectionFactory
{
    /// <summary>Opens a socket for host-level services, which need no device.</summary>
    Task<AdbSocket> OpenHostAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens a socket, switches it to <paramref name="device"/>, and requests
    /// <paramref name="service"/>.
    /// </summary>
    Task<AdbSocket> OpenServiceAsync(DeviceId device, string service, CancellationToken cancellationToken);
}

internal sealed class AdbConnectionFactory(IAdbServer server) : IAdbConnectionFactory
{
    public async Task<AdbSocket> OpenHostAsync(CancellationToken cancellationToken)
    {
        await server.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        return await AdbSocket.ConnectAsync(server.Port, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdbSocket> OpenServiceAsync(
        DeviceId device,
        string service,
        CancellationToken cancellationToken)
    {
        var socket = await OpenHostAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendServiceAsync(AdbProtocol.HostTransport(device), cancellationToken)
                .ConfigureAwait(false);
            await socket.SendServiceAsync(service, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            await socket.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
