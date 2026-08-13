using Handspan.Adb;
using Handspan.Core.Interfaces;
using Handspan.Data;
using Handspan.Media;
using Handspan.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Handspan.Services;

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
