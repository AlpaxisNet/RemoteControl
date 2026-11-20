using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Protocol.Text;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Extensions.Tests;

/// <summary>What a second review found in the first round of fixes, each as a test.</summary>
public sealed class McpServerToolsRoundTwoTests
{
    private static Func<RcOptions, RcOptions> Options(Func<RcOptions, RcOptions>? more = null) =>
        options => (more ?? (o => o))(options with { McpEnabled = true, McpFullControl = true, McpApprove = false });

    [UnixFact]
    public void A_link_whose_target_runs_through_another_link_is_followed_to_where_it_really_leads()
    {
        using var outside = new TempFolder();
        using var workspace = new TempFolder();
        var local = new McpLocalFiles(workspace.Path, ".");
        string work = Path.Combine(workspace.Path, "work");
        Directory.CreateDirectory(Path.Combine(work, "files"));
        // h -> outside; files/levels -> <root>/h (its text lies inside the root, where it leads does not).
        File.CreateSymbolicLink(Path.Combine(workspace.Path, "h"), outside.Path);
        File.CreateSymbolicLink(Path.Combine(work, "files", "levels"), Path.Combine(workspace.Path, "h"));
        Assert.Contains("leads out of", Assert.Throws<McpToolException>(() => local.ResolveUnder(work, "files/levels/id_rsa")).Message);
        // ../x/. through a link that sits deeper than it really is.
        File.CreateSymbolicLink(Path.Combine(work, "x"), outside.Path);
        File.CreateSymbolicLink(Path.Combine(work, "files", "up"), "../x/.");
        Assert.Throws<McpToolException>(() => local.ResolveUnder(work, "files/up/a.nw"));
        // A loop is a tool error, not an internal one.
        File.CreateSymbolicLink(Path.Combine(work, "loop"), Path.Combine(work, "loop"));
        Assert.Contains("cannot be followed", Assert.Throws<McpToolException>(() => local.ResolveUnder(work, "loop/a")).Message);
        // A link that stays inside is fine, and so is a path that does not exist yet.
        Directory.CreateDirectory(Path.Combine(work, "real"));
        File.CreateSymbolicLink(Path.Combine(work, "inside"), Path.Combine(work, "real"));
        Assert.Equal(Path.Combine(work, "inside", "a.nw"), local.ResolveUnder(work, "inside/a.nw"));
        Assert.Equal(Path.Combine(work, "new", "deeper", "a.nw"), local.ResolveUnder(work, "new/deeper/a.nw"));
    }

    [Fact]
    public async Task A_move_with_a_new_name_moves_first_and_checks_every_name_it_takes()
    {
        await using var rig = new ServerRig();
        ServerRig.InstallFiles(rig.Fake, new[] { new FileBrowserFolder("rw", "levels/*"), new FileBrowserFolder("rw", "levels/old/*") },
            ("levels/test.nw", Encoding.ASCII.GetBytes("test")), ("levels/start.nw", Encoding.ASCII.GetBytes("live")));
        await rig.OkAsync("rc_move_file", """{"path":"levels/test.nw","newName":"start.nw","folder":"levels/old/","collectMilliseconds":0}""");
        string[] order = rig.Fake.Calls.Where(call => call.Method is "McpFileBrowser.MoveAsync" or "McpFileBrowser.RenameAsync").Select(call => call.Method + " " + string.Join(" ", call.Arguments)).ToArray();
        Assert.Equal(new[] { "McpFileBrowser.MoveAsync levels/test.nw levels/old/", "McpFileBrowser.RenameAsync levels/old/test.nw start.nw" }, order);

        // The name a moved file has in its new folder before the rename is a name it takes as well.
        rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/old/test.nw"] = Encoding.ASCII.GetBytes("old test");
        rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/test.nw"] = Encoding.ASCII.GetBytes("test");
        Assert.Contains("levels/old/test.nw exists", await rig.FailAsync("rc_move_file", """{"path":"levels/test.nw","newName":"other.nw","folder":"levels/old/"}"""));

        // A rename that changes only the case is checked too: on a server that tells case apart these are two files.
        rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/TEST.nw"] = Encoding.ASCII.GetBytes("other");
        Assert.Contains("levels/TEST.nw exists", await rig.FailAsync("rc_move_file", """{"path":"levels/test.nw","newName":"TEST.nw"}"""));
        rig.Fake.FakeMcpFileBrowser.RemoteFiles.TryRemove("levels/TEST.nw", out _);
        await rig.OkAsync("rc_move_file", """{"path":"levels/test.nw","newName":"TEST.nw","collectMilliseconds":0}""");
    }

    [Fact]
    public async Task A_file_a_listing_shows_as_empty_is_listed_again_before_it_is_read()
    {
        await using var rig = new ServerRig();
        ServerRig.InstallFiles(rig.Fake, new[] { new FileBrowserFolder("rw", "levels/*") }, ("levels/a.txt", Encoding.ASCII.GetBytes("filled since")));
        rig.Fake.FakeMcpFileBrowser.RaiseFilesReceived("levels/", new[] { new FileBrowserEntry("a.txt", "rw", 0, 0) });
        rig.Fake.ClearCalls();
        Assert.Equal("filled since", (await rig.OkAsync("rc_read_file", """{"path":"levels/a.txt"}""")).GetProperty("text").GetString());
        string[] calls = rig.Fake.Calls.Select(call => call.Method).Where(method => method.StartsWith("McpFileBrowser.", StringComparison.Ordinal)).ToArray();
        Assert.True(Array.IndexOf(calls, "McpFileBrowser.ChangeFolderAsync") < Array.IndexOf(calls, "McpFileBrowser.DownloadAsync"), string.Join(", ", calls));
    }

    [Fact]
    public async Task A_wait_on_a_connection_that_went_away_ends_early_and_by_id_claims_are_for_that_player()
    {
        await using var rig = new ServerRig();
        rig.Fake.PlayerList.Add(new RcPlayer(5, string.Empty, "Nameless", "start.nw", PlayerListCategory.None, -1, string.Empty, new Dictionary<int, string>()));
        _ = Task.Delay(300).ContinueWith(_ => rig.Ui.Post(() => rig.Fake.IsConnected = false));
        var watch = Stopwatch.StartNew();
        JsonElement info = await rig.OkAsync("rc_get_player_info", """{"id":5,"parts":["attributes"],"timeoutSeconds":20}""");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.Contains("connection ended", info.GetProperty("errors").GetProperty("attributes").GetString());
        Assert.Contains("attributes #5", rig.Setup.Host.Claims);
    }

    [Fact]
    public async Task A_change_made_while_the_question_was_open_is_not_overwritten()
    {
        await using var rig = new ServerRig(Options(o => o with { McpApprove = true }));
        rig.Nc.Weapons["bow"] = ("", "// old\n");
        rig.Nc.Publish();
        rig.Setup.Host.ScriptApproval = McpApproval.AllowOnce;
        rig.Setup.Host.WhileAsking = () => rig.Nc.Weapons["bow"] = ("", "// changed by other staff\n");
        Assert.Contains("while the question was open", await rig.FailAsync("rc_write_script", """{"kind":"weapon","name":"bow","script":"// mine\n"}"""));
        Assert.Equal("// changed by other staff\n", rig.Nc.Weapons["bow"].Script);

        ServerRig.InstallFiles(rig.Fake, new[] { new FileBrowserFolder("rw", "levels/*") }, ("levels/a.txt", Encoding.ASCII.GetBytes("old")));
        rig.Setup.Host.WhileAsking = () => rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/a.txt"] = Encoding.ASCII.GetBytes("theirs");
        Assert.Contains("while the question was open", await rig.FailAsync("rc_write_file", """{"path":"levels/a.txt","text":"mine"}"""));
        Assert.Equal("theirs", Encoding.ASCII.GetString(rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/a.txt"]));
    }

    [Fact]
    public async Task A_restore_stays_inside_the_scope_and_force_goes_without_an_unreadable_old_version()
    {
        await using var rig = new ServerRig();
        (FakeRcSession other, FakeNc otherNc) = rig.AddSession("Other");
        rig.Nc.Weapons["bow"] = ("", "// old\n");
        rig.Nc.Publish();
        otherNc.Weapons["bow"] = ("", "// other\n");
        otherNc.Publish();
        string id = (await rig.OkAsync("rc_write_script", """{"kind":"weapon","name":"bow","script":"// new\n","collectMilliseconds":0}""")).GetProperty("backup").GetString()!;
        rig.Setup.Options.Update(o => o with { McpServerScope = "Other" });
        Assert.Equal(-32003, BridgeSetup.ErrorOf(await rig.CallAsync("rc_restore_backup", JsonSerializer.Serialize(new { id, server = "Other" }))).Code);
        Assert.Equal("// other\n", otherNc.Weapons["bow"].Script);
        rig.Setup.Options.Update(o => o with { McpServerScope = string.Empty });

        ServerRig.InstallFiles(rig.Fake, new[] { new FileBrowserFolder("rw", "levels/*") }, ("levels/gone.txt", Encoding.ASCII.GetBytes("x")));
        rig.Fake.FakeMcpFileBrowser.DownloadHandler = _ => Task.FromResult<byte[]?>(null);
        Assert.Contains("could not be read for its backup", await rig.FailAsync("rc_delete_file", """{"path":"levels/gone.txt"}"""));
        Assert.True(rig.Fake.FakeMcpFileBrowser.RemoteFiles.ContainsKey("levels/gone.txt"));
        rig.Setup.Options.Update(o => o with { McpApprove = true });
        await rig.OkAsync("rc_delete_file", """{"path":"levels/gone.txt","force":true,"collectMilliseconds":0}""");
        Assert.False(rig.Fake.FakeMcpFileBrowser.RemoteFiles.ContainsKey("levels/gone.txt"));
        Assert.Contains(rig.Setup.Host.Calls, call => call.StartsWith("approve Delete levels/gone.txt", StringComparison.Ordinal) && call.Contains("force: true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_deleted_npc_keeps_its_flags_and_attributes()
    {
        await using var rig = new ServerRig();
        rig.Nc.Npcs[1000] = ("door", "// door\n");
        rig.Nc.Publish();
        rig.Answer(nameof(IRcSession.RequestNpcFlagsAsync), call => rig.Fake.RaiseNpcFlags(call.Argument<int>(0), "open=1\n"));
        rig.Answer(nameof(IRcSession.RequestNpcAttributesAsync), call => rig.Fake.RaiseNpcAttributes(call.Argument<int>(0), "level=start.nw\nx=30"));
        await rig.OkAsync("rc_delete_script", """{"kind":"npc","id":1000,"collectMilliseconds":0}""");
        string[] kinds = (await rig.OkAsync("rc_backups")).GetProperty("backups").EnumerateArray().Select(item => item.GetProperty("kind").GetString()!).Order().ToArray();
        Assert.Equal(new[] { "npc", "npcattributes", "npcflags" }, kinds);
    }

    [Fact]
    public async Task Npcs_go_to_another_server_by_name_and_a_reused_id_is_caught()
    {
        await using var rig = new ServerRig();
        rig.Nc.Npcs[1000] = ("door", "// door\n");
        rig.Nc.Publish();
        await rig.OkAsync("rc_export", """{"localFolder":"work","kinds":["npc"]}""");

        (FakeRcSession other, FakeNc otherNc) = rig.AddSession("Other");
        otherNc.Npcs[1000] = ("shop", "// shop\n");
        otherNc.Npcs[2000] = ("door", "// other door\n");
        otherNc.Publish();
        await rig.OkAsync("rc_import", """{"localFolder":"work","server":"Other","collectMilliseconds":0}""");
        Assert.Equal("// shop\n", otherNc.Npcs[1000].Script);
        Assert.Equal("// door\n", otherNc.Npcs[2000].Script);

        // On its own server an id that now has another NPC is not written into.
        rig.Nc.Npcs[1000] = ("bank", "// bank\n");
        rig.Nc.Publish();
        string folder = Path.Combine(rig.Setup.Paths.McpWorkspaceDirectory, "work");
        File.WriteAllText(Path.Combine(folder, "npcs", "1000_door.gs2"), "// door v2\n");
        JsonElement imported = await rig.OkAsync("rc_import", """{"localFolder":"work","collectMilliseconds":0}""");
        Assert.Contains("is named bank", imported.GetProperty("items")[0].GetProperty("message").GetString());
        Assert.Equal("// bank\n", rig.Nc.Npcs[1000].Script);
    }

    [Fact]
    public async Task A_renamed_local_npc_file_is_sent()
    {
        await using var rig = new ServerRig();
        rig.Nc.Npcs[1000] = ("door", "// door\n");
        rig.Nc.Publish();
        string folder = (await rig.OkAsync("rc_export", """{"localFolder":"work","kinds":["npc"]}""")).GetProperty("folder").GetString()!;
        File.Move(Path.Combine(folder, "npcs", "1000_door.gs2"), Path.Combine(folder, "npcs", "1000_frontdoor.gs2"));
        File.WriteAllText(Path.Combine(folder, "npcs", "1000_frontdoor.gs2"), "// edited\n");
        await rig.OkAsync("rc_import", """{"localFolder":"work","collectMilliseconds":0}""");
        Assert.Equal("// edited\n", rig.Nc.Npcs[1000].Script);
    }

    [Fact]
    public async Task A_long_answer_is_gathered_until_the_server_is_quiet()
    {
        await using var rig = new ServerRig();
        rig.Answer(nameof(IRcSession.SendRcChatAsync), call =>
        {
            if (!call.Argument<string>(0).StartsWith("/scriptscan", StringComparison.Ordinal)) return;
            foreach (int delay in new[] { 400, 1300, 2200 })
                _ = Task.Delay(delay).ContinueWith(_ => rig.Ui.Post(() => rig.Fake.RaiseRcChat("line " + delay)));
        });
        JsonElement scan = await rig.OkAsync("rc_search_scripts", """{"query":"x","serverSide":true,"collectMilliseconds":1000,"regex":false,"caseSensitive":false,"contextLines":0,"refresh":true,"names":""}""");
        Assert.Equal(new[] { "line 400", "line 1300", "line 2200" }, scan.GetProperty("output").EnumerateArray().Select(item => item.GetString()));
        Assert.True(scan.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task Default_values_and_chosen_encodings_are_taken_as_given()
    {
        await using var rig = new ServerRig();
        rig.Fake.UseNewBanProtocol = false;
        await rig.OkAsync("rc_set_ban", """{"target":"bob","banned":false,"type":"","noEnd":false,"releaseSeconds":0,"collectMilliseconds":0}""");
        Assert.Equal(new object?[] { "bob", false, string.Empty }, rig.Fake.LastCall(nameof(IRcSession.SetLegacyPlayerBanAsync))!.Arguments);

        ServerRig.InstallFiles(rig.Fake, new[] { new FileBrowserFolder("rw", "levels/*") }, ("levels/a.txt", Encoding.UTF8.GetBytes("café\n")));
        JsonElement edited = await rig.OkAsync("rc_write_file", """{"path":"levels/a.txt","encoding":"utf8-bom","edits":[{"expected":"café","replacement":"cafés"}]}""");
        Assert.Equal("utf8-bom", edited.GetProperty("encoding").GetString());
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("cafés\n")), rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/a.txt"]);
    }

    [Fact]
    public async Task An_index_with_a_lone_surrogate_key_is_a_tool_error()
    {
        await using var rig = new ServerRig();
        string folder = Path.Combine(rig.Setup.Paths.McpWorkspaceDirectory, "work");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, McpServerTools.IndexFileName), """{"server":"x","entries":[],"\uDC00xxxxxxx":1}""");
        Assert.Contains("is not a valid export index", await rig.FailAsync("rc_import", """{"localFolder":"work"}"""));
    }
}
