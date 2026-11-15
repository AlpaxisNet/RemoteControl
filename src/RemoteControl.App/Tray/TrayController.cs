using RemoteControl.App.Platform;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.App.Tray;

/// <summary>The tray icon as the program drives it; the real one is <see cref="AvaloniaTrayIcon"/>, tests use a recording fake.</summary>
internal interface ITrayIcon : IDisposable
{
    /// <summary>Whether the icon is really shown on this desktop.</summary>
    bool IsShown { get; }

    /// <summary>Raised when the icon is clicked (activated).</summary>
    event EventHandler? Clicked;

    /// <summary>Sets the tool tip.</summary>
    /// <param name="text">The text.</param>
    void SetToolTip(string text);

    /// <summary>Shows the PM alert picture (<c>pmicon_tray.png</c>) or the normal one (<c>rcicon.png</c>).</summary>
    /// <param name="alert">Which.</param>
    void SetAlert(bool alert);

    /// <summary>Replaces the menu.</summary>
    /// <param name="entries">The entries.</param>
    /// <param name="invoke">What choosing an entry does.</param>
    void SetMenu(IReadOnlyList<TrayMenuEntry> entries, Action<TrayMenuEntry> invoke);
}

/// <summary>
/// The tray icon's behaviour (<c>TMain.cpp:211-423</c>): the tool tip, the menu built fresh from the sessions, the PM blink, the
/// single and double click, and the menu actions. Every action looks the session up again in the list the menu was built from and
/// does nothing for one that has been signed out (defect D3). UI thread only.
/// </summary>
internal sealed class TrayController : IDisposable
{
    /// <summary>How long a click waits for a second one (<c>TMain.cpp:296</c>).</summary>
    public static readonly TimeSpan DoubleClickDelay = TimeSpan.FromMilliseconds(220);

    /// <summary>How long clicks are ignored after a double click.</summary>
    public static readonly TimeSpan DoubleClickQuiet = TimeSpan.FromMilliseconds(300);

    /// <summary>The blink interval of the PM alert.</summary>
    public static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How often the menu and tool tip are compared with the sessions (player counts, NC state).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly AppShell _shell;
    private readonly ITrayIcon _icon;
    private readonly IUiTimers _timers;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IDisposable _refresh;
    private readonly HashSet<IPrivateMessages> _watched = [];
    private IReadOnlyList<SessionContext> _menuSessions = Array.Empty<SessionContext>();
    private string _signature = string.Empty;
    private string _toolTip = string.Empty;
    private IDisposable? _pendingClick;
    private IDisposable? _blink;
    private DateTimeOffset _quietUntil;
    private bool _pmAlert;
    private bool _alertShown;
    private bool _disposed;

    /// <summary>Creates the controller and shows the current state.</summary>
    /// <param name="shell">The program.</param>
    /// <param name="icon">The icon.</param>
    /// <param name="timers">The timers.</param>
    /// <param name="clock">The clock (for the quiet time after a double click).</param>
    public TrayController(AppShell shell, ITrayIcon icon, IUiTimers timers, Func<DateTimeOffset>? clock = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _icon = icon ?? throw new ArgumentNullException(nameof(icon));
        _timers = timers ?? throw new ArgumentNullException(nameof(timers));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _icon.Clicked += OnClicked;
        _shell.SessionsChanged += OnSessionsChanged;
        _shell.PrivateMessageAttention += OnPrivateMessageAttention;
        _refresh = _timers.Every(RefreshInterval, Refresh);
        Refresh();
    }

    /// <summary>Whether the icon blinks.</summary>
    public bool IsBlinking => _blink is not null;

    /// <summary>The menu shown now (tests).</summary>
    public IReadOnlyList<TrayMenuEntry> Menu { get; private set; } = Array.Empty<TrayMenuEntry>();

    /// <summary>The state the menu was built from.</summary>
    /// <returns>The state.</returns>
    public TrayMenuState CurrentState()
    {
        var sessions = new List<TraySessionState>();
        foreach (SessionContext session in _shell.Sessions)
        {
            bool connected = false, nc = false, unread = false;
            int players = 0;
            UiSafe.Run(() =>
            {
                connected = session.Session.IsConnected;
                nc = session.Session.IsNcAuthenticated;
                players = session.Session.Players.Count;
                unread = session.Services.Get<IPrivateMessages>()?.HasUnread == true;
            }, "tray state");
            sessions.Add(new TraySessionState(session.ServerName, connected, nc, _shell.PrimarySessions.Contains(session), ReferenceEquals(session, _shell.ActiveSession), unread, players));
        }
        return new TrayMenuState(sessions);
    }

    /// <summary>Brings the menu, the tool tip and the blink up to date.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        WatchUnread();
        TrayMenuState state = CurrentState();
        IReadOnlyList<TrayMenuEntry> entries = TrayMenuModel.Build(state);
        string signature = TrayMenuModel.Signature(entries) + string.Join(",", _shell.Sessions.Select(session => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(session)));
        if (!string.Equals(signature, _signature, StringComparison.Ordinal))
        {
            _signature = signature;
            SessionContext[] sessions = _shell.Sessions.ToArray();
            _menuSessions = sessions;
            Menu = entries;
            // Each menu acts on the sessions it was built from, even when it is chosen after a newer menu replaced it.
            UiSafe.Run(() => _icon.SetMenu(entries, entry => Invoke(entry, sessions)), "tray menu");
        }
        string toolTip = TrayMenuModel.ToolTip(state);
        if (!string.Equals(toolTip, _toolTip, StringComparison.Ordinal))
        {
            _toolTip = toolTip;
            UiSafe.Run(() => _icon.SetToolTip(toolTip), "tray tool tip");
        }
        if (_pmAlert && _shell.Desktop.AnyWindowActive) _pmAlert = false;
        UpdateBlink(_pmAlert || state.Sessions.Any(session => session.HasUnreadPm));
    }

    /// <summary>Stops the PM alert of <see cref="IAppShell.RequestAttention"/> (the unread state of the sessions keeps blinking).</summary>
    public void ClearAlert()
    {
        _pmAlert = false;
        Refresh();
    }

    /// <summary>What a click on the icon does: after 220 ms without a second click, <see cref="AppShell.ToggleVisibility"/>; a double click opens the latest PM (or does Open).</summary>
    public void OnClicked()
    {
        if (_disposed) return;
        if (_pendingClick is not null)
        {
            _pendingClick.Dispose();
            _pendingClick = null;
            _quietUntil = _clock() + DoubleClickQuiet;
            OpenLatestPrivateMessageOrOpen();
            return;
        }
        if (_clock() < _quietUntil) return;
        _pendingClick = _timers.After(DoubleClickDelay, () =>
        {
            _pendingClick = null;
            _shell.ToggleVisibility();
        });
    }

    /// <summary>Does what an entry of the menu shown now does.</summary>
    /// <param name="entry">The entry.</param>
    public void Invoke(TrayMenuEntry entry) => Invoke(entry, _menuSessions);

    private void Invoke(TrayMenuEntry entry, IReadOnlyList<SessionContext> menuSessions)
    {
        if (_disposed || entry is null) return;
        SessionContext? session = entry.Session >= 0 && entry.Session < menuSessions.Count ? menuSessions[entry.Session] : null;
        if (session is not null && (session.IsEnded || !_shell.Sessions.Contains(session))) session = null;
        switch (entry.Command)
        {
            case TrayCommand.Open:
                _pmAlert = false;
                _shell.OpenFromTray();
                break;
            case TrayCommand.ServerList:
                _shell.ShowServerList(_shell.ActiveSession);
                break;
            case TrayCommand.SignOut:
                _pmAlert = false;
                if (_shell.ActiveSession is { } active) _shell.SignOut(active);
                break;
            case TrayCommand.LatestPrivateMessage:
                OpenLatestPrivateMessageOrOpen();
                break;
            case TrayCommand.ShowSession when session is not null:
                _pmAlert = false;
                _shell.ActiveSession = session;
                _shell.ShowMainWindow(session);
                break;
            case TrayCommand.SignOutSession when session is not null:
                _shell.SignOut(session);
                break;
            case TrayCommand.TogglePrimary when session is not null:
                _shell.TogglePrimary(session);
                break;
            case TrayCommand.Quit:
                _shell.Quit();
                break;
        }
        Refresh();
    }

    /// <summary>Releases the icon and the timers.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refresh.Dispose();
        _pendingClick?.Dispose();
        _blink?.Dispose();
        _blink = null;
        _icon.Clicked -= OnClicked;
        _shell.SessionsChanged -= OnSessionsChanged;
        _shell.PrivateMessageAttention -= OnPrivateMessageAttention;
        foreach (IPrivateMessages messages in _watched) messages.UnreadChanged -= OnUnreadChanged;
        _watched.Clear();
        UiSafe.Run(() => _icon.SetAlert(false), "tray icon");
        UiSafe.Run(_icon.Dispose, "tray icon");
    }

    private void OpenLatestPrivateMessageOrOpen()
    {
        _pmAlert = false;
        SessionContext? target = _shell.ActiveSession is { } active && active.Services.Get<IPrivateMessages>() is { HasUnread: true }
            ? active
            : _shell.Sessions.LastOrDefault(session => session.Services.Get<IPrivateMessages>()?.HasUnread == true);
        target ??= _shell.ActiveSession;
        bool opened = false;
        if (target?.Services.Get<IPrivateMessages>() is { } messages) UiSafe.Run(() => opened = messages.OpenLatest(), "open latest PM");
        if (!opened) _shell.OpenFromTray();
        Refresh();
    }

    private void OnClicked(object? sender, EventArgs e) => UiSafe.Run(OnClicked, "tray click");

    private void OnSessionsChanged(object? sender, EventArgs e) => Refresh();

    private void OnUnreadChanged(object? sender, EventArgs e) => Refresh();

    private void OnPrivateMessageAttention(object? sender, SessionContext session)
    {
        if (!_icon.IsShown) return;
        _pmAlert = true;
        Refresh();
    }

    private void WatchUnread()
    {
        var current = new HashSet<IPrivateMessages>();
        foreach (SessionContext session in _shell.Sessions)
            if (session.Services.Get<IPrivateMessages>() is { } messages) current.Add(messages);
        foreach (IPrivateMessages gone in _watched.Where(messages => !current.Contains(messages)).ToArray())
        {
            gone.UnreadChanged -= OnUnreadChanged;
            _watched.Remove(gone);
        }
        foreach (IPrivateMessages added in current.Where(messages => !_watched.Contains(messages)))
        {
            added.UnreadChanged += OnUnreadChanged;
            _watched.Add(added);
        }
    }

    private void UpdateBlink(bool blink)
    {
        if (blink && _blink is null)
        {
            _alertShown = true;
            UiSafe.Run(() => _icon.SetAlert(true), "tray icon");
            _blink = _timers.Every(BlinkInterval, () =>
            {
                _alertShown = !_alertShown;
                bool shown = _alertShown;
                UiSafe.Run(() => _icon.SetAlert(shown), "tray icon");
            });
        }
        else if (!blink && _blink is not null)
        {
            _blink.Dispose();
            _blink = null;
            _alertShown = false;
            UiSafe.Run(() => _icon.SetAlert(false), "tray icon");
        }
    }
}
