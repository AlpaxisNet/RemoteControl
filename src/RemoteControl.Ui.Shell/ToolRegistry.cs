using Avalonia.Controls;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Shell;

/// <summary>
/// What can be opened for a session, by name (<see cref="ToolIds"/>). Each group of windows registers its windows once when the
/// program starts; any group opens a window through <see cref="SessionContext.OpenTool"/>. Used on the UI thread only.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, Func<SessionContext, object?, Window?>> _tools = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers how a tool is opened. The function shows (or brings to the front) the window and returns it, or returns null when
    /// it showed nothing (a tool that needs the NC while there is none, for example). A later registration replaces an earlier one.
    /// </summary>
    /// <param name="toolId">The name, one of <see cref="ToolIds"/>.</param>
    /// <param name="open">Opens the tool for a session with the caller's argument.</param>
    public void Register(string toolId, Func<SessionContext, object?, Window?> open)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolId);
        ArgumentNullException.ThrowIfNull(open);
        _tools[toolId] = open;
    }

    /// <summary>Whether something is registered under the name.</summary>
    /// <param name="toolId">The name.</param>
    /// <returns>Whether it can be opened.</returns>
    public bool IsRegistered(string toolId) => toolId is not null && _tools.ContainsKey(toolId);

    /// <summary>
    /// Opens a tool for a session. Nothing is opened for a session that has ended or for a name nobody registered; an exception of
    /// the tool is logged and shown (<see cref="UiSafe"/>), not thrown.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="toolId">The name.</param>
    /// <param name="argument">What the tool needs, as <see cref="ToolIds"/> says for it.</param>
    /// <returns>The window that was shown, or null.</returns>
    public Window? Open(SessionContext session, string toolId, object? argument = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsEnded || toolId is null || !_tools.TryGetValue(toolId, out Func<SessionContext, object?, Window?>? open)) return null;
        Window? window = null;
        UiSafe.Run(() => window = open(session, argument), "open " + toolId);
        return window;
    }
}
