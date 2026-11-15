using RemoteControl.App.Hotkeys;
using RemoteControl.App.Mcp;
using RemoteControl.App.Platform;
using RemoteControl.App.Tray;
using RemoteControl.Core;
using RemoteControl.Extensions;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Extensions;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.Start;

namespace RemoteControl.App;

/// <summary>How <see cref="AppRuntime.Start"/> puts the program together; the program uses the real parts, tests give fakes.</summary>
internal sealed record AppRuntimeSettings
{
    /// <summary>The folders.</summary>
    public required AppPaths Paths { get; init; }

    /// <summary>How the services are made (the defaults in the program).</summary>
    public AppServicesSettings? ServicesSettings { get; init; }

    /// <summary>Services made already (tests); <see cref="Paths"/> and <see cref="ServicesSettings"/> are then not used.</summary>
    public AppServices? Services { get; init; }

    /// <summary>Makes connections (real <see cref="RcSession"/>s by default).</summary>
    public SessionFactory? Factory { get; init; }

    /// <summary>Makes the desktop for the services.</summary>
    public required Func<AppServices, IDesktop> Desktop { get; init; }

    /// <summary>Makes the tray icon, or null for none.</summary>
    public Func<AppServices, ITrayIcon?>? Tray { get; init; }

    /// <summary>Makes the system's hotkey (none by default).</summary>
    public Func<IHotkeyBackend>? Hotkey { get; init; }

    /// <summary>The MCP approval questions (windows by default).</summary>
    public IMcpApprovals? Approvals { get; init; }

    /// <summary>Where the MCP bridge listens (the C++ names by default).</summary>
    public McpTransportOptions? McpTransport { get; init; }

    /// <summary>The timers of the tray.</summary>
    public IUiTimers? Timers { get; init; }

    /// <summary>The <c>--debug</c>/<c>--packetlog</c> output.</summary>
    public DebugOutput? Debug { get; init; }

    /// <summary>Whether the last-resort handlers are installed (the program; not in tests).</summary>
    public bool InstallErrorHandlers { get; init; }

    /// <summary>Shows the start-up problems to the user (an error dialog each by default).</summary>
    public Func<IReadOnlyList<string>, Task>? ShowProblems { get; init; }

    /// <summary>Questions of the shell (default: dialogs).</summary>
    public Func<string, string, Task<bool>>? Confirm { get; init; }

    /// <summary>The hotkey's failure message (default: a dialog).</summary>
    public Func<string, Task>? ShowHotkeyProblem { get; init; }

    /// <summary>Makes the extensions host (the program's default; tests give one with temporary folders).</summary>
    public Func<AppServices, IAppShell, ExtensionsHost>? Extensions { get; init; }
}

/// <summary>
/// The program put together (review 1.3): services (with the problems they found), the crash log, the bundled fonts, the tool
/// registry with every group, the shell, the extensions host, the start windows, the tray icon, the global hotkey and the MCP bridge;
/// then the login window is shown. Everything it made is released by <see cref="AppShell.Quit"/>.
/// </summary>
internal sealed class AppRuntime
{
    private AppRuntime(AppServices services, IReadOnlyList<string> problems, ToolRegistry tools, AppShell shell, ExtensionsHost extensions, StartController start,
        TrayController? tray, HotkeyController hotkey, McpHost mcpHost, McpBridgeController mcp)
    {
        Services = services;
        Problems = problems;
        Tools = tools;
        Shell = shell;
        Extensions = extensions;
        StartController = start;
        Tray = tray;
        Hotkey = hotkey;
        McpHost = mcpHost;
        Mcp = mcp;
    }

    /// <summary>The services.</summary>
    public AppServices Services { get; }

    /// <summary>What the services could not read (shown to the user).</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>The tool registry.</summary>
    public ToolRegistry Tools { get; }

    /// <summary>The shell.</summary>
    public AppShell Shell { get; }

    /// <summary>The extensions.</summary>
    public ExtensionsHost Extensions { get; }

    /// <summary>The start windows.</summary>
    public StartController StartController { get; }

    /// <summary>The tray, or null without one.</summary>
    public TrayController? Tray { get; }

    /// <summary>The global hotkey.</summary>
    public HotkeyController Hotkey { get; }

    /// <summary>The MCP host.</summary>
    public McpHost McpHost { get; }

    /// <summary>The MCP bridge.</summary>
    public McpBridgeController Mcp { get; }

    /// <summary>The problems shown at start (tests await it).</summary>
    public Task ProblemsShown { get; private set; } = Task.CompletedTask;

    /// <summary>Puts the program together and shows the login window.</summary>
    /// <param name="settings">The parts.</param>
    /// <returns>The program.</returns>
    public static AppRuntime Start(AppRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppServices services;
        IReadOnlyList<string> problems;
        if (settings.Services is { } given)
        {
            services = given;
            problems = Array.Empty<string>();
        }
        else
        {
            AppServicesResult created = AppServices.Create(settings.Paths, settings.ServicesSettings);
            services = created.Services;
            problems = created.Problems;
        }
        DebugOutput debug = settings.Debug ?? DebugOutput.None;
        var owned = new List<IDisposable>();
        if (settings.InstallErrorHandlers)
        {
            owned.Add(UiErrors.Install(services.Log, services.CrashLog));
            services.CrashLog.WriteStartup();
        }
        debug.Follow(services.Log);
        owned.Add(debug);
        BundledFonts.Register(services.Log);

        var tools = new ToolRegistry();
        IDesktop desktop = settings.Desktop(services);
        var shell = new AppShell(services, tools, settings.Factory ?? (options => new RcSession(options)), desktop, new AppShellSettings { Debug = debug, Confirm = settings.Confirm });
        foreach (IDisposable component in owned) shell.Own(component);

        ExtensionsHost extensions = settings.Extensions?.Invoke(services, shell) ?? new ExtensionsHost(services, shell);
        shell.Own(extensions);
        shell.SetService<IExtensionActions>(extensions);
        GroupFeatures.RegisterAll(tools, extensions);

        var start = new StartController(services, shell, shell.ConnectAsync);
        start.LoginClosed += (_, _) => shell.Quit();
        shell.AttachStart(new StartWindows(start));
        shell.Own(start);

        IUiTimers timers = settings.Timers ?? DispatcherTimers.Instance;
        TrayController? tray = null;
        ITrayIcon? icon = null;
        UiSafe.Run(() => icon = settings.Tray?.Invoke(services), "tray icon");
        if (icon is not null)
        {
            tray = new TrayController(shell, icon, timers);
            shell.Own(tray);
            ITrayIcon shown = icon;
            shell.TrayShown = () => shown.IsShown;
        }

        IHotkeyBackend backend = new NoHotkeyBackend("The program was started without a global hotkey.");
        UiSafe.Run(() => backend = settings.Hotkey?.Invoke() ?? backend, "global hotkey");
        var hotkey = new HotkeyController(services, backend, shell.ToggleVisibility, settings.ShowHotkeyProblem);
        shell.Own(hotkey);

        var mcpHost = new McpHost(shell, settings.Approvals ?? new DialogApprovals(services));
        var mcp = new McpBridgeController(services, mcpHost, settings.McpTransport);
        shell.Own(mcp);

        var runtime = new AppRuntime(services, problems, tools, shell, extensions, start, tray, hotkey, mcpHost, mcp);
        start.ShowLogin();
        if (problems.Count > 0)
        {
            Func<IReadOnlyList<string>, Task> show = settings.ShowProblems ?? ShowProblemsAsync;
            runtime.ProblemsShown = UiSafe.RunAsync(() => show(problems), "start-up problems");
        }
        return runtime;
    }

    private static async Task ShowProblemsAsync(IReadOnlyList<string> problems)
    {
        foreach (string problem in problems) await Dialogs.ShowErrorAsync("Remote Control", problem).ConfigureAwait(true);
    }
}
