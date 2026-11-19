using System.Runtime.CompilerServices;
using Avalonia.Controls;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Players;

/// <summary>A private message that arrived while no window of its player was open.</summary>
internal sealed class PendingPm
{
    /// <summary>The most messages kept per player (<c>maxPendingPrivateMessages</c>).</summary>
    public const int MaximumMessages = 500;

    /// <summary>Creates it.</summary>
    public PendingPm(string account, string nick, PrivateMessageKind kind)
    {
        Account = account;
        Nick = nick;
        Kind = kind;
    }

    /// <summary>The sender's account.</summary>
    public string Account { get; set; }

    /// <summary>The sender's nickname.</summary>
    public string Nick { get; set; }

    /// <summary>The kind of the latest message (its icon blinks in the player list).</summary>
    public PrivateMessageKind Kind { get; set; }

    /// <summary>The messages not looked at yet, oldest first.</summary>
    public List<string> Messages { get; } = new();
}

/// <summary>A player of another server, from a PM server's player list (<c>id\taccount\tnick\tlevel</c>).</summary>
/// <param name="Id">The player id.</param>
/// <param name="Account">The account.</param>
/// <param name="Nick">The nickname (the account when none was sent).</param>
/// <param name="Level">The level.</param>
internal sealed record RemotePlayer(int Id, string Account, string Nick, string Level);

/// <summary>What a player action works on, captured when its menu opened (defect D8: never read from the selection later).</summary>
/// <param name="Id">The player id, or -1 for an account that is not online (accounts list).</param>
/// <param name="Account">The account.</param>
/// <param name="Nick">The nickname.</param>
/// <param name="Level">The level, or empty.</param>
internal sealed record PlayerTarget(int Id, string Account, string Nick, string Level);

/// <summary>
/// The players group's part of one session, made the first time the session uses the group (<see cref="PlayersFeature.Start"/>
/// or any of its tools) and dropped when the session ends. It listens to the session for as long as the session lives, whether or not a
/// window is open: incoming private messages (unread state, attention, history), the replies that open the edit dialogs (rights,
/// attributes, comments, profile, account, ban data, ban lists, account list), the status list and the PM servers' players. It
/// offers <see cref="IPrivateMessages"/> for the session. Used on the UI thread only.
/// </summary>
internal sealed partial class PlayersSession : IPrivateMessages
{
    private static readonly ConditionalWeakTable<SessionContext, PlayersSession> s_sessions = new();
    private readonly Dictionary<int, PendingPm> _pending = new();
    private readonly List<int> _pendingOrder = new();
    private readonly Dictionary<int, (string Account, string Nick)> _identities = new();
    private readonly Dictionary<string, IReadOnlyList<RemotePlayer>> _serverPlayers = new(StringComparer.Ordinal);
    private readonly IDisposable _attachment;
    private readonly IDisposable _provided;
    private IReadOnlyList<string> _statusNames = Array.Empty<string>();
    private IReadOnlyList<BanType> _banTypes = Array.Empty<BanType>();
    private bool _alert;
    private bool _ended;

    private PlayersSession(SessionContext context)
    {
        Context = context;
        History = new PmHistory(context.App.Paths, context.App.Log);
        _provided = context.Services.Provide<IPrivateMessages>(this);
        _attachment = context.Attach(Subscribe);
        context.SessionReplaced += OnSessionReplaced;
        context.Ended += OnEnded;
    }

    /// <summary>The session.</summary>
    public SessionContext Context { get; }

    /// <summary>The application services.</summary>
    public AppServices App => Context.App;

    /// <summary>The options now.</summary>
    public RcOptions Options => Context.App.Options.Current;

    /// <summary>The PM history files.</summary>
    public PmHistory History { get; }

    /// <summary>The players with messages not looked at, by id.</summary>
    public IReadOnlyDictionary<int, PendingPm> Pending => _pending;

    /// <summary>The player whose message arrived last and was not looked at, or 0.</summary>
    public int LatestPmPlayerId { get; private set; }

    /// <summary>The status names of the server's <c>statuslist</c>, by status index.</summary>
    public IReadOnlyList<string> StatusNames => _statusNames;

    /// <summary>The ban types of the server's <c>bantypes</c> list.</summary>
    public IReadOnlyList<BanType> BanTypes => _banTypes;

    /// <summary>The players of the PM servers whose list arrived, by server.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RemotePlayer>> ServerPlayers => _serverPlayers;

    /// <inheritdoc />
    public bool HasUnread => _alert;

    /// <inheritdoc />
    public event EventHandler? UnreadChanged;

    /// <summary>Raised when <see cref="Pending"/> changed.</summary>
    public event EventHandler? PendingChanged;

    /// <summary>Raised when <see cref="StatusNames"/> changed.</summary>
    public event EventHandler? StatusNamesChanged;

    /// <summary>Raised when <see cref="BanTypes"/> changed.</summary>
    public event EventHandler? BanTypesChanged;

    /// <summary>Raised with the server whose players arrived (or null when all were dropped).</summary>
    public event EventHandler<string?>? ServerPlayersChanged;

    /// <summary>The players group's part of a session, made on first use; null for a session that has ended.</summary>
    /// <param name="context">The session.</param>
    /// <returns>The object, or null.</returns>
    public static PlayersSession? For(SessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsEnded) return null;
        if (s_sessions.TryGetValue(context, out PlayersSession? existing) && !existing._ended) return existing;
        var created = new PlayersSession(context);
        s_sessions.AddOrUpdate(context, created);
        return created;
    }

    // ---- IPrivateMessages ------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public bool OpenLatest()
    {
        int id = LatestPmPlayerId;
        if (id == 0 || !_pending.ContainsKey(id)) return false;
        return OpenPrivateMessage(id) is not null;
    }

    /// <inheritdoc />
    public void ClearUnread()
    {
        if (!_alert) return;
        _alert = false;
        UiSafe.Run(() => UnreadChanged?.Invoke(this, EventArgs.Empty), "PlayersSession.UnreadChanged");
    }

    // ---- Windows -----------------------------------------------------------------------------------------------------------

    /// <summary>The player list, when it was opened.</summary>
    public PlayerListWindow? PlayerList => Context.Windows.Find(ToolIds.PlayerList) as PlayerListWindow;

    /// <summary>Shows the player list (one per session).</summary>
    /// <returns>The window.</returns>
    public Window? ShowPlayerList()
    {
        Window? window = Context.Windows.ShowSingle(ToolIds.PlayerList, () => new PlayerListWindow(this));
        (window as PlayerListWindow)?.OnShownByUser();
        return window;
    }

    /// <summary>Shows the accounts list (one per session).</summary>
    /// <returns>The window.</returns>
    public Window? ShowAccounts() => Context.Windows.ShowSingle(ToolIds.Accounts, () => new AccountsListWindow(this));

    /// <summary>Shows "Get Accounts List" (one per session).</summary>
    /// <returns>The window.</returns>
    public Window? ShowGetAccounts()
    {
        Window? window = Context.Windows.ShowSingle("accounts:query", () => new GetAccountsWindow(this));
        (window as GetAccountsWindow)?.FocusSpecification();
        return window;
    }

    /// <summary>Opens the account editor for a new account.</summary>
    /// <returns>The window.</returns>
    public Window? ShowNewAccount() => Context.Windows.ShowSingle("account-new", () => new AccountEditorWindow(this, null));

    /// <summary>
    /// Opens the PM window of a player (one per player id), shows the messages that were waiting and marks them read. The account
    /// and nickname are looked up when not given: this server's players, waiting messages, earlier messages, other servers' lists.
    /// </summary>
    /// <param name="playerId">The player.</param>
    /// <param name="account">The account, or null.</param>
    /// <param name="nick">The nickname, or null.</param>
    /// <returns>The window, or null when the player is unknown.</returns>
    public Window? OpenPrivateMessage(int playerId, string? account = null, string? nick = null)
    {
        if (playerId == 0 || _ended) return null;
        (string resolvedAccount, string resolvedNick) = Resolve(playerId, account, nick);
        if (resolvedAccount.Length == 0) return null;
        _identities[playerId] = (resolvedAccount, resolvedNick);
        string source = SourceServerOf(playerId, resolvedAccount, resolvedNick);
        Window? window = Context.Windows.ShowSingle("pm:" + playerId, () =>
        {
            var created = new PrivateMessageWindow(this, playerId, resolvedAccount, resolvedNick, source);
            if (_pending.TryGetValue(playerId, out PendingPm? waiting))
                foreach (string message in waiting.Messages) created.AddIncoming(message);
            return created;
        });
        if (window is PrivateMessageWindow pm) pm.FocusReply();
        MarkRead(playerId);
        return window;
    }

    /// <summary>Opens the PM history of an account ("History: account - nick").</summary>
    /// <param name="account">The account.</param>
    /// <param name="nick">The nickname.</param>
    /// <returns>The window, or null without an account.</returns>
    public TextViewerWindow? ShowHistory(string account, string nick)
    {
        if (string.IsNullOrEmpty(account) || _ended) return null;
        var window = new TextViewerWindow(App, "History: " + account + " - " + nick, string.Empty, PmHistory.IsHeader) { Width = 540, Height = 475 };
        Context.Windows.Track(window);
        window.Show();
        UiSafe.Fire(async () =>
        {
            string text = await History.ReadAsync(account, window.ClosedToken).ConfigureAwait(true);
            if (!window.IsClosed) window.Text = text;
        }, "PlayersSession.ShowHistory");
        return window;
    }

    /// <summary>Marks a player's messages as looked at: its icon stops blinking and the alert ends (<c>markPrivateMessageRead</c>).</summary>
    /// <param name="playerId">The player.</param>
    public void MarkRead(int playerId)
    {
        bool removed = _pending.Remove(playerId);
        _pendingOrder.Remove(playerId);
        if (LatestPmPlayerId == playerId) LatestPmPlayerId = _pendingOrder.Count == 0 ? 0 : _pendingOrder[^1];
        ClearUnread();
        if (removed) RaisePendingChanged();
    }

    /// <summary>The account and nickname known for a player id.</summary>
    /// <param name="playerId">The player.</param>
    /// <param name="account">What the caller knows, or null.</param>
    /// <param name="nick">What the caller knows, or null.</param>
    /// <returns>The account (empty when unknown) and nickname.</returns>
    public (string Account, string Nick) Resolve(int playerId, string? account, string? nick)
    {
        string resolvedAccount = account ?? string.Empty;
        string resolvedNick = nick ?? string.Empty;
        if (resolvedAccount.Length > 0 && resolvedNick.Length > 0) return (resolvedAccount, resolvedNick);
        RcPlayer? player = Context.Session.Players.FirstOrDefault(item => item.Id == playerId);
        if (player is not null) return Fill(player.Account, player.Nickname);
        if (_pending.TryGetValue(playerId, out PendingPm? pending)) return Fill(pending.Account, pending.Nick);
        if (_identities.TryGetValue(playerId, out var known)) return Fill(known.Account, known.Nick);
        foreach (IReadOnlyList<RemotePlayer> players in _serverPlayers.Values)
        {
            RemotePlayer? remote = players.FirstOrDefault(item => item.Id == playerId);
            if (remote is not null) return Fill(remote.Account, remote.Nick);
        }
        return (resolvedAccount, resolvedNick);

        (string, string) Fill(string foundAccount, string foundNick) =>
            (resolvedAccount.Length > 0 ? resolvedAccount : foundAccount, resolvedNick.Length > 0 ? resolvedNick : foundNick);
    }

    /// <summary>The other server a player was listed on (<c>privateMessageSourceServer</c>), or empty for this server or none.</summary>
    private string SourceServerOf(int playerId, string account, string nick)
    {
        foreach ((string server, IReadOnlyList<RemotePlayer> players) in _serverPlayers)
        {
            if (string.Equals(server, Context.ServerName, StringComparison.OrdinalIgnoreCase)) continue;
            if (players.Any(item => playerId > 0 && item.Id == playerId || account.Length > 0 && item.Account == account || nick.Length > 0 && item.Nick == nick)) return server;
        }
        return string.Empty;
    }

    /// <summary>Forgets a PM server's players ("Close server").</summary>
    /// <param name="server">The server.</param>
    public void DropServerPlayers(string server)
    {
        if (_serverPlayers.Remove(server)) ServerPlayersChanged?.Invoke(this, server);
    }

    /// <summary>The C++ behaviour of the status combo: <c>PLI_PLAYERPROPS 'U' (status + 32)</c>.</summary>
    /// <param name="status">0 Online, 1 Away, 2 Offline.</param>
    /// <returns>Whether it was sent.</returns>
    public Task<bool> SendStatusAsync(int status)
    {
        if (_ended) return Task.FromResult(false);
        byte value = (byte)Math.Clamp(status + 32, 32, 255);
        return Context.Session.SendRawPacketAsync((int)Protocol.Wire.ClientPacketId.PlayerProps, new byte[] { (byte)'U', value });
    }

    // ---- Session events ------------------------------------------------------------------------------------------------------

    private IDisposable Subscribe(IRcSession session)
    {
        EventHandler<PrivateMessageEventArgs> pm = UiSafe.Handler<PrivateMessageEventArgs>((_, e) => OnPrivateMessage(e), "PlayersSession.PrivateMessage");
        EventHandler<PlayerRightsEventArgs> rights = UiSafe.Handler<PlayerRightsEventArgs>((_, e) => OnRights(e), "PlayersSession.Rights");
        EventHandler<PlayerAttributesEventArgs> attributes = UiSafe.Handler<PlayerAttributesEventArgs>((_, e) => OnAttributes(e), "PlayersSession.Attributes");
        EventHandler<AccountTextEventArgs> comments = UiSafe.Handler<AccountTextEventArgs>((_, e) => OnComments(e), "PlayersSession.Comments");
        EventHandler<AccountDataEventArgs> account = UiSafe.Handler<AccountDataEventArgs>((_, e) => OnAccount(e), "PlayersSession.Account");
        EventHandler<ProfileEventArgs> profile = UiSafe.Handler<ProfileEventArgs>((_, e) => OnProfile(e), "PlayersSession.Profile");
        EventHandler<AccountListEventArgs> accounts = UiSafe.Handler<AccountListEventArgs>((_, e) => OnAccountList(e), "PlayersSession.AccountList");
        EventHandler<BanDataEventArgs> ban = UiSafe.Handler<BanDataEventArgs>((_, e) => OnBanData(e), "PlayersSession.BanData");
        EventHandler<BanListEventArgs> banList = UiSafe.Handler<BanListEventArgs>((_, e) => OnBanList(e), "PlayersSession.BanList");
        EventHandler<ServerDataEventArgs> data = UiSafe.Handler<ServerDataEventArgs>((_, e) => OnServerData(e), "PlayersSession.ServerData");
        EventHandler<PmServerPlayersEventArgs> remote = UiSafe.Handler<PmServerPlayersEventArgs>((_, e) => OnServerPlayers(e), "PlayersSession.ServerPlayers");
        session.PrivateMessageReceived += pm;
        session.PlayerRightsReceived += rights;
        session.PlayerAttributesReceived += attributes;
        session.PlayerCommentsReceived += comments;
        session.AccountReceived += account;
        session.ProfileReceived += profile;
        session.AccountListReceived += accounts;
        session.BanDataReceived += ban;
        session.BanListReceived += banList;
        session.ServerDataReceived += data;
        session.PmServerPlayersReceived += remote;
        return new Unsubscriber(() =>
        {
            session.PrivateMessageReceived -= pm;
            session.PlayerRightsReceived -= rights;
            session.PlayerAttributesReceived -= attributes;
            session.PlayerCommentsReceived -= comments;
            session.AccountReceived -= account;
            session.ProfileReceived -= profile;
            session.AccountListReceived -= accounts;
            session.BanDataReceived -= ban;
            session.BanListReceived -= banList;
            session.ServerDataReceived -= data;
            session.PmServerPlayersReceived -= remote;
        });
    }

    private void OnPrivateMessage(PrivateMessageEventArgs e)
    {
        if (_ended || e.PlayerId < 0) return;
        if (e.Kind == PrivateMessageKind.Mass && Options.NoMassMessages) return;
        (string account, string nick) = Resolve(e.PlayerId, e.Account, e.Nickname);
        string sender = nick.Length > 0 ? nick : account;
        _ = History.AppendAsync(account, sender, e.Text);
        if (account.Length > 0) _identities[e.PlayerId] = (account, nick);
        string identity = PmText.Identity(account, nick);

        if (Context.Windows.Find("pm:" + e.PlayerId) is PrivateMessageWindow { IsClosed: false } open)
        {
            open.AddIncoming(e.Text);
            open.ShowAndActivate();
            return;
        }

        if (!_pending.TryGetValue(e.PlayerId, out PendingPm? pending))
        {
            pending = new PendingPm(account, nick, e.Kind);
            _pending[e.PlayerId] = pending;
        }
        else
        {
            if (account.Length > 0) pending.Account = account;
            if (nick.Length > 0) pending.Nick = nick;
            pending.Kind = e.Kind;
        }
        pending.Messages.Add(e.Text);
        if (pending.Messages.Count > PendingPm.MaximumMessages) pending.Messages.RemoveRange(0, pending.Messages.Count - PendingPm.MaximumMessages);
        _pendingOrder.Remove(e.PlayerId);
        _pendingOrder.Add(e.PlayerId);
        LatestPmPlayerId = e.PlayerId;
        RaisePendingChanged();

        // The "New PM from …" line in the RC chat is the main window's (it knows whether the PM window is open and does not flash
        // twice); this group keeps the unread state, the sound and the tray.
        if (Options.NotificationSounds) App.Sounds.Beep();
        if (!_alert)
        {
            _alert = true;
            UiSafe.Run(() => UnreadChanged?.Invoke(this, EventArgs.Empty), "PlayersSession.UnreadChanged");
        }
        Context.Shell.RequestAttention(Context, AttentionKind.PrivateMessage);
    }

    private void OnRights(PlayerRightsEventArgs e)
    {
        if (_ended || string.IsNullOrEmpty(e.Account)) return;
        ShowDialog(new RightsWindow(this, e.Account, e.Rights));
    }

    private void OnAttributes(PlayerAttributesEventArgs e)
    {
        if (_ended || string.IsNullOrEmpty(e.Account)) return;
        string text = string.IsNullOrEmpty(e.EditorText) ? e.Attributes.ToEditorText() : e.EditorText;
        Backup("attributes", e.Account, text, modified: false);
        ShowDialog(new AttributesWindow(this, e.Attributes, text));
    }

    private void OnComments(AccountTextEventArgs e)
    {
        if (_ended || string.IsNullOrEmpty(e.Account)) return;
        Backup("comments", e.Account, e.Text, modified: false);
        ShowDialog(new CommentsWindow(this, e.Account, e.Text));
    }

    private void OnProfile(ProfileEventArgs e)
    {
        if (_ended || string.IsNullOrEmpty(e.Account)) return;
        Backup("profile", e.Account, string.Join('\n', new[] { e.Account }.Concat(e.Fields)), modified: false);
        ShowDialog(new ProfileWindow(this, e.Account, e.Fields));
    }

    private void OnAccount(AccountDataEventArgs e)
    {
        if (_ended) return;
        string account = e.Account.Account;
        Window? window = Context.Windows.ShowSingle("account:" + account, () => new AccountEditorWindow(this, e.Account));
        if (window is AccountEditorWindow editor && !ReferenceEquals(editor.Data, e.Account)) editor.Load(e.Account);
    }

    private void OnAccountList(AccountListEventArgs e)
    {
        if (_ended) return;
        if (ShowAccounts() is AccountsListWindow list) list.SetAccounts(e.Accounts);
    }

    private void OnBanData(BanDataEventArgs e)
    {
        if (_ended || string.IsNullOrEmpty(e.Account)) return;
        // The legacy answer (Alpaxis sends it for /openaccess and /openban too) is shown as legacy data: read as lister lines, a banned
        // account looked not banned.
        if (Context.Windows.ShowSingle("ban", () => new BanWindow(this)) is BanWindow window)
            window.Load(e.Account, e.ComputerId, e.Details, UseNewBanType && !BanTexts.IsLegacyDetails(e.Details));
    }

    private void OnBanList(BanListEventArgs e)
    {
        if (_ended) return;
        switch (e.Kind)
        {
            case "bantypes":
                _banTypes = BanTexts.ParseTypes(e.Content);
                UiSafe.Run(() => BanTypesChanged?.Invoke(this, EventArgs.Empty), "PlayersSession.BanTypes");
                break;
            case "banhistory" or "staffactivity":
                string title = (e.Kind == "banhistory" ? "Ban History of " : "Staff Activity of ") + e.Account;
                var window = new TextViewerWindow(App, title, BanTexts.HistoryText(e.Content), BanTexts.IsHistoryHeader) { Width = 440, Height = 300 };
                Context.Windows.Track(window);
                window.Show();
                break;
        }
    }

    private void OnServerData(ServerDataEventArgs e)
    {
        if (_ended || e.Kind != "statuslist") return;
        _statusNames = (e.Content ?? string.Empty).Split(',').Select(name => name.Trim()).Where(name => name.Length > 0).ToArray();
        UiSafe.Run(() => StatusNamesChanged?.Invoke(this, EventArgs.Empty), "PlayersSession.StatusNames");
    }

    private void OnServerPlayers(PmServerPlayersEventArgs e)
    {
        if (_ended || string.IsNullOrEmpty(e.Server)) return;
        var players = new List<RemotePlayer>();
        foreach (string raw in (e.Players ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            string[] fields = line.Split('\t');
            string account = fields.Length > 1 ? fields[1] : string.Empty;
            string nick = fields.Length > 2 ? fields[2] : string.Empty;
            string level = fields.Length > 3 ? fields[3] : string.Empty;
            if (!int.TryParse(fields[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int id))
            {
                id = 0;
                nick = line;
            }
            if (nick.Length == 0) nick = account;
            players.Add(new RemotePlayer(id, account, nick, level));
        }
        _serverPlayers[e.Server] = players;
        ServerPlayersChanged?.Invoke(this, e.Server);
    }

    private void OnSessionReplaced(object? sender, EventArgs e)
    {
        UiSafe.Run(() =>
        {
            _serverPlayers.Clear();
            _banTypes = Array.Empty<BanType>();
            ServerPlayersChanged?.Invoke(this, null);
            BanTypesChanged?.Invoke(this, EventArgs.Empty);
        }, "PlayersSession.SessionReplaced");
    }

    private void OnEnded(object? sender, EventArgs e)
    {
        if (_ended) return;
        _ended = true;
        _attachment.Dispose();
        _provided.Dispose();
        Context.SessionReplaced -= OnSessionReplaced;
        Context.Ended -= OnEnded;
        s_sessions.Remove(Context);
    }

    private void RaisePendingChanged() => UiSafe.Run(() => PendingChanged?.Invoke(this, EventArgs.Empty), "PlayersSession.PendingChanged");

    /// <summary>Whether ban requests and the Edit Access window use the new ban type: the option <c>usenewbantype</c> and the session's protocol.</summary>
    public bool UseNewBanType => Options.UseNewBanType && Context.Session.UseNewBanProtocol;

    /// <summary>Shows an edit dialog as one of the session's windows (any number at once, none modal).</summary>
    /// <param name="window">The dialog.</param>
    public void ShowDialog(RcWindow window)
    {
        if (_ended) return;
        Context.Windows.Track(window);
        window.Show();
    }

    /// <summary>Keeps a copy of an editor text (<c>backupEditorText</c>), off the UI thread.</summary>
    /// <param name="type">attributes, comments or profile.</param>
    /// <param name="account">The account.</param>
    /// <param name="text">The text.</param>
    /// <param name="modified">Whether it is the text being sent.</param>
    public void Backup(string type, string account, string text, bool modified)
    {
        var backup = new EditorBackup(App.Paths.BackupDirectory, Context.ServerName);
        _ = backup.WriteAsync(type, account, text, modified);
    }
}
