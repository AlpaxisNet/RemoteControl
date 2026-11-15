using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Extensions;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Extensions.Tests;

public sealed class ExtensionsHostTests
{
    private static ScriptEditorSnapshot Editor(string text = "function onCreated() {\n  echo(\"hi\\there\");\t\u0001\n}", string selection = "echo") =>
        new("Weapon: -bow", ScriptKind.Weapon, "-bow", true, true, text, selection);

    [AvaloniaFact]
    public async Task Switching_the_option_off_and_on_while_window_and_consumer_hold_the_host_never_throws()
    {
        if (!TestWorld.ShellAvailable) return;
        await using var world = new TestWorld();
        world.WriteEcho();
        world.WriteState("test.echo 1 1\n");
        ExtensionsHost host = world.Host;
        ExtensionsWindow window = world.ShowWindow();
        var consumer = world.Test.Shell.GetService<IExtensionActions>()!;
        int changes = 0;
        consumer.Changed += (_, _) => changes++;
        await world.WaitForStatusAsync("test.echo", ExtensionStatus.Running);
        Assert.Equal(2, consumer.ActionsFor("script-editor").Count);
        ExtensionRow staleRow = window.Rows.Single();
        int reported = UiSafe.ReportedCount;

        for (int round = 0; round < 2; round++)
        {
            int before = changes;
            world.Test.Environment.SetOptions(o => o with { ExtensionsEnabled = false });
            Assert.False(host.IsEnabled);
            Assert.Empty(consumer.ActionsFor("script-editor"));
            Assert.True(changes > before);
            consumer.Invoke("test.echo/lint", world.Test.Context, Editor());
            Assert.Empty(host.Extensions);
            Assert.Null(host.Find("test.echo"));
            Assert.Equal(string.Empty, host.GetLog("test.echo"));
            Assert.False(host.SetEnabled("test.echo", true).Success);
            Assert.False(host.SetOutputTab("test.echo", true).Success);
            Assert.False(host.SetApprovedCapabilities("test.echo", ["script-read"]).Success);
            Assert.False((await host.RemoveAsync("test.echo", host.Generation)).Success);
            await host.RescanAsync();
            TestUi.Settle();
            Assert.True(window.DisabledBanner.IsVisible);
            Assert.Empty(window.Rows);
            staleRow.Enabled = !staleRow.Enabled;
            await window.RemoveAsync(staleRow);
            if (TestWorld.OpenDialog() is { } error) error.Accept();
            if (round == 0) Shots.Save(window, "extensions-switched-off");
            await host.WhenStoppedAsync();

            world.Test.Environment.SetOptions(o => o with { ExtensionsEnabled = true });
            Assert.True(host.IsEnabled);
            await TestUi.WaitUntilAsync(() => window.Rows.Count == 1, what: "the list again");
            Assert.False(window.DisabledBanner.IsVisible);
            await world.WaitForStatusAsync("test.echo", ExtensionStatus.Running);
            Assert.Equal(2, consumer.ActionsFor("script-editor").Count);
        }
        Assert.Equal(reported, UiSafe.ReportedCount);
    }

    [AvaloniaFact]
    public async Task Disposed_host_works_as_no_extensions()
    {
        await using var world = new TestWorld();
        world.WriteIdle(world.UserRoot, "idle", "test.idle", "Idle");
        ExtensionsHost host = world.Host;
        ExtensionsWindow window = world.ShowWindow();
        host.Dispose();
        Assert.True(window.IsClosed);
        Assert.False(host.IsEnabled);
        Assert.Empty(host.ActionsFor("script-editor"));
        host.Invoke("test.idle/x", world.Test.Context, Editor());
        world.Test.Environment.SetOptions(o => o with { ExtensionsEnabled = false });
        world.Test.Environment.SetOptions(o => o with { ExtensionsEnabled = true });
        Assert.False(host.IsEnabled);
        host.Dispose();
    }

    [AvaloniaFact]
    public async Task ActionsFor_resolves_the_icons_and_Invoke_sends_valid_json_with_the_editors_context()
    {
        if (!TestWorld.ShellAvailable) return;
        await using var world = new TestWorld();
        string folder = world.WriteEcho();
        world.WriteState("test.echo 1 0\n");
        ExtensionsHost host = world.Host;
        await world.WaitForStatusAsync("test.echo", ExtensionStatus.Running);

        IReadOnlyList<WindowActionButton> actions = host.ActionsFor("script-editor");
        Assert.Equal(2, actions.Count);
        WindowActionButton lint = actions[0];
        Assert.Equal("Lint", lint.Label);
        Assert.True(lint.AtBottom);
        Assert.Equal(Path.Combine(folder, "images", "lint.png"), lint.IconPath);
        WindowActionButton format = actions[1];
        Assert.Equal("Format", format.Label);
        Assert.False(format.AtBottom);
        Assert.Null(format.IconPath);
        Assert.Empty(host.ActionsFor("file-editor"));
        Assert.Empty(host.ActionsFor(""));

        ScriptEditorSnapshot editor = Editor();
        host.Invoke(lint.Id, world.Test.Context, editor);
        string received = Path.Combine(folder, "received.txt");
        string? request = null;
        await TestUi.WaitUntilAsync(() =>
        {
            if (!File.Exists(received)) return false;
            request = File.ReadAllLines(received).FirstOrDefault(line => line.Contains("window/action", StringComparison.Ordinal));
            return request is not null;
        }, TimeSpan.FromSeconds(5), "the request at the runtime");

        using JsonDocument document = JsonDocument.Parse(request!);
        JsonElement root = document.RootElement;
        Assert.Equal("window/action", root.GetProperty("method").GetString());
        Assert.StartsWith("window-action-", root.GetProperty("id").GetString());
        JsonElement parameters = root.GetProperty("params");
        Assert.Equal("lint", parameters.GetProperty("action").GetString());
        JsonElement context = parameters.GetProperty("context");
        Assert.Equal("weapons:-bow", context.GetProperty("windowId").GetString());
        Assert.Equal("script-editor", context.GetProperty("kind").GetString());
        Assert.Equal("Weapon: -bow", context.GetProperty("title").GetString());
        Assert.Equal("weapons", context.GetProperty("scriptType").GetString());
        Assert.Equal("-bow", context.GetProperty("scriptName").GetString());
        Assert.Equal(editor.Text, context.GetProperty("text").GetString());
        Assert.Equal("echo", context.GetProperty("selection").GetString());

        host.Invoke("test.echo/no-such-action", world.Test.Context, editor);
        Assert.Contains(world.Test.Environment.Log.Snapshot(), line => line.Contains("no-such-action", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Invoking_an_action_of_a_stopped_runtime_says_why()
    {
        if (!TestWorld.ShellAvailable) return;
        await using var world = new TestWorld();
        world.WritePackage(world.UserRoot, "quitter", """
            { "id": "test.quit", "name": "Quitter", "version": "1", "runtime": "/bin/sh", "entry": "main.sh",
              "windowActions": [ { "id": "go", "label": "Go" } ] }
            """, ("main.sh", "exit 3"));
        world.WriteState("test.quit 1 0\n");
        ExtensionsHost host = world.Host;
        await world.WaitForStatusAsync("test.quit", ExtensionStatus.Failed);
        Assert.Equal("Runtime exited unexpectedly", host.Find("test.quit")!.Error);
        WindowActionButton go = host.ActionsFor("script-editor").Single();
        host.Invoke(go.Id, world.Test.Context, Editor());
        RcDialog dialog = await TestWorld.WaitForDialogAsync();
        Assert.Contains("Run Go from Quitter failed", dialog.Message);
        Assert.Contains("not running", dialog.Message);
        dialog.Accept();
    }

    [AvaloniaFact]
    public async Task Output_reaches_the_active_sessions_chat_with_the_log_so_far_and_the_tab_closes()
    {
        if (!TestWorld.ShellAvailable) return;
        await using var world = new TestWorld();
        world.WriteEcho();
        world.WriteState("test.echo 1 1\n");
        ExtensionsHost host = world.Host;
        await TestUi.WaitUntilAsync(() => host.GetLog("test.echo").Contains("ready to go"), TimeSpan.FromSeconds(5), "the first line");
        Assert.Contains("test.echo", host.Router.OpenTabs);

        var chat = new FakeChat();
        world.Test.Context.Services.Provide<ISessionChat>(chat);
        TestUi.Settle();
        Assert.Contains("ready to go", chat.LinesOf("Echo"));
        Assert.Contains("Echo", chat.Tabs);

        WindowActionButton lint = host.ActionsFor("script-editor")[0];
        host.Invoke(lint.Id, world.Test.Context, Editor());
        await TestUi.WaitUntilAsync(() => chat.LinesOf("Echo").Contains("action received"), TimeSpan.FromSeconds(5), "the answer in the tab");
        Assert.Equal(1, chat.LinesOf("Echo").Count(line => line == "ready to go"));

        Assert.True(host.SetOutputTab("test.echo", false).Success);
        await TestUi.WaitUntilAsync(() => chat.Removed.Contains("Echo"), what: "the tab removed");
        Assert.DoesNotContain("test.echo", host.Router.OpenTabs);
        Assert.True(host.SetOutputTab("test.echo", true).Success);
        await TestUi.WaitUntilAsync(() => chat.Tabs.Contains("Echo"), what: "the tab again");
        Assert.Contains("action received", chat.LinesOf("Echo"));
    }

    [AvaloniaFact]
    public async Task Output_follows_the_session_whose_editor_invoked_the_action_and_a_replacing_main_window()
    {
        if (!TestWorld.ShellAvailable) return;
        await using var world = new TestWorld();
        world.WriteEcho();
        world.WriteState("test.echo 1 1\n");
        var activeChat = new FakeChat();
        world.Test.Context.Services.Provide<ISessionChat>(activeChat);

        var otherFake = new FakeRcSession("other");
        var other = new SessionContext(world.Test.Services, world.Test.Shell, world.Test.Tools, otherFake, world.Test.Login with { Account = "other" });
        world.Test.Shell.Add(other);
        world.Test.Shell.ActiveSession = world.Test.Context;
        var otherChat = new FakeChat();
        IDisposable otherProvided = other.Services.Provide<ISessionChat>(otherChat);
        try
        {
            ExtensionsHost host = world.Host;
            await TestUi.WaitUntilAsync(() => activeChat.LinesOf("Echo").Contains("ready to go"), TimeSpan.FromSeconds(5), "the first line in the active session");
            Assert.Empty(otherChat.Appended);

            host.Invoke(host.ActionsFor("script-editor")[0].Id, other, Editor());
            await TestUi.WaitUntilAsync(() => otherChat.LinesOf("Echo").Contains("action received"), TimeSpan.FromSeconds(5), "the answer in the other session");
            Assert.Contains("ready to go", otherChat.LinesOf("Echo"));
            Assert.Contains("Echo", activeChat.Removed);

            otherProvided.Dispose();
            var replacement = new FakeChat();
            otherProvided = other.Services.Provide<ISessionChat>(replacement);
            TestUi.Settle();
            Assert.Contains("action received", replacement.LinesOf("Echo"));

            other.End();
            world.Test.Shell.SignOut(other);
            TestUi.Settle();
            Assert.Contains("action received", activeChat.LinesOf("Echo"));
        }
        finally
        {
            otherProvided.Dispose();
            other.End();
        }
    }

    [AvaloniaFact]
    public async Task Switching_off_removes_the_output_tabs()
    {
        if (!TestWorld.ShellAvailable) return;
        await using var world = new TestWorld();
        world.WriteEcho();
        world.WriteState("test.echo 1 1\n");
        var chat = new FakeChat();
        world.Test.Context.Services.Provide<ISessionChat>(chat);
        ExtensionsHost host = world.Host;
        await TestUi.WaitUntilAsync(() => chat.LinesOf("Echo").Contains("ready to go"), TimeSpan.FromSeconds(5), "the first line");
        world.Test.Environment.SetOptions(o => o with { ExtensionsEnabled = false });
        Assert.Contains("Echo", chat.Removed);
        Assert.Empty(host.Router.OpenTabs);
    }

    [AvaloniaFact]
    public async Task The_tool_opens_the_window()
    {
        await using var world = new TestWorld();
        world.WriteIdle(world.UserRoot, "idle", "test.idle", "Idle");
        ExtensionsFeature.Register(world.Test.Tools, world.Host);
        Window? window = world.Test.Context.OpenTool(ToolIds.Extensions);
        Assert.IsType<ExtensionsWindow>(window);
        Assert.True(window!.IsVisible);
        Assert.Same(window, world.Test.Context.OpenTool(ToolIds.Extensions));
    }

    [AvaloniaFact]
    public async Task The_tool_says_when_extensions_are_off_and_offers_the_options()
    {
        await using var world = new TestWorld(enabled: false);
        world.Test.RecordTools(ToolIds.RcOptions);
        ExtensionsFeature.Register(world.Test.Tools, world.Host);
        Assert.Null(world.Test.Context.OpenTool(ToolIds.Extensions));
        RcDialog dialog = await TestWorld.WaitForDialogAsync();
        Assert.Equal(ExtensionsFeature.SwitchedOffMessage, dialog.Message);
        Shots.Save(dialog, "extensions-off-dialog");
        dialog.Cancel();
        TestUi.Settle();
        Assert.Empty(world.Test.OpenedTools);

        Assert.Null(world.Test.Context.OpenTool(ToolIds.Extensions));
        dialog = await TestWorld.WaitForDialogAsync();
        dialog.Accept();
        await TestUi.WaitUntilAsync(() => world.Test.OpenedTools.Count == 1, what: "the options opened");
        Assert.Equal(ToolIds.RcOptions, world.Test.OpenedTools[0].ToolId);
        Assert.Null(world.Host.Window);
    }
}
