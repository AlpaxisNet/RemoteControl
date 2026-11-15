using Avalonia.Controls;
using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Shell;

/// <summary>What a session logged in with; kept in memory only, to connect again after a disconnect.</summary>
/// <param name="Account">The account.</param>
/// <param name="Password">The password.</param>
/// <param name="Nickname">The nickname the RC shows.</param>
/// <param name="Server">The game server (from the list server, or <see cref="ServerInfo.Direct"/> for direct mode).</param>
/// <param name="ListServer">The list-server profile's name the server came from, or empty for direct mode.</param>
public sealed record SessionLogin(string Account, string Password, string Nickname, ServerInfo Server, string ListServer)
{
    /// <summary>Never shows the password.</summary>
    /// <returns>Account and server.</returns>
    public override string ToString() => Account + "@" + Server.Name;
}

/// <summary>
/// One server session and everything its windows share: the connection, who is logged in, the session's windows, and the
/// services the groups of windows offer each other. The program creates one per joined server and ends it on sign-out.
/// </summary>
/// <remarks>
/// <para>
/// A connection object is used once: after a disconnect the program connects a new one and calls <see cref="Replace"/>. Windows
/// therefore never keep the <see cref="IRcSession"/> they were opened with: they read <see cref="Session"/> when they need it, and
/// they subscribe to its events through <see cref="Attach"/>, which subscribes again after a replacement and unsubscribes when the
/// window closes.
/// </para>
/// <para>Everything here is used on the UI thread only.</para>
/// </remarks>
public sealed class SessionContext
{
    private readonly List<Attachment> _attachments = [];
    private bool _ended;

    /// <summary>Creates the context of a session that is connected (or being connected).</summary>
    /// <param name="app">The program's services.</param>
    /// <param name="shell">The program, as the windows see it.</param>
    /// <param name="tools">What can be opened.</param>
    /// <param name="session">The connection.</param>
    /// <param name="login">What it logged in with.</param>
    public SessionContext(AppServices app, IAppShell shell, ToolRegistry tools, IRcSession session, SessionLogin login)
    {
        App = app ?? throw new ArgumentNullException(nameof(app));
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Tools = tools ?? throw new ArgumentNullException(nameof(tools));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Login = login ?? throw new ArgumentNullException(nameof(login));
    }

    /// <summary>The program's services: options, theme, pictures, sounds, paths.</summary>
    public AppServices App { get; }

    /// <summary>The program: its sessions, the login and server-list windows, sign-out.</summary>
    public IAppShell Shell { get; }

    /// <summary>What can be opened for a session.</summary>
    public ToolRegistry Tools { get; }

    /// <summary>The connection in use now. Do not keep it: read it when needed, subscribe through <see cref="Attach"/>.</summary>
    public IRcSession Session { get; private set; }

    /// <summary>What the session logged in with.</summary>
    public SessionLogin Login { get; private set; }

    /// <summary>
    /// The server's name as the program shows it and uses it for folders: without the list server's type prefix (<c>"P "</c>,
    /// <c>"H "</c>, <c>"U "</c>, <c>"3 "</c>), as the C++ did. <see cref="SessionLogin.Server"/> has the name as it was sent.
    /// </summary>
    public string ServerName => Login.Server.DisplayName;

    /// <summary>The account.</summary>
    public string Account => Login.Account;

    /// <summary>The nickname.</summary>
    public string Nickname => Login.Nickname;

    /// <summary>The session's windows; they close when the session ends.</summary>
    public SessionWindows Windows { get; } = new();

    /// <summary>What the groups of windows offer each other for this session.</summary>
    public SessionServices Services { get; } = new();

    /// <summary>Whether the session has ended (sign-out or program exit): nothing may be opened for it any more.</summary>
    public bool IsEnded => _ended;

    /// <summary>Raised after <see cref="Replace"/>: there is a new connection (reconnect, or another server in the same window); caches start empty.</summary>
    public event EventHandler? SessionReplaced;

    /// <summary>Raised once, by <see cref="End"/>, after the session's windows were closed.</summary>
    public event EventHandler? Ended;

    /// <summary>Opens a tool for this session (<see cref="ToolIds"/>).</summary>
    /// <param name="toolId">The name.</param>
    /// <param name="argument">What the tool needs.</param>
    /// <returns>The window shown, or null.</returns>
    public Window? OpenTool(string toolId, object? argument = null) => Tools.Open(this, toolId, argument);

    /// <summary>
    /// Subscribes to the connection's events for as long as the returned object lives. The function is called with the current
    /// connection now and with the new one after every <see cref="Replace"/>; what it returns is disposed before the next call and
    /// when the returned object is disposed. In a window: <c>Track(session.Attach(s =&gt; { s.RcChatReceived += OnChat; return
    /// Disposable(() =&gt; s.RcChatReceived -= OnChat); }))</c>.
    /// </summary>
    /// <param name="subscribe">Subscribes to a connection and returns what unsubscribes (or null).</param>
    /// <returns>Dispose it to unsubscribe for good.</returns>
    public IDisposable Attach(Func<IRcSession, IDisposable?> subscribe)
    {
        ArgumentNullException.ThrowIfNull(subscribe);
        var attachment = new Attachment(this, subscribe);
        if (_ended) return attachment;
        _attachments.Add(attachment);
        attachment.Bind(Session);
        return attachment;
    }

    /// <summary>
    /// Puts a new connection in the place of the old one (which the caller disposes): every <see cref="Attach"/> subscription
    /// moves to it, then <see cref="SessionReplaced"/> is raised.
    /// </summary>
    /// <param name="session">The new connection.</param>
    /// <param name="login">What it logged in with (the server may be another one).</param>
    public void Replace(IRcSession session, SessionLogin login)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(login);
        if (_ended) return;
        Session = session;
        Login = login;
        foreach (Attachment attachment in _attachments.ToArray()) attachment.Bind(session);
        UiSafe.Run(() => SessionReplaced?.Invoke(this, EventArgs.Empty), "session replaced");
    }

    /// <summary>Changes the nickname the session goes by (the options window, the AFK suffix).</summary>
    /// <param name="nickname">The new nickname.</param>
    public void SetNickname(string nickname) => Login = Login with { Nickname = nickname ?? string.Empty };

    /// <summary>
    /// Ends the session: unsubscribes everything, closes its windows, disposes the connection and raises <see cref="Ended"/>.
    /// Calling it again does nothing.
    /// </summary>
    public void End()
    {
        if (_ended) return;
        _ended = true;
        foreach (Attachment attachment in _attachments.ToArray()) attachment.Release();
        _attachments.Clear();
        UiSafe.Run(Windows.CloseAll, "close session windows");
        UiSafe.Run(Session.Dispose, "dispose session");
        UiSafe.Run(() => Ended?.Invoke(this, EventArgs.Empty), "session ended");
    }

    private sealed class Attachment(SessionContext owner, Func<IRcSession, IDisposable?> subscribe) : IDisposable
    {
        private IDisposable? _current;
        private bool _released;

        public void Bind(IRcSession session)
        {
            if (_released) return;
            DisposeCurrent();
            UiSafe.Run(() => _current = subscribe(session), "subscribe to session");
        }

        public void Release()
        {
            if (_released) return;
            _released = true;
            DisposeCurrent();
        }

        public void Dispose()
        {
            Release();
            owner._attachments.Remove(this);
        }

        private void DisposeCurrent()
        {
            IDisposable? current = _current;
            _current = null;
            if (current is not null) UiSafe.Run(current.Dispose, "unsubscribe from session");
        }
    }
}

/// <summary>Makes an <see cref="IDisposable"/> from an action, for <see cref="SessionContext.Attach"/>.</summary>
public sealed class Unsubscriber : IDisposable
{
    private Action? _action;

    /// <summary>Creates it.</summary>
    /// <param name="action">What disposing does; it runs once.</param>
    public Unsubscriber(Action action) => _action = action ?? throw new ArgumentNullException(nameof(action));

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}
