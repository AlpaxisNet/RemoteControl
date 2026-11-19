using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RemoteControl.App.Hotkeys;
using RemoteControl.App.Platform;
using RemoteControl.App.Tray;
using RemoteControl.Core;
using RemoteControl.Ui.Common;

namespace RemoteControl.App;

/// <summary>The application: the Fluent theme and the AvaloniaEdit styles; it puts the program together and shows the login window.</summary>
public sealed class App : Application
{
    private AppRuntime? _runtime;

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
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
            });
            // Avalonia 11 takes no files dropped from a file manager on X11; the bridge does (FileDrop).
            if (OperatingSystem.IsLinux()) FileDrop.Bridge = new X11FileDropBridge(_runtime.Services.Log);
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
