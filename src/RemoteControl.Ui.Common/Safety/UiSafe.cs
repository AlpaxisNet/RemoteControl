using Avalonia.Threading;
using RemoteControl.Core;

namespace RemoteControl.Ui.Common;

/// <summary>What <see cref="UiSafe.ErrorReported"/> carries.</summary>
public sealed class UiErrorEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    /// <param name="exception">The exception that was caught.</param>
    /// <param name="context">What was running, for example <c>PlayerList.Refresh</c>.</param>
    /// <param name="windowShown">Whether an error window was opened for it.</param>
    public UiErrorEventArgs(Exception exception, string context, bool windowShown)
    {
        Exception = exception;
        Context = context;
        WindowShown = windowShown;
    }

    /// <summary>The exception that was caught.</summary>
    public Exception Exception { get; }

    /// <summary>What was running.</summary>
    public string Context { get; }

    /// <summary>Whether an error window was opened for it (at most one at a time, see <see cref="UiSafe.ErrorWindowInterval"/>).</summary>
    public bool WindowShown { get; }
}

/// <summary>
/// Rule 4 of <c>AGENTS.md</c>: every handler ends in a catch. These wrappers run user-interface code and, when it throws, log the
/// exception to <see cref="AppLog"/>, write it to the <see cref="CrashLog"/> and show one non-modal error window. The window is
/// rate-limited: while one is open, or for <see cref="ErrorWindowInterval"/> after one opened, further errors are only logged, so
/// a failing timer cannot open hundreds of windows. Nothing here throws.
/// </summary>
/// <example>
/// <code>
/// refreshButton.Click += UiSafe.Handler((_, _) =&gt; RefreshList(), "PlayerList.Refresh");
/// saveButton.Click += UiSafe.AsyncHandler(SaveAsync, "Options.Save");          // instead of an async void handler
/// UiSafe.Run(() =&gt; ApplyLayout(), "MainWindow.ApplyLayout");
/// await UiSafe.RunAsync(LoadAsync, "FileBrowser.Load");
/// </code>
/// </example>
public static class UiSafe
{
    private static readonly object s_gate = new();
    private static DateTime s_lastWindow = DateTime.MinValue;
    private static bool s_windowOpen;

    /// <summary>The log exceptions are written to; <see cref="AppLog.Default"/> until <see cref="Configure"/>.</summary>
    public static AppLog Log { get; private set; } = AppLog.Default;

    /// <summary>The crash log exceptions are written to, or <c>null</c> for none (until <see cref="Configure"/>).</summary>
    public static CrashLog? CrashLog { get; private set; }

    /// <summary>Whether errors open an error window (on by default).</summary>
    public static bool ShowErrorWindows { get; set; } = true;

    /// <summary>The shortest time between two error windows (10 seconds by default).</summary>
    public static TimeSpan ErrorWindowInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The title of the error window.</summary>
    public static string ErrorWindowTitle { get; set; } = "Remote Control - Error";

    /// <summary>How many exceptions were reported since start (or <see cref="ResetStatistics"/>).</summary>
    public static int ReportedCount { get; private set; }

    /// <summary>How many error windows were opened.</summary>
    public static int WindowCount { get; private set; }

    /// <summary>How many errors were not shown because a window was open or one opened shortly before.</summary>
    public static int SuppressedCount { get; private set; }

    /// <summary>Raised after an exception was reported (on the thread that reported it).</summary>
    public static event EventHandler<UiErrorEventArgs>? ErrorReported;

    /// <summary>Sets where exceptions are written (<see cref="AppServices.Create"/> and <see cref="UiErrors.Install"/> call it).</summary>
    /// <param name="log">The program log.</param>
    /// <param name="crashLog">The crash log, or <c>null</c>.</param>
    public static void Configure(AppLog log, CrashLog? crashLog)
    {
        Log = log ?? AppLog.Default;
        CrashLog = crashLog;
    }

    /// <summary>Runs <paramref name="action"/>; an exception is reported instead of thrown.</summary>
    /// <param name="action">The code.</param>
    /// <param name="context">What it does, for the log and the error window.</param>
    /// <returns><c>true</c> when it finished without an exception.</returns>
    public static bool Run(Action action, string context)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception)
        {
            Report(exception, context);
            return false;
        }
    }

    /// <summary>Runs <paramref name="function"/>; an exception is reported and <paramref name="fallback"/> returned.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="function">The code.</param>
    /// <param name="context">What it does.</param>
    /// <param name="fallback">The result when it throws.</param>
    /// <returns>The result.</returns>
    public static T Run<T>(Func<T> function, string context, T fallback)
    {
        try
        {
            return function();
        }
        catch (Exception exception)
        {
            Report(exception, context);
            return fallback;
        }
    }

    /// <summary>Awaits <paramref name="function"/>; an exception is reported instead of thrown. A cancellation is not an error.</summary>
    /// <param name="function">The asynchronous code.</param>
    /// <param name="context">What it does.</param>
    /// <returns><c>true</c> when it finished without an exception.</returns>
    public static async Task<bool> RunAsync(Func<Task> function, string context)
    {
        try
        {
            await function().ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            Report(exception, context);
            return false;
        }
    }

    /// <summary>Awaits <paramref name="function"/>; an exception is reported and <paramref name="fallback"/> returned.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="function">The asynchronous code.</param>
    /// <param name="context">What it does.</param>
    /// <param name="fallback">The result when it throws or is cancelled.</param>
    /// <returns>The result.</returns>
    public static async Task<T> RunAsync<T>(Func<Task<T>> function, string context, T fallback)
    {
        try
        {
            return await function().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception exception)
        {
            Report(exception, context);
            return fallback;
        }
    }

    /// <summary>Starts <paramref name="function"/> without awaiting it; an exception is reported. Use it instead of <c>async void</c>.</summary>
    /// <param name="function">The asynchronous code.</param>
    /// <param name="context">What it does.</param>
    public static void Fire(Func<Task> function, string context) => _ = RunAsync(function, context);

    /// <summary>Wraps an event handler so that an exception is reported instead of thrown.</summary>
    /// <param name="handler">The handler.</param>
    /// <param name="context">What it does.</param>
    /// <returns>The safe handler.</returns>
    public static EventHandler Handler(EventHandler handler, string context)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return (sender, args) => Run(() => handler(sender, args), context);
    }

    /// <summary>Wraps an event handler so that an exception is reported instead of thrown.</summary>
    /// <typeparam name="TArgs">The event data type.</typeparam>
    /// <param name="handler">The handler.</param>
    /// <param name="context">What it does.</param>
    /// <returns>The safe handler.</returns>
    public static EventHandler<TArgs> Handler<TArgs>(EventHandler<TArgs> handler, string context)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return (sender, args) => Run(() => handler(sender, args), context);
    }

    /// <summary>An event handler that starts asynchronous work and reports its exception (the only safe form of <c>async void</c>).</summary>
    /// <param name="handler">The asynchronous work.</param>
    /// <param name="context">What it does.</param>
    /// <returns>The handler.</returns>
    public static EventHandler AsyncHandler(Func<Task> handler, string context)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return (_, _) => Fire(handler, context);
    }

    /// <summary>An event handler that starts asynchronous work with the event data and reports its exception.</summary>
    /// <typeparam name="TArgs">The event data type.</typeparam>
    /// <param name="handler">The asynchronous work, given the sender and the event data.</param>
    /// <param name="context">What it does.</param>
    /// <returns>The handler.</returns>
    public static EventHandler<TArgs> AsyncHandler<TArgs>(Func<object?, TArgs, Task> handler, string context)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return (sender, args) => Fire(() => handler(sender, args), context);
    }

    /// <summary>Wraps an action (a callback, a timer tick) so that an exception is reported instead of thrown.</summary>
    /// <param name="action">The code.</param>
    /// <param name="context">What it does.</param>
    /// <returns>The safe action.</returns>
    public static Action Wrap(Action action, string context)
    {
        ArgumentNullException.ThrowIfNull(action);
        return () => Run(action, context);
    }

    /// <summary>
    /// Reports an exception: logs it, writes a crash report and, unless rate-limited, shows the error window (posted to the UI
    /// thread when called from another one). Never throws.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <param name="context">What was running.</param>
    /// <param name="kind">The crash-report kind.</param>
    public static void Report(Exception exception, string context, string kind = "ui_handler")
    {
        try
        {
            context = string.IsNullOrWhiteSpace(context) ? "user interface" : context;
            Log.Error("Unhandled exception in " + context + ".", exception);
            CrashLog?.Write(kind + " " + context, exception, false);
            bool shown = TryClaimWindow();
            lock (s_gate) ReportedCount++;
            if (shown)
            {
                string message = ErrorText(exception, context);
                if (Dispatcher.UIThread.CheckAccess()) OpenWindow(message);
                else Dispatcher.UIThread.Post(() => OpenWindow(message));
            }
            ErrorReported?.Invoke(null, new UiErrorEventArgs(exception, context, shown));
        }
        catch (Exception secondary)
        {
            try
            {
                Log.Error("Reporting an exception failed.", secondary);
            }
            catch (Exception)
            {
                // Nothing more can be done.
            }
        }
    }

    /// <summary>The text of the error window.</summary>
    /// <param name="exception">The exception.</param>
    /// <param name="context">What was running.</param>
    /// <returns>The text.</returns>
    public static string ErrorText(Exception exception, string context) =>
        $"Something went wrong ({context}): {exception.GetType().Name}: {exception.Message}\n\nThe program keeps running. The details were written to the log.";

    /// <summary>Sets the counters and the rate limit back (tests).</summary>
    public static void ResetStatistics()
    {
        lock (s_gate)
        {
            ReportedCount = 0;
            WindowCount = 0;
            SuppressedCount = 0;
            s_lastWindow = DateTime.MinValue;
            s_windowOpen = false;
        }
    }

    private static bool TryClaimWindow()
    {
        lock (s_gate)
        {
            if (!ShowErrorWindows) return false;
            DateTime now = DateTime.UtcNow;
            if (s_windowOpen || now - s_lastWindow < ErrorWindowInterval)
            {
                SuppressedCount++;
                return false;
            }
            s_windowOpen = true;
            s_lastWindow = now;
            WindowCount++;
            return true;
        }
    }

    private static void OpenWindow(string message)
    {
        try
        {
            Task shown = Dialogs.ShowErrorAsync(ErrorWindowTitle, message);
            shown.ContinueWith(_ => { lock (s_gate) s_windowOpen = false; }, TaskScheduler.Default);
        }
        catch (Exception exception)
        {
            lock (s_gate) s_windowOpen = false;
            Log.Error("The error window could not be shown.", exception);
        }
    }
}
