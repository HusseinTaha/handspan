using Handspan.App.ViewModels;
using Handspan.App.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace Handspan.App;

public partial class App : Application
{
    private IServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _services = Startup.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>(),
            };

            // Must be DisposeAsync: DeviceManager and the transfer manager are IAsyncDisposable-only, and
            // a synchronous Dispose on the container throws because of it. Blocking here is acceptable —
            // it is the last thing the process does, and it lets transfers stop cleanly.
            desktop.ShutdownRequested += (_, _) =>
            {
                if (_services is IAsyncDisposable disposable)
                {
                    disposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
