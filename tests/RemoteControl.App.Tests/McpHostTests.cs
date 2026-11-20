using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.App.Mcp;
using RemoteControl.Extensions;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.App.Tests;

public sealed class McpHostTests
{
    private sealed class Rig : IDisposable
    {
        public Rig(Func<Core.RcOptions, Core.RcOptions>? options = null)
        {
            Harness = new ShellHarness(options);
            Host = new McpHost(Harness.Shell, Approvals);
        }

        public ShellHarness Harness { get; }

        public FakeApprovals Approvals { get; } = new();

        public McpHost Host { get; }

        public void Dispose() => Harness.Dispose();
    }

    [AvaloniaFact]
    public async Task Server_state_connections_and_selecting_one()
    {
        using var rig = new Rig();
        Assert.Equal(new McpServerState(string.Empty, false), await rig.Host.GetServerStateAsync());
        Assert.Empty(await rig.Host.ListConnectionsAsync());

        SessionContext alpha = await rig.Harness.ConnectedAsync("Alpha");
        await rig.Harness.ConnectedAsync("Beta");
        await rig.Harness.ConnectedAsync("Down");
        rig.Harness.Made[2].IsConnected = false;

        Assert.Equal(new McpServerState("Down", false), await rig.Host.GetServerStateAsync());
        Assert.Equal(new[] { "Alpha", "Beta" }, await rig.Host.ListConnectionsAsync());

        McpActionResult selected = await rig.Host.SelectConnectionAsync("alpha");
        Assert.True(selected.Success);
        Assert.Same(alpha, rig.Harness.Shell.ActiveSession);
        Assert.Equal(new McpServerState("Alpha", true), await rig.Host.GetServerStateAsync());

        McpActionResult missing = await rig.Host.SelectConnectionAsync("Down");
        Assert.Equal("Connected RC server session not found", missing.Error);
        Assert.Same(alpha, rig.Harness.Shell.ActiveSession);
    }

    [AvaloniaFact]
    public async Task Login_state_submit_servers_and_connect_go_to_the_start_windows()
    {
        using var rig = new Rig();
        rig.Harness.Start.ServerNames.AddRange(["Alpha", "Beta"]);
        rig.Harness.Start.Connect = name => rig.Harness.ConnectAsync(name);

        Assert.Equal(new McpLoginState(true, "staff", "Staff", true), await rig.Host.GetLoginStateAsync());

        Assert.True((await rig.Host.SubmitLoginAsync("other", "Nick")).Success);
        Assert.Contains("Submit:other:Nick", rig.Harness.Start.Calls);
        rig.Harness.Start.SubmitResult = ConnectResult.Failed("The existing login form has no password");
        Assert.Equal("The existing login form has no password", (await rig.Host.SubmitLoginAsync(string.Empty, string.Empty)).Error);

        Assert.Equal(new[] { "Alpha", "Beta" }, await rig.Host.ListServersAsync());
        Assert.True((await rig.Host.ConnectServerAsync("Beta")).Success);
        Assert.Equal("Beta", Assert.Single(rig.Harness.Shell.Sessions).ServerName);
    }

    [AvaloniaFact]
    public async Task Windows_are_listed_with_their_tabs_and_the_focused_one_can_be_presented_or_closed()
    {
        using var rig = new Rig();
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "RC Chat" });
        tabs.Items.Add(new TabItem { Header = new TextBlock { Text = "NC" } });
        var window = new Window { Title = "Tabs", Content = tabs };
        var hidden = new Window { Title = "Hidden" };
        rig.Harness.Desktop.OpenWindows.AddRange([window, hidden]);
        rig.Harness.Environment.Show(window, 300, 200);

        IReadOnlyList<McpWindowInfo> windows = await rig.Host.ListWindowsAsync();

        McpWindowInfo info = Assert.Single(windows);
        Assert.Equal("Tabs", info.Title);
        Assert.Equal(new[] { "RC Chat", "NC" }, info.Tabs);

        Assert.Equal(window.IsActive ? string.Empty : "No focused window or invalid action", (await rig.Host.FocusedWindowActionAsync("present")).Error);
        Assert.Equal("No focused window or invalid action", (await rig.Host.FocusedWindowActionAsync("shake")).Error);
        if (window.IsActive)
        {
            Assert.True((await rig.Host.FocusedWindowActionAsync("close")).Success);
            Assert.False(window.IsVisible);
        }
    }

    [AvaloniaFact]
    public async Task Script_editors_of_every_session_are_listed_updated_and_saved()
    {
        using var rig = new Rig();
        SessionContext alpha = await rig.Harness.ConnectedAsync("Alpha");
        SessionContext beta = await rig.Harness.ConnectedAsync("Beta");
        var alphaEditors = new FakeEditors();
        alphaEditors.Add("Weapon: bow", "function onCreated() {}");
        var betaEditors = new FakeEditors();
        betaEditors.Add("Class: util", "//util", modified: true);
        using IDisposable a = alpha.Services.Provide<IScriptEditors>(alphaEditors);
        using IDisposable b = beta.Services.Provide<IScriptEditors>(betaEditors);

        IReadOnlyList<McpScriptEditorInfo> editors = await rig.Host.ListScriptEditorsAsync();
        Assert.Equal(new[] { "Class: util", "Weapon: bow" }, editors.Select(editor => editor.Name).ToArray());
        Assert.True(editors[0].Modified);
        Assert.Equal("function onCreated() {}", editors[1].Text);

        McpActionResult replaced = await rig.Host.UpdateScriptEditorAsync("Weapon: bow", text => McpTextEdit.Replace(text.Replace("{}", "{ echo(1); }")));
        Assert.True(replaced.Success);
        Assert.Equal("function onCreated() { echo(1); }", alphaEditors.Open[0].Text);
        Assert.True(alphaEditors.Open[0].IsModified);

        McpActionResult refused = await rig.Host.UpdateScriptEditorAsync("Weapon: bow", _ => McpTextEdit.Refuse("Expected text is not unique in the current buffer"));
        Assert.Equal("Expected text is not unique in the current buffer", refused.Error);
        Assert.Equal("Script editor is not open", (await rig.Host.UpdateScriptEditorAsync("NPC: door", McpTextEdit.Replace)).Error);

        Assert.True((await rig.Host.SaveScriptEditorAsync("Class: util")).Success);
        Assert.Equal("Class: util", Assert.Single(betaEditors.Saved));
        Assert.Equal("Script editor is not open", (await rig.Host.SaveScriptEditorAsync("Weapon: none")).Error);
        betaEditors.SaveResult = false;
        Assert.False((await rig.Host.SaveScriptEditorAsync("Class: util")).Success);
    }

    [AvaloniaFact]
    public async Task Views_open_the_tools_of_the_active_session()
    {
        using var rig = new Rig();
        Assert.Equal("No connected RC server window", (await rig.Host.OpenViewAsync("toalls")).Error);
        SessionContext session = await rig.Harness.ConnectedAsync("Alpha");
        var opened = new List<string>();
        foreach ((string view, string tool) in McpHost.ViewTools)
        {
            string id = tool;
            rig.Harness.Tools.Register(id, (_, _) =>
            {
                opened.Add(id);
                return session.Windows.Find(ToolIds.MainWindow);
            });
            Assert.True((await rig.Host.OpenViewAsync(view)).Success, view);
        }

        Assert.Equal(McpProtocol.Views.Select(view => McpHost.ViewTools[view]).ToArray(), opened.ToArray());
        Assert.Equal(McpProtocol.Views.Order().ToArray(), McpHost.ViewTools.Keys.Order().ToArray());
        Assert.Equal("Unsupported managed RC view", (await rig.Host.OpenViewAsync("minimap")).Error);
        rig.Harness.Tools.Register(ToolIds.Npcs, (_, _) => null);
        Assert.False((await rig.Host.OpenViewAsync("npcs")).Success);
    }

    [AvaloniaFact]
    public async Task Chat_is_sent_through_the_main_window_of_the_active_session()
    {
        using var rig = new Rig();
        Assert.Equal("No connected RC server window", (await rig.Host.SendChatAsync("hello")).Error);
        await rig.Harness.ConnectedAsync("Alpha");
        Assert.Equal("Chat text is empty", (await rig.Host.SendChatAsync(string.Empty)).Error);

        McpActionResult sent = await rig.Host.SendChatAsync("hello there");

        Assert.True(sent.Success);
        Assert.Contains(rig.Harness.Made[0].Calls, call => call.Method == nameof(IRcSession.SendRcChatAsync) && (string?)call.Arguments[0] == "hello there");

        rig.Harness.Made[0].IsConnected = false;
        Assert.Equal("RC is not connected", (await rig.Host.SendChatAsync("hello")).Error);
    }

    [AvaloniaFact]
    public async Task Server_tools_find_sessions_whose_journal_outlives_a_reconnect()
    {
        using var rig = new Rig();
        Assert.Null(await rig.Host.FindSessionAsync(string.Empty));
        SessionContext alpha = await rig.Harness.ConnectedAsync("Alpha");
        SessionContext beta = await rig.Harness.ConnectedAsync("Beta");
        rig.Harness.Shell.ActiveSession = alpha;

        McpSessionTarget selected = (await rig.Host.FindSessionAsync(string.Empty))!;
        Assert.Equal("Alpha", selected.ServerName);
        Assert.Same(beta.Session, (await rig.Host.FindSessionAsync("BETA"))!.Session);
        Assert.Null(await rig.Host.FindSessionAsync("Gamma"));

        rig.Harness.Made[0].RaiseRcChat("before");
        IDisposable pending = rig.Host.ClaimAnswer(selected, "rights", "bob");
        Assert.True((await rig.Harness.Shell.ReconnectAsync(alpha)).Success);
        // A claim was made for a request on the old connection, whose answer will not come: it must not take the user's next one.
        Assert.Equal(0, alpha.ClaimedAnswers.Count);
        pending.Dispose();
        FakeRcSession second = rig.Harness.Made[^1];
        Assert.NotSame(rig.Harness.Made[0], second);
        second.RaiseRcChat("after");
        McpSessionTarget again = (await rig.Host.FindSessionAsync("alpha"))!;
        Assert.Same(selected.Journal, again.Journal);
        Assert.Same(second, again.Session);
        Assert.Equal(new[] { "before", "after" }, again.Journal.Read(0, new[] { McpSessionJournal.Rc }).Select(entry => entry.Text));

        McpSessionTarget betaTarget = (await rig.Host.FindSessionAsync("Beta"))!;
        Assert.True((await rig.Host.EnterChatLineAsync(betaTarget, "/find bob")).Success);
        Assert.Contains(rig.Harness.Made[1].Calls, call => call.Method == nameof(IRcSession.SendRcChatAsync) && (string?)call.Arguments[0] == "/find bob");
        var stranger = new McpSessionTarget("Gamma", rig.Harness.Made[1], new McpSessionJournal());
        Assert.Equal("Connected RC server session not found", (await rig.Host.EnterChatLineAsync(stranger, "x")).Error);

        // A claimed answer is not shown by the windows; the claim is gone once taken or let go.
        using (rig.Host.ClaimAnswer(betaTarget, "rights", "bob"))
        {
            Assert.Equal(1, beta.ClaimedAnswers.Count);
            Assert.True(beta.ClaimedAnswers.TryTake("rights", "BOB"));
            Assert.False(beta.ClaimedAnswers.TryTake("rights", "bob"));
        }
        IDisposable any = rig.Host.ClaimAnswer(betaTarget, "accountlist", ClaimedAnswers.Any);
        any.Dispose();
        Assert.Equal(0, beta.ClaimedAnswers.Count);

        ScriptRequest? requested = null;
        rig.Harness.Tools.Register(ToolIds.ScriptEditor, (context, argument) =>
        {
            requested = argument as ScriptRequest;
            return context.Windows.Find(ToolIds.MainWindow);
        });
        Assert.True((await rig.Host.OpenScriptEditorAsync(again, "npc", "door", 7)).Success);
        Assert.Equal(new ScriptRequest(RemoteControl.Ui.Shell.ScriptKind.Npc, "door", 7), requested);
        Assert.False((await rig.Host.OpenScriptEditorAsync(again, "level", "x", 0)).Success);
    }

    [AvaloniaFact]
    public async Task A_window_that_switches_server_starts_its_journal_again()
    {
        using var rig = new Rig();
        SessionContext session = await rig.Harness.ConnectedAsync("Alpha");
        McpSessionJournal journal = (await rig.Host.FindSessionAsync("Alpha"))!.Journal;
        rig.Harness.Made[0].RaisePrivateMessage(3, "friend", "Friend", "secret of Alpha");
        Assert.True((await rig.Harness.ConnectAsync("Beta", session)).Success);
        McpSessionTarget beta = (await rig.Host.FindSessionAsync("Beta"))!;
        Assert.Same(journal, beta.Journal);
        McpJournalEntry only = Assert.Single(beta.Journal.Read(0));
        Assert.Equal(McpSessionJournal.Connection, only.Kind);
        Assert.Contains("from Alpha to Beta", only.Text);
    }

    [AvaloniaFact]
    public async Task Approvals_are_asked_through_the_questions()
    {
        using var rig = new Rig();
        rig.Approvals.Allow = true;
        rig.Approvals.ScriptAnswer = McpApproval.AlwaysAllow;

        Assert.True(await rig.Host.ApproveAsync("Send RC chat text"));
        Assert.Equal(McpApproval.AlwaysAllow, await rig.Host.ApproveScriptAsync(McpScriptAction.Edit, "Weapon: bow", "Weapon"));
        Assert.Equal(new[] { "Send RC chat text", "Edit:Weapon: bow:Weapon" }, rig.Approvals.Questions);
    }

    [AvaloniaFact]
    public async Task Through_the_bridge_a_denied_approval_refuses_the_action_and_is_audited()
    {
        using var rig = new Rig(options => options with { McpEnabled = true, McpFullControl = true, McpApprove = true, McpAudit = true });
        await rig.Harness.ConnectedAsync("Alpha");
        string audit = Path.Combine(rig.Harness.Environment.UserDirectory, "audit.log");
        using var bridge = new McpBridge(new McpBridgeOptions { Host = rig.Host, Options = rig.Harness.Environment.Services.Options, Post = action => action(), AuditLogFile = audit });

        string? response = await bridge.HandleAsync("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"rc_send_chat","arguments":{"text":"hi"}}}""");

        Assert.NotNull(response);
        Assert.Contains("-32002", response);
        Assert.Contains("Mutation denied by user", response);
        Assert.Equal("Send RC chat text", Assert.Single(rig.Approvals.Questions));
        Assert.DoesNotContain(rig.Harness.Made[0].Calls, call => call.Method == nameof(IRcSession.SendRcChatAsync) && (string?)call.Arguments[0] == "hi");
        Assert.Contains("approval-denied", File.ReadAllText(audit));

        rig.Approvals.Allow = true;
        response = await bridge.HandleAsync("""{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"rc_send_chat","arguments":{"text":"hi"}}}""");
        Assert.Contains("\"isError\":false", response);
        Assert.Contains(rig.Harness.Made[0].Calls, call => call.Method == nameof(IRcSession.SendRcChatAsync) && (string?)call.Arguments[0] == "hi");
    }

    [AvaloniaFact]
    public async Task Through_the_bridge_a_script_edit_needs_the_script_approval_and_always_allow_is_saved()
    {
        using var rig = new Rig(options => options with { McpEnabled = true, McpFullControl = true, McpApprove = true, McpAudit = false });
        SessionContext session = await rig.Harness.ConnectedAsync("Alpha");
        var editors = new FakeEditors();
        editors.Add("Weapon: bow", "old");
        using IDisposable provided = session.Services.Provide<IScriptEditors>(editors);
        using var bridge = new McpBridge(new McpBridgeOptions { Host = rig.Host, Options = rig.Harness.Environment.Services.Options, Post = action => action(), AuditLogFile = Path.Combine(rig.Harness.Environment.UserDirectory, "a.log") });
        const string write = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"rc_write_script_editor","arguments":{"editor":"Weapon: bow","text":"new"}}}""";

        string? denied = await bridge.HandleAsync(write);
        Assert.Contains("Mutation denied by user", denied);
        Assert.Equal("old", editors.Open[0].Text);

        rig.Approvals.ScriptAnswer = McpApproval.AlwaysAllow;
        await bridge.HandleAsync(write);
        Assert.Equal("new", editors.Open[0].Text);
        Assert.True(rig.Harness.Environment.Options.McpApproveWeapon);

        rig.Approvals.ScriptAnswer = McpApproval.Deny;
        await bridge.HandleAsync(write.Replace("\"new\"", "\"newer\""));
        Assert.Equal("newer", editors.Open[0].Text);
        Assert.Equal(2, rig.Approvals.Questions.Count);
    }

    [AvaloniaFact]
    public async Task The_bridge_follows_the_option_live()
    {
        string sockets = Path.Combine(Path.GetTempPath(), "rc-mcp-" + Guid.NewGuid().ToString("N")[..8]);
        using var rig = new Rig(options => options with { McpEnabled = false });
        using var controller = new McpBridgeController(rig.Harness.Environment.Services, rig.Host, new McpTransportOptions { SocketDirectory = sockets });
        Assert.Null(controller.Bridge);

        rig.Harness.Environment.SetOptions(options => options with { McpEnabled = true });
        await TestUi.WaitUntilAsync(() => controller.Bridge is not null);
        Assert.True(controller.Bridge!.IsListening);

        rig.Harness.Environment.SetOptions(options => options with { McpEnabled = false });
        await TestUi.WaitUntilAsync(() => controller.Bridge is null);
        controller.Dispose();
        try
        {
            Directory.Delete(sockets, recursive: true);
        }
        catch (IOException)
        {
            // Removed by the bridge already.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [AvaloniaFact]
    public void The_approval_window_answers_with_its_buttons_and_denies_when_closed()
    {
        using var env = new TestEnvironment(theme: "dark");
        var window = new McpApprovalWindow(env.Services, "RC MCP Script Approval", "Edit the open script buffer without saving or applying it?\n\nWeapon: bow", "Always allow Weapon scripts");
        env.Show(window, 420, 160);
        Shots.Save(window, "app-mcp-script-approval-dark");
        TestUi.Click(TestUi.Find<Button>(window, McpApprovalWindow.AlwaysName));
        Assert.Equal(McpApproval.AlwaysAllow, window.Answer.Result);

        var simple = new McpApprovalWindow(env.Services, "RC MCP Approval", "Allow this MCP action?\n\nSend RC chat text", null);
        env.Show(simple, 420, 140);
        Assert.Null(TestUi.TryFind<Button>(simple, McpApprovalWindow.AlwaysName));
        env.SetOptions(options => options with { Theme = "light", DarkMode = false });
        Shots.Save(simple, "app-mcp-approval-light");
        simple.Close();
        Assert.Equal(McpApproval.Deny, simple.Answer.Result);
    }
}
