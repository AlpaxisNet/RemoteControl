using Avalonia.Controls;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Scripts;

/// <summary>
/// The script group: the script editor (<see cref="ToolIds.ScriptEditor"/>), the weapon, class, NPC and level lists, the server
/// options, server flags and folder configuration editors, and the external editor. The program calls <see cref="Register"/> once and
/// <see cref="Start"/> when a session starts, so that <see cref="IScriptEditors"/> is offered before any editor is opened.
/// </summary>
public static class ScriptsFeature
{
    /// <summary>Registers every tool of the group.</summary>
    /// <param name="tools">The program's tool registry.</param>
    public static void Register(ToolRegistry tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        tools.Register(ToolIds.ScriptEditor, OpenEditor);
        tools.Register(ToolIds.Weapons, (context, _) => Single(context, ToolIds.Weapons, () => new ScriptListWindow(context, classes: false)));
        tools.Register(ToolIds.Classes, (context, _) => Single(context, ToolIds.Classes, () => new ScriptListWindow(context, classes: true)));
        tools.Register(ToolIds.Npcs, (context, _) => Single(context, ToolIds.Npcs, () => new NpcListWindow(context)));
        tools.Register(ToolIds.Levels, (context, _) => ScriptsSession.Open(context, new LevelListTarget()));
        tools.Register(ToolIds.ServerOptions, (context, _) => ScriptsSession.Open(context, new ServerTextTarget(ServerTextKind.ServerOptions)));
        tools.Register(ToolIds.ServerFlags, (context, _) => ScriptsSession.Open(context, new ServerTextTarget(ServerTextKind.ServerFlags)));
        tools.Register(ToolIds.FolderConfig, (context, _) => ScriptsSession.Open(context, new ServerTextTarget(ServerTextKind.FolderConfig)));
    }

    /// <summary>
    /// Starts the script group's part of a session: offers <see cref="IScriptEditors"/> and opens the editors that were open last time
    /// once the NC connects. Calling it again does nothing.
    /// </summary>
    /// <param name="context">The session.</param>
    public static void Start(SessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsEnded) return;
        ScriptsSession.Start(context);
    }

    private static Window? Single(SessionContext context, string key, Func<Window> create)
    {
        ScriptsSession.For(context);
        return context.Windows.ShowSingle(key, create);
    }

    private static Window? OpenEditor(SessionContext context, object? argument)
    {
        if (argument is not ScriptRequest request || string.IsNullOrEmpty(request.Name) && request.Kind != ScriptKind.Npc) return null;
        EditorTarget target = request.Kind switch
        {
            ScriptKind.Weapon => new WeaponTarget(request.Name),
            ScriptKind.Class => new ClassTarget(request.Name),
            ScriptKind.Npc => new NpcScriptTarget(request.NpcId, request.Name ?? string.Empty),
            _ => new TextTarget(request),
        };
        return ScriptsSession.Open(context, target);
    }
}
