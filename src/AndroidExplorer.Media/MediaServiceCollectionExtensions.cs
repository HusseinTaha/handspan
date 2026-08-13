using AndroidExplorer.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Media;

public static class MediaServiceCollectionExtensions
{
    /// <summary>Registers the thumbnail and gallery services.</summary>
    public static IServiceCollection AddMediaServices(this IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var shell = provider.GetRequiredService<IShellIntegration>();
            var root = Path.Combine(shell.GetAppDataFolder(), "cache", "thumbnails");

            return new ThumbnailCache(root, provider.GetRequiredService<ILogger<ThumbnailCache>>());
        });

        services.AddSingleton<IThumbnailServiceFactory, ThumbnailServiceFactory>();
        services.AddSingleton<IGalleryServiceFactory, GalleryServiceFactory>();
        services.AddSingleton<IBackupServiceFactory, BackupServiceFactory>();

        // One loopback streaming server for the whole application; it is started lazily on first use.
        services.AddSingleton<DeviceStreamServer>();
        services.AddSingleton<MediaPreviewService>();
        services.AddSingleton<Core.Interfaces.IMediaPreviewService>(
            provider => provider.GetRequiredService<MediaPreviewService>());

        return services;
    }
}
