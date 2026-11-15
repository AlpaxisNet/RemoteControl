using Avalonia.Threading;
using RemoteControl.Core;

namespace RemoteControl.Ui.Common;

/// <summary>
/// The program's last-resort handlers (rule 4): <see cref="AppDomain.UnhandledException"/> (written to the crash log; the process
/// ends afterwards, .NET cannot stop that), <see cref="TaskScheduler.UnobservedTaskException"/> (logged, written, marked observed)
/// and <see cref="Dispatcher.UnhandledException"/> of the UI thread (logged, written, shown through <see cref="UiSafe"/> and marked
/// handled, so that the program keeps running).
/// </summary>
/// <example>
/// In <c>App.OnFrameworkInitializationCompleted</c>:
/// <code>
/// services.CrashLog.WriteStartup();
/// _errorHooks = UiErrors.Install(services.Log, services.CrashLog);
/// </code>
/// </example>
public static class UiErrors
{
    /// <summary>Installs the handlers and configures <see cref="UiSafe"/>.</summary>
    /// <param name="log">The program log.</param>
    /// <param name="crashLog">The crash log.</param>
    /// <returns>Disposing it removes the handlers.</returns>
    public static IDisposable Install(AppLog log, CrashLog crashLog)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(crashLog);
        UiSafe.Configure(log, crashLog);

        UnhandledExceptionEventHandler domain = (_, args) =>
        {
            try
            {
                log.Error("Unhandled exception" + (args.IsTerminating ? "; the program ends." : "."), args.ExceptionObject as Exception);
                crashLog.Write("unhandled_exception", args.ExceptionObject, args.IsTerminating);
            }
            catch (Exception)
            {
                // A last-resort handler must not throw.
            }
        };
        EventHandler<UnobservedTaskExceptionEventArgs> tasks = (_, args) =>
        {
            try
            {
                log.Error("Unobserved task exception.", args.Exception);
                crashLog.Write("unobserved_task", args.Exception, false);
            }
            catch (Exception)
            {
                // A last-resort handler must not throw.
            }
            finally
            {
                args.SetObserved();
            }
        };
        DispatcherUnhandledExceptionEventHandler dispatcher = (_, args) =>
        {
            args.Handled = true;
            UiSafe.Report(args.Exception, "UI dispatcher", "ui_dispatcher");
        };

        AppDomain.CurrentDomain.UnhandledException += domain;
        TaskScheduler.UnobservedTaskException += tasks;
        Dispatcher.UIThread.UnhandledException += dispatcher;
        return new Hooks(() =>
        {
            AppDomain.CurrentDomain.UnhandledException -= domain;
            TaskScheduler.UnobservedTaskException -= tasks;
            Dispatcher.UIThread.UnhandledException -= dispatcher;
        });
    }

    private sealed class Hooks(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
