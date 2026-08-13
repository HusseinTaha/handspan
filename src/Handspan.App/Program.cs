using Avalonia;

namespace Handspan.App;

internal static class Program
{
    // Avalonia needs to be initialized before any UI type is touched, so keep this method free of
    // anything that could load one.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
