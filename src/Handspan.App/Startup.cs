using Handspan.App.Platform;
using Handspan.App.ViewModels;
using Handspan.Core.Interfaces;
using Handspan.Core.Platform;
using Handspan.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Handspan.App;

/// <summary>
/// The application's composition root.
/// </summary>
/// <remarks>
/// This is the one place allowed to know about concrete implementations from the lower layers; the
/// rest of the UI depends only on interfaces from Handspan.Core (see CLAUDE.md).
/// </remarks>
internal static class Startup
{
    public static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        var platform = PlatformModule.Create();
        var appData = platform.ShellIntegration.GetAppDataFolder();

        var logger = CreateLogger(appData);
        services.AddLogging(builder => builder.AddSerilog(logger, dispose: true));

        // Worth a line in the log: which of the two data locations is in use decides where a support
        // request should look, and a portable build that quietly fell back to per-user data is the single
        // most confusing state this app has. The folder itself is never logged (§43) — the UI shows it.
        if (PortableMode.IsEnabled)
        {
            logger.Information("Running in portable mode; data is stored beside the application.");
        }
        else if (PortableMode.FallbackReason is { } reason)
        {
            logger.Warning("Portable mode was requested but not used: {Reason}.", reason);
        }

        // Platform seam: one implementation per OS, resolved once at startup.
        services.AddSingleton(platform.ShellIntegration);
        services.AddSingleton(platform.AdbBinaryProvider);
        services.AddSingleton(platform.Notifications);
        services.AddSingleton(platform.PowerEvents);

        // ADB transport plus the device manager that sits on it.
        services.AddDeviceServices();

        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();

        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<ExplorerViewModel>();
        services.AddSingleton<TransfersViewModel>();
        services.AddSingleton<GalleryViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<StorageViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<BackupViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        // ValidateOnBuild walks every registration now rather than failing on the first resolve at launch.
        // A missing registration then surfaces as one clear error listing what is wrong — and the
        // composition test calls this same method, so it fails in CI instead of in front of a user.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
        });
    }

    /// <summary>
    /// File logging with the privacy rules from spec §43: no file paths, filenames or EXIF GPS
    /// unless verbose diagnostics are explicitly enabled.
    /// </summary>
    private static Serilog.ILogger CreateLogger(string appDataFolder)
    {
        var logFolder = Path.Combine(appDataFolder, "logs");
        Directory.CreateDirectory(logFolder);

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logFolder, "handspan-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}

/// <summary>Platform implementations chosen for the current OS.</summary>
internal sealed record PlatformBundle(
    IShellIntegration ShellIntegration,
    IAdbBinaryProvider AdbBinaryProvider,
    IPlatformNotifications Notifications,
    IPowerEvents PowerEvents);

