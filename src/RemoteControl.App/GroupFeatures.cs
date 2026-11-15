using RemoteControl.Ui.Extensions;
using RemoteControl.Ui.Files;
using RemoteControl.Ui.Players;
using RemoteControl.Ui.Scripts;
using RemoteControl.Ui.Session;
using RemoteControl.Ui.Shell;

namespace RemoteControl.App;

/// <summary>The groups of windows the program is made of, and what each needs from it.</summary>
internal static class GroupFeatures
{
    /// <summary>Registers every group's tools (Session, Players, Scripts, Files, and Extensions with the program's host).</summary>
    /// <param name="tools">The registry.</param>
    /// <param name="extensions">The program's extensions.</param>
    public static void RegisterAll(ToolRegistry tools, ExtensionsHost extensions)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(extensions);
        SessionFeature.Register(tools);
        PlayersFeature.Register(tools);
        ScriptsFeature.Register(tools);
        FilesFeature.Register(tools);
        ExtensionsFeature.Register(tools, extensions);
    }

    /// <summary>Starts the groups' part of a new session: private messages, script editors and synchronisation.</summary>
    /// <param name="context">The session.</param>
    public static void StartSession(SessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        PlayersFeature.Start(context);
        ScriptsFeature.Start(context);
        FilesFeature.Start(context);
    }
}
