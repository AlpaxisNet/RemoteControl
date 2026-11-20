using System.Globalization;
using System.Text.Json;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Text;

namespace RemoteControl.Extensions;

internal static partial class McpServerTools
{
    /// <summary>The parts of a player <c>rc_get_player_info</c> reads.</summary>
    internal static readonly string[] PlayerParts = { "player", "account", "rights", "attributes", "comments", "profile", "ban", "banhistory", "staffactivity" };

    /// <summary>The parts <c>rc_write_player_info</c> writes.</summary>
    internal static readonly string[] WritablePlayerParts = { "account", "rights", "attributes", "comments", "profile" };

    // ---- Players ---------------------------------------------------------------------------------------------------------------

    public static async Task<string> ListPlayersAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        var remote = new List<(int Id, string Account, string Nickname, string Level)>();
        string pmServer = c.Args.String("pmServer").Trim();
        if (pmServer.Length > 0)
        {
            // The players of another server join the list (Alpaxis) or arrive as a text, one tab-separated line each (classic servers).
            c.RequireRc();
            var answered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<PmServerPlayersEventArgs> text = (_, e) =>
            {
                if (!string.Equals(e.Server, pmServer, StringComparison.OrdinalIgnoreCase)) return;
                remote.AddRange(RemotePlayers(e.Players));
                answered.TrySetResult(true);
            };
            EventHandler<PlayerEventArgs> joined = (_, e) =>
            {
                if (e.Player.IsExternal) answered.TrySetResult(true);
            };
            session.PmServerPlayersReceived += text;
            session.PlayerJoined += joined;
            try
            {
                McpToolContext.Sent(await session.RequestPmServerPlayersAsync(pmServer).ConfigureAwait(true), session, "The request for the players of " + pmServer);
                await Task.WhenAny(answered.Task, Task.Delay(TimeSpan.FromSeconds(Math.Min(5, c.Timeout.TotalSeconds)), c.Token)).ConfigureAwait(true);
                await Task.Delay(300, c.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // The bridge stops: list what is there.
            }
            finally
            {
                session.PmServerPlayersReceived -= text;
                session.PlayerJoined -= joined;
            }
        }
        Func<string, bool> filter = NameFilter(c.Args.String("query"));
        bool external = c.Args.Bool("includeExternal", true);
        bool channels = c.Args.Bool("includeChannels", false);
        RcPlayer[] players = session.Players
            .Where(player => (external || !player.IsExternal) && (channels || !player.IsChannel))
            .Where(player => filter(player.Account) || filter(player.Nickname) || filter(player.Level))
            .OrderBy(player => player.IsExternal).ThenBy(player => player.Account, StringComparer.OrdinalIgnoreCase).ToArray();
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("count", players.Length);
            writer.WriteStartArray("players");
            foreach (RcPlayer player in players) WritePlayer(writer, player, properties: false);
            writer.WriteEndArray();
            if (pmServer.Length > 0 && remote.Count > 0)
            {
                // Players the other server sent as a text: their ids reach them with rc_send_message (kind pm), as the PM windows do.
                writer.WriteStartArray("pmServerPlayers");
                foreach ((int id, string account, string nickname, string level) in remote.Where(player => filter(player.Account) || filter(player.Nickname) || filter(player.Level)))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", id);
                    McpJson.Text(writer, "account", account);
                    McpJson.Text(writer, "nickname", nickname);
                    McpJson.Text(writer, "level", level);
                    McpJson.Text(writer, "server", pmServer);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (channels)
            {
                writer.WriteStartArray("channels");
                foreach (RcChannel channel in session.Channels)
                {
                    writer.WriteStartObject();
                    McpJson.Text(writer, "name", channel.Name);
                    McpJson.Text(writer, "players", channel.Players);
                    McpJson.Text(writer, "id", channel.Id);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        });
    }

    /// <summary>The players of a PM server's text, as the Players group reads it: tab-separated id, account, nickname, level per line.</summary>
    private static IEnumerable<(int Id, string Account, string Nickname, string Level)> RemotePlayers(string? text)
    {
        foreach (string raw in (text ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            string[] fields = line.Split('\t');
            if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) continue;
            string account = fields.Length > 1 ? fields[1] : string.Empty;
            string nickname = fields.Length > 2 && fields[2].Length > 0 ? fields[2] : account;
            yield return (id, account, nickname, fields.Length > 3 ? fields[3] : string.Empty);
        }
    }

    public static async Task<string> GetPlayerInfoAsync(McpToolContext c)
    {
        c.RequireRc();
        (string account, RcPlayer? online) = ResolvePlayer(c, requireOnline: false);
        IReadOnlyList<string> parts = c.Args.Strings("parts").Select(part => part.Trim().ToLowerInvariant()).Where(part => part.Length > 0).ToArray();
        if (parts.Count == 0) parts = new[] { "player", "account", "rights", "comments", "ban" };
        string? unknown = parts.FirstOrDefault(part => !PlayerParts.Contains(part));
        if (unknown is not null) throw new McpToolException("Unknown part \"" + unknown + "\": use " + string.Join(", ", PlayerParts) + ".");
        if (account.Length == 0 && parts.Any(part => part != "player" && part != "attributes" && part != "ban"))
            throw new McpToolException("The player has no account name; give an account.");

        var tasks = new Dictionary<string, Task<PlayerPart>>(StringComparer.Ordinal);
        foreach (string part in parts.Distinct())
        {
            if (part == "player") continue;
            tasks[part] = FetchPlayerPartAsync(c, c.Target, part, account, online);
        }
        try
        {
            await Task.WhenAll(tasks.Values).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is McpToolException or OperationCanceledException)
        {
            // Each part reports its own failure below.
        }
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            McpJson.Text(writer, "account", account);
            if (parts.Contains("player"))
            {
                writer.WritePropertyName("player");
                if (online is null) writer.WriteNullValue();
                else WritePlayer(writer, online, properties: true);
            }
            var errors = new List<KeyValuePair<string, string>>();
            writer.WriteStartObject("parts");
            foreach ((string part, Task<PlayerPart> task) in tasks)
            {
                if (task.IsCompletedSuccessfully)
                {
                    writer.WritePropertyName(part);
                    task.Result.Write(writer);
                }
                else
                {
                    errors.Add(new KeyValuePair<string, string>(part, task.Exception?.InnerException?.Message ?? "cancelled"));
                }
            }
            writer.WriteEndObject();
            if (errors.Count > 0)
            {
                writer.WriteStartObject("errors");
                foreach ((string part, string message) in errors) McpJson.Text(writer, part, message);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        });
    }

    /// <summary>A part of a player as fetched: its editor text (what a backup keeps) and how it is written into a result.</summary>
    private sealed record PlayerPart(string Text, Action<Utf8JsonWriter> Write);

    /// <summary>
    /// Asks the server for a part of a player and waits for it. The answer is claimed first, so that the Players group does not open its
    /// window for it (an answer comes to every listener of the session).
    /// </summary>
    private static async Task<PlayerPart> FetchPlayerPartAsync(McpToolContext c, McpSessionTarget target, string part, string account, RcPlayer? online)
    {
        IRcSession session = target.Session;
        // A request by player id (no account) claims that id's answer only, so that no other player's answer is taken by it.
        using IDisposable claim = c.Claim(target, part, account.Length > 0 ? account : online is not null ? "#" + online.Id.ToString(CultureInfo.InvariantCulture) : "*");
        switch (part)
        {
            case "account":
            {
                AccountData data = (await c.RequestAsync<AccountDataEventArgs>(session, h => session.AccountReceived += h, h => session.AccountReceived -= h,
                    e => SameAccount(e.Account.Account, account), () => session.RequestAccountAsync(account), "the account of " + account).ConfigureAwait(true)).Account;
                string text = AccountText.Format(data);
                return new PlayerPart(text, writer =>
                {
                    writer.WriteStartObject();
                    McpJson.Text(writer, "text", text);
                    McpJson.Text(writer, "email", data.Email);
                    writer.WriteNumber("adminLevel", data.AdminLevel);
                    McpJson.Text(writer, "adminWorlds", data.AdminWorlds);
                    writer.WriteBoolean("banned", data.Banned);
                    McpJson.Text(writer, "banReason", data.BanReason);
                    writer.WriteBoolean("guest", data.Guest);
                    writer.WriteEndObject();
                });
            }
            case "rights":
            {
                PlayerRights rights = (await c.RequestAsync<PlayerRightsEventArgs>(session, h => session.PlayerRightsReceived += h, h => session.PlayerRightsReceived -= h,
                    e => SameAccount(e.Account, account), () => session.RequestPlayerRightsAsync(account), "the rights of " + account).ConfigureAwait(true)).Rights;
                string text = PlayerRightsText.Format(rights);
                return new PlayerPart(text, writer =>
                {
                    writer.WriteStartObject();
                    McpJson.Text(writer, "text", text);
                    writer.WriteNumber("bits", rights.Rights);
                    McpJson.Strings(writer, "granted", RightsNames.All.Where((_, bit) => rights.Has(bit)));
                    McpJson.Text(writer, "ipRange", rights.IpRange);
                    McpJson.Strings(writer, "folders", rights.Folders.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    writer.WriteEndObject();
                });
            }
            case "attributes":
            {
                PlayerAttributesEventArgs attributes = account.Length > 0
                    ? await c.RequestAsync<PlayerAttributesEventArgs>(session, h => session.PlayerAttributesReceived += h, h => session.PlayerAttributesReceived -= h,
                        e => SameAccount(e.Account, account), () => session.RequestPlayerAttributesAsync(account), "the attributes of " + account).ConfigureAwait(true)
                    : await c.RequestAsync<PlayerAttributesEventArgs>(session, h => session.PlayerAttributesReceived += h, h => session.PlayerAttributesReceived -= h,
                        e => e.Attributes.PlayerId == online!.Id, () => session.RequestPlayerAttributesAsync(online!.Id), "the attributes of player " + online!.Id).ConfigureAwait(true);
                string text = attributes.EditorText.Length > 0 ? attributes.EditorText : PlayerAttributesText.Format(attributes.Attributes);
                return new PlayerPart(text, writer =>
                {
                    writer.WriteStartObject();
                    McpJson.Text(writer, "text", text);
                    writer.WriteEndObject();
                });
            }
            case "comments":
            {
                string comments = (await c.RequestAsync<AccountTextEventArgs>(session, h => session.PlayerCommentsReceived += h, h => session.PlayerCommentsReceived -= h,
                    e => SameAccount(e.Account, account), () => session.RequestPlayerCommentsAsync(account), "the comments of " + account).ConfigureAwait(true)).Text;
                return new PlayerPart(comments, writer =>
                {
                    writer.WriteStartObject();
                    McpJson.Text(writer, "text", comments);
                    writer.WriteEndObject();
                });
            }
            case "profile":
            {
                IReadOnlyList<string> fields = (await c.RequestAsync<ProfileEventArgs>(session, h => session.ProfileReceived += h, h => session.ProfileReceived -= h,
                    e => SameAccount(e.Account, account), () => session.RequestProfileAsync(account), "the profile of " + account).ConfigureAwait(true)).Fields;
                string text = ProfileText.Format(fields);
                return new PlayerPart(text, writer =>
                {
                    writer.WriteStartObject();
                    McpJson.Text(writer, "text", text);
                    writer.WriteStartObject("fields");
                    for (int index = 0; index < ProfileText.Labels.Count; index++) McpJson.Text(writer, ProfileText.Labels[index].TrimEnd(':'), index < fields.Count ? fields[index] : string.Empty);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                });
            }
            case "ban":
            {
                int id = online is { IsExternal: false } ? online.Id : -1;
                BanDataEventArgs ban = await c.RequestAsync<BanDataEventArgs>(session, h => session.BanDataReceived += h, h => session.BanDataReceived -= h,
                    e => account.Length == 0 || SameAccount(e.Account, account) || e.Account.Length == 0, () => session.RequestPlayerBanAsync(account, id), "the ban of " + (account.Length > 0 ? account : "player " + id)).ConfigureAwait(true);
                return new PlayerPart(ban.Details, writer =>
                {
                    writer.WriteStartObject();
                    McpJson.Text(writer, "account", ban.Account);
                    McpJson.Text(writer, "computerId", ban.ComputerId);
                    McpJson.Text(writer, "details", ban.Details);
                    writer.WriteStartObject("fields");
                    foreach ((string key, string value) in BanText.ParseDetails(ban.Details)) McpJson.Text(writer, key, value);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                });
            }
            default:
            {
                string kind = part;
                BanListEventArgs list = await c.RequestAsync<BanListEventArgs>(session, h => session.BanListReceived += h, h => session.BanListReceived -= h,
                    e => e.Kind == kind && (e.Account.Length == 0 || SameAccount(e.Account, account)),
                    () => kind == "banhistory" ? session.RequestBanHistoryAsync(account) : session.RequestStaffActivityAsync(account), "the " + kind + " of " + account).ConfigureAwait(true);
                return new PlayerPart(list.Content, writer =>
                {
                    writer.WriteStartObject();
                    McpJson.Text(writer, "text", list.Content);
                    writer.WriteEndObject();
                });
            }
        }
    }

    public static async Task<string> WritePlayerInfoAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireRc();
        string account = c.Args.RequiredString("account");
        string part = c.Args.RequiredString("part").Trim().ToLowerInvariant();
        if (!WritablePlayerParts.Contains(part)) throw new McpToolException("part must be one of " + string.Join(", ", WritablePlayerParts) + ".");
        bool reset = c.Args.Bool("reset", false);
        if (reset)
        {
            if (part != "attributes") throw new McpToolException("reset applies to the part attributes only.");
            await c.ApproveAsync("Reset the attributes of " + account + " on " + c.Server + " (the player starts over)").ConfigureAwait(true);
            long resetSince = c.Target.Journal.Sequence;
            McpBackupEntry? saved = await BackupPlayerPartAsync(c, c.Target, account, part, "rc_write_player_info reset").ConfigureAwait(true);
            McpToolContext.Sent(await session.ResetPlayerAsync(account).ConfigureAwait(true), session, "The reset");
            return Written(c, await c.CollectAsync(c.Target, resetSince, 1000, AnswerKinds).ConfigureAwait(true), saved, "The attributes of " + account + " were reset.");
        }
        bool create = c.Args.Bool("create", false);
        if (create && part != "account") throw new McpToolException("create applies to the part account only.");
        if (!c.Args.TryString("text", out string text)) throw new McpToolException("The argument \"text\" is required: the editor text, as rc_get_player_info returns it.");
        if (part == "account") CheckAccountText(text, account);
        await c.ApproveAsync((create ? "Create the account " : "Change the " + part + " of ") + account + " on " + c.Server + ":\n" + McpToolContext.Whole(text)).ConfigureAwait(true);
        McpBackupEntry? backup = create ? null : await BackupPlayerPartAsync(c, c.Target, account, part, "rc_write_player_info").ConfigureAwait(true);
        long since = c.Target.Journal.Sequence;
        await WritePlayerPartAsync(c, c.Target, account, part, text, create).ConfigureAwait(true);
        return Written(c, await c.CollectAsync(c.Target, since, 1000, AnswerKinds).ConfigureAwait(true), backup, (create ? "The account " + account + " was sent to be created." : "The " + part + " of " + account + " was sent."));
    }

    /// <summary>An account text changes the account its "Account name" line names: it must be the one approved and backed up.</summary>
    private static void CheckAccountText(string text, string account)
    {
        string named = AccountText.Parse(text).Account;
        if (named.Length > 0 && !SameAccount(named, account))
            throw new McpToolException("The text names the account " + named + ", not " + account + ": give account " + named + ", or change its Account name line.");
    }

    internal static async Task WritePlayerPartAsync(McpToolContext c, McpSessionTarget target, string account, string part, string text, bool create)
    {
        IRcSession session = target.Session;
        bool sent;
        switch (part)
        {
            case "account":
                CheckAccountText(text, account);
                AccountData data = AccountText.Parse(text) with { Account = account };
                sent = create ? await session.AddAccountAsync(data).ConfigureAwait(true) : await session.SetAccountAsync(account, data).ConfigureAwait(true);
                break;
            case "rights":
                int unnamed = 0;
                try
                {
                    using IDisposable claim = c.Claim(target, "rights", account);
                    unnamed = (await c.RequestAsync<PlayerRightsEventArgs>(session, h => session.PlayerRightsReceived += h, h => session.PlayerRightsReceived -= h,
                        e => SameAccount(e.Account, account), () => session.RequestPlayerRightsAsync(account), "the rights of " + account).ConfigureAwait(true)).Rights.Rights;
                }
                catch (McpToolException)
                {
                    // Without the current rights the bits that have no name in the text are sent as 0, as the C++ did.
                }
                sent = await session.SetPlayerRightsAsync(account, PlayerRightsText.Parse(text, unnamed)).ConfigureAwait(true);
                break;
            case "attributes":
                PlayerAttributes attributes = PlayerAttributesText.Parse(text);
                sent = await session.SetPlayerAttributesAsync(account, attributes).ConfigureAwait(true);
                break;
            case "comments":
                sent = await session.SetPlayerCommentsAsync(account, text).ConfigureAwait(true);
                break;
            default:
                sent = await session.SetProfileAsync(account, ProfileText.Parse(text)).ConfigureAwait(true);
                break;
        }
        McpToolContext.Sent(sent, session, "The " + part + " of " + account);
    }

    /// <summary>
    /// Keeps the current text of a part before it is replaced. When it cannot be read (the server does not answer) or kept, nothing is
    /// sent, unless the call gives <c>force: true</c>.
    /// </summary>
    internal static async Task<McpBackupEntry?> BackupPlayerPartAsync(McpToolContext c, McpSessionTarget target, string account, string part, string reason)
    {
        PlayerPart current;
        try
        {
            current = await FetchPlayerPartAsync(c, target, part, account, null).ConfigureAwait(true);
        }
        catch (McpToolException exception) when (!c.Args.Bool("force", false))
        {
            throw new McpToolException("The current " + part + " of " + account + " could not be read for its backup (" + exception.Message + "), so nothing was sent; give force: true to send it anyway.");
        }
        catch (McpToolException)
        {
            return null;
        }
        return await c.BackupAsync(target.ServerName, part, account, 0, string.Empty, System.Text.Encoding.UTF8.GetBytes(current.Text), reason).ConfigureAwait(true);
    }

    public static async Task<string> PlayerActionAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireRc();
        string action = c.Args.RequiredString("action").ToLowerInvariant();
        (string account, RcPlayer? player) = ResolvePlayer(c, requireOnline: true);
        RcPlayer target = player!;
        if (target.IsExternal) throw new McpToolException(account + " is on another server; only players of this server can be " + (action == "warp" ? "warped." : "disconnected."));
        long since = c.Target.Journal.Sequence;
        switch (action)
        {
            case "warp":
            {
                string level = c.Args.RequiredString("level");
                if (!c.Args.TryDouble("x", out double x) || !c.Args.TryDouble("y", out double y)) throw new McpToolException("warp needs x and y (in tiles).");
                await c.ApproveAsync("Warp " + Describe(target) + " to " + level + " (" + x.ToString(CultureInfo.InvariantCulture) + ", " + y.ToString(CultureInfo.InvariantCulture) + ") on " + c.Server).ConfigureAwait(true);
                McpToolContext.Sent(await session.WarpPlayerAsync(target.Id, level, x, y).ConfigureAwait(true), session, "The warp");
                break;
            }
            case "disconnect":
            {
                string reason = c.Args.String("reason");
                await c.ApproveAsync("Disconnect " + Describe(target) + " from " + c.Server + (reason.Length > 0 ? ": " + reason : string.Empty)).ConfigureAwait(true);
                McpToolContext.Sent(await session.DisconnectPlayerAsync(target.Id, reason).ConfigureAwait(true), session, "The disconnect");
                break;
            }
            default:
                throw new McpToolException("action must be warp or disconnect.");
        }
        return Written(c, await c.CollectAsync(c.Target, since, 1000, AnswerKinds).ConfigureAwait(true), null, action == "warp" ? Describe(target) + " was warped." : Describe(target) + " was disconnected.");
    }

    public static async Task<string> SendMessageAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireRc();
        string kind = c.Args.String("kind").ToLowerInvariant();
        if (kind.Length == 0) kind = "pm";
        string message = c.Args.RequiredString("message").Replace("\r\n", "\n", StringComparison.Ordinal);
        bool all = c.Args.Bool("all", false);
        var recipients = new List<RcPlayer>();
        // An id the player list does not have may be a player of another server that came as a text (rc_list_players pmServer): it is sent
        // as given, as the PM windows do, and the server refuses an id it does not know.
        foreach (int id in c.Args.Ints("ids"))
            recipients.Add(session.Players.FirstOrDefault(player => player.Id == id)
                ?? new RcPlayer(id, "player id " + id, string.Empty, string.Empty, PlayerListCategory.External, -1, string.Empty, new Dictionary<int, string>()));
        foreach (string account in c.Args.Strings("accounts"))
        {
            RcPlayer? found = session.Players.Where(player => SameAccount(player.Account, account)).OrderBy(player => player.IsExternal).FirstOrDefault();
            recipients.Add(found ?? throw new McpToolException(account + " is not in the player list."));
        }
        recipients = recipients.DistinctBy(player => player.Id).ToList();
        long since = c.Target.Journal.Sequence;
        switch (kind)
        {
            case "pm":
                if (recipients.Count == 0) throw new McpToolException("A private message needs ids or accounts.");
                await c.ApproveAsync("Send a private message to " + string.Join(", ", recipients.Select(Describe)) + " on " + c.Server + ":\n" + McpToolContext.Whole(message)).ConfigureAwait(true);
                McpToolContext.Sent(recipients.Count == 1
                    ? await session.SendPrivateMessageAsync(recipients[0].Id, message).ConfigureAwait(true)
                    : await session.SendMassPrivateMessageAsync(recipients.Select(player => player.Id).ToArray(), message).ConfigureAwait(true), session, "The message");
                break;
            case "admin":
                if (!all && recipients.Count == 0) throw new McpToolException("An admin message needs ids, accounts or all: true.");
                await c.ApproveAsync("Send an admin message to " + (all ? "everyone" : string.Join(", ", recipients.Select(Describe))) + " on " + c.Server + ":\n" + McpToolContext.Whole(message)).ConfigureAwait(true);
                if (all) McpToolContext.Sent(await session.SendAdminMessageToAllAsync(message).ConfigureAwait(true), session, "The admin message");
                foreach (RcPlayer player in all ? (IEnumerable<RcPlayer>)Array.Empty<RcPlayer>() : recipients)
                    McpToolContext.Sent(await session.SendAdminMessageAsync(player.Id, message).ConfigureAwait(true), session, "The admin message to " + player.Account);
                break;
            case "toall":
                await c.ApproveAsync("Send a to-all message on " + c.Server + ":\n" + McpToolContext.Whole(message)).ConfigureAwait(true);
                McpToolContext.Sent(await session.SendToAllAsync(message).ConfigureAwait(true), session, "The to-all message");
                break;
            default:
                throw new McpToolException("kind must be pm, admin or toall.");
        }
        return Written(c, await c.CollectAsync(c.Target, since, 500, AnswerKinds).ConfigureAwait(true), null, "The message was sent.");
    }

    public static async Task<string> SetBanAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireRc();
        string target = c.Args.RequiredString("target");
        bool banned = c.Args.Bool("banned", true);
        string reason = c.Args.String("reason");
        string world = c.Args.String("world").ToLowerInvariant();
        if (world.Length == 0) world = "local";
        if (world is not ("local" or "all")) throw new McpToolException("world must be local or all.");
        long since = c.Target.Journal.Sequence;
        if (!session.UseNewBanProtocol || c.Args.Bool("legacy", false))
        {
            // The old ban packet carries an account, banned or not, and a reason: nothing else may be asked of it silently.
            // Refused only when asked for: clients often send every argument with its default value, and an unban has no length.
            string? unsupported = world == "all" ? "world all" : target.StartsWith("pc:", StringComparison.OrdinalIgnoreCase) ? "a computer id (pc:)"
                : c.Args.String("type").Trim().Length > 0 ? "type" : banned && c.Args.TryInt("releaseSeconds", out int asked) && asked != 0 ? "a length (releaseSeconds)" : null;
            if (unsupported is not null)
                throw new McpToolException("The legacy ban packet bans an account of this server without an end; " + unsupported + " needs the new ban protocol (the option usenewbantype).");
            await c.ApproveAsync((banned ? "Ban " : "Unban ") + target + " on " + c.Server + (banned ? " without an end" : string.Empty) + " (legacy ban)" + (reason.Length > 0 ? ": " + reason : string.Empty)).ConfigureAwait(true);
            McpToolContext.Sent(await session.SetLegacyPlayerBanAsync(target, banned, reason).ConfigureAwait(true), session, "The ban");
        }
        else
        {
            string type = c.Args.String("type");
            string release = c.Args.TryInt("releaseSeconds", out int seconds) ? Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture) : c.Args.Bool("noEnd", false) ? "0" : string.Empty;
            if (banned && release.Length == 0 && type.Length == 0) throw new McpToolException("A ban needs a type (rc_list_ban_types), releaseSeconds, or noEnd: true.");
            string length = release.Length == 0 ? "the length of its type" : release == "0" ? "no end" : release + " seconds";
            string where = world == "all" ? " on every server of the network (from " + c.Server + ")" : " on " + c.Server;
            // A ban on every server of the network is asked every time, whatever mcpapprove says.
            await c.ApproveAsync((banned ? "Ban " : "Unban ") + target + where + " (" + (type.Length > 0 ? type + ", " : string.Empty) + (banned ? length : "lifted") + ")" + (reason.Length > 0 ? ": " + reason : string.Empty), always: world == "all").ConfigureAwait(true);
            McpToolContext.Sent(await session.SetBanAsync(target, world, banned, type, release, reason).ConfigureAwait(true), session, "The ban");
        }
        return Written(c, await c.CollectAsync(c.Target, since, 1000, AnswerKinds).ConfigureAwait(true), null, (banned ? "The ban of " : "The lift of the ban of ") + target + " was sent.");
    }

    public static async Task<string> IrcAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireRc();
        string action = c.Args.RequiredString("action").ToLowerInvariant();
        string channel = c.Args.String("channel").Trim();
        if (action is "join" or "part" or "send" && channel.Length == 0) throw new McpToolException(action + " needs a channel, such as #graal.");
        string text = c.Args.String("text");
        if (action == "send" && text.Length == 0) throw new McpToolException("send needs text.");
        string what = action switch
        {
            "login" => "Log in to IRC",
            "join" => "Join the IRC channel " + channel,
            "part" => "Leave the IRC channel " + channel,
            "send" => "Send to the IRC channel " + channel + ":\n" + McpToolContext.Whole(text),
            _ => throw new McpToolException("action must be login, join, part or send."),
        };
        await c.ApproveAsync(what + " on " + c.Server).ConfigureAwait(true);
        long since = c.Target.Journal.Sequence;
        bool sent = action switch
        {
            "login" => await session.IrcLoginAsync().ConfigureAwait(true),
            "join" => await session.IrcJoinAsync(channel).ConfigureAwait(true),
            "part" => await session.IrcPartAsync(channel).ConfigureAwait(true),
            _ => await session.SendIrcTextAsync("privmsg", channel, text).ConfigureAwait(true),
        };
        McpToolContext.Sent(sent, session, "The IRC request");
        return Written(c, await c.CollectAsync(c.Target, since, 1000, McpSessionJournal.Irc, McpSessionJournal.Rc, McpSessionJournal.Error).ConfigureAwait(true), null, what + ": sent.");
    }

    public static async Task<string> ListBanTypesAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireRc();
        BanListEventArgs types = await c.RequestAsync<BanListEventArgs>(session, h => session.BanListReceived += h, h => session.BanListReceived -= h,
            e => e.Kind == "bantypes", session.RequestBanTypesAsync, "the ban types").ConfigureAwait(true);
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            McpJson.Strings(writer, "types", types.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            writer.WriteEndObject();
        });
    }

    public static async Task<string> ListAccountsAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireRc();
        string filter = c.Args.String("filter");
        if (filter.Length == 0) filter = "*";
        string conditions = c.Args.String("conditions");
        using IDisposable claim = c.Claim(c.Target, "accountlist", "*");
        AccountListEventArgs list = await c.RequestAsync<AccountListEventArgs>(session, h => session.AccountListReceived += h, h => session.AccountListReceived -= h,
            _ => true, () => session.RequestAccountListAsync(filter, conditions), "the account list").ConfigureAwait(true);
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("count", list.Accounts.Count);
            McpJson.Strings(writer, "accounts", list.Accounts);
            writer.WriteEndObject();
        });
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------------------

    /// <summary>The player of <c>id</c> or <c>account</c>: the account name and the player-list entry (this server's before another's).</summary>
    private static (string Account, RcPlayer? Player) ResolvePlayer(McpToolContext c, bool requireOnline)
    {
        IRcSession session = c.Session;
        if (c.Args.TryInt("id", out int id))
        {
            RcPlayer player = session.Players.FirstOrDefault(item => item.Id == id) ?? throw new McpToolException("No player has the id " + id + " (rc_list_players lists them).");
            return (player.Account, player);
        }
        string account = c.Args.String("account");
        if (account.Length == 0) throw new McpToolException("Give the player's account or id.");
        RcPlayer? online = session.Players.Where(item => SameAccount(item.Account, account)).OrderBy(item => item.IsExternal).FirstOrDefault();
        if (requireOnline && online is null) throw new McpToolException(account + " is not online on " + c.Server + ".");
        return (online?.Account ?? account, online);
    }

    private static string Describe(RcPlayer player) => player.Account + (player.Nickname.Length > 0 && player.Nickname != player.Account ? " (" + player.Nickname + ")" : string.Empty);

    private static void WritePlayer(Utf8JsonWriter writer, RcPlayer player, bool properties)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", player.Id);
        McpJson.Text(writer, "account", player.Account);
        McpJson.Text(writer, "nickname", player.Nickname);
        McpJson.Text(writer, "level", player.Level);
        McpJson.Text(writer, "community", player.Community);
        writer.WriteBoolean("external", player.IsExternal);
        if (player.IsChannel) writer.WriteBoolean("channel", true);
        if (player.IsNpcServer) writer.WriteBoolean("npcServer", true);
        if (player.StatusIcon >= 0) writer.WriteNumber("statusIcon", player.StatusIcon);
        if (properties)
        {
            writer.WriteStartObject("properties");
            foreach ((int key, string value) in player.Properties.OrderBy(pair => pair.Key)) McpJson.Text(writer, key.ToString(CultureInfo.InvariantCulture), value);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    /// <summary>The result of a change: what was done, the backup and what the server said meanwhile.</summary>
    internal static string Written(McpToolContext c, IReadOnlyList<McpJournalEntry> events, McpBackupEntry? backup, string message, Action<Utf8JsonWriter>? more = null) => JsonText.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        McpJson.Text(writer, "message", message);
        if (backup is not null) McpJson.Text(writer, "backup", backup.Id);
        more?.Invoke(writer);
        McpJson.Entries(writer, "events", events);
        writer.WriteEndObject();
    });
}
