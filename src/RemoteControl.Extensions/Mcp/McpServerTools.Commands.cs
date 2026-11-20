using System.Text.Json;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Extensions;

internal static partial class McpServerTools
{
    /// <summary>The RC commands of the Alpaxis GServer and its NPC-Server part, as its <c>/help</c> lists them (used when a server does not answer <c>/help</c>).</summary>
    internal static readonly (string Command, string Description)[] KnownServerCommands =
    {
        ("/help", "This message."),
        ("/version", "Displays gserver version"),
        ("/credits", "Displays gserver credits"),
        ("/stats", "Shows the statistics of the server and of its scripts."),
        ("/optionshelp", "Lists the server options with what each does."),
        ("/playerinfo accountname", "Shows what is known of an online player."),
        ("/open accountname", "Opens the player's attributes window."),
        ("/openacc accountname", "Opens the player's account window."),
        ("/opencomments accountname", "Opens the player's comments window."),
        ("/openban accountname (or /openaccess)", "Opens the player's ban info."),
        ("/openrights accountname", "Opens the player's rights window."),
        ("/openprofile accountname", "Opens the player's profile."),
        ("/reset accountname", "Resets the account."),
        ("/disconnect accountname [reason]", "Disconnects every player of the account or community name."),
        ("/find file", "Finds a file. Accepts wildcards."),
        ("/findmissing", "Writes the names of the images of NPCs that the server does not have to logs/missingfiles.txt."),
        ("/refreshservermessage", "Reloads servermessage.html."),
        ("/refreshfilesystem (or /refreshfilelist)", "Reloads the server's known file list."),
        ("/updatelevel level[,level]", "Reloads levels from hard disk."),
        ("/updatelevelall", "Updates all loaded levels."),
        ("/restartserver", "Restarts the server, kicking all joined players."),
        ("/reloadserver", "Reloads the server configuration files."),
        ("/updateserverhq", "Sends the ServerHQ information to the serverlist."),
        ("/reloadwordfilter", "Reloads the word filter rules."),
        ("/reloadipbans", "Reloads the ip bans."),
        ("/reloadweapons", "Reloads the weapons from disk."),
        ("/npc<command,params>", "Passes the command to the Control-NPC's onRCChat."),
        ("/npcstart", "Starts the NPC-Server part again: its scripts load as at the server's start."),
        ("/npcshutdown", "Stops the NPC-Server part (database NPCs saved first); players stay connected."),
        ("/npckill", "Stops the NPC-Server part at once, saving nothing (when /npcshutdown or /npcstart fail)."),
        ("/savenpcs", "Saves every database NPC now."),
        ("/scripthelp text", "Shows the script help for a function, or the names that match the text."),
        ("/scriptscan weapons/npcs/classes/levels/all text", "Finds the script lines that hold the text."),
    };

    private static readonly string[] OutputKinds = { McpSessionJournal.Rc, McpSessionJournal.Server, McpSessionJournal.Nc, McpSessionJournal.Error };

    private static readonly string[] RcOutputKinds = { McpSessionJournal.Rc, McpSessionJournal.Server };

    /// <summary>
    /// What a command gathered, and whether all of the server's answer is in it: <paramref name="Cut"/> says why it may not be (nothing
    /// came in time, the server was still writing, or lines were dropped or left out).
    /// </summary>
    internal sealed record CommandOutput(IReadOnlyList<McpJournalEntry> Entries, bool Complete, string Cut);

    /// <summary>The most entries a gathering returns.</summary>
    private const int MaximumCommandEntries = 1000;

    /// <summary>
    /// Sends an RC chat command and gathers what the server writes back (the entries of <paramref name="kinds"/>): it waits up to
    /// <c>collectMilliseconds</c> (or <paramref name="defaultMaximum"/>; at most 30 seconds) for the first line, and after that until the
    /// server has said nothing for <paramref name="quiet"/>, however long the answer takes, but at most a minute more. A pause in the
    /// middle of an answer longer than <paramref name="quiet"/> also ends it (the rest is in <c>rc_events</c>). The server's answer
    /// cannot be told from other lines that arrive meanwhile (other staff's chat), so the result may hold some.
    /// </summary>
    internal static async Task<CommandOutput> RunCommandAsync(McpToolContext c, McpSessionTarget target, string command, TimeSpan quiet, int defaultMaximum, string[]? kinds = null)
    {
        kinds ??= OutputKinds;
        c.RequireRc(target);
        long since = target.Journal.Sequence;
        McpToolContext.Sent(await target.Session.SendRcChatAsync(command).ConfigureAwait(true), target.Session, "The command " + command);
        TimeSpan maximum = TimeSpan.FromMilliseconds(Math.Clamp(c.Args.Int("collectMilliseconds", defaultMaximum), 200, 30000));
        DateTimeOffset end = DateTimeOffset.UtcNow + maximum;
        long seen = since;
        DateTimeOffset? first = null;
        DateTimeOffset last = DateTimeOffset.UtcNow;
        bool complete = true;
        string cut = string.Empty;
        while (!c.Token.IsCancellationRequested)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan wait;
            if (first is null)
            {
                wait = end - now;
                if (wait <= TimeSpan.Zero)
                {
                    complete = false;
                    cut = "The server wrote nothing within " + (int)maximum.TotalMilliseconds + " ms. It may answer later (read rc_events with since " + since
                        + "), or give a larger collectMilliseconds (at most 30000).";
                    break;
                }
            }
            else
            {
                TimeSpan silence = quiet - (now - last);
                TimeSpan ceiling = first.Value + TimeSpan.FromMinutes(1) - now;
                if (silence <= TimeSpan.Zero) break;
                if (ceiling <= TimeSpan.Zero)
                {
                    complete = false;
                    cut = "The server was still writing a minute after its first line; the rest is in rc_events (since " + since + ").";
                    break;
                }
                wait = silence < ceiling ? silence : ceiling;
            }
            if (!await target.Journal.WaitAsync(seen, wait, c.Token).ConfigureAwait(true)) continue;
            // Only lines of the kinds wanted count as an answer (a player joining does not keep the wait going).
            if (target.Journal.Read(seen, kinds, 1).Count > 0)
            {
                last = DateTimeOffset.UtcNow;
                first ??= last;
            }
            seen = target.Journal.Sequence;
        }
        if (c.Token.IsCancellationRequested)
        {
            complete = false;
            cut = "The call was cancelled while the server was answering.";
        }
        IReadOnlyList<McpJournalEntry> entries = target.Journal.Read(since, kinds, MaximumCommandEntries);
        // The journal keeps the last events only: a long answer may have pushed its own beginning out, or be more than is returned.
        if (target.Journal.Oldest > since + 1)
        {
            complete = false;
            cut = "The answer was longer than the journal keeps, so its first lines are lost; narrow the command.";
        }
        else if (entries.Count == MaximumCommandEntries)
        {
            complete = false;
            cut = "Only the first " + MaximumCommandEntries + " entries are returned; read the rest with rc_events since " + entries[^1].Sequence + ".";
        }
        return new CommandOutput(entries, complete, cut);
    }

    public static async Task<string> ServerCommandsAsync(McpToolContext c)
    {
        CommandOutput help = await RunCommandAsync(c, c.Target, "/help", TimeSpan.FromMilliseconds(1200), 6000, RcOutputKinds).ConfigureAwait(true);
        IReadOnlyList<McpJournalEntry> lines = help.Entries;
        var commands = new List<(string Command, string Description)>();
        foreach (McpJournalEntry entry in lines)
        {
            foreach (string raw in entry.Text.Split('\n'))
            {
                // /help lists one command per line, the line beginning with it ("/find file: Finds a file."); anything else is not a command.
                string line = raw.Trim();
                if (!line.StartsWith('/') || line.Length < 2 || !char.IsLetter(line[1])) continue;
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                commands.Add(colon > 0 ? (line[..colon], line[(colon + 2)..]) : (line, string.Empty));
            }
        }
        bool answered = commands.Count > 0;
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("fromServer", answered);
            if (answered) writer.WriteBoolean("complete", help.Complete);
            if (answered && !help.Complete) McpJson.Text(writer, "cut", help.Cut);
            if (!answered) writer.WriteString("note", "The server listed no commands for /help; these are the commands of the Alpaxis GServer.");
            writer.WriteString("hint", "Run a command with rc_command. Other tools do more for some: rc_search_scripts (serverSide for /scriptscan, which also reads level scripts), rc_script_help (fromServer for /scripthelp), rc_update_levels, rc_get_player_info, rc_find_missing_files.");
            writer.WriteStartArray("commands");
            foreach ((string command, string description) in answered ? commands : KnownServerCommands.ToList())
            {
                writer.WriteStartObject();
                McpJson.Text(writer, "command", command);
                if (description.Length > 0) McpJson.Text(writer, "description", description);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (answered && lines.Count > 0) McpJson.Entries(writer, "output", lines);
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// <c>rc_search_scripts</c> with <c>serverSide</c>: the server's <c>/scriptscan</c>, which also reads level scripts: one scan per kind
    /// given (all when none or <c>all</c> is given). It finds plain text, as the server does: what it cannot do is refused.
    /// </summary>
    private static async Task<string> ScanScriptsOnServerAsync(McpToolContext c)
    {
        string query = c.Args.RequiredString("query");
        // What /scriptscan cannot do is refused, but only when asked for: clients often send every argument with its default value.
        string? unsupported = c.Args.Bool("regex", false) ? "regex" : c.Args.Bool("caseSensitive", false) ? "caseSensitive" : c.Args.Int("contextLines", 0) != 0 ? "contextLines"
            : c.Args.String("names").Trim().Length > 0 ? "names" : !c.Args.Bool("refresh", true) ? "refresh" : null;
        if (unsupported is not null) throw new McpToolException("/scriptscan finds plain text in whole kinds of scripts; leave " + unsupported + " out with serverSide.");
        if (query.Contains('\n')) throw new McpToolException("The text to find is one line.");
        var scopes = new List<string>();
        foreach (string kind in c.Args.Strings("kinds").Select(kind => kind.Trim().ToLowerInvariant()).Where(kind => kind.Length > 0))
        {
            string scope = kind switch
            {
                "weapon" or "weapons" => "weapons",
                "class" or "classes" => "classes",
                "npc" or "npcs" => "npcs",
                "level" or "levels" => "levels",
                "all" => "all",
                _ => throw new McpToolException("Unknown kind \"" + kind + "\" for /scriptscan: use weapon, class, npc, level or all."),
            };
            if (!scopes.Contains(scope)) scopes.Add(scope);
        }
        if (scopes.Count == 0 || scopes.Contains("all")) scopes = new List<string> { "all" };
        var scans = new List<(string Command, IReadOnlyList<McpJournalEntry> Lines, bool Complete, string Cut)>();
        foreach (string scope in scopes)
        {
            string command = "/scriptscan " + scope + " " + query;
            CommandOutput output = await RunCommandAsync(c, c.Target, command, TimeSpan.FromMilliseconds(1500), 15000, RcOutputKinds).ConfigureAwait(true);
            scans.Add((command, output.Entries, output.Complete, output.Cut));
        }
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            McpJson.Text(writer, "command", string.Join(" + ", scans.Select(scan => scan.Command)));
            string[] output = scans.SelectMany(scan => scan.Lines).SelectMany(entry => entry.Text.Split('\n')).ToArray();
            writer.WriteNumber("lines", output.Length);
            writer.WriteBoolean("complete", scans.All(scan => scan.Complete));
            if (!scans.All(scan => scan.Complete)) McpJson.Text(writer, "cut", string.Join(" ", scans.Where(scan => !scan.Complete).Select(scan => scan.Command + ": " + scan.Cut)));
            McpJson.Strings(writer, "output", output);
            if (output.Length == 0) writer.WriteString("note", "The server wrote nothing back: it may not know /scriptscan, or nothing holds the text.");
            else writer.WriteString("note", "The output is what the server wrote meanwhile; another staff member's chat at the same moment would be in it too.");
            writer.WriteEndObject();
        });
    }

    /// <summary><c>rc_script_help</c> with <c>fromServer</c>: the NPC-Server's own <c>/scripthelp</c>.</summary>
    private static async Task<string> ServerScriptHelpAsync(McpToolContext c, string text)
    {
        if (text.Length == 0 || text.Contains('\n')) throw new McpToolException("Give name or query (one line) for /scripthelp.");
        McpSessionTarget target = c.Target;
        CommandOutput result = await RunCommandAsync(c, target, "/scripthelp " + text, TimeSpan.FromMilliseconds(1200), 8000, RcOutputKinds).ConfigureAwait(true);
        IReadOnlyList<McpJournalEntry> lines = result.Entries;
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            McpJson.Text(writer, "server", target.ServerName);
            McpJson.Text(writer, "command", "/scripthelp " + text);
            writer.WriteBoolean("complete", result.Complete);
            if (!result.Complete) McpJson.Text(writer, "cut", result.Cut);
            McpJson.Strings(writer, "output", lines.SelectMany(entry => entry.Text.Split('\n')));
            if (lines.Count == 0) writer.WriteString("note", "The server wrote nothing back.");
            writer.WriteEndObject();
        });
    }
}
