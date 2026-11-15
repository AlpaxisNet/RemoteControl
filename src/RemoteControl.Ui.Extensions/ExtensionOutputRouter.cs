using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Extensions;

/// <summary>
/// Puts the output of extensions into tabs of the sessions' main windows (<c>appendChannelMessage</c>/<c>removeChannel</c> of the C++,
/// review mainui 3.5). A tab goes to the main window of the session the extension concerns (the last session whose script editor
/// invoked one of its actions), else to the main window it is already in, else to the active session. Each open tab keeps its lines
/// (bounded) so that a main window that appears later, or replaces a closed one, gets the output so far. Used on the UI thread only.
/// </summary>
internal sealed class ExtensionOutputRouter : IDisposable
{
    /// <summary>The most lines kept per tab for a main window that appears later.</summary>
    internal const int MaximumLines = 2000;

    /// <summary>The most characters kept per tab (the extension log's limit).</summary>
    internal const int MaximumCharacters = 65536;

    private readonly IAppShell _shell;
    private readonly Dictionary<string, Tab> _tabs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionContext> _concerned = new(StringComparer.Ordinal);
    private readonly Dictionary<SessionContext, Action> _watched = [];
    private readonly EventHandler _sessionsChanged;
    private bool _disposed;

    /// <summary>Creates the router and follows the program's sessions.</summary>
    /// <param name="shell">The program.</param>
    public ExtensionOutputRouter(IAppShell shell)
    {
        _shell = shell;
        _sessionsChanged = UiSafe.Handler((_, _) => OnSessionsChanged(), "extension output: sessions changed");
        _shell.SessionsChanged += _sessionsChanged;
        OnSessionsChanged();
    }

    /// <summary>The ids of the extensions that have a tab open.</summary>
    internal IReadOnlyCollection<string> OpenTabs => _tabs.Keys;

    /// <summary>Opens (or keeps) the tab of an extension with the lines so far.</summary>
    /// <param name="id">The extension id.</param>
    /// <param name="name">The extension name, the tab's title.</param>
    /// <param name="backlog">The lines so far.</param>
    public void Open(string id, string name, IReadOnlyList<string> backlog)
    {
        if (_disposed) return;
        if (!_tabs.TryGetValue(id, out Tab? tab))
        {
            tab = new Tab(id, name);
            _tabs[id] = tab;
            foreach (string line in backlog) tab.Add(line);
        }
        Ensure(tab, null);
    }

    /// <summary>Shows a line in the tab of an extension (the tab is made when it is not open).</summary>
    /// <param name="id">The extension id.</param>
    /// <param name="name">The extension name.</param>
    /// <param name="line">The line.</param>
    public void Line(string id, string name, string line)
    {
        if (_disposed) return;
        if (!_tabs.TryGetValue(id, out Tab? tab))
        {
            tab = new Tab(id, name);
            _tabs[id] = tab;
        }
        tab.Add(line);
        Ensure(tab, line);
    }

    /// <summary>Closes the tab of an extension.</summary>
    /// <param name="id">The extension id.</param>
    public void Close(string id)
    {
        if (!_tabs.Remove(id, out Tab? tab)) return;
        RemoveFromWindow(tab);
    }

    /// <summary>Closes every tab whose extension is not in <paramref name="keep"/>.</summary>
    /// <param name="keep">The ids of the extensions whose output still goes to a tab.</param>
    public void CloseAllBut(IReadOnlySet<string> keep)
    {
        foreach (string id in _tabs.Keys.Where(id => !keep.Contains(id)).ToArray()) Close(id);
    }

    /// <summary>Closes every tab (Extensions switched off).</summary>
    public void CloseAll()
    {
        foreach (string id in _tabs.Keys.ToArray()) Close(id);
        _concerned.Clear();
    }

    /// <summary>Notes the session an extension's output concerns from now on (its action was invoked there).</summary>
    /// <param name="id">The extension id.</param>
    /// <param name="session">The session.</param>
    public void Concern(string id, SessionContext session)
    {
        if (_disposed) return;
        _concerned[id] = session;
        if (_tabs.TryGetValue(id, out Tab? tab)) Ensure(tab, null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        CloseAll();
        _disposed = true;
        _shell.SessionsChanged -= _sessionsChanged;
        foreach (Action unwatch in _watched.Values) unwatch();
        _watched.Clear();
    }

    private void OnSessionsChanged()
    {
        if (_disposed) return;
        var sessions = new HashSet<SessionContext>(_shell.Sessions);
        foreach (SessionContext gone in _watched.Keys.Where(session => !sessions.Contains(session) || session.IsEnded).ToArray())
        {
            _watched[gone]();
            _watched.Remove(gone);
        }
        foreach (SessionContext session in sessions)
        {
            if (session.IsEnded || _watched.ContainsKey(session)) continue;
            EventHandler<Type> services = UiSafe.Handler<Type>((_, type) =>
            {
                if (type == typeof(ISessionChat)) EnsureAll();
            }, "extension output: main window changed");
            EventHandler ended = UiSafe.Handler((_, _) => OnSessionsChanged(), "extension output: session ended");
            session.Services.Changed += services;
            session.Ended += ended;
            _watched[session] = () =>
            {
                session.Services.Changed -= services;
                session.Ended -= ended;
            };
        }
        EnsureAll();
    }

    private void EnsureAll()
    {
        if (_disposed) return;
        foreach (Tab tab in _tabs.Values.ToArray()) Ensure(tab, null);
    }

    /// <summary>Makes sure the tab is in the window it belongs in (sending the lines so far when it moves) and shows the new line.</summary>
    private void Ensure(Tab tab, string? newLine)
    {
        (SessionContext? session, ISessionChat? chat) = Resolve(tab);
        if (chat is null)
        {
            if (tab.Chat is not null && !Provides(tab.Session, tab.Chat)) tab.Chat = null;
            return;
        }
        if (ReferenceEquals(chat, tab.Chat))
        {
            if (newLine is not null) UiSafe.Run(() => chat.AppendToTab(tab.TabName, newLine), "extension output line");
            return;
        }
        RemoveFromWindow(tab);
        tab.Chat = chat;
        tab.Session = session;
        tab.TabName = TabNameFor(chat, tab.Name);
        UiSafe.Run(() =>
        {
            if (tab.Lines.Count == 0) chat.AppendToTab(tab.TabName, string.Empty);
            foreach (string line in tab.Lines) chat.AppendToTab(tab.TabName, line);
        }, "extension output tab");
    }

    private (SessionContext? Session, ISessionChat? Chat) Resolve(Tab tab)
    {
        if (_concerned.TryGetValue(tab.Id, out SessionContext? concerned))
        {
            if (!IsAlive(concerned)) _concerned.Remove(tab.Id);
            else if (ChatOf(concerned) is { } chat) return (concerned, chat);
        }
        if (tab.Session is { } current && tab.Chat is not null && IsAlive(current) && Provides(current, tab.Chat)) return (current, tab.Chat);
        SessionContext? active = _shell.ActiveSession;
        if (IsAlive(active) && ChatOf(active!) is { } activeChat) return (active, activeChat);
        return (null, null);
    }

    private void RemoveFromWindow(Tab tab)
    {
        ISessionChat? chat = tab.Chat;
        SessionContext? session = tab.Session;
        tab.Chat = null;
        tab.Session = null;
        if (chat is not null && Provides(session, chat)) UiSafe.Run(() => chat.RemoveTab(tab.TabName), "extension output tab closed");
    }

    private bool IsAlive(SessionContext? session) => session is { IsEnded: false } && _shell.Sessions.Contains(session);

    private static ISessionChat? ChatOf(SessionContext session) => session.Services.Get<ISessionChat>();

    private static bool Provides(SessionContext? session, ISessionChat chat) => session is { IsEnded: false } && ReferenceEquals(session.Services.Get<ISessionChat>(), chat);

    /// <summary>The tab's title: the extension name, unless that is the RC chat's own tab.</summary>
    private static string TabNameFor(ISessionChat chat, string name)
    {
        string main = UiSafe.Run(() => chat.Tabs.Count > 0 ? chat.Tabs[0] : string.Empty, "extension output tab name", string.Empty);
        if (name.Length == 0) name = "Extension";
        return string.Equals(main, name, StringComparison.Ordinal) ? name + " (extension)" : name;
    }

    private sealed class Tab(string id, string name)
    {
        private int _characters;

        public string Id { get; } = id;

        public string Name { get; } = name;

        public string TabName { get; set; } = name;

        public Queue<string> Lines { get; } = new();

        public ISessionChat? Chat { get; set; }

        public SessionContext? Session { get; set; }

        public void Add(string line)
        {
            Lines.Enqueue(line);
            _characters += line.Length;
            while (Lines.Count > 1 && (Lines.Count > MaximumLines || _characters > MaximumCharacters)) _characters -= Lines.Dequeue().Length;
        }
    }
}
