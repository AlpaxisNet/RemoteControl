using Avalonia.Controls;
using RemoteControl.App.Platform;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.App;

/// <summary>
/// The program as its windows see it (<see cref="IAppShell"/>): the sessions and the active one, connecting a chosen server (new session
/// or "switch server"), reconnecting, signing out, quitting, the activity clock and the attention requests. It owns the program's
/// components (tray, hotkey, MCP bridge, extensions, the start windows) and releases them when the program quits. UI thread only.
/// </summary>
/// <remarks>
/// Connections are made by the <see cref="SessionFactory"/> it is given (real ones in the program, <c>FakeRcSession</c>s in tests), so the
/// whole connect, switch, reconnect and sign-out logic runs without a network. Nothing here blocks: connecting is awaited, a connection
/// is disposed without waiting (defect D18).
/// </remarks>
internal sealed class AppShell : IAppShell
{
    private readonly AppServices _services;
    private readonly ToolRegistry _tools;
    private readonly SessionFactory _factory;
    private readonly IDesktop _desktop;
    private readonly Action<SessionContext> _startFeatures;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<SessionContext> _sessions = [];
    private readonly HashSet<SessionContext> _primary = [];
    private readonly Dictionary<Type, object> _programServices = [];
    private readonly List<IDisposable> _owned = [];
    private readonly Dictionary<SessionContext, IDisposable> _packetLogs = [];
    private SessionContext? _active;
    private IStartWindows? _start;
    private Task? _quitQuestion;
    private bool _quitting;

    /// <summary>Creates the shell.</summary>
    /// <param name="services">The program's services.</param>
    /// <param name="tools">The tool registry with every group registered.</param>
    /// <param name="factory">Makes connections.</param>
    /// <param name="desktop">The desktop (windows, flash, beep, shutdown).</param>
    /// <param name="settings">What else the shell needs; defaults when null.</param>
    public AppShell(AppServices services, ToolRegistry tools, SessionFactory factory, IDesktop desktop, AppShellSettings? settings = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        settings ??= new AppShellSettings();
        _startFeatures = settings.StartFeatures ?? GroupFeatures.StartSession;
        _confirm = settings.Confirm ?? ((title, message) => Dialogs.ConfirmAsync(title, message, null, "Quit", "Cancel"));
        _clock = settings.Clock ?? (() => DateTimeOffset.UtcNow);
        Post = settings.Post ?? (action => UiDispatch.Post(action, "session event"));
        Debug = settings.Debug ?? DebugOutput.None;
        LastActivity = _clock();
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionContext> Sessions => _sessions;

    /// <inheritdoc />
    public SessionContext? ActiveSession
    {
        get => _active;
        set
        {
            if (value is not null && !_sessions.Contains(value)) return;
            if (ReferenceEquals(_active, value)) return;
            _active = value;
            RaiseSessionsChanged();
        }
    }

    /// <inheritdoc />
    public event EventHandler? SessionsChanged;

    /// <summary>Raised by <see cref="RequestAttention"/> for a private message (the tray icon blinks).</summary>
    public event EventHandler<SessionContext>? PrivateMessageAttention;

    /// <summary>Raised once when the program starts to quit (after the question, before anything is released).</summary>
    public event EventHandler? Quitting;

    /// <inheritdoc />
    public bool TrayAvailable => TrayShown?.Invoke() == true;

    /// <summary>Tells whether a tray icon is really shown (set by the program when it made the tray).</summary>
    public Func<bool>? TrayShown { get; set; }

    /// <inheritdoc />
    public DateTimeOffset LastActivity { get; private set; }

    /// <summary>Where session events run (the UI thread).</summary>
    public Action<Action> Post { get; }

    /// <summary>The <c>--debug</c>/<c>--packetlog</c> output.</summary>
    public DebugOutput Debug { get; }

    /// <summary>The program's services.</summary>
    public AppServices Services => _services;

    /// <summary>The tool registry.</summary>
    public ToolRegistry Tools => _tools;

    /// <summary>The desktop.</summary>
    public IDesktop Desktop => _desktop;

    /// <summary>The start windows (login, server list), once attached.</summary>
    public IStartWindows? Start => _start;

    /// <summary>Whether the program is quitting (or has quit).</summary>
    public bool IsQuitting => _quitting;

    /// <summary>The sessions whose main windows the tray and the hotkey show and hide together (the <c>[*]</c> marker).</summary>
    public IReadOnlyCollection<SessionContext> PrimarySessions => _primary;

    /// <summary>Gives the shell the start windows (made after the shell, because they need it).</summary>
    /// <param name="start">The start windows.</param>
    public void AttachStart(IStartWindows start) => _start = start ?? throw new ArgumentNullException(nameof(start));

    /// <summary>Offers a service of the whole program (<see cref="GetService{T}"/>).</summary>
    /// <typeparam name="T">The interface.</typeparam>
    /// <param name="service">The service.</param>
    public void SetService<T>(T service) where T : class => _programServices[typeof(T)] = service ?? throw new ArgumentNullException(nameof(service));

    /// <summary>Makes a component the shell's: it is disposed when the program quits, the last one added first.</summary>
    /// <param name="component">The component.</param>
    public void Own(IDisposable component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (_quitting) UiSafe.Run(component.Dispose, "dispose component");
        else _owned.Add(component);
    }

    /// <inheritdoc />
    public T? GetService<T>() where T : class => _programServices.TryGetValue(typeof(T), out object? service) ? (T)service : null;

    // ---- Connecting ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The connect function of the start windows: makes a connection, connects it to the chosen server and, on success, puts it into
    /// the session to switch (disposing that session's old connection) or into a new session whose groups are started and whose main
    /// window is opened; then hides the login window. On failure the connection is disposed and no session is made.
    /// </summary>
    /// <param name="choice">The server chosen.</param>
    /// <returns>Whether it is connected, and why not.</returns>
    public async Task<ConnectResult> ConnectAsync(ServerChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (_quitting) return ConnectResult.Failed("The program is closing.");
        IRcSession connection;
        try
        {
            connection = CreateConnection(choice.Login);
        }
        catch (Exception exception)
        {
            _services.Log.Error("A connection could not be made.", exception);
            return ConnectResult.Failed(exception.Message);
        }
        Debug.Line("connecting to " + choice.Login.Server.Name + " (" + choice.Login.Server.Host + ":" + choice.Login.Server.Port + ")");
        ConnectResult result = await ConnectConnectionAsync(connection, choice.Login).ConfigureAwait(true);
        if (!result.Success || _quitting)
        {
            UiSafe.Run(connection.Dispose, "dispose failed connection");
            Debug.Line("connect failed: " + (result.Error ?? "closing"));
            return result.Success ? ConnectResult.Failed("The program is closing.") : result;
        }

        if (choice.SwitchFor is { IsEnded: false } target && _sessions.Contains(target))
        {
            IRcSession old = target.Session;
            target.Replace(connection, choice.Login);
            if (!ReferenceEquals(old, connection)) UiSafe.Run(old.Dispose, "dispose switched connection");
            _active = target;
            target.OpenTool(ToolIds.MainWindow);
            RaiseSessionsChanged();
        }
        else
        {
            var context = new SessionContext(_services, this, _tools, connection, choice.Login);
            _sessions.Add(context);
            _active = context;
            WatchPackets(context);
            UiSafe.Run(() => _startFeatures(context), "start session groups");
            context.OpenTool(ToolIds.MainWindow);
            RaiseSessionsChanged();
        }
        Debug.Line("connected to " + choice.Login.Server.Name);
        _start?.HideLogin();
        return result;
    }

    /// <inheritdoc />
    public async Task<ConnectResult> ReconnectAsync(SessionContext session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsEnded || _quitting || !_sessions.Contains(session)) return ConnectResult.Failed("The session has ended.");
        IRcSession old = session.Session;
        SessionLogin login = session.Login;
        IRcSession connection;
        try
        {
            connection = CreateConnection(login);
        }
        catch (Exception exception)
        {
            _services.Log.Error("A connection could not be made.", exception);
            UiSafe.Run(old.Dispose, "dispose old connection");
            return ConnectResult.Failed(exception.Message);
        }
        Debug.Line("reconnecting to " + login.Server.Name);
        ConnectResult result = await ConnectConnectionAsync(connection, login).ConfigureAwait(true);
        if (result.Success && !session.IsEnded && !_quitting)
        {
            session.Replace(connection, login);
            RaiseSessionsChanged();
        }
        else
        {
            UiSafe.Run(connection.Dispose, "dispose failed connection");
            if (result.Success) result = ConnectResult.Failed("The session has ended.");
        }
        UiSafe.Run(old.Dispose, "dispose old connection");
        return result;
    }

    private IRcSession CreateConnection(SessionLogin login)
    {
        Core.AppLog log = _services.Log;
        // No option of the options file concerns the protocol: versions and time limits keep the protocol's defaults.
        var options = new RcSessionOptions
        {
            Account = login.Account,
            Password = login.Password,
            Post = Post,
            // With --debug these lines reach standard error through the program log.
            Log = line => log.Info("[protocol] " + line),
        };
        return _factory(options) ?? throw new InvalidOperationException("No connection was made.");
    }

    private static async Task<ConnectResult> ConnectConnectionAsync(IRcSession connection, SessionLogin login)
    {
        try
        {
            ConnectResult? result = await connection.ConnectAsync(login.Server).ConfigureAwait(true);
            return result ?? ConnectResult.Failed("Unable to connect.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ConnectResult.Failed(exception.Message);
        }
    }

    private void WatchPackets(SessionContext context)
    {
        if (!Debug.Packets) return;
        _packetLogs[context] = context.Attach(session => Debug.WatchPackets(session));
    }

    // ---- Sign-out and quit -----------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public void SignOut(SessionContext session)
    {
        if (session is null || !_sessions.Remove(session)) return;
        _primary.Remove(session);
        if (_packetLogs.Remove(session, out IDisposable? packets)) UiSafe.Run(packets.Dispose, "packet log");
        if (ReferenceEquals(_active, session)) _active = _sessions.Count > 0 ? _sessions[^1] : null;
        Debug.Line("signed out of " + session.ServerName);
        UiSafe.Run(session.End, "end session");
        RaiseSessionsChanged();
        if (_sessions.Count == 0 && !_quitting) _start?.ShowLogin();
    }

    /// <inheritdoc />
    public void Quit()
    {
        if (_quitting || _quitQuestion is { IsCompleted: false }) return;
        _quitQuestion = UiSafe.RunAsync(QuitAsync, "quit");
    }

    /// <summary>What <see cref="Quit"/> does, awaitable (tests): asks about unsaved editors, then ends everything.</summary>
    /// <returns>Whether the program quit.</returns>
    internal async Task<bool> QuitAsync()
    {
        if (_quitting) return true;
        IReadOnlyList<string> unsaved = UnsavedEditors();
        if (unsaved.Count > 0)
        {
            string message = "These script editors have changes the server has not confirmed:\n\n" + string.Join("\n", unsaved) + "\n\nQuit anyway?";
            bool quit = await _confirm("Quit", message).ConfigureAwait(true);
            if (!quit || _quitting) return _quitting;
        }
        QuitNow();
        return true;
    }

    /// <summary>The titles of every session's script editors with unconfirmed changes, with the server's name when there are several sessions.</summary>
    /// <returns>The titles.</returns>
    internal IReadOnlyList<string> UnsavedEditors()
    {
        var titles = new List<string>();
        foreach (SessionContext session in _sessions)
        {
            IReadOnlyList<string>? unsaved = null;
            UiSafe.Run(() => unsaved = session.Services.Get<IScriptEditors>()?.UnsavedTitles, "unsaved editors");
            if (unsaved is null) continue;
            foreach (string title in unsaved) titles.Add(_sessions.Count > 1 ? title + " (" + session.ServerName + ")" : title);
        }
        return titles;
    }

    private void QuitNow()
    {
        _quitting = true;
        Debug.Line("quitting");
        UiSafe.Run(() => Quitting?.Invoke(this, EventArgs.Empty), "quitting");
        foreach (SessionContext session in _sessions.ToArray()) UiSafe.Run(session.End, "end session");
        _sessions.Clear();
        _primary.Clear();
        foreach (IDisposable packets in _packetLogs.Values) UiSafe.Run(packets.Dispose, "packet log");
        _packetLogs.Clear();
        _active = null;
        UiSafe.Run(() => SessionsChanged?.Invoke(this, EventArgs.Empty), "sessions changed");
        for (int index = _owned.Count - 1; index >= 0; index--) UiSafe.Run(_owned[index].Dispose, "dispose component");
        _owned.Clear();
        UiSafe.Run(_services.Dispose, "dispose services");
        UiSafe.Run(_desktop.Shutdown, "shutdown");
    }

    // ---- Activity and attention ------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public void NoteActivity() => LastActivity = _clock();

    /// <inheritdoc />
    public void RequestAttention(SessionContext session, AttentionKind kind)
    {
        if (session is null || session.IsEnded || _quitting) return;
        if (_desktop.AnyWindowActive) return;
        switch (kind)
        {
            case AttentionKind.Alert:
                FlashMainWindow(session);
                break;
            case AttentionKind.AlertWithSound:
                FlashMainWindow(session);
                if (_services.Options.Current.NotificationSounds) UiSafe.Run(_desktop.Beep, "alert sound");
                break;
            case AttentionKind.PrivateMessage:
                UiSafe.Run(() => PrivateMessageAttention?.Invoke(this, session), "private message attention");
                break;
        }
    }

    private void FlashMainWindow(SessionContext session)
    {
        if (MainWindowOf(session) is { } window) UiSafe.Run(() => _desktop.Flash(window), "flash window");
    }

    // ---- Windows of the program ------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public void ShowLogin() => _start?.ShowLogin();

    /// <inheritdoc />
    public void ShowServerList(SessionContext? switchFor) => _start?.ShowServerList(switchFor is { IsEnded: false } ? switchFor : null);

    /// <inheritdoc />
    public void ShowListServerSettings(Window? owner) => _start?.ShowListServerSettings(owner);

    /// <summary>The main window of a session, or null.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The window.</returns>
    public static Window? MainWindowOf(SessionContext session) =>
        session.Services.Get<ISessionChat>()?.Window ?? session.Windows.Find(ToolIds.MainWindow);

    /// <summary>Whether a session's main window is shown.</summary>
    /// <param name="session">The session.</param>
    /// <returns>Whether.</returns>
    public static bool IsMainWindowVisible(SessionContext session) => MainWindowOf(session) is { IsVisible: true };

    /// <summary>Shows a session's main window and brings it to the front (opening it again when it was closed).</summary>
    /// <param name="session">The session.</param>
    public void ShowMainWindow(SessionContext session)
    {
        if (session is null || session.IsEnded || !_sessions.Contains(session)) return;
        if (session.Services.Get<ISessionChat>() is { } chat) UiSafe.Run(chat.Activate, "show main window");
        else session.OpenTool(ToolIds.MainWindow);
    }

    /// <summary>Hides a session's main window (the session stays signed in).</summary>
    /// <param name="session">The session.</param>
    public static void HideMainWindow(SessionContext session)
    {
        Window? window = MainWindowOf(session);
        if (window is null) return;
        if (window is RcWindow rc) UiSafe.Run(rc.HideAndRemember, "hide main window");
        else UiSafe.Run(window.Hide, "hide main window");
    }

    /// <summary>Marks or unmarks a session as primary (<c>[*]</c>).</summary>
    /// <param name="session">The session.</param>
    public void TogglePrimary(SessionContext session)
    {
        if (session is null || !_sessions.Contains(session)) return;
        if (!_primary.Remove(session)) _primary.Add(session);
        RaiseSessionsChanged();
    }

    /// <summary>
    /// What a click on the tray icon and the global hotkey do (<c>toggleTrayApplication</c>): with primary sessions that are connected,
    /// hide all of their main windows when any is shown, else show them all; otherwise show or hide the active session's main window;
    /// otherwise show or hide the login window.
    /// </summary>
    public void ToggleVisibility()
    {
        if (_quitting) return;
        SessionContext[] primaries = _sessions.Where(session => _primary.Contains(session) && session.Session.IsConnected).ToArray();
        if (primaries.Length > 0)
        {
            bool hide = primaries.Any(IsMainWindowVisible);
            foreach (SessionContext session in primaries)
            {
                if (hide) HideMainWindow(session);
                else ShowMainWindow(session);
            }
        }
        else if (_active is { } active)
        {
            if (IsMainWindowVisible(active)) HideMainWindow(active);
            else ShowMainWindow(active);
        }
        else if (_start is { } start)
        {
            if (start.IsLoginVisible) start.HideLogin();
            else start.ShowLogin();
        }
    }

    /// <summary>
    /// What "Open" of the tray does (<c>onTrayOpen</c>): shows the main windows of the connected primary sessions, else the active
    /// session's, else the login window.
    /// </summary>
    public void OpenFromTray()
    {
        if (_quitting) return;
        SessionContext[] primaries = _sessions.Where(session => _primary.Contains(session) && session.Session.IsConnected).ToArray();
        if (primaries.Length > 0)
        {
            foreach (SessionContext session in primaries) ShowMainWindow(session);
            _active = primaries[^1];
            RaiseSessionsChanged();
        }
        else if (_active is { } active) ShowMainWindow(active);
        else _start?.ShowLogin();
    }

    private void RaiseSessionsChanged() => UiSafe.Run(() => SessionsChanged?.Invoke(this, EventArgs.Empty), "sessions changed");
}

/// <summary>What else an <see cref="AppShell"/> needs; every member has a default.</summary>
internal sealed record AppShellSettings
{
    /// <summary>Starts the groups' part of a new session (default: <see cref="GroupFeatures.StartSession"/>).</summary>
    public Action<SessionContext>? StartFeatures { get; init; }

    /// <summary>Asks a question with Quit/Cancel (default: <see cref="Dialogs.ConfirmAsync"/>).</summary>
    public Func<string, string, Task<bool>>? Confirm { get; init; }

    /// <summary>The clock of <see cref="IAppShell.LastActivity"/>.</summary>
    public Func<DateTimeOffset>? Clock { get; init; }

    /// <summary>Where session events run (default: the UI thread through <see cref="UiDispatch.Post"/>).</summary>
    public Action<Action>? Post { get; init; }

    /// <summary>The <c>--debug</c>/<c>--packetlog</c> output.</summary>
    public DebugOutput? Debug { get; init; }
}
