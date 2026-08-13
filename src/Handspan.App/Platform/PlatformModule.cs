using Handspan.App.Platform.MacOS;
using Handspan.App.Platform.Windows;

namespace Handspan.App.Platform;

/// <summary>
/// Selects the platform implementations for the current OS.
/// </summary>
/// <remarks>
/// This is the only OS branch in the application. Everything else depends on the interfaces in
/// Handspan.Core.Platform, which is what keeps macOS support a packaging job rather than a
/// rewrite (see CLAUDE.md).
/// </remarks>
internal static class PlatformModule
{
    public static PlatformBundle Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new PlatformBundle(
                new WindowsShellIntegration(),
                new WindowsAdbBinaryProvider(),
                new LogOnlyNotifications(),
                new WindowsPowerEvents());
        }

        if (OperatingSystem.IsMacOS())
        {
            return new PlatformBundle(
                new MacShellIntegration(),
                new MacAdbBinaryProvider(),
                new LogOnlyNotifications(),
                new NullPowerEvents());
        }

        throw new PlatformNotSupportedException(
            "Handspan supports Windows and macOS. Linux is possible — Avalonia and the ADB "
            + "transport both work there — but no platform implementations have been written yet.");
    }
}
