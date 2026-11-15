using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Players;

/// <summary>
/// The player list of a session (<c>TPlayerList</c>), "Player List - &lt;server&gt;", 580 by 420. Tabs: "This server" (the groups
/// Admins and Players with icon, Nick, Account, Level and ID, sortable and resizable columns, type to search), "Guilds",
/// "Servers" (other servers and their players) and "Channels". Bottom: the status (Online, Away, Offline), Refresh, Mass PM,
/// Admin Message and Close (closing only hides it).
/// </summary>
/// <remarks>
/// Rows are view models updated in place from the session's player events, so the selection and the scroll position survive
/// updates, and the server and guild lists are only asked for when the window is shown or Refresh is pressed (defect D8). Every
/// menu item is bound to the player it was opened for. A player with an unread message blinks between its status icon and the
/// icon of the message's kind every 500 ms.
/// </remarks>
internal sealed partial class PlayerListWindow : RcWindow
{
    private readonly PlayersSession? _owner;
    private readonly ColumnLayout _playerColumns = new(new[] { "", "Nick", "Account", "Level", "ID" }, new double[] { 24, 180, 120, 120, 60 }, new[] { false, false, false, false, true });
    private readonly ColumnLayout _guildColumns = new(new[] { "", "Nick", "Account" }, new double[] { 24, 220, 160 });
    private readonly ColumnLayout _serverColumns = new(new[] { "", "Nick", "Account" }, new double[] { 24, 220, 160 });
    private readonly ColumnLayout _channelColumns = new(new[] { "", "Nick", "Players", "ID" }, new double[] { 24, 220, 80, 80 });
    private readonly PlayerRow _adminsGroup = new("Admins", admins: true);
    private readonly PlayerRow _playersGroup = new("Players", admins: false);
    private readonly List<PlayerRow> _adminRows = new();
    private readonly List<PlayerRow> _playerRows = new();
    private readonly Dictionary<int, PlayerRow> _online = new();
    private readonly Dictionary<int, PlayerRow> _offline = new();
    private readonly Dictionary<string, ServerState> _servers = new(StringComparer.Ordinal);
    private readonly ColumnHeaderBar _playerHeader;
    private readonly ColumnHeaderBar _guildHeader;
    private readonly ColumnHeaderBar _channelHeader;
    private readonly ListBox _players;
    private readonly ListBox _guilds;
    private readonly ListBox _serverList;
    private readonly ListBox _channels;
    private long _order;
    private int _sortColumn;
    private bool _descending;
    private int _guildSort = 1;
    private bool _guildDescending;
    private int _channelSort = 1;
    private bool _channelDescending;
    private bool _blinkOn = true;
    private bool _statusFromCode;
    private bool _autoAway;

    /// <summary>For the designer.</summary>
    public PlayerListWindow() : this(null)
    {
    }

    /// <summary>Creates the player list of a session.</summary>
    /// <param name="owner">The session.</param>
    public PlayerListWindow(PlayersSession? owner) : base(owner?.App)
    {
        InitializeComponent();
        _owner = owner;
        HideOnClose = true;
        Items = new ObservableCollection<PlayerRow>();

        (_players, _playerHeader) = MakeList(PlayersHost, _playerColumns, "PlayerColumn", "Players",
            RowTemplates.Make<PlayerRow>(_playerColumns, nameof(PlayerRow.Icon), nameof(PlayerRow.Weight), null, nameof(PlayerRow.Nick), nameof(PlayerRow.Account), nameof(PlayerRow.Level), nameof(PlayerRow.IdText)));
        _players.SelectionMode = SelectionMode.Multiple;
        _players.ItemsSource = Items;
        _playerHeader.SortRequested += UiSafe.Handler<int>((_, column) => SortPlayers(column), "PlayerList.Sort");
        Track(ListSearch.Attach(_players, item => item is PlayerRow { IsGroup: false } row ? row.Nick + " " + row.Account : null));
        _players.Tapped += UiSafe.Handler<TappedEventArgs>((_, e) => { if (RowAt<PlayerRow>(e.Source) is { IsGroup: true } group) ToggleGroup(group); }, "PlayerList.Tapped");
        _players.DoubleTapped += UiSafe.Handler<TappedEventArgs>((_, e) => { if (RowAt<PlayerRow>(e.Source) is { IsGroup: false } row) OpenPm(row); }, "PlayerList.DoubleTapped");
        _players.KeyDown += UiSafe.Handler<KeyEventArgs>(OnPlayersKeyDown, "PlayerList.Keys");
        _players.ContextRequested += UiSafe.Handler<ContextRequestedEventArgs>(OnPlayersContext, "PlayerList.Menu");

        (_guilds, _guildHeader) = MakeList(GuildsHost, _guildColumns, "GuildColumn", "Guilds",
            RowTemplates.Make<SimpleRow>(_guildColumns, nameof(SimpleRow.Icon), null, null, nameof(SimpleRow.Text1), nameof(SimpleRow.Text2)));
        _guilds.ItemsSource = Guilds;
        _guildHeader.SortRequested += UiSafe.Handler<int>((_, column) => { (_guildSort, _guildDescending) = NextSort(_guildSort, _guildDescending, column); RefreshGuilds(); }, "PlayerList.GuildSort");
        Track(ListSearch.Attach(_guilds, item => (item as SimpleRow)?.Text1));

        (_serverList, _) = MakeList(ServersHost, _serverColumns, "ServerColumn", "Servers",
            RowTemplates.Make<SimpleRow>(_serverColumns, nameof(SimpleRow.Icon), null, nameof(SimpleRow.Indent), nameof(SimpleRow.Text1), nameof(SimpleRow.Text2)));
        _serverList.ItemsSource = Servers;
        Track(ListSearch.Attach(_serverList, item => (item as SimpleRow)?.Text1));
        _serverList.Tapped += UiSafe.Handler<TappedEventArgs>((_, e) => { if (RowAt<SimpleRow>(e.Source) is { Player: null } server) ActivateServer(server.Server); }, "PlayerList.ServerTapped");
        _serverList.DoubleTapped += UiSafe.Handler<TappedEventArgs>((_, e) =>
        {
            if (RowAt<SimpleRow>(e.Source) is { Player: { } player }) _owner?.OpenPrivateMessage(player.Id, player.Account, player.Nick);
        }, "PlayerList.ServerDoubleTapped");
        _serverList.ContextRequested += UiSafe.Handler<ContextRequestedEventArgs>(OnServersContext, "PlayerList.ServerMenu");

        (_channels, _channelHeader) = MakeList(ChannelsHost, _channelColumns, "ChannelColumn", "Channels",
            RowTemplates.Make<SimpleRow>(_channelColumns, nameof(SimpleRow.Icon), null, null, nameof(SimpleRow.Text1), nameof(SimpleRow.Text2), nameof(SimpleRow.Text3)));
        _channels.ItemsSource = Channels;
        _channelHeader.SortRequested += UiSafe.Handler<int>((_, column) => { (_channelSort, _channelDescending) = NextSort(_channelSort, _channelDescending, column); RefreshChannels(); }, "PlayerList.ChannelSort");
        Track(ListSearch.Attach(_channels, item => (item as SimpleRow)?.Text1));

        RefreshButton.Click += On.Click(Refresh, "PlayerList.Refresh");
        MassPmButton.Click += On.ClickAsync(MassPmAsync, "PlayerList.MassPm");
        AdminMessageButton.Click += On.ClickAsync(() => _owner?.AdminMessageToAllAsync(this) ?? Task.FromResult(false), "PlayerList.AdminMessage");
        CloseButton.Click += On.Click(HideByUser, "PlayerList.Close");
        StatusCombo.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => OnStatusChanged(), "PlayerList.Status");
        Activated += UiSafe.Handler((_, _) => _owner?.ClearUnread(), "PlayerList.Activated");
        Every(TimeSpan.FromMilliseconds(500), Blink, "PlayerList.Blink");

        if (owner is null) return;
        UpdateTitle();
        EventHandler pending = UiSafe.Handler((_, _) => OnPendingChanged(), "PlayerList.Pending");
        EventHandler statuses = UiSafe.Handler((_, _) => RefreshIcons(), "PlayerList.Statuses");
        EventHandler<string?> remote = UiSafe.Handler<string?>((_, server) => OnServerPlayers(server), "PlayerList.ServerPlayers");
        EventHandler replaced = UiSafe.Handler((_, _) => UpdateTitle(), "PlayerList.Replaced");
        EventHandler<OptionsChangedEventArgs> options = UiSafe.Handler<OptionsChangedEventArgs>((_, e) => OnOptionsChanged(e), "PlayerList.Options");
        owner.PendingChanged += pending;
        owner.StatusNamesChanged += statuses;
        owner.ServerPlayersChanged += remote;
        owner.Context.SessionReplaced += replaced;
        owner.App.Options.Changed += options;
        OnClosedOnce(() =>
        {
            owner.PendingChanged -= pending;
            owner.StatusNamesChanged -= statuses;
            owner.ServerPlayersChanged -= remote;
            owner.Context.SessionReplaced -= replaced;
            owner.App.Options.Changed -= options;
        });
        Track(owner.Context.Attach(Subscribe));
    }

    /// <summary>The rows of "This server" as shown (groups and the players of the expanded groups).</summary>
    public ObservableCollection<PlayerRow> Items { get; }

    /// <summary>The rows of "Guilds".</summary>
    public ObservableCollection<SimpleRow> Guilds { get; } = new();

    /// <summary>The rows of "Servers".</summary>
    public ObservableCollection<SimpleRow> Servers { get; } = new();

    /// <summary>The rows of "Channels".</summary>
    public ObservableCollection<SimpleRow> Channels { get; } = new();

    /// <summary>The list of "This server" (for tests).</summary>
    internal ListBox PlayersList => _players;

    /// <summary>The list of "Servers" (for tests).</summary>
    internal ListBox ServersList => _serverList;

    /// <summary>The header of "This server" (for tests).</summary>
    internal ColumnHeaderBar PlayersHeader => _playerHeader;

    /// <summary>The column widths of "This server" (for tests).</summary>
    internal ColumnLayout PlayerColumns => _playerColumns;

    /// <summary>The "Admins" group row.</summary>
    internal PlayerRow AdminsGroup => _adminsGroup;

    /// <summary>The "Players" group row.</summary>
    internal PlayerRow PlayersGroup => _playersGroup;

    /// <summary>The row of a player (online or offline), or null.</summary>
    internal PlayerRow? RowOf(int playerId) => _online.TryGetValue(playerId, out PlayerRow? row) ? row : _offline.GetValueOrDefault(playerId);

    /// <summary>Called each time the session shows the window (the C++ <c>open</c>): the PM server and guild lists are asked for.</summary>
    public void OnShownByUser()
    {
        RequestRemoteLists();
        if (_autoAway)
        {
            _autoAway = false;
            SetStatus(0);
        }
    }

    /// <summary>Selects a player row (for tests and the search).</summary>
    internal void Select(PlayerRow row) => _players.SelectedItem = row;

    /// <summary>The menu of a player row, bound to that player.</summary>
    internal List<Control> MenuFor(PlayerRow row) => _owner?.PlayerMenu(this, row.Target) ?? new List<Control>();

    // ---- Session ---------------------------------------------------------------------------------------------------------

    private IDisposable Subscribe(IRcSession session)
    {
        EventHandler<PlayerEventArgs> joined = UiSafe.Handler<PlayerEventArgs>((_, e) => Upsert(e.Player), "PlayerList.Joined");
        EventHandler<PlayerEventArgs> left = UiSafe.Handler<PlayerEventArgs>((_, e) => RemovePlayer(e.Player.Id), "PlayerList.Left");
        EventHandler channels = UiSafe.Handler((_, _) => RefreshChannels(), "PlayerList.Channels");
        EventHandler<ValueEventArgs<int>> servers = UiSafe.Handler<ValueEventArgs<int>>((_, _) => RefreshServers(), "PlayerList.Servers");
        EventHandler<ValueEventArgs<int>> guilds = UiSafe.Handler<ValueEventArgs<int>>((_, _) => RefreshGuilds(), "PlayerList.Guilds");
        session.PlayerJoined += joined;
        session.PlayerUpdated += joined;
        session.PlayerLeft += left;
        session.ChannelsChanged += channels;
        session.PmServersUpdated += servers;
        session.PmGuildsUpdated += guilds;
        Reload(session);
        return new Unsubscriber(() =>
        {
            session.PlayerJoined -= joined;
            session.PlayerUpdated -= joined;
            session.PlayerLeft -= left;
            session.ChannelsChanged -= channels;
            session.PmServersUpdated -= servers;
            session.PmGuildsUpdated -= guilds;
        });
    }

    /// <summary>Shows what a connection's caches hold (on opening, after a reconnect and on Refresh).</summary>
    private void Reload(IRcSession session)
    {
        HashSet<int> selected = SelectedIds();
        _online.Clear();
        _offline.Clear();
        _adminRows.Clear();
        _playerRows.Clear();
        foreach (RcPlayer player in session.Players)
        {
            if (player.IsChannel) continue;
            var row = new PlayerRow(player.Id, ++_order, offline: false);
            Fill(row, player);
            _online[player.Id] = row;
            (row.InAdmins ? _adminRows : _playerRows).Add(row);
        }
        SyncOfflineRows(rebuild: false);
        _adminRows.Sort(Compare);
        _playerRows.Sort(Compare);
        RebuildItems(selected);
        RefreshGuilds(session);
        RefreshServers(session);
        RefreshChannels(session);
    }

    private void Refresh()
    {
        if (_owner is null) return;
        Reload(_owner.Context.Session);
        RequestRemoteLists();
    }

    private void RequestRemoteLists()
    {
        if (_owner is null || _owner.Context.IsEnded) return;
        IRcSession session = _owner.Context.Session;
        if (!session.IsConnected) return;
        UiSafe.Fire(async () =>
        {
            await session.RequestPmServerListAsync().ConfigureAwait(true);
            await session.RequestPmGuildListAsync().ConfigureAwait(true);
        }, "PlayerList.RequestLists");
    }

    private void UpdateTitle()
    {
        string server = _owner?.Context.ServerName ?? string.Empty;
        Title = server.Length == 0 ? "Player List" : "Player List - " + server;
    }

    // ---- This server: incremental updates --------------------------------------------------------------------------------

    private void Fill(PlayerRow row, RcPlayer player)
    {
        row.Nick = player.Nickname;
        row.Account = player.Account;
        row.Level = player.Level;
        row.Status = player.StatusIcon;
        row.Icon = IconOf(row);
    }

    private void Upsert(RcPlayer player)
    {
        if (player.IsChannel)
        {
            RemovePlayer(player.Id);
            return;
        }
        if (_offline.Remove(player.Id, out PlayerRow? offline)) RemoveRow(offline);
        if (!_online.TryGetValue(player.Id, out PlayerRow? row))
        {
            row = new PlayerRow(player.Id, ++_order, offline: false);
            Fill(row, player);
            _online[player.Id] = row;
            InsertRow(row);
            return;
        }
        bool wasAdmin = row.InAdmins;
        Fill(row, player);
        if (wasAdmin != row.InAdmins || !IsInPlace(row)) MoveRow(row, wasAdmin);
    }

    private void RemovePlayer(int playerId)
    {
        if (!_online.Remove(playerId, out PlayerRow? row)) return;
        RemoveRow(row, row.InAdmins);
        if (_owner?.Pending.ContainsKey(playerId) == true) SyncOfflineRows(rebuild: true);
    }

    /// <summary>Players that sent a PM not looked at yet and are not online stay listed under Players with level "Offline".</summary>
    private void SyncOfflineRows(bool rebuild)
    {
        if (_owner is null) return;
        foreach ((int id, PendingPm pending) in _owner.Pending)
        {
            if (_online.ContainsKey(id) || _offline.ContainsKey(id) || pending.Account.Length == 0 && pending.Nick.Length == 0) continue;
            var row = new PlayerRow(id, long.MaxValue / 2 + id, offline: true)
            {
                Nick = pending.Nick.Length > 0 ? pending.Nick : pending.Account,
                Account = pending.Account,
                Level = "Offline",
            };
            row.Icon = IconOf(row);
            _offline[id] = row;
            if (rebuild) InsertRow(row);
            else _playerRows.Add(row);
        }
        foreach (int id in _offline.Keys.Where(id => !_owner.Pending.ContainsKey(id)).ToArray())
        {
            PlayerRow row = _offline[id];
            _offline.Remove(id);
            if (rebuild) RemoveRow(row);
            else _playerRows.Remove(row);
        }
    }

    private List<PlayerRow> ListOf(bool admins) => admins ? _adminRows : _playerRows;

    private PlayerRow GroupOf(bool admins) => admins ? _adminsGroup : _playersGroup;

    private int GroupIndex(bool admins) => admins ? 0 : 1 + (_adminsGroup.IsExpanded ? _adminRows.Count : 0);

    private int PositionFor(List<PlayerRow> list, PlayerRow row)
    {
        int low = 0, high = list.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (Compare(list[middle], row) <= 0) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private void InsertRow(PlayerRow row)
    {
        bool admins = row.InAdmins;
        List<PlayerRow> list = ListOf(admins);
        int position = PositionFor(list, row);
        list.Insert(position, row);
        if (GroupOf(admins).IsExpanded) Items.Insert(GroupIndex(admins) + 1 + position, row);
    }

    private void RemoveRow(PlayerRow row) => RemoveRow(row, ListOf(true).Contains(row));

    private void RemoveRow(PlayerRow row, bool admins)
    {
        List<PlayerRow> list = ListOf(admins);
        int position = list.IndexOf(row);
        if (position < 0)
        {
            admins = !admins;
            list = ListOf(admins);
            position = list.IndexOf(row);
            if (position < 0) return;
        }
        list.RemoveAt(position);
        if (GroupOf(admins).IsExpanded) Items.RemoveAt(GroupIndex(admins) + 1 + position);
    }

    private bool IsInPlace(PlayerRow row)
    {
        List<PlayerRow> list = ListOf(row.InAdmins);
        int index = list.IndexOf(row);
        if (index < 0) return false;
        return (index == 0 || Compare(list[index - 1], row) <= 0) && (index == list.Count - 1 || Compare(row, list[index + 1]) <= 0);
    }

    private void MoveRow(PlayerRow row, bool wasAdmin)
    {
        bool selected = _players.SelectedItems?.Contains(row) == true;
        RemoveRow(row, wasAdmin);
        InsertRow(row);
        if (selected && Items.Contains(row) && _players.SelectedItems is { } items && !items.Contains(row)) items.Add(row);
    }

    private void RebuildItems(HashSet<int>? selected = null)
    {
        selected ??= SelectedIds();
        Items.Clear();
        Items.Add(_adminsGroup);
        if (_adminsGroup.IsExpanded) foreach (PlayerRow row in _adminRows) Items.Add(row);
        Items.Add(_playersGroup);
        if (_playersGroup.IsExpanded) foreach (PlayerRow row in _playerRows) Items.Add(row);
        _adminsGroup.Icon = App?.Assets.ChannelIcon(_adminsGroup.IsExpanded);
        _playersGroup.Icon = App?.Assets.ChannelIcon(_playersGroup.IsExpanded);
        if (selected.Count == 0 || _players.SelectedItems is not { } items) return;
        foreach (PlayerRow row in Items)
            if (!row.IsGroup && selected.Contains(row.Id)) items.Add(row);
    }

    private HashSet<int> SelectedIds()
    {
        var ids = new HashSet<int>();
        if (_players?.SelectedItems is { } items)
            foreach (object? item in items)
                if (item is PlayerRow { IsGroup: false } row) ids.Add(row.Id);
        return ids;
    }

    private void ToggleGroup(PlayerRow group)
    {
        bool selected = ReferenceEquals(_players.SelectedItem, group);
        group.IsExpanded = !group.IsExpanded;
        RebuildItems();
        if (selected)
        {
            _players.SelectedItem = group;
            _players.ContainerFromItem(group)?.Focus();
        }
    }

    private int Compare(PlayerRow left, PlayerRow right)
    {
        int result = _sortColumn switch
        {
            1 => string.Compare(left.Nick, right.Nick, StringComparison.OrdinalIgnoreCase),
            2 => string.Compare(left.Account, right.Account, StringComparison.OrdinalIgnoreCase),
            3 => string.Compare(left.Level, right.Level, StringComparison.OrdinalIgnoreCase),
            4 => left.Id.CompareTo(right.Id),
            _ => 0,
        };
        if (_descending) result = -result;
        return result != 0 ? result : left.Order.CompareTo(right.Order);
    }

    private static (int Column, bool Descending) NextSort(int column, bool descending, int clicked) =>
        clicked == column ? (column, !descending) : (clicked, false);

    private void SortPlayers(int column)
    {
        if (column == 0)
        {
            _sortColumn = 0;
            _descending = false;
        }
        else
        {
            (_sortColumn, _descending) = NextSort(_sortColumn, _descending, column);
        }
        _playerHeader.ShowSort(_sortColumn == 0 ? -1 : _sortColumn, _descending);
        _adminRows.Sort(Compare);
        _playerRows.Sort(Compare);
        RebuildItems();
    }

    /// <summary>The column the players are sorted by (0 = the server's order) and the direction.</summary>
    internal (int Column, bool Descending) PlayerSort => (_sortColumn, _descending);

    // ---- Icons -----------------------------------------------------------------------------------------------------------

    private IImage? IconOf(PlayerRow row)
    {
        if (App is null) return null;
        if (_owner is not null && _owner.Pending.TryGetValue(row.Id, out PendingPm? pending) && _blinkOn) return App.Assets.PmIcon(KindOf(pending.Kind));
        return StatusIcon(row.Status);
    }

    /// <summary>
    /// The icon of a status index: the server's status list names it (defect D14: an index 32 too high, the raw protocol byte, is
    /// mapped back; anything else unknown is "online").
    /// </summary>
    internal IImage StatusIcon(int status)
    {
        AssetStore assets = App!.Assets;
        IReadOnlyList<string> names = _owner?.StatusNames ?? Array.Empty<string>();
        int index = status >= 0 && status < names.Count ? status : status >= 32 && status - 32 < names.Count ? status - 32 : -1;
        return index < 0 ? assets.Get(AssetStore.OnlineStatusIcon) : assets.StatusIcon(names[index]);
    }

    private static PmKind KindOf(PrivateMessageKind kind) => kind switch
    {
        PrivateMessageKind.Mass => PmKind.Mass,
        PrivateMessageKind.Guild => PmKind.Guild,
        PrivateMessageKind.Admin => PmKind.Admin,
        _ => PmKind.Normal,
    };

    private void RefreshIcons()
    {
        foreach (PlayerRow row in _online.Values) row.Icon = IconOf(row);
        foreach (PlayerRow row in _offline.Values) row.Icon = IconOf(row);
    }

    private void OnPendingChanged()
    {
        _blinkOn = true;
        SyncOfflineRows(rebuild: true);
        RefreshIcons();
    }

    private void Blink()
    {
        if (_owner is null || _owner.Pending.Count == 0) return;
        _blinkOn = !_blinkOn;
        foreach (int id in _owner.Pending.Keys)
            if (RowOf(id) is { } row) row.Icon = IconOf(row);
    }

    /// <summary>Whether the blinking currently shows the PM icons.</summary>
    internal bool BlinkShowsPmIcon => _blinkOn;

    // ---- Guilds, servers, channels --------------------------------------------------------------------------------------

    private void RefreshGuilds() => RefreshGuilds(_owner?.Context.Session);

    private void RefreshGuilds(IRcSession? session)
    {
        if (session is null) return;
        IEnumerable<string> names = session.PmGuilds;
        names = _guildSort == 1 ? (_guildDescending ? names.OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase) : names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) : names;
        _guildHeader.ShowSort(_guildSort, _guildDescending);
        Guilds.Clear();
        foreach (string guild in names) Guilds.Add(new SimpleRow { Icon = App?.Assets.ChannelIcon(false), Text1 = guild });
    }

    private void RefreshChannels() => RefreshChannels(_owner?.Context.Session);

    private void RefreshChannels(IRcSession? session)
    {
        if (session is null) return;
        Func<RcChannel, string> key = _channelSort switch { 2 => channel => channel.Players, 3 => channel => channel.Id, _ => channel => channel.Name };
        IEnumerable<RcChannel> channels = _channelDescending ? session.Channels.OrderByDescending(key, StringComparer.OrdinalIgnoreCase) : session.Channels.OrderBy(key, StringComparer.OrdinalIgnoreCase);
        _channelHeader.ShowSort(_channelSort, _channelDescending);
        Channels.Clear();
        foreach (RcChannel channel in channels)
            Channels.Add(new SimpleRow { Icon = App?.Assets.ChannelIcon(true), Text1 = channel.Name, Text2 = channel.Players, Text3 = channel.Id });
    }

    private void RefreshServers() => RefreshServers(_owner?.Context.Session);

    private void RefreshServers(IRcSession? session)
    {
        if (session is null || _owner is null) return;
        object? selected = _serverList.SelectedItem is SimpleRow row ? (row.Player is null ? row.Server : (row.Server, row.Player.Id)) : null;
        Servers.Clear();
        foreach (string server in session.PmServers)
        {
            ServerState state = _servers.TryGetValue(server, out ServerState? known) ? known : _servers[server] = new ServerState();
            bool received = _owner.ServerPlayers.TryGetValue(server, out IReadOnlyList<RemotePlayer>? players);
            state.Received = received;
            if (!received) state.Expanded = false;
            Servers.Add(new SimpleRow { Icon = App?.Assets.ChannelIcon(received && state.Expanded), Text1 = server, Server = server });
            if (received && state.Expanded && players is not null)
                foreach (RemotePlayer player in players)
                    Servers.Add(new SimpleRow { Icon = App?.Assets.Get(AssetStore.OnlineStatusIcon), Text1 = player.Nick, Text2 = player.Account, Server = server, Player = player, Indent = new Thickness(16, 0, 0, 0) });
        }
        if (selected is not null)
            _serverList.SelectedItem = Servers.FirstOrDefault(item => item.Player is null ? Equals(item.Server, selected) : Equals((item.Server, item.Player.Id), selected));
    }

    private void OnServerPlayers(string? server)
    {
        if (server is not null)
        {
            ServerState state = _servers.TryGetValue(server, out ServerState? known) ? known : _servers[server] = new ServerState();
            state.Requested = true;
            state.Received = true;
            state.Expanded = true;
        }
        else
        {
            _servers.Clear();
        }
        RefreshServers();
    }

    /// <summary>A click on a server row (<c>onServerActivated</c>): ask for its players, collapse (and unmap) or expand it.</summary>
    internal void ActivateServer(string server)
    {
        if (_owner is null) return;
        ServerState state = _servers.TryGetValue(server, out ServerState? known) ? known : _servers[server] = new ServerState();
        IRcSession session = _owner.Context.Session;
        if (!state.Requested)
        {
            if (!session.IsConnected) return;
            state.Requested = true;
            _ = session.RequestPmServerPlayersAsync(server);
        }
        else if (state.Received && state.Expanded)
        {
            state.Expanded = false;
            if (session.IsConnected) _ = session.UnmapPmServerAsync(server);
            RefreshServers();
        }
        else if (state.Received)
        {
            state.Expanded = true;
            RefreshServers();
        }
    }

    /// <summary>"Close server": collapse it, stop listing its players and forget them.</summary>
    internal void CloseServer(string server)
    {
        if (_owner is null || !_servers.TryGetValue(server, out ServerState? state) || !(state.Requested || state.Received)) return;
        _servers.Remove(server);
        IRcSession session = _owner.Context.Session;
        if (session.IsConnected) _ = session.UnmapPmServerAsync(server);
        _owner.DropServerPlayers(server);
        RefreshServers();
    }

    /// <summary>The menu of a Servers row.</summary>
    internal List<Control> ServerMenuFor(SimpleRow row)
    {
        if (_owner is null) return new List<Control>();
        if (row.Player is { } player) return _owner.RemotePlayerMenu(this, new PlayerTarget(player.Id, player.Account, player.Nick, player.Level));
        var close = new MenuItem { Header = "Close server", Name = "MenuCloseServer" };
        string server = row.Server;
        close.Click += On.Click(() => CloseServer(server), "PlayerList.CloseServer");
        return new List<Control> { close };
    }

    // ---- Input -----------------------------------------------------------------------------------------------------------

    private void OpenPm(PlayerRow row) => _owner?.OpenPrivateMessage(row.Id, row.Account, row.Nick);

    private void OnPlayersKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || _players.SelectedItem is not PlayerRow row) return;
        e.Handled = true;
        if (row.IsGroup) ToggleGroup(row);
        else OpenPm(row);
    }

    private void OnPlayersContext(object? sender, ContextRequestedEventArgs e)
    {
        PlayerRow? row = RowAt<PlayerRow>(e.Source) ?? _players.SelectedItem as PlayerRow;
        if (row is null || row.IsGroup || _owner is null) return;
        if (_players.SelectedItems?.Contains(row) != true) _players.SelectedItem = row;
        var menu = new ContextMenu { ItemsSource = MenuFor(row) };
        menu.Open(_players);
        e.Handled = true;
    }

    private void OnServersContext(object? sender, ContextRequestedEventArgs e)
    {
        if ((RowAt<SimpleRow>(e.Source) ?? _serverList.SelectedItem as SimpleRow) is not { } row) return;
        _serverList.SelectedItem = row;
        var menu = new ContextMenu { ItemsSource = ServerMenuFor(row) };
        menu.Open(_serverList);
        e.Handled = true;
    }

    private static T? RowAt<T>(object? source) where T : class =>
        (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as T;

    /// <summary>The ids Mass PM goes to: the selected players when more than one is selected, otherwise every player listed.</summary>
    internal (IReadOnlyList<int> Ids, bool Selected) MassPmTargets()
    {
        var selected = (_players.SelectedItems?.OfType<PlayerRow>() ?? Enumerable.Empty<PlayerRow>()).Where(row => !row.IsGroup && row.Id != 0).Select(row => row.Id).Distinct().ToList();
        if (selected.Count > 1) return (selected, true);
        return (_adminRows.Concat(_playerRows).Where(row => row.Id != 0).Select(row => row.Id).Distinct().ToList(), false);
    }

    private Task MassPmAsync()
    {
        if (_owner is null) return Task.CompletedTask;
        (IReadOnlyList<int> ids, bool selected) = MassPmTargets();
        return _owner.MassPmAsync(this, ids, selected);
    }

    // ---- Status and away -------------------------------------------------------------------------------------------------

    private void OnStatusChanged()
    {
        if (_statusFromCode || _owner is null) return;
        _autoAway = false;
        SendStatus();
    }

    private void SendStatus()
    {
        if (_owner is null || !_owner.Context.Session.IsConnected) return;
        _ = _owner.SendStatusAsync(Math.Max(0, StatusCombo.SelectedIndex));
    }

    /// <summary>Sets the status combo from code and sends it.</summary>
    internal void SetStatus(int index)
    {
        _statusFromCode = true;
        try
        {
            StatusCombo.SelectedIndex = index;
        }
        finally
        {
            _statusFromCode = false;
        }
        SendStatus();
    }

    /// <summary>The status chosen (0 Online, 1 Away, 2 Offline).</summary>
    internal int StatusIndex => StatusCombo.SelectedIndex;

    private void HideByUser()
    {
        HideAndRemember();
        OnHiddenByUser();
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel && !IsVisible) OnHiddenByUser();
    }

    /// <summary>"Go in away mode when closing playerlist" (<c>attachaway</c>): hiding the list sets the status to Away.</summary>
    private void OnHiddenByUser()
    {
        if (_owner is null || !_owner.Options.AttachAway || StatusCombo.SelectedIndex != 0) return;
        _autoAway = true;
        SetStatus(1);
    }

    private void OnOptionsChanged(OptionsChangedEventArgs e)
    {
        if (!e.Contains(OptionKeys.AttachAway) || _owner is null) return;
        bool away = e.Current.AttachAway && !IsVisible;
        if (away && StatusCombo.SelectedIndex == 0)
        {
            _autoAway = true;
            SetStatus(1);
        }
        else if (!away && _autoAway)
        {
            _autoAway = false;
            SetStatus(0);
        }
    }

    // ---- Construction helpers --------------------------------------------------------------------------------------------

    private static (ListBox List, ColumnHeaderBar Header) MakeList(Border host, ColumnLayout layout, string headerPrefix, string name, Avalonia.Controls.Templates.IDataTemplate template)
    {
        var header = new ColumnHeaderBar(layout, headerPrefix);
        var list = new ListBox
        {
            Name = name,
            ItemTemplate = template,
            BorderThickness = new Thickness(0),
        };
        list.Classes.Add("rows");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(list);
        ColumnsPanel.SetLayout(dock, layout);
        host.Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = dock,
        };
        return (list, header);
    }

    private sealed class ServerState
    {
        public bool Requested { get; set; }

        public bool Received { get; set; }

        public bool Expanded { get; set; }
    }

    private AppServices? App => _owner?.App;
}
