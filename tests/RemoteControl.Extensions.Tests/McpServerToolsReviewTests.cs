using System.Text;
using System.Text.Json;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Protocol.Text;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Extensions.Tests;

/// <summary>What a review of the server tools found, each as a test: backups that must be taken, answers that must not open windows, paths that must not escape.</summary>
public sealed class McpServerToolsReviewTests
{
    private static Func<RcOptions, RcOptions> Options(Func<RcOptions, RcOptions>? more = null) =>
        options => (more ?? (o => o))(options with { McpEnabled = true, McpFullControl = true, McpApprove = false });

    [Fact]
    public async Task A_weapon_written_twice_is_updated_with_a_backup_and_keeps_its_image()
    {
        await using var rig = new ServerRig();
        rig.Nc.Publish();
        await rig.OkAsync("rc_write_script", """{"kind":"weapon","name":"Shop","script":"// v1\n","image":"shop.png","collectMilliseconds":0}""");
        Assert.DoesNotContain(rig.Fake.Weapons, weapon => weapon.Name == "Shop");

        // The session's list does not know the new weapon; the tool asks for the list again before it calls it new.
        JsonElement second = await rig.OkAsync("rc_write_script", """{"kind":"weapon","name":"Shop","script":"// v2\n","collectMilliseconds":0}""");
        Assert.False(second.GetProperty("created").GetBoolean());
        Assert.Equal(("shop.png", "// v2\n"), rig.Nc.Weapons["Shop"]);
        Assert.Contains("// v1", File.ReadAllText(Assert.Single(rig.BackupFiles())));
        await rig.OkAsync("rc_write_script", """{"kind":"weapon","name":"Shop","edits":[{"expected":"v2","replacement":"v3"}],"collectMilliseconds":0}""");
        Assert.Equal("// v3\n", rig.Nc.Weapons["Shop"].Script);

        // A deleted weapon leaves the session's list, so it can be made again at once (no wait for a script that is not there).
        await rig.OkAsync("rc_delete_script", """{"kind":"weapon","name":"Shop","collectMilliseconds":0}""");
        Assert.DoesNotContain(rig.Fake.Weapons, weapon => weapon.Name == "Shop");
        JsonElement again = await rig.OkAsync("rc_write_script", """{"kind":"weapon","name":"Shop","script":"// new\n","timeoutSeconds":2,"collectMilliseconds":0}""");
        Assert.True(again.GetProperty("created").GetBoolean());
    }

    [Fact]
    public async Task Player_reads_claim_their_answers_and_let_them_go()
    {
        await using var rig = new ServerRig();
        rig.Answer(nameof(IRcSession.RequestPlayerRightsAsync), _ => rig.Fake.RaisePlayerRights("bob", new PlayerRights(1, "*", string.Empty)));
        rig.Answer(nameof(IRcSession.RequestAccountListAsync), _ => rig.Fake.RaiseAccountList(new[] { "bob" }));
        rig.Answer(nameof(IRcSession.RequestLocalNpcsAsync), call => rig.Fake.RaiseLocalNpcs(call.Argument<string>(0), "[npc]"));
        await rig.OkAsync("rc_get_player_info", """{"account":"bob","parts":["rights","comments"],"timeoutSeconds":1}""");
        await rig.OkAsync("rc_list_accounts");
        await rig.OkAsync("rc_read_local_npcs", """{"level":"start.nw"}""");
        Assert.Equal(new[] { "rights bob", "comments bob", "accountlist *", "localnpcs start.nw" }, rig.Setup.Host.Claims);
        Assert.Equal(0, rig.Setup.Host.OpenClaims);
    }

    [UnixFact]
    public async Task Links_inside_an_export_folder_are_not_followed()
    {
        using var outside = new TempFolder();
        await using var rig = new ServerRig();
        ServerRig.InstallFiles(rig.Fake, new[] { new FileBrowserFolder("rw", "levels/*") }, ("levels/a.nw", Encoding.ASCII.GetBytes("A")));
        string work = Path.Combine(rig.Setup.Paths.McpWorkspaceDirectory, "work");
        Directory.CreateDirectory(Path.Combine(work, "files"));
        File.CreateSymbolicLink(Path.Combine(work, "files", "levels"), outside.Path);
        JsonElement exported = await rig.OkAsync("rc_export", """{"localFolder":"work","scripts":false,"files":["levels/a.nw"]}""");
        Assert.False(exported.GetProperty("ok").GetBoolean());
        Assert.Contains("link", exported.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("item").GetString() == "levels/a.nw").GetProperty("message").GetString());
        Assert.Empty(Directory.GetFiles(outside.Path));

        // Reading: a secret behind a link is neither listed nor sent.
        File.WriteAllText(Path.Combine(outside.Path, "id_rsa"), "secret");
        JsonElement dry = await rig.OkAsync("rc_import", """{"localFolder":"work","dryRun":true}""");
        Assert.DoesNotContain(dry.GetProperty("items").EnumerateArray(), item => item.GetProperty("item").GetString()!.Contains("id_rsa", StringComparison.Ordinal));
        Assert.False(rig.Fake.FakeMcpFileBrowser.RemoteFiles.ContainsKey("levels/id_rsa"));
    }

    [Fact]
    public async Task A_move_does_not_replace_a_file_unless_asked_and_then_keeps_it()
    {
        await using var rig = new ServerRig();
        ServerRig.InstallFiles(rig.Fake, new[] { new FileBrowserFolder("rw", "levels/*") }, ("levels/test.nw", Encoding.ASCII.GetBytes("test")), ("levels/start.nw", Encoding.ASCII.GetBytes("live")));
        Assert.Contains("give overwrite: true", await rig.FailAsync("rc_move_file", """{"path":"levels/test.nw","newName":"start.nw"}"""));
        Assert.False(rig.Fake.WasCalled("McpFileBrowser.RenameAsync"));
        JsonElement moved = await rig.OkAsync("rc_move_file", """{"path":"levels/test.nw","newName":"start.nw","overwrite":true,"collectMilliseconds":0}""");
        Assert.Equal("live", File.ReadAllText(Assert.Single(rig.BackupFiles())));
        Assert.False(string.IsNullOrEmpty(moved.GetProperty("backup").GetString()));
    }

    [Fact]
    public async Task A_backup_that_cannot_be_written_stops_the_change_unless_forced()
    {
        await using var rig = new ServerRig();
        rig.Nc.Weapons["bow"] = ("", "// old\n");
        rig.Nc.Publish();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.Setup.Paths.McpBackupDirectory)!);
        File.WriteAllText(rig.Setup.Paths.McpBackupDirectory, "a file where the backup folder belongs");
        Assert.Contains("could not be backed up", await rig.FailAsync("rc_write_script", """{"kind":"weapon","name":"bow","script":"// new\n"}"""));
        Assert.Contains("could not be backed up", await rig.FailAsync("rc_delete_script", """{"kind":"weapon","name":"bow"}"""));
        Assert.Equal("// old\n", rig.Nc.Weapons["bow"].Script);
        await rig.OkAsync("rc_write_script", """{"kind":"weapon","name":"bow","script":"// new\n","force":true,"collectMilliseconds":0}""");
        Assert.Equal("// new\n", rig.Nc.Weapons["bow"].Script);
    }

    [Fact]
    public async Task A_backtracking_pattern_stops_the_search_with_a_message()
    {
        await using var rig = new ServerRig();
        rig.Nc.Weapons["slow"] = ("", new string('a', 40) + "!\n");
        rig.Nc.Publish();
        Assert.Contains("took too long", await rig.FailAsync("rc_search_scripts", """{"query":"(a+)+$","regex":true}"""));
        Assert.Contains("took too long", await rig.FailAsync("rc_read_script", """{"kind":"weapon","name":"slow","query":"(a+)+$","regex":true}"""));
    }

    [Fact]
    public async Task The_legacy_ban_refuses_what_it_cannot_carry()
    {
        await using var rig = new ServerRig();
        rig.Fake.UseNewBanProtocol = false;
        Assert.Contains("needs the new ban protocol", await rig.FailAsync("rc_set_ban", """{"target":"bob","releaseSeconds":3600}"""));
        Assert.Contains("needs the new ban protocol", await rig.FailAsync("rc_set_ban", """{"target":"pc:ABC123"}"""));
        Assert.Contains("needs the new ban protocol", await rig.FailAsync("rc_set_ban", """{"target":"bob","world":"all"}"""));
        Assert.False(rig.Fake.WasCalled(nameof(IRcSession.SetLegacyPlayerBanAsync)));
        await rig.OkAsync("rc_set_ban", """{"target":"bob","reason":"spam","collectMilliseconds":0}""");
        Assert.Equal(new object?[] { "bob", true, "spam" }, rig.Fake.LastCall(nameof(IRcSession.SetLegacyPlayerBanAsync))!.Arguments);
    }

    [Fact]
    public async Task A_restore_needs_the_permission_of_the_change_it_makes()
    {
        await using var rig = new ServerRig();
        rig.Answer(nameof(IRcSession.RequestPlayerRightsAsync), _ => rig.Fake.RaisePlayerRights("bob", new PlayerRights(0, "*", "r levels/*")));
        string id = (await rig.OkAsync("rc_write_player_info", JsonSerializer.Serialize(new { account = "bob", part = "rights", text = "IP Range: *\n\nFolder Access:\nrw levels/*\n", collectMilliseconds = 0 }))).GetProperty("backup").GetString()!;
        rig.Setup.Options.Update(o => o with { McpFullControl = false, McpRead = true, McpWrite = true, McpAdmin = false });
        Assert.Contains("needs administrative actions", await rig.FailAsync("rc_restore_backup", JsonSerializer.Serialize(new { id })));
        rig.Setup.Options.Update(o => o with { McpAdmin = true });
        await rig.OkAsync("rc_restore_backup", JsonSerializer.Serialize(new { id, collectMilliseconds = 0 }));
        Assert.Equal("r levels/*", rig.Fake.LastCall(nameof(IRcSession.SetPlayerRightsAsync))!.Argument<PlayerRights>(1).Folders);
        Assert.Equal(2, rig.BackupFiles().Length);
    }

    [Fact]
    public async Task The_scope_refuses_a_session_only_when_a_tool_uses_it()
    {
        await using var rig = new ServerRig(Options(o => o with { McpServerScope = "Other" }));
        JsonElement check = await rig.OkAsync("rc_check_script", """{"text":"function onCreated() {}\n","api":false}""");
        Assert.Equal(0, check.GetProperty("errors").GetInt32());
        Assert.Equal((-32003, "Current server is outside configured MCP server scope"), BridgeSetup.ErrorOf(await rig.CallAsync("rc_check_script", """{"kind":"weapon","name":"bow","api":false}""")));
        Assert.Equal((-32003, "Current server is outside configured MCP server scope"), BridgeSetup.ErrorOf(await rig.CallAsync("rc_backups", """{"ofServer":"Alpaxis"}""")));
    }

    [Fact]
    public async Task Backups_of_other_servers_stay_out_of_the_scope()
    {
        await using var rig = new ServerRig();
        rig.Nc.Weapons["bow"] = ("", "// old\n");
        rig.Nc.Publish();
        string id = (await rig.OkAsync("rc_write_script", """{"kind":"weapon","name":"bow","script":"// new\n","collectMilliseconds":0}""")).GetProperty("backup").GetString()!;
        rig.Setup.Options.Update(o => o with { McpServerScope = "Other" });
        Assert.Empty((await rig.OkAsync("rc_backups")).GetProperty("backups").EnumerateArray());
        Assert.Equal(-32003, BridgeSetup.ErrorOf(await rig.CallAsync("rc_backups", JsonSerializer.Serialize(new { id }))).Code);
    }

    [Fact]
    public async Task Text_with_a_lone_surrogate_is_no_internal_error()
    {
        await using var rig = new ServerRig();
        // JSON allows an escaped lone surrogate in a string or a property name; no .NET string can hold one, so the request is refused whole.
        Assert.Equal(-32700, BridgeSetup.ErrorOf(await rig.Setup.CallAsync("""{"jsonrpc":"2.0","id":1,"method":"\ud800"}""")).Code);
        Assert.Equal(-32700, BridgeSetup.ErrorOf(await rig.CallAsync("rc_command", """{"text":"\ud800"}""")).Code);
        Assert.Equal(-32700, BridgeSetup.ErrorOf(await rig.Setup.CallAsync("""{"jsonrpc":"2.0","id":2,"method":"ping","\udc00":0}""")).Code);
        Assert.Equal(-32700, BridgeSetup.ErrorOf(await rig.CallAsync("rc_status", """{"\ud83d":1}""")).Code);
        Assert.True(McpRequestProcessor.ExpectsResponse("""{"method":"x","\udc00":0}"""));
        Assert.Equal(JsonValueKind.Object, (await rig.Setup.CallAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}""")).GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Reference_ranges_past_the_end_are_empty()
    {
        await using var rig = new ServerRig();
        rig.Folder.Write("reference/CORE.md", "# Core\nline\n");
        rig.Folder.Write("reference/docs/empty.md", string.Empty);
        Assert.Equal(string.Empty, (await rig.OkAsync("rc_gs2_docs", """{"document":"CORE.md","start":5000}""")).GetProperty("text").GetString());
        Assert.Equal(string.Empty, (await rig.OkAsync("rc_gs2_docs", """{"document":"docs/empty.md","start":1}""")).GetProperty("text").GetString());
        Assert.Equal("line", (await rig.OkAsync("rc_gs2_docs", """{"document":"CORE.md","start":2,"end":2}""")).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Extra_reference_folders_never_repeat_or_shadow_the_bundled_documents()
    {
        using var checkout = new TempFolder();
        checkout.Write("docs/GraalScript_Reference.md", "# Guide\r\n\r\nshowimg\r\n");
        checkout.Write("ghidra/TShowImg.txt", "showimg\n");
        using var docs = new TempFolder();
        string named = Path.Combine(docs.Path, "docs");
        Directory.CreateDirectory(named);
        File.WriteAllText(Path.Combine(named, "extra.md"), "showimg\n");
        await using var rig = new ServerRig(Options(o => o with { McpReferenceFolders = checkout.Path + ";" + named + ";~/no-such-folder-for-rc-tests;relative/none" }));
        rig.Folder.Write("reference/docs/GraalScript_Reference.md", "# Guide\n\nshowimg\n");
        JsonElement search = await rig.OkAsync("rc_gs2_search", """{"query":"showimg"}""");
        string[] ids = search.GetProperty("results").EnumerateArray().Select(item => item.GetProperty("document").GetString()!).ToArray();
        Assert.Equal(1, ids.Count(id => id.EndsWith("GraalScript_Reference.md", StringComparison.Ordinal)));
        Assert.Contains("docs2/extra.md", ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        JsonElement[] folders = (await rig.OkAsync("rc_gs2_docs")).GetProperty("otherFolders").EnumerateArray().ToArray();
        Assert.Contains(folders, item => item.TryGetProperty("prefix", out JsonElement prefix) && prefix.GetString() == "docs2/");
        Assert.Contains(folders, item => item.GetProperty("folder").GetString()!.EndsWith("no-such-folder-for-rc-tests", StringComparison.Ordinal) && !item.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public void An_empty_text_takes_a_set_without_a_blank_first_line()
    {
        using JsonDocument arguments = JsonDocument.Parse("""{"set":{"state":"1"}}""");
        Assert.Equal("state=1", McpServerTools.MergeSettings(string.Empty, new McpArguments(arguments.RootElement), ignoreCase: false, out _));
    }

    [Fact]
    public async Task Ascii_files_keep_the_servers_encoding_and_utf8_is_never_decoded_lossily()
    {
        await using var rig = new ServerRig();
        ServerRig.InstallFiles(rig.Fake, new[] { new FileBrowserFolder("rw", "levels/*") },
            ("levels/plain.txt", Encoding.ASCII.GetBytes("hello\n")), ("levels/old.txt", RcText.GetBytes("café\n")));
        JsonElement edited = await rig.OkAsync("rc_write_file", """{"path":"levels/plain.txt","edits":[{"expected":"hello","replacement":"héllo"}]}""");
        Assert.Equal("windows-1252", edited.GetProperty("encoding").GetString());
        Assert.Equal(RcText.GetBytes("héllo\n"), rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/plain.txt"]);
        Assert.Contains("not valid UTF-8", await rig.FailAsync("rc_write_file", """{"path":"levels/old.txt","encoding":"utf8","edits":[{"expected":"caf","replacement":"cof"}]}"""));
        Assert.Equal(RcText.GetBytes("café\n"), rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/old.txt"]);
    }

    [Fact]
    public async Task An_npc_created_with_a_script_needs_script_writes_and_their_question()
    {
        await using var rig = new ServerRig(o => o with { McpEnabled = true, McpFullControl = false, McpRead = true, McpServer = true, McpWrite = false, McpApprove = true });
        Assert.Contains("needs script writes", await rig.FailAsync("rc_npc_action", """{"action":"create","name":"shop","script":"// shop\n"}"""));
        Assert.False(rig.Fake.WasCalled(nameof(IRcSession.CreateNpcAsync)));
        rig.Setup.Options.Update(o => o with { McpWrite = true });
        rig.Setup.Host.ScriptApproval = McpApproval.Deny;
        Assert.Equal(-32002, BridgeSetup.ErrorOf(await rig.CallAsync("rc_npc_action", """{"action":"create","name":"shop","script":"// shop\n"}""")).Code);
        Assert.Contains(rig.Setup.Host.Calls, call => call.StartsWith("approve-script Write NPC: 1000 shop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Players_another_server_sends_as_text_are_listed_and_reachable()
    {
        await using var rig = new ServerRig();
        rig.Answer(nameof(IRcSession.RequestPmServerPlayersAsync), call => rig.Fake.RaisePmServerPlayers(call.Argument<string>(0), "71\tfar\tFar Away\tstart.nw\nbad line\n"));
        JsonElement players = await rig.OkAsync("rc_list_players", """{"pmServer":"Elsewhere"}""");
        JsonElement remote = Assert.Single(players.GetProperty("pmServerPlayers").EnumerateArray());
        Assert.Equal((71, "far", "Elsewhere"), (remote.GetProperty("id").GetInt32(), remote.GetProperty("account").GetString(), remote.GetProperty("server").GetString()));
        await rig.OkAsync("rc_send_message", """{"ids":[71],"message":"hi","collectMilliseconds":0}""");
        Assert.Equal(71, rig.Fake.LastCall(nameof(IRcSession.SendPrivateMessageAsync))!.Argument<int>(0));
    }

    [Fact]
    public async Task A_renamed_npc_leaves_no_old_file_that_an_import_would_send()
    {
        await using var rig = new ServerRig();
        rig.Nc.Npcs[1000] = ("OldShop", "// old shop\n");
        rig.Nc.Publish();
        string folder = (await rig.OkAsync("rc_export", """{"localFolder":"work","kinds":["npc"]}""")).GetProperty("folder").GetString()!;
        Assert.True(File.Exists(Path.Combine(folder, "npcs", "1000_OldShop.gs2")));
        rig.Nc.Npcs[1000] = ("Bank", "// bank\n");
        rig.Nc.Publish();
        await rig.OkAsync("rc_export", """{"localFolder":"work","kinds":["npc"]}""");
        Assert.False(File.Exists(Path.Combine(folder, "npcs", "1000_OldShop.gs2")));
        // Even a copy left behind by hand is no new script for an NPC the index names.
        File.WriteAllText(Path.Combine(folder, "npcs", "1000_OldShop.gs2"), "// old shop\n");
        rig.Fake.ClearCalls();
        Assert.Equal("Nothing to send (no file changed since the export).", (await rig.OkAsync("rc_import", """{"localFolder":"work"}""")).GetProperty("message").GetString());
        Assert.False(rig.Fake.WasCalled(nameof(IRcSession.UpdateNpcScriptAsync)));
    }

    [Fact]
    public async Task An_account_text_must_name_the_account_it_is_sent_for()
    {
        await using var rig = new ServerRig();
        string text = AccountText.Format(new AccountData { Account = "Bob", Email = "x@y" });
        Assert.Contains("names the account Bob", await rig.FailAsync("rc_write_player_info", JsonSerializer.Serialize(new { account = "Alice", part = "account", text })));
        Assert.False(rig.Fake.WasCalled(nameof(IRcSession.SetAccountAsync)));
    }

    [Fact]
    public async Task Approvals_show_every_line_that_is_run()
    {
        await using var rig = new ServerRig(Options(o => o with { McpApprove = true }));
        rig.Setup.Host.Approve = false;
        string text = "/stats" + new string(' ', 300) + "\n/restartserver";
        Assert.Equal(-32002, BridgeSetup.ErrorOf(await rig.CallAsync("rc_command", JsonSerializer.Serialize(new { text }))).Code);
        Assert.Contains(rig.Setup.Host.Calls, call => call.StartsWith("approve Run on Alpaxis (rc):\n(2 lines)\n/stats", StringComparison.Ordinal) && call.EndsWith("\n/restartserver", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_copy_whose_destination_listing_fails_replaces_nothing()
    {
        await using var rig = new ServerRig();
        (FakeRcSession source, _) = rig.AddSession("Source");
        ServerRig.InstallFiles(source, new[] { new FileBrowserFolder("rw", "levels/*") }, ("levels/start.nw", Encoding.ASCII.GetBytes("new")));
        FakeFileBrowser destination = rig.Fake.FakeMcpFileBrowser;
        destination.RemoteFiles["levels/start.nw"] = Encoding.ASCII.GetBytes("live");
        destination.OnCall = call =>
        {
            // The folder list comes; the listing of levels/ never does.
            if (call.Method == "McpFileBrowser.StartAsync") destination.RaiseFoldersReceived(new[] { new FileBrowserFolder("rw", "levels/*") });
        };
        JsonElement copied = await rig.OkAsync("rc_copy", """{"from":"Source","files":["levels/start.nw"],"overwrite":false,"timeoutSeconds":1,"collectMilliseconds":0}""");
        Assert.Equal("failed", Assert.Single(copied.GetProperty("items").EnumerateArray()).GetProperty("outcome").GetString());
        Assert.Equal("live", Encoding.ASCII.GetString(destination.RemoteFiles["levels/start.nw"]));
    }

    [Fact]
    public async Task A_slow_server_answer_to_a_command_is_still_gathered()
    {
        await using var rig = new ServerRig();
        rig.Answer(nameof(IRcSession.SendRcChatAsync), call =>
        {
            if (call.Argument<string>(0).StartsWith("/scriptscan", StringComparison.Ordinal))
                _ = Task.Delay(1800).ContinueWith(_ => rig.Ui.Post(() => rig.Fake.RaiseRcChat("weapons/-Chest: this.chest = 0;")));
        });
        JsonElement scan = await rig.OkAsync("rc_search_scripts", """{"query":"this.chest","serverSide":true,"collectMilliseconds":5000}""");
        Assert.Equal("weapons/-Chest: this.chest = 0;", Assert.Single(scan.GetProperty("output").EnumerateArray()).GetString());
        Assert.Contains("leave names out", await rig.FailAsync("rc_search_scripts", """{"query":"x","serverSide":true,"names":"a*"}"""));
    }
}
