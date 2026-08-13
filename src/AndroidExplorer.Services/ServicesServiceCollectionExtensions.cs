using AndroidExplorer.Adb;
using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Data;
using AndroidExplorer.Media;
using AndroidExplorer.Search;
using Microsoft.Extensions.DependencyInjection;

namespace AndroidExplorer.Services;

public static class ServicesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the application services and the ADB transport they sit on.
    /// </summary>
    public static IServiceCollection AddDeviceServices(this IServiceCollection services)
    {
        services.AddAdbTransport();
        services.AddLocalStore();
        services.AddMediaServices();
        services.AddSearchServices();

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ITransferManagerFactory, TransferManagerFactory>();
        services.AddSingleton<IDeviceManager, DeviceManager>();

        return services;
    }
}
