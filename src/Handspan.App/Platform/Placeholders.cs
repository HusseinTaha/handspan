using Handspan.Core.Platform;

namespace Handspan.App.Platform;

/// <summary>
/// Writes notifications to the log instead of showing them.
/// </summary>
/// <remarks>
/// Deliberately not a real implementation yet: native notifications are a phase 3 deliverable
/// (spec §86), and a stub that logs is honest, whereas one that silently swallows would not be.
/// </remarks>
internal sealed class LogOnlyNotifications : IPlatformNotifications
{
    public Task ShowAsync(string title, string message)
    {
        System.Diagnostics.Debug.WriteLine($"[notification] {title}: {message}");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Does nothing. Sleep and wake handling is a phase 6 deliverable (spec §81).
/// </summary>
internal sealed class NullPowerEvents : IPowerEvents
{
    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public void Start()
    {
        // Nothing to subscribe to yet. The events are declared so that phase 6 can implement this
        // without touching any consumer.
        _ = Suspending;
        _ = Resumed;
    }
}
