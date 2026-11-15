using Avalonia.Controls;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Extensions;

/// <summary>Registers the windows of the extensions group.</summary>
public static class ExtensionsFeature
{
    /// <summary>The title of the message shown when the extensions are switched off.</summary>
    internal const string SwitchedOffTitle = "Extensions";

    /// <summary>The message shown when the extensions are switched off.</summary>
    internal const string SwitchedOffMessage = "Extensions are switched off.\n\nTurn on \"Extensions\" in the RC options (General) to use them.";

    /// <summary>The button of that message that opens the RC options.</summary>
    internal const string OpenOptionsText = "Open RC Options";

    /// <summary>
    /// Registers <see cref="ToolIds.Extensions"/>: it shows the host's extensions window; with the extensions switched off it says
    /// so and offers to open the RC options of the session that asked.
    /// </summary>
    /// <param name="tools">The program's tool registry.</param>
    /// <param name="host">The program's extensions.</param>
    public static void Register(ToolRegistry tools, ExtensionsHost host)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(host);
        tools.Register(ToolIds.Extensions, (context, _) =>
        {
            Window? owner = context.Services.Get<ISessionChat>()?.Window;
            if (host.IsEnabled) return host.ShowWindow(owner);
            UiSafe.Fire(async () =>
            {
                bool open = await Dialogs.ConfirmAsync(SwitchedOffTitle, SwitchedOffMessage, owner, OpenOptionsText, "Close").ConfigureAwait(true);
                if (open && !context.IsEnded) context.OpenTool(ToolIds.RcOptions);
            }, "extensions switched off");
            return null;
        });
    }
}
