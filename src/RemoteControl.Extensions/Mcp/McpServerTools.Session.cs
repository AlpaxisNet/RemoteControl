using System.Text;
using System.Text.Json;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Text;

namespace RemoteControl.Extensions;

/// <summary>
/// The server tools of the MCP bridge: what a user does in the program's windows, done on a connected session directly (players,
/// accounts, bans, messages, NC scripts and NPCs, the file browser, server settings), plus what an agent needs around it (the session's
/// events, backups of what it replaced, local folders, script checking). Every method runs on the host's context (see
/// <see cref="McpToolContext"/>) and returns the JSON text of its result, or throws <see cref="McpToolException"/> (a tool error) or
/// <see cref="McpDeniedException"/>.
/// </summary>
internal static partial class McpServerTools
{
    private static readonly string[] AnswerKinds = { McpSessionJournal.Rc, McpSessionJournal.Nc, McpSessionJournal.Server, McpSessionJournal.Error, McpSessionJournal.Admin, McpSessionJournal.Files };

    // ---- Session -------------------------------------------------------------------------------------------------------------

    public static async Task<string> SessionInfoAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        if (c.Args.Bool("refresh", false) && session.IsAuthenticated)
        {
            try
            {
                await c.RequestAsync<ValueEventArgs<int>>(session, h => session.PmServersUpdated += h, h => session.PmServersUpdated -= h, _ => true, session.RequestPmServerListAsync, "the server list").ConfigureAwait(true);
            }
            catch (McpToolException)
            {
                // The list stays as it was; the rest is still worth reporting.
            }
        }
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            McpJson.Text(writer, "server", c.Server);
            McpJson.Text(writer, "account", session.Account);
            writer.WriteBoolean("connected", session.IsConnected);
            writer.WriteBoolean("authenticated", session.IsAuthenticated);
            writer.WriteStartObject("nc");
            writer.WriteBoolean("available", session.HasNcServer);
            writer.WriteBoolean("connected", session.IsNcConnected);
            writer.WriteBoolean("authenticated", session.IsNcAuthenticated);
            McpJson.Text(writer, "address", session.NcServerAddress ?? string.Empty);
            writer.WriteEndObject();
            writer.WriteNumber("maxUploadFileSize", session.MaxUploadFileSize);
            writer.WriteBoolean("newBanProtocol", session.UseNewBanProtocol);
            writer.WriteStartObject("counts");
            writer.WriteNumber("players", session.Players.Count(player => !player.IsExternal));
            writer.WriteNumber("externalPlayers", session.Players.Count(player => player.IsExternal));
            writer.WriteNumber("channels", session.Channels.Count);
            writer.WriteNumber("weapons", session.Weapons.Count);
            writer.WriteNumber("classes", session.Classes.Count);
            writer.WriteNumber("npcs", session.Npcs.Count);
            writer.WriteEndObject();
            McpJson.Strings(writer, "pmServers", session.PmServers);
            McpJson.Strings(writer, "pmGuilds", session.PmGuilds);
            writer.WriteStartObject("events");
            writer.WriteNumber("last", c.Target.Journal.Sequence);
            writer.WriteNumber("oldest", c.Target.Journal.Oldest);
            writer.WriteEndObject();
            McpJson.Text(writer, "lastError", session.LastError ?? string.Empty);
            writer.WriteEndObject();
        });
    }

    public static async Task<string> ReadEventsAsync(McpToolContext c)
    {
        McpSessionJournal journal = c.Target.Journal;
        long since = Math.Max(0, (long)c.Args.Int("since", 0));
        string[] kinds = c.Args.Strings("kinds").Select(kind => kind.Trim().ToLowerInvariant()).Where(kind => kind.Length > 0).ToArray();
        string? unknown = kinds.FirstOrDefault(kind => !McpSessionJournal.Kinds.Contains(kind));
        if (unknown is not null) throw new McpToolException("Unknown event kind \"" + unknown + "\": use " + string.Join(", ", McpSessionJournal.Kinds) + ".");
        int limit = Math.Clamp(c.Args.Int("limit", 100), 1, 1000);
        double wait = Math.Clamp(c.Args.TryDouble("waitSeconds", out double seconds) ? seconds : 0, 0, 120);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(wait);
        long seen = since;
        IReadOnlyList<McpJournalEntry> entries = journal.Read(since, kinds, limit);
        while (entries.Count == 0 && DateTimeOffset.UtcNow < deadline && !c.Token.IsCancellationRequested)
        {
            seen = journal.Sequence;
            await journal.WaitAsync(seen, deadline - DateTimeOffset.UtcNow, c.Token).ConfigureAwait(true);
            entries = journal.Read(since, kinds, limit);
        }
        long next = entries.Count >= limit ? entries[^1].Sequence : journal.Sequence;
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("next", next);
            writer.WriteBoolean("missed", since > 0 && since + 1 < journal.Oldest);
            McpJson.Entries(writer, "events", entries);
            writer.WriteEndObject();
        });
    }

    public static async Task<string> CommandAsync(McpToolContext c)
    {
        string text = c.Args.RequiredString("text").Replace("\r\n", "\n", StringComparison.Ordinal);
        string connection = c.Args.String("connection").ToLowerInvariant();
        if (connection.Length == 0) connection = "rc";
        if (connection is not ("rc" or "nc" or "program")) throw new McpToolException("connection must be rc, nc or program.");
        c.RequireRc();
        if (connection == "nc") c.RequireNc();
        await c.ApproveAsync("Run on " + c.Server + " (" + connection + "):\n" + McpToolContext.Whole(text)).ConfigureAwait(true);
        long since = c.Target.Journal.Sequence;
        switch (connection)
        {
            case "rc":
                McpToolContext.Sent(await c.Session.SendRcChatAsync(text).ConfigureAwait(true), c.Session, "The RC chat");
                break;
            case "nc":
                foreach (string line in text.Split('\n').Where(line => line.Length > 0))
                    McpToolContext.Sent(await c.Session.SendNcChatAsync(line).ConfigureAwait(true), c.Session, "The NC chat");
                break;
            default:
                foreach (string line in text.Split('\n').Where(line => line.Length > 0))
                {
                    McpActionResult result = await c.Host.EnterChatLineAsync(c.Target, line).ConfigureAwait(true);
                    if (!result.Success) throw new McpToolException(result.Error);
                }
                break;
        }
        IReadOnlyList<McpJournalEntry> events = await c.CollectAsync(c.Target, since, 1500).ConfigureAwait(true);
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("sent", true);
            McpJson.Entries(writer, "events", events);
            writer.WriteNumber("next", c.Target.Journal.Sequence);
            writer.WriteEndObject();
        });
    }

    public static async Task<string> NcConnectAsync(McpToolContext c)
    {
        bool connect = c.Args.Bool("connect", true);
        IRcSession session = c.Session;
        c.RequireRc();
        if (connect && session.IsNcAuthenticated) return Status("The NC is already connected.");
        if (!connect && !session.IsNcConnected) return Status("The NC is not connected.");
        if (connect && !session.HasNcServer) throw new McpToolException("The server named no NPC server.");
        await c.ApproveAsync((connect ? "Connect" : "Disconnect") + " the NC (NPC-Control) of " + c.Server).ConfigureAwait(true);
        if (!connect)
        {
            McpActionResult typed = await c.Host.EnterChatLineAsync(c.Target, "/nc disconnect").ConfigureAwait(true);
            if (!typed.Success && !session.DisconnectNc()) throw new McpToolException("The NC could not be disconnected" + McpToolContext.Because(session) + ".");
            return Status("The NC was disconnected; it stays down until rc_nc_connect or /nc connect.");
        }
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onConnected = (_, _) => connected.TrySetResult(true);
        session.NcConnected += onConnected;
        try
        {
            McpActionResult typed = await c.Host.EnterChatLineAsync(c.Target, "/nc connect").ConfigureAwait(true);
            if (!typed.Success && !await session.ConnectNcAsync(c.Token).ConfigureAwait(true)) throw new McpToolException("The NC could not be connected" + McpToolContext.Because(session) + ".");
            if (!session.IsNcAuthenticated) await c.WaitAsync(connected.Task, "the NC login").ConfigureAwait(true);
        }
        finally
        {
            session.NcConnected -= onConnected;
        }
        return Status("The NC is connected: " + session.Weapons.Count + " weapons, " + session.Classes.Count + " classes, " + session.Npcs.Count + " NPCs so far.");
    }

    public static async Task<string> OpenScriptEditorAsync(McpToolContext c)
    {
        ScriptKind kind = ParseKind(c.Args.RequiredString("kind"));
        c.RequireNc();
        (string name, int id) = await ResolveScriptAsync(c, c.Target, kind, c.Args.String("name"), c.Args.Int("id", 0)).ConfigureAwait(true);
        await c.ApproveAsync("Open the script editor of " + Describe(kind, name, id) + " on " + c.Server).ConfigureAwait(true);
        McpActionResult result = await c.Host.OpenScriptEditorAsync(c.Target, KindName(kind), name, id).ConfigureAwait(true);
        if (!result.Success) throw new McpToolException(result.Error);
        return Status("The script editor of " + Describe(kind, name, id) + " is open.");
    }

    public static async Task<string> CapturePacketsAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireRc();
        double seconds = Math.Clamp(c.Args.TryDouble("seconds", out double value) ? value : 5, 0.5, 60);
        string connection = c.Args.String("connection").ToLowerInvariant();
        if (connection is not ("" or "both" or "rc" or "nc")) throw new McpToolException("connection must be rc, nc or both.");
        var ids = new HashSet<int>(c.Args.Ints("packetIds"));
        int limit = Math.Clamp(c.Args.Int("maxPackets", 200), 1, 2000);
        int bytes = Math.Clamp(c.Args.Int("maxBytesPerPacket", 256), 0, 65536);
        var packets = new List<RawPacketEventArgs>();
        int skipped = 0;
        EventHandler<RawPacketEventArgs> handler = (_, e) =>
        {
            if (connection == "rc" && e.Connection != RcConnectionKind.Rc || connection == "nc" && e.Connection != RcConnectionKind.Nc) return;
            if (ids.Count > 0 && !ids.Contains(e.PacketId)) return;
            if (packets.Count >= limit) skipped++;
            else packets.Add(e);
        };
        session.RawPacketReceived += handler;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), c.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            session.RawPacketReceived -= handler;
        }
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("seconds", seconds);
            writer.WriteNumber("skipped", skipped);
            writer.WriteStartArray("packets");
            foreach (RawPacketEventArgs packet in packets)
            {
                bool nc = packet.Connection == RcConnectionKind.Nc;
                writer.WriteStartObject();
                writer.WriteString("connection", nc ? "nc" : "rc");
                writer.WriteNumber("id", packet.PacketId);
                McpJson.Text(writer, "name", PacketNames.NameOf(nc, PacketDirection.Incoming, packet.PacketId) ?? string.Empty);
                writer.WriteNumber("length", packet.Payload.Length);
                ReadOnlySpan<byte> shown = packet.Payload.AsSpan(0, Math.Min(bytes, packet.Payload.Length));
                writer.WriteString("hex", Convert.ToHexString(shown));
                writer.WriteString("text", Printable(shown));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static async Task<string> SendRawPacketAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        string connection = c.Args.String("connection").ToLowerInvariant();
        if (connection.Length == 0) connection = "rc";
        if (connection is not ("rc" or "nc")) throw new McpToolException("connection must be rc or nc.");
        if (!c.Args.TryInt("packetId", out int id) || id is < 0 or > 223) throw new McpToolException("packetId must be a number from 0 to 223.");
        byte[] payload;
        if (c.Args.TryString("base64", out string base64))
        {
            try { payload = Convert.FromBase64String(base64); }
            catch (FormatException) { throw new McpToolException("base64 is not valid Base64."); }
        }
        else if (c.Args.TryString("hex", out string hex))
        {
            try { payload = Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal)); }
            catch (FormatException) { throw new McpToolException("hex is not valid hexadecimal."); }
        }
        else
        {
            payload = Protocol.Wire.RcText.GetBytes(c.Args.String("text"));
        }
        if (connection == "nc") c.RequireNc();
        else c.RequireRc();
        string name = PacketNames.NameOf(connection == "nc", PacketDirection.Outgoing, id) ?? "packet " + id;
        await c.ApproveAsync("Send the raw " + connection.ToUpperInvariant() + " packet " + name + " (" + payload.Length + " bytes) to " + c.Server).ConfigureAwait(true);
        long since = c.Target.Journal.Sequence;
        bool sent = connection == "nc" ? await session.SendNcPacketAsync(id, payload).ConfigureAwait(true) : await session.SendRawPacketAsync(id, payload).ConfigureAwait(true);
        McpToolContext.Sent(sent, session, "The packet");
        IReadOnlyList<McpJournalEntry> events = await c.CollectAsync(c.Target, since, 1000).ConfigureAwait(true);
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("sent", true);
            McpJson.Text(writer, "packet", name);
            writer.WriteNumber("bytes", payload.Length);
            McpJson.Entries(writer, "events", events);
            writer.WriteEndObject();
        });
    }

    public static async Task<string> FindMissingFilesAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireRc();
        MissingFilesEventArgs answer = await c.RequestAsync<MissingFilesEventArgs>(session, h => session.MissingFilesReceived += h, h => session.MissingFilesReceived -= h, _ => true,
            session.RequestMissingFilesAsync, "the missing-files search (a server from before this request was added never answers it)").ConfigureAwait(true);
        if (answer.Error is not null) throw new McpToolException("The server refused the search: " + answer.Error);
        string query = c.Args.String("query");
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("count", answer.Files.Count);
            writer.WriteStartArray("files");
            foreach (MissingFile file in answer.Files)
            {
                if (query.Length > 0 && file.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 && file.UsedBy.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                writer.WriteStartObject();
                McpJson.Text(writer, "name", file.Name);
                writer.WriteString("kind", file.Kind.ToString().ToLowerInvariant());
                McpJson.Text(writer, "folder", file.Folder);
                McpJson.Text(writer, "path", file.Folder + file.Name);
                writer.WriteNumber("uses", file.Uses);
                McpJson.Text(writer, "usedBy", file.UsedBy);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------------------

    internal static string Status(string message) => JsonText.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        McpJson.Text(writer, "message", message);
        writer.WriteEndObject();
    });

    private static string Printable(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length);
        foreach (byte value in bytes) text.Append(value is >= 32 and < 127 ? (char)value : '.');
        return text.ToString();
    }

    /// <summary>Whether two account names are the same (the servers compare them without regard to case).</summary>
    internal static bool SameAccount(string? left, string? right) => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>The lines of a text that contain the query (or match the expression), with context, numbered as <c>N: line</c>.</summary>
    internal static string SearchText(string text, Func<string, bool> matches, int contextLines, ref int budget)
    {
        string[] lines = text.Split('\n');
        for (int index = 0; index < lines.Length; index++) if (lines[index].EndsWith('\r')) lines[index] = lines[index][..^1];
        contextLines = Math.Clamp(contextLines, 0, 20);
        var included = new bool[lines.Length];
        bool any = false;
        for (int index = 0; index < lines.Length && budget > 0; index++)
        {
            if (!matches(lines[index])) continue;
            any = true;
            budget--;
            for (int line = Math.Max(0, index - contextLines); line <= Math.Min(lines.Length - 1, index + contextLines); line++) included[line] = true;
        }
        if (!any) return string.Empty;
        var result = new StringBuilder();
        int previous = -2;
        for (int index = 0; index < included.Length; index++)
        {
            if (!included[index]) continue;
            if (previous >= 0 && index != previous + 1) result.Append("--\n");
            result.Append(index + 1).Append(": ").Append(lines[index]).Append('\n');
            previous = index;
            if (result.Length > McpProtocol.MaximumOutput) break;
        }
        return result.ToString();
    }

    /// <summary>A line test from a query: a substring (without regard to case unless asked) or a regular expression.</summary>
    internal static Func<string, bool> Matcher(McpArguments args)
    {
        string query = args.RequiredString("query");
        bool caseSensitive = args.Bool("caseSensitive", false);
        if (!args.Bool("regex", false))
        {
            StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return line => line.Contains(query, comparison);
        }
        System.Text.RegularExpressions.Regex expression;
        try
        {
            expression = new System.Text.RegularExpressions.Regex(query, (caseSensitive ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException exception)
        {
            throw new McpToolException("The regular expression is not valid: " + exception.Message);
        }
        // A line that takes too long stops the search: counting it as no match would let a backtracking pattern run for hours.
        return line =>
        {
            try { return expression.IsMatch(line); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                throw new McpToolException("The regular expression took too long on a line (it backtracks); simplify it, for example without nested quantifiers such as (\\w+\\s*)+.");
            }
        };
    }

    /// <summary>A wildcard test for names: <c>*</c> and <c>?</c>, without regard to case; an empty pattern matches everything; a pattern without wildcards matches as a substring.</summary>
    internal static Func<string, bool> NameFilter(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return _ => true;
        if (pattern.IndexOfAny(new[] { '*', '?' }) < 0) return name => name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        return name => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);
    }

    internal static void WriteEdits(Utf8JsonWriter writer, IReadOnlyList<string> notes)
    {
        if (notes.Count == 0) return;
        McpJson.Strings(writer, "notes", notes);
    }
}
