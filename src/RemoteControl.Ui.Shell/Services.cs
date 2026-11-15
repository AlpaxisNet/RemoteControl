using Avalonia.Controls;

namespace RemoteControl.Ui.Shell;

/// <summary>The chat of a session's main window, offered by Ui.Session (<see cref="SessionServices"/>).</summary>
public interface ISessionChat
{
    /// <summary>The names of the chat tabs, the RC chat first.</summary>
    IReadOnlyList<string> Tabs { get; }

    /// <summary>Shows a line in the RC chat tab as if the server had sent it.</summary>
    /// <param name="text">The line.</param>
    void AppendLine(string text);

    /// <summary>Shows a line in a named tab, which is made when it does not exist (an extension's output, a channel).</summary>
    /// <param name="tab">The tab's name.</param>
    /// <param name="text">The line.</param>
    void AppendToTab(string tab, string text);

    /// <summary>Removes a named tab (not the RC chat).</summary>
    /// <param name="tab">The tab's name.</param>
    void RemoveTab(string tab);

    /// <summary>Does what typing a line into the chat field and pressing Enter does, the program's own commands included.</summary>
    /// <param name="text">The line.</param>
    /// <returns>Whether it was handled or sent.</returns>
    bool Send(string text);

    /// <summary>Shows the main window and brings it to the front.</summary>
    void Activate();

    /// <summary>The main window, as the owner for dialogs.</summary>
    Window? Window { get; }
}

/// <summary>The private messages of a session, offered by Ui.Players.</summary>
public interface IPrivateMessages
{
    /// <summary>Whether a private message arrived that was not looked at yet (the tray icon blinks).</summary>
    bool HasUnread { get; }

    /// <summary>Raised when <see cref="HasUnread"/> changed.</summary>
    event EventHandler? UnreadChanged;

    /// <summary>Opens the window of the latest private message that was not looked at.</summary>
    /// <returns>Whether there was one.</returns>
    bool OpenLatest();

    /// <summary>Marks everything as looked at (the main window got the focus).</summary>
    void ClearUnread();
}

/// <summary>A script editor that is open, as other groups see it.</summary>
/// <param name="Title">The editor's title, unique among the open editors of the session.</param>
/// <param name="Kind">What it edits.</param>
/// <param name="Name">The script's name.</param>
/// <param name="IsActive">Whether its window is the active one.</param>
/// <param name="IsModified">Whether it has changes the server has not confirmed.</param>
/// <param name="Text">Its text.</param>
/// <param name="Selection">The selected text, or empty.</param>
public sealed record ScriptEditorSnapshot(string Title, ScriptKind Kind, string Name, bool IsActive, bool IsModified, string Text, string Selection);

/// <summary>The open script editors of a session, offered by Ui.Scripts.</summary>
public interface IScriptEditors
{
    /// <summary>The open editors.</summary>
    IReadOnlyList<ScriptEditorSnapshot> Editors { get; }

    /// <summary>Raised when an editor was opened, closed, changed or saved.</summary>
    event EventHandler? Changed;

    /// <summary>Replaces an editor's text with what a function makes of it (as one undo step); the function returns null to leave it.</summary>
    /// <param name="title">The editor's title.</param>
    /// <param name="edit">Gets the current text, returns the new text or null.</param>
    /// <returns>Whether the editor exists and its text was replaced.</returns>
    bool Update(string title, Func<string, string?> edit);

    /// <summary>Sends an editor's text to the server, as its save button does.</summary>
    /// <param name="title">The editor's title.</param>
    /// <returns>Whether the server took it.</returns>
    Task<bool> SaveAsync(string title);

    /// <summary>The titles of the editors with changes the server has not confirmed (asked before the session or the program ends).</summary>
    IReadOnlyList<string> UnsavedTitles { get; }
}

/// <summary>What the sync window tells the rest of a session, offered by Ui.Files.</summary>
public interface ISyncStatus
{
    /// <summary>The progress shown in the main window's title (0 to 100), or null when sync is off.</summary>
    int? Percent { get; }

    /// <summary>Whether files are being synchronised: the file browser is not opened meanwhile.</summary>
    bool IsFileSyncActive { get; }

    /// <summary>Raised when one of the two changed.</summary>
    event EventHandler? Changed;
}

/// <summary>A button an extension puts into a kind of window.</summary>
/// <param name="Id">What identifies it for <see cref="IExtensionActions.Invoke"/>.</param>
/// <param name="Label">Its text.</param>
/// <param name="AtBottom">Whether it goes into the bottom bar instead of the top one.</param>
/// <param name="IconPath">A picture file for it, or null.</param>
public sealed record WindowActionButton(string Id, string Label, bool AtBottom, string? IconPath);

/// <summary>The buttons extensions put into windows, offered for the whole program by Ui.Extensions (<see cref="IAppShell.GetService{T}"/>).</summary>
public interface IExtensionActions
{
    /// <summary>Raised when the buttons changed (an extension was enabled or disabled).</summary>
    event EventHandler? Changed;

    /// <summary>The buttons for a kind of window.</summary>
    /// <param name="windowKind">The kind, <c>"script-editor"</c> for a script editor.</param>
    /// <returns>The buttons, in the order to show them.</returns>
    IReadOnlyList<WindowActionButton> ActionsFor(string windowKind);

    /// <summary>Tells the extension its button was pressed in a script editor.</summary>
    /// <param name="actionId">The button.</param>
    /// <param name="session">The session of the window.</param>
    /// <param name="editor">The editor it was pressed in.</param>
    void Invoke(string actionId, SessionContext session, ScriptEditorSnapshot editor);
}
