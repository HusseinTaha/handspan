using Avalonia.Threading;

namespace Handspan.App.Platform;

/// <summary>
/// Marshals work onto the UI thread.
/// </summary>
/// <remarks>
/// Device and transfer events arrive on background threads, and a bound <c>ObservableCollection</c> may only be
/// mutated on the UI thread — so view models must marshal. Going through an interface rather than calling
/// <see cref="Dispatcher"/> directly keeps that behaviour testable: a test host has no message loop, so
/// anything posted to the real dispatcher simply never runs, and a view model that depends on it cannot be
/// verified at all.
/// </remarks>
public interface IUiDispatcher
{
    void Post(Action action);
}

internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
