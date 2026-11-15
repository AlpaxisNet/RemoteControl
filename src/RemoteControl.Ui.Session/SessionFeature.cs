using Avalonia.Controls;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Session;

/// <summary>
/// The Session group: the main window of a server session (<see cref="ToolIds.MainWindow"/>), the toalls window, the RC options window,
/// the level-NPC dump dialog and the debug information window.
/// </summary>
public static class SessionFeature
{
    /// <summary>Registers the group's tools.</summary>
    /// <param name="tools">The program's tool registry.</param>
    public static void Register(ToolRegistry tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        tools.Register(ToolIds.MainWindow, (context, _) => context.Windows.ShowSingle(ToolIds.MainWindow, () => new MainWindow(context)));
        tools.Register(ToolIds.Toalls, (context, _) => context.Windows.ShowSingle(ToolIds.Toalls, () => new ToallsWindow(context)));
        tools.Register(ToolIds.RcOptions, (context, _) => OpenOptions(context));
        tools.Register(ToolIds.LocalNpcs, (context, _) =>
            context.Session.IsConnected && context.Session.IsNcAuthenticated ? context.Windows.ShowSingle(ToolIds.LocalNpcs, () => new LocalNpcsDialog(context)) : null);
        tools.Register(ToolIds.DebugInfo, (context, _) => OpenDebugInfo(context));
    }

    private static Window? OpenOptions(SessionContext context)
    {
        bool existing = context.Windows.Find(ToolIds.RcOptions) is not null;
        Window? window = context.Windows.ShowSingle(ToolIds.RcOptions, () => new OptionsWindow(context));
        if (window is OptionsWindow options)
        {
            options.Open();
            if (existing) options.ShowAndActivate();
        }
        return window;
    }

    private static Window? OpenDebugInfo(SessionContext context)
    {
        bool existing = context.Windows.Find(ToolIds.DebugInfo) is not null;
        Window? window = context.Windows.ShowSingle(ToolIds.DebugInfo, () => new DebugInfoWindow(context));
        if (existing && window is DebugInfoWindow debug) debug.Refresh(true);
        return window;
    }
}
