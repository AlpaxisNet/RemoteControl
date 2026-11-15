using Avalonia.Controls;
using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Ui.Shell;

/// <summary>Why a session wants the user's attention.</summary>
public enum AttentionKind
{
    /// <summary>An <c>#ALERT</c> line in the chat: the task bar entry flashes.</summary>
    Alert,

    /// <summary>An alert that also beeps when notification sounds are on.</summary>
    AlertWithSound,

    /// <summary>A private message arrived: the tray icon blinks.</summary>
    PrivateMessage,
}

/// <summary>
/// The program as its windows see it: the sessions, the windows that belong to no session, and what only the program can do.
/// Implemented by the program (RemoteControl.App) and by a fake in window tests. Used on the UI thread only.
/// </summary>
public interface IAppShell
{
    /// <summary>The sessions the user is signed in to, in the order they were opened.</summary>
    IReadOnlyList<SessionContext> Sessions { get; }

    /// <summary>The session the tray icon and the MCP server act on: the last one opened or chosen.</summary>
    SessionContext? ActiveSession { get; set; }

    /// <summary>Raised when a session was added or removed or <see cref="ActiveSession"/> changed.</summary>
    event EventHandler? SessionsChanged;

    /// <summary>
    /// Whether the program has a tray icon on this desktop. With one, closing a session's main window only hides it (the session
    /// stays signed in and is reached through the tray); without one, the main window asks before it signs out.
    /// </summary>
    bool TrayAvailable { get; }

    /// <summary>Shows the login window.</summary>
    void ShowLogin();

    /// <summary>
    /// Shows the server list of the session's list server so the user can pick a server; with a session given, the server chosen
    /// replaces that session's connection in its windows (the C++ "switch server"), otherwise it becomes a new session.
    /// </summary>
    /// <param name="switchFor">The session whose window asked, or null.</param>
    void ShowServerList(SessionContext? switchFor);

    /// <summary>Shows the list-server settings (the profiles: Alpaxis built in, the user's own).</summary>
    /// <param name="owner">The window that asked, or null.</param>
    void ShowListServerSettings(Window? owner);

    /// <summary>
    /// Connects again with what a session logged in with and puts the new connection into the session
    /// (<see cref="SessionContext.Replace"/>). The old connection is disposed.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>Whether it is connected again, and why not.</returns>
    Task<ConnectResult> ReconnectAsync(SessionContext session);

    /// <summary>Signs a session out: its windows close, its connection ends; with no session left the login window is shown.</summary>
    /// <param name="session">The session.</param>
    void SignOut(SessionContext session);

    /// <summary>Ends the program (asking first when script editors have changes the server has not confirmed).</summary>
    void Quit();

    /// <summary>Tells the program the user did something (a key, the mouse) in any of its windows: the away timer starts again.</summary>
    void NoteActivity();

    /// <summary>When the user last did something in any window of the program (<see cref="NoteActivity"/>), for the away timer of every session.</summary>
    DateTimeOffset LastActivity { get; }

    /// <summary>Asks for the user's attention for a session (task bar flash, beep, tray blink), unless one of the program's windows is active.</summary>
    /// <param name="session">The session.</param>
    /// <param name="kind">Why.</param>
    void RequestAttention(SessionContext session, AttentionKind kind);

    /// <summary>A service of the whole program (<see cref="IExtensionActions"/>, for example), or null when the program has none.</summary>
    /// <typeparam name="T">The interface.</typeparam>
    /// <returns>The service, or null.</returns>
    T? GetService<T>() where T : class;
}

/// <summary>What the server-list window hands the program when the user picked a server.</summary>
/// <param name="Login">What to log in with.</param>
/// <param name="SwitchFor">The session whose connection the new one replaces, or null for a new session.</param>
public sealed record ServerChoice(SessionLogin Login, SessionContext? SwitchFor);

/// <summary>Makes connections; the program makes real ones, tests make fakes.</summary>
/// <param name="options">Account, password and where events are raised.</param>
/// <returns>A connection that is not connected yet.</returns>
public delegate IRcSession SessionFactory(RcSessionOptions options);
