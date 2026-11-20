using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using RemoteControl.Extensions;
using RemoteControl.Protocol.Session;
using ScriptKind = RemoteControl.Ui.Shell.ScriptKind;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.App.Mcp;

/// <summary>The approval questions of the MCP server; dialogs in the program, a fake in tests.</summary>
internal interface IMcpApprovals
{
    /// <summary>"Allow this MCP action?" with Deny / Allow once.</summary>
    /// <param name="description">What the client wants.</param>
    /// <returns>Whether it was allowed.</returns>
    Task<bool> ApproveAsync(string description);

    /// <summary>The script question with Deny / Allow once / Always allow &lt;type&gt; scripts.</summary>
    /// <param name="action">Edit or save.</param>
    /// <param name="editor">The editor.</param>
    /// <param name="scriptType">The script type, or empty.</param>
    /// <returns>The answer.</returns>
    Task<McpApproval> ApproveScriptAsync(McpScriptAction action, string editor, string scriptType);
}

/// <summary>
/// What the MCP tools do in the running program (<see cref="IMcpHost"/>, the <c>McpGuiActions</c> of <c>TMain.cpp:823-837</c>): the
/// login state and form through the start windows, the sessions through the shell, the windows of the program, the active session's
/// chat and views, the script editors of every session (<see cref="IScriptEditors"/>), and the approval questions. The server tools
/// get the connection of a session (<see cref="FindSessionAsync"/>) with its <see cref="McpSessionJournal"/>: every session gets one
/// as soon as it exists, attached to each of its connections (reconnects included) until the session ends. Called by the bridge on
/// the UI thread; never throws.
/// </summary>
internal sealed class McpHost : IMcpHost
{
    /// <summary>The C++ view names of <c>mcpOpenView</c> and the tools they open.</summary>
    public static readonly IReadOnlyDictionary<string, string> ViewTools = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["player_list"] = ToolIds.PlayerList,
        ["file_browser"] = ToolIds.FileBrowser,
        ["classes"] = ToolIds.Classes,
        ["weapons"] = ToolIds.Weapons,
        ["npcs"] = ToolIds.Npcs,
        ["server_options"] = ToolIds.ServerOptions,
        ["server_flags"] = ToolIds.ServerFlags,
        ["folder_config"] = ToolIds.FolderConfig,
        ["toalls"] = ToolIds.Toalls,
        ["accounts"] = ToolIds.Accounts,
        ["options"] = ToolIds.RcOptions,
    };

    private readonly AppShell _shell;
    private readonly IMcpApprovals _approvals;
    private readonly ConditionalWeakTable<SessionContext, McpSessionJournal> _journals = new();

    /// <summary>Creates the host.</summary>
    /// <param name="shell">The program.</param>
    /// <param name="approvals">The approval questions.</param>
    public McpHost(AppShell shell, IMcpApprovals approvals)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _shell.SessionsChanged += (_, _) => UiSafe.Run(AttachJournals, "MCP journals");
        AttachJournals();
    }

    /// <summary>The journal of a session (made and attached when the session was first seen).</summary>
    /// <param name="session">The session.</param>
    /// <returns>Its journal.</returns>
    internal McpSessionJournal JournalOf(SessionContext session) => _journals.GetValue(session, context =>
    {
        var journal = new McpSessionJournal();
        context.Attach(connection => journal.Attach(connection));
        // A window that switches to another server keeps its context: what the journal saw belongs to the old server, so it goes.
        string server = context.ServerName;
        context.SessionReplaced += (_, _) => UiSafe.Run(() =>
        {
            if (string.Equals(context.ServerName, server, StringComparison.Ordinal)) return;
            journal.Clear();
            journal.Add(McpSessionJournal.Connection, "The session went from " + server + " to " + context.ServerName + "; the events of " + server + " were dropped.");
            server = context.ServerName;
        }, "MCP journal server switch");
        return journal;
    });

    private void AttachJournals()
    {
        foreach (SessionContext session in _shell.Sessions)
            if (!session.IsEnded) JournalOf(session);
    }

    public Task<McpSessionTarget?> FindSessionAsync(string server)
    {
        SessionContext? found = FindContext(server);
        return Task.FromResult(found is null ? null : new McpSessionTarget(found.ServerName, found.Session, JournalOf(found)));
    }

    public async Task<McpActionResult> EnterChatLineAsync(McpSessionTarget target, string text)
    {
        if (ContextOf(target) is not { } session) return McpActionResult.Fail("Connected RC server session not found");
        if (string.IsNullOrEmpty(text)) return McpActionResult.Fail("Chat text is empty");
        bool sent;
        if (session.Services.Get<ISessionChat>() is { } chat) sent = UiSafe.Run(() => chat.Send(text), "MCP chat line", false);
        else sent = await session.Session.SendRcChatAsync(text).ConfigureAwait(true);
        return sent ? McpActionResult.Ok : McpActionResult.Fail("The line could not be sent");
    }

    public Task<McpActionResult> OpenScriptEditorAsync(McpSessionTarget target, string kind, string name, int npcId)
    {
        if (ContextOf(target) is not { } session) return Task.FromResult(McpActionResult.Fail("Connected RC server session not found"));
        ScriptKind? editorKind = kind switch { "weapon" => ScriptKind.Weapon, "class" => ScriptKind.Class, "npc" => ScriptKind.Npc, _ => null };
        if (editorKind is null) return Task.FromResult(McpActionResult.Fail("kind must be weapon, class or npc"));
        Window? window = UiSafe.Run(() => session.OpenTool(ToolIds.ScriptEditor, new ScriptRequest(editorKind.Value, name ?? string.Empty, npcId)), "MCP open script editor", null);
        return Task.FromResult(window is not null ? McpActionResult.Ok : McpActionResult.Fail("The script editor could not be opened (it needs the NC connection)"));
    }

    public IDisposable ClaimAnswer(McpSessionTarget target, string kind, string key)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ContextOf(target)?.ClaimedAnswers.Claim(kind, key) ?? new Unsubscriber(() => { });
    }

    /// <summary>The session of a target: the one whose journal it is (a journal lives as long as its session, across reconnects).</summary>
    private SessionContext? ContextOf(McpSessionTarget? target) =>
        target is null ? null : _shell.Sessions.FirstOrDefault(session => !session.IsEnded && _journals.TryGetValue(session, out McpSessionJournal? journal) && ReferenceEquals(journal, target.Journal));

    /// <summary>The session named <paramref name="server"/> (ignoring case, a connected one first), or the selected session when it is empty.</summary>
    private SessionContext? FindContext(string? server)
    {
        if (string.IsNullOrEmpty(server)) return _shell.ActiveSession is { IsEnded: false } active ? active : _shell.Sessions.FirstOrDefault(session => !session.IsEnded && session.Session.IsConnected);
        return _shell.Sessions.Where(session => !session.IsEnded && string.Equals(session.ServerName, server, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.Session.IsConnected).FirstOrDefault();
    }

    public Task<McpServerState> GetServerStateAsync()
    {
        SessionContext? active = _shell.ActiveSession;
        return Task.FromResult(active is null ? new McpServerState(string.Empty, false) : new McpServerState(active.ServerName, active.Session.IsConnected));
    }

    public Task<IReadOnlyList<string>> ListConnectionsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(_shell.Sessions.Where(session => session.Session.IsConnected).Select(session => session.ServerName).ToArray());

    public Task<McpActionResult> SelectConnectionAsync(string server)
    {
        SessionContext? found = _shell.Sessions.FirstOrDefault(session => session.Session.IsConnected && string.Equals(session.ServerName, server ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (found is null) return Task.FromResult(McpActionResult.Fail("Connected RC server session not found"));
        _shell.ActiveSession = found;
        return Task.FromResult(McpActionResult.Ok);
    }

    public Task<McpLoginState> GetLoginStateAsync()
    {
        IStartWindows? start = _shell.Start;
        if (start is null) return Task.FromResult(new McpLoginState(false, string.Empty, string.Empty, false));
        return Task.FromResult(new McpLoginState(start.IsLoginVisible, start.SelectedAccount, start.Nickname, start.HasPassword));
    }

    public async Task<McpActionResult> SubmitLoginAsync(string account, string nickname)
    {
        if (_shell.Start is not { } start) return McpActionResult.Fail("The login window is not available");
        return Map(await start.SubmitLoginAsync(account ?? string.Empty, nickname ?? string.Empty).ConfigureAwait(true));
    }

    public Task<IReadOnlyList<string>> ListServersAsync() => Task.FromResult(_shell.Start?.Servers ?? Array.Empty<string>());

    public async Task<McpActionResult> ConnectServerAsync(string server)
    {
        if (_shell.Start is not { } start) return McpActionResult.Fail("Server list is not loaded");
        return Map(await start.ConnectAsync(server ?? string.Empty).ConfigureAwait(true));
    }

    public Task<IReadOnlyList<McpWindowInfo>> ListWindowsAsync()
    {
        var windows = new List<McpWindowInfo>();
        foreach (Window window in _shell.Desktop.Windows)
        {
            if (!window.IsVisible) continue;
            windows.Add(new McpWindowInfo(window.Title ?? string.Empty, window.IsActive, TabsOf(window)));
        }
        return Task.FromResult<IReadOnlyList<McpWindowInfo>>(windows);
    }

    public Task<IReadOnlyList<McpScriptEditorInfo>> ListScriptEditorsAsync()
    {
        var editors = new Dictionary<string, McpScriptEditorInfo>(StringComparer.Ordinal);
        foreach (SessionContext session in SessionsActiveFirst())
        {
            if (session.Services.Get<IScriptEditors>() is not { } open) continue;
            foreach (ScriptEditorSnapshot editor in open.Editors)
                editors.TryAdd(editor.Title, new McpScriptEditorInfo(editor.Title, editor.IsActive, editor.IsModified, editor.Text, editor.Selection));
        }
        return Task.FromResult<IReadOnlyList<McpScriptEditorInfo>>(editors.Values.OrderBy(editor => editor.Name, StringComparer.Ordinal).ToArray());
    }

    public Task<McpActionResult> UpdateScriptEditorAsync(string editor, Func<string, McpTextEdit> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (FindEditor(editor) is not { } found) return Task.FromResult(McpActionResult.Fail("Script editor is not open"));
        bool called = false;
        string? error = null;
        found.Editors.Update(found.Snapshot.Title, current =>
        {
            called = true;
            McpTextEdit result = edit(current);
            if (result.Error is not null || result.NewText is null)
            {
                error = result.Error ?? "Nothing to change";
                return null;
            }
            return result.NewText;
        });
        if (error is not null) return Task.FromResult(McpActionResult.Fail(error));
        return Task.FromResult(called ? McpActionResult.Ok : McpActionResult.Fail("Script editor is not open"));
    }

    public async Task<McpActionResult> SaveScriptEditorAsync(string editor)
    {
        if (FindEditor(editor) is not { } found) return McpActionResult.Fail("Script editor is not open");
        bool saved = await found.Editors.SaveAsync(found.Snapshot.Title).ConfigureAwait(true);
        if (saved) return McpActionResult.Ok;
        return McpActionResult.Fail(found.Snapshot.Kind == ScriptKind.Text ? "Open editor has no save action" : "The server did not take the script");
    }

    public Task<McpActionResult> OpenViewAsync(string view)
    {
        if (_shell.ActiveSession is not { IsEnded: false } session) return Task.FromResult(McpActionResult.Fail("No connected RC server window"));
        if (view is null || !ViewTools.TryGetValue(view, out string? tool)) return Task.FromResult(McpActionResult.Fail("Unsupported managed RC view"));
        Window? window = session.OpenTool(tool);
        return Task.FromResult(window is not null ? McpActionResult.Ok : McpActionResult.Fail("The view could not be opened (it needs the NC connection, or the server refused)"));
    }

    public async Task<McpActionResult> SendChatAsync(string text)
    {
        if (_shell.ActiveSession is not { IsEnded: false } session) return McpActionResult.Fail("No connected RC server window");
        if (!session.Session.IsConnected) return McpActionResult.Fail("RC is not connected");
        if (string.IsNullOrEmpty(text)) return McpActionResult.Fail("Chat text is empty");
        bool sent;
        if (session.Services.Get<ISessionChat>() is { } chat) sent = UiSafe.Run(() => chat.Send(text), "MCP chat", false);
        else sent = await session.Session.SendRcChatAsync(text).ConfigureAwait(true);
        return sent ? McpActionResult.Ok : McpActionResult.Fail("The chat text could not be sent");
    }

    public Task<McpActionResult> FocusedWindowActionAsync(string action)
    {
        Window? focused = _shell.Desktop.Windows.FirstOrDefault(window => window.IsActive && window.IsVisible);
        if (focused is null || action is not ("present" or "close")) return Task.FromResult(McpActionResult.Fail("No focused window or invalid action"));
        if (action == "present")
        {
            UiSafe.Run(() =>
            {
                if (focused.WindowState == WindowState.Minimized) focused.WindowState = WindowState.Normal;
                focused.Activate();
            }, "MCP present window");
        }
        else UiSafe.Run(focused.Close, "MCP close window");
        return Task.FromResult(McpActionResult.Ok);
    }

    public Task<bool> ApproveAsync(string description) => _approvals.ApproveAsync(description ?? string.Empty);

    public Task<McpApproval> ApproveScriptAsync(McpScriptAction action, string editor, string scriptType) =>
        _approvals.ApproveScriptAsync(action, editor ?? string.Empty, scriptType ?? string.Empty);

    /// <summary>The labels of the tabs of every tab control in a window, in order.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The labels.</returns>
    internal static IReadOnlyList<string> TabsOf(Window window)
    {
        var tabs = new List<string>();
        foreach (TabControl control in window.GetLogicalDescendants().OfType<TabControl>())
        {
            foreach (object? item in control.Items)
            {
                object? header = item is TabItem tab ? tab.Header : item;
                tabs.Add(TextOf(header));
            }
        }
        return tabs;
    }

    private static string TextOf(object? header) => header switch
    {
        null => string.Empty,
        string text => text,
        TextBlock block => block.Text ?? string.Empty,
        ContentControl content => TextOf(content.Content),
        Control control => control.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text).FirstOrDefault(text => !string.IsNullOrEmpty(text)) ?? string.Empty,
        _ => header.ToString() ?? string.Empty,
    };

    private IEnumerable<SessionContext> SessionsActiveFirst()
    {
        if (_shell.ActiveSession is { } active) yield return active;
        foreach (SessionContext session in _shell.Sessions)
            if (!ReferenceEquals(session, _shell.ActiveSession)) yield return session;
    }

    private (IScriptEditors Editors, ScriptEditorSnapshot Snapshot)? FindEditor(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (SessionContext session in SessionsActiveFirst())
        {
            if (session.IsEnded || session.Services.Get<IScriptEditors>() is not { } editors) continue;
            ScriptEditorSnapshot? snapshot = editors.Editors.FirstOrDefault(editor => string.Equals(editor.Title, name, StringComparison.Ordinal));
            if (snapshot is not null) return (editors, snapshot);
        }
        return null;
    }

    private static McpActionResult Map(ConnectResult result) =>
        result.Success ? McpActionResult.Ok : McpActionResult.Fail(string.IsNullOrEmpty(result.Error) ? "Unable to connect" : result.Error);
}
