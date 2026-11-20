using System.Text.Json;
using RemoteControl.Core;

namespace RemoteControl.Extensions;

/// <summary>The permission a tool needs: the options <c>mcpread</c>, <c>mcpwrite</c>, <c>mcplogin</c>, <c>mcpserver</c>, <c>mcpwindows</c>, <c>mcpadmin</c> (or <c>mcpfullcontrol</c>).</summary>
internal enum McpToolGroup
{
    Read,
    Write,
    Login,
    Server,
    Windows,
    Admin,
}

/// <summary>Whether a server tool acts on a session.</summary>
internal enum McpSessionUse
{
    /// <summary>No session (local work: backups, checking a given text).</summary>
    None,

    /// <summary>A session when there is one (the <c>server</c> argument, or the selected session).</summary>
    Optional,

    /// <summary>A connected session is needed.</summary>
    Required,
}

/// <summary>A tool of <c>tools/list</c>.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Group">The permission it needs.</param>
/// <param name="Json">Its <c>tools/list</c> entry.</param>
/// <param name="Run">What it does (server tools), or <c>null</c> for the program tools handled by <see cref="McpRequestProcessor"/> itself.</param>
/// <param name="Session">Whether it acts on a session.</param>
/// <param name="TimeoutSeconds">How long it waits for an answer of the server unless <c>timeoutSeconds</c> says otherwise.</param>
internal sealed record McpToolDefinition(string Name, McpToolGroup Group, string Json, Func<McpToolContext, Task<string>>? Run, McpSessionUse Session, int TimeoutSeconds);

/// <summary>
/// The tool list of <c>tools/list</c>: the program tools of the C++ (names, descriptions and input schemas as it had them), then the
/// server tools that act on a connected session directly, each in the group of the permission it needs.
/// </summary>
internal static class McpTools
{
    private const string Server = "\"server\":{\"type\":\"string\",\"description\":\"The connected server to act on (as rc_list_connections names it); the selected session when left out.\"}";
    private const string Timeout = "\"timeoutSeconds\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":600,\"description\":\"How long to wait for each answer of the server.\"}";
    private const string Collect = "\"collectMilliseconds\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":30000,\"description\":\"How long to gather what the server says afterwards (returned as events).\"}";
    private const string Kind = "\"kind\":{\"type\":\"string\",\"enum\":[\"weapon\",\"class\",\"npc\"]}";
    private const string ScriptName = "\"name\":{\"type\":\"string\",\"description\":\"The weapon or class name, or the NPC's name.\"},\"id\":{\"type\":\"integer\",\"description\":\"The NPC id (NPCs only).\"}";
    private const string Edits = "\"edits\":{\"type\":\"array\",\"description\":\"Guarded replacements applied in order: each expected text must occur exactly once (or every occurrence with all: true), otherwise nothing is written.\",\"items\":{\"type\":\"object\",\"properties\":{\"expected\":{\"type\":\"string\"},\"replacement\":{\"type\":\"string\"},\"all\":{\"type\":\"boolean\"}},\"required\":[\"expected\",\"replacement\"]}},\"expected\":{\"type\":\"string\",\"description\":\"One guarded replacement (with replacement) instead of edits.\"},\"replacement\":{\"type\":\"string\"}";
    private const string Search = "\"query\":{\"type\":\"string\"},\"regex\":{\"type\":\"boolean\",\"description\":\"query is a .NET regular expression.\"},\"caseSensitive\":{\"type\":\"boolean\"},\"contextLines\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":20}";
    private const string Force = "\"force\":{\"type\":\"boolean\",\"description\":\"Go ahead even when the version it replaces cannot be read or backed up (normally nothing is changed then).\"}";
    private const string Lines = "\"start\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"First line of a numbered range (at most 200 lines).\"},\"end\":{\"type\":\"integer\",\"minimum\":1},\"query\":{\"type\":\"string\",\"description\":\"Return only the numbered lines that contain this (with contextLines).\"},\"regex\":{\"type\":\"boolean\"},\"caseSensitive\":{\"type\":\"boolean\"},\"contextLines\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":20}";
    private const string Encoding = "\"encoding\":{\"type\":\"string\",\"enum\":[\"auto\",\"utf8\",\"utf8-bom\",\"windows-1252\",\"base64\"],\"description\":\"auto (default): UTF-8 when the bytes are valid UTF-8, otherwise Windows-1252, the servers' encoding.\"}";

    private static readonly McpToolDefinition[] Definitions = Build();

    private static readonly Dictionary<string, McpToolDefinition> ByName = Definitions.ToDictionary(definition => definition.Name, StringComparer.Ordinal);

    /// <summary>Every tool name, in the order of <c>tools/list</c> with every permission.</summary>
    public static IReadOnlyList<string> AllNames { get; } = Definitions.Select(definition => definition.Name).ToList();

    /// <summary>The names of the server tools (those with <see cref="McpToolDefinition.Run"/>).</summary>
    public static IReadOnlyList<string> ServerToolNames { get; } = Definitions.Where(definition => definition.Run is not null).Select(definition => definition.Name).ToList();

    /// <summary>A tool by name.</summary>
    public static bool TryGet(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out McpToolDefinition? definition) => ByName.TryGetValue(name ?? string.Empty, out definition);

    /// <summary>Whether the options allow a group.</summary>
    public static bool Allowed(McpToolGroup group, RcOptions options) => options.McpFullControl || group switch
    {
        McpToolGroup.Read => options.McpRead,
        McpToolGroup.Write => options.McpWrite,
        McpToolGroup.Login => options.McpLogin,
        McpToolGroup.Server => options.McpServer,
        McpToolGroup.Windows => options.McpWindows,
        _ => options.McpAdmin,
    };

    /// <summary>The tools the options allow, as a compact JSON array.</summary>
    public static string ListJson(RcOptions options) => JsonText.Write(writer =>
    {
        writer.WriteStartArray();
        foreach (McpToolDefinition definition in Definitions)
            if (Allowed(definition.Group, options)) writer.WriteRawValue(definition.Json, skipInputValidation: true);
        writer.WriteEndArray();
    });

    private static McpToolDefinition[] Build()
    {
        var tools = new List<McpToolDefinition>();
        McpToolGroup[] order = { McpToolGroup.Read, McpToolGroup.Write, McpToolGroup.Login, McpToolGroup.Server, McpToolGroup.Windows, McpToolGroup.Admin };
        List<McpToolDefinition> server = ServerTools();
        foreach (McpToolGroup group in order)
        {
            tools.AddRange(ProgramTools(group));
            tools.AddRange(server.Where(definition => definition.Group == group));
        }
        return tools.ToArray();
    }

    // ---- The program tools of the C++ -------------------------------------------------------------------------------------------

    private static IEnumerable<McpToolDefinition> ProgramTools(McpToolGroup group)
    {
        string json = group switch
        {
            McpToolGroup.Read => """
                {"name":"rc_status","description":"Current selected RC GUI process and server context, and the local folders MCP may use.","inputSchema":{"type":"object"}},
                {"name":"rc_list_connections","description":"List all connected RC server GUI sessions available to MCP.","inputSchema":{"type":"object"}},
                {"name":"rc_login_status","description":"Login form state without exposing its password.","inputSchema":{"type":"object"}},
                {"name":"rc_list_servers","description":"List server names loaded by the native RC server selector.","inputSchema":{"type":"object"}},
                {"name":"rc_list_windows","description":"List visible managed RC windows and tabs.","inputSchema":{"type":"object"}},
                {"name":"rc_read_window","description":"Read structured state for a visible managed RC window by title.","inputSchema":{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}},
                {"name":"rc_list_script_editors","description":"List open tracked script editors, including dirty state.","inputSchema":{"type":"object"}},
                {"name":"rc_read_script_editor","description":"Read the complete current open editor buffer and selection, including unsaved text.","inputSchema":{"type":"object","properties":{"editor":{"type":"string"}},"required":["editor"]}},
                {"name":"rc_read_script_lines","description":"Read a bounded line-numbered range from the current open editor buffer, including unsaved text.","inputSchema":{"type":"object","properties":{"editor":{"type":"string"},"start":{"type":"integer","minimum":1},"end":{"type":"integer","minimum":1}},"required":["editor","start","end"]}},
                {"name":"rc_search_script_editor","description":"Search the current open editor buffer and return bounded line-numbered matches with optional context.","inputSchema":{"type":"object","properties":{"editor":{"type":"string"},"query":{"type":"string"},"caseSensitive":{"type":"boolean"},"contextLines":{"type":"integer","minimum":0,"maximum":20}},"required":["editor","query"]}}
                """,
            McpToolGroup.Write => """
                {"name":"rc_replace_script_text","description":"Guarded partial edit: replace one unique expected text region in the open buffer and leave it unsaved; never Save/Apply.","inputSchema":{"type":"object","properties":{"editor":{"type":"string"},"expected":{"type":"string","maxLength":65536},"replacement":{"type":"string","maxLength":65536}},"required":["editor","expected","replacement"]}},
                {"name":"rc_write_script_editor","description":"Edit only the open script buffer and leave it unsaved; does not Save/Apply to the server.","inputSchema":{"type":"object","properties":{"editor":{"type":"string"},"text":{"type":"string"}},"required":["editor","text"]}},
                {"name":"rc_save_script_editor","description":"Explicitly Save/Apply the current open script buffer to the server.","inputSchema":{"type":"object","properties":{"editor":{"type":"string"}},"required":["editor"]}}
                """,
            McpToolGroup.Login => """
                {"name":"rc_submit_login","description":"Submit the native login form using its existing password and optional non-secret account/nickname overrides; never returns credentials.","inputSchema":{"type":"object","properties":{"account":{"type":"string"},"nickname":{"type":"string"}}}},
                {"name":"rc_connect_server","description":"Select and connect a named server through the native RC server list workflow.","inputSchema":{"type":"object","properties":{"server":{"type":"string"}},"required":["server"]}}
                """,
            McpToolGroup.Server => """
                {"name":"rc_send_chat","description":"Send RC chat text through the native connected RC controller.","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}}
                """,
            McpToolGroup.Windows => """
                {"name":"rc_open_window","description":"Create/show a supported managed RC view through its native controller.","inputSchema":{"type":"object","properties":{"view":{"type":"string","enum":["player_list","file_browser","classes","weapons","npcs","server_options","server_flags","folder_config","toalls","accounts","options"]}},"required":["view"]}},
                {"name":"rc_select_connection","description":"Select which connected RC server session subsequent MCP GUI actions target.","inputSchema":{"type":"object","properties":{"server":{"type":"string"}},"required":["server"]}}
                """,
            _ => """
                {"name":"rc_focused_window_action","description":"Present or close the focused RC window.","inputSchema":{"type":"object","properties":{"action":{"type":"string","enum":["present","close"]}},"required":["action"]}}
                """,
        };
        using JsonDocument document = JsonDocument.Parse("[" + json + "]");
        foreach (JsonElement tool in document.RootElement.EnumerateArray())
            yield return new McpToolDefinition(tool.GetProperty("name").GetString()!, group, JsonText.Write(writer => tool.WriteTo(writer)), null, McpSessionUse.None, 0);
    }

    // ---- The server tools -------------------------------------------------------------------------------------------------------

    private static List<McpToolDefinition> ServerTools() => new()
    {
        // Read: diagnose.
        Tool("rc_session_info", McpToolGroup.Read, "State of a server session: account, RC and NC connection, upload limit, counts of players, weapons, classes and NPCs, private-message servers, and the number of the last event.",
            "\"refresh\":{\"type\":\"boolean\",\"description\":\"Ask for the list of the network's other servers (pmServers) again.\"}", None, McpServerTools.SessionInfoAsync, timeout: true),
        Tool("rc_read_events", McpToolGroup.Read, "Read what the session saw since an event number: RC and NC chat (script compiler output, errors), private, admin and to-all messages, IRC, server texts, file-browser messages, refused sends, connects and disconnects, players joining and leaving. Pass the returned next as since to read only newer events; waitSeconds waits for something new.",
            "\"since\":{\"type\":\"integer\",\"minimum\":0},\"kinds\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"rc\",\"nc\",\"pm\",\"admin\",\"toall\",\"irc\",\"server\",\"files\",\"error\",\"connection\",\"players\"]}},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"waitSeconds\":{\"type\":\"number\",\"minimum\":0,\"maximum\":120}", None, McpServerTools.ReadEventsAsync),
        Tool("rc_list_players", McpToolGroup.Read, "The player list: id, account, nickname, level, community, and whether the player is on another server.",
            "\"query\":{\"type\":\"string\",\"description\":\"Filter on account, nickname or level (* and ? wildcards, otherwise a substring).\"},\"includeExternal\":{\"type\":\"boolean\"},\"includeChannels\":{\"type\":\"boolean\"},\"pmServer\":{\"type\":\"string\",\"description\":\"First ask for the players of this other server (rc_session_info lists pmServers), so they get ids for rc_send_message.\"}", None, McpServerTools.ListPlayersAsync, timeout: true),
        Tool("rc_get_player_info", McpToolGroup.Read, "Everything about a player or account at once, as the RC windows show it: the player-list entry with its properties, and the editor texts of account, rights, attributes, comments, profile, ban, ban history and staff activity (the texts rc_write_player_info takes back). Parts that fail are listed under errors.",
            "\"account\":{\"type\":\"string\"},\"id\":{\"type\":\"integer\",\"description\":\"A player id of the player list instead of the account.\"},\"parts\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"player\",\"account\",\"rights\",\"attributes\",\"comments\",\"profile\",\"ban\",\"banhistory\",\"staffactivity\"]},\"description\":\"Default: player, account, rights, comments, ban.\"}", Required(), McpServerTools.GetPlayerInfoAsync, timeout: true),
        Tool("rc_list_accounts", McpToolGroup.Read, "The server's accounts matching a wildcard and conditions, as the accounts window lists them.",
            "\"filter\":{\"type\":\"string\",\"description\":\"Account wildcard, default *.\"},\"conditions\":{\"type\":\"string\"}", None, McpServerTools.ListAccountsAsync, timeout: true),
        Tool("rc_list_ban_types", McpToolGroup.Read, "The ban types the server knows (for rc_set_ban).", "", None, McpServerTools.ListBanTypesAsync, timeout: true),
        Tool("rc_read_server_text", McpToolGroup.Read, "Read the server options, the server flags or the folder configuration (the whole text, or the lines matching query).",
            "\"kind\":{\"type\":\"string\",\"enum\":[\"options\",\"flags\",\"folderconfig\"]}," + Search, Required("kind"), McpServerTools.ReadServerTextAsync, timeout: true),
        Tool("rc_list_scripts", McpToolGroup.Read, "List the NC's weapons, classes and database NPCs (id, name, type, level). Needs the NC.",
            "\"kind\":{\"type\":\"string\",\"enum\":[\"all\",\"weapon\",\"class\",\"npc\"]},\"query\":{\"type\":\"string\",\"description\":\"Name filter (* and ? wildcards, otherwise a substring).\"},\"refresh\":{\"type\":\"boolean\",\"description\":\"Ask the server for the weapon list again.\"}", None, McpServerTools.ListScriptsAsync, timeout: true),
        Tool("rc_read_script", McpToolGroup.Read, "Read a weapon, class or NPC script fresh from the server (and a weapon's image): the whole text, a numbered line range, or the lines matching query.",
            Kind + "," + ScriptName + "," + Lines, Required("kind"), McpServerTools.ReadScriptAsync, timeout: true),
        Tool("rc_search_scripts", McpToolGroup.Read, "Search every weapon, class and NPC script of the server (or some kinds or names) for a text or regular expression; returns numbered matching lines per script. Use it to find where a function, flag or variable is defined or used. serverSide: true lets the server search instead (/scriptscan, plain text), which also covers level scripts (kind level).",
            "\"serverSide\":{\"type\":\"boolean\"},\"kinds\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"weapon\",\"class\",\"npc\",\"level\",\"all\"]}},\"names\":{\"type\":\"string\",\"description\":\"Only scripts whose name matches (* and ? wildcards, otherwise a substring).\"},\"refresh\":{\"type\":\"boolean\",\"description\":\"false uses the scripts already fetched in this session where there are any (faster).\"},\"maxMatches\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000}," + Search + "," + Collect,
            Required("query"), McpServerTools.SearchScriptsAsync, timeout: true),
        Tool("rc_read_npc", McpToolGroup.Read, "Read a database NPC: its attributes, flags and/or script.",
            "\"id\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"},\"parts\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"attributes\",\"flags\",\"script\"]},\"description\":\"Default: attributes and flags.\"}", None, McpServerTools.ReadNpcAsync, timeout: true),
        Tool("rc_list_levels", McpToolGroup.Read, "The NC's level list. Needs the NC.",
            "\"query\":{\"type\":\"string\"},\"refresh\":{\"type\":\"boolean\"}", None, McpServerTools.ListLevelsAsync, timeout: true),
        Tool("rc_read_local_npcs", McpToolGroup.Read, "The level NPCs of a level as the NC dumps them (names, positions, scripts' first lines).",
            "\"level\":{\"type\":\"string\"}", Required("level"), McpServerTools.ReadLocalNpcsAsync, timeout: true),
        Tool("rc_list_files", McpToolGroup.Read, "The file browser: without folder, the folders the account may use with their rights; with folder (such as levels/), its files with size, time and rights.",
            "\"folder\":{\"type\":\"string\"},\"query\":{\"type\":\"string\",\"description\":\"Name filter (* and ? wildcards, otherwise a substring).\"}", None, McpServerTools.ListFilesAsync, timeout: true),
        Tool("rc_read_file", McpToolGroup.Read, "Read a server file raw (levels, ganis, text and config files, images as base64): the whole text, a numbered line range, or the lines matching query.",
            "\"path\":{\"type\":\"string\",\"description\":\"folder/name, such as levels/start.nw.\"}," + Encoding + "," + Lines, Required("path"), McpServerTools.ReadFileAsync),
        Tool("rc_search_files", McpToolGroup.Read, "Search the text files of a server folder (optionally only names matching pattern) for a text or regular expression.",
            "\"folder\":{\"type\":\"string\"},\"pattern\":{\"type\":\"string\",\"description\":\"File name wildcard, such as *.nw.\"},\"maxFiles\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"maxFileBytes\":{\"type\":\"integer\",\"minimum\":1},\"maxMatches\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000}," + Search,
            Required("folder", "query"), McpServerTools.SearchFilesAsync, timeout: true),
        Tool("rc_find_missing_files", McpToolGroup.Read, "Ask the game server which files its levels, NPCs, weapons, classes and animations name but it does not have, with the folder each belongs in (copy them in with rc_copy or rc_write_file). Needs a server that knows the request.",
            "\"query\":{\"type\":\"string\",\"description\":\"Only names or users containing this.\"}", None, McpServerTools.FindMissingFilesAsync, timeout: true, defaultTimeout: 120),
        Tool("rc_server_commands", McpToolGroup.Read, "The RC commands the server knows, from its /help (the Alpaxis GServer's list when it says nothing), with which MCP tools do the same with more structure.",
            Collect, None, McpServerTools.ServerCommandsAsync),
        Tool("rc_capture_packets", McpToolGroup.Read, "Record the packets the server sends for a few seconds (id, name, length, hex and printable text) to see what the protocol does.",
            "\"seconds\":{\"type\":\"number\",\"minimum\":0.5,\"maximum\":60},\"connection\":{\"type\":\"string\",\"enum\":[\"both\",\"rc\",\"nc\"]},\"packetIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"maxPackets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2000},\"maxBytesPerPacket\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":65536}", None, McpServerTools.CapturePacketsAsync),
        Tool("rc_check_script", McpToolGroup.Read, "Check a GS2 script for syntax errors and likely mistakes without sending it (line, column, message), as the script editor does: with the script API of api.gscript.dev it also reports unknown functions and wrong argument counts. Give text, or kind and name to check what the server has. format: true also returns it formatted.",
            "\"text\":{\"type\":\"string\"}," + Kind + "," + ScriptName + ",\"format\":{\"type\":\"boolean\"},\"api\":{\"type\":\"boolean\",\"description\":\"false checks the syntax only, without the script API.\"}", None, McpServerTools.CheckScriptAsync, session: McpSessionUse.Optional),
        Tool("rc_script_help", McpToolGroup.Read, "Look up the GS2 script API (api.gscript.dev, the definitions the script editor completes with): a function, variable or event by name with its signature, parameters, return type, scope (clientside, serverside, global), description and example; or find names by query (* and ? wildcards, or a substring; inDescriptions searches the descriptions too). fromServer: true asks the connected NPC-Server instead (/scripthelp), which knows exactly its own build.",
            "\"name\":{\"type\":\"string\",\"description\":\"Exact name, such as showimg or player.chat.\"},\"query\":{\"type\":\"string\"},\"inDescriptions\":{\"type\":\"boolean\"},\"scope\":{\"type\":\"string\",\"enum\":[\"clientside\",\"serverside\",\"global\"]},\"type\":{\"type\":\"string\",\"description\":\"function, variable, event ...\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":300},\"refresh\":{\"type\":\"boolean\",\"description\":\"Load the API again from its source.\"},\"fromServer\":{\"type\":\"boolean\"},\"collectMilliseconds\":{\"type\":\"integer\",\"minimum\":200,\"maximum\":30000}",
            None, McpServerTools.ScriptHelpAsync, session: McpSessionUse.Optional),
        Tool("rc_gs2_docs", McpToolGroup.Read, "The GS2 language reference bundled with the program (GScript.GS2Taco: docs/GraalScript_Reference.md, verified by people, CORE.md, the basics, GUI controls and profiles, events, particles, the RC script help): without document, the list of documents; with document, its text, a section (a heading), a line range, or its outline (long documents give their outline first). Read it before writing GS2.",
            "\"document\":{\"type\":\"string\",\"description\":\"Such as docs/GraalScript_Reference.md or CORE.md.\"},\"section\":{\"type\":\"string\",\"description\":\"A heading (or part of one).\"},\"outline\":{\"type\":\"boolean\"},\"start\":{\"type\":\"integer\",\"minimum\":1},\"end\":{\"type\":\"integer\",\"minimum\":1},\"filter\":{\"type\":\"string\",\"description\":\"List only documents whose name matches (also those of the folders of mcpreferencefolders).\"}",
            None, McpServerTools.Gs2DocsAsync, session: McpSessionUse.None),
        Tool("rc_gs2_search", McpToolGroup.Read, "Search the GS2 reference for a text or regular expression: the verified guide first, then the core reference, the basics, the other documents, and the folders of the option mcpreferencefolders (such as decompiled engine code and example scripts of a GS2Taco checkout). Returns numbered lines with context and the sections they are in.",
            Search + ",\"documents\":{\"type\":\"string\",\"description\":\"Only documents whose name matches (* and ? wildcards, otherwise a substring), such as docs/*.md or ghidra.\"},\"includeOtherFolders\":{\"type\":\"boolean\"},\"maxMatches\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"maxPerDocument\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}",
            Required("query"), McpServerTools.Gs2SearchAsync, session: McpSessionUse.None),
        Tool("rc_backups", McpToolGroup.Read, "The copies MCP kept of everything it overwrote or deleted (newest first), or one of them with its content (give id). rc_restore_backup puts one back.",
            "\"id\":{\"type\":\"string\"},\"ofServer\":{\"type\":\"string\",\"description\":\"Only backups of this server.\"},\"query\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}", None, McpServerTools.BackupsAsync, session: McpSessionUse.None),

        // Write: scripts and files.
        Tool("rc_write_script", McpToolGroup.Write, "Write a weapon, class or NPC script straight to the server (a weapon or class is added when it does not exist): the whole script, or guarded edits of the server's current text. The old script is backed up first; the NC's answer (compiler output) is returned as events.",
            Kind + "," + ScriptName + ",\"script\":{\"type\":\"string\",\"description\":\"The whole new script.\"}," + Edits + ",\"image\":{\"type\":\"string\",\"description\":\"A weapon's image (kept when left out).\"},\"create\":{\"type\":\"boolean\",\"description\":\"false refuses to add a script that does not exist.\"},\"verify\":{\"type\":\"boolean\",\"description\":\"Read the script back and compare.\"}," + Force + "," + Collect,
            Required("kind"), McpServerTools.WriteScriptAsync, timeout: true, destructive: true),
        Tool("rc_delete_script", McpToolGroup.Write, "Delete a weapon, class or database NPC on the server; its script is backed up first.",
            Kind + "," + ScriptName + "," + Force + "," + Collect, Required("kind"), McpServerTools.DeleteScriptAsync, timeout: true, destructive: true),
        Tool("rc_write_file", McpToolGroup.Write, "Write a server file raw: text (kept in the file's own encoding), base64 bytes, or guarded edits of its current text. The file it replaces is backed up first; the server's answer is waited for.",
            "\"path\":{\"type\":\"string\",\"description\":\"folder/name, such as levels/start.nw.\"},\"text\":{\"type\":\"string\"},\"base64\":{\"type\":\"string\"}," + Encoding + "," + Edits + ",\"create\":{\"type\":\"boolean\",\"description\":\"false refuses to make a new file.\"}," + Force,
            Required("path"), McpServerTools.WriteFileAsync, timeout: true, destructive: true),
        Tool("rc_delete_file", McpToolGroup.Write, "Delete a server file; it is backed up first (a file over 64 MiB only with force).", "\"path\":{\"type\":\"string\"}," + Force + "," + Collect, Required("path"), McpServerTools.DeleteFileAsync, timeout: true, destructive: true),
        Tool("rc_move_file", McpToolGroup.Write, "Rename a server file and/or move it to another folder. A file of the new name is replaced only with overwrite: true, after a backup.",
            "\"path\":{\"type\":\"string\"},\"newName\":{\"type\":\"string\"},\"folder\":{\"type\":\"string\",\"description\":\"The destination folder.\"},\"overwrite\":{\"type\":\"boolean\"}," + Force + "," + Collect, Required("path"), McpServerTools.MoveFileAsync, timeout: true, destructive: true),
        Tool("rc_copy", McpToolGroup.Write, "Copy scripts and files from one connected server (from) to another (server, the destination) without passing them through the client: weapons, classes and NPC scripts (NPCs must exist on the destination), and files by path or wildcard (levels/*.nw) or whole folders (images/). What it replaces is backed up. Combine with rc_find_missing_files to bring over the images and animations the copied scripts need.",
            "\"from\":{\"type\":\"string\",\"description\":\"The source server.\"},\"scripts\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" + Kind + "," + ScriptName + ",\"newName\":{\"type\":\"string\"},\"toId\":{\"type\":\"integer\"}},\"required\":[\"kind\"]}},\"weapons\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"classes\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"files\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"folder\":{\"type\":\"string\",\"description\":\"Destination folder for every file (default: the same folder).\"},\"overwrite\":{\"type\":\"boolean\",\"description\":\"false keeps what exists on the destination.\"}," + Force + "," + Collect,
            Required("from"), McpServerTools.CopyAsync, timeout: true, destructive: true),
        Tool("rc_export", McpToolGroup.Write, "Put scripts and/or files of a server into a local folder (inside mcpfileroots) so they can be read, searched and edited with local tools: weapons/, classes/, npcs/ as .gs2 files, files/ in their server folders, and rc-export.json. rc_import sends back what changed.",
            "\"localFolder\":{\"type\":\"string\",\"description\":\"Absolute inside a root, or relative to the first root; default the server's name.\"},\"scripts\":{\"type\":\"boolean\",\"description\":\"Export scripts (default true unless files are given).\"},\"kinds\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"weapon\",\"class\",\"npc\"]}},\"names\":{\"type\":\"string\",\"description\":\"Only scripts whose name matches.\"},\"files\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Paths, wildcards (levels/*.nw) or folders (images/).\"}",
            None, McpServerTools.ExportAsync, timeout: true),
        Tool("rc_import", McpToolGroup.Write, "Send the scripts and files of a local folder made by rc_export (and new .gs2 files in weapons/, classes/, npcs/ <id>_name.gs2, and files under files/<folder>/) to a server: by default only what changed since the export when it is the same server, everything for another server. dryRun lists what would be sent.",
            "\"localFolder\":{\"type\":\"string\"},\"changedOnly\":{\"type\":\"boolean\"},\"dryRun\":{\"type\":\"boolean\"},\"kinds\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"weapon\",\"class\",\"npc\",\"file\"]}}," + Force + "," + Collect,
            Required("localFolder"), McpServerTools.ImportAsync, timeout: true, destructive: true),
        Tool("rc_restore_backup", McpToolGroup.Write, "Put back a copy MCP kept (rc_backups): a script, file, server text, NPC flags or player text, on its server or another one. What it replaces is backed up too. Player texts and server settings need mcpadmin, NPC flags mcpserver, as their own tools do.",
            "\"id\":{\"type\":\"string\"},\"server\":{\"type\":\"string\",\"description\":\"The server to restore on; default the backup's server.\"}," + Force + "," + Collect, Required("id"), McpServerTools.RestoreBackupAsync, timeout: true, destructive: true, session: McpSessionUse.None),

        // Login: connections.
        Tool("rc_nc_connect", McpToolGroup.Login, "Connect (or with connect: false disconnect) the NC, the NPC-Control connection that scripts, NPCs and levels need.",
            "\"connect\":{\"type\":\"boolean\"}", None, McpServerTools.NcConnectAsync, timeout: true),

        // Server: game actions.
        Tool("rc_command", McpToolGroup.Server, "Send a line (or lines) as RC chat, which runs the server's commands (rc_server_commands lists them; on Alpaxis for example /stats, /playerinfo, /find, /updatelevelall, /reloadweapons, /refreshfilesystem, /savenpcs, /npcstart, /npcshutdown, /restartserver), or as NC chat (connection nc), or type it into the program's chat field with the program's own commands (connection program; it always goes to the server, never to a selected IRC tab); returns what the server answered meanwhile.",
            "\"text\":{\"type\":\"string\"},\"connection\":{\"type\":\"string\",\"enum\":[\"rc\",\"nc\",\"program\"]}," + Collect, Required("text"), McpServerTools.CommandAsync, destructive: true),
        Tool("rc_send_message", McpToolGroup.Server, "Send a private message (to one player, or a mass message to several), an admin message (to players or all), or a to-all message.",
            "\"kind\":{\"type\":\"string\",\"enum\":[\"pm\",\"admin\",\"toall\"]},\"message\":{\"type\":\"string\"},\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"accounts\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"all\":{\"type\":\"boolean\",\"description\":\"Admin message to everyone.\"}", Required("message"), McpServerTools.SendMessageAsync),
        Tool("rc_irc", McpToolGroup.Server, "The network chat (IRC channels such as #graal): log in, join or leave a channel, or send a line to one; what the channels say arrives as irc events (rc_read_events).",
            "\"action\":{\"type\":\"string\",\"enum\":[\"login\",\"join\",\"part\",\"send\"]},\"channel\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"}," + Collect, Required("action"), McpServerTools.IrcAsync),
        Tool("rc_player_action", McpToolGroup.Server, "Warp a player of this server to a level and position, or disconnect them.",
            "\"action\":{\"type\":\"string\",\"enum\":[\"warp\",\"disconnect\"]},\"account\":{\"type\":\"string\"},\"id\":{\"type\":\"integer\"},\"level\":{\"type\":\"string\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"reason\":{\"type\":\"string\"}", Required("action"), McpServerTools.PlayerActionAsync, destructive: true),
        Tool("rc_npc_action", McpToolGroup.Server, "Database NPCs: create one (with an optional script, which also needs mcpwrite), reset, warp, or write its flags (the whole text, edits, or set/remove of keys). Delete one with rc_delete_script.",
            "\"action\":{\"type\":\"string\",\"enum\":[\"create\",\"reset\",\"warp\",\"write_flags\"]},\"id\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"},\"type\":{\"type\":\"string\",\"description\":\"create: OBJECT (default), ANIMAL, POLICE, HORSE, BOAT, HOUSEC.\"},\"scripter\":{\"type\":\"string\"},\"level\":{\"type\":\"string\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"script\":{\"type\":\"string\"},\"text\":{\"type\":\"string\",\"description\":\"write_flags: every flag, one per line.\"},\"set\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}},\"remove\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," + Edits + "," + Force + "," + Collect,
            Required("action"), McpServerTools.NpcActionAsync, timeout: true, destructive: true),
        Tool("rc_update_levels", McpToolGroup.Server, "Make the server reload levels (after a level file changed).",
            "\"levels\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," + Collect, Required("levels"), McpServerTools.UpdateLevelsAsync),

        // Windows: show the user.
        Tool("rc_open_script_editor", McpToolGroup.Windows, "Open the program's script editor of a weapon, class or NPC so the user sees it.",
            Kind + "," + ScriptName, Required("kind"), McpServerTools.OpenScriptEditorAsync),

        // Admin: accounts, bans, server settings, raw packets.
        Tool("rc_write_player_info", McpToolGroup.Admin, "Change a player's account, rights, attributes, comments or profile with the editor text rc_get_player_info returned (edit it and send it back); create: true with part account makes a new account; reset: true with part attributes resets the player. The old text is backed up first.",
            "\"account\":{\"type\":\"string\"},\"part\":{\"type\":\"string\",\"enum\":[\"account\",\"rights\",\"attributes\",\"comments\",\"profile\"]},\"text\":{\"type\":\"string\"},\"create\":{\"type\":\"boolean\"},\"reset\":{\"type\":\"boolean\"}," + Force + "," + Collect,
            Required("account", "part"), McpServerTools.WritePlayerInfoAsync, timeout: true, destructive: true),
        Tool("rc_set_ban", McpToolGroup.Admin, "Ban or unban an account or computer id (pc:<id>): on this server (world local) or on every server of the network (world all, which the user is always asked about), with a ban type (rc_list_ban_types) and its length, a length in seconds, or no end.",
            "\"target\":{\"type\":\"string\"},\"banned\":{\"type\":\"boolean\",\"description\":\"false lifts the ban.\"},\"world\":{\"type\":\"string\",\"enum\":[\"local\",\"all\"]},\"type\":{\"type\":\"string\"},\"releaseSeconds\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"The ban's length from now (0: no end); left out: the type's length.\"},\"noEnd\":{\"type\":\"boolean\"},\"reason\":{\"type\":\"string\"},\"legacy\":{\"type\":\"boolean\",\"description\":\"Use the old ban packet.\"}," + Collect,
            Required("target"), McpServerTools.SetBanAsync, destructive: true),
        Tool("rc_write_server_text", McpToolGroup.Admin, "Change the server options, server flags or folder configuration: the whole text, guarded edits, or (options and flags) set keys to values and remove keys. The old text is backed up first.",
            "\"kind\":{\"type\":\"string\",\"enum\":[\"options\",\"flags\",\"folderconfig\"]},\"text\":{\"type\":\"string\"},\"set\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}},\"remove\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," + Edits + "," + Force + "," + Collect,
            Required("kind"), McpServerTools.WriteServerTextAsync, timeout: true, destructive: true),
        Tool("rc_send_raw_packet", McpToolGroup.Admin, "Send any packet on the RC or NC connection (for what no other tool does): the packet id and its payload as text (Windows-1252), hex or base64.",
            "\"connection\":{\"type\":\"string\",\"enum\":[\"rc\",\"nc\"]},\"packetId\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":223},\"text\":{\"type\":\"string\"},\"hex\":{\"type\":\"string\"},\"base64\":{\"type\":\"string\"}," + Collect,
            Required("packetId"), McpServerTools.SendRawPacketAsync, destructive: true),
    };

    private static string[] None => Array.Empty<string>();

    private static string[] Required(params string[] names) => names;

    private static McpToolDefinition Tool(string name, McpToolGroup group, string description, string properties, string[] required, Func<McpToolContext, Task<string>> run,
        bool timeout = false, bool destructive = false, McpSessionUse session = McpSessionUse.Required, int defaultTimeout = 20)
    {
        var parts = new List<string>();
        if (properties.Length > 0) parts.Add(properties);
        if (session != McpSessionUse.None) parts.Add(Server);
        if (timeout) parts.Add(Timeout);
        string schema = "{\"type\":\"object\",\"properties\":{" + string.Join(",", parts) + "}" + (required.Length > 0 ? ",\"required\":[" + string.Join(",", required.Select(item => "\"" + item + "\"")) + "]" : string.Empty) + "}";
        bool readOnly = group == McpToolGroup.Read;
        string json = JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("description", description);
            writer.WritePropertyName("inputSchema");
            using (JsonDocument document = JsonDocument.Parse(schema)) document.RootElement.WriteTo(writer);
            writer.WriteStartObject("annotations");
            writer.WriteBoolean("readOnlyHint", readOnly);
            writer.WriteBoolean("destructiveHint", !readOnly && destructive);
            writer.WriteBoolean("openWorldHint", session != McpSessionUse.None || group != McpToolGroup.Read);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        return new McpToolDefinition(name, group, json, run, session, defaultTimeout);
    }
}
