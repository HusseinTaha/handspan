using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Platform;
using AndroidExplorer.Data;
using AndroidExplorer.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Services.Tests;

/// <summary>
/// Verifies the dependency graph actually resolves.
/// </summary>
/// <remarks>
/// A missing registration is invisible to unit tests that construct services by hand, and shows up only as
/// a crash on launch — which is exactly what happened when <see cref="IMediaIndexStore"/> was added without
/// being registered. <c>ValidateOnBuild</c> turns that class of mistake into a test failure.
/// </remarks>
public class CompositionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        // The platform seam is supplied by the app; everything below it must resolve on its own.
        services.AddSingleton<IShellIntegration>(new StubShellIntegration());
        services.AddSingleton<IAdbBinaryProvider>(new StubAdbBinaryProvider());

        services.AddDeviceServices();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// Validates the application's own composition root, not a copy of it.
    /// </summary>
    /// <remarks>
    /// This is the test that catches a view model gaining a constructor dependency nobody registered — which
    /// is invisible to every other test and shows up as a crash on launch. It has happened twice.
    /// </remarks>
    [Fact]
    public async Task The_applications_composition_root_resolves()
    {
        // Startup returns IServiceProvider; the concrete container is what owns disposal.
        var provider = AndroidExplorer.App.Startup.BuildServiceProvider();

        Assert.NotNull(provider);

        if (provider is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_whole_service_graph_resolves()
    {
        // ValidateOnBuild walks every registration, so this throws if anything is unregistered.
        // Must be disposed asynchronously: services such as DeviceManager are IAsyncDisposable-only, and a
        // synchronous Dispose on the container throws because of it.
        await using var provider = BuildProvider();

        Assert.NotNull(provider);
    }

    [Theory]
    [InlineData(typeof(IDeviceManager))]
    [InlineData(typeof(ICacheService))]
    [InlineData(typeof(ITransferJobStore))]
    [InlineData(typeof(IMediaIndexStore))]
    [InlineData(typeof(ITransferManagerFactory))]
    [InlineData(typeof(IThumbnailServiceFactory))]
    [InlineData(typeof(IGalleryServiceFactory))]
    [InlineData(typeof(IAndroidExplorerDatabase))]
    public async Task Each_top_level_service_can_be_resolved(Type serviceType)
    {
        // Must be disposed asynchronously: services such as DeviceManager are IAsyncDisposable-only, and a
        // synchronous Dispose on the container throws because of it.
        await using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService(serviceType));
    }

    private sealed class StubShellIntegration : IShellIntegration
    {
        private readonly string _folder = Path.Combine(
            Path.GetTempPath(), $"ae-composition-{Guid.NewGuid():N}");

        public Task RevealInFileManagerAsync(string localPath) => Task.CompletedTask;

        public Task OpenAsync(string localPath) => Task.CompletedTask;

        public Task OpenWithAsync(string localPath) => Task.CompletedTask;

        public string GetDefaultDownloadFolder() => _folder;

        public string GetAppDataFolder()
        {
            Directory.CreateDirectory(_folder);
            return _folder;
        }
    }

    private sealed class StubAdbBinaryProvider : IAdbBinaryProvider
    {
        public Task<string?> LocateAsync(CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<string> DownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task PrepareForExecutionAsync(string path, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
