using System.Globalization;
using RemoteControl.Protocol.Text;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Session;

public sealed partial class RcSession
{
    private readonly List<RcPlayer> _players = new();
    private readonly List<RcPlayer> _channelPlayers = new();
    private List<RcChannel> _channels = new();
    private List<string> _pmServers = new();
    private List<string> _pmGuilds = new();
    private List<SimpleServerEntry> _gameServers = new();
    private readonly Queue<string> _pendingBanAccounts = new();
    private bool _clientRcRequested;
    private string _serverOptions = string.Empty;
    private string _serverFlags = string.Empty;
    private string _folderConfig = string.Empty;
    private long _maxUploadFileSize;

    /// <inheritdoc />
    public IReadOnlyList<RcPlayer> Players => _players.AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<RcChannel> Channels => _channels.AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<string> PmServers => _pmServers.AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<string> PmGuilds => _pmGuilds.AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<SimpleServerEntry> GameServers => _gameServers.AsReadOnly();

    /// <inheritdoc />
    public string ServerOptions => _serverOptions;

    /// <inheritdoc />
    public string ServerFlags => _serverFlags;

    /// <inheritdoc />
    public string FolderConfig => _folderConfig;

    /// <inheritdoc />
    public long MaxUploadFileSize => Interlocked.Read(ref _maxUploadFileSize);

    private static int PacketId(byte first) => (first - 32) & 0xFF;

    /// <summary>One packet of the RC connection; runs on the context. Nothing in here may escape: a bad packet is dropped and logged.</summary>
    private void HandleRcPacket(byte[] packet)
    {
        if (_closed || packet.Length == 0) return;
        int id = PacketId(packet[0]);
        byte[] payload = packet.AsSpan(1).ToArray();
        Raise(RawPacketReceived, new RawPacketEventArgs(RcConnectionKind.Rc, id, payload));
        try
        {
            DispatchRc((ServerPacketId)id, payload);
        }
        catch (Exception exception)
        {
            string message = $"Packet {id} ({payload.Length} bytes) could not be read and was dropped: {exception.Message}";
            Log(message);
            Raise(ProtocolError, new TextEventArgs(message));
        }
    }

    private void DispatchRc(ServerPacketId id, byte[] payload)
    {
        var reader = new PacketReader(payload);
        switch (id)
        {
            case ServerPacketId.Signature:
                if (!_authenticated)
                {
                    _authenticated = true;
                    Raise(Connected);
                    _login?.TrySetResult(ConnectResult.Ok);
                }

                break;
            case ServerPacketId.ServerListConnected:
                SendListerTextNow("bantypes", null, ClientPacketId.RequestText);
                SendListerTextNow("pmguilds", null, ClientPacketId.RequestText);
                SendListerTextNow("pmservers", null, ClientPacketId.RequestText);
                break;
            case ServerPacketId.DisconnectMessage:
            {
                string text = reader.ReadRemainingText();
                EndSession(text.Length > 0 ? text : "Disconnected by the server", requested: false);
                break;
            }
            case ServerPacketId.FileSendFailed:
            {
                string text = reader.ReadRemainingText();
                Raise(FileBrowserMessageReceived, new TextEventArgs(text));
                FileSendFailed(text);
                break;
            }
            case ServerPacketId.FileUpToDate:
                FileUpToDate(FirstLine(reader.ReadRemainingText()));
                break;
            case ServerPacketId.BombAdd:
                ServerData("bombadd", reader);
                break;
            case ServerPacketId.AdminMessage:
                Raise(AdminMessageReceived, new TextEventArgs(reader.ReadRemainingText().Replace('§', '\n')));
                break;
            case ServerPacketId.StaffGuilds:
                ServerData("staffguilds", reader);
                break;
            case ServerPacketId.AccountAdd or ServerPacketId.AccountStatus or ServerPacketId.AccountName or ServerPacketId.AccountDelete
                or ServerPacketId.AccountProps or ServerPacketId.AccountPropsGet or ServerPacketId.AccountChange or ServerPacketId.Unknown60
                or ServerPacketId.RcPlayerProps:
                ServerData(PacketNames.NameOf(false, PacketDirection.Incoming, (int)id) ?? id.ToString(), reader);
                break;
            case ServerPacketId.PlayerPropsChange:
                if (reader.TryReadGShort(out int changedId))
                    Raise(PlayerPropertiesChanged, new PlayerPropertiesChangedEventArgs(changedId, reader.ReadRemainingBytes()));
                break;
            case ServerPacketId.AccountList:
                HandleAccountList(reader);
                break;
            case ServerPacketId.NpcServerAddress:
                if (reader.TryReadGShort(out _)) _ncServerAddress = reader.ReadRemainingText();
                break;
            case ServerPacketId.RcChat:
                Raise(RcChatReceived, new TextEventArgs(FixOfPlus(reader.ReadRemainingText())));
                break;
            case ServerPacketId.Profile:
                HandleProfile(payload);
                break;
            case ServerPacketId.PlayerBan:
                HandleLegacyBan(reader);
                break;
            case ServerPacketId.OtherPlayerProps:
                HandleOtherPlayerProps(payload);
                break;
            case ServerPacketId.ToAll:
                HandleToAll(reader);
                break;
            case ServerPacketId.PlayerAttributes:
                HandlePlayerAttributes(payload);
                break;
            case ServerPacketId.Account:
                HandleAccount(reader);
                break;
            case ServerPacketId.ServerOptions:
                _serverOptions = reader.ReadRemainingCommaText();
                Raise(ServerOptionsReceived, new TextEventArgs(_serverOptions));
                break;
            case ServerPacketId.FolderConfig:
                _folderConfig = reader.ReadRemainingCommaText();
                Raise(FolderConfigReceived, new TextEventArgs(_folderConfig));
                break;
            case ServerPacketId.AddPlayer:
                HandleAddPlayer(reader);
                break;
            case ServerPacketId.DeletePlayer:
                if (reader.TryReadGShort(out int leftId)) RemovePlayer(leftId);
                break;
            case ServerPacketId.ServerFlags:
                HandleServerFlags(reader);
                break;
            case ServerPacketId.PlayerRights:
                HandlePlayerRights(reader);
                break;
            case ServerPacketId.PlayerComments:
            {
                string account = reader.ReadGStringOrEmpty();
                Raise(PlayerCommentsReceived, new AccountTextEventArgs(account, reader.ReadRemainingCommaText()));
                break;
            }
            case ServerPacketId.FileBrowserDirList:
                HandleDirList(reader.ReadRemainingCommaText());
                break;
            case ServerPacketId.FileBrowserDir:
                HandleDirectory(payload);
                break;
            case ServerPacketId.FileBrowserMessage:
                Raise(FileBrowserMessageReceived, new TextEventArgs(reader.ReadRemainingCommaText()));
                break;
            case ServerPacketId.LargeFileStart:
                LargeFileStart(FirstLine(reader.ReadRemainingText()));
                break;
            case ServerPacketId.LargeFileEnd:
                LargeFileEnd(FirstLine(reader.ReadRemainingText()));
                break;
            case ServerPacketId.LargeFileSize:
                if (reader.TryReadGInt5(out long size)) LargeFileSize(size);
                break;
            case ServerPacketId.File:
                HandleFile(reader);
                break;
            case ServerPacketId.PrivateMessage:
                HandlePrivateMessage(reader);
                break;
            case ServerPacketId.NewWorldTime:
                if (reader.TryReadGInt4(out int time)) Raise(WorldTimeReceived, new ValueEventArgs<int>(time));
                break;
            case ServerPacketId.NcLevelList:
                HandleLevelList(payload);
                break;
            case ServerPacketId.ServerText:
                HandleServerText(reader.ReadRemainingText());
                break;
            case ServerPacketId.MaxUploadFileSize:
                if (reader.TryReadGInt5(out long maxSize))
                {
                    Interlocked.Exchange(ref _maxUploadFileSize, maxSize);
                    Raise(MaxUploadFileSizeReceived, new ValueEventArgs<long>(maxSize));
                }

                break;
            case ServerPacketId.BoardPacket:
                ServerData("boardpacket", reader);
                break;
            case ServerPacketId.StatusList:
                ServerData("statuslist", reader);
                break;
            case ServerPacketId.ClearWeapons:
                _weapons.Clear();
                ServerData("clearweapons", reader);
                break;
        }
    }

    private void ServerData(string kind, PacketReader reader) => Raise(ServerDataReceived, new ServerDataEventArgs(kind, reader.ReadRemainingText()));

    private static string FixOfPlus(string text) => text.Replace(" of +", " of ", StringComparison.Ordinal);

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? text : text[..newline];
    }

    // ---- Players -----------------------------------------------------------------------------------------------------------

    private RcPlayer? FindPlayer(int id) =>
        _players.FirstOrDefault(player => player.Id == id) ?? _channelPlayers.FirstOrDefault(player => player.Id == id);

    private void HandleAddPlayer(PacketReader reader)
    {
        if (!reader.TryReadGShort(out int id) || !reader.TryReadGString(out string account)) return;
        List<(int Id, string Value)> properties = PlayerPropertyStream.ReadAll(reader);
        if (account.Contains("(npcserver)", StringComparison.Ordinal))
            SendRcNow(ClientPacketId.NpcServerQuery, writer => writer.WriteGShort(id).WriteText("location"));
        RcPlayer? existing = FindPlayer(id);
        var values = new Dictionary<int, string>();
        foreach ((int propertyId, string value) in properties) values[propertyId] = value;
        if (values.TryGetValue((int)PlayerPropertyId.AccountName, out string? fromProperties) && fromProperties.Length > 0) account = fromProperties;
        RcPlayer player = BuildPlayer(id, account, values, existing: null);
        StorePlayer(player, existing);
        foreach ((int propertyId, string value) in properties)
            Raise(PlayerPropertyChanged, new PlayerPropertyChangedEventArgs(id, propertyId, PlayerAttributes.PropertyName(propertyId), value));
    }

    private void HandleOtherPlayerProps(byte[] payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryReadGShort(out int id)) return;
        int start = reader.Position;
        List<(int Id, string Value)> properties = PlayerPropertyStream.ReadAll(reader);
        RcPlayer? existing = FindPlayer(id);
        var values = existing is null ? new Dictionary<int, string>() : new Dictionary<int, string>(existing.Properties);
        foreach ((int propertyId, string value) in properties) values[propertyId] = value;
        string account = values.TryGetValue((int)PlayerPropertyId.AccountName, out string? name) ? name : existing?.Account ?? string.Empty;
        StorePlayer(BuildPlayer(id, account, values, existing), existing);
        foreach ((int propertyId, string value) in properties)
            Raise(PlayerPropertyChanged, new PlayerPropertyChangedEventArgs(id, propertyId, PlayerAttributes.PropertyName(propertyId), value));
        if (start < payload.Length)
            Raise(PlayerPropertiesChanged, new PlayerPropertiesChangedEventArgs(id, payload.AsSpan(start).ToArray()));
    }

    private static RcPlayer BuildPlayer(int id, string account, Dictionary<int, string> values, RcPlayer? existing)
    {
        string Text(PlayerPropertyId property, string fallback) => values.TryGetValue((int)property, out string? value) ? value : fallback;
        int Number(PlayerPropertyId property, int fallback) =>
            values.TryGetValue((int)property, out string? value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : fallback;
        return new RcPlayer(id, account, Text(PlayerPropertyId.Nickname, existing?.Nickname ?? string.Empty), Text(PlayerPropertyId.CurrentLevel, existing?.Level ?? string.Empty),
            (PlayerListCategory)Number(PlayerPropertyId.PlayerListCategory, (int)(existing?.Category ?? PlayerListCategory.None)),
            Number(PlayerPropertyId.StatusMessage, existing?.StatusIcon ?? -1), Text(PlayerPropertyId.CommunityName, existing?.Community ?? string.Empty),
            new Dictionary<int, string>(values));
    }

    private void StorePlayer(RcPlayer player, RcPlayer? existing)
    {
        bool wasChannel = existing?.IsChannel ?? false;
        int playerIndex = _players.FindIndex(item => item.Id == player.Id);
        int channelIndex = _channelPlayers.FindIndex(item => item.Id == player.Id);
        if (player.IsChannel)
        {
            if (playerIndex >= 0) _players.RemoveAt(playerIndex);
            if (channelIndex >= 0) _channelPlayers[channelIndex] = player;
            else _channelPlayers.Add(player);
            RebuildChannels();
            if (existing is not null && !wasChannel) Raise(PlayerLeft, new PlayerEventArgs(existing));
            Raise(ChannelsChanged);
            return;
        }

        if (channelIndex >= 0)
        {
            _channelPlayers.RemoveAt(channelIndex);
            RebuildChannels();
            Raise(ChannelsChanged);
        }

        if (playerIndex >= 0)
        {
            _players[playerIndex] = player;
            Raise(PlayerUpdated, new PlayerEventArgs(player));
        }
        else
        {
            _players.Add(player);
            Raise(PlayerJoined, new PlayerEventArgs(player));
        }
    }

    private void RemovePlayer(int id)
    {
        RcPlayer? player = FindPlayer(id);
        if (player is null) return;
        if (_channelPlayers.RemoveAll(item => item.Id == id) > 0)
        {
            RebuildChannels();
            Raise(ChannelsChanged);
            return;
        }

        _players.RemoveAll(item => item.Id == id);
        Raise(PlayerLeft, new PlayerEventArgs(player));
    }

    private void RebuildChannels() => _channels = _channelPlayers.Select(RcChannel.FromPlayer).ToList();

    private void HandleToAll(PacketReader reader)
    {
        if (!reader.TryReadGShort(out int id) || !reader.TryReadGString(out string message)) return;
        RcPlayer? sender = FindPlayer(id);
        string name = sender is null ? string.Empty : sender.DisplayName;
        Raise(ToAllReceived, new TextEventArgs(name.Length > 0 ? $"{name}: {message}" : message));
    }

    private void HandlePrivateMessage(PacketReader reader)
    {
        if (!reader.TryReadGShort(out int id)) return;
        string message = reader.ReadRemainingText();
        // Classic servers send "<kind>",<comma text>; Alpaxis sends "","<kind>",<comma text>.
        IReadOnlyList<string> fields = CommaText.Split(message);
        int skip = 0;
        PrivateMessageKind kind = PrivateMessageKind.Normal;
        for (int index = 0; index < Math.Min(2, fields.Count); index++)
        {
            string field = fields[index];
            if (index == 0 && field.Length == 0 && fields.Count > 1)
            {
                skip = 1;
                continue;
            }

            if (field.EndsWith("message:", StringComparison.OrdinalIgnoreCase))
            {
                kind = field.StartsWith("Mass", StringComparison.OrdinalIgnoreCase) ? PrivateMessageKind.Mass
                    : field.StartsWith("Guild", StringComparison.OrdinalIgnoreCase) ? PrivateMessageKind.Guild
                    : field.StartsWith("Admin", StringComparison.OrdinalIgnoreCase) ? PrivateMessageKind.Admin
                    : PrivateMessageKind.Normal;
                skip = index + 1;
            }

            break;
        }

        if (fields.Count == 0 && message.Length == 0) return;
        string text = string.Join('\n', fields.Skip(skip));
        RcPlayer? sender = FindPlayer(id);
        Raise(PrivateMessageReceived, new PrivateMessageEventArgs(id, sender?.Account ?? string.Empty, sender?.Nickname ?? string.Empty, text, kind));
    }

    // ---- Accounts, rights, profiles, bans ----------------------------------------------------------------------------------

    private void HandleAccountList(PacketReader reader)
    {
        var accounts = new List<string>();
        while (!reader.AtEnd)
        {
            string account = reader.ReadGStringOrEmpty();
            if (account.Length > 0) accounts.Add(account);
        }

        accounts.Sort(StringComparer.OrdinalIgnoreCase);
        Raise(AccountListReceived, new AccountListEventArgs(accounts));
    }

    private void HandleProfile(byte[] payload)
    {
        // Alpaxis puts a GChar 34 (the account property id) before the account; classic servers do not.
        List<string>? fields = null;
        if (payload.Length > 0 && payload[0] == GEncoding.EncodeGByte((int)PlayerPropertyId.AccountName)) fields = ReadExactGStrings(payload, 1);
        fields ??= ReadLenientGStrings(payload, 0);
        string account = fields.Count > 0 ? fields[0] : string.Empty;
        Raise(ProfileReceived, new ProfileEventArgs(account, fields.Skip(1).ToArray()));
    }

    internal static List<string>? ReadExactGStrings(byte[] payload, int offset)
    {
        var reader = new PacketReader(payload, offset, payload.Length - offset);
        var fields = new List<string>();
        while (!reader.AtEnd)
        {
            if (!reader.TryReadGString(out string field)) return null;
            fields.Add(field);
        }

        return fields;
    }

    private static List<string> ReadLenientGStrings(byte[] payload, int offset)
    {
        var reader = new PacketReader(payload, offset, payload.Length - offset);
        var fields = new List<string>();
        while (!reader.AtEnd) fields.Add(reader.ReadGStringOrEmpty());
        return fields;
    }

    private void HandleLegacyBan(PacketReader reader)
    {
        string account = _pendingBanAccounts.Count > 0 ? _pendingBanAccounts.Dequeue() : string.Empty;
        string details = string.Empty;
        if (!reader.AtEnd)
        {
            string parsed = reader.ReadGStringOrEmpty();
            if (parsed.Length > 0) account = parsed;
            bool banned = reader.TryReadByte(out byte flag) && flag == '!';
            details = BanText.FormatLegacyDetails(banned, reader.ReadRemainingText());
        }

        Raise(BanDataReceived, new BanDataEventArgs(account, string.Empty, details));
    }

    private void HandleAccount(PacketReader reader)
    {
        string account = reader.ReadGStringOrEmpty();
        string password = reader.ReadGStringOrEmpty();
        string email = reader.ReadGStringOrEmpty();
        int banned = reader.TryReadGByte(out int value) ? value : 0;
        int guest = reader.TryReadGByte(out value) ? value : 0;
        int adminLevel = reader.TryReadGByte(out value) ? value : 0;
        string adminWorlds = reader.ReadGStringOrEmpty();
        string banLength = reader.ReadGStringOrEmpty();
        string banReason = reader.AtEnd ? string.Empty : CommaText.Untokenize(reader.ReadGStringOrEmpty());
        var data = new AccountData
        {
            Account = account, Password = password, Email = email, Banned = banned != 0, Guest = guest != 0, AdminLevel = adminLevel,
            AdminWorlds = adminWorlds, BanLength = banLength, BanReason = banReason,
        };
        Raise(AccountReceived, new AccountDataEventArgs(data));
    }

    private void HandlePlayerRights(PacketReader reader)
    {
        string account = reader.ReadGStringOrEmpty();
        int rights = reader.TryReadGInt5(out long raw) ? unchecked((int)raw) : 0;
        string ipRange = reader.ReadGStringOrEmpty();
        string folders = string.Empty;
        if (reader.TryReadGShort(out int length) && reader.TryReadText(Math.Min(Math.Max(length, 0), reader.Remaining), out string text))
            folders = length <= text.Length ? CommaText.Untokenize(text) : string.Empty;
        Raise(PlayerRightsReceived, new PlayerRightsEventArgs(account, new PlayerRights(rights, ipRange, folders)));
    }

    private void HandlePlayerAttributes(byte[] payload)
    {
        PlayerAttributes? attributes = PlayerAttributes.Decode(payload);
        if (attributes is null) return;
        Raise(PlayerAttributesReceived, new PlayerAttributesEventArgs(attributes, PlayerAttributesText.Format(attributes)));
    }

    private void HandleServerFlags(PacketReader reader)
    {
        if (!reader.TryReadGShort(out int count)) return;
        var flags = new System.Text.StringBuilder();
        for (int index = 0; index < count && !reader.AtEnd; index++)
        {
            if (!reader.TryReadGByte(out int length)) break;
            if (reader.TryReadText(length, out string flag)) flags.Append(flag).Append('\n');
        }

        _serverFlags = flags.ToString();
        Raise(ServerFlagsReceived, new TextEventArgs(_serverFlags));
    }

    // ---- Server texts ------------------------------------------------------------------------------------------------------

    internal static List<string> SplitParts(string untokenized)
    {
        var parts = new List<string>();
        int start = 0;
        while (start < untokenized.Length)
        {
            int end = untokenized.IndexOf('\n', start);
            if (end < 0)
            {
                parts.Add(untokenized[start..]);
                break;
            }

            parts.Add(untokenized[start..end]);
            start = end + 1;
        }

        return parts;
    }

    private void HandleServerText(string message)
    {
        string untokenized = CommaText.Untokenize(message.Replace('\u0001', '\n'));
        List<string> parts = SplitParts(untokenized);
        bool handled = parts.Count >= 2 && parts[0] == "GraalEngine";
        if (handled)
        {
            switch (parts[1])
            {
                case "pmservers":
                    _pmServers = ParseNames(parts, dropAll: true);
                    Raise(PmServersUpdated, new ValueEventArgs<int>(_pmServers.Count));
                    break;
                case "pmguilds":
                    _pmGuilds = ParseNames(parts, dropAll: false);
                    Raise(PmGuildsUpdated, new ValueEventArgs<int>(_pmGuilds.Count));
                    break;
                case "clientrc":
                    // The answer to OpenClientRcNow: 1, or 0 and the reason (then the ban texts stay unanswered; say why once).
                    if (parts.Count >= 3 && parts[2] == "0") Refuse("The server does not answer ban requests on this connection: " + (parts.Count > 3 && parts[3].Length > 0 ? parts[3] : "it refused the client RC") + ".");
                    break;
                case "pmserverplayers" when parts.Count >= 3:
                    Raise(PmServerPlayersReceived, new PmServerPlayersEventArgs(parts[2], string.Join('\n', parts.Skip(3))));
                    break;
                case "irc" when parts.Count >= 3:
                    HandleIrcText(parts);
                    break;
                case "lister" when parts.Count >= 3 && parts[2] == "simpleserverlist":
                    HandleSimpleServerList(parts);
                    break;
                case "lister" when parts.Count >= 3:
                    HandleListerText(parts);
                    break;
                default:
                    handled = false;
                    break;
            }
        }

        if (!handled) Raise(ServerTextReceived, new TextEventArgs(untokenized));
    }

    private static List<string> ParseNames(List<string> parts, bool dropAll)
    {
        var names = new List<string>();
        for (int index = 2; index < parts.Count; index++)
        {
            foreach (string entry in parts[index].Split(','))
            {
                string name = TextHelpers.Trim(entry);
                if (name.Length > 0 && !(dropAll && name == "all")) names.Add(name);
            }
        }

        return names;
    }

    private void HandleIrcText(List<string> parts)
    {
        string command = parts[2];
        if (command == "join" && parts.Count >= 4) Raise(IrcMessageReceived, new IrcMessageEventArgs(parts[3], "* Joined " + parts[3]));
        else if (command == "part" && parts.Count >= 4) Raise(IrcMessageReceived, new IrcMessageEventArgs(parts[3], "* Left " + parts[3]));
        else if (command is "privmsg" or "notice" && parts.Count >= 6)
        {
            string source = parts[3], channel = parts[4], message = parts[5];
            string line = command == "notice" ? $"* {source}: {message}" : source.Length == 0 ? message : $"<{source}> {message}";
            Raise(IrcMessageReceived, new IrcMessageEventArgs(channel, line));
        }
    }

    private void HandleListerText(List<string> parts)
    {
        string command = parts[2];
        switch (command)
        {
            case "ban":
            {
                string pending = _pendingBanAccounts.Count > 0 ? _pendingBanAccounts.Dequeue() : string.Empty;
                string account = parts.Count > 3 ? parts[3] : pending;
                string computerId = parts.Count > 4 ? parts[4] : string.Empty;
                string details = parts.Count > 5 ? string.Join('\n', parts.Skip(5)) : string.Empty;
                if (account == "GraalEngine" && computerId == "0" && pending.Length > 0)
                {
                    account = pending;
                    computerId = string.Empty;
                }

                Raise(BanDataReceived, new BanDataEventArgs(account, computerId, details));
                break;
            }
            case "bantypes":
                Raise(BanListReceived, new BanListEventArgs("bantypes", string.Empty, string.Join('\n', parts.Skip(3))));
                break;
            case "banhistory" or "staffactivity":
                Raise(BanListReceived, new BanListEventArgs(command, parts.Count > 3 ? parts[3] : string.Empty, string.Join('\n', parts.Skip(4))));
                break;
        }
    }

    private void HandleSimpleServerList(List<string> parts)
    {
        string joined = string.Join(',', parts.Skip(3));
        var entries = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (char c in joined)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                entries.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }

        if (current.Length > 0) entries.Add(current.ToString());
        var servers = new List<SimpleServerEntry>();
        for (int index = 0; index + 2 < entries.Count; index += 3)
        {
            if (!int.TryParse(entries[index + 2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int players)) continue;
            string name = entries[index].Trim('"'), display = entries[index + 1].Trim('"');
            if (name.Length > 0 && display.Length > 0) servers.Add(new SimpleServerEntry(name, display, players));
        }

        _gameServers = servers;
        Raise(GameServersUpdated);
    }

    private void HandleLevelList(byte[] payload)
    {
        _levelListRequests = Math.Max(0, _levelListRequests - 1);
        string text = DecodeLineList(payload, 0);
        _levels = SplitParts(text).Where(line => line.Length > 0).Select(line => new RcLevel(line)).ToList();
        Raise(LevelListReceived, new TextEventArgs(text));
    }

    /// <summary>
    /// A list of lines in either format a server uses: one GString per line (Alpaxis: class scripts, NPC flags, level lists) or
    /// comma text (classic servers, as RClib read them). The GString form is taken only when the whole payload parses as GStrings.
    /// </summary>
    internal static string DecodeLineList(byte[] payload, int offset)
    {
        if (offset >= payload.Length) return string.Empty;
        List<string>? lines = ReadExactGStrings(payload, offset);
        if (lines is not null) return string.Join('\n', lines);
        return CommaText.Untokenize(RcText.GetString(payload.AsSpan(offset)));
    }
}
