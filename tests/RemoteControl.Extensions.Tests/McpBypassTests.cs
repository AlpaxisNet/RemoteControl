using System.Text.Json;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Extensions.Tests;

/// <summary>The option <c>mcpbypass</c>, the commands <c>rc_command</c> runs without asking, and denials of a headless program.</summary>
public sealed class McpBypassTests
{
    /// <summary>No permission group and approvals on: only the bypass lets anything through.</summary>
    private static RcOptions Bypassed(RcOptions options) => options with
    {
        McpEnabled = true, McpRead = false, McpWrite = false, McpServer = false, McpLogin = false, McpWindows = false, McpAdmin = false,
        McpFullControl = false, McpApprove = true, McpBypass = true,
    };

    [Fact]
    public async Task The_bypass_allows_every_tool_and_asks_nothing()
    {
        await using var rig = new ServerRig(Bypassed);
        // A question, if one were asked, would be denied.
        rig.Setup.Host.Approve = false;
        rig.Setup.Host.ScriptApproval = McpApproval.Deny;

        JsonElement list = await rig.Setup.CallAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.Equal(McpTools.AllNames.Count, list.GetProperty("result").GetProperty("tools").GetArrayLength());

        JsonElement command = await rig.OkAsync("rc_command", """{"text":"/restartserver","collectMilliseconds":0}""");
        Assert.True(command.GetProperty("sent").GetBoolean());
        Assert.Equal("/restartserver", rig.Fake.LastCall(nameof(IRcSession.SendRcChatAsync))!.Argument<string>(0));

        // What approvals ask every time is not asked either: a ban on every server, a script delete.
        await rig.OkAsync("rc_set_ban", """{"target":"bob","world":"all","noEnd":true,"collectMilliseconds":0}""");
        Assert.True(rig.Fake.WasCalled(nameof(IRcSession.SetBanAsync)));
        rig.Nc.Weapons["bow"] = ("", "a\n");
        rig.Nc.Publish();
        await rig.OkAsync("rc_delete_script", """{"kind":"weapon","name":"bow","collectMilliseconds":0}""");
        Assert.False(rig.Nc.Weapons.ContainsKey("bow"));
        // The program's own tools too.
        Assert.Equal("RC chat text sent", BridgeSetup.ToolText(await rig.CallAsync("rc_send_chat", """{"text":"hello"}""")));

        Assert.Equal(0, rig.Setup.Host.ApprovalQuestions);
        // Backups are still kept.
        Assert.NotEmpty(rig.BackupFiles());
        JsonElement status = await rig.OkAsync("rc_status");
        Assert.False(status.GetProperty("approvals").GetBoolean());
        Assert.True(status.GetProperty("bypass").GetBoolean());
        Assert.Equal(0, rig.WrongThread);
    }

    [Fact]
    public async Task Without_the_bypass_the_groups_and_approvals_apply_as_before()
    {
        await using var rig = new ServerRig(o => Bypassed(o) with { McpBypass = false, McpRead = true });
        JsonElement list = await rig.Setup.CallAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.True(list.GetProperty("result").GetProperty("tools").GetArrayLength() < McpTools.AllNames.Count);
        Assert.Equal((-32602, "Tool unavailable or disabled by MCP scope"), BridgeSetup.ErrorOf(await rig.CallAsync("rc_command", """{"text":"/find *.nw"}""")));
        JsonElement status = await rig.OkAsync("rc_status");
        Assert.True(status.GetProperty("approvals").GetBoolean());
        Assert.False(status.GetProperty("bypass").GetBoolean());
    }

    [Theory]
    [InlineData("/find *.nw", "rc", true)]
    [InlineData("  /FIND levels/*.nw  ", "rc", true)]
    [InlineData("/find", "program", true)]
    [InlineData("/stats", "rc", true)]
    [InlineData("/playerinfo bob", "rc", true)]
    [InlineData("/scriptscan all onCreated", "rc", true)]
    [InlineData("/version", "rc", true)]
    [InlineData("/find *.nw", "nc", false)]
    [InlineData("/find *.nw\n/restartserver", "rc", false)]
    [InlineData("/findmissing", "rc", false)]
    [InlineData("/finder", "rc", false)]
    [InlineData("/open bob", "rc", false)]
    [InlineData("/restartserver", "rc", false)]
    [InlineData("find *.nw", "rc", false)]
    [InlineData("hello", "rc", false)]
    [InlineData("", "rc", false)]
    public void Commands_that_only_answer_are_told_apart(string text, string connection, bool readOnly) =>
        Assert.Equal(readOnly, McpServerTools.IsReadOnlyCommand(text, connection));

    [Fact]
    public async Task A_find_is_not_asked_while_a_change_is()
    {
        await using var rig = new ServerRig(o => o with { McpEnabled = true, McpFullControl = true, McpApprove = true });
        rig.Setup.Host.Approve = false;
        rig.Answer(nameof(IRcSession.SendRcChatAsync), call => rig.Fake.RaiseRcChat("Found: levels/" + call.Argument<string>(0)[6..]));

        JsonElement found = await rig.OkAsync("rc_command", """{"text":"/find start.nw","collectMilliseconds":50}""");
        Assert.Contains(found.GetProperty("events").EnumerateArray(), item => item.GetProperty("text").GetString() == "Found: levels/start.nw");
        await rig.OkAsync("rc_command", """{"text":"/stats","connection":"program","collectMilliseconds":0}""");
        Assert.Equal(0, rig.Setup.Host.ApprovalQuestions);

        Assert.Equal((-32002, "Mutation denied by user"), BridgeSetup.ErrorOf(await rig.CallAsync("rc_command", """{"text":"/updatelevelall"}""")));
        Assert.Equal((-32002, "Mutation denied by user"), BridgeSetup.ErrorOf(await rig.CallAsync("rc_command", """{"text":"/find a\n/restartserver"}""")));
        Assert.Equal(2, rig.Setup.Host.ApprovalQuestions);
        Assert.DoesNotContain(rig.Fake.Calls, call => call.Method == nameof(IRcSession.SendRcChatAsync) && call.Argument<string>(0).Contains("/restartserver", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_denial_carries_the_note_of_a_program_no_one_watches()
    {
        using var folder = new TempFolder();
        await using var setup = new BridgeSetup(folder, o => o with { McpEnabled = true, McpFullControl = true, McpApprove = true }, deniedNote: "no one can answer here");
        setup.Host.Approve = false;
        Assert.Equal((-32002, "Mutation denied by user: no one can answer here"), BridgeSetup.ErrorOf(await setup.ToolAsync("rc_send_chat", """{"text":"hello"}""")));
    }
}
