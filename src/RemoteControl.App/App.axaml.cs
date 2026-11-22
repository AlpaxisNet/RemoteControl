using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RemoteControl.App.Hotkeys;
using RemoteControl.App.Platform;
using RemoteControl.App.Tray;
using RemoteControl.Core;
using RemoteControl.Ui.Common;

namespace RemoteControl.App;

/// <summary>
/// The application: the Fluent theme and the AvaloniaEdit styles; it puts the program together and shows the login window. Headless
/// (<c>--headless</c>), on Avalonia's screenless platform, it shows nothing on the desktop: no dialog waits for an answer, no browser,
/// sound or file manager is started, and Ctrl+C or a termination signal ends the program.
/// </summary>
public sealed class App : Application
{
    private AppRuntime? _runtime;
    private readonly List<PosixSignalRegistration> _signals = new();

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        bool headless = Program.Arguments.Headless;
        if (headless)
        {
            // Nothing can be answered or looked at: dialogs only log, error windows are not made, no program is started on a desktop.
            Dialogs.Unattended = true;
            UiSafe.ShowErrorWindows = false;
            ExternalPrograms.Disabled = true;
        }
        try
        {
            AppPaths paths = AppPaths.ForCurrentUser();
            _runtime = AppRuntime.Start(new AppRuntimeSettings
            {
                Paths = paths,
                Desktop = services => new AvaloniaDesktop(desktop, services),
                Tray = CreateTray,
                Hotkey = CreateHotkeyBackend,
                Debug = Program.Debug,
                InstallErrorHandlers = true,
                Headless = headless,
            });
            if (headless) StopOnSignals(_runtime.Shell, desktop);
            // Avalonia 11 takes no files dropped from a file manager on X11; the bridge does (FileDrop).
            else if (OperatingSystem.IsLinux()) FileDrop.Bridge = new X11FileDropBridge(_runtime.Services.Log);
            // The application's handlers (UiErrors) now write the crash log; the early ones would write every report twice.
            Program.EarlyHandlers?.Dispose();
            Program.EarlyHandlers = null;
        }
        catch (Exception exception)
        {
            // A failure while the program is put together is written and shown, then the program ends.
            Program.EarlyCrashLog?.Write("startup", exception, true);
            UiSafe.Log.Error("The program could not start.", exception);
            UiSafe.Fire(async () =>
            {
                await Dialogs.ShowErrorAsync("Remote Control", "Remote Control could not start:\n\n" + exception.Message).ConfigureAwait(true);
                desktop.Shutdown(1);
            }, "start-up failure");
        }
    }

    /// <summary>Ends a headless program on Ctrl+C or a termination signal, as quitting from the tray would, without the unsaved-editors question.</summary>
    private void StopOnSignals(AppShell shell, IClassicDesktopStyleApplicationLifetime desktop)
    {
        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            Dispatcher.UIThread.Post(() => UiSafe.Run(shell.QuitWithoutAsking, "headless stop"));
        }
        foreach (PosixSignal signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM })
        {
            try
            {
                _signals.Add(PosixSignalRegistration.Create(signal, Stop));
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
            {
                UiSafe.Log.Warn("The headless program does not stop on " + signal + ".", exception);
            }
        }
        desktop.Exit += (_, _) =>
        {
            foreach (PosixSignalRegistration registration in _signals) registration.Dispose();
            _signals.Clear();
        };
    }

    private ITrayIcon? CreateTray(AppServices services)
    {
        TrayAvailability? availability = null;
        if (!TrayAvailability.AlwaysAvailable)
        {
            availability = new TrayAvailability(services.Log);
            availability.Start();
        }
        TrayAvailability? check = availability;
        try
        {
            return new AvaloniaTrayIcon(this, services.Paths, services.Log, () => check?.IsAvailable ?? TrayAvailability.AlwaysAvailable, availability);
        }
        catch (Exception exception)
        {
            // No tray on this desktop: TrayAvailable stays false and main windows ask before signing out.
            availability?.Dispose();
            services.Log.Warn("The tray icon could not be made.", exception);
            return null;
        }
    }

    private static IHotkeyBackend CreateHotkeyBackend()
    {
        if (OperatingSystem.IsWindows()) return new WindowsHotkeyBackend();
        if (OperatingSystem.IsLinux()) return new X11HotkeyBackend();
        return new NoHotkeyBackend("Global hotkeys are not supported on this system.");
    }
}
