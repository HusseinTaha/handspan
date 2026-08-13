using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the local SQLite store and the directory cache built on it.
    /// </summary>
    /// <remarks>
    /// The database lives in the platform's application data folder, so it is per-user and deleting it
    /// resets every cache without touching settings on the device.
    /// </remarks>
    public static IServiceCollection AddLocalStore(this IServiceCollection services)
    {
        services.AddSingleton<IAndroidExplorerDatabase>(provider =>
        {
            var shell = provider.GetRequiredService<IShellIntegration>();
            var path = Path.Combine(shell.GetAppDataFolder(), "android-explorer.db");

            return new AndroidExplorerDatabase(
                path, provider.GetRequiredService<ILogger<AndroidExplorerDatabase>>());
        });

        services.AddSingleton<ICacheService, SqliteCacheService>();
        services.AddSingleton<ITransferJobStore, SqliteTransferJobStore>();
        services.AddSingleton<IMediaIndexStore, SqliteMediaIndexStore>();
        services.AddSingleton<IFileIndexStore, SqliteFileIndexStore>();
        services.AddSingleton<IDeviceProfileStore, SqliteDeviceProfileStore>();

        return services;
    }
}
