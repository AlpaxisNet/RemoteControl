using System.Text;
using System.Text.Json;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;

namespace RemoteControl.Extensions.Tests;

/// <summary>What a third review found in the second round of fixes, each as a test.</summary>
public sealed class McpServerToolsRoundThreeTests
{
    private static Func<RcOptions, RcOptions> Options(Func<RcOptions, RcOptions>? more = null) =>
        options => (more ?? (o => o))(options with { McpEnabled = true, McpFullControl = true, McpApprove = false });

    [Fact]
    public async Task A_delete_or_a_move_is_about_the_files_the_question_named()
    {
        await using var rig = new ServerRig(Options(o => o with { McpApprove = true }));
        ServerRig.InstallFiles(rig.Fake, new[] { new FileBrowserFolder("rw", "levels/*"), new FileBrowserFolder("rw", "levels/old/*") },
            ("levels/a.txt", Encoding.ASCII.GetBytes("old")), ("levels/b.txt", Encoding.ASCII.GetBytes("b")));
        rig.Setup.Host.WhileAsking = () => rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/a.txt"] = Encoding.ASCII.GetBytes("rewritten by other staff");
        Assert.Contains("while the question was open", await rig.FailAsync("rc_delete_file", """{"path":"levels/a.txt"}"""));
        Assert.True(rig.Fake.FakeMcpFileBrowser.RemoteFiles.ContainsKey("levels/a.txt"));

        // A file that appears where the move goes, while it is asked, is not replaced unseen.
        rig.Setup.Host.WhileAsking = () => rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/old/b.txt"] = Encoding.ASCII.GetBytes("theirs");
        Assert.Contains("while the question was open", await rig.FailAsync("rc_move_file", """{"path":"levels/b.txt","folder":"levels/old/","overwrite":true}"""));
        Assert.Equal("theirs", Encoding.ASCII.GetString(rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/old/b.txt"]));
        Assert.True(rig.Fake.FakeMcpFileBrowser.RemoteFiles.ContainsKey("levels/b.txt"));
    }

    [Fact]
    public async Task An_empty_file_is_downloaded_for_its_backup_rather_than_taken_as_empty()
    {
        await using var rig = new ServerRig();
        ServerRig.InstallFiles(rig.Fake, new[] { new FileBrowserFolder("rw", "levels/*") }, ("levels/a.txt", Encoding.ASCII.GetBytes("filled since")));
        rig.Fake.FakeMcpFileBrowser.RaiseFilesReceived("levels/", new[] { new FileBrowserEntry("a.txt", "rw", 0, 0) });
        string id = (await rig.OkAsync("rc_delete_file", """{"path":"levels/a.txt","collectMilliseconds":0}""")).GetProperty("backup").GetString()!;
        JsonElement backup = (await rig.OkAsync("rc_backups", JsonSerializer.Serialize(new { id }))).GetProperty("backup");
        Assert.Equal("filled since".Length, backup.GetProperty("size").GetInt32());
    }

    [Fact]
    public async Task A_forced_write_over_an_unreadable_script_says_so_and_is_always_asked()
    {
        await using var rig = new ServerRig(Options(o => o with { McpApprove = true, McpApproveWeapon = true }));
        rig.Nc.Weapons["bow"] = ("bow.png", "// old\n");
        rig.Nc.Publish();
        // The weapon is in the list, but the NC does not answer for its script.
        rig.Nc.Weapons.Remove("bow");
        Assert.Contains("could not be read for its backup", await rig.FailAsync("rc_write_script", """{"kind":"weapon","name":"bow","script":"// new\n","timeoutSeconds":1}"""));
        // Its image is not known (the list has none): it must be given.
        Assert.Contains("give image", await rig.FailAsync("rc_write_script", """{"kind":"weapon","name":"bow","script":"// new\n","timeoutSeconds":1,"force":true}"""));

        rig.Fake.WeaponList.Clear();
        rig.Fake.WeaponList.Add(new RcWeapon("bow", "bow.png", string.Empty));
        await rig.OkAsync("rc_write_script", """{"kind":"weapon","name":"bow","script":"// new\n","timeoutSeconds":1,"force":true,"collectMilliseconds":0}""");
        // Weapons were always allowed, yet the forced write was asked, and the question tells what it replaces.
        string asked = Assert.Single(rig.Setup.Host.Calls, call => call.StartsWith("approve-script", StringComparison.Ordinal));
        Assert.Contains("could not be read, without a backup", asked);
        Assert.Contains("force: true", asked);
        Assert.Equal(nameof(IRcSession.UpdateWeaponAsync), rig.Fake.Calls.Last(call => call.Method.EndsWith("WeaponAsync", StringComparison.Ordinal) && !call.Method.StartsWith("Request", StringComparison.Ordinal)).Method);
        Assert.Equal("bow.png", rig.Fake.LastCall(nameof(IRcSession.UpdateWeaponAsync))!.Argument<string>(1));
    }

    [Fact]
    public async Task Force_goes_without_unreadable_npc_flags_and_a_restored_script_is_checked_after_the_question()
    {
        await using var rig = new ServerRig();
        rig.Nc.Npcs[1000] = ("door", "// door\n");
        rig.Nc.Publish();
        Assert.Contains("could not be read for its backup", await rig.FailAsync("rc_npc_action", """{"action":"write_flags","id":1000,"text":"open=1\n","timeoutSeconds":1}"""));
        await rig.OkAsync("rc_npc_action", """{"action":"write_flags","id":1000,"text":"open=1\n","timeoutSeconds":1,"force":true,"collectMilliseconds":0}""");
        Assert.Equal(new object?[] { 1000, "open=1\n" }, rig.Fake.LastCall(nameof(IRcSession.SetNpcFlagsAsync))!.Arguments);
        // Set, remove and edits need the old flags whatever force says.
        Assert.Contains("the flags of NPC 1000", await rig.FailAsync("rc_npc_action", """{"action":"write_flags","id":1000,"set":{"open":"2"},"timeoutSeconds":1,"force":true}"""));

        // A weapon added while the restore was asked would be written over without its backup.
        rig.Nc.Weapons["bow"] = ("", "// old\n");
        rig.Nc.Publish();
        string id = (await rig.OkAsync("rc_delete_script", """{"kind":"weapon","name":"bow","collectMilliseconds":0}""")).GetProperty("backup").GetString()!;
        rig.Setup.Options.Update(o => o with { McpApprove = true });
        rig.Setup.Host.WhileAsking = () =>
        {
            rig.Nc.Weapons["bow"] = ("", "// added by other staff\n");
            rig.Nc.Publish();
        };
        Assert.Contains("while the question was open", await rig.FailAsync("rc_restore_backup", JsonSerializer.Serialize(new { id })));
        Assert.Equal("// added by other staff\n", rig.Nc.Weapons["bow"].Script);
    }

    [Fact]
    public async Task An_import_over_an_unreadable_file_is_reported_as_a_replacement()
    {
        await using var rig = new ServerRig();
        ServerRig.InstallFiles(rig.Fake, new[] { new FileBrowserFolder("rw", "levels/*") }, ("levels/a.txt", Encoding.ASCII.GetBytes("server")));
        string folder = Path.Combine(rig.Setup.Paths.McpWorkspaceDirectory, "work");
        Directory.CreateDirectory(Path.Combine(folder, "files", "levels"));
        File.WriteAllText(Path.Combine(folder, "files", "levels", "a.txt"), "local");
        rig.Fake.FakeMcpFileBrowser.DownloadHandler = _ => Task.FromResult<byte[]?>(null);
        JsonElement item = (await rig.OkAsync("rc_import", """{"localFolder":"work","force":true,"collectMilliseconds":0}""")).GetProperty("items")[0];
        Assert.Equal("replaced", item.GetProperty("outcome").GetString());
        Assert.Contains("could not be read", item.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Npc_files_without_an_id_are_reported_and_a_renamed_file_keeps_its_server()
    {
        await using var rig = new ServerRig();
        rig.Nc.Npcs[1000] = ("door", "// door\n");
        rig.Nc.Publish();
        string folder = (await rig.OkAsync("rc_export", """{"localFolder":"work","kinds":["npc"]}""")).GetProperty("folder").GetString()!;

        // The folder goes to another server, where id 1000 is another NPC; there the export adds that server's NPCs.
        (FakeRcSession other, FakeNc otherNc) = rig.AddSession("Other");
        otherNc.Npcs[1000] = ("shop", "// shop\n");
        otherNc.Npcs[2000] = ("door", "// other door\n");
        otherNc.Publish();
        await rig.OkAsync("rc_export", """{"localFolder":"work","kinds":["npc"],"server":"Other"}""");
        Assert.True(File.Exists(Path.Combine(folder, "npcs", "1000_shop.gs2")));
        // The export of Other did not take the door of the first server for its own NPC 1000.
        Assert.True(File.Exists(Path.Combine(folder, "npcs", "1000_door.gs2")));

        // The first server's door is renamed in the folder: it is still the first server's NPC, so on Other it goes by name (to 2000).
        File.Move(Path.Combine(folder, "npcs", "1000_door.gs2"), Path.Combine(folder, "npcs", "1000_door2.gs2"));
        File.WriteAllText(Path.Combine(folder, "npcs", "1000_door2.gs2"), "// door v2\n");
        File.WriteAllText(Path.Combine(folder, "npcs", "lamp.gs2"), "// no id\n");
        JsonElement imported = await rig.OkAsync("rc_import", """{"localFolder":"work","server":"Other","changedOnly":false,"collectMilliseconds":0}""");
        Assert.Equal("// shop\n", otherNc.Npcs[1000].Script);
        JsonElement[] items = imported.GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(items, item => item.GetProperty("item").GetString() == "npcs/lamp.gs2" && item.GetProperty("outcome").GetString() == "skipped");
        Assert.Contains(items, item => item.GetProperty("item").GetString()!.Contains("door2", StringComparison.Ordinal) && item.GetProperty("outcome").GetString() == "failed");
    }

    [UnixFact]
    public async Task Local_names_that_differ_in_case_are_two_files_where_the_system_tells_them_apart()
    {
        if (OperatingSystem.IsMacOS()) return;
        await using var rig = new ServerRig();
        rig.Nc.Weapons["Bow"] = ("", "// Bow\n");
        rig.Nc.Publish();
        string folder = (await rig.OkAsync("rc_export", """{"localFolder":"work","kinds":["weapon"]}""")).GetProperty("folder").GetString()!;
        File.WriteAllText(Path.Combine(folder, "weapons", "bow.gs2"), "// another weapon\n");
        JsonElement dry = await rig.OkAsync("rc_import", """{"localFolder":"work","dryRun":true}""");
        Assert.Contains(dry.GetProperty("items").EnumerateArray(), item => item.GetProperty("item").GetString() == "weapon bow");
    }

    [Fact]
    public async Task A_command_that_gets_no_answer_in_time_is_not_complete()
    {
        await using var rig = new ServerRig();
        JsonElement help = await rig.OkAsync("rc_script_help", """{"name":"echo","fromServer":true,"collectMilliseconds":200}""");
        Assert.False(help.GetProperty("complete").GetBoolean());
        Assert.Contains("collectMilliseconds", help.GetProperty("cut").GetString());
    }

    [UnixFact]
    public void A_root_inside_another_root_is_not_trusted_when_it_is_a_link_out_of_it()
    {
        using var outside = new TempFolder();
        using var workspace = new TempFolder();
        File.WriteAllText(Path.Combine(outside.Path, "secret"), "x");
        File.CreateSymbolicLink(Path.Combine(workspace.Path, "exports"), outside.Path);
        // The link is named as a root of its own, after the workspace that holds it.
        var local = new McpLocalFiles(workspace.Path, ".;exports");
        Assert.Contains("leads out of", Assert.Throws<McpToolException>(() => local.Resolve(Path.Combine(workspace.Path, "exports", "secret"))).Message);
        // Named alone, the user's own link is a root like any other.
        var alone = new McpLocalFiles(workspace.Path, Path.Combine(workspace.Path, "exports"));
        Assert.Equal(Path.Combine(workspace.Path, "exports", "secret"), alone.Resolve(Path.Combine(workspace.Path, "exports", "secret")));
    }

    [Fact]
    public async Task A_local_file_is_read_through_its_checked_handle()
    {
        using var workspace = new TempFolder();
        var local = new McpLocalFiles(workspace.Path, ".");
        string file = local.ResolveUnder(local.Resolve(string.Empty), "a.txt");
        File.WriteAllText(file, "content");
        Assert.Equal("content", Encoding.ASCII.GetString(await local.ReadAsync(file, CancellationToken.None)));
        Assert.Throws<McpToolException>(() => local.ResolveUnder(workspace.Path, "../a.txt"));
    }

    [Fact]
    public void The_root_of_a_drive_holds_every_path_on_it_and_device_prefixes_are_dropped()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.True(McpLocalFiles.IsInside(root, Path.Combine(root, "a", "b")));
        Assert.True(McpLocalFiles.IsInside(Path.Combine(root, "a"), Path.Combine(root, "a", "b")));
        Assert.False(McpLocalFiles.IsInside(Path.Combine(root, "a"), Path.Combine(root, "ab")));
        Assert.Equal(@"C:\x", McpLocalFiles.WithoutDevicePrefix(@"\\?\C:\x"));
        Assert.Equal(@"\\host\share\x", McpLocalFiles.WithoutDevicePrefix(@"\\?\UNC\host\share\x"));
        Assert.Equal("/a", McpLocalFiles.WithoutDevicePrefix("/a"));
    }

    [WindowsFact]
    public void A_windows_link_target_is_taken_as_text_from_the_links_folder()
    {
        Assert.Equal(@"C:\a\c", McpLocalFiles.WindowsLinkTarget(@"C:\a\b", @"..\c"));
        Assert.Equal(@"C:\foo", McpLocalFiles.WindowsLinkTarget(@"C:\a\b", @"\foo"));
        Assert.Equal(@"D:\x", McpLocalFiles.WindowsLinkTarget(@"C:\a", @"D:\x"));
    }
}
