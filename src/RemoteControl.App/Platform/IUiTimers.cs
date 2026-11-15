using Avalonia.Threading;
using RemoteControl.Ui.Common;

namespace RemoteControl.App.Platform;

/// <summary>Timers on the UI thread; tests use a fake whose timers fire when the test says so.</summary>
internal interface IUiTimers
{
    /// <summary>Runs an action once after a delay.</summary>
    /// <param name="delay">The delay.</param>
    /// <param name="action">The action.</param>
    /// <returns>Dispose it to cancel.</returns>
    IDisposable After(TimeSpan delay, Action action);

    /// <summary>Runs an action repeatedly.</summary>
    /// <param name="interval">The interval.</param>
    /// <param name="action">The action.</param>
    /// <returns>Dispose it to stop.</returns>
    IDisposable Every(TimeSpan interval, Action action);
}

/// <summary>The real timers: <see cref="DispatcherTimer"/>s, every tick guarded by <see cref="UiSafe"/>.</summary>
internal sealed class DispatcherTimers : IUiTimers
{
    /// <summary>The one instance.</summary>
    public static DispatcherTimers Instance { get; } = new();

    public IDisposable After(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            UiSafe.Run(action, "timer");
        };
        timer.Start();
        return new Stopper(timer);
    }

    public IDisposable Every(TimeSpan interval, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => UiSafe.Run(action, "timer");
        timer.Start();
        return new Stopper(timer);
    }

    private sealed class Stopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
