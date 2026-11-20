using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using RemoteControl.Core;
using RemoteControl.Protocol.Testing;

namespace RemoteControl.Extensions.Tests;

/// <summary>An <see cref="IMcpHost"/> that records its calls and checks the thread they arrive on.</summary>
internal sealed class FakeHost : IMcpHost
{
    private readonly UiThread? _ui;

    public FakeHost(UiThread? ui = null) => _ui = ui;

    public ConcurrentQueue<string> Calls { get; } = new();

    public int WrongThread;

    public McpServerState Server { get; set; } = new("Alpaxis", true);

    public List<string> Connections { get; } = new() { "Alpaxis", "Other" };

    public McpLoginState Login { get; set; } = new(true, "account", "nick", true);

    public List<string> Servers { get; } = new() { "Alpaxis", "Test Server" };

    public List<McpWindowInfo> Windows { get; } = new() { new("Remote Control", true, new[] { "Chat", "Ext \"tab\"" }), new("Weapon: bow", false, Array.Empty<string>()) };

    public SortedDictionary<string, (string Text, bool Modified, string Selection)> Editors { get; } = new(StringComparer.Ordinal);

    public bool Approve { get; set; } = true;

    public McpApproval ScriptApproval { get; set; } = McpApproval.AllowOnce;

    public int ApprovalQuestions;

    private Task<T> Record<T>(string call, T value)
    {
        if (_ui is not null && !_ui.IsCurrent) Interlocked.Increment(ref WrongThread);
        Calls.Enqueue(call);
        return Task.FromResult(value);
    }

    public Task<McpServerState> GetServerStateAsync() => Record("server", Server);

    public Task<IReadOnlyList<string>> ListConnectionsAsync() => Record<IReadOnlyList<string>>("connections", Connections);

    public Task<McpActionResult> SelectConnectionAsync(string server) =>
        Record("select " + server, Connections.Contains(server, StringComparer.OrdinalIgnoreCase) ? McpActionResult.Ok : McpActionResult.Fail("Connected RC server session not found"));

    public Task<McpLoginState> GetLoginStateAsync() => Record("login", Login);

    public Task<McpActionResult> SubmitLoginAsync(string account, string nickname) => Record($"submit {account}/{nickname}", McpActionResult.Ok);

    public Task<IReadOnlyList<string>> ListServersAsync() => Record<IReadOnlyList<string>>("servers", Servers);

    public Task<McpActionResult> ConnectServerAsync(string server) => Record("connect " + server, McpActionResult.Ok);

    public Task<IReadOnlyList<McpWindowInfo>> ListWindowsAsync() => Record<IReadOnlyList<McpWindowInfo>>("windows", Windows);

    public Task<IReadOnlyList<McpScriptEditorInfo>> ListScriptEditorsAsync() =>
        Record<IReadOnlyList<McpScriptEditorInfo>>("editors", Editors.Select(pair => new McpScriptEditorInfo(pair.Key, pair.Key.StartsWith("Weapon", StringComparison.Ordinal), pair.Value.Modified, pair.Value.Text, pair.Value.Selection)).ToList());

    public Task<McpActionResult> UpdateScriptEditorAsync(string editor, Func<string, McpTextEdit> edit)
    {
        if (!Editors.TryGetValue(editor, out var state)) return Record("update " + editor, McpActionResult.Fail("Script editor is not open"));
        McpTextEdit result = edit(state.Text);
        if (result.Error is not null) return Record("update " + editor, McpActionResult.Fail(result.Error));
        Editors[editor] = (result.NewText!, true, state.Selection);
        return Record("update " + editor, McpActionResult.Ok);
    }

    public Task<McpActionResult> SaveScriptEditorAsync(string editor) =>
        Record("save " + editor, Editors.ContainsKey(editor) ? McpActionResult.Ok : McpActionResult.Fail("Script editor is not open"));

    public Task<McpActionResult> OpenViewAsync(string view) => Record("open " + view, McpActionResult.Ok);

    public Task<McpActionResult> SendChatAsync(string text) => Record("chat " + text, text.Length == 0 ? McpActionResult.Fail("Chat text is empty") : McpActionResult.Ok);

    public Task<McpActionResult> FocusedWindowActionAsync(string action) => Record("focused " + action, McpActionResult.Ok);

    /// <summary>The sessions server tools find, by server name; the first is the selected one.</summary>
    public List<McpSessionTarget> Sessions { get; } = new();

    /// <summary>Adds a session with a journal attached to it (call on the UI thread when there is one).</summary>
    public McpSessionTarget AddSession(string server, FakeRcSession session)
    {
        var journal = new McpSessionJournal();
        journal.Attach(session);
        var target = new McpSessionTarget(server, session, journal);
        Sessions.Add(target);
        return target;
    }

    public Task<McpSessionTarget?> FindSessionAsync(string server) =>
        Record("find " + server, server.Length == 0 ? Sessions.FirstOrDefault() : Sessions.FirstOrDefault(target => string.Equals(target.ServerName, server, StringComparison.OrdinalIgnoreCase)));

    public Task<McpActionResult> EnterChatLineAsync(McpSessionTarget target, string text)
    {
        if (!Sessions.Contains(target)) return Record("line " + target.ServerName + " " + text, McpActionResult.Fail("Connected RC server session not found"));
        _ = target.Session.SendRcChatAsync(text);
        return Record("line " + target.ServerName + " " + text, McpActionResult.Ok);
    }

    public Task<McpActionResult> OpenScriptEditorAsync(McpSessionTarget target, string kind, string name, int npcId) => Record($"editor {target.ServerName} {kind} {name} {npcId}", McpActionResult.Ok);

    /// <summary>The claims made (kind and key), in order, and how many are still open.</summary>
    public ConcurrentQueue<string> Claims { get; } = new();

    public int OpenClaims;

    public IDisposable ClaimAnswer(McpSessionTarget target, string kind, string key)
    {
        if (_ui is not null && !_ui.IsCurrent) Interlocked.Increment(ref WrongThread);
        Claims.Enqueue(kind + " " + key);
        Interlocked.Increment(ref OpenClaims);
        return new Release(() => Interlocked.Decrement(ref OpenClaims));
    }

    private sealed class Release(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }

    /// <summary>Runs while an approval question is open (as other staff could change the server meanwhile).</summary>
    public Action? WhileAsking { get; set; }

    public Task<bool> ApproveAsync(string description)
    {
        Interlocked.Increment(ref ApprovalQuestions);
        WhileAsking?.Invoke();
        return Record("approve " + description, Approve);
    }

    public Task<McpApproval> ApproveScriptAsync(McpScriptAction action, string editor, string scriptType)
    {
        Interlocked.Increment(ref ApprovalQuestions);
        WhileAsking?.Invoke();
        return Record($"approve-script {action} {editor} {scriptType}", ScriptApproval);
    }
}

/// <summary>A bridge with a fake host and options in a temporary folder.</summary>
internal sealed class BridgeSetup : IAsyncDisposable
{
    public BridgeSetup(TempFolder folder, Func<RcOptions, RcOptions>? options = null, UiThread? ui = null, int processId = 4242, string? socketDirectory = null)
    {
        Folder = folder;
        Paths = new AppPaths(folder.Combine("user"), folder.Combine("program"));
        Options = new OptionsStore(Paths);
        Options.Load();
        Options.Update(value => (options ?? (o => o with { McpEnabled = true, McpFullControl = true, McpApprove = false }))(value));
        Host = new FakeHost(ui);
        Host.Editors["Weapon: bow"] = ("alpha\nBeta match\ngamma\n", false, "Beta");
        Host.Editors["Class: util"] = ("function a() {}\n", true, "");
        Transport = new McpTransportOptions { SocketDirectory = socketDirectory ?? folder.Combine("sockets"), ProbeTimeout = TimeSpan.FromSeconds(2) };
        Bridge = new McpBridge(new McpBridgeOptions
        {
            Host = Host,
            Options = Options,
            Post = ui is null ? action => Task.Run(action) : ui.Post,
            AuditLogFile = Paths.McpAuditLogFile,
            ReferenceDirectory = folder.Combine("reference"),
            Transport = Transport,
            ProcessId = processId,
        });
    }

    public TempFolder Folder { get; }

    public AppPaths Paths { get; }

    public OptionsStore Options { get; }

    public FakeHost Host { get; }

    public McpTransportOptions Transport { get; }

    public McpBridge Bridge { get; }

    public async Task<JsonElement> CallAsync(string request)
    {
        string? response = await Wait.WithTimeout(Bridge.HandleAsync(request));
        Assert.NotNull(response);
        using JsonDocument document = JsonDocument.Parse(response!);
        return document.RootElement.Clone();
    }

    public Task<JsonElement> ToolAsync(string name, string arguments = "{}", int id = 1) =>
        CallAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}}");

    public static string ToolText(JsonElement response, bool expectError = false)
    {
        JsonElement result = response.GetProperty("result");
        Assert.Equal(expectError, result.GetProperty("isError").GetBoolean());
        JsonElement content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        return content.GetProperty("text").GetString()!;
    }

    public static (int Code, string Message) ErrorOf(JsonElement response)
    {
        JsonElement error = response.GetProperty("error");
        return (error.GetProperty("code").GetInt32(), error.GetProperty("message").GetString()!);
    }

    public string[] AuditLines() => File.Exists(Paths.McpAuditLogFile) ? File.ReadAllLines(Paths.McpAuditLogFile) : Array.Empty<string>();

    public async ValueTask DisposeAsync() => await Wait.WithTimeout(Bridge.DisposeAsync().AsTask());
}

public sealed class McpBridgeTests
{
    [Fact]
    public async Task InitializeListPingAndNotifications()
    {
        using var folder = new TempFolder();
        await using var setup = new BridgeSetup(folder);
        JsonElement initialize = await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
        Assert.Equal("2.0", initialize.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, initialize.GetProperty("id").GetInt32());
        JsonElement result = initialize.GetProperty("result");
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.False(result.GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean());
        Assert.Equal("rc", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("3", result.GetProperty("serverInfo").GetProperty("version").GetString());

        Assert.Null(await setup.Bridge.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
        Assert.Null(await setup.Bridge.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":1}}"));
        Assert.Null(await setup.Bridge.HandleAsync("   "));
        Assert.Equal(JsonValueKind.Object, (await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":\"p\",\"method\":\"ping\"}")).GetProperty("result").ValueKind);

        JsonElement list = await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":\"list-1\",\"method\":\"tools/list\"}");
        Assert.Equal("list-1", list.GetProperty("id").GetString());
        string[] names = list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(McpTools.AllNames, names);
        foreach (JsonElement tool in list.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(tool.GetProperty("description").GetString()));
            Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
        }
        JsonElement lines = list.GetProperty("result").GetProperty("tools").EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "rc_read_script_lines");
        Assert.Equal(new[] { "editor", "start", "end" }, lines.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(value => value.GetString()));

        string[] audit = setup.AuditLines();
        Assert.Equal(3, audit.Length);
        using JsonDocument first = JsonDocument.Parse(audit[0]);
        Assert.Equal("initialize", first.RootElement.GetProperty("method").GetString());
        Assert.Equal(string.Empty, first.RootElement.GetProperty("tool").GetString());
        Assert.Equal("ok", first.RootElement.GetProperty("outcome").GetString());
        Assert.True(Math.Abs(first.RootElement.GetProperty("time").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) < 60);
        Assert.Contains("notifications/initialized", audit[1]);
    }

    [Theory]
    [InlineData(false, false, false, false, false, false, new string[0])]
    [InlineData(true, false, false, false, false, false, new[] { "rc_status", "rc_list_connections", "rc_login_status", "rc_list_servers", "rc_list_windows", "rc_read_window", "rc_list_script_editors", "rc_read_script_editor", "rc_read_script_lines", "rc_search_script_editor" })]
    [InlineData(false, true, false, false, false, false, new[] { "rc_replace_script_text", "rc_write_script_editor", "rc_save_script_editor" })]
    [InlineData(false, false, true, false, false, false, new[] { "rc_submit_login", "rc_connect_server" })]
    [InlineData(false, false, false, true, false, false, new[] { "rc_send_chat" })]
    [InlineData(false, false, false, false, true, false, new[] { "rc_open_window", "rc_select_connection" })]
    [InlineData(false, false, false, false, false, true, new[] { "rc_focused_window_action" })]
    public async Task ToolsFollowThePermissions(bool read, bool write, bool login, bool server, bool windows, bool admin, string[] expected)
    {
        using var folder = new TempFolder();
        await using var setup = new BridgeSetup(folder, o => o with { McpEnabled = true, McpFullControl = false, McpRead = read, McpWrite = write, McpLogin = login, McpServer = server, McpWindows = windows, McpAdmin = admin, McpApprove = false });
        JsonElement list = await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
        string[] listed = list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray();
        // The program tools of the C++ keep their groups; every server tool listed belongs to an allowed group, and every allowed one is listed.
        Assert.Equal(expected, listed.Where(name => !McpTools.ServerToolNames.Contains(name)));
        RcOptions options = setup.Options.Current;
        Assert.Equal(McpTools.ServerToolNames.Where(name => McpTools.TryGet(name, out McpToolDefinition? definition) && McpTools.Allowed(definition.Group, options)), listed.Where(McpTools.ServerToolNames.Contains));
        foreach (string tool in McpTools.AllNames.Except(listed))
        {
            (int code, string message) = BridgeSetup.ErrorOf(await setup.ToolAsync(tool));
            Assert.Equal(-32602, code);
            Assert.Equal("Tool unavailable or disabled by MCP scope", message);
        }
        Assert.DoesNotContain(setup.Host.Calls, call => !call.StartsWith("server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedRequests()
    {
        using var folder = new TempFolder();
        await using var setup = new BridgeSetup(folder);
        Assert.Equal((-32700, "Parse error"), BridgeSetup.ErrorOf(await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":1,")));
        Assert.Equal(JsonValueKind.Null, (await setup.CallAsync("not json")).GetProperty("id").ValueKind);
        Assert.Equal((-32600, "Invalid Request"), BridgeSetup.ErrorOf(await setup.CallAsync("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]")));
        Assert.Equal((-32600, "Invalid Request"), BridgeSetup.ErrorOf(await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":{\"x\":1},\"method\":\"ping\"}")));
        Assert.Equal((-32600, "Invalid Request"), BridgeSetup.ErrorOf(await setup.CallAsync("42")));
        JsonElement unknown = await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"prompts/list\"}");
        Assert.Equal("abc", unknown.GetProperty("id").GetString());
        Assert.Equal((-32601, "Method not found"), BridgeSetup.ErrorOf(unknown));
        Assert.Equal((-32601, "Method not found"), BridgeSetup.ErrorOf(await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":null}")));
        JsonElement nestedId = await setup.CallAsync("{\"params\":{\"id\":99},\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"ping\"}");
        Assert.Equal(5, nestedId.GetProperty("id").GetInt32());
        Assert.Equal((-32602, "Tool unavailable or disabled by MCP scope"), BridgeSetup.ErrorOf(await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\"}")));
        Assert.Equal((-32602, "Tool unavailable or disabled by MCP scope"), BridgeSetup.ErrorOf(await setup.ToolAsync("rc_does_not_exist")));
        Assert.Equal((-32602, "Edit requires valid JSON editor and text strings"), BridgeSetup.ErrorOf(await setup.ToolAsync("rc_write_script_editor", "{\"editor\":\"Weapon: bow\",\"text\":5}")));
        Assert.Equal((-32602, "Partial edit requires valid JSON editor, expected, and replacement strings"), BridgeSetup.ErrorOf(await setup.ToolAsync("rc_replace_script_text", "{\"editor\":\"Weapon: bow\",\"expected\":\"a\"}")));
        Assert.Contains(setup.AuditLines(), line => line.Contains("invalid-json-string", StringComparison.Ordinal));
        Assert.Contains(setup.AuditLines(), line => line.Contains("method-not-found", StringComparison.Ordinal));
        Assert.True(McpRequestProcessor.ExpectsResponse("not json"));
        Assert.True(McpRequestProcessor.ExpectsResponse("[1]"));
        Assert.True(McpRequestProcessor.ExpectsResponse("{\"id\":null,\"method\":\"x\"}"));
        Assert.False(McpRequestProcessor.ExpectsResponse("{\"method\":\"notifications/initialized\",\"params\":{\"id\":1}}"));
        Assert.False(McpRequestProcessor.ExpectsResponse(" "));
    }

    [Fact]
    public async Task DisabledMcpAnswersEveryRequestWithAnError()
    {
        using var folder = new TempFolder();
        await using var setup = new BridgeSetup(folder, o => o with { McpEnabled = false });
        Assert.Equal((-32001, "MCP integration is disabled in RC Options"), BridgeSetup.ErrorOf(await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}")));
        Assert.Equal((-32001, "MCP integration is disabled in RC Options"), BridgeSetup.ErrorOf(await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"rc/instance\"}")));
        Assert.Null(await setup.Bridge.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
        Assert.All(setup.AuditLines(), line => Assert.Contains("\"outcome\":\"disabled\"", line));
        Assert.Empty(setup.Host.Calls);
    }

    [Fact]
    public async Task ReadTools()
    {
        using var folder = new TempFolder();
        await using var setup = new BridgeSetup(folder, o => o with { McpEnabled = true, McpFullControl = true, McpApprove = false, McpServerScope = "alpaxis" });
        using (JsonDocument status = JsonDocument.Parse(BridgeSetup.ToolText(await setup.ToolAsync("rc_status"))))
        {
            Assert.Equal("connected", status.RootElement.GetProperty("bridge").GetString());
            Assert.Equal(4242, status.RootElement.GetProperty("processId").GetInt32());
            Assert.True(status.RootElement.GetProperty("serverConnected").GetBoolean());
            Assert.Equal("Alpaxis", status.RootElement.GetProperty("server").GetString());
            Assert.Equal("alpaxis", status.RootElement.GetProperty("serverScope").GetString());
        }
        Assert.Equal("[\"Alpaxis\",\"Other\"]", BridgeSetup.ToolText(await setup.ToolAsync("rc_list_connections")));
        Assert.Equal("{\"visible\":true,\"account\":\"account\",\"nickname\":\"nick\",\"hasPassword\":true}", BridgeSetup.ToolText(await setup.ToolAsync("rc_login_status")));
        Assert.Equal("[\"Alpaxis\",\"Test Server\"]", BridgeSetup.ToolText(await setup.ToolAsync("rc_list_servers")));
        Assert.Equal("[{\"title\":\"Remote Control\",\"active\":true,\"tabs\":[\"Chat\",\"Ext \\\"tab\\\"\"]},{\"title\":\"Weapon: bow\",\"active\":false,\"tabs\":[]}]", BridgeSetup.ToolText(await setup.ToolAsync("rc_list_windows")));
        Assert.Equal("[{\"title\":\"Weapon: bow\",\"active\":false,\"tabs\":[]}]", BridgeSetup.ToolText(await setup.ToolAsync("rc_read_window", "{\"title\":\"WEAPON: BOW\"}")));
        Assert.Equal("[]", BridgeSetup.ToolText(await setup.ToolAsync("rc_read_window", "{\"title\":\"absent\"}")));
        using (JsonDocument editors = JsonDocument.Parse(BridgeSetup.ToolText(await setup.ToolAsync("rc_list_script_editors"))))
        {
            Assert.Equal(new[] { "Class: util", "Weapon: bow" }, editors.RootElement.EnumerateArray().Select(editor => editor.GetProperty("name").GetString()));
            JsonElement bow = editors.RootElement[1];
            Assert.True(bow.GetProperty("active").GetBoolean());
            Assert.False(bow.GetProperty("modified").GetBoolean());
            Assert.Equal("alpha\nBeta match\ngamma\n", bow.GetProperty("text").GetString());
            Assert.Equal("Beta", bow.GetProperty("selection").GetString());
        }
        Assert.Equal("[{\"name\":\"Class: util\",\"active\":false,\"modified\":true,\"text\":\"function a() {}\\n\",\"selection\":\"\"}]", BridgeSetup.ToolText(await setup.ToolAsync("rc_read_script_editor", "{\"editor\":\"Class: util\"}")));
        Assert.Equal("2: Beta match\n3: gamma\n", BridgeSetup.ToolText(await setup.ToolAsync("rc_read_script_lines", "{\"editor\":\"Weapon: bow\",\"start\":2,\"end\":3}")));
        Assert.Equal("1: alpha\n2: Beta match\n3: gamma\n4: \n", BridgeSetup.ToolText(await setup.ToolAsync("rc_read_script_lines", "{\"editor\":\"Weapon: bow\"}")));
        Assert.Equal("Script editor is not open", BridgeSetup.ToolText(await setup.ToolAsync("rc_read_script_lines", "{\"editor\":\"NPC: none\",\"start\":1,\"end\":2}"), expectError: true));
        Assert.Equal("1: alpha\n2: Beta match\n3: gamma\n", BridgeSetup.ToolText(await setup.ToolAsync("rc_search_script_editor", "{\"editor\":\"Weapon: bow\",\"query\":\"MATCH\",\"contextLines\":1}")));
        Assert.Equal(string.Empty, BridgeSetup.ToolText(await setup.ToolAsync("rc_search_script_editor", "{\"editor\":\"Weapon: bow\",\"query\":\"MATCH\",\"caseSensitive\":true}")));
        Assert.Equal("Script editor is not open", BridgeSetup.ToolText(await setup.ToolAsync("rc_search_script_editor", "{\"editor\":\"x\",\"query\":\"a\"}"), expectError: true));
        // The arguments may also be given directly in params (some clients do).
        JsonElement direct = await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"rc_read_script_lines\",\"editor\":\"Weapon: bow\",\"start\":1,\"end\":1}}");
        Assert.Equal("1: alpha\n", BridgeSetup.ToolText(direct));

        JsonElement instance = await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":-9001,\"method\":\"rc/instance\",\"params\":{}}");
        Assert.Equal("{\"processId\":4242,\"active\":true,\"serverConnected\":true,\"editorCount\":2,\"server\":\"Alpaxis\"}", instance.GetProperty("result").GetRawText());
    }

    [Fact]
    public async Task WriteToolsAndTheGuardedReplace()
    {
        using var folder = new TempFolder();
        await using var setup = new BridgeSetup(folder);
        Assert.Equal("Open script buffer partially edited; changes remain unsaved", BridgeSetup.ToolText(await setup.ToolAsync("rc_replace_script_text", "{\"editor\":\"Weapon: bow\",\"expected\":\"Beta match\",\"replacement\":\"Beta changed\"}")));
        Assert.Equal(("alpha\nBeta changed\ngamma\n", true, "Beta"), setup.Host.Editors["Weapon: bow"]);
        Assert.Equal("Expected text was not found in the current buffer", BridgeSetup.ToolText(await setup.ToolAsync("rc_replace_script_text", "{\"editor\":\"Weapon: bow\",\"expected\":\"zzz\",\"replacement\":\"x\"}"), expectError: true));
        setup.Host.Editors["Class: util"] = ("x x", false, "");
        Assert.Equal("Expected text is not unique in the current buffer", BridgeSetup.ToolText(await setup.ToolAsync("rc_replace_script_text", "{\"editor\":\"Class: util\",\"expected\":\"x\",\"replacement\":\"y\"}"), expectError: true));
        Assert.Equal(("x x", false, ""), setup.Host.Editors["Class: util"]);
        Assert.Equal("Script editor is not open", BridgeSetup.ToolText(await setup.ToolAsync("rc_replace_script_text", "{\"editor\":\"NPC: x\",\"expected\":\"x\",\"replacement\":\"y\"}"), expectError: true));
        Assert.Equal("Open script buffer edited; changes remain unsaved", BridgeSetup.ToolText(await setup.ToolAsync("rc_write_script_editor", "{\"editor\":\"Class: util\",\"text\":\"new \\\"text\\\" é\\n\"}")));
        Assert.Equal("new \"text\" é\n", setup.Host.Editors["Class: util"].Text);
        Assert.Equal("Script Save/Apply action invoked", BridgeSetup.ToolText(await setup.ToolAsync("rc_save_script_editor", "{\"editor\":\"Class: util\"}")));
        Assert.Equal("Script editor is not open", BridgeSetup.ToolText(await setup.ToolAsync("rc_save_script_editor", "{\"editor\":\"Class: none\"}"), expectError: true));
        Assert.Contains("save Class: util", setup.Host.Calls);
        Assert.Contains(setup.AuditLines(), line => line.Contains("\"tool\":\"rc_save_script_editor\",\"outcome\":\"failed\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActionTools()
    {
        using var folder = new TempFolder();
        await using var setup = new BridgeSetup(folder);
        Assert.Equal("Native login form submitted", BridgeSetup.ToolText(await setup.ToolAsync("rc_submit_login", "{\"account\":\"me\"}")));
        Assert.Contains("submit me/", setup.Host.Calls);
        Assert.Equal("Native server connection invoked", BridgeSetup.ToolText(await setup.ToolAsync("rc_connect_server", "{\"server\":\"Test Server\"}")));
        Assert.Equal("RC server session selected", BridgeSetup.ToolText(await setup.ToolAsync("rc_select_connection", "{\"server\":\"other\"}")));
        Assert.Equal("Connected RC server session not found", BridgeSetup.ToolText(await setup.ToolAsync("rc_select_connection", "{\"server\":\"nope\"}"), expectError: true));
        Assert.Equal("Managed RC view opened", BridgeSetup.ToolText(await setup.ToolAsync("rc_open_window", "{\"view\":\"player_list\"}")));
        Assert.Equal("Unsupported managed RC view", BridgeSetup.ToolText(await setup.ToolAsync("rc_open_window", "{\"view\":\"desktop\"}"), expectError: true));
        Assert.Equal("RC chat text sent", BridgeSetup.ToolText(await setup.ToolAsync("rc_send_chat", "{\"text\":\"hello\"}")));
        Assert.Equal("Chat text is empty", BridgeSetup.ToolText(await setup.ToolAsync("rc_send_chat", "{}"), expectError: true));
        Assert.Equal("Focused window action invoked", BridgeSetup.ToolText(await setup.ToolAsync("rc_focused_window_action", "{\"action\":\"present\"}")));
        Assert.Equal("No focused window or invalid action", BridgeSetup.ToolText(await setup.ToolAsync("rc_focused_window_action", "{\"action\":\"minimize\"}"), expectError: true));
        Assert.Equal(new[] { "open player_list", "chat hello", "focused present" }, setup.Host.Calls.Where(call => call.StartsWith("open", StringComparison.Ordinal) || call.StartsWith("chat h", StringComparison.Ordinal) || call.StartsWith("focused", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheServerScopeLimitsAllButConnectionTools()
    {
        using var folder = new TempFolder();
        await using var setup = new BridgeSetup(folder, o => o with { McpEnabled = true, McpFullControl = true, McpApprove = false, McpServerScope = "Other Server" });
        Assert.Equal((-32003, "Current server is outside configured MCP server scope"), BridgeSetup.ErrorOf(await setup.ToolAsync("rc_send_chat", "{\"text\":\"x\"}")));
        Assert.Equal((-32003, "Current server is outside configured MCP server scope"), BridgeSetup.ErrorOf(await setup.ToolAsync("rc_list_script_editors")));
        Assert.Equal((-32003, "Current server is outside configured MCP server scope"), BridgeSetup.ErrorOf(await setup.ToolAsync("rc_unknown_tool")));
        Assert.DoesNotContain(setup.Host.Calls, call => call.StartsWith("chat", StringComparison.Ordinal));
        Assert.False(BridgeSetup.ToolText(await setup.ToolAsync("rc_status")).Length == 0);
        Assert.Equal("RC server session selected", BridgeSetup.ToolText(await setup.ToolAsync("rc_select_connection", "{\"server\":\"Other\"}")));
        Assert.Contains(setup.AuditLines(), line => line.Contains("server-scope-denied", StringComparison.Ordinal));
        setup.Host.Server = new McpServerState("other server", true);
        Assert.Equal("RC chat text sent", BridgeSetup.ToolText(await setup.ToolAsync("rc_send_chat", "{\"text\":\"x\"}")));
    }

    [Fact]
    public async Task ApprovalsAreAskedAndAlwaysAllowIsSaved()
    {
        using var folder = new TempFolder();
        await using var setup = new BridgeSetup(folder, o => o with { McpEnabled = true, McpFullControl = true, McpApprove = true });
        setup.Host.Approve = false;
        Assert.Equal((-32002, "Mutation denied by user"), BridgeSetup.ErrorOf(await setup.ToolAsync("rc_send_chat", "{\"text\":\"hi\"}")));
        Assert.Contains("approve Send RC chat text", setup.Host.Calls);
        Assert.DoesNotContain("chat hi", setup.Host.Calls);
        Assert.Equal((-32002, "Mutation denied by user"), BridgeSetup.ErrorOf(await setup.ToolAsync("rc_focused_window_action", "{\"action\":\"close\"}")));
        Assert.Contains("approve Focused window action: close", setup.Host.Calls);
        setup.Host.Approve = true;
        Assert.Equal("RC chat text sent", BridgeSetup.ToolText(await setup.ToolAsync("rc_send_chat", "{\"text\":\"hi\"}")));

        setup.Host.ScriptApproval = McpApproval.Deny;
        Assert.Equal((-32002, "Mutation denied by user"), BridgeSetup.ErrorOf(await setup.ToolAsync("rc_write_script_editor", "{\"editor\":\"Weapon: bow\",\"text\":\"x\"}")));
        Assert.Contains("approve-script Edit Weapon: bow Weapon", setup.Host.Calls);
        Assert.Contains(setup.AuditLines(), line => line.Contains("approval-denied", StringComparison.Ordinal));

        setup.Host.ScriptApproval = McpApproval.AlwaysAllow;
        Assert.Equal("Open script buffer edited; changes remain unsaved", BridgeSetup.ToolText(await setup.ToolAsync("rc_write_script_editor", "{\"editor\":\"Weapon: bow\",\"text\":\"x\"}")));
        Assert.True(setup.Options.Current.McpApproveWeapon);
        Assert.Contains("mcpapproveweapon", File.ReadAllText(setup.Paths.OptionsFile));
        int questions = setup.Host.ApprovalQuestions;
        setup.Host.ScriptApproval = McpApproval.Deny;
        Assert.Equal("Script Save/Apply action invoked", BridgeSetup.ToolText(await setup.ToolAsync("rc_save_script_editor", "{\"editor\":\"Weapon: bow\"}")));
        Assert.Equal(questions, setup.Host.ApprovalQuestions);
        Assert.Equal((-32002, "Mutation denied by user"), BridgeSetup.ErrorOf(await setup.ToolAsync("rc_save_script_editor", "{\"editor\":\"Class: util\"}")));
        Assert.Contains("approve-script SaveApply Class: util Class", setup.Host.Calls);
        Assert.False(setup.Options.Current.McpApproveClass);

        // "Always allow" without a script type counts as allow once and saves nothing.
        setup.Host.Editors["Server Options"] = ("", false, "");
        setup.Host.ScriptApproval = McpApproval.AlwaysAllow;
        Assert.Equal("Open script buffer edited; changes remain unsaved", BridgeSetup.ToolText(await setup.ToolAsync("rc_write_script_editor", "{\"editor\":\"Server Options\",\"text\":\"y\"}")));
        Assert.False(setup.Options.Current.McpApproveNpc);
    }

    [Fact]
    public async Task AuditCanBeSwitchedOff()
    {
        using var folder = new TempFolder();
        await using var setup = new BridgeSetup(folder, o => o with { McpEnabled = true, McpFullControl = true, McpApprove = false, McpAudit = false });
        await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
        await setup.ToolAsync("rc_status");
        Assert.False(File.Exists(setup.Paths.McpAuditLogFile));
    }

    [Fact]
    public async Task HostCallsRunOnTheGivenContext()
    {
        using var folder = new TempFolder();
        using var ui = new UiThread();
        await using var setup = new BridgeSetup(folder, ui: ui);
        foreach (string tool in McpTools.AllNames.Except(McpTools.ServerToolNames)) await setup.ToolAsync(tool, "{\"editor\":\"Weapon: bow\",\"text\":\"t\",\"expected\":\"t\",\"replacement\":\"u\",\"view\":\"npcs\",\"action\":\"present\",\"server\":\"Alpaxis\",\"query\":\"t\",\"title\":\"x\"}");
        await setup.CallAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"rc/instance\"}");
        Assert.True(setup.Host.Calls.Count > 20);
        Assert.Equal(0, setup.Host.WrongThread);
        Assert.Empty(ui.Errors);
    }

    [Fact]
    public async Task AfterDisposalPendingHostCallsAreCancelled()
    {
        using var folder = new TempFolder();
        var posted = new ConcurrentQueue<Action>();
        var setup = new BridgeSetup(folder);
        var bridge = new McpBridge(new McpBridgeOptions { Host = setup.Host, Options = setup.Options, Post = posted.Enqueue, AuditLogFile = setup.Paths.McpAuditLogFile, Transport = setup.Transport });
        Task<string?> pending = bridge.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"rc_status\"}}");
        await Wait.UntilAsync(() => !posted.IsEmpty, "the host call to be posted");
        await Wait.WithTimeout(bridge.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait.WithTimeout(pending));
        while (posted.TryDequeue(out Action? action)) action();
        Assert.Empty(setup.Host.Calls);
        Assert.Equal("The MCP bridge is closed", bridge.Start().Error);
        await setup.DisposeAsync();
    }
}
