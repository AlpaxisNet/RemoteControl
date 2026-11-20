using System.Globalization;
using RemoteControl.Protocol.Net;
using RemoteControl.Protocol.Text;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Session;

public sealed partial class RcSession
{
    /// <summary>A text for a field that ends at the packet's newline: line breaks would end the packet early, so they become spaces.</summary>
    internal static string OneLine(string? text) =>
        (text ?? string.Empty).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    private static string Tokenized(params string[] lines) => CommaText.Tokenize(string.Join('\n', lines));

    private static void WriteNpcId(PacketWriter writer, int id) =>
        writer.WriteByte(unchecked((byte)(((id >> 14) & 0xFF) + 32))).WriteByte((byte)(((id >> 7) & 0x7F) + 32)).WriteByte((byte)((id & 0x7F) + 32));

    private void WriteLineList(PacketWriter writer, string? text)
    {
        string cleaned = (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal);
        if (_options.NcLineListFormat == NcLineListFormat.CommaText)
        {
            writer.WriteText(CommaText.Tokenize(cleaned));
            return;
        }

        List<string> lines = SplitParts(cleaned);
        for (int index = 0; index < lines.Count; index++)
        {
            if (RcText.ByteCount(lines[index]) > GEncoding.MaxGStringLength)
                throw new PacketTooLongException($"Line {index + 1} is longer than 223 bytes; the server reads one length byte per line.");
            writer.WriteGString(lines[index]);
        }
    }

    private Task<bool> SendListerTextNow(string command, string? value, ClientPacketId packet)
    {
        string text = value is { Length: > 0 } ? Tokenized("GraalEngine", "lister", command, value) : Tokenized("GraalEngine", "lister", command);
        return SendRcNow(packet, writer => writer.WriteText(text));
    }

    /// <summary>
    /// Opens the server's "client RC" once per connection: the <c>clientrc</c> request (<c>requesttext("clientrc", 1)</c>) the in-game RC
    /// sends. An Alpaxis game server from before 2026-11-19 answers the texts of the ban window and of the player menu
    /// (<c>getban</c>, <c>getbanbyid</c>, <c>setban</c>, <c>getbanhistory</c>, <c>getstaffactivity</c>, <c>bantypes</c>) only on a
    /// connection that opened it, and drops them without a word otherwise, which left Ban History, Staff Activity and Edit Access
    /// doing nothing; it checks the same staff list, address and rights as the RC login, and tells the other staff "New RC: … (in the
    /// client)", which is why this is sent right before the first of those texts and not at login. A newer Alpaxis server answers
    /// the texts to an RC program whose login is complete in any case and announces nothing, so there the request only costs its
    /// answer; it is kept for the servers that were not updated. A server without the request ignores it. On the context.
    /// </summary>
    /// <returns>Whether it was sent (true when it was sent before).</returns>
    private Task<bool> OpenClientRcNow()
    {
        if (_clientRcRequested) return Task.FromResult(true);
        _clientRcRequested = true;
        return SendRcNow(ClientPacketId.RequestText, writer => writer.WriteText(Tokenized("GraalEngine", "clientrc", "1")));
    }

    /// <summary>Sends a ban text after <see cref="OpenClientRcNow"/>, both queued in order. On the context.</summary>
    private async Task<bool> SendBanTextNow(Func<Task<bool>> send)
    {
        Task<bool> open = OpenClientRcNow();
        Task<bool> sent = send();
        return await open.ConfigureAwait(false) & await sent.ConfigureAwait(false);
    }

    private Task<bool> SendWithPending<T>(Queue<T> queue, T item, Func<Task<bool>> send) => Run(() =>
    {
        queue.Enqueue(item);
        return send();
    }, false);

    // ---- Chat and messages -------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<bool> SendRcChatAsync(string text, bool alsoOnNc = false) => Run(async () =>
    {
        List<string> lines = SplitParts((text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal));
        if (lines.Count == 0) lines.Add(string.Empty);
        var packets = lines.Select(line => Frames.Packet((int)ClientPacketId.RcChat, RcText.GetBytes(line))).ToArray();
        Task<bool> rc = SendRcNow(packets);
        Task<bool> nc = alsoOnNc && _ncAuthenticated ? SendNcLinesNow(lines) : Task.FromResult(true);
        return await rc.ConfigureAwait(false) & await nc.ConfigureAwait(false);
    }, false);

    private Task<bool> SendNcLinesNow(List<string> lines)
    {
        var connection = _nc;
        if (connection is null || !_ncAuthenticated) return FalseTask;
        return Complete(connection.SendAsync(lines.Select(line => Frames.Packet((int)ClientPacketId.RcChat, RcText.GetBytes(line))).ToArray()));
    }

    /// <inheritdoc />
    public Task<bool> SendNcChatAsync(string text) => Run(() =>
    {
        List<string> lines = SplitParts((text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal));
        if (lines.Count == 0) lines.Add(string.Empty);
        return SendNcLinesNow(lines);
    }, false);

    /// <inheritdoc />
    public Task<bool> SendToAllAsync(string message) => SendRc(ClientPacketId.ToAll, writer => writer.WriteGString(OneLine(message)));

    /// <inheritdoc />
    public Task<bool> SendPrivateMessageAsync(int playerId, string message) =>
        SendRc(ClientPacketId.PrivateMessage, writer => writer.WriteGShort(1).WriteGShort(playerId).WriteText(CommaText.Tokenize(message)));

    /// <inheritdoc />
    public Task<bool> SendMassPrivateMessageAsync(IReadOnlyList<int> playerIds, string message) => SendRc(ClientPacketId.PrivateMessage, writer =>
    {
        if (playerIds is null || playerIds.Count == 0) throw new PacketTooLongException("A mass message needs at least one recipient.");
        if (playerIds.Count > GEncoding.MaxGShort) throw new PacketTooLongException($"A mass message can go to at most {GEncoding.MaxGShort} players.");
        writer.WriteGShort(playerIds.Count);
        foreach (int id in playerIds) writer.WriteGShort(id);
        writer.WriteText(CommaText.Tokenize(message));
    });

    private static string AdminText(string? message) =>
        (message ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', '§');

    /// <inheritdoc />
    public Task<bool> SendAdminMessageAsync(int playerId, string message) =>
        SendRc(ClientPacketId.PrivateAdminMessage, writer => writer.WriteGShort(playerId).WriteText(AdminText(message)));

    /// <inheritdoc />
    public Task<bool> SendAdminMessageToAllAsync(string message) => SendRc(ClientPacketId.AdminMessage, writer => writer.WriteText(AdminText(message)));

    /// <inheritdoc />
    public Task<bool> SetNicknameAsync(string nickname) =>
        SendRc(ClientPacketId.PlayerProps, writer => writer.WriteGByte((int)PlayerPropertyId.Nickname).WriteGString(OneLine(nickname)));

    // ---- Players -----------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<bool> WarpPlayerAsync(int playerId, string level, double x, double y) => SendRc(ClientPacketId.WarpPlayer, writer =>
        writer.WriteGShort(playerId).WriteGByte((int)Math.Floor(x * 2)).WriteGByte((int)Math.Floor(y * 2)).WriteText(OneLine(level)));

    /// <inheritdoc />
    public Task<bool> DisconnectPlayerAsync(int playerId, string reason) =>
        SendRc(ClientPacketId.DisconnectPlayer, writer => writer.WriteGShort(playerId).WriteText(OneLine(reason)));

    /// <inheritdoc />
    public Task<bool> RequestPlayerRightsAsync(string account) => SendRc(ClientPacketId.PlayerRightsGet, writer => writer.WriteGString(account));

    /// <inheritdoc />
    public Task<bool> SetPlayerRightsAsync(string account, PlayerRights rights) => SendRc(ClientPacketId.PlayerRightsSet, writer =>
        writer.WriteGString(account).WriteGInt5((uint)rights.Rights).WriteGString(rights.IpRange).WriteGShortString(CommaText.Tokenize(rights.Folders)));

    /// <inheritdoc />
    public Task<bool> RequestPlayerAttributesAsync(string account) =>
        SendRc(ClientPacketId.PlayerPropsGetByAccount, writer => writer.WriteGString(account).WriteGByte(0));

    /// <inheritdoc />
    public Task<bool> RequestPlayerAttributesAsync(int playerId) =>
        SendRc(ClientPacketId.PlayerPropsGetById, writer => writer.WriteGShort(Math.Min(playerId, GEncoding.MaxGShort)));

    /// <inheritdoc />
    public Task<bool> SetPlayerAttributesAsync(string account, PlayerAttributes attributes) =>
        SendRc(ClientPacketId.PlayerPropsSetByAccount, writer => writer.WriteBytes(attributes.EncodeForUpload(account)));

    /// <inheritdoc />
    public Task<bool> ResetPlayerAsync(string account) => SendRc(ClientPacketId.PlayerPropsReset, writer => writer.WriteText(OneLine(account)));

    /// <inheritdoc />
    public Task<bool> RequestAccountAsync(string account) => SendRc(ClientPacketId.AccountGet, writer => writer.WriteText(OneLine(account)));

    private Task<bool> SendAccount(ClientPacketId packet, string? account, AccountData data) => SendRc(packet, writer =>
    {
        string name = data.Account.Length > 0 ? data.Account : account ?? string.Empty;
        if (name.Length == 0) throw new PacketTooLongException("The account has no name.");
        int adminLevel = Math.Clamp(data.AdminLevel, 0, GEncoding.MaxGStringLength);
        string banReason = CommaText.Tokenize(data.BanReason);
        if (RcText.ByteCount(banReason) > GEncoding.MaxGStringLength)
            throw new PacketTooLongException("The ban reason is longer than the 223 bytes the server's field carries.");
        writer.WriteGString(name).WriteGString(data.Password).WriteGString(data.Email).WriteGByte(data.Banned ? 1 : 0).WriteGByte(data.Guest ? 1 : 0)
            .WriteGByte(adminLevel).WriteGString(data.AdminWorlds.Length == 0 ? "all" : data.AdminWorlds).WriteGString(banReason);
    });

    /// <inheritdoc />
    public Task<bool> SetAccountAsync(string account, AccountData data) => SendAccount(ClientPacketId.AccountSet, account, data);

    /// <inheritdoc />
    public Task<bool> AddAccountAsync(AccountData data) => SendAccount(ClientPacketId.AccountAdd, null, data);

    /// <inheritdoc />
    public Task<bool> RequestAccountListAsync(string accountFilter, string conditions) => SendRc(ClientPacketId.AccountListGet, writer =>
        writer.WriteGString(TextHelpers.Trim(accountFilter)).WriteGString(TextHelpers.Trim(conditions)));

    /// <inheritdoc />
    public Task<bool> RequestPlayerCommentsAsync(string account) => SendRc(ClientPacketId.PlayerCommentsGet, writer => writer.WriteGString(account));

    /// <inheritdoc />
    public Task<bool> SetPlayerCommentsAsync(string account, string comments) =>
        SendRc(ClientPacketId.PlayerCommentsSet, writer => writer.WriteGString(account).WriteText(CommaText.Tokenize(comments)));

    /// <inheritdoc />
    public Task<bool> RequestProfileAsync(string account) => SendRc(ClientPacketId.ProfileGet, writer => writer.WriteText(OneLine(account)));

    /// <inheritdoc />
    public Task<bool> SetProfileAsync(string account, IReadOnlyList<string> fields) => SendRc(ClientPacketId.ProfileSet, writer =>
    {
        writer.WriteGString(account);
        for (int index = 0; index < 10; index++) writer.WriteGString(OneLine(fields is not null && index < fields.Count ? fields[index] : string.Empty));
    });

    // ---- Bans --------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<bool> RequestPlayerBanAsync(string account, int playerId = -1) =>
        UseNewBanProtocol ? RequestNewPlayerBanAsync(account, playerId) : RequestLegacyPlayerBanAsync(account);

    /// <inheritdoc />
    public Task<bool> RequestNewPlayerBanAsync(string account, int playerId = -1) =>
        SendWithPending(_pendingBanAccounts, account ?? string.Empty, () => SendBanTextNow(() => playerId >= 0
            ? SendListerTextNow("getbanbyid", playerId.ToString(CultureInfo.InvariantCulture), ClientPacketId.SendText)
            : SendListerTextNow("getban", account, ClientPacketId.SendText)));

    /// <inheritdoc />
    public Task<bool> RequestLegacyPlayerBanAsync(string account) =>
        SendWithPending(_pendingBanAccounts, account ?? string.Empty, () => SendRcNow(ClientPacketId.PlayerBanGet, writer => writer.WriteGString(account)));

    /// <inheritdoc />
    public Task<bool> RequestPlayerBanByAccountAsync(string account) => RequestNewPlayerBanAsync(account, -1);

    /// <inheritdoc />
    public Task<bool> RequestBanTypesAsync() => Run(() => SendBanTextNow(() => SendListerTextNow("bantypes", null, ClientPacketId.RequestText)), false);

    /// <inheritdoc />
    public Task<bool> RequestBanHistoryAsync(string account) => Run(() => SendBanTextNow(() => SendListerTextNow("getbanhistory", account, ClientPacketId.SendText)), false);

    /// <inheritdoc />
    public Task<bool> RequestStaffActivityAsync(string account) => Run(() => SendBanTextNow(() => SendListerTextNow("getstaffactivity", account, ClientPacketId.SendText)), false);

    /// <inheritdoc />
    public Task<bool> SetBanAsync(string target, string world, bool banned, string banType, string releaseTime, string reason) =>
        Run(() => SendBanTextNow(() => SendRcNow(ClientPacketId.SendText, writer => writer.WriteText(CommaText.Tokenize(BanText.SetBanFields(target, world, banned, banType, releaseTime, reason))))), false);

    /// <inheritdoc />
    public Task<bool> SetLegacyPlayerBanAsync(string account, bool banned, string reason) => SendRc(ClientPacketId.PlayerBanSet, writer =>
    {
        string text = CommaText.Tokenize(reason);
        if (RcText.ByteCount(text) > GEncoding.MaxGShort) throw new PacketTooLongException("The ban reason is too long.");
        writer.WriteGString(account).WriteGByte(banned ? 1 : 0).WriteText(text);
    });

    // ---- Server settings ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<bool> RequestServerOptionsAsync() => SendRc(ClientPacketId.ServerOptionsGet);

    /// <inheritdoc />
    public Task<bool> UploadServerOptionsAsync(string content) => SendRc(ClientPacketId.ServerOptionsSet, writer => writer.WriteText(CommaText.Tokenize(content)));

    /// <inheritdoc />
    public Task<bool> RequestServerFlagsAsync() => SendRc(ClientPacketId.ServerFlagsGet);

    /// <inheritdoc />
    public Task<bool> UploadServerFlagsAsync(string content) => SendRc(ClientPacketId.ServerFlagsSet, writer =>
    {
        List<string> flags = SplitParts((content ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal))
            .Where(line => line.Trim().Length > 0).ToList();
        if (flags.Count > GEncoding.MaxGShort) throw new PacketTooLongException("Too many server flags.");
        writer.WriteGShort(flags.Count);
        foreach (string flag in flags) writer.WriteGString(flag);
    });

    /// <inheritdoc />
    public Task<bool> RequestFolderConfigAsync() => SendRc(ClientPacketId.FolderConfigGet);

    /// <inheritdoc />
    public Task<bool> UploadFolderConfigAsync(string content) => SendRc(ClientPacketId.FolderConfigSet, writer => writer.WriteText(CommaText.Tokenize(content)));

    /// <inheritdoc />
    public Task<bool> UpdateLevelAsync(string level) => UpdateLevelsAsync(new[] { level });

    /// <inheritdoc />
    public Task<bool> UpdateLevelsAsync(IReadOnlyList<string> levels) => SendRc(ClientPacketId.UpdateLevels, writer =>
    {
        if (levels.Count > GEncoding.MaxGShort) throw new PacketTooLongException("Too many levels.");
        writer.WriteGShort(levels.Count);
        foreach (string level in levels) writer.WriteGString(level ?? string.Empty);
    });

    /// <inheritdoc />
    public Task<bool> UploadLevelAsync(string levelPath, ReadOnlyMemory<byte> content) => _browser.UploadAsync(levelPath, content);

    /// <inheritdoc />
    public Task<byte[]?> DownloadLevelAsync(string levelPath, CancellationToken cancellationToken = default) => _browser.DownloadAsync(levelPath, cancellationToken);

    // ---- PM servers, IRC, server list --------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<bool> RequestServerListAsync() =>
        SendRc(ClientPacketId.RequestText, writer => writer.WriteText(Tokenized("GraalEngine", "lister", "simplelist")));

    /// <inheritdoc />
    public Task<bool> RequestPmServerListAsync() =>
        SendRc(ClientPacketId.RequestText, writer => writer.WriteText(CommaText.Tokenize("GraalEngine\npmservers\nall\n")));

    /// <inheritdoc />
    public Task<bool> RequestPmGuildListAsync() =>
        SendRc(ClientPacketId.RequestText, writer => writer.WriteText(CommaText.Tokenize("GraalEngine\npmguilds\n\n")));

    /// <inheritdoc />
    public Task<bool> RequestPmServerPlayersAsync(string server) =>
        SendRc(ClientPacketId.RequestText, writer => writer.WriteText(CommaText.Tokenize("GraalEngine\npmserverplayers\n" + OneLine(server) + "\n")));

    /// <inheritdoc />
    public Task<bool> UnmapPmServerAsync(string server) =>
        SendRc(ClientPacketId.RequestText, writer => writer.WriteText(CommaText.Tokenize("GraalEngine\npmunmapserver\n" + OneLine(server) + "\n")));

    /// <inheritdoc />
    public Task<bool> SendIrcTextAsync(string command, params string[] parameters) => SendRc(ClientPacketId.SendText, writer =>
    {
        var lines = new List<string> { "GraalEngine", "irc", OneLine(command) };
        if (parameters is not null) lines.AddRange(parameters.Take(3).Select(OneLine));
        writer.WriteText(CommaText.Tokenize(string.Join('\n', lines)));
    });

    /// <inheritdoc />
    public Task<bool> IrcLoginAsync() => SendIrcTextAsync("login", "-");

    /// <inheritdoc />
    public Task<bool> IrcJoinAsync(string channel) => SendIrcTextAsync("join", channel);

    /// <inheritdoc />
    public Task<bool> IrcPartAsync(string channel) => SendIrcTextAsync("part", channel);

    // ---- NC ----------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<bool> RequestWeaponListAsync() => SendNc(ClientPacketId.NcWeaponListGet);

    /// <inheritdoc />
    public Task<bool> RequestWeaponScriptAsync(string name) => SendNc(ClientPacketId.NcWeaponGet, writer => writer.WriteText(OneLine(name)));

    /// <inheritdoc />
    public Task<bool> AddWeaponAsync(string name, string image, string script) => SendNc(ClientPacketId.NcWeaponAdd, writer =>
        writer.WriteGString(name).WriteGString(image).WriteText(AdminText(script)));

    /// <inheritdoc />
    public Task<bool> UpdateWeaponAsync(string name, string image, string script) => AddWeaponAsync(name, image, script);

    /// <inheritdoc />
    public Task<bool> DeleteWeaponAsync(string name) => SendNc(ClientPacketId.NcWeaponDelete, writer => writer.WriteText(OneLine(name)));

    /// <inheritdoc />
    public Task<bool> RequestClassScriptAsync(string name) => SendNc(ClientPacketId.NcClassEdit, writer => writer.WriteText(OneLine(name)));

    /// <inheritdoc />
    public Task<bool> AddClassAsync(string name, string script) => SendNc(ClientPacketId.NcClassAdd, writer =>
    {
        writer.WriteGString(name);
        WriteLineList(writer, script);
    });

    /// <inheritdoc />
    public Task<bool> UpdateClassAsync(string name, string script) => AddClassAsync(name, script);

    /// <inheritdoc />
    public Task<bool> DeleteClassAsync(string name) => SendNc(ClientPacketId.NcClassDelete, writer => writer.WriteText(OneLine(name)));

    /// <inheritdoc />
    public Task<bool> RequestNpcScriptAsync(int npcId) => SendNc(ClientPacketId.NcNpcScriptGet, writer => WriteNpcId(writer, npcId));

    /// <inheritdoc />
    public Task<bool> UpdateNpcScriptAsync(int npcId, string script) =>
        SendNc(ClientPacketId.NcNpcScriptSet, writer => writer.WriteGInt3(npcId).WriteText(CommaText.Tokenize(script)));

    /// <inheritdoc />
    public Task<bool> RequestNpcAttributesAsync(int npcId) =>
        SendWithPending(_pendingNpcAttributes, npcId, () => SendNcNow(ClientPacketId.NcNpcGet, writer => WriteNpcId(writer, npcId)));

    /// <inheritdoc />
    public Task<bool> RequestNpcFlagsAsync(int npcId) => SendNc(ClientPacketId.NcNpcFlagsGet, writer => WriteNpcId(writer, npcId));

    /// <inheritdoc />
    public Task<bool> SetNpcFlagsAsync(int npcId, string flags) => SendNc(ClientPacketId.NcNpcFlagsSet, writer =>
    {
        WriteNpcId(writer, npcId);
        WriteLineList(writer, flags);
    });

    /// <inheritdoc />
    public Task<bool> ResetNpcAsync(int npcId) => SendNc(ClientPacketId.NcNpcReset, writer => WriteNpcId(writer, npcId));

    /// <inheritdoc />
    public Task<bool> WarpNpcAsync(int npcId, double x, double y, string level) => SendNc(ClientPacketId.NcNpcWarp, writer =>
    {
        WriteNpcId(writer, npcId);
        writer.WriteGByte((int)Math.Floor(x * 2)).WriteGByte((int)Math.Floor(y * 2)).WriteText(OneLine(level));
    });

    /// <inheritdoc />
    public Task<bool> DeleteNpcAsync(int npcId) => SendNc(ClientPacketId.NcNpcDelete, writer => writer.WriteGInt3(npcId));

    /// <inheritdoc />
    public Task<bool> CreateNpcAsync(string name, int npcId, string type, string scripter, string level, string x, string y) =>
        SendNc(ClientPacketId.NcNpcAdd, writer => writer.WriteText(Tokenized(OneLine(name), npcId.ToString(CultureInfo.InvariantCulture), OneLine(type),
            OneLine(scripter), OneLine(level), OneLine(x), OneLine(y))));

    /// <inheritdoc />
    public Task<bool> RequestLocalNpcsAsync(string level) =>
        SendWithPending(_pendingLocalNpcs, level ?? string.Empty, () => SendNcNow(ClientPacketId.NcLocalNpcsGet, writer => writer.WriteText(OneLine(level))));

    /// <inheritdoc />
    public Task<bool> RequestLevelListAsync() => Run(() =>
    {
        _levelListRequests++;
        return SendNcNow(ClientPacketId.NcLevelListGet);
    }, false);

    // ---- Raw ---------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<bool> SendRawPacketAsync(int packetId, ReadOnlyMemory<byte> payload)
    {
        byte[] bytes = payload.ToArray();
        return Run(() => SendRcNow(new[] { Frames.Packet(packetId, bytes) }), false);
    }

    /// <inheritdoc />
    public Task<bool> SendNcPacketAsync(int packetId, ReadOnlyMemory<byte> payload)
    {
        byte[] bytes = payload.ToArray();
        return Run(() =>
        {
            FramedConnection? connection = _nc;
            if (connection is null || !_ncAuthenticated) return FalseTask;
            if (packetId == (int)ClientPacketId.NcLevelListGet) _levelListRequests++;
            return Complete(connection.SendAsync(new[] { Frames.Packet(packetId, bytes) }));
        }, false);
    }
}
