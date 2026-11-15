namespace RemoteControl.Extensions;

/// <summary>The outcome of an MCP action in the program: success, or the message the MCP client sees.</summary>
/// <param name="Success">Whether it worked.</param>
/// <param name="Error">Why not, or empty.</param>
public readonly record struct McpActionResult(bool Success, string Error)
{
    /// <summary>Success.</summary>
    public static McpActionResult Ok => new(true, string.Empty);

    /// <summary>A failure with a message.</summary>
    /// <param name="error">The message.</param>
    /// <returns>The failure.</returns>
    public static McpActionResult Fail(string error) => new(false, error);
}

/// <summary>The server of the selected session (<c>McpGuiActions.serverName</c>/<c>connected</c>).</summary>
/// <param name="Name">The server name, or empty without a session.</param>
/// <param name="Connected">Whether the selected session is connected.</param>
public sealed record McpServerState(string Name, bool Connected);

/// <summary>The login form, without its password.</summary>
/// <param name="Visible">Whether the login form is shown.</param>
/// <param name="Account">The account name in the form.</param>
/// <param name="Nickname">The nickname in the form.</param>
/// <param name="HasPassword">Whether a password is filled in (never the password itself).</param>
public sealed record McpLoginState(bool Visible, string Account, string Nickname, bool HasPassword);

/// <summary>A visible window of the program.</summary>
/// <param name="Title">The window title.</param>
/// <param name="Active">Whether it has the focus.</param>
/// <param name="Tabs">The labels of its tabs (every tab control inside it), in order.</param>
public sealed record McpWindowInfo(string Title, bool Active, IReadOnlyList<string> Tabs);

/// <summary>An open script editor (<c>ScriptEditorSnapshot</c>).</summary>
/// <param name="Name">The editor name, its window title (such as <c>Weapon: bow</c>, <c>Class: util</c>, <c>NPC: door</c>); unique.</param>
/// <param name="Active">Whether its window has the focus.</param>
/// <param name="Modified">Whether it has unsaved changes.</param>
/// <param name="Text">The whole buffer, unsaved changes included.</param>
/// <param name="Selection">The selected text, or empty.</param>
public sealed record McpScriptEditorInfo(string Name, bool Active, bool Modified, string Text, string Selection);

/// <summary>What an edit function returns to <see cref="IMcpHost.UpdateScriptEditorAsync"/>: the new buffer, or why the buffer stays as it is.</summary>
/// <param name="NewText">The new buffer, or <c>null</c> when <paramref name="Error"/> is set.</param>
/// <param name="Error">Why nothing changes, or <c>null</c>.</param>
public sealed record McpTextEdit(string? NewText, string? Error)
{
    /// <summary>Replace the buffer by <paramref name="text"/>.</summary>
    /// <param name="text">The new buffer.</param>
    /// <returns>The edit.</returns>
    public static McpTextEdit Replace(string text) => new(text, null);

    /// <summary>Leave the buffer and report <paramref name="error"/>.</summary>
    /// <param name="error">The message.</param>
    /// <returns>The edit.</returns>
    public static McpTextEdit Refuse(string error) => new(null, error);
}

/// <summary>What an MCP client wants to do with a script editor, for the approval question.</summary>
public enum McpScriptAction
{
    /// <summary>Change the open buffer and leave it unsaved ("Edit the open script buffer without saving or applying it?").</summary>
    Edit,

    /// <summary>Press Save/Apply ("Save/Apply the current open script buffer to the server?").</summary>
    SaveApply,
}

/// <summary>The user's answer to an approval question.</summary>
public enum McpApproval
{
    /// <summary>Deny.</summary>
    Deny,

    /// <summary>Allow once.</summary>
    AllowOnce,

    /// <summary>Always allow scripts of this type (saved to the options as <c>mcpapproveweapon</c>, <c>mcpapproveclass</c> or <c>mcpapprovenpc</c>).</summary>
    AlwaysAllow,
}

/// <summary>
/// What an MCP tool does inside the running program; the windows implement it (<c>McpGuiActions</c> and the editor tracking of the C++).
/// </summary>
/// <remarks>
/// <para><b>Thread.</b> <see cref="McpBridge"/> calls every method on the context it was given (the UI thread), one call at a time per
/// MCP client; the calls of two clients may interleave at the methods' await points. A method may take as long as it needs (an
/// approval waits for the user); it must not throw (an exception is reported to the client as a tool error and logged).</para>
/// <para><b>Permissions</b> (the options <c>mcpread</c>, <c>mcpwrite</c>, ...), the server scope, the approvals' saved answers and the
/// audit log are handled by the bridge; a method is only called when the tool is allowed.</para>
/// </remarks>
public interface IMcpHost
{
    /// <summary>The server of the selected session (<c>rc_status</c>, <c>rc/instance</c>, the server scope).</summary>
    /// <returns>The server state.</returns>
    Task<McpServerState> GetServerStateAsync();

    /// <summary>The server names of the connected sessions (<c>rc_list_connections</c>).</summary>
    /// <returns>The names.</returns>
    Task<IReadOnlyList<string>> ListConnectionsAsync();

    /// <summary>Selects the connected session that later MCP actions target (<c>rc_select_connection</c>; name compared ignoring case). C++ error: "Connected RC server session not found".</summary>
    /// <param name="server">The server name.</param>
    /// <returns>Success, or the message.</returns>
    Task<McpActionResult> SelectConnectionAsync(string server);

    /// <summary>The login form state (<c>rc_login_status</c>).</summary>
    /// <returns>The state, never the password.</returns>
    Task<McpLoginState> GetLoginStateAsync();

    /// <summary>Submits the login form with its saved password (<c>rc_submit_login</c>); an empty account or nickname keeps the form's value.</summary>
    /// <param name="account">The account override, or empty.</param>
    /// <param name="nickname">The nickname override, or empty.</param>
    /// <returns>Success, or the message.</returns>
    Task<McpActionResult> SubmitLoginAsync(string account, string nickname);

    /// <summary>The server names of the server list (<c>rc_list_servers</c>).</summary>
    /// <returns>The names.</returns>
    Task<IReadOnlyList<string>> ListServersAsync();

    /// <summary>Selects and connects a server of the server list (<c>rc_connect_server</c>).</summary>
    /// <param name="server">The server name.</param>
    /// <returns>Success, or the message.</returns>
    Task<McpActionResult> ConnectServerAsync(string server);

    /// <summary>The visible windows of the program with their tabs (<c>rc_list_windows</c>, <c>rc_read_window</c>, and whether a window is active for <c>rc/instance</c>).</summary>
    /// <returns>The windows.</returns>
    Task<IReadOnlyList<McpWindowInfo>> ListWindowsAsync();

    /// <summary>The open script editors, sorted by name (<c>rc_list_script_editors</c> and the other editor tools).</summary>
    /// <returns>The editors.</returns>
    Task<IReadOnlyList<McpScriptEditorInfo>> ListScriptEditorsAsync();

    /// <summary>
    /// Changes the buffer of an open editor (<c>rc_write_script_editor</c>, <c>rc_replace_script_text</c>): calls <paramref name="edit"/>
    /// once, synchronously, with the current buffer; when it returns a new text the buffer is replaced as one undoable user action
    /// and marked modified (unsaved; never saved or applied). When it returns an error nothing changes and that error is returned.
    /// </summary>
    /// <param name="editor">The editor name (exact).</param>
    /// <param name="edit">Computes the new buffer from the current one.</param>
    /// <returns>Success; "Script editor is not open"; or the edit's error.</returns>
    Task<McpActionResult> UpdateScriptEditorAsync(string editor, Func<string, McpTextEdit> edit);

    /// <summary>Presses the Save/Apply button of an open editor (<c>rc_save_script_editor</c>). C++ errors: "Script editor is not open", "Open editor has no save action".</summary>
    /// <param name="editor">The editor name (exact).</param>
    /// <returns>Success, or the message.</returns>
    Task<McpActionResult> SaveScriptEditorAsync(string editor);

    /// <summary>Opens a view of the selected session (<c>rc_open_window</c>); <paramref name="view"/> is one of <see cref="McpProtocol.Views"/>. C++ error: "No connected RC server window".</summary>
    /// <param name="view">The view.</param>
    /// <returns>Success, or the message.</returns>
    Task<McpActionResult> OpenViewAsync(string view);

    /// <summary>Sends a chat line in the selected session (<c>rc_send_chat</c>). C++ errors: "No connected RC server window", "RC is not connected", "Chat text is empty".</summary>
    /// <param name="text">The chat text.</param>
    /// <returns>Success, or the message.</returns>
    Task<McpActionResult> SendChatAsync(string text);

    /// <summary>Presents or closes the focused window (<c>rc_focused_window_action</c>); <paramref name="action"/> is <c>present</c> or <c>close</c>. C++ error: "No focused window or invalid action".</summary>
    /// <param name="action">The action.</param>
    /// <returns>Success, or the message.</returns>
    Task<McpActionResult> FocusedWindowActionAsync(string action);

    /// <summary>Asks the user "Allow this MCP action?" with Deny / Allow once (only called while <c>mcpapprove</c> is on).</summary>
    /// <param name="description">What the client wants, such as "Send RC chat text".</param>
    /// <returns>Whether the user allowed it.</returns>
    Task<bool> ApproveAsync(string description);

    /// <summary>
    /// Asks the user about a script edit or save (only called while <c>mcpapprove</c> is on and the type was not always allowed):
    /// Deny / Allow once, plus "Always allow &lt;type&gt; scripts" when <paramref name="scriptType"/> is not empty.
    /// </summary>
    /// <param name="action">Edit or Save/Apply.</param>
    /// <param name="editor">The editor name.</param>
    /// <param name="scriptType"><c>Weapon</c>, <c>Class</c>, <c>NPC</c> or empty (<see cref="McpProtocol.ScriptTypeOf"/>).</param>
    /// <returns>The answer.</returns>
    Task<McpApproval> ApproveScriptAsync(McpScriptAction action, string editor, string scriptType);
}
