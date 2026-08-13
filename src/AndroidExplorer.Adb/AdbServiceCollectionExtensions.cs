using Microsoft.Extensions.DependencyInjection;

namespace AndroidExplorer.Adb;

/// <summary>
/// Registers the ADB transport.
/// </summary>
/// <remarks>
/// Callers depend on the interfaces here, not the implementations — which is why the whole transport
/// is internal apart from its interfaces and this method.
/// </remarks>
public static class AdbServiceCollectionExtensions
{
    public static IServiceCollection AddAdbTransport(this IServiceCollection services)
    {
        services.AddSingleton<IAdbServer, AdbServerManager>();
        services.AddSingleton<IAdbConnectionFactory, AdbConnectionFactory>();
        services.AddSingleton<IAdbHostClient, AdbHostClient>();
        services.AddSingleton<IAdbShellClient, AdbShellClient>();
        services.AddSingleton<IAdbFileLister, AdbFileLister>();
        services.AddSingleton<IAdbDeviceProbe, AdbDeviceProbe>();
        services.AddSingleton<IAdbFileSystemFactory, AdbFileSystemFactory>();

        return services;
    }
}
