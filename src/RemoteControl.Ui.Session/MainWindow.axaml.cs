using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Session;

/// <summary>What the debug information window reads from a session's main window.</summary>
internal interface IMainWindowInfo
{
    /// <summary>The number of chat tabs.</summary>
    int TabCount { get; }

    /// <summary>The joined IRC channels.</summary>
    int JoinedChannelCount { get; }

    /// <summary>The session's controller (away, NC, uptime).</summary>
    SessionController Controller { get; }

    /// <summary>The main window.</summary>
    Window MainWindow { get; }
}

/// <summary>
/// The main RC window of a server session (<c>TRemoteFrame</c>, review section 3): the classic menu bar or the graphical header (picture,
/// 15 picture buttons, three outlined labels), the chat tabs, and the chat entry with its keys, history, the program's own commands and
/// completion. The connection work it did in the C++ is in <see cref="SessionController"/>. Closing it hides it when there is a tray
/// icon, otherwise it asks before signing out. It offers <see cref="ISessionChat"/> to the other groups.
/// </summary>
internal sealed partial class MainWindow : RcWindow, ISessionChat, IMainWindowInfo
{
    private readonly SessionContext _context;
    private readonly SessionController _controller;
    private ISyncStatus? _sync;
    private IPrivateMessages? _privateMessages;

    /// <summary>For the XAML previewer only.</summary>
    public MainWindow() : this(null!, null, false)
    {
    }

    /// <summary>Creates the main window of a session.</summary>
    /// <param name="context">The session.</param>
    /// <param name="clock">The time for the controller (tests).</param>
    /// <param name="startTimer">Whether the controller's timer runs (tests call <see cref="SessionController.Tick"/> themselves).</param>
    public MainWindow(SessionContext context, Func<DateTimeOffset>? clock = null, bool startTimer = true) : base(context?.App)
    {
        InitializeComponent();
        _context = context!;
        if (context is null)
        {
            _controller = null!;
            return;
        }
        _controller = Track(new SessionController(context, clock));
        _controller.ChatLine += (_, line) => AppendMain(line);
        _controller.StateChanged += (_, _) => RefreshState();
        _controller.AskReconnect = AskReconnectAsync;

        InitializeChat();
        InitializeEntry();
        InitializeMenu();
        InitializeHeader();
        ApplyLayout();

        Track(context.Attach(Subscribe));
        Track(context.Services.Provide<ISessionChat>(this));
        Track(context.Services.Provide<IMainWindowInfo>(this));
        Listen<OptionsChangedEventArgs>(h => context.App.Options.Changed += h, h => context.App.Options.Changed -= h, (_, e) => OnOptionsChanged(e), "MainWindow options");
        Listen<Type>(h => context.Services.Changed += h, h => context.Services.Changed -= h, (_, type) => OnServicesChanged(type), "MainWindow services");
        Listen(h => context.SessionReplaced += h, h => context.SessionReplaced -= h, (_, _) => OnSessionReplaced(), "MainWindow replaced");
        HookSync();
        HookPrivateMessages();
        ActivityHook.Attach(this, context);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Activated += UiSafe.Handler((_, _) => _privateMessages?.ClearUnread(), "MainWindow.Activated");
        Opened += UiSafe.Handler((_, _) => ChatEntry.Focus(), "MainWindow.Opened");
        Every(TimeSpan.FromSeconds(1), RefreshTitle, "MainWindow.Title");
        _controller.Start(startTimer);
        RefreshState();
        RefreshTitle();
    }

    /// <summary>The session's controller (tests and the debug information).</summary>
    internal SessionController Controller => _controller;

    SessionController IMainWindowInfo.Controller => _controller;

    Window IMainWindowInfo.MainWindow => this;

    /// <inheritdoc />
    public int TabCount => ChatTabs.Items.Count;

    /// <inheritdoc />
    public int JoinedChannelCount => _controller.JoinedIrcChannels.Count;

    /// <summary>Whether closing was asked by the user and is being answered (tests).</summary>
    internal Task? PendingSignOutQuestion { get; private set; }

    // ---- Session events ----------------------------------------------------------------------------------------------------

    private IDisposable Subscribe(IRcSession session)
    {
        bool Current() => !IsClosed && ReferenceEquals(session, _context.Session);
        EventHandler<TextEventArgs> rcChat = (_, e) => { if (Current()) UiSafe.Run(() => OnServerChat(e.Text), "MainWindow.RcChat"); };
        EventHandler<TextEventArgs> serverText = (_, e) => { if (Current()) UiSafe.Run(() => OnServerChat(e.Text), "MainWindow.ServerText"); };
        EventHandler<TextEventArgs> ncChat = (_, e) => { if (Current()) UiSafe.Run(() => OnNcChat(e.Text), "MainWindow.NcChat"); };
        EventHandler<IrcMessageEventArgs> irc = (_, e) => { if (Current()) UiSafe.Run(() => OnIrcMessage(e.Channel, e.Line), "MainWindow.Irc"); };
        EventHandler<PrivateMessageEventArgs> pm = (_, e) => { if (Current()) UiSafe.Run(() => OnPrivateMessage(e), "MainWindow.Pm"); };
        EventHandler<LocalNpcsEventArgs> localNpcs = (_, e) => { if (Current()) UiSafe.Run(() => OnLocalNpcs(e), "MainWindow.LocalNpcs"); };
        EventHandler<PlayerEventArgs> players = (_, _) => { if (Current()) UiSafe.Run(RefreshTitle, "MainWindow.Players"); };
        EventHandler<TextEventArgs> adminMessage = (_, e) => { if (Current()) UiSafe.Run(() => OnAdminMessage(e.Text), "MainWindow.AdminMessage"); };
        session.RcChatReceived += rcChat;
        session.ServerTextReceived += serverText;
        session.NcChatReceived += ncChat;
        session.IrcMessageReceived += irc;
        session.PrivateMessageReceived += pm;
        session.LocalNpcsReceived += localNpcs;
        session.PlayerJoined += players;
        session.PlayerLeft += players;
        session.AdminMessageReceived += adminMessage;
        return new Unsubscriber(() =>
        {
            session.RcChatReceived -= rcChat;
            session.ServerTextReceived -= serverText;
            session.NcChatReceived -= ncChat;
            session.IrcMessageReceived -= irc;
            session.PrivateMessageReceived -= pm;
            session.LocalNpcsReceived -= localNpcs;
            session.PlayerJoined -= players;
            session.PlayerLeft -= players;
            session.AdminMessageReceived -= adminMessage;
        });
    }

    private void OnSessionReplaced()
    {
        UpdateServerLabel();
        RefreshState();
        RefreshTitle();
    }

    private void OnPrivateMessage(PrivateMessageEventArgs e)
    {
        RcOptions options = _context.App.Options.Current;
        if (e.Kind == PrivateMessageKind.Mass && options.NoMassMessages) return;
        if (_context.Windows.Find(ToolIds.PrivateMessage + ":" + e.PlayerId) is { IsVisible: true }) return;
        string display = string.IsNullOrEmpty(e.Nickname) || string.Equals(e.Nickname, e.Account, StringComparison.Ordinal) ? e.Account : e.Nickname + " (" + e.Account + ")";
        if (options.NewPmAlerts) AppendMain("#ALERT New PM from " + display, new ChatAppendOptions { SuppressUrgency = true }, attention: false);
        // The players group asks for attention (sound, tray) when it is part of the program; without it this window does.
        if (_context.Services.Get<IPrivateMessages>() is null) _context.Shell.RequestAttention(_context, AttentionKind.PrivateMessage);
    }

    /// <summary>
    /// An admin message (PLO_RC_ADMINMESSAGE): an alert line of the RC chat, which asks for attention. The server sends a title line and
    /// the text ("Admin &lt;account&gt;:", or a notice of its own such as "Server message:"), which the session splits at the server's §; the
    /// chat shows them on one line, and only a text without a title of its own is marked "Admin message:".
    /// </summary>
    private void OnAdminMessage(string? text)
    {
        string[] lines = (text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return;
        bool titled = lines.Length > 1 && lines[0].EndsWith(':');
        AppendMain("#ALERT " + (titled ? string.Empty : "Admin message: ") + string.Join(' ', lines));
    }

    private void OnLocalNpcs(LocalNpcsEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        var window = new LocalNpcsResultWindow(_context, e.Level, e.Text);
        _context.Windows.Track(window);
        window.Show();
    }

    // ---- Options and services ----------------------------------------------------------------------------------------------

    private void OnOptionsChanged(OptionsChangedEventArgs e)
    {
        if (e.Contains(OptionKeys.GraphicalMenu)) ApplyLayout();
        if (e.Keys.Any(key => key.StartsWith("icon", StringComparison.Ordinal))) UpdateButtonImages();
        if (e.Contains(OptionKeys.Background, OptionKeys.BackgroundTint, OptionKeys.BackgroundTintSolid)) HeaderBackground.ApplyBackgroundOptions(_context.App.Paths, e.Current);
        if (e.Contains(OptionKeys.LabelServers)) UpdateServerLabel();
        if (e.Contains(OptionKeys.LabelNpcServer)) NpcServerLabel.Text = e.Current.LabelNpcServer;
        if (e.Contains(OptionKeys.LabelPlayers)) RefreshTitle();
        RefreshState();
    }

    private void OnServicesChanged(Type type)
    {
        if (type == typeof(ISyncStatus)) HookSync();
        else if (type == typeof(IPrivateMessages)) HookPrivateMessages();
    }

    private void HookSync()
    {
        ISyncStatus? next = _context.Services.Get<ISyncStatus>();
        if (ReferenceEquals(next, _sync)) return;
        if (_sync is not null) _sync.Changed -= OnSyncChanged;
        _sync = next;
        if (_sync is not null) _sync.Changed += OnSyncChanged;
        RefreshState();
        RefreshTitle();
    }

    private void OnSyncChanged(object? sender, EventArgs e)
    {
        if (IsClosed) return;
        UiSafe.Run(() =>
        {
            RefreshState();
            RefreshTitle();
        }, "MainWindow.Sync");
    }

    private void HookPrivateMessages() => _privateMessages = _context.Services.Get<IPrivateMessages>();

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        if (_sync is not null) _sync.Changed -= OnSyncChanged;
        _sync = null;
        base.OnClosed(e);
    }

    // ---- Title and state ---------------------------------------------------------------------------------------------------

    private bool IsFileSyncActive => _sync?.IsFileSyncActive == true;

    private void RefreshTitle()
    {
        if (_context is null || IsClosed) return;
        RcOptions options = _context.App.Options.Current;
        int count = _context.Session.Players.Count;
        string label = options.LabelPlayers.Length == 0 ? "Players:" : options.LabelPlayers;
        Title = BuildInfo.Title(_context.ServerName, label + " " + count, _sync?.Percent);
        PlayersLabel.Text = options.LabelPlayers.Length == 0 ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : options.LabelPlayers + " " + count;
    }

    private void UpdateServerLabel()
    {
        string label = _context.App.Options.Current.LabelServers;
        ServerLabel.Text = label.Length == 0 ? _context.ServerName : label + " " + _context.ServerName;
    }

    /// <summary>Shows what depends on the NC, the options and the sync state (<c>updateNCUi</c>, <c>applyOptionalTools</c>).</summary>
    private void RefreshState()
    {
        if (_context is null || IsClosed) return;
        RcOptions options = _context.App.Options.Current;
        bool nc = _controller.IsNcActive;
        bool connected = _context.Session.IsConnected;
        foreach (int index in new[] { 8, 9, 10, 11 }) _buttons[index].IsVisible = nc;
        _buttons[12].IsVisible = options.ExtensionsEnabled;
        _buttons[13].IsVisible = options.LevelListEnabled && nc;
        _buttons[14].IsVisible = options.SyncEnabled && connected;
        _buttons[1].IsEnabled = !IsFileSyncActive;
        MenuFileBrowser.IsEnabled = !IsFileSyncActive;
        foreach (MenuItem item in new[] { MenuNpcs, MenuClasses, MenuWeapons, MenuLocalNpcs }) item.IsEnabled = nc;
        MenuExtensions.IsEnabled = options.ExtensionsEnabled;
        NpcServerLabel.IsVisible = nc;
        SetNcTabVisible(nc && options.SeparateNc);
        LayoutHeader();
    }

    // ---- Opening tools -----------------------------------------------------------------------------------------------------

    /// <summary>Opens another window of the session; quietly nothing when what it needs is not there (C++: "return silently").</summary>
    internal void OpenTool(string toolId)
    {
        if (_context.IsEnded) return;
        IRcSession session = _context.Session;
        RcOptions options = _context.App.Options.Current;
        bool allowed = toolId switch
        {
            ToolIds.RcOptions or ToolIds.DebugInfo => true,
            ToolIds.Extensions => options.ExtensionsEnabled,
            ToolIds.FileBrowser => session.IsConnected && !IsFileSyncActive,
            ToolIds.Npcs or ToolIds.Classes or ToolIds.Weapons or ToolIds.LocalNpcs => session.IsConnected && _controller.IsNcActive,
            ToolIds.Levels => options.LevelListEnabled && session.IsConnected && _controller.IsNcActive,
            ToolIds.Sync => options.SyncEnabled && session.IsConnected,
            _ => session.IsConnected,
        };
        if (allowed) _context.OpenTool(toolId);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        UiSafe.Run(() =>
        {
            if (e.Key == Key.F8)
            {
                _context.Shell.ShowServerList(_context);
                e.Handled = true;
            }
            else if (e.Key == Key.F1)
            {
                OpenTool(ToolIds.DebugInfo);
                e.Handled = true;
            }
            else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                OpenFind();
                e.Handled = true;
            }
        }, "MainWindow.Key");
    }

    // ---- Closing -----------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || e.IsProgrammatic || _context is null || _context.IsEnded) return;
        e.Cancel = true;
        RequestClose();
    }

    /// <summary>What the window's close button does: hide with a tray icon, otherwise ask before signing out.</summary>
    internal void RequestClose()
    {
        if (_context.IsEnded || IsClosed) return;
        if (_context.Shell.TrayAvailable)
        {
            HideAndRemember();
            return;
        }
        if (PendingSignOutQuestion is { IsCompleted: false }) return;
        PendingSignOutQuestion = UiSafe.RunAsync(async () =>
        {
            bool yes = await Dialogs.ConfirmAsync("Sign out", "Sign out of " + _context.ServerName + "?", this, "Sign out", "Cancel").ConfigureAwait(true);
            if (yes && !_context.IsEnded) _context.Shell.SignOut(_context);
        }, "MainWindow.SignOut");
    }

    private void SignOut()
    {
        if (!_context.IsEnded) _context.Shell.SignOut(_context);
    }

    private async Task<bool?> AskReconnectAsync(string reason)
    {
        if (IsClosed) return null;
        AppendMain("Disconnected: " + reason, new ChatAppendOptions { SuppressEmotes = true }, attention: false);
        Window? owner = IsVisible ? this : null;
        bool reconnect = await Dialogs.ConfirmAsync("Connection Error", reason, owner, "Reconnect", "Close").ConfigureAwait(true);
        // A dialog closed because its owner closes ends before the owner does: look again once the closing went through.
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        if (IsClosed || _context.IsEnded) return null;
        return reconnect;
    }

    // ---- ISessionChat ------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyList<string> Tabs => _tabs.Select(tab => tab.Name).ToArray();

    /// <inheritdoc />
    public void AppendLine(string text)
    {
        if (IsClosed || text is null) return;
        OnServerChat(text);
    }

    /// <inheritdoc />
    public void AppendToTab(string tab, string text)
    {
        if (IsClosed || string.IsNullOrEmpty(tab)) return;
        if (tab == MainTabName) AppendMain(text ?? string.Empty);
        else AppendToChannel(tab, text ?? string.Empty, ChatTabKind.Extension);
    }

    /// <inheritdoc />
    public void RemoveTab(string tab)
    {
        if (IsClosed || string.IsNullOrEmpty(tab) || tab == MainTabName) return;
        CloseTab(tab, part: true);
    }

    /// <inheritdoc />
    public bool Send(string text)
    {
        if (IsClosed || string.IsNullOrWhiteSpace(text)) return false;
        return Execute(text.Trim());
    }

    /// <inheritdoc />
    void ISessionChat.Activate() => ShowAndActivate();

    /// <inheritdoc />
    Window? ISessionChat.Window => IsClosed ? null : this;
}
