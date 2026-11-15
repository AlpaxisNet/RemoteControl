using RemoteControl.Protocol.Session;

namespace RemoteControl.Ui.Session;

/// <summary>What a completion row is.</summary>
internal enum CompletionKind
{
    /// <summary>A slash command.</summary>
    Command,

    /// <summary>An online player.</summary>
    Player,
}

/// <summary>
/// One row of the chat entry's completion list (<c>refreshMentionCompletion</c>, <c>TRemoteFrame.cpp:2265-2309</c>).
/// </summary>
/// <param name="Kind">Command or player.</param>
/// <param name="Insertion">The command text (<c>/nc connect</c>) or the account.</param>
/// <param name="Name">What is shown in bold: the command name (<c>/nc</c> for its sub-commands) or the account.</param>
/// <param name="Parameters">The parameter chips of a command (<c>account reason</c>), or the nickname of a player.</param>
/// <param name="Description">The grey description of a command, or the community of a player.</param>
/// <param name="Category">The icon category: <c>player</c>, <c>security</c>, <c>text</c>, <c>network</c>, <c>danger</c>, <c>info</c>.</param>
/// <param name="Nickname">A player's nickname.</param>
/// <param name="Community">A player's community name.</param>
internal sealed record CompletionItem(CompletionKind Kind, string Insertion, string Name, string Parameters, string Description, string Category, string Nickname = "", string Community = "")
{
    /// <summary>The C++ display text: <c>account - nick - community</c> for players, the command for commands.</summary>
    public string Display => Kind == CompletionKind.Command ? Insertion : Name + (Nickname.Length > 0 ? " - " + Nickname : string.Empty) + (Community.Length > 0 ? " - " + Community : string.Empty);

    /// <summary>The parameter chips, one per word.</summary>
    public IReadOnlyList<string> ParameterChips => Kind == CompletionKind.Command && Parameters.Length > 0 ? Parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];

    /// <summary>The second, dimmer text of the row.</summary>
    public string Detail => Kind == CompletionKind.Command ? Description : string.Join("  ", new[] { Nickname, Community }.Where(text => text.Length > 0));

    /// <summary>The one-letter mark drawn in the row's circle.</summary>
    public string Glyph => Category switch
    {
        "player" => "@",
        "security" => "!",
        "text" => "T",
        "network" => "~",
        "danger" => "x",
        _ => "/",
    };
}

/// <summary>
/// The chat entry's completion: the 50 commands of the C++ (names, usages, parameters, descriptions, <c>TRemoteFrame.cpp:113-215</c>)
/// built once, and the matching of the word at the cursor against them and the online players (<c>onMentionMatch</c>,
/// <c>onMentionSelected</c>, <c>TRemoteFrame.cpp:1457-1546</c>). Filtering is done in memory on every call (defect D11: nothing is
/// rebuilt while the user types).
/// </summary>
internal static class ChatCompletion
{
    /// <summary>The commands whose argument completes to an account.</summary>
    private static readonly string[] s_playerArgumentCommands = ["/openprofile", "/openacc", "/open", "/openrights", "/opencomments", "/openaccess", "/disconnect", "/reset"];

    private static readonly string[] s_commandNames =
    [
        "/clear", "/help", "/optionshelp", "/stats", "/playerinfo", "/open", "/openrights", "/opencomments", "/openaccess", "/openacc", "/openprofile", "/openban",
        "/disconnect", "/reset", "/localbans", "/staffactivity", "/find", "/finddef", "/global", "/updatelevel", "/refreshfilelist", "/clientstats", "/npcstart",
        "/npckill", "/reloadscriptlibs", "/loadlang", "/savenpcs", "/clearnpcs", "/npc", "/style", "/listscriptlogfunctions", "/functionprofilestart",
        "/functionprofilestop", "/functionprofileshow", "/scripthelp", "/scripthelp2", "/scriptscan", "/memstats", "/activeobjects", "/showstaticvarlinks",
        "/countnoclassnpcs", "/clearnoclassnpcs", "/npcshutdown", "/rchelp", "/nc", "/nc connect", "/nc disconnect", "/nc rc", "/reconnect", "/rc",
    ];

    private static readonly Dictionary<string, string> s_descriptions = new(StringComparer.Ordinal)
    {
        ["/clear"] = "clears the current chat", ["/help"] = "shows server and RC help", ["/optionshelp"] = "displays available server options",
        ["/stats"] = "displays server info", ["/playerinfo"] = "displays info about an online player", ["/open"] = "opens player attributes",
        ["/openrights"] = "opens player rights", ["/opencomments"] = "opens player comments", ["/openaccess"] = "opens player ban info",
        ["/openacc"] = "opens the account", ["/openprofile"] = "opens the profile", ["/openban"] = "opens account ban history",
        ["/disconnect"] = "disconnects a player", ["/reset"] = "resets a player", ["/localbans"] = "lists local bans", ["/staffactivity"] = "lists staff actions",
        ["/find"] = "searches game files", ["/finddef"] = "searches default game files", ["/global"] = "sends a global command",
        ["/updatelevel"] = "reloads levels from disk", ["/refreshfilelist"] = "rescans the server folders", ["/clientstats"] = "shows client stats",
        ["/npcstart"] = "starts the NPC server", ["/npckill"] = "stops the NPC server", ["/reloadscriptlibs"] = "reloads script libraries",
        ["/loadlang"] = "reloads translations", ["/savenpcs"] = "saves database NPCs", ["/clearnpcs"] = "deletes local database NPCs",
        ["/npc"] = "sends a control-NPC command", ["/style"] = "formats a script", ["/listscriptlogfunctions"] = "lists loggable functions",
        ["/functionprofilestart"] = "starts the function profiler", ["/functionprofilestop"] = "stops the function profiler",
        ["/functionprofileshow"] = "shows function statistics", ["/scripthelp"] = "prints script function help", ["/scripthelp2"] = "searches the script API help",
        ["/scriptscan"] = "scans npcs/weapons/classes/scripts/levels/all for text", ["/memstats"] = "shows memory usage", ["/activeobjects"] = "shows active objects",
        ["/showstaticvarlinks"] = "shows static variable links", ["/countnoclassnpcs"] = "counts unclassed NPCs", ["/clearnoclassnpcs"] = "deletes unclassed NPCs",
        ["/npcshutdown"] = "closes the server", ["/rchelp"] = "shows RC commands", ["/nc"] = "controls the NPC connection",
        ["/nc connect"] = "connects to the NPC server", ["/nc disconnect"] = "disconnects the NPC server", ["/nc rc"] = "reconnects the NPC server",
        ["/reconnect"] = "reconnects the RC server", ["/rc"] = "reconnects the RC server",
    };

    private static readonly Dictionary<string, string> s_usages = new(StringComparer.Ordinal)
    {
        ["/playerinfo"] = "/playerinfo <account>", ["/open"] = "/open <account>", ["/openrights"] = "/openrights <account>", ["/opencomments"] = "/opencomments <account>",
        ["/openaccess"] = "/openaccess <account>", ["/openacc"] = "/openacc <account>", ["/openprofile"] = "/openprofile <account>", ["/openban"] = "/openban <account>",
        ["/disconnect"] = "/disconnect <account> <reason>", ["/reset"] = "/reset <account>", ["/staffactivity"] = "/staffactivity <account>",
        ["/find"] = "/find <filepattern>", ["/finddef"] = "/finddef <filepattern>", ["/global"] = "/global <text>", ["/updatelevel"] = "/updatelevel <level[,level]>",
        ["/clientstats"] = "/clientstats <account>", ["/clearnpcs"] = "/clearnpcs <levelname>", ["/npc"] = "/npc <command>", ["/style"] = "/style <weapon|npc|class> <name>",
        ["/functionprofileshow"] = "/functionprofileshow <weapon|npc|class> <name>", ["/scripthelp"] = "/scripthelp <text>", ["/scripthelp2"] = "/scripthelp2 [query]",
        ["/scriptscan"] = "/scriptscan <scope> <text>", ["/memstats"] = "/memstats [full|malloc] [level/npc]", ["/nc"] = "/nc <connect|disconnect|rc>",
    };

    private static readonly Dictionary<string, string> s_parameters = new(StringComparer.Ordinal)
    {
        ["/playerinfo"] = "account", ["/open"] = "account", ["/openrights"] = "account", ["/opencomments"] = "account", ["/openaccess"] = "account",
        ["/openacc"] = "account", ["/openprofile"] = "account", ["/openban"] = "account", ["/disconnect"] = "account reason", ["/reset"] = "account",
        ["/staffactivity"] = "account", ["/find"] = "filepattern", ["/finddef"] = "filepattern", ["/global"] = "text", ["/updatelevel"] = "level[,level]",
        ["/clientstats"] = "account", ["/clearnpcs"] = "levelname", ["/npc"] = "command", ["/style"] = "weapon|npc|class name", ["/functionprofileshow"] = "weapon|npc|class name",
        ["/scripthelp"] = "text", ["/scripthelp2"] = "[query]", ["/scriptscan"] = "npcs/weapons/classes/scripts/levels/all text", ["/memstats"] = "[full|malloc] [level/npc]",
        ["/nc"] = "connect|disconnect|rc", ["/nc connect"] = "connect", ["/nc disconnect"] = "disconnect", ["/nc rc"] = "rc",
    };

    private static readonly string[] s_playerCategoryCommands = ["/playerinfo", "/open", "/openrights", "/opencomments", "/openaccess", "/openacc", "/openprofile", "/openban", "/disconnect", "/reset", "/staffactivity"];

    /// <summary>The command rows, in the C++ order, built once.</summary>
    public static IReadOnlyList<CompletionItem> Commands { get; } = s_commandNames.Select(BuildCommand).ToArray();

    /// <summary>The usage text of a command (<c>commandCompletionUsage</c>).</summary>
    /// <param name="command">The command.</param>
    /// <returns>The usage, or the command itself.</returns>
    public static string Usage(string command) => s_usages.TryGetValue(command, out string? usage) ? usage : command;

    /// <summary>The player rows: every online player but yourself, one per account (case-insensitive).</summary>
    /// <param name="players">The session's players.</param>
    /// <param name="ownAccount">The account logged in.</param>
    /// <returns>The rows.</returns>
    public static IReadOnlyList<CompletionItem> PlayerRows(IEnumerable<RcPlayer> players, string ownAccount)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<CompletionItem>();
        foreach (RcPlayer player in players)
        {
            string account = player.Account ?? string.Empty;
            if (account.Length == 0 || player.IsChannel || string.Equals(account, ownAccount, StringComparison.OrdinalIgnoreCase) || !seen.Add(account)) continue;
            string nick = string.IsNullOrEmpty(player.Nickname) ? account : player.Nickname;
            string community = player.Community ?? string.Empty;
            string shownNick = string.Equals(nick, account, StringComparison.OrdinalIgnoreCase) ? string.Empty : nick;
            string shownCommunity = community.Length == 0 || string.Equals(community, account, StringComparison.OrdinalIgnoreCase) || string.Equals(community, nick, StringComparison.OrdinalIgnoreCase) ? string.Empty : community;
            rows.Add(new CompletionItem(CompletionKind.Player, account, account, shownNick, shownCommunity, "player", nick, community));
        }
        return rows;
    }

    /// <summary>The rows that match the word before the cursor.</summary>
    /// <param name="text">The entry text.</param>
    /// <param name="caret">The cursor position.</param>
    /// <param name="players">The player rows (<see cref="PlayerRows"/>).</param>
    /// <returns>The matching rows, commands first.</returns>
    public static IReadOnlyList<CompletionItem> Match(string text, int caret, IReadOnlyList<CompletionItem> players)
    {
        Context? context = Analyze(text, caret);
        if (context is null) return [];
        var result = new List<CompletionItem>();
        foreach (CompletionItem command in Commands)
            if (Matches(context, command)) result.Add(command);
        foreach (CompletionItem player in players)
            if (Matches(context, player)) result.Add(player);
        return result;
    }

    /// <summary>Puts a chosen row into the entry text.</summary>
    /// <param name="text">The entry text.</param>
    /// <param name="caret">The cursor position.</param>
    /// <param name="item">The row.</param>
    /// <returns>The new text and cursor position.</returns>
    public static (string Text, int Caret) Apply(string text, int caret, CompletionItem item)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        int at = TokenStart(text, caret);
        if (at < caret && text[at] == '@')
        {
            string replacement = text[..at] + "@" + item.Insertion + " " + text[caret..];
            return (replacement, at + item.Insertion.Length + 2);
        }
        bool commandContext = (at < caret && text[at] == '/') || PreviousWord(text, at).StartsWith('/');
        if (!commandContext) return (text, caret);
        string value = item.Insertion;
        if (item.Kind == CompletionKind.Command)
        {
            string parent = PreviousWord(text, at);
            if (parent.Length > 0 && value.StartsWith(parent + " ", StringComparison.Ordinal)) value = value[(parent.Length + 1)..];
            value += " ";
        }
        string result = text[..at] + value + text[caret..];
        return (result, at + value.Length);
    }

    private sealed record Context(bool Mention, bool DirectCommand, bool CommandArgument, string Parent, string Query);

    private static Context? Analyze(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        if (caret <= 0) return null;
        int at = TokenStart(text, caret);
        string token = text[at..caret];
        bool mention = token.StartsWith('@');
        bool direct = token.StartsWith('/');
        string parent = PreviousWord(text, at);
        bool argument = !mention && !direct && parent.StartsWith('/');
        if (!mention && !direct && !argument) return null;
        string query = mention ? token[1..] : token;
        return new Context(mention, direct, argument, argument ? parent : string.Empty, query.ToLowerInvariant());
    }

    private static bool Matches(Context context, CompletionItem item)
    {
        bool commandRow = item.Kind == CompletionKind.Command;
        if (context.Mention) return !commandRow && PlayerMatches(item, context.Query);
        if (context.DirectCommand) return commandRow && item.Insertion.StartsWith(context.Query, StringComparison.Ordinal);
        // The argument of a command: its sub-commands, and players for the commands that take an account.
        if (commandRow)
        {
            string parent = context.Parent;
            if (!item.Insertion.StartsWith(parent + " ", StringComparison.Ordinal)) return false;
            return item.Insertion[(parent.Length + 1)..].StartsWith(context.Query, StringComparison.Ordinal);
        }
        return s_playerArgumentCommands.Contains(context.Parent, StringComparer.Ordinal) && PlayerMatches(item, context.Query);
    }

    private static bool PlayerMatches(CompletionItem item, string query) =>
        item.Insertion.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Nickname.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Community.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static int TokenStart(string text, int caret)
    {
        int at = caret - 1;
        while (at >= 0 && !char.IsWhiteSpace(text[at])) at--;
        return at + 1;
    }

    private static string PreviousWord(string text, int tokenStart)
    {
        int end = tokenStart - 1;
        while (end >= 0 && char.IsWhiteSpace(text[end])) end--;
        if (end < 0 || end == tokenStart - 1) return string.Empty;
        int start = end;
        while (start >= 0 && !char.IsWhiteSpace(text[start])) start--;
        return text[(start + 1)..(end + 1)];
    }

    private static CompletionItem BuildCommand(string command)
    {
        string name = command.StartsWith("/nc ", StringComparison.Ordinal) ? "/nc" : command;
        return new CompletionItem(
            CompletionKind.Command,
            command,
            name,
            s_parameters.TryGetValue(command, out string? parameters) ? parameters : string.Empty,
            s_descriptions.TryGetValue(command, out string? description) ? description : string.Empty,
            CategoryOf(command));
    }

    /// <summary>The colour category of a command's icon (<c>commandCompletionIcon</c>, <c>TRemoteFrame.cpp:242-307</c>).</summary>
    private static string CategoryOf(string command)
    {
        if (command is "/openrights" or "/openaccess") return "security";
        if (command is "/opencomments" or "/style" or "/scripthelp" or "/scriptscan") return "text";
        if (command is "/find" or "/finddef" or "/refreshfilelist" or "/updatelevel") return "network";
        if (command is "/disconnect" or "/nc disconnect" or "/reset") return "danger";
        if (command.StartsWith("/nc", StringComparison.Ordinal) || command == "/reconnect") return "network";
        return s_playerCategoryCommands.Contains(command, StringComparer.Ordinal) ? "player" : "info";
    }
}
