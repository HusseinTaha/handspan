using Handspan.Core.Platform;
using Microsoft.Win32;

namespace Handspan.App.Platform.Windows;

/// <summary>
/// Sleep and wake notifications on Windows (spec §81).
/// </summary>
/// <remarks>
/// Transfers must be paused before the machine suspends: a USB connection does not survive sleep, and
/// pausing deliberately keeps the partial files so the queue resumes instead of restarting.
/// </remarks>
internal sealed class WindowsPowerEvents : IPowerEvents, IDisposable
{
    private bool _started;

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Suspending?.Invoke(this, EventArgs.Empty);
                break;
            case PowerModes.Resume:
                Resumed?.Invoke(this, EventArgs.Empty);
                break;
            case PowerModes.StatusChange:
            default:
                break;
        }
    }

    /// <summary>Log-off and shutdown deserve the same treatment as sleep.</summary>
    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        => Suspending?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _started = false;
    }
}
