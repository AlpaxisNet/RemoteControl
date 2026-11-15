namespace RemoteControl.Ui.Shell;

/// <summary>
/// The names under which the groups of windows register what they can open (<see cref="ToolRegistry"/>). A group never references
/// another group: the main window opens the player list with <c>session.OpenTool(ToolIds.PlayerList)</c>.
/// </summary>
public static class ToolIds
{
    /// <summary>The main window of a session (Ui.Session). Opened by the program when a session starts.</summary>
    public const string MainWindow = "main";

    /// <summary>The player list (Ui.Players). One per session.</summary>
    public const string PlayerList = "players";

    /// <summary>The accounts list (Ui.Players). One per session.</summary>
    public const string Accounts = "accounts";

    /// <summary>The toalls window (Ui.Session). One per session.</summary>
    public const string Toalls = "toalls";

    /// <summary>The RC options window (Ui.Session). One per session.</summary>
    public const string RcOptions = "options";

    /// <summary>The file browser (Ui.Files). One per session.</summary>
    public const string FileBrowser = "files";

    /// <summary>The sync and git backups window (Ui.Files). One per session.</summary>
    public const string Sync = "sync";

    /// <summary>The server options editor (Ui.Scripts). One per session.</summary>
    public const string ServerOptions = "serveroptions";

    /// <summary>The server flags editor (Ui.Scripts). One per session.</summary>
    public const string ServerFlags = "serverflags";

    /// <summary>The folder configuration editor (Ui.Scripts). One per session.</summary>
    public const string FolderConfig = "folderconfig";

    /// <summary>The NPC list (Ui.Scripts). One per session; needs the NC.</summary>
    public const string Npcs = "npcs";

    /// <summary>The class list (Ui.Scripts). One per session; needs the NC.</summary>
    public const string Classes = "classes";

    /// <summary>The weapon list (Ui.Scripts). One per session; needs the NC.</summary>
    public const string Weapons = "weapons";

    /// <summary>The level list (Ui.Scripts). One per session; needs the NC.</summary>
    public const string Levels = "levels";

    /// <summary>The level-NPC dump dialog (Ui.Session). Needs the NC.</summary>
    public const string LocalNpcs = "localnpcs";

    /// <summary>The extensions window (Ui.Extensions). One for the program.</summary>
    public const string Extensions = "extensions";

    /// <summary>
    /// A script editor (Ui.Scripts). The argument is a <see cref="ScriptRequest"/>: which weapon, class or NPC to open, or a text
    /// to edit that is no server script.
    /// </summary>
    public const string ScriptEditor = "scripteditor";

    /// <summary>A private-message window (Ui.Players). The argument is the player's id (<see cref="int"/>).</summary>
    public const string PrivateMessage = "pm";

    /// <summary>The debug information window (Ui.Session).</summary>
    public const string DebugInfo = "debuginfo";
}

/// <summary>What to open in a script editor (<see cref="ToolIds.ScriptEditor"/>).</summary>
/// <param name="Kind">What the text is.</param>
/// <param name="Name">The weapon's or class's name, the NPC's name, or the title of another text.</param>
/// <param name="NpcId">The NPC's id for <see cref="ScriptKind.Npc"/>, else 0.</param>
/// <param name="Text">The text for <see cref="ScriptKind.Text"/> (a file from the file browser, for example); null for a script, which is asked from the server.</param>
/// <param name="Save">For <see cref="ScriptKind.Text"/>: what "Save" does with the edited text; it returns whether the text was taken. Null makes the editor read-only.</param>
public sealed record ScriptRequest(ScriptKind Kind, string Name, int NpcId = 0, string? Text = null, Func<string, Task<bool>>? Save = null);

/// <summary>What a script editor edits.</summary>
public enum ScriptKind
{
    /// <summary>A weapon (GUI script).</summary>
    Weapon,

    /// <summary>A class.</summary>
    Class,

    /// <summary>A database NPC.</summary>
    Npc,

    /// <summary>A text that is no server script: the caller gives the text and what saving does.</summary>
    Text,
}
