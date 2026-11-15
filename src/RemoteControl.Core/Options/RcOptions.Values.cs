using System.Collections.Immutable;

namespace RemoteControl.Core;

public sealed partial record RcOptions
{
    /// <summary>The nickname sent to the server after login. Key <c>nickname</c>.</summary>
    public string Nickname { get => _nickname; init => _nickname = value ?? string.Empty; }
    private readonly string _nickname = "";

    /// <summary>Ignore mass private messages. Key <c>nomassmessages</c>.</summary>
    public bool NoMassMessages { get; init; } = false;

    /// <summary>Ignore mass private messages while the own account has no game client connected. Key <c>nomassifclienton</c>.</summary>
    public bool NoMassIfClientOn { get; init; } = false;

    /// <summary>Show private messages without HTML formatting. Key <c>nohtmlinpms</c>.</summary>
    public bool NoHtmlInPms { get; init; } = false;

    /// <summary>Do not load images referenced by HTML in private messages. Key <c>nohtmlimages</c>.</summary>
    public bool NoHtmlImages { get; init; } = false;

    /// <summary>Go in away mode while the player list is closed. Key <c>attachaway</c>.</summary>
    public bool AttachAway { get; init; } = true;

    /// <summary>Set the nickname to away when the user is inactive. Key <c>afkenabled</c>.</summary>
    public bool AfkEnabled { get; init; } = false;

    /// <summary>Minutes of inactivity before the away nickname is set (1 to 1440). Key <c>afktimeout</c>.</summary>
    public int AfkTimeout { get => _afkTimeout; init => _afkTimeout = Math.Clamp(value, 1, 1440); }
    private readonly int _afkTimeout = 15;

    /// <summary>The global show/hide key combination, for example <c>Ctrl+Alt+r</c>; empty for none. Key <c>globalhotkey</c>.</summary>
    public string GlobalHotkey { get => _globalHotkey; init => _globalHotkey = value ?? string.Empty; }
    private readonly string _globalHotkey = "";

    /// <summary>Append the RC chat to <see cref="ChatLogFile"/>. Key <c>logrcchat</c>.</summary>
    public bool LogRcChat { get; init; } = false;

    /// <summary>Show find results in a tab of their own. Key <c>separatefindresults</c>.</summary>
    public bool SeparateFindResults { get; init; } = true;

    /// <summary>The RC chat log file; a relative path is resolved by <see cref="AppPaths.ResolveUserPath"/>. Key <c>chatlogfile</c>.</summary>
    public string ChatLogFile { get => _chatLogFile; init => _chatLogFile = value ?? string.Empty; }
    private readonly string _chatLogFile = "logs/log.txt";

    /// <summary>The folder downloads are written to; a relative path is resolved by <see cref="AppPaths.ResolveUserPath"/>. Key <c>downloadfolder</c>.</summary>
    public string DownloadFolder { get => _downloadFolder; init => _downloadFolder = value ?? string.Empty; }
    private readonly string _downloadFolder = "Downloads";

    /// <summary>The folder the external editor works in; a relative path is resolved by <see cref="AppPaths.ResolveUserPath"/>. Key <c>externaleditorworkspace</c>.</summary>
    public string ExternalEditorWorkspace { get => _externalEditorWorkspace; init => _externalEditorWorkspace = value ?? string.Empty; }
    private readonly string _externalEditorWorkspace = "ExternalEditor";

    /// <summary>The command that starts the external editor. Key <c>externaleditorcommand</c>.</summary>
    public string ExternalEditorCommand { get => _externalEditorCommand; init => _externalEditorCommand = value ?? string.Empty; }
    private readonly string _externalEditorCommand = "";

    /// <summary>What the external editor opens: <c>off</c>, <c>scripts</c> or <c>text</c>. Key <c>externaleditorscope</c>.</summary>
    public string ExternalEditorScope { get => _externalEditorScope; init => _externalEditorScope = value ?? string.Empty; }
    private readonly string _externalEditorScope = "off";

    /// <summary>Use the modern file browser. Key <c>modernfilebrowser</c>.</summary>
    public bool ModernFileBrowser { get; init; } = false;

    /// <summary>Show previews when hovering files in the file browser. Key <c>filebrowserhoverpreview</c>.</summary>
    public bool FileBrowserHoverPreview { get; init; } = true;

    /// <summary>Show thumbnails in the modern file browser. Key <c>filebrowserthumbnails</c>.</summary>
    public bool FileBrowserThumbnails { get; init; } = false;

    /// <summary>Enable extensions. Key <c>extensionsenabled</c>.</summary>
    public bool ExtensionsEnabled { get; init; } = false;

    /// <summary>Enable sync and Git backups. Key <c>syncenabled</c>.</summary>
    public bool SyncEnabled { get; init; } = false;

    /// <summary>Show the legacy level list button. Key <c>levellistenabled</c>.</summary>
    public bool LevelListEnabled { get; init; } = true;

    /// <summary>Use the new ban type in the ban window. Key <c>usenewbantype</c>.</summary>
    public bool UseNewBanType { get; init; } = true;

    /// <summary>Reconnect to the NPC-Control automatically. Key <c>autoreconnectnc</c>.</summary>
    public bool AutoReconnectNc { get; init; } = true;

    /// <summary>Do not store passwords of saved accounts. Key <c>dontsavepassword</c>.</summary>
    public bool DontSavePassword { get; init; } = false;

    /// <summary>Show the graphical button menu instead of the classic menu. Key <c>graphicalmenu</c>.</summary>
    public bool GraphicalMenu { get; init; } = true;

    /// <summary>Dark mode (derived from the theme: every theme but <c>light</c>). Key <c>darkmode</c>.</summary>
    public bool DarkMode { get; init; } = true;

    /// <summary>The user-interface theme id. Key <c>theme</c>.</summary>
    public string Theme { get => _theme; init => _theme = value ?? string.Empty; }
    private readonly string _theme = "catppuccin";

    /// <summary>The script editor syntax theme id. Key <c>syntaxtheme</c>.</summary>
    public string SyntaxTheme { get => _syntaxTheme; init => _syntaxTheme = value ?? string.Empty; }
    private readonly string _syntaxTheme = "catppuccin";

    /// <summary>Follow the user-interface theme with the syntax theme. Key <c>syncsyntaxtheme</c>.</summary>
    public bool SyncSyntaxTheme { get; init; } = true;

    /// <summary>Overwrite the chat colours with the theme palette when a theme is chosen. Key <c>synccolors</c>.</summary>
    public bool SyncColors { get; init; } = true;

    /// <summary>Round the window corners. Key <c>roundedcorners</c>.</summary>
    public bool RoundedCorners { get; init; } = true;

    /// <summary>The chat font size in points (1 to 1000). Key <c>chatfontsize</c>.</summary>
    public int ChatFontSize { get => _chatFontSize; init => _chatFontSize = Math.Clamp(value, 1, 1000); }
    private readonly int _chatFontSize = 9;

    /// <summary>The chat font family; empty becomes <c>Sans</c>. Key <c>chatfontfamily</c>.</summary>
    public string ChatFontFamily { get => _chatFontFamily; init => _chatFontFamily = string.IsNullOrEmpty(value) ? "Sans" : value; }
    private readonly string _chatFontFamily = "Sans";

    /// <summary>Allow global private messages (sent to the list server). Key <c>globalpms</c>.</summary>
    public bool GlobalPms { get; init; } = true;

    /// <summary>Show yourself on other buddy lists (sent to the list server). Key <c>buddytracking</c>.</summary>
    public bool BuddyTracking { get; init; } = true;

    /// <summary>Show buddies (sent to the list server). Key <c>showbuddies</c>.</summary>
    public bool ShowBuddies { get; init; } = false;

    /// <summary>Highlight script syntax. Key <c>syntaxhighlighting</c>.</summary>
    public bool SyntaxHighlighting { get; init; } = true;

    /// <summary>Indent new script lines automatically. Key <c>autoindenting</c>.</summary>
    public bool AutoIndenting { get; init; } = true;

    /// <summary>Smart Home and End keys in the script editor. Key <c>smarthomeend</c>.</summary>
    public bool SmartHomeEnd { get; init; } = true;

    /// <summary>Highlight matching brackets. Key <c>showbrackets</c>.</summary>
    public bool ShowBrackets { get; init; } = true;

    /// <summary>Show line numbers in the script editor. Key <c>showlinenumbers</c>.</summary>
    public bool ShowLineNumbers { get; init; } = true;

    /// <summary>Show the script editor minimap. Key <c>minimap</c>.</summary>
    public bool Minimap { get; init; } = false;

    /// <summary>Enable autocomplete and signature help. Key <c>lsp</c>.</summary>
    public bool Lsp { get; init; } = true;

    /// <summary>Enable script analysis. Key <c>scriptdiagnostics</c>.</summary>
    public bool ScriptDiagnostics { get; init; } = true;

    /// <summary>The autocomplete source, a URL or a path. Key <c>autocompletesource</c>.</summary>
    public string AutocompleteSource { get => _autocompleteSource; init => _autocompleteSource = value ?? string.Empty; }
    private readonly string _autocompleteSource = "https://api.gscript.dev/";

    /// <summary>Show the NC chat in a tab of its own. Key <c>separatenc</c>.</summary>
    public bool SeparateNc { get; init; } = false;

    /// <summary>Prefix RC chat lines with a timestamp formatted by <see cref="TimestampFormat"/>. Key <c>rctimestamps</c>.</summary>
    public bool RcTimestamps { get; init; } = true;

    /// <summary>Show new private message alerts in the RC chat. Key <c>newpmalerts</c>.</summary>
    public bool NewPmAlerts { get; init; } = true;

    /// <summary>Play sounds for alerts and private messages. Key <c>notificationsounds</c>.</summary>
    public bool NotificationSounds { get; init; } = true;

    /// <summary>The script editor tab width (1 to 1000). Key <c>scripttabwidth</c>.</summary>
    public int ScriptTabWidth { get => _scriptTabWidth; init => _scriptTabWidth = Math.Clamp(value, 1, 1000); }
    private readonly int _scriptTabWidth = 2;

    /// <summary>Indent scripts with tabs. Key <c>scriptusetabs</c>.</summary>
    public bool ScriptUseTabs { get; init; } = false;

    /// <summary>The script font size in points (1 to 1000). Key <c>scriptfontsize</c>.</summary>
    public int ScriptFontSize { get => _scriptFontSize; init => _scriptFontSize = Math.Clamp(value, 1, 1000); }
    private readonly int _scriptFontSize = 10;

    /// <summary>The script font family; empty becomes <c>Monospace</c>. Key <c>scriptfontfamily</c>.</summary>
    public string ScriptFontFamily { get => _scriptFontFamily; init => _scriptFontFamily = string.IsNullOrEmpty(value) ? "Monospace" : value; }
    private readonly string _scriptFontFamily = "Monospace";

    /// <summary>The formatter indent width (1 to 16). Key <c>formatindentwidth</c>.</summary>
    public int FormatIndentWidth { get => _formatIndentWidth; init => _formatIndentWidth = Math.Clamp(value, 1, 16); }
    private readonly int _formatIndentWidth = 2;

    /// <summary>Use tabs when formatting. Key <c>formatusetabs</c>.</summary>
    public bool FormatUseTabs { get; init; } = false;

    /// <summary>Trim trailing whitespace when formatting. Key <c>formattrimtrailing</c>.</summary>
    public bool FormatTrimTrailing { get; init; } = true;

    /// <summary>Remove line comments when formatting. Key <c>removelinecomments</c>.</summary>
    public bool RemoveLineComments { get; init; } = false;

    /// <summary>Remove block comments when formatting. Key <c>removeblockcomments</c>.</summary>
    public bool RemoveBlockComments { get; init; } = false;

    /// <summary>Preserve a standalone <c>//#CLIENTSIDE</c> line when formatting. Key <c>preserveclientside</c>.</summary>
    public bool PreserveClientside { get; init; } = true;

    /// <summary>Web browsers to try, stored as a comma list. Key <c>webbrowsers</c>.</summary>
    public ImmutableArray<string> WebBrowsers { get => _webBrowsers; init => _webBrowsers = value.IsDefault ? ImmutableArray<string>.Empty : value; }
    private readonly ImmutableArray<string> _webBrowsers = ImmutableArray.Create("firefox", "mozilla", "konqueror", "netscape");

    /// <summary>The background image, relative to the program's <c>images</c> folder or absolute. Key <c>background</c>.</summary>
    public string Background { get => _background; init => _background = value ?? string.Empty; }
    private readonly string _background = "rc_background.png";

    /// <summary>The background tint as <c>#rrggbbaa</c>. Key <c>backgroundtint</c>.</summary>
    public string BackgroundTint { get => _backgroundTint; init => _backgroundTint = value ?? string.Empty; }
    private readonly string _backgroundTint = "#00000000";

    /// <summary>Derive the background tint from the theme. Key <c>syncbackgroundtint</c>.</summary>
    public bool SyncBackgroundTint { get; init; } = false;

    /// <summary>Fill the background with the tint instead of the image. Key <c>backgroundtintsolid</c>.</summary>
    public bool BackgroundTintSolid { get; init; } = false;

    /// <summary>The strftime-style timestamp pattern; empty becomes <c>[%I:%M %p]</c>. Format with <see cref="Core.TimestampFormat.Format"/>. Key <c>timestampformat</c>.</summary>
    public string TimestampFormat { get => _timestampFormat; init => _timestampFormat = string.IsNullOrEmpty(value) ? "[%I:%M %p]" : value; }
    private readonly string _timestampFormat = "[%I:%M %p]";

    /// <summary>The chat entry text colour. Key <c>coloredit</c>.</summary>
    public string ColorEdit { get => _colorEdit; init => _colorEdit = value ?? string.Empty; }
    private readonly string _colorEdit = "#cdd6f4";

    /// <summary>The chat entry background colour. Key <c>coloreditback</c>.</summary>
    public string ColorEditBack { get => _colorEditBack; init => _colorEditBack = value ?? string.Empty; }
    private readonly string _colorEditBack = "#1e1e2e";

    /// <summary>The chat text colour. Key <c>colorchat</c>.</summary>
    public string ColorChat { get => _colorChat; init => _colorChat = value ?? string.Empty; }
    private readonly string _colorChat = "#cdd6f4";

    /// <summary>The chat background colour. Key <c>colorchatback</c>.</summary>
    public string ColorChatBack { get => _colorChatBack; init => _colorChatBack = value ?? string.Empty; }
    private readonly string _colorChatBack = "#1e1e2e";

    /// <summary>The colour of bold chat prefixes, timestamps and links. Key <c>colorchatbold</c>.</summary>
    public string ColorChatBold { get => _colorChatBold; init => _colorChatBold = value ?? string.Empty; }
    private readonly string _colorChatBold = "#cba6f7";

    /// <summary>The header label colour. Key <c>colorlabel</c>.</summary>
    public string ColorLabel { get => _colorLabel; init => _colorLabel = value ?? string.Empty; }
    private readonly string _colorLabel = "#cba6f7";

    /// <summary>The header label background colour. Key <c>colorlabelback</c>.</summary>
    public string ColorLabelBack { get => _colorLabelBack; init => _colorLabelBack = value ?? string.Empty; }
    private readonly string _colorLabelBack = "#1e1e2e";

    /// <summary>The link colour. Key <c>colorlink</c>.</summary>
    public string ColorLink { get => _colorLink; init => _colorLink = value ?? string.Empty; }
    private readonly string _colorLink = "#4ec9b0";

    /// <summary>The colour of <c>#ALERT</c> lines. Key <c>coloralert</c>.</summary>
    public string ColorAlert { get => _colorAlert; init => _colorAlert = value ?? string.Empty; }
    private readonly string _colorAlert = "#f48771";

    /// <summary>The server header label. Key <c>labelservers</c>.</summary>
    public string LabelServers { get => _labelServers; init => _labelServers = value ?? string.Empty; }
    private readonly string _labelServers = "Server:";

    /// <summary>The players header label. Key <c>labelplayers</c>.</summary>
    public string LabelPlayers { get => _labelPlayers; init => _labelPlayers = value ?? string.Empty; }
    private readonly string _labelPlayers = "Players:";

    /// <summary>The NPC-server header label. Key <c>labelnpcserver</c>.</summary>
    public string LabelNpcServer { get => _labelNpcServer; init => _labelNpcServer = value ?? string.Empty; }
    private readonly string _labelNpcServer = "";

    /// <summary>Enable the local MCP integration. Key <c>mcpenabled</c>.</summary>
    public bool McpEnabled { get; init; } = false;

    /// <summary>Allow read-only MCP tools. Key <c>mcpread</c>.</summary>
    public bool McpRead { get; init; } = true;

    /// <summary>Allow MCP file and script writes. Key <c>mcpwrite</c>.</summary>
    public bool McpWrite { get; init; } = false;

    /// <summary>Allow administrative MCP actions. Key <c>mcpadmin</c>.</summary>
    public bool McpAdmin { get; init; } = false;

    /// <summary>Allow MCP server and game mutations. Key <c>mcpserver</c>.</summary>
    public bool McpServer { get; init; } = false;

    /// <summary>Allow MCP login and connection control. Key <c>mcplogin</c>.</summary>
    public bool McpLogin { get; init; } = false;

    /// <summary>Allow MCP window and user-interface control. Key <c>mcpwindows</c>.</summary>
    public bool McpWindows { get; init; } = false;

    /// <summary>Allow full native RC control over MCP. Key <c>mcpfullcontrol</c>.</summary>
    public bool McpFullControl { get; init; } = false;

    /// <summary>Require approval for MCP mutations. Key <c>mcpapprove</c>.</summary>
    public bool McpApprove { get; init; } = true;

    /// <summary>Write the MCP audit log. Key <c>mcpaudit</c>.</summary>
    public bool McpAudit { get; init; } = true;

    /// <summary>Saved approval: weapon edits over MCP without prompting. Key <c>mcpapproveweapon</c>.</summary>
    public bool McpApproveWeapon { get; init; } = false;

    /// <summary>Saved approval: class edits over MCP without prompting. Key <c>mcpapproveclass</c>.</summary>
    public bool McpApproveClass { get; init; } = false;

    /// <summary>Saved approval: NPC edits over MCP without prompting. Key <c>mcpapprovenpc</c>.</summary>
    public bool McpApproveNpc { get; init; } = false;

    /// <summary>The file roots MCP tools may use. Key <c>mcpfileroots</c>.</summary>
    public string McpFileRoots { get => _mcpFileRoots; init => _mcpFileRoots = value ?? string.Empty; }
    private readonly string _mcpFileRoots = ".";

    /// <summary>The server scope MCP tools may use. Key <c>mcpserverscope</c>.</summary>
    public string McpServerScope { get => _mcpServerScope; init => _mcpServerScope = value ?? string.Empty; }
    private readonly string _mcpServerScope = "";

    /// <summary>The names of the 15 graphical-menu buttons, in the order of <see cref="ButtonImages"/>; the keys are <c>icon&lt;name&gt;</c> and <c>icon&lt;name&gt;pressed</c>.</summary>
    public static ImmutableArray<string> ButtonNames { get; } = ImmutableArray.Create("playerlist", "filebrowser", "accounts", "toalls", "options", "serverflags", "folderoptions", "serveroptions", "localnpcs", "classlist", "weaponlist", "npclist", "help", "levellist", "guiscripts");

    /// <summary>The default picture of each button, in the order of <see cref="ButtonNames"/>.</summary>
    public static ImmutableArray<string> DefaultButtonImages { get; } = ImmutableArray.Create("rc_playerlist_normal.png", "rc_filebrowser_normal.png", "rc_accounts_normal.png", "rc_toalls_normal.png", "rc_options_normal.png", "rc_serverflags_normal.png", "rc_folderoptions_normal.png", "rc_serveroptions_normal.png", "rc_localnpcs_normal.png", "rc_classlist_normal.png", "rc_weaponlist_normal.png", "rc_npclist_normal.png", "rc_help_normal.png", "rc_levellist_normal.png", "rc_guiscripts_normal.png");

    /// <summary>The default pressed picture of each button, in the order of <see cref="ButtonNames"/>.</summary>
    public static ImmutableArray<string> DefaultButtonImagesPressed { get; } = ImmutableArray.Create("rc_playerlist_normal.png", "rc_filebrowser_normal.png", "rc_accounts_normal.png", "rc_toalls_normal.png", "rc_options_normal.png", "rc_serverflags_normal.png", "rc_folderoptions_normal.png", "rc_serveroptions_normal.png", "rc_localnpcs_normal.png", "rc_classlist_normal.png", "rc_weaponlist_normal.png", "rc_npclist_normal.png", "rc_help_pressed.png", "rc_levellist_pressed.png", "rc_guiscripts_pressed.png");

    /// <summary>Builds the table of every key, in the order the file is written.</summary>
    private static ImmutableArray<OptionField> BuildFields()
    {
        var fields = ImmutableArray.CreateBuilder<OptionField>();
        fields.Add(OptionField.Text("nickname", o => o.Nickname, (o, v) => o with { Nickname = v }));
        fields.Add(OptionField.Flag("nomassmessages", o => o.NoMassMessages, (o, v) => o with { NoMassMessages = v }));
        fields.Add(OptionField.Flag("nomassifclienton", o => o.NoMassIfClientOn, (o, v) => o with { NoMassIfClientOn = v }));
        fields.Add(OptionField.Flag("nohtmlinpms", o => o.NoHtmlInPms, (o, v) => o with { NoHtmlInPms = v }));
        fields.Add(OptionField.Flag("nohtmlimages", o => o.NoHtmlImages, (o, v) => o with { NoHtmlImages = v }));
        fields.Add(OptionField.Flag("attachaway", o => o.AttachAway, (o, v) => o with { AttachAway = v }));
        fields.Add(OptionField.Flag("afkenabled", o => o.AfkEnabled, (o, v) => o with { AfkEnabled = v }));
        fields.Add(OptionField.Number("afktimeout", o => o.AfkTimeout, (o, v) => o with { AfkTimeout = v }));
        fields.Add(OptionField.Text("globalhotkey", o => o.GlobalHotkey, (o, v) => o with { GlobalHotkey = v }));
        fields.Add(OptionField.Flag("logrcchat", o => o.LogRcChat, (o, v) => o with { LogRcChat = v }));
        fields.Add(OptionField.Flag("separatefindresults", o => o.SeparateFindResults, (o, v) => o with { SeparateFindResults = v }));
        fields.Add(OptionField.Text("chatlogfile", o => o.ChatLogFile, (o, v) => o with { ChatLogFile = v }));
        fields.Add(OptionField.Text("downloadfolder", o => o.DownloadFolder, (o, v) => o with { DownloadFolder = v }));
        fields.Add(OptionField.Text("externaleditorworkspace", o => o.ExternalEditorWorkspace, (o, v) => o with { ExternalEditorWorkspace = v }));
        fields.Add(OptionField.Text("externaleditorcommand", o => o.ExternalEditorCommand, (o, v) => o with { ExternalEditorCommand = v }));
        fields.Add(OptionField.Text("externaleditorscope", o => o.ExternalEditorScope, (o, v) => o with { ExternalEditorScope = v }));
        fields.Add(OptionField.Flag("modernfilebrowser", o => o.ModernFileBrowser, (o, v) => o with { ModernFileBrowser = v }));
        fields.Add(OptionField.Flag("filebrowserhoverpreview", o => o.FileBrowserHoverPreview, (o, v) => o with { FileBrowserHoverPreview = v }));
        fields.Add(OptionField.Flag("filebrowserthumbnails", o => o.FileBrowserThumbnails, (o, v) => o with { FileBrowserThumbnails = v }));
        fields.Add(OptionField.Flag("extensionsenabled", o => o.ExtensionsEnabled, (o, v) => o with { ExtensionsEnabled = v }));
        fields.Add(OptionField.Flag("syncenabled", o => o.SyncEnabled, (o, v) => o with { SyncEnabled = v }));
        fields.Add(OptionField.Flag("levellistenabled", o => o.LevelListEnabled, (o, v) => o with { LevelListEnabled = v }));
        fields.Add(OptionField.Flag("usenewbantype", o => o.UseNewBanType, (o, v) => o with { UseNewBanType = v }));
        fields.Add(OptionField.Flag("autoreconnectnc", o => o.AutoReconnectNc, (o, v) => o with { AutoReconnectNc = v }));
        fields.Add(OptionField.Flag("dontsavepassword", o => o.DontSavePassword, (o, v) => o with { DontSavePassword = v }));
        fields.Add(OptionField.Flag("graphicalmenu", o => o.GraphicalMenu, (o, v) => o with { GraphicalMenu = v }));
        fields.Add(OptionField.Flag("darkmode", o => o.DarkMode, (o, v) => o with { DarkMode = v }));
        fields.Add(OptionField.Text("theme", o => o.Theme, (o, v) => o with { Theme = v }));
        fields.Add(OptionField.Text("syntaxtheme", o => o.SyntaxTheme, (o, v) => o with { SyntaxTheme = v }));
        fields.Add(OptionField.Flag("syncsyntaxtheme", o => o.SyncSyntaxTheme, (o, v) => o with { SyncSyntaxTheme = v }));
        fields.Add(OptionField.Flag("synccolors", o => o.SyncColors, (o, v) => o with { SyncColors = v }));
        fields.Add(OptionField.Flag("roundedcorners", o => o.RoundedCorners, (o, v) => o with { RoundedCorners = v }));
        fields.Add(OptionField.Number("chatfontsize", o => o.ChatFontSize, (o, v) => o with { ChatFontSize = v }));
        fields.Add(OptionField.Text("chatfontfamily", o => o.ChatFontFamily, (o, v) => o with { ChatFontFamily = v }));
        fields.Add(OptionField.Flag("globalpms", o => o.GlobalPms, (o, v) => o with { GlobalPms = v }));
        fields.Add(OptionField.Flag("buddytracking", o => o.BuddyTracking, (o, v) => o with { BuddyTracking = v }));
        fields.Add(OptionField.Flag("showbuddies", o => o.ShowBuddies, (o, v) => o with { ShowBuddies = v }));
        fields.Add(OptionField.Flag("syntaxhighlighting", o => o.SyntaxHighlighting, (o, v) => o with { SyntaxHighlighting = v }));
        fields.Add(OptionField.Flag("autoindenting", o => o.AutoIndenting, (o, v) => o with { AutoIndenting = v }));
        fields.Add(OptionField.Flag("smarthomeend", o => o.SmartHomeEnd, (o, v) => o with { SmartHomeEnd = v }));
        fields.Add(OptionField.Flag("showbrackets", o => o.ShowBrackets, (o, v) => o with { ShowBrackets = v }));
        fields.Add(OptionField.Flag("showlinenumbers", o => o.ShowLineNumbers, (o, v) => o with { ShowLineNumbers = v }));
        fields.Add(OptionField.Flag("minimap", o => o.Minimap, (o, v) => o with { Minimap = v }));
        fields.Add(OptionField.Flag("lsp", o => o.Lsp, (o, v) => o with { Lsp = v }));
        fields.Add(OptionField.Flag("scriptdiagnostics", o => o.ScriptDiagnostics, (o, v) => o with { ScriptDiagnostics = v }));
        fields.Add(OptionField.Text("autocompletesource", o => o.AutocompleteSource, (o, v) => o with { AutocompleteSource = v }));
        fields.Add(OptionField.Flag("separatenc", o => o.SeparateNc, (o, v) => o with { SeparateNc = v }));
        fields.Add(OptionField.Flag("rctimestamps", o => o.RcTimestamps, (o, v) => o with { RcTimestamps = v }));
        fields.Add(OptionField.Flag("newpmalerts", o => o.NewPmAlerts, (o, v) => o with { NewPmAlerts = v }));
        fields.Add(OptionField.Flag("notificationsounds", o => o.NotificationSounds, (o, v) => o with { NotificationSounds = v }));
        fields.Add(OptionField.Number("scripttabwidth", o => o.ScriptTabWidth, (o, v) => o with { ScriptTabWidth = v }));
        fields.Add(OptionField.Flag("scriptusetabs", o => o.ScriptUseTabs, (o, v) => o with { ScriptUseTabs = v }));
        fields.Add(OptionField.Number("scriptfontsize", o => o.ScriptFontSize, (o, v) => o with { ScriptFontSize = v }));
        fields.Add(OptionField.Text("scriptfontfamily", o => o.ScriptFontFamily, (o, v) => o with { ScriptFontFamily = v }));
        fields.Add(OptionField.Number("formatindentwidth", o => o.FormatIndentWidth, (o, v) => o with { FormatIndentWidth = v }));
        fields.Add(OptionField.Flag("formatusetabs", o => o.FormatUseTabs, (o, v) => o with { FormatUseTabs = v }));
        fields.Add(OptionField.Flag("formattrimtrailing", o => o.FormatTrimTrailing, (o, v) => o with { FormatTrimTrailing = v }));
        fields.Add(OptionField.Flag("removelinecomments", o => o.RemoveLineComments, (o, v) => o with { RemoveLineComments = v }));
        fields.Add(OptionField.Flag("removeblockcomments", o => o.RemoveBlockComments, (o, v) => o with { RemoveBlockComments = v }));
        fields.Add(OptionField.Flag("preserveclientside", o => o.PreserveClientside, (o, v) => o with { PreserveClientside = v }));
        fields.Add(OptionField.List("webbrowsers", o => o.WebBrowsers, (o, v) => o with { WebBrowsers = v }));
        fields.Add(OptionField.Text("background", o => o.Background, (o, v) => o with { Background = v }));
        fields.Add(OptionField.Text("backgroundtint", o => o.BackgroundTint, (o, v) => o with { BackgroundTint = v }));
        fields.Add(OptionField.Flag("syncbackgroundtint", o => o.SyncBackgroundTint, (o, v) => o with { SyncBackgroundTint = v }));
        fields.Add(OptionField.Flag("backgroundtintsolid", o => o.BackgroundTintSolid, (o, v) => o with { BackgroundTintSolid = v }));
        fields.Add(OptionField.Text("timestampformat", o => o.TimestampFormat, (o, v) => o with { TimestampFormat = v }));
        for (int index = 0; index < ButtonNames.Length; index++)
        {
            int slot = index;
            fields.Add(OptionField.Text("icon" + ButtonNames[slot], o => o.ButtonImages[slot], (o, v) => o with { ButtonImages = o.ButtonImages.SetItem(slot, v) }));
            fields.Add(OptionField.Text("icon" + ButtonNames[slot] + "pressed", o => o.ButtonImagesPressed[slot], (o, v) => o with { ButtonImagesPressed = o.ButtonImagesPressed.SetItem(slot, v) }));
        }
        fields.Add(OptionField.Text("coloredit", o => o.ColorEdit, (o, v) => o with { ColorEdit = v }));
        fields.Add(OptionField.Text("coloreditback", o => o.ColorEditBack, (o, v) => o with { ColorEditBack = v }));
        fields.Add(OptionField.Text("colorchat", o => o.ColorChat, (o, v) => o with { ColorChat = v }));
        fields.Add(OptionField.Text("colorchatback", o => o.ColorChatBack, (o, v) => o with { ColorChatBack = v }));
        fields.Add(OptionField.Text("colorchatbold", o => o.ColorChatBold, (o, v) => o with { ColorChatBold = v }));
        fields.Add(OptionField.Text("colorlabel", o => o.ColorLabel, (o, v) => o with { ColorLabel = v }));
        fields.Add(OptionField.Text("colorlabelback", o => o.ColorLabelBack, (o, v) => o with { ColorLabelBack = v }));
        fields.Add(OptionField.Text("colorlink", o => o.ColorLink, (o, v) => o with { ColorLink = v }));
        fields.Add(OptionField.Text("coloralert", o => o.ColorAlert, (o, v) => o with { ColorAlert = v }));
        fields.Add(OptionField.Text("labelservers", o => o.LabelServers, (o, v) => o with { LabelServers = v }));
        fields.Add(OptionField.Text("labelplayers", o => o.LabelPlayers, (o, v) => o with { LabelPlayers = v }));
        fields.Add(OptionField.Text("labelnpcserver", o => o.LabelNpcServer, (o, v) => o with { LabelNpcServer = v }));
        fields.Add(OptionField.Flag("mcpenabled", o => o.McpEnabled, (o, v) => o with { McpEnabled = v }));
        fields.Add(OptionField.Flag("mcpread", o => o.McpRead, (o, v) => o with { McpRead = v }));
        fields.Add(OptionField.Flag("mcpwrite", o => o.McpWrite, (o, v) => o with { McpWrite = v }));
        fields.Add(OptionField.Flag("mcpadmin", o => o.McpAdmin, (o, v) => o with { McpAdmin = v }));
        fields.Add(OptionField.Flag("mcpserver", o => o.McpServer, (o, v) => o with { McpServer = v }));
        fields.Add(OptionField.Flag("mcplogin", o => o.McpLogin, (o, v) => o with { McpLogin = v }));
        fields.Add(OptionField.Flag("mcpwindows", o => o.McpWindows, (o, v) => o with { McpWindows = v }));
        fields.Add(OptionField.Flag("mcpfullcontrol", o => o.McpFullControl, (o, v) => o with { McpFullControl = v }));
        fields.Add(OptionField.Flag("mcpapprove", o => o.McpApprove, (o, v) => o with { McpApprove = v }));
        fields.Add(OptionField.Flag("mcpaudit", o => o.McpAudit, (o, v) => o with { McpAudit = v }));
        fields.Add(OptionField.Flag("mcpapproveweapon", o => o.McpApproveWeapon, (o, v) => o with { McpApproveWeapon = v }));
        fields.Add(OptionField.Flag("mcpapproveclass", o => o.McpApproveClass, (o, v) => o with { McpApproveClass = v }));
        fields.Add(OptionField.Flag("mcpapprovenpc", o => o.McpApproveNpc, (o, v) => o with { McpApproveNpc = v }));
        fields.Add(OptionField.Text("mcpfileroots", o => o.McpFileRoots, (o, v) => o with { McpFileRoots = v }));
        fields.Add(OptionField.Text("mcpserverscope", o => o.McpServerScope, (o, v) => o with { McpServerScope = v }));
        return fields.ToImmutable();
    }
}
