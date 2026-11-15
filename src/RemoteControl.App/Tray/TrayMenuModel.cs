using System.Globalization;
using System.Text;

namespace RemoteControl.App.Tray;

/// <summary>What a tray menu entry does.</summary>
internal enum TrayCommand
{
    /// <summary>A separator line.</summary>
    Separator,

    /// <summary>A submenu (its entries are <see cref="TrayMenuEntry.Children"/>).</summary>
    Submenu,

    /// <summary>"Open": the primary sessions' main windows, else the active session's, else the login window.</summary>
    Open,

    /// <summary>"Server List" of the active session (shown when its NC is logged in, as the C++).</summary>
    ServerList,

    /// <summary>"Sign out" of the active session.</summary>
    SignOut,

    /// <summary>"Latest Private Message": the window of the latest PM not looked at yet.</summary>
    LatestPrivateMessage,

    /// <summary>A session's entry: makes it the active session and shows its main window.</summary>
    ShowSession,

    /// <summary>"Sign out &lt;server&gt;".</summary>
    SignOutSession,

    /// <summary>Marks a session as primary or not (the <c>[*]</c> marker).</summary>
    TogglePrimary,

    /// <summary>"Quit".</summary>
    Quit,
}

/// <summary>One entry of the tray menu, without any Avalonia object.</summary>
/// <param name="Command">What it does.</param>
/// <param name="Label">Its text (empty for a separator).</param>
/// <param name="Session">The index of the session it acts on in <see cref="TrayMenuState.Sessions"/>, or -1.</param>
/// <param name="Checked">Null for a plain entry; otherwise a check-box (or the active-session mark) and whether it is ticked.</param>
/// <param name="Children">The entries of a submenu, else empty.</param>
internal sealed record TrayMenuEntry(TrayCommand Command, string Label, int Session = -1, bool? Checked = null, IReadOnlyList<TrayMenuEntry>? Children = null)
{
    /// <summary>The entries of a submenu.</summary>
    public IReadOnlyList<TrayMenuEntry> Items => Children ?? Array.Empty<TrayMenuEntry>();
}

/// <summary>What the tray menu shows of one session.</summary>
/// <param name="Name">The server's name.</param>
/// <param name="Connected">Whether its connection is up.</param>
/// <param name="NcAuthenticated">Whether its NC is logged in.</param>
/// <param name="Primary">Whether it is marked primary.</param>
/// <param name="Active">Whether it is the active session.</param>
/// <param name="HasUnreadPm">Whether it has a private message not looked at yet.</param>
/// <param name="Players">How many players it shows.</param>
internal sealed record TraySessionState(string Name, bool Connected, bool NcAuthenticated, bool Primary, bool Active, bool HasUnreadPm, int Players);

/// <summary>Everything the tray menu and tool tip are made from.</summary>
/// <param name="Sessions">The sessions, in the order they were opened.</param>
internal sealed record TrayMenuState(IReadOnlyList<TraySessionState> Sessions)
{
    /// <summary>The index of the active session, or -1.</summary>
    public int ActiveIndex
    {
        get
        {
            for (int index = 0; index < Sessions.Count; index++) if (Sessions[index].Active) return index;
            return -1;
        }
    }
}

/// <summary>
/// The tray menu as plain data (<c>TMain.cpp:305-423</c>), made fresh from the current sessions every time: no entry can be duplicated
/// or point to a session that was signed out (defect D3). The program turns it into a <c>NativeMenu</c>.
/// </summary>
/// <remarks>
/// <para>The entries of the StatusNotifier and Windows menus of the C++: <c>Open</c>, <c>Server List</c> (while the active session's
/// NC is logged in), <c>Sign out</c> (with an active session), then per session <c>[*] &lt;server&gt;</c> and
/// <c>Sign out [*] &lt;server&gt;</c>, and <c>Quit</c>.</para>
/// <para>Differences: the active session is ticked; "Latest Private Message" is in the menu while a PM is unread (the C++ had it on
/// the double click only, which is kept); the primary marker is set in a "Primary Windows" submenu of check boxes, because a native
/// menu has no right click inside it (the Windows C++ toggled it by right-clicking an entry); a session whose connection dropped is
/// listed with "(disconnected)" so that it can still be shown or signed out (the C++ hid it from the menu).</para>
/// </remarks>
internal static class TrayMenuModel
{
    /// <summary>The longest tool tip, as the C++ cut it.</summary>
    public const int MaximumToolTipLength = 64;

    /// <summary>The primary marker.</summary>
    public const string PrimaryMarker = "[*] ";

    /// <summary>Makes the menu.</summary>
    /// <param name="state">The sessions.</param>
    /// <returns>The entries, in order.</returns>
    public static IReadOnlyList<TrayMenuEntry> Build(TrayMenuState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var entries = new List<TrayMenuEntry> { new(TrayCommand.Open, "Open") };
        int active = state.ActiveIndex;
        if (active >= 0 && state.Sessions[active].NcAuthenticated) entries.Add(new TrayMenuEntry(TrayCommand.ServerList, "Server List"));
        if (active >= 0) entries.Add(new TrayMenuEntry(TrayCommand.SignOut, "Sign out"));
        if (state.Sessions.Any(session => session.HasUnreadPm)) entries.Add(new TrayMenuEntry(TrayCommand.LatestPrivateMessage, "Latest Private Message"));
        if (state.Sessions.Count > 0)
        {
            entries.Add(new TrayMenuEntry(TrayCommand.Separator, string.Empty));
            for (int index = 0; index < state.Sessions.Count; index++)
            {
                TraySessionState session = state.Sessions[index];
                string label = SessionLabel(session);
                entries.Add(new TrayMenuEntry(TrayCommand.ShowSession, label, index, session.Active));
                entries.Add(new TrayMenuEntry(TrayCommand.SignOutSession, "Sign out " + label, index));
            }
            var primary = new List<TrayMenuEntry>();
            for (int index = 0; index < state.Sessions.Count; index++)
                primary.Add(new TrayMenuEntry(TrayCommand.TogglePrimary, state.Sessions[index].Name, index, state.Sessions[index].Primary));
            entries.Add(new TrayMenuEntry(TrayCommand.Submenu, "Primary Windows", Children: primary));
        }
        entries.Add(new TrayMenuEntry(TrayCommand.Separator, string.Empty));
        entries.Add(new TrayMenuEntry(TrayCommand.Quit, "Quit"));
        return entries;
    }

    /// <summary>A session's label: the primary marker, the server's name, and "(disconnected)" when its connection dropped.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The label.</returns>
    public static string SessionLabel(TraySessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return (session.Primary ? PrimaryMarker : string.Empty) + session.Name + (session.Connected ? string.Empty : " (disconnected)");
    }

    /// <summary>
    /// The tool tip (<c>setTrayTooltip</c>): <c>Remote Control</c>, or <c>&lt;server&gt;: &lt;n&gt; player(s)</c> of the active connected
    /// session, cut to 64 characters.
    /// </summary>
    /// <param name="state">The sessions.</param>
    /// <returns>The tool tip.</returns>
    public static string ToolTip(TrayMenuState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        int active = state.ActiveIndex;
        string text = "Remote Control";
        if (active >= 0 && state.Sessions[active] is { Connected: true, Name.Length: > 0 } session)
            text = session.Name + ": " + session.Players.ToString(CultureInfo.InvariantCulture) + (session.Players == 1 ? " player" : " players");
        return text.Length > MaximumToolTipLength ? text[..MaximumToolTipLength] : text;
    }

    /// <summary>A text that changes whenever the menu would (to rebuild the native menu only then).</summary>
    /// <param name="entries">The entries.</param>
    /// <returns>The signature.</returns>
    public static string Signature(IReadOnlyList<TrayMenuEntry> entries)
    {
        var text = new StringBuilder();
        Append(text, entries);
        return text.ToString();
    }

    private static void Append(StringBuilder text, IReadOnlyList<TrayMenuEntry> entries)
    {
        foreach (TrayMenuEntry entry in entries)
        {
            text.Append((int)entry.Command).Append('|').Append(entry.Label).Append('|').Append(entry.Session).Append('|').Append(entry.Checked switch { null => '-', true => '1', false => '0' });
            if (entry.Children is { Count: > 0 } children)
            {
                text.Append('[');
                Append(text, children);
                text.Append(']');
            }
            text.Append('\n');
        }
    }
}
