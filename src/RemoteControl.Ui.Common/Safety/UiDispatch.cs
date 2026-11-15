using Avalonia.Controls;
using Avalonia.Threading;
using RemoteControl.Core;

namespace RemoteControl.Ui.Common;

/// <summary>
/// Running code on the UI thread safely: <see cref="Post"/> (fire and forget, exceptions reported by <see cref="UiSafe"/>),
/// <see cref="InvokeAsync(Action)"/> (awaitable, exceptions go to the awaiting caller), <see cref="Debounce"/> (run once after a quiet
/// period) and <see cref="Repeat"/> (a repeating timer that stops when its owner window closes).
/// </summary>
/// <example>
/// <code>
/// UiDispatch.Post(() =&gt; status.Text = "Connected", "Session.Connected");
/// var save = UiDispatch.Debounce(TimeSpan.FromMilliseconds(300), SaveFilter, "Filter.Save", owner: this);
/// filterBox.TextChanged += (_, _) =&gt; save.Trigger();
/// UiDispatch.Repeat(TimeSpan.FromMilliseconds(500), BlinkPmIcons, "PlayerList.Blink", owner: this);
/// </code>
/// </example>
public static class UiDispatch
{
    /// <summary>Queues <paramref name="action"/> on the UI thread; an exception is reported by <see cref="UiSafe"/>.</summary>
    /// <param name="action">The code.</param>
    /// <param name="context">What it does.</param>
    /// <param name="priority">The dispatcher priority; <see cref="DispatcherPriority.Default"/> when <c>null</c>.</param>
    public static void Post(Action action, string context = "UiDispatch.Post", DispatcherPriority? priority = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            Dispatcher.UIThread.Post(() => UiSafe.Run(action, context), priority ?? DispatcherPriority.Default);
        }
        catch (Exception exception)
        {
            UiSafe.Log.Error("Posting to the UI thread failed (" + context + ").", exception);
        }
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread (at once when already on it) and completes when it ran.</summary>
    /// <param name="action">The code.</param>
    /// <returns>A task that fails with the action's exception.</returns>
    public static Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    /// <summary>Runs <paramref name="function"/> on the UI thread and returns its result.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="function">The code.</param>
    /// <returns>A task with the result, or failing with the function's exception.</returns>
    public static Task<T> InvokeAsync<T>(Func<T> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (Dispatcher.UIThread.CheckAccess())
        {
            try
            {
                return Task.FromResult(function());
            }
            catch (Exception exception)
            {
                return Task.FromException<T>(exception);
            }
        }
        return Dispatcher.UIThread.InvokeAsync(function).GetTask();
    }

    /// <summary>Runs asynchronous work started on the UI thread and completes when it completes.</summary>
    /// <param name="function">The asynchronous code.</param>
    /// <returns>A task that fails with the work's exception.</returns>
    public static Task InvokeAsync(Func<Task> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return Dispatcher.UIThread.CheckAccess() ? function() : Dispatcher.UIThread.InvokeAsync(function);
    }

    /// <summary>
    /// A debouncer: each <see cref="Debouncer.Trigger"/> restarts the delay, and <paramref name="action"/> runs once on the UI thread
    /// after <paramref name="delay"/> without a new trigger. Stops when <paramref name="owner"/> closes.
    /// </summary>
    /// <param name="delay">The quiet period.</param>
    /// <param name="action">The code.</param>
    /// <param name="context">What it does.</param>
    /// <param name="owner">The window it belongs to, or <c>null</c>.</param>
    /// <returns>The debouncer; dispose it to stop it.</returns>
    public static Debouncer Debounce(TimeSpan delay, Action action, string context, Window? owner = null) => new(delay, action, context, owner);

    /// <summary>
    /// A repeating timer on the UI thread. A tick that throws is reported; after <see cref="RepeatingTimer.MaximumFailures"/> failing
    /// ticks in a row the timer stops (logged). The timer stops when <paramref name="owner"/> closes (rule 5).
    /// </summary>
    /// <param name="interval">The time between ticks (at least 1 ms).</param>
    /// <param name="tick">The code.</param>
    /// <param name="context">What it does.</param>
    /// <param name="owner">The window it belongs to, or <c>null</c> (then dispose the timer yourself).</param>
    /// <returns>The running timer.</returns>
    public static RepeatingTimer Repeat(TimeSpan interval, Action tick, string context, Window? owner = null) => new(interval, tick, context, owner);
}

/// <summary>Runs an action once after a quiet period (see <see cref="UiDispatch.Debounce"/>). Use on the UI thread.</summary>
public sealed class Debouncer : IDisposable
{
    private readonly Action _action;
    private readonly string _context;
    private readonly DispatcherTimer _timer;
    private Window? _owner;
    private bool _disposed;

    internal Debouncer(TimeSpan delay, Action action, string context, Window? owner)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _context = context;
        _timer = new DispatcherTimer { Interval = delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay };
        _timer.Tick += OnTick;
        _owner = owner;
        if (owner is not null) owner.Closed += OnOwnerClosed;
    }

    /// <summary>Whether the action waits to run.</summary>
    public bool IsPending => _timer.IsEnabled;

    /// <summary>Starts (or restarts) the delay.</summary>
    public void Trigger()
    {
        if (_disposed) return;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Drops a pending run.</summary>
    public void Cancel() => _timer.Stop();

    /// <summary>Runs a pending action now.</summary>
    public void Flush()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        UiSafe.Run(_action, _context);
    }

    /// <summary>Stops the debouncer; a pending run is dropped.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        if (_owner is not null) _owner.Closed -= OnOwnerClosed;
        _owner = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (!_disposed) UiSafe.Run(_action, _context);
    }

    private void OnOwnerClosed(object? sender, EventArgs e) => Dispose();
}

/// <summary>A repeating UI-thread timer (see <see cref="UiDispatch.Repeat"/>).</summary>
public sealed class RepeatingTimer : IDisposable
{
    /// <summary>How many failing ticks in a row stop the timer.</summary>
    public const int MaximumFailures = 3;

    private readonly Action _tick;
    private readonly string _context;
    private readonly DispatcherTimer _timer;
    private Window? _owner;
    private int _failures;
    private bool _disposed;

    internal RepeatingTimer(TimeSpan interval, Action tick, string context, Window? owner)
    {
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        _context = context;
        _timer = new DispatcherTimer { Interval = interval < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : interval };
        _timer.Tick += OnTick;
        _owner = owner;
        if (owner is not null) owner.Closed += OnOwnerClosed;
        _timer.Start();
    }

    /// <summary>Whether the timer runs.</summary>
    public bool IsRunning => _timer.IsEnabled;

    /// <summary>How many ticks ran.</summary>
    public int TickCount { get; private set; }

    /// <summary>The time between ticks.</summary>
    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : value;
    }

    /// <summary>Pauses the timer.</summary>
    public void Stop() => _timer.Stop();

    /// <summary>Resumes a paused timer (not after <see cref="Dispose"/>).</summary>
    public void Start()
    {
        if (!_disposed) _timer.Start();
    }

    /// <summary>Stops the timer for good and removes its handlers.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        if (_owner is not null) _owner.Closed -= OnOwnerClosed;
        _owner = null;
    }

    /// <summary>Runs one tick now (tests).</summary>
    internal void TickNow() => OnTick(this, EventArgs.Empty);

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        TickCount++;
        if (UiSafe.Run(_tick, _context))
        {
            _failures = 0;
            return;
        }
        if (++_failures >= MaximumFailures)
        {
            UiSafe.Log.Warn($"The timer {_context} failed {MaximumFailures} times in a row and was stopped.");
            Dispose();
        }
    }

    private void OnOwnerClosed(object? sender, EventArgs e) => Dispose();
}
