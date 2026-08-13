using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Handspan.Adb.Tests;

/// <summary>
/// Wires the real transport to a <see cref="FakeAdbServer"/>.
/// </summary>
/// <remarks>
/// Only <see cref="IAdbServer"/> is substituted, so everything under test — framing, sync, exec, the
/// filesystem — is the production code path. Nothing is mocked.
/// </remarks>
internal sealed class FakeServerFixture : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private FakeServerFixture(FakeAdbServer server, ServiceProvider provider)
    {
        Server = server;
        _provider = provider;
    }

    public FakeAdbServer Server { get; }

    public DeviceId Device => new(Server.Serial);

    public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();

    public static FakeServerFixture Start()
    {
        var server = FakeAdbServer.Start();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAdbTransport();

        // Registered after AddAdbTransport so it wins: points the transport at the fake's port.
        services.AddSingleton<IAdbServer>(new FakeServerHandle(server.Port));

        return new FakeServerFixture(server, services.BuildServiceProvider());
    }

    /// <summary>Creates a filesystem with everything enabled, as a modern device would report.</summary>
    public IDeviceFileSystem CreateFileSystem(DeviceCapabilities? capabilities = null)
        => Get<IAdbFileSystemFactory>().Create(Device, capabilities ?? FullCapabilities);

    public static DeviceCapabilities FullCapabilities => new()
    {
        CanBrowseSharedStorage = true,
        CanUpload = true,
        CanDownload = true,
        CanDelete = true,
        CanRename = true,
        CanCreateDirectory = true,
        CanStream = true,
        HasStatV2 = true,
        HasLsV2 = true,
        HasShellV2 = true,
        HasSha256Sum = true,
        HasDdNoTrunc = true,
    };

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class FakeServerHandle(int port) : IAdbServer
    {
        public string? BinaryPath => "(fake)";

        public int? ServerVersion => 41;

        public bool StartedByUs => false;

        public int Port { get; } = port;

        public Task<int> EnsureRunningAsync(CancellationToken cancellationToken) => Task.FromResult(41);

        public Task RestartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
