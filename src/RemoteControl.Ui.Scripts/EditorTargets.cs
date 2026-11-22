using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Wire;
using RemoteControl.Ui.Shell;
using ProtocolKind = RemoteControl.Protocol.Session.ScriptKind;
using ShellKind = RemoteControl.Ui.Shell.ScriptKind;

namespace RemoteControl.Ui.Scripts;

/// <summary>What a save did.</summary>
/// <param name="Sent">Whether the text went out on a live connection (the protocol has no other confirmation).</param>
/// <param name="Message">Why not, for the window's status line.</param>
internal sealed record SaveOutcome(bool Sent, string? Message)
{
    public static SaveOutcome Ok { get; } = new(true, null);

    public static SaveOutcome NcDown { get; } = new(false, "Not saved: the NPC-Control (NC) is not connected. Your changes are kept.");

    public static SaveOutcome RcDown { get; } = new(false, "Not saved: the server is not connected. Your changes are kept.");

    public static SaveOutcome Refused(IRcSession session) =>
        new(false, "Not saved: the connection refused it" + (string.IsNullOrEmpty(session.LastError) ? "." : " (" + session.LastError + ").") + " Your changes are kept.");
}

/// <summary>
/// What an <see cref="EditorWindow"/> edits and how it talks to the server: the weapon, class and NPC scripts, the NPC flags and
/// attributes, the server texts, the level list and texts of other groups (<see cref="ScriptRequest"/>).
/// </summary>
internal abstract class EditorTarget
{
    /// <summary>The key of the window among the session's (<see cref="SessionWindows.ShowSingle"/>).</summary>
    public abstract string Key { get; }

    /// <summary>The window title without the server.</summary>
    public abstract string BaseTitle { get; }

    /// <summary>Whether the server's name is appended as " (server)" (otherwise " - server").</summary>
    public virtual bool ServerInParentheses => true;

    /// <summary>The kind for <see cref="IScriptEditors"/>, or null when the editor is not listed there.</summary>
    public virtual ShellKind? Kind => null;

    /// <summary>The name for <see cref="IScriptEditors"/>.</summary>
    public virtual string Name => BaseTitle;

    /// <summary>The editor mode.</summary>
    public abstract ScriptEditorMode Mode { get; }

    /// <summary>The default size.</summary>
    public virtual (double Width, double Height) Size => (700, 520);

    /// <summary>Whether the weapon's image field is shown.</summary>
    public virtual bool HasIcon => false;

    /// <summary>Whether extensions' buttons are shown.</summary>
    public virtual bool HasExtensionActions => false;

    /// <summary>The type in <c>script-windows.txt</c>, or null when the window's place is not remembered.</summary>
    public virtual string? RecordType => null;

    /// <summary>The backup type (<see cref="Core.EditorBackup"/>).</summary>
    public virtual string BackupType => string.Empty;

    /// <summary>The backup target name.</summary>
    public virtual string BackupTarget => string.Empty;

    /// <summary>Whether this is a script for the external editor scope (otherwise a text, scope <c>text</c> only); null: never external.</summary>
    public virtual bool? ExternalIsScript => null;

    /// <summary>The workspace category and file name.</summary>
    public virtual (string Category, string Name) External => (string.Empty, string.Empty);

    /// <summary>Whether the text cannot be changed.</summary>
    public virtual bool ReadOnly => false;

    /// <summary>The save button's text.</summary>
    public virtual string ApplyLabel => "Apply";

    /// <summary>Whether Go to line and Format are offered.</summary>
    public virtual bool HasFormat => true;

    /// <summary>Whether the text needs the NC.</summary>
    public virtual bool NeedsNc => true;

    /// <summary>The text when the caller gave one (no request is sent).</summary>
    public virtual string? GivenText => null;

    /// <summary>Asks the server for the text; null when nothing could be sent (and why).</summary>
    public abstract Task<string?> RequestAsync(IRcSession session);

    /// <summary>Subscribes to the answers: the callback gets the text and, for a weapon, its image.</summary>
    public abstract IDisposable? Subscribe(IRcSession session, Action<string, string?> received);

    /// <summary>Sends the text.</summary>
    public abstract Task<SaveOutcome> SaveAsync(SessionContext context, string text, string icon);

    /// <summary>Whether the NC (or the RC for server texts) is ready.</summary>
    public bool IsConnected(IRcSession session) => NeedsNc ? session.IsNcAuthenticated : session.IsAuthenticated;

    /// <summary>The text shown while nothing could be requested.</summary>
    public string NotConnectedText => NeedsNc ? "Not connected to the NPC-Control (NC). The text is loaded when the NC connects." : "Not connected to the server.";

    protected static IDisposable Unsubscribe(Action action) => new Unsubscriber(action);
}

/// <summary>A weapon (GUI script).</summary>
internal sealed class WeaponTarget(string name) : EditorTarget
{
    public override string Key => "script:weapon:" + name;
    public override string BaseTitle => "Weapon: " + name;
    public override ShellKind? Kind => ShellKind.Weapon;
    public override string Name => name;
    public override ScriptEditorMode Mode => ScriptEditorMode.Script;
    public override bool HasIcon => true;
    public override bool HasExtensionActions => true;
    public override string? RecordType => "weapon";
    public override string BackupType => "weapon";
    public override string BackupTarget => name;
    public override bool? ExternalIsScript => true;
    public override (string Category, string Name) External => ("weapons", name);

    public override async Task<string?> RequestAsync(IRcSession session) =>
        await session.RequestWeaponScriptAsync(name).ConfigureAwait(true) ? null : "The weapon could not be requested.";

    public override IDisposable? Subscribe(IRcSession session, Action<string, string?> received)
    {
        EventHandler<ScriptReceivedEventArgs> handler = (_, e) =>
        {
            if (e.Kind == ProtocolKind.Weapon && e.Name == name) received(e.Script, e.Image);
        };
        session.ScriptReceived += handler;
        return Unsubscribe(() => session.ScriptReceived -= handler);
    }

    public override async Task<SaveOutcome> SaveAsync(SessionContext context, string text, string icon)
    {
        IRcSession session = context.Session;
        if (!session.IsNcAuthenticated) return SaveOutcome.NcDown;
        return await session.UpdateWeaponAsync(name, icon, text).ConfigureAwait(true) ? SaveOutcome.Ok : SaveOutcome.Refused(session);
    }
}

/// <summary>A class; an empty class that does not exist yet is created on the first save with text (the server deletes empty classes).</summary>
internal sealed class ClassTarget(string name) : EditorTarget
{
    public override string Key => "script:class:" + name;
    public override string BaseTitle => "Class: " + name;
    public override ShellKind? Kind => ShellKind.Class;
    public override string Name => name;
    public override ScriptEditorMode Mode => ScriptEditorMode.Script;
    public override bool HasExtensionActions => true;
    public override string? RecordType => "class";
    public override string BackupType => "class";
    public override string BackupTarget => name;
    public override bool? ExternalIsScript => true;
    public override (string Category, string Name) External => ("classes", name);

    /// <summary>Whether the class is new (Add): nothing is requested.</summary>
    public bool IsNew { get; init; }

    public override string? GivenText => IsNew ? string.Empty : null;

    public override async Task<string?> RequestAsync(IRcSession session) =>
        await session.RequestClassScriptAsync(name).ConfigureAwait(true) ? null : "The class could not be requested.";

    public override IDisposable? Subscribe(IRcSession session, Action<string, string?> received)
    {
        EventHandler<ScriptReceivedEventArgs> handler = (_, e) =>
        {
            if (e.Kind == ProtocolKind.Class && e.Name == name) received(e.Script, null);
        };
        session.ScriptReceived += handler;
        return Unsubscribe(() => session.ScriptReceived -= handler);
    }

    public override async Task<SaveOutcome> SaveAsync(SessionContext context, string text, string icon)
    {
        IRcSession session = context.Session;
        if (!session.IsNcAuthenticated) return SaveOutcome.NcDown;
        bool exists = session.Classes.Any(item => item.Name == name);
        if (exists) return await session.UpdateClassAsync(name, text).ConfigureAwait(true) ? SaveOutcome.Ok : SaveOutcome.Refused(session);
        if (text.Length == 0) return SaveOutcome.Ok;
        return await session.AddClassAsync(name, text).ConfigureAwait(true) ? SaveOutcome.Ok : SaveOutcome.Refused(session);
    }
}

/// <summary>A database NPC's script.</summary>
internal sealed class NpcScriptTarget(int id, string name) : EditorTarget
{
    public override string Key => "script:npc:" + id;
    public override string BaseTitle => "Npcscript: " + name;
    public override ShellKind? Kind => ShellKind.Npc;
    public override string Name => name;
    public override ScriptEditorMode Mode => ScriptEditorMode.Script;
    public override string BackupType => "npcscript";
    public override string BackupTarget => name.Length > 0 ? "npc" + name : id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public override bool? ExternalIsScript => true;
    public override (string Category, string Name) External => ("npcs", id + "_" + (name.Length == 0 ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : name));

    public int Id => id;

    public override async Task<string?> RequestAsync(IRcSession session) =>
        await session.RequestNpcScriptAsync(id).ConfigureAwait(true) ? null : "The NPC script could not be requested.";

    public override IDisposable? Subscribe(IRcSession session, Action<string, string?> received)
    {
        EventHandler<ScriptReceivedEventArgs> handler = (_, e) =>
        {
            if (e.Kind == ProtocolKind.Npc && e.Id == id) received(e.Script, null);
        };
        session.ScriptReceived += handler;
        return Unsubscribe(() => session.ScriptReceived -= handler);
    }

    public override async Task<SaveOutcome> SaveAsync(SessionContext context, string text, string icon)
    {
        IRcSession session = context.Session;
        if (!session.IsNcAuthenticated) return SaveOutcome.NcDown;
        return await session.UpdateNpcScriptAsync(id, text).ConfigureAwait(true) ? SaveOutcome.Ok : SaveOutcome.Refused(session);
    }
}

/// <summary>A text of another group (a file from the file browser): the caller gives the text and what saving does.</summary>
internal sealed class TextTarget(ScriptRequest request) : EditorTarget
{
    public override string Key => "script:text:" + request.Name;
    public override string BaseTitle => request.Name;
    public override bool ServerInParentheses => false;
    public override ShellKind? Kind => ShellKind.Text;
    public override string Name => request.Name;
    public override ScriptEditorMode Mode => Scripting.HighlightingResources.LanguageForFile(request.Name) switch
    {
        Scripting.HighlightingLanguage.Graal => ScriptEditorMode.Script,
        Scripting.HighlightingLanguage.Ini => ScriptEditorMode.Configuration,
        _ => request.Name.EndsWith(".gs2", StringComparison.OrdinalIgnoreCase) ? ScriptEditorMode.Script : ScriptEditorMode.Plain,
    };
    public override bool ReadOnly => request.Save is null;
    public override string ApplyLabel => "Save";
    public override bool NeedsNc => false;
    public override string? GivenText => request.Text ?? string.Empty;

    public override Task<string?> RequestAsync(IRcSession session) => Task.FromResult<string?>(null);

    public override IDisposable? Subscribe(IRcSession session, Action<string, string?> received) => null;

    public override async Task<SaveOutcome> SaveAsync(SessionContext context, string text, string icon)
    {
        if (request.Save is null) return new SaveOutcome(false, "This text cannot be saved.");
        return await request.Save(text).ConfigureAwait(true) ? SaveOutcome.Ok : new SaveOutcome(false, "Not saved: the text was not taken. Your changes are kept.");
    }
}

/// <summary>An NPC's flags (opened only from the NPC list's "Edit Flags"; flags the sync engine pulls are ignored unless this window waits).</summary>
internal sealed class NpcFlagsTarget(int id, string name) : EditorTarget
{
    public override string Key => "npcflags:" + id;
    public override string BaseTitle => "NPC Flags: " + (name.Length == 0 ? "NPC " + id : name);
    public override ScriptEditorMode Mode => ScriptEditorMode.Configuration;
    public override (double Width, double Height) Size => (600, 460);
    public override string BackupType => "npcflags";
    public override string BackupTarget => id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public override bool? ExternalIsScript => false;
    public override (string Category, string Name) External => ("npcflags", "npc_" + id + "_flags.txt");

    public override async Task<string?> RequestAsync(IRcSession session) =>
        await session.RequestNpcFlagsAsync(id).ConfigureAwait(true) ? null : "The NPC flags could not be requested.";

    public override IDisposable? Subscribe(IRcSession session, Action<string, string?> received)
    {
        EventHandler<NpcTextEventArgs> handler = (_, e) =>
        {
            if (e.NpcId == id) received(e.Text, null);
        };
        session.NpcFlagsReceived += handler;
        return Unsubscribe(() => session.NpcFlagsReceived -= handler);
    }

    public override async Task<SaveOutcome> SaveAsync(SessionContext context, string text, string icon)
    {
        IRcSession session = context.Session;
        if (!session.IsNcAuthenticated) return SaveOutcome.NcDown;
        return await session.SetNpcFlagsAsync(id, text).ConfigureAwait(true) ? SaveOutcome.Ok : SaveOutcome.Refused(session);
    }
}

/// <summary>An NPC's attributes, read-only.</summary>
internal sealed class NpcAttributesTarget(int id) : EditorTarget
{
    public override string Key => "npcattributes:" + id;
    public override string BaseTitle => "Attributes of NPC " + id;
    public override bool ServerInParentheses => false;
    public override ScriptEditorMode Mode => ScriptEditorMode.Configuration;
    public override (double Width, double Height) Size => (500, 360);
    public override bool ReadOnly => true;
    public override bool HasFormat => false;

    public override async Task<string?> RequestAsync(IRcSession session) =>
        await session.RequestNpcAttributesAsync(id).ConfigureAwait(true) ? null : "The NPC attributes could not be requested.";

    public override IDisposable? Subscribe(IRcSession session, Action<string, string?> received)
    {
        EventHandler<NpcTextEventArgs> handler = (_, e) =>
        {
            if (e.NpcId == id) received(e.Text, null);
        };
        session.NpcAttributesReceived += handler;
        return Unsubscribe(() => session.NpcAttributesReceived -= handler);
    }

    public override Task<SaveOutcome> SaveAsync(SessionContext context, string text, string icon) => Task.FromResult(new SaveOutcome(false, "The attributes cannot be changed here."));
}

/// <summary>The kinds of server text.</summary>
internal enum ServerTextKind
{
    ServerOptions,
    ServerFlags,
    FolderConfig,
}

/// <summary>The server options, server flags or folder configuration (C++ <c>TServerTextEditor</c>).</summary>
internal sealed class ServerTextTarget(ServerTextKind kind) : EditorTarget
{
    public ServerTextKind TextKind => kind;
    public override string Key => kind switch { ServerTextKind.ServerOptions => ToolIds.ServerOptions, ServerTextKind.ServerFlags => ToolIds.ServerFlags, _ => ToolIds.FolderConfig };
    public override string BaseTitle => kind switch { ServerTextKind.ServerOptions => "Server Options", ServerTextKind.ServerFlags => "Server Flags", _ => "Folder Config" };
    public override bool ServerInParentheses => false;
    public override ScriptEditorMode Mode => ScriptEditorMode.Configuration;
    public override (double Width, double Height) Size => (600, 460);
    public override string BackupType => kind switch { ServerTextKind.ServerOptions => "serveroptions", ServerTextKind.ServerFlags => "serverflags", _ => "folderconfig" };
    public override bool? ExternalIsScript => false;
    public override (string Category, string Name) External => ("text", BackupType + ".txt");
    public override bool NeedsNc => false;

    public override async Task<string?> RequestAsync(IRcSession session)
    {
        bool sent = kind switch
        {
            ServerTextKind.ServerOptions => await session.RequestServerOptionsAsync().ConfigureAwait(true),
            ServerTextKind.ServerFlags => await session.RequestServerFlagsAsync().ConfigureAwait(true),
            _ => await session.RequestFolderConfigAsync().ConfigureAwait(true),
        };
        return sent ? null : "The text could not be requested.";
    }

    public override IDisposable? Subscribe(IRcSession session, Action<string, string?> received)
    {
        EventHandler<TextEventArgs> handler = (_, e) => received(e.Text, null);
        switch (kind)
        {
            case ServerTextKind.ServerOptions:
                session.ServerOptionsReceived += handler;
                return Unsubscribe(() => session.ServerOptionsReceived -= handler);
            case ServerTextKind.ServerFlags:
                session.ServerFlagsReceived += handler;
                return Unsubscribe(() => session.ServerFlagsReceived -= handler);
            default:
                session.FolderConfigReceived += handler;
                return Unsubscribe(() => session.FolderConfigReceived -= handler);
        }
    }

    public override async Task<SaveOutcome> SaveAsync(SessionContext context, string text, string icon)
    {
        IRcSession session = context.Session;
        if (!session.IsAuthenticated) return SaveOutcome.RcDown;
        bool sent = kind switch
        {
            ServerTextKind.ServerOptions => await session.UploadServerOptionsAsync(text).ConfigureAwait(true),
            ServerTextKind.ServerFlags => await session.UploadServerFlagsAsync(text).ConfigureAwait(true),
            _ => await session.UploadFolderConfigAsync(text).ConfigureAwait(true),
        };
        return sent ? SaveOutcome.Ok : SaveOutcome.Refused(session);
    }
}

/// <summary>The NC level list (C++ <c>TLevelList</c>): <c>PLI_NC_LEVELLISTGET</c> and <c>PLI_NC_LEVELLISTSET</c>.</summary>
internal sealed class LevelListTarget : EditorTarget
{
    /// <summary>PLI_NC_LEVELLISTSET.</summary>
    public const int LevelListSetPacket = 151;

    public override string Key => ToolIds.Levels;
    public override string BaseTitle => "Levels";
    public override bool ServerInParentheses => false;
    public override ScriptEditorMode Mode => ScriptEditorMode.Plain;
    public override (double Width, double Height) Size => (600, 460);
    public override bool HasFormat => false;

    /// <summary>The level list as the C++ showed it: carriage returns become line breaks, blank lines are dropped.</summary>
    public static string Normalize(string text)
    {
        string value = (text ?? string.Empty).Replace('\r', '\n');
        while (value.Contains("\n\n", StringComparison.Ordinal)) value = value.Replace("\n\n", "\n", StringComparison.Ordinal);
        return value;
    }

    public override async Task<string?> RequestAsync(IRcSession session) =>
        await session.RequestLevelListAsync().ConfigureAwait(true) ? null : "Unable to request the local level list.";

    public override IDisposable? Subscribe(IRcSession session, Action<string, string?> received)
    {
        EventHandler<TextEventArgs> handler = (_, e) => received(Normalize(e.Text), null);
        session.LevelListReceived += handler;
        return Unsubscribe(() => session.LevelListReceived -= handler);
    }

    public override async Task<SaveOutcome> SaveAsync(SessionContext context, string text, string icon)
    {
        IRcSession session = context.Session;
        if (!session.IsNcAuthenticated) return SaveOutcome.NcDown;
        // Comma text, as the C++ sent it, made so that a server that also reads GString lists cannot take it for one.
        byte[] payload;
        try
        {
            payload = RcText.GetBytes(LineListText.CommaText(Normalize(text).Trim('\n')));
        }
        catch (PacketTooLongException exception)
        {
            return new SaveOutcome(false, "Not saved: " + exception.Message + " Your changes are kept.");
        }
        return await session.SendNcPacketAsync(LevelListSetPacket, payload).ConfigureAwait(true) ? SaveOutcome.Ok : SaveOutcome.Refused(session);
    }
}
