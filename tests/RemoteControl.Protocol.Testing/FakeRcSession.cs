using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Text;

namespace RemoteControl.Protocol.Testing;

/// <summary>A call made to a <see cref="FakeRcSession"/> or one of its file-browser views.</summary>
/// <param name="Method">The method name (for a view: <c>"FileBrowser.DownloadAsync"</c>, <c>"SyncFileBrowser.DownloadAsync"</c> or <c>"BackupFileBrowser.DownloadAsync"</c>).</param>
/// <param name="Arguments">The arguments, as passed (byte memory is copied into an array).</param>
public sealed record RecordedCall(string Method, IReadOnlyList<object?> Arguments)
{
    /// <summary>An argument cast to its type.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="index">The argument's index.</param>
    /// <returns>The argument.</returns>
    public T Argument<T>(int index) => (T)Arguments[index]!;
}

/// <summary>
/// An <see cref="IRcSession"/> without sockets, for window tests. Every call is recorded in <see cref="Calls"/>; every cache is a
/// mutable list or property the test sets; every event has a <c>Raise…</c> method (which also updates the matching cache, as the
/// real session would). Events are raised synchronously on the thread that calls <c>Raise…</c>. As with the real session, after
/// <see cref="RaiseDisconnected"/>, <see cref="Disconnect"/> or <see cref="Dispose"/> it raises nothing more and every call returns
/// <c>false</c> (calls are still recorded).
/// </summary>
public sealed class FakeRcSession : IRcSession
{
    private readonly List<RecordedCall> _calls = new();
    private bool _closed;

    /// <summary>Creates a fake session that looks connected and logged in.</summary>
    /// <param name="account">The account.</param>
    public FakeRcSession(string account = "TestAccount")
    {
        Account = account;
        FakeFileBrowser = new FakeFileBrowser(this, isSync: false, "FileBrowser.");
        FakeSyncFileBrowser = new FakeFileBrowser(this, isSync: true, "SyncFileBrowser.");
        FakeBackupFileBrowser = new FakeFileBrowser(this, isSync: false, "BackupFileBrowser.");
    }

    // ---- Recording -------------------------------------------------------------------------------------------------------------

    /// <summary>Every call so far, in order.</summary>
    public IReadOnlyList<RecordedCall> Calls
    {
        get
        {
            lock (_calls) return _calls.ToArray();
        }
    }

    /// <summary>The calls to one method.</summary>
    /// <param name="method">The method name (use <c>nameof</c>).</param>
    /// <returns>The calls.</returns>
    public IReadOnlyList<RecordedCall> CallsTo(string method) => Calls.Where(call => call.Method == method).ToArray();

    /// <summary>Whether a method was called.</summary>
    /// <param name="method">The method name.</param>
    /// <returns>Whether.</returns>
    public bool WasCalled(string method) => Calls.Any(call => call.Method == method);

    /// <summary>The last call to a method, or <c>null</c>.</summary>
    /// <param name="method">The method name.</param>
    /// <returns>The call.</returns>
    public RecordedCall? LastCall(string method) => Calls.LastOrDefault(call => call.Method == method);

    /// <summary>Forgets the recorded calls.</summary>
    public void ClearCalls()
    {
        lock (_calls) _calls.Clear();
    }

    internal void Record(string method, object?[] arguments)
    {
        var call = new RecordedCall(method, arguments);
        lock (_calls) _calls.Add(call);
        OnCall?.Invoke(call);
    }

    /// <summary>Called with every request after it was recorded (on the caller's thread), to answer it the moment it is made, as a server would.</summary>
    public Action<RecordedCall>? OnCall { get; set; }

    /// <summary>What the sending methods return while the session is open.</summary>
    public bool SendResult { get; set; } = true;

    /// <summary>What <see cref="ConnectAsync(ServerInfo, CancellationToken)"/> returns.</summary>
    public ConnectResult ConnectResult { get; set; } = ConnectResult.Ok;

    /// <summary>What <see cref="ConnectNcAsync"/> returns.</summary>
    public bool NcConnectResult { get; set; } = true;

    /// <summary>Whether the session has ended.</summary>
    public bool IsClosed => _closed;

    private Task<bool> Send(string method, params object?[] arguments)
    {
        Record(method, arguments);
        return Task.FromResult(SendResult && !_closed);
    }

    // ---- State -----------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public string Account { get; set; }

    /// <inheritdoc />
    public ServerInfo? Server { get; set; } = ServerInfo.Direct("127.0.0.1", 14900);

    /// <inheritdoc />
    public bool IsConnected { get; set; } = true;

    /// <inheritdoc />
    public bool IsAuthenticated { get; set; } = true;

    /// <inheritdoc />
    public bool IsNcConnected { get; set; }

    /// <inheritdoc />
    public bool IsNcAuthenticated { get; set; }

    /// <inheritdoc />
    public bool HasNcServer => !string.IsNullOrEmpty(NcServerAddress);

    /// <inheritdoc />
    public string? NcServerAddress { get; set; }

    /// <inheritdoc />
    public bool UseNewBanProtocol { get; set; } = true;

    /// <inheritdoc />
    public string? LastError { get; set; }

    // ---- Caches ----------------------------------------------------------------------------------------------------------------

    /// <summary>The players (settable list behind <see cref="Players"/>).</summary>
    public List<RcPlayer> PlayerList { get; } = new();

    /// <summary>The channels (settable list behind <see cref="Channels"/>).</summary>
    public List<RcChannel> ChannelList { get; } = new();

    /// <summary>The weapons (settable list behind <see cref="Weapons"/>).</summary>
    public List<RcWeapon> WeaponList { get; } = new();

    /// <summary>The classes (settable list behind <see cref="Classes"/>).</summary>
    public List<RcClass> ClassList { get; } = new();

    /// <summary>The NPCs (settable list behind <see cref="Npcs"/>).</summary>
    public List<RcNpc> NpcList { get; } = new();

    /// <summary>The levels (settable list behind <see cref="Levels"/>).</summary>
    public List<RcLevel> LevelList { get; } = new();

    /// <summary>The PM servers (settable list behind <see cref="PmServers"/>).</summary>
    public List<string> PmServerList { get; } = new();

    /// <summary>The PM guilds (settable list behind <see cref="PmGuilds"/>).</summary>
    public List<string> PmGuildList { get; } = new();

    /// <summary>The NPC flags (settable dictionary behind <see cref="NpcFlags"/>).</summary>
    public Dictionary<int, string> NpcFlagMap { get; } = new();

    /// <summary>The simple server list (settable list behind <see cref="GameServers"/>).</summary>
    public List<SimpleServerEntry> GameServerList { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<RcPlayer> Players => PlayerList;

    /// <inheritdoc />
    public IReadOnlyList<RcChannel> Channels => ChannelList;

    /// <inheritdoc />
    public IReadOnlyList<RcWeapon> Weapons => WeaponList;

    /// <inheritdoc />
    public IReadOnlyList<RcClass> Classes => ClassList;

    /// <inheritdoc />
    public IReadOnlyList<RcNpc> Npcs => NpcList;

    /// <inheritdoc />
    public IReadOnlyList<RcLevel> Levels => LevelList;

    /// <inheritdoc />
    public IReadOnlyList<string> PmServers => PmServerList;

    /// <inheritdoc />
    public IReadOnlyList<string> PmGuilds => PmGuildList;

    /// <inheritdoc />
    public IReadOnlyDictionary<int, string> NpcFlags => NpcFlagMap;

    /// <inheritdoc />
    public string ServerOptions { get; set; } = string.Empty;

    /// <inheritdoc />
    public string ServerFlags { get; set; } = string.Empty;

    /// <inheritdoc />
    public string FolderConfig { get; set; } = string.Empty;

    /// <inheritdoc />
    public long MaxUploadFileSize { get; set; } = 20 * 1024 * 1024;

    /// <inheritdoc />
    public IReadOnlyList<SimpleServerEntry> GameServers => GameServerList;

    /// <summary>The windows' file-browser view, with its settable state and raise methods.</summary>
    public FakeFileBrowser FakeFileBrowser { get; }

    /// <summary>The synchroniser's file-browser view, with its settable state and raise methods.</summary>
    public FakeFileBrowser FakeSyncFileBrowser { get; }

    /// <inheritdoc />
    public IRcFileBrowser FileBrowser => FakeFileBrowser;

    /// <inheritdoc />
    public IRcFileBrowser SyncFileBrowser => FakeSyncFileBrowser;

    /// <summary>The server backup's file-browser view, with its settable state and raise methods.</summary>
    public FakeFileBrowser FakeBackupFileBrowser { get; }

    /// <inheritdoc />
    public IRcFileBrowser BackupFileBrowser => FakeBackupFileBrowser;

    // ---- Events ----------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public event EventHandler? Connected;

    /// <inheritdoc />
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <inheritdoc />
    public event EventHandler? NcConnected;

    /// <inheritdoc />
    public event EventHandler<DisconnectedEventArgs>? NcDisconnected;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? ProtocolError;

    /// <inheritdoc />
    public event EventHandler<RawPacketEventArgs>? RawPacketReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? RcChatReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? NcChatReceived;

    /// <inheritdoc />
    public event EventHandler<PrivateMessageEventArgs>? PrivateMessageReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? AdminMessageReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? ToAllReceived;

    /// <inheritdoc />
    public event EventHandler<IrcMessageEventArgs>? IrcMessageReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? ServerTextReceived;

    /// <inheritdoc />
    public event EventHandler<ServerDataEventArgs>? ServerDataReceived;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<int>>? WorldTimeReceived;

    /// <inheritdoc />
    public event EventHandler<PlayerEventArgs>? PlayerJoined;

    /// <inheritdoc />
    public event EventHandler<PlayerEventArgs>? PlayerUpdated;

    /// <inheritdoc />
    public event EventHandler<PlayerEventArgs>? PlayerLeft;

    /// <inheritdoc />
    public event EventHandler? ChannelsChanged;

    /// <inheritdoc />
    public event EventHandler<PlayerPropertyChangedEventArgs>? PlayerPropertyChanged;

    /// <inheritdoc />
    public event EventHandler<PlayerPropertiesChangedEventArgs>? PlayerPropertiesChanged;

    /// <inheritdoc />
    public event EventHandler<PlayerAttributesEventArgs>? PlayerAttributesReceived;

    /// <inheritdoc />
    public event EventHandler<PlayerRightsEventArgs>? PlayerRightsReceived;

    /// <inheritdoc />
    public event EventHandler<AccountTextEventArgs>? PlayerCommentsReceived;

    /// <inheritdoc />
    public event EventHandler<AccountDataEventArgs>? AccountReceived;

    /// <inheritdoc />
    public event EventHandler<ProfileEventArgs>? ProfileReceived;

    /// <inheritdoc />
    public event EventHandler<AccountListEventArgs>? AccountListReceived;

    /// <inheritdoc />
    public event EventHandler<BanDataEventArgs>? BanDataReceived;

    /// <inheritdoc />
    public event EventHandler<BanListEventArgs>? BanListReceived;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<int>>? PmServersUpdated;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<int>>? PmGuildsUpdated;

    /// <inheritdoc />
    public event EventHandler<PmServerPlayersEventArgs>? PmServerPlayersReceived;

    /// <inheritdoc />
    public event EventHandler? GameServersUpdated;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? ServerOptionsReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? ServerFlagsReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? FolderConfigReceived;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<long>>? MaxUploadFileSizeReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? FileBrowserMessageReceived;

    /// <inheritdoc />
    public event EventHandler<MissingFilesEventArgs>? MissingFilesReceived;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<int>>? WeaponListReceived;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<string>>? ClassAdded;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<string>>? ClassDeleted;

    /// <inheritdoc />
    public event EventHandler<NpcEventArgs>? NpcAdded;

    /// <inheritdoc />
    public event EventHandler<NpcEventArgs>? NpcDeleted;

    /// <inheritdoc />
    public event EventHandler<NpcTextEventArgs>? NpcAttributesReceived;

    /// <inheritdoc />
    public event EventHandler<NpcTextEventArgs>? NpcFlagsReceived;

    /// <inheritdoc />
    public event EventHandler<ScriptReceivedEventArgs>? ScriptReceived;

    /// <inheritdoc />
    public event EventHandler<LocalNpcsEventArgs>? LocalNpcsReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? LevelListReceived;

    // ---- Raising events --------------------------------------------------------------------------------------------------------

    internal void Fire<T>(EventHandler<T>? handler, T args) where T : EventArgs
    {
        if (!_closed) handler?.Invoke(this, args);
    }

    private void Fire(EventHandler? handler)
    {
        if (!_closed) handler?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raises <see cref="Connected"/> and marks the session logged in.</summary>
    public void RaiseConnected()
    {
        IsConnected = true;
        IsAuthenticated = true;
        Fire(Connected);
    }

    /// <summary>Raises <see cref="Disconnected"/> (the last event) and closes the fake.</summary>
    /// <param name="reason">The reason.</param>
    /// <param name="requested">Whether it was asked for.</param>
    public void RaiseDisconnected(string reason = "Connection closed", bool requested = false)
    {
        if (_closed) return;
        LastError = reason;
        IsConnected = IsAuthenticated = IsNcConnected = IsNcAuthenticated = false;
        EventHandler<DisconnectedEventArgs>? handler = Disconnected;
        _closed = true;
        handler?.Invoke(this, new DisconnectedEventArgs(reason, requested));
    }

    /// <summary>Raises <see cref="NcConnected"/> and marks the NC logged in.</summary>
    public void RaiseNcConnected()
    {
        IsNcConnected = IsNcAuthenticated = true;
        Fire(NcConnected);
    }

    /// <summary>Raises <see cref="NcDisconnected"/> and marks the NC closed.</summary>
    /// <param name="reason">The reason.</param>
    /// <param name="requested">Whether it was asked for.</param>
    public void RaiseNcDisconnected(string reason = "Connection closed", bool requested = false)
    {
        IsNcConnected = IsNcAuthenticated = false;
        Fire(NcDisconnected, new DisconnectedEventArgs(reason, requested));
    }

    /// <summary>Raises <see cref="ProtocolError"/>.</summary>
    /// <param name="text">The text.</param>
    public void RaiseProtocolError(string text)
    {
        LastError = text;
        Fire(ProtocolError, new TextEventArgs(text));
    }

    /// <summary>Raises <see cref="RawPacketReceived"/>.</summary>
    public void RaiseRawPacket(RcConnectionKind connection, int packetId, byte[] payload) =>
        Fire(RawPacketReceived, new RawPacketEventArgs(connection, packetId, payload));

    /// <summary>Raises <see cref="RcChatReceived"/>.</summary>
    /// <param name="text">The line.</param>
    public void RaiseRcChat(string text) => Fire(RcChatReceived, new TextEventArgs(text));

    /// <summary>Raises <see cref="NcChatReceived"/>.</summary>
    /// <param name="text">The line.</param>
    public void RaiseNcChat(string text) => Fire(NcChatReceived, new TextEventArgs(text));

    /// <summary>Raises <see cref="PrivateMessageReceived"/>.</summary>
    public void RaisePrivateMessage(int playerId, string account, string nickname, string text, PrivateMessageKind kind = PrivateMessageKind.Normal) =>
        Fire(PrivateMessageReceived, new PrivateMessageEventArgs(playerId, account, nickname, text, kind));

    /// <summary>Raises <see cref="AdminMessageReceived"/>.</summary>
    /// <param name="text">The message.</param>
    public void RaiseAdminMessage(string text) => Fire(AdminMessageReceived, new TextEventArgs(text));

    /// <summary>Raises <see cref="ToAllReceived"/>.</summary>
    /// <param name="text">The message.</param>
    public void RaiseToAll(string text) => Fire(ToAllReceived, new TextEventArgs(text));

    /// <summary>Raises <see cref="IrcMessageReceived"/>.</summary>
    public void RaiseIrcMessage(string channel, string line) => Fire(IrcMessageReceived, new IrcMessageEventArgs(channel, line));

    /// <summary>Raises <see cref="ServerTextReceived"/>.</summary>
    /// <param name="text">The text.</param>
    public void RaiseServerText(string text) => Fire(ServerTextReceived, new TextEventArgs(text));

    /// <summary>Raises <see cref="ServerDataReceived"/>.</summary>
    public void RaiseServerData(string kind, string content) => Fire(ServerDataReceived, new ServerDataEventArgs(kind, content));

    /// <summary>Raises <see cref="WorldTimeReceived"/>.</summary>
    /// <param name="time">The world time.</param>
    public void RaiseWorldTime(int time) => Fire(WorldTimeReceived, new ValueEventArgs<int>(time));

    /// <summary>Adds the player to <see cref="PlayerList"/> and raises <see cref="PlayerJoined"/>.</summary>
    /// <param name="player">The player.</param>
    public void RaisePlayerJoined(RcPlayer player)
    {
        PlayerList.RemoveAll(item => item.Id == player.Id);
        PlayerList.Add(player);
        Fire(PlayerJoined, new PlayerEventArgs(player));
    }

    /// <summary>Replaces the player in <see cref="PlayerList"/> and raises <see cref="PlayerUpdated"/>.</summary>
    /// <param name="player">The player.</param>
    public void RaisePlayerUpdated(RcPlayer player)
    {
        int index = PlayerList.FindIndex(item => item.Id == player.Id);
        if (index >= 0) PlayerList[index] = player;
        else PlayerList.Add(player);
        Fire(PlayerUpdated, new PlayerEventArgs(player));
    }

    /// <summary>Removes the player from <see cref="PlayerList"/> and raises <see cref="PlayerLeft"/>.</summary>
    /// <param name="player">The player.</param>
    public void RaisePlayerLeft(RcPlayer player)
    {
        PlayerList.RemoveAll(item => item.Id == player.Id);
        Fire(PlayerLeft, new PlayerEventArgs(player));
    }

    /// <summary>Replaces <see cref="ChannelList"/> and raises <see cref="ChannelsChanged"/>.</summary>
    /// <param name="channels">The channels.</param>
    public void RaiseChannelsChanged(IEnumerable<RcChannel> channels)
    {
        ChannelList.Clear();
        ChannelList.AddRange(channels);
        Fire(ChannelsChanged);
    }

    /// <summary>Raises <see cref="PlayerPropertyChanged"/>.</summary>
    public void RaisePlayerPropertyChanged(int playerId, int propertyId, string name, string value) =>
        Fire(PlayerPropertyChanged, new PlayerPropertyChangedEventArgs(playerId, propertyId, name, value));

    /// <summary>Raises <see cref="PlayerPropertiesChanged"/>.</summary>
    public void RaisePlayerPropertiesChanged(int playerId, byte[] properties) =>
        Fire(PlayerPropertiesChanged, new PlayerPropertiesChangedEventArgs(playerId, properties));

    /// <summary>Raises <see cref="PlayerAttributesReceived"/> with the editor text formatted from the attributes.</summary>
    /// <param name="attributes">The attributes.</param>
    public void RaisePlayerAttributes(PlayerAttributes attributes) =>
        Fire(PlayerAttributesReceived, new PlayerAttributesEventArgs(attributes, PlayerAttributesText.Format(attributes)));

    /// <summary>Raises <see cref="PlayerRightsReceived"/>.</summary>
    public void RaisePlayerRights(string account, PlayerRights rights) => Fire(PlayerRightsReceived, new PlayerRightsEventArgs(account, rights));

    /// <summary>Raises <see cref="PlayerCommentsReceived"/>.</summary>
    public void RaisePlayerComments(string account, string comments) => Fire(PlayerCommentsReceived, new AccountTextEventArgs(account, comments));

    /// <summary>Raises <see cref="AccountReceived"/>.</summary>
    /// <param name="account">The account.</param>
    public void RaiseAccount(AccountData account) => Fire(AccountReceived, new AccountDataEventArgs(account));

    /// <summary>Raises <see cref="ProfileReceived"/>.</summary>
    public void RaiseProfile(string account, IReadOnlyList<string> fields) => Fire(ProfileReceived, new ProfileEventArgs(account, fields));

    /// <summary>Raises <see cref="AccountListReceived"/>.</summary>
    /// <param name="accounts">The accounts.</param>
    public void RaiseAccountList(IReadOnlyList<string> accounts) => Fire(AccountListReceived, new AccountListEventArgs(accounts));

    /// <summary>Raises <see cref="BanDataReceived"/>.</summary>
    public void RaiseBanData(string account, string computerId, string details) => Fire(BanDataReceived, new BanDataEventArgs(account, computerId, details));

    /// <summary>Raises <see cref="BanListReceived"/>.</summary>
    public void RaiseBanList(string kind, string account, string content) => Fire(BanListReceived, new BanListEventArgs(kind, account, content));

    /// <summary>Replaces <see cref="PmServerList"/> and raises <see cref="PmServersUpdated"/>.</summary>
    /// <param name="servers">The servers.</param>
    public void RaisePmServersUpdated(IEnumerable<string> servers)
    {
        PmServerList.Clear();
        PmServerList.AddRange(servers);
        Fire(PmServersUpdated, new ValueEventArgs<int>(PmServerList.Count));
    }

    /// <summary>Replaces <see cref="PmGuildList"/> and raises <see cref="PmGuildsUpdated"/>.</summary>
    /// <param name="guilds">The guilds.</param>
    public void RaisePmGuildsUpdated(IEnumerable<string> guilds)
    {
        PmGuildList.Clear();
        PmGuildList.AddRange(guilds);
        Fire(PmGuildsUpdated, new ValueEventArgs<int>(PmGuildList.Count));
    }

    /// <summary>Raises <see cref="PmServerPlayersReceived"/>.</summary>
    public void RaisePmServerPlayers(string server, string players) => Fire(PmServerPlayersReceived, new PmServerPlayersEventArgs(server, players));

    /// <summary>Replaces <see cref="GameServerList"/> and raises <see cref="GameServersUpdated"/>.</summary>
    /// <param name="servers">The servers.</param>
    public void RaiseGameServersUpdated(IEnumerable<SimpleServerEntry> servers)
    {
        GameServerList.Clear();
        GameServerList.AddRange(servers);
        Fire(GameServersUpdated);
    }

    /// <summary>Sets <see cref="ServerOptions"/> and raises <see cref="ServerOptionsReceived"/>.</summary>
    /// <param name="text">The options.</param>
    public void RaiseServerOptions(string text)
    {
        ServerOptions = text;
        Fire(ServerOptionsReceived, new TextEventArgs(text));
    }

    /// <summary>Sets <see cref="ServerFlags"/> and raises <see cref="ServerFlagsReceived"/>.</summary>
    /// <param name="text">The flags.</param>
    public void RaiseServerFlags(string text)
    {
        ServerFlags = text;
        Fire(ServerFlagsReceived, new TextEventArgs(text));
    }

    /// <summary>Sets <see cref="FolderConfig"/> and raises <see cref="FolderConfigReceived"/>.</summary>
    /// <param name="text">The configuration.</param>
    public void RaiseFolderConfig(string text)
    {
        FolderConfig = text;
        Fire(FolderConfigReceived, new TextEventArgs(text));
    }

    /// <summary>Sets <see cref="MaxUploadFileSize"/> and raises <see cref="MaxUploadFileSizeReceived"/>.</summary>
    /// <param name="size">The limit.</param>
    public void RaiseMaxUploadFileSize(long size)
    {
        MaxUploadFileSize = size;
        Fire(MaxUploadFileSizeReceived, new ValueEventArgs<long>(size));
    }

    /// <summary>Raises <see cref="MissingFilesReceived"/> with a list, as the session does after the last part.</summary>
    /// <param name="files">The files.</param>
    public void RaiseMissingFiles(IEnumerable<MissingFile> files) => Fire(MissingFilesReceived, new MissingFilesEventArgs(files.ToArray()));

    /// <summary>Raises <see cref="MissingFilesReceived"/> with the server's refusal (<c>missingfiles,error,&lt;sentence&gt;</c>).</summary>
    /// <param name="error">The sentence.</param>
    public void RaiseMissingFilesError(string error) => Fire(MissingFilesReceived, new MissingFilesEventArgs(error));

    /// <summary>Raises <see cref="FileBrowserMessageReceived"/>.</summary>
    /// <param name="text">The message.</param>
    public void RaiseFileBrowserMessage(string text) => Fire(FileBrowserMessageReceived, new TextEventArgs(text));

    /// <summary>Replaces <see cref="WeaponList"/> and raises <see cref="WeaponListReceived"/>.</summary>
    /// <param name="weapons">The weapons.</param>
    public void RaiseWeaponList(IEnumerable<RcWeapon> weapons)
    {
        WeaponList.Clear();
        WeaponList.AddRange(weapons);
        Fire(WeaponListReceived, new ValueEventArgs<int>(WeaponList.Count));
    }

    /// <summary>Adds the class to <see cref="ClassList"/> and raises <see cref="ClassAdded"/>.</summary>
    /// <param name="name">The class.</param>
    public void RaiseClassAdded(string name)
    {
        if (!ClassList.Any(item => item.Name == name)) ClassList.Add(new RcClass(name, string.Empty));
        Fire(ClassAdded, new ValueEventArgs<string>(name));
    }

    /// <summary>Removes the class from <see cref="ClassList"/> and raises <see cref="ClassDeleted"/>.</summary>
    /// <param name="name">The class.</param>
    public void RaiseClassDeleted(string name)
    {
        ClassList.RemoveAll(item => item.Name == name);
        Fire(ClassDeleted, new ValueEventArgs<string>(name));
    }

    /// <summary>Adds the NPC to <see cref="NpcList"/> and raises <see cref="NpcAdded"/>.</summary>
    /// <param name="npc">The NPC.</param>
    public void RaiseNpcAdded(RcNpc npc)
    {
        NpcList.RemoveAll(item => item.Id == npc.Id);
        NpcList.Add(npc);
        Fire(NpcAdded, new NpcEventArgs(npc.Id, npc.Name));
    }

    /// <summary>Removes the NPC from <see cref="NpcList"/> and raises <see cref="NpcDeleted"/>.</summary>
    /// <param name="id">The NPC id.</param>
    public void RaiseNpcDeleted(int id)
    {
        NpcList.RemoveAll(item => item.Id == id);
        Fire(NpcDeleted, new NpcEventArgs(id, string.Empty));
    }

    /// <summary>Raises <see cref="NpcAttributesReceived"/>.</summary>
    public void RaiseNpcAttributes(int npcId, string text) => Fire(NpcAttributesReceived, new NpcTextEventArgs(npcId, text));

    /// <summary>Sets the NPC's entry of <see cref="NpcFlagMap"/> and raises <see cref="NpcFlagsReceived"/>.</summary>
    public void RaiseNpcFlags(int npcId, string flags)
    {
        NpcFlagMap[npcId] = flags;
        Fire(NpcFlagsReceived, new NpcTextEventArgs(npcId, flags));
    }

    /// <summary>Raises <see cref="ScriptReceived"/> (and stores a weapon's or class's script in its cache entry).</summary>
    public void RaiseScript(ScriptKind kind, string name, string script, int id = 0, string image = "")
    {
        if (kind == ScriptKind.Weapon)
        {
            WeaponList.RemoveAll(item => item.Name == name);
            WeaponList.Add(new RcWeapon(name, image, script));
        }
        else if (kind == ScriptKind.Class)
        {
            ClassList.RemoveAll(item => item.Name == name);
            ClassList.Add(new RcClass(name, script));
        }

        Fire(ScriptReceived, new ScriptReceivedEventArgs(kind, name, id, script, image));
    }

    /// <summary>Raises <see cref="LocalNpcsReceived"/>.</summary>
    public void RaiseLocalNpcs(string level, string text) => Fire(LocalNpcsReceived, new LocalNpcsEventArgs(level, text));

    /// <summary>Replaces <see cref="LevelList"/> with the lines of the text and raises <see cref="LevelListReceived"/>.</summary>
    /// <param name="text">The level list, one per line.</param>
    public void RaiseLevelList(string text)
    {
        LevelList.Clear();
        LevelList.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => new RcLevel(line)));
        Fire(LevelListReceived, new TextEventArgs(text));
    }

    // ---- Connecting ------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<ConnectResult> ConnectAsync(ServerInfo server, CancellationToken cancellationToken = default)
    {
        Record(nameof(ConnectAsync), new object?[] { server });
        Server = server;
        return Task.FromResult(_closed ? ConnectResult.Failed("The session is closed.") : ConnectResult);
    }

    /// <inheritdoc />
    public Task<ConnectResult> ConnectAsync(string host, int port, CancellationToken cancellationToken = default) =>
        ConnectAsync(ServerInfo.Direct(host, port), cancellationToken);

    /// <inheritdoc />
    public Task<bool> ConnectNcAsync(CancellationToken cancellationToken = default)
    {
        Record(nameof(ConnectNcAsync), Array.Empty<object?>());
        bool result = NcConnectResult && !_closed;
        if (result) IsNcConnected = IsNcAuthenticated = true;
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public bool DisconnectNc()
    {
        Record(nameof(DisconnectNc), Array.Empty<object?>());
        bool open = IsNcConnected;
        IsNcConnected = IsNcAuthenticated = false;
        return open && !_closed;
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        Record(nameof(Disconnect), Array.Empty<object?>());
        RaiseDisconnected("Disconnected", requested: true);
    }

    /// <summary>Closes the fake without raising anything.</summary>
    public void Dispose()
    {
        Record(nameof(Dispose), Array.Empty<object?>());
        _closed = true;
    }

    /// <inheritdoc />
    public Task<byte[]?> DownloadLevelAsync(string levelPath, CancellationToken cancellationToken = default)
    {
        Record(nameof(DownloadLevelAsync), new object?[] { levelPath });
        return FakeFileBrowser.DownloadAsync(levelPath, cancellationToken);
    }

    // ---- Sending (generated from IRcSession: each records its arguments and returns SendResult) -------------------------------

    /// <inheritdoc />
    public Task<bool> SendRcChatAsync(string text, bool alsoOnNc = false) => Send(nameof(SendRcChatAsync), text, alsoOnNc);

    /// <inheritdoc />
    public Task<bool> SendNcChatAsync(string text) => Send(nameof(SendNcChatAsync), text);

    /// <inheritdoc />
    public Task<bool> SendToAllAsync(string message) => Send(nameof(SendToAllAsync), message);

    /// <inheritdoc />
    public Task<bool> SendPrivateMessageAsync(int playerId, string message) => Send(nameof(SendPrivateMessageAsync), playerId, message);

    /// <inheritdoc />
    public Task<bool> SendMassPrivateMessageAsync(IReadOnlyList<int> playerIds, string message) => Send(nameof(SendMassPrivateMessageAsync), playerIds, message);

    /// <inheritdoc />
    public Task<bool> SendAdminMessageAsync(int playerId, string message) => Send(nameof(SendAdminMessageAsync), playerId, message);

    /// <inheritdoc />
    public Task<bool> SendAdminMessageToAllAsync(string message) => Send(nameof(SendAdminMessageToAllAsync), message);

    /// <inheritdoc />
    public Task<bool> SetNicknameAsync(string nickname) => Send(nameof(SetNicknameAsync), nickname);

    /// <inheritdoc />
    public Task<bool> WarpPlayerAsync(int playerId, string level, double x, double y) => Send(nameof(WarpPlayerAsync), playerId, level, x, y);

    /// <inheritdoc />
    public Task<bool> DisconnectPlayerAsync(int playerId, string reason) => Send(nameof(DisconnectPlayerAsync), playerId, reason);

    /// <inheritdoc />
    public Task<bool> RequestPlayerRightsAsync(string account) => Send(nameof(RequestPlayerRightsAsync), account);

    /// <inheritdoc />
    public Task<bool> SetPlayerRightsAsync(string account, PlayerRights rights) => Send(nameof(SetPlayerRightsAsync), account, rights);

    /// <inheritdoc />
    public Task<bool> RequestPlayerAttributesAsync(string account) => Send(nameof(RequestPlayerAttributesAsync), account);

    /// <inheritdoc />
    public Task<bool> RequestPlayerAttributesAsync(int playerId) => Send(nameof(RequestPlayerAttributesAsync), playerId);

    /// <inheritdoc />
    public Task<bool> SetPlayerAttributesAsync(string account, PlayerAttributes attributes) => Send(nameof(SetPlayerAttributesAsync), account, attributes);

    /// <inheritdoc />
    public Task<bool> ResetPlayerAsync(string account) => Send(nameof(ResetPlayerAsync), account);

    /// <inheritdoc />
    public Task<bool> RequestAccountAsync(string account) => Send(nameof(RequestAccountAsync), account);

    /// <inheritdoc />
    public Task<bool> SetAccountAsync(string account, AccountData data) => Send(nameof(SetAccountAsync), account, data);

    /// <inheritdoc />
    public Task<bool> AddAccountAsync(AccountData data) => Send(nameof(AddAccountAsync), data);

    /// <inheritdoc />
    public Task<bool> RequestAccountListAsync(string accountFilter, string conditions) => Send(nameof(RequestAccountListAsync), accountFilter, conditions);

    /// <inheritdoc />
    public Task<bool> RequestPlayerCommentsAsync(string account) => Send(nameof(RequestPlayerCommentsAsync), account);

    /// <inheritdoc />
    public Task<bool> SetPlayerCommentsAsync(string account, string comments) => Send(nameof(SetPlayerCommentsAsync), account, comments);

    /// <inheritdoc />
    public Task<bool> RequestProfileAsync(string account) => Send(nameof(RequestProfileAsync), account);

    /// <inheritdoc />
    public Task<bool> SetProfileAsync(string account, IReadOnlyList<string> fields) => Send(nameof(SetProfileAsync), account, fields);

    /// <inheritdoc />
    public Task<bool> RequestPlayerBanAsync(string account, int playerId = -1) => Send(nameof(RequestPlayerBanAsync), account, playerId);

    /// <inheritdoc />
    public Task<bool> RequestNewPlayerBanAsync(string account, int playerId = -1) => Send(nameof(RequestNewPlayerBanAsync), account, playerId);

    /// <inheritdoc />
    public Task<bool> RequestLegacyPlayerBanAsync(string account) => Send(nameof(RequestLegacyPlayerBanAsync), account);

    /// <inheritdoc />
    public Task<bool> RequestPlayerBanByAccountAsync(string account) => Send(nameof(RequestPlayerBanByAccountAsync), account);

    /// <inheritdoc />
    public Task<bool> RequestBanTypesAsync() => Send(nameof(RequestBanTypesAsync));

    /// <inheritdoc />
    public Task<bool> RequestMissingFilesAsync() => Send(nameof(RequestMissingFilesAsync));

    /// <inheritdoc />
    public Task<bool> RequestBanHistoryAsync(string account) => Send(nameof(RequestBanHistoryAsync), account);

    /// <inheritdoc />
    public Task<bool> RequestStaffActivityAsync(string account) => Send(nameof(RequestStaffActivityAsync), account);

    /// <inheritdoc />
    public Task<bool> SetBanAsync(string target, string world, bool banned, string banType, string releaseTime, string reason) => Send(nameof(SetBanAsync), target, world, banned, banType, releaseTime, reason);

    /// <inheritdoc />
    public Task<bool> SetLegacyPlayerBanAsync(string account, bool banned, string reason) => Send(nameof(SetLegacyPlayerBanAsync), account, banned, reason);

    /// <inheritdoc />
    public Task<bool> RequestServerOptionsAsync() => Send(nameof(RequestServerOptionsAsync));

    /// <inheritdoc />
    public Task<bool> UploadServerOptionsAsync(string content) => Send(nameof(UploadServerOptionsAsync), content);

    /// <inheritdoc />
    public Task<bool> RequestServerFlagsAsync() => Send(nameof(RequestServerFlagsAsync));

    /// <inheritdoc />
    public Task<bool> UploadServerFlagsAsync(string content) => Send(nameof(UploadServerFlagsAsync), content);

    /// <inheritdoc />
    public Task<bool> RequestFolderConfigAsync() => Send(nameof(RequestFolderConfigAsync));

    /// <inheritdoc />
    public Task<bool> UploadFolderConfigAsync(string content) => Send(nameof(UploadFolderConfigAsync), content);

    /// <inheritdoc />
    public Task<bool> UpdateLevelAsync(string level) => Send(nameof(UpdateLevelAsync), level);

    /// <inheritdoc />
    public Task<bool> UpdateLevelsAsync(IReadOnlyList<string> levels) => Send(nameof(UpdateLevelsAsync), levels);

    /// <inheritdoc />
    public Task<bool> UploadLevelAsync(string levelPath, ReadOnlyMemory<byte> content) => Send(nameof(UploadLevelAsync), levelPath, content.ToArray());

    /// <inheritdoc />
    public Task<bool> RequestServerListAsync() => Send(nameof(RequestServerListAsync));

    /// <inheritdoc />
    public Task<bool> RequestPmServerListAsync() => Send(nameof(RequestPmServerListAsync));

    /// <inheritdoc />
    public Task<bool> RequestPmGuildListAsync() => Send(nameof(RequestPmGuildListAsync));

    /// <inheritdoc />
    public Task<bool> RequestPmServerPlayersAsync(string server) => Send(nameof(RequestPmServerPlayersAsync), server);

    /// <inheritdoc />
    public Task<bool> UnmapPmServerAsync(string server) => Send(nameof(UnmapPmServerAsync), server);

    /// <inheritdoc />
    public Task<bool> SendIrcTextAsync(string command, params string[] parameters) => Send(nameof(SendIrcTextAsync), command, parameters);

    /// <inheritdoc />
    public Task<bool> IrcLoginAsync() => Send(nameof(IrcLoginAsync));

    /// <inheritdoc />
    public Task<bool> IrcJoinAsync(string channel) => Send(nameof(IrcJoinAsync), channel);

    /// <inheritdoc />
    public Task<bool> IrcPartAsync(string channel) => Send(nameof(IrcPartAsync), channel);

    /// <inheritdoc />
    public Task<bool> RequestWeaponListAsync() => Send(nameof(RequestWeaponListAsync));

    /// <inheritdoc />
    public Task<bool> RequestWeaponScriptAsync(string name) => Send(nameof(RequestWeaponScriptAsync), name);

    /// <inheritdoc />
    public Task<bool> AddWeaponAsync(string name, string image, string script) => Send(nameof(AddWeaponAsync), name, image, script);

    /// <inheritdoc />
    public Task<bool> UpdateWeaponAsync(string name, string image, string script) => Send(nameof(UpdateWeaponAsync), name, image, script);

    /// <inheritdoc />
    public Task<bool> DeleteWeaponAsync(string name) => Send(nameof(DeleteWeaponAsync), name);

    /// <inheritdoc />
    public Task<bool> RequestClassScriptAsync(string name) => Send(nameof(RequestClassScriptAsync), name);

    /// <inheritdoc />
    public Task<bool> AddClassAsync(string name, string script) => Send(nameof(AddClassAsync), name, script);

    /// <inheritdoc />
    public Task<bool> UpdateClassAsync(string name, string script) => Send(nameof(UpdateClassAsync), name, script);

    /// <inheritdoc />
    public Task<bool> DeleteClassAsync(string name) => Send(nameof(DeleteClassAsync), name);

    /// <inheritdoc />
    public Task<bool> RequestNpcScriptAsync(int npcId) => Send(nameof(RequestNpcScriptAsync), npcId);

    /// <inheritdoc />
    public Task<bool> UpdateNpcScriptAsync(int npcId, string script) => Send(nameof(UpdateNpcScriptAsync), npcId, script);

    /// <inheritdoc />
    public Task<bool> RequestNpcAttributesAsync(int npcId) => Send(nameof(RequestNpcAttributesAsync), npcId);

    /// <inheritdoc />
    public Task<bool> RequestNpcFlagsAsync(int npcId) => Send(nameof(RequestNpcFlagsAsync), npcId);

    /// <inheritdoc />
    public Task<bool> SetNpcFlagsAsync(int npcId, string flags) => Send(nameof(SetNpcFlagsAsync), npcId, flags);

    /// <inheritdoc />
    public Task<bool> ResetNpcAsync(int npcId) => Send(nameof(ResetNpcAsync), npcId);

    /// <inheritdoc />
    public Task<bool> WarpNpcAsync(int npcId, double x, double y, string level) => Send(nameof(WarpNpcAsync), npcId, x, y, level);

    /// <inheritdoc />
    public Task<bool> DeleteNpcAsync(int npcId) => Send(nameof(DeleteNpcAsync), npcId);

    /// <inheritdoc />
    public Task<bool> CreateNpcAsync(string name, int npcId, string type, string scripter, string level, string x, string y) => Send(nameof(CreateNpcAsync), name, npcId, type, scripter, level, x, y);

    /// <inheritdoc />
    public Task<bool> RequestLocalNpcsAsync(string level) => Send(nameof(RequestLocalNpcsAsync), level);

    /// <inheritdoc />
    public Task<bool> RequestLevelListAsync() => Send(nameof(RequestLevelListAsync));

    /// <inheritdoc />
    public Task<bool> SendRawPacketAsync(int packetId, ReadOnlyMemory<byte> payload) => Send(nameof(SendRawPacketAsync), packetId, payload.ToArray());

    /// <inheritdoc />
    public Task<bool> SendNcPacketAsync(int packetId, ReadOnlyMemory<byte> payload) => Send(nameof(SendNcPacketAsync), packetId, payload.ToArray());
}
