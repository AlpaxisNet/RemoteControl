namespace RemoteControl.Core;

/// <summary>The keys of <c>control2config.txt</c>, as they appear in <see cref="OptionsChangedEventArgs.Keys"/>.</summary>
public static class OptionKeys
{
    /// <summary>The key of <see cref="RcOptions.Nickname"/>.</summary>
    public const string Nickname = "nickname";

    /// <summary>The key of <see cref="RcOptions.NoMassMessages"/>.</summary>
    public const string NoMassMessages = "nomassmessages";

    /// <summary>The key of <see cref="RcOptions.NoMassIfClientOn"/>.</summary>
    public const string NoMassIfClientOn = "nomassifclienton";

    /// <summary>The key of <see cref="RcOptions.NoHtmlInPms"/>.</summary>
    public const string NoHtmlInPms = "nohtmlinpms";

    /// <summary>The key of <see cref="RcOptions.NoHtmlImages"/>.</summary>
    public const string NoHtmlImages = "nohtmlimages";

    /// <summary>The key of <see cref="RcOptions.AttachAway"/>.</summary>
    public const string AttachAway = "attachaway";

    /// <summary>The key of <see cref="RcOptions.AfkEnabled"/>.</summary>
    public const string AfkEnabled = "afkenabled";

    /// <summary>The key of <see cref="RcOptions.AfkTimeout"/>.</summary>
    public const string AfkTimeout = "afktimeout";

    /// <summary>The key of <see cref="RcOptions.GlobalHotkey"/>.</summary>
    public const string GlobalHotkey = "globalhotkey";

    /// <summary>The key of <see cref="RcOptions.LogRcChat"/>.</summary>
    public const string LogRcChat = "logrcchat";

    /// <summary>The key of <see cref="RcOptions.SeparateFindResults"/>.</summary>
    public const string SeparateFindResults = "separatefindresults";

    /// <summary>The key of <see cref="RcOptions.ChatLogFile"/>.</summary>
    public const string ChatLogFile = "chatlogfile";

    /// <summary>The key of <see cref="RcOptions.DownloadFolder"/>.</summary>
    public const string DownloadFolder = "downloadfolder";

    /// <summary>The key of <see cref="RcOptions.ExternalEditorWorkspace"/>.</summary>
    public const string ExternalEditorWorkspace = "externaleditorworkspace";

    /// <summary>The key of <see cref="RcOptions.ExternalEditorCommand"/>.</summary>
    public const string ExternalEditorCommand = "externaleditorcommand";

    /// <summary>The key of <see cref="RcOptions.ExternalEditorScope"/>.</summary>
    public const string ExternalEditorScope = "externaleditorscope";

    /// <summary>The key of <see cref="RcOptions.ModernFileBrowser"/>.</summary>
    public const string ModernFileBrowser = "modernfilebrowser";

    /// <summary>The key of <see cref="RcOptions.FileBrowserHoverPreview"/>.</summary>
    public const string FileBrowserHoverPreview = "filebrowserhoverpreview";

    /// <summary>The key of <see cref="RcOptions.FileBrowserThumbnails"/>.</summary>
    public const string FileBrowserThumbnails = "filebrowserthumbnails";

    /// <summary>The key of <see cref="RcOptions.ExtensionsEnabled"/>.</summary>
    public const string ExtensionsEnabled = "extensionsenabled";

    /// <summary>The key of <see cref="RcOptions.SyncEnabled"/>.</summary>
    public const string SyncEnabled = "syncenabled";

    /// <summary>The key of <see cref="RcOptions.LevelListEnabled"/>.</summary>
    public const string LevelListEnabled = "levellistenabled";

    /// <summary>The key of <see cref="RcOptions.UseNewBanType"/>.</summary>
    public const string UseNewBanType = "usenewbantype";

    /// <summary>The key of <see cref="RcOptions.AutoReconnectNc"/>.</summary>
    public const string AutoReconnectNc = "autoreconnectnc";

    /// <summary>The key of <see cref="RcOptions.DontSavePassword"/>.</summary>
    public const string DontSavePassword = "dontsavepassword";

    /// <summary>The key of <see cref="RcOptions.GraphicalMenu"/>.</summary>
    public const string GraphicalMenu = "graphicalmenu";

    /// <summary>The key of <see cref="RcOptions.DarkMode"/>.</summary>
    public const string DarkMode = "darkmode";

    /// <summary>The key of <see cref="RcOptions.Theme"/>.</summary>
    public const string Theme = "theme";

    /// <summary>The key of <see cref="RcOptions.SyntaxTheme"/>.</summary>
    public const string SyntaxTheme = "syntaxtheme";

    /// <summary>The key of <see cref="RcOptions.SyncSyntaxTheme"/>.</summary>
    public const string SyncSyntaxTheme = "syncsyntaxtheme";

    /// <summary>The key of <see cref="RcOptions.SyncColors"/>.</summary>
    public const string SyncColors = "synccolors";

    /// <summary>The key of <see cref="RcOptions.RoundedCorners"/>.</summary>
    public const string RoundedCorners = "roundedcorners";

    /// <summary>The key of <see cref="RcOptions.ChatFontSize"/>.</summary>
    public const string ChatFontSize = "chatfontsize";

    /// <summary>The key of <see cref="RcOptions.ChatFontFamily"/>.</summary>
    public const string ChatFontFamily = "chatfontfamily";

    /// <summary>The key of <see cref="RcOptions.GlobalPms"/>.</summary>
    public const string GlobalPms = "globalpms";

    /// <summary>The key of <see cref="RcOptions.BuddyTracking"/>.</summary>
    public const string BuddyTracking = "buddytracking";

    /// <summary>The key of <see cref="RcOptions.ShowBuddies"/>.</summary>
    public const string ShowBuddies = "showbuddies";

    /// <summary>The key of <see cref="RcOptions.SyntaxHighlighting"/>.</summary>
    public const string SyntaxHighlighting = "syntaxhighlighting";

    /// <summary>The key of <see cref="RcOptions.AutoIndenting"/>.</summary>
    public const string AutoIndenting = "autoindenting";

    /// <summary>The key of <see cref="RcOptions.SmartHomeEnd"/>.</summary>
    public const string SmartHomeEnd = "smarthomeend";

    /// <summary>The key of <see cref="RcOptions.ShowBrackets"/>.</summary>
    public const string ShowBrackets = "showbrackets";

    /// <summary>The key of <see cref="RcOptions.ShowLineNumbers"/>.</summary>
    public const string ShowLineNumbers = "showlinenumbers";

    /// <summary>The key of <see cref="RcOptions.Minimap"/>.</summary>
    public const string Minimap = "minimap";

    /// <summary>The key of <see cref="RcOptions.Lsp"/>.</summary>
    public const string Lsp = "lsp";

    /// <summary>The key of <see cref="RcOptions.ScriptDiagnostics"/>.</summary>
    public const string ScriptDiagnostics = "scriptdiagnostics";

    /// <summary>The key of <see cref="RcOptions.AutocompleteSource"/>.</summary>
    public const string AutocompleteSource = "autocompletesource";

    /// <summary>The key of <see cref="RcOptions.SeparateNc"/>.</summary>
    public const string SeparateNc = "separatenc";

    /// <summary>The key of <see cref="RcOptions.RcTimestamps"/>.</summary>
    public const string RcTimestamps = "rctimestamps";

    /// <summary>The key of <see cref="RcOptions.NewPmAlerts"/>.</summary>
    public const string NewPmAlerts = "newpmalerts";

    /// <summary>The key of <see cref="RcOptions.NotificationSounds"/>.</summary>
    public const string NotificationSounds = "notificationsounds";

    /// <summary>The key of <see cref="RcOptions.ScriptTabWidth"/>.</summary>
    public const string ScriptTabWidth = "scripttabwidth";

    /// <summary>The key of <see cref="RcOptions.ScriptUseTabs"/>.</summary>
    public const string ScriptUseTabs = "scriptusetabs";

    /// <summary>The key of <see cref="RcOptions.ScriptFontSize"/>.</summary>
    public const string ScriptFontSize = "scriptfontsize";

    /// <summary>The key of <see cref="RcOptions.ScriptFontFamily"/>.</summary>
    public const string ScriptFontFamily = "scriptfontfamily";

    /// <summary>The key of <see cref="RcOptions.FormatIndentWidth"/>.</summary>
    public const string FormatIndentWidth = "formatindentwidth";

    /// <summary>The key of <see cref="RcOptions.FormatUseTabs"/>.</summary>
    public const string FormatUseTabs = "formatusetabs";

    /// <summary>The key of <see cref="RcOptions.FormatTrimTrailing"/>.</summary>
    public const string FormatTrimTrailing = "formattrimtrailing";

    /// <summary>The key of <see cref="RcOptions.RemoveLineComments"/>.</summary>
    public const string RemoveLineComments = "removelinecomments";

    /// <summary>The key of <see cref="RcOptions.RemoveBlockComments"/>.</summary>
    public const string RemoveBlockComments = "removeblockcomments";

    /// <summary>The key of <see cref="RcOptions.PreserveClientside"/>.</summary>
    public const string PreserveClientside = "preserveclientside";

    /// <summary>The key of <see cref="RcOptions.WebBrowsers"/>.</summary>
    public const string WebBrowsers = "webbrowsers";

    /// <summary>The key of <see cref="RcOptions.Background"/>.</summary>
    public const string Background = "background";

    /// <summary>The key of <see cref="RcOptions.BackgroundTint"/>.</summary>
    public const string BackgroundTint = "backgroundtint";

    /// <summary>The key of <see cref="RcOptions.SyncBackgroundTint"/>.</summary>
    public const string SyncBackgroundTint = "syncbackgroundtint";

    /// <summary>The key of <see cref="RcOptions.BackgroundTintSolid"/>.</summary>
    public const string BackgroundTintSolid = "backgroundtintsolid";

    /// <summary>The key of <see cref="RcOptions.TimestampFormat"/>.</summary>
    public const string TimestampFormat = "timestampformat";

    /// <summary>The key of <see cref="RcOptions.ColorEdit"/>.</summary>
    public const string ColorEdit = "coloredit";

    /// <summary>The key of <see cref="RcOptions.ColorEditBack"/>.</summary>
    public const string ColorEditBack = "coloreditback";

    /// <summary>The key of <see cref="RcOptions.ColorChat"/>.</summary>
    public const string ColorChat = "colorchat";

    /// <summary>The key of <see cref="RcOptions.ColorChatBack"/>.</summary>
    public const string ColorChatBack = "colorchatback";

    /// <summary>The key of <see cref="RcOptions.ColorChatBold"/>.</summary>
    public const string ColorChatBold = "colorchatbold";

    /// <summary>The key of <see cref="RcOptions.ColorLabel"/>.</summary>
    public const string ColorLabel = "colorlabel";

    /// <summary>The key of <see cref="RcOptions.ColorLabelBack"/>.</summary>
    public const string ColorLabelBack = "colorlabelback";

    /// <summary>The key of <see cref="RcOptions.ColorLink"/>.</summary>
    public const string ColorLink = "colorlink";

    /// <summary>The key of <see cref="RcOptions.ColorAlert"/>.</summary>
    public const string ColorAlert = "coloralert";

    /// <summary>The key of <see cref="RcOptions.LabelServers"/>.</summary>
    public const string LabelServers = "labelservers";

    /// <summary>The key of <see cref="RcOptions.LabelPlayers"/>.</summary>
    public const string LabelPlayers = "labelplayers";

    /// <summary>The key of <see cref="RcOptions.LabelNpcServer"/>.</summary>
    public const string LabelNpcServer = "labelnpcserver";

    /// <summary>The key of <see cref="RcOptions.McpEnabled"/>.</summary>
    public const string McpEnabled = "mcpenabled";

    /// <summary>The key of <see cref="RcOptions.McpRead"/>.</summary>
    public const string McpRead = "mcpread";

    /// <summary>The key of <see cref="RcOptions.McpWrite"/>.</summary>
    public const string McpWrite = "mcpwrite";

    /// <summary>The key of <see cref="RcOptions.McpAdmin"/>.</summary>
    public const string McpAdmin = "mcpadmin";

    /// <summary>The key of <see cref="RcOptions.McpServer"/>.</summary>
    public const string McpServer = "mcpserver";

    /// <summary>The key of <see cref="RcOptions.McpLogin"/>.</summary>
    public const string McpLogin = "mcplogin";

    /// <summary>The key of <see cref="RcOptions.McpWindows"/>.</summary>
    public const string McpWindows = "mcpwindows";

    /// <summary>The key of <see cref="RcOptions.McpFullControl"/>.</summary>
    public const string McpFullControl = "mcpfullcontrol";

    /// <summary>The key of <see cref="RcOptions.McpApprove"/>.</summary>
    public const string McpApprove = "mcpapprove";

    /// <summary>The key of <see cref="RcOptions.McpAudit"/>.</summary>
    public const string McpAudit = "mcpaudit";

    /// <summary>The key of <see cref="RcOptions.McpApproveWeapon"/>.</summary>
    public const string McpApproveWeapon = "mcpapproveweapon";

    /// <summary>The key of <see cref="RcOptions.McpApproveClass"/>.</summary>
    public const string McpApproveClass = "mcpapproveclass";

    /// <summary>The key of <see cref="RcOptions.McpApproveNpc"/>.</summary>
    public const string McpApproveNpc = "mcpapprovenpc";

    /// <summary>The key of <see cref="RcOptions.McpFileRoots"/>.</summary>
    public const string McpFileRoots = "mcpfileroots";

    /// <summary>The key of <see cref="RcOptions.McpServerScope"/>.</summary>
    public const string McpServerScope = "mcpserverscope";

    /// <summary>The key of <see cref="RcOptions.McpReferenceFolders"/>.</summary>
    public const string McpReferenceFolders = "mcpreferencefolders";

    /// <summary>The key of <see cref="RcOptions.ImportMissingFolder"/>.</summary>
    public const string ImportMissingFolder = "importmissingfolder";

    /// <summary>The key of the picture of a graphical-menu button.</summary>
    /// <param name="buttonName">One of <see cref="RcOptions.ButtonNames"/>.</param>
    /// <param name="pressed">The pressed picture instead of the normal one.</param>
    /// <returns><c>icon&lt;name&gt;</c> or <c>icon&lt;name&gt;pressed</c>.</returns>
    public static string ButtonImage(string buttonName, bool pressed = false) => "icon" + buttonName + (pressed ? "pressed" : string.Empty);
}
