using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;

namespace RemoteControl.Sync.Tests;

public sealed class EngineTests
{
    private const string BowPath = "weapons/weaponBow.txt";
    private const string UtilPath = "classes/Util.txt";
    private const string GuardPath = "npcs/npcGuard.txt";

    private static FakeServer StandardServer()
    {
        var server = new FakeServer();
        server.Weapons["Bow"] = ("bow.png", "//bow");
        server.Classes["Util"] = "//util";
        server.Npcs[5] = ("Guard", "OBJECT", "//guard");
        server.NpcFlags[5] = "a=1\nb=2";
        server.Config[SyncConfigKind.ServerOptions] = "name=Test\n";
        server.Config[SyncConfigKind.ServerFlags] = "flag=1\n";
        server.Config[SyncConfigKind.FolderConfig] = "file levels/*.nw\n";
        server.Roots.Add(new FileBrowserFolder("rw", "levels/*"));
        server.AddFile("levels/a.nw", "abc", 1000);
        server.AddFolder("levels", "sub");
        server.AddFile("levels/sub/b.nw", "xy", 2000);
        return server;
    }

    private static SyncSettings Everything(SyncDirection direction = SyncDirection.DownloadOnly) =>
        new() { Files = true, ServerConfig = true, Direction = direction };

    [Fact]
    public async Task FirstSyncDownloadsEverythingWithProgressInOrder()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(Everything(), StandardServer());
        await harness.SyncNowAsync();

        Assert.Equal(ScriptDocument.BuildWeapon("Bow", "bow.png", "//bow"), harness.ReadLocal(BowPath));
        Assert.Equal("//util", harness.ReadLocal(UtilPath));
        Assert.Equal(ScriptDocument.BuildNpc("Guard", 5, "OBJECT", string.Empty, string.Empty, "a=1\nb=2", "//guard"), harness.ReadLocal(GuardPath));
        Assert.Equal("name=Test\n", harness.ReadLocal("config/serveroptions.txt"));
        Assert.Equal("flag=1\n", harness.ReadLocal("config/serverflags.txt"));
        Assert.Equal("file levels/*.nw\n", harness.ReadLocal("config/folderconfig.txt"));
        Assert.Equal("abc", harness.ReadLocal("files/levels/a.nw"));
        Assert.Equal("xy", harness.ReadLocal("files/levels/sub/b.nw"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1000).UtcDateTime, File.GetLastWriteTimeUtc(harness.Local("files/levels/a.nw")));
        Assert.False(harness.Session.WasCalled(nameof(IRcSession.RequestNpcFlagsAsync)));

        string manifest = File.ReadAllText(SyncManifest.PathFor(harness.Workspace));
        Assert.True(SyncManifest.TryParse(manifest, out SyncManifest parsed, out _));
        Assert.Equal(3, parsed.Scripts.Count);
        Assert.All(parsed.Scripts, script => Assert.True(SyncHash.IsTrusted(script.LocalHash) && SyncHash.IsTrusted(script.ServerHash)));
        Assert.Equal(2, parsed.Files.Count);
        Assert.All(parsed.Files, file => Assert.True(SyncHash.IsTrusted(file.LocalHash)));
        Assert.Equal(new ManifestFile("levels/a.nw", "files/levels/a.nw", 3, 1000, SyncHash.Of("abc"u8)), parsed.Files.Single(file => file.RemotePath == "levels/a.nw"));
        Assert.Equal(3, parsed.Configs.Count);

        await harness.WaitUntilAsync(() => harness.Engine.Snapshot.Phase == "Sync complete", "the end of the pass");
        SyncSnapshot last = await harness.SnapshotAsync();
        Assert.Equal(8, last.Downloaded);
        Assert.Equal(SyncRunState.Stopped, last.State);
        Assert.Equal("levels", last.Settings.FileFolders);

        IReadOnlyList<SyncSnapshot> snapshots = harness.Snapshots;
        Assert.NotEmpty(snapshots);
        for (int index = 1; index < snapshots.Count; index++) Assert.True(snapshots[index].Sequence > snapshots[index - 1].Sequence);
        Assert.Contains(snapshots, snapshot => snapshot.State == SyncRunState.OneShot && snapshot.WorkTotal > 0);
        Assert.All(snapshots, snapshot => Assert.InRange(snapshot.Percent, 0, 100));
        Assert.All(snapshots, snapshot => Assert.True(snapshot.WorkDone <= snapshot.WorkTotal || snapshot.WorkTotal == 0));
        Assert.Contains(snapshots, snapshot => snapshot.State == SyncRunState.OneShot && snapshot.WorkTotal > 0 && snapshot.WorkDone == snapshot.WorkTotal);
        Assert.DoesNotContain(last.Log, line => line.Contains("Internal error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalEditGoesUp()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings { Direction = SyncDirection.TwoWay }, StandardServer());
        await harness.SyncNowAsync();
        harness.WriteLocal(BowPath, ScriptDocument.BuildWeapon("Bow", "bow.png", "//bow edited"));
        harness.WriteLocal(UtilPath, "//util edited");
        await harness.SyncNowAsync();

        RecordedCall weapon = Assert.Single(harness.Session.CallsTo(nameof(IRcSession.UpdateWeaponAsync)));
        Assert.Equal(new object?[] { "Bow", "bow.png", "//bow edited" }, weapon.Arguments);
        Assert.Equal(new object?[] { "Util", "//util edited" }, Assert.Single(harness.Session.CallsTo(nameof(IRcSession.UpdateClassAsync))).Arguments);
        Assert.Equal(SyncItemState.InSync, (await harness.StatusOf("weapon:Bow")).State);

        // Nothing changed since: a further pass uploads nothing and does not bring the old text back.
        await harness.SyncNowAsync();
        Assert.Single(harness.Session.CallsTo(nameof(IRcSession.UpdateWeaponAsync)));
        Assert.Equal("//util edited", harness.ReadLocal(UtilPath));
    }

    [Fact]
    public async Task ServerEditComesDownAndConfigurationChangesAreReported()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(Everything(), StandardServer());
        await harness.SyncNowAsync();
        var changes = new List<ServerConfigurationChangedEventArgs>();
        harness.Engine.ServerConfigurationChanged += (_, e) => changes.Add(e);
        harness.Server.Weapons["Bow"] = ("bow2.png", "//new bow");
        harness.Server.Config[SyncConfigKind.ServerOptions] = "name=Changed\n";
        harness.Server.AddFile("levels/a.nw", "abcd", 3000);
        await harness.SyncNowAsync();

        Assert.Equal(ScriptDocument.BuildWeapon("Bow", "bow2.png", "//new bow"), harness.ReadLocal(BowPath));
        Assert.Equal("name=Changed\n", harness.ReadLocal("config/serveroptions.txt"));
        Assert.Equal("abcd", harness.ReadLocal("files/levels/a.nw"));
        ServerConfigurationChangedEventArgs change = Assert.Single(changes);
        Assert.Equal((SyncConfigKind.ServerOptions, "name=Changed\n"), (change.Kind, change.Content));
        Assert.False(harness.Session.WasCalled(nameof(IRcSession.UpdateWeaponAsync)));
        Assert.False(harness.Session.WasCalled(nameof(IRcSession.UploadServerOptionsAsync)));
    }

    [Fact]
    public async Task BothChangedIsAConflictThatLosesNothing()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings { Direction = SyncDirection.TwoWay }, StandardServer());
        await harness.SyncNowAsync();
        string local = ScriptDocument.BuildWeapon("Bow", "bow.png", "//local edit");
        harness.WriteLocal(BowPath, local);
        harness.Server.Weapons["Bow"] = ("bow.png", "//server edit");
        await harness.SyncNowAsync();

        string server = ScriptDocument.BuildWeapon("Bow", "bow.png", "//server edit");
        Assert.Equal(local, harness.ReadLocal(BowPath));
        SyncItemStatus status = await harness.StatusOf("weapon:Bow");
        Assert.Equal(SyncItemState.Conflict, status.State);
        Assert.Equal(".graal-sync/conflicts/" + BowPath, status.ConflictCopy);
        Assert.Equal(server, harness.ReadLocal(status.ConflictCopy!));
        Assert.False(harness.Session.WasCalled(nameof(IRcSession.UpdateWeaponAsync)));
        Assert.Equal(1, (await harness.SnapshotAsync()).Conflicts);

        // A further pass keeps the conflict as it is.
        await harness.SyncNowAsync();
        Assert.Equal(local, harness.ReadLocal(BowPath));
        Assert.False(harness.Session.WasCalled(nameof(IRcSession.UpdateWeaponAsync)));

        await harness.Engine.ResolveConflictAsync("weapon:Bow", ConflictResolution.UseServer).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(server, harness.ReadLocal(BowPath));
        Assert.Equal(local, harness.ReadLocal(status.ConflictCopy + ".local"));
        Assert.Equal(SyncItemState.InSync, (await harness.StatusOf("weapon:Bow")).State);
    }

    [Fact]
    public async Task KeepingTheLocalSideUploadsIt()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings { Direction = SyncDirection.TwoWay }, StandardServer());
        await harness.SyncNowAsync();
        harness.WriteLocal(UtilPath, "//mine");
        harness.Server.Classes["Util"] = "//theirs";
        await harness.SyncNowAsync();
        Assert.Equal(SyncItemState.Conflict, (await harness.StatusOf("class:Util")).State);

        await harness.Engine.ResolveConflictAsync("class:Util", ConflictResolution.KeepLocal).WaitAsync(TimeSpan.FromSeconds(5));
        await harness.IdleAsync();
        await harness.WaitUntilAsync(() => harness.Session.WasCalled(nameof(IRcSession.UpdateClassAsync)), "the upload");
        Assert.Equal(new object?[] { "Util", "//mine" }, harness.Session.LastCall(nameof(IRcSession.UpdateClassAsync))!.Arguments);
        Assert.Equal("//mine", harness.ReadLocal(UtilPath));
    }

    [Fact]
    public async Task WhileRunningTheWatcherReportsLocalEditsAndEventsStayOnTheContext()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings { Direction = SyncDirection.TwoWay }, StandardServer(),
            configure: options => options with { WatchFileSystem = true });
        bool offContext = false;
        harness.Engine.StateChanged += (_, _) =>
        {
            if (SynchronizationContext.Current != harness.Context) offContext = true;
        };
        harness.Engine.LogAdded += (_, _) =>
        {
            if (SynchronizationContext.Current != harness.Context) offContext = true;
        };
        harness.Engine.Start();
        await harness.WaitUntilAsync(() => File.Exists(harness.Local(UtilPath)), "the first pull");
        await harness.IdleAsync();
        File.WriteAllText(harness.Local(UtilPath), "//watched");
        await harness.WaitUntilAsync(() => harness.Session.WasCalled(nameof(IRcSession.UpdateClassAsync)), "the upload of the watched edit");
        Assert.Equal(new object?[] { "Util", "//watched" }, harness.Session.LastCall(nameof(IRcSession.UpdateClassAsync))!.Arguments);
        Assert.False(harness.Session.WasCalled(nameof(IRcSession.UpdateWeaponAsync)));
        Assert.Equal(SyncRunState.Running, (await harness.SnapshotAsync()).State);
        Assert.False(offContext);
    }

    [Fact]
    public async Task AFileTheWatcherSeesWhileItIsWrittenIsUploadedOnceItIsQuiet()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings { Direction = SyncDirection.TwoWay }, StandardServer(),
            configure: options => options with { WatchFileSystem = true, WatchSettleDelay = TimeSpan.FromSeconds(1) });
        harness.Engine.Start();
        await harness.WaitUntilAsync(() => File.Exists(harness.Local(UtilPath)), "the first pull");
        await harness.IdleAsync();
        // An editor that saves by emptying the file and writing it again: the empty class must never go to the server (it would delete it).
        File.WriteAllText(harness.Local(UtilPath), string.Empty);
        await Task.Delay(100);
        File.WriteAllText(harness.Local(UtilPath), "//written");
        await harness.WaitUntilAsync(() => harness.Session.WasCalled(nameof(IRcSession.UpdateClassAsync)), "the upload of the written file");
        await harness.IdleAsync();
        RecordedCall upload = Assert.Single(harness.Session.CallsTo(nameof(IRcSession.UpdateClassAsync)));
        Assert.Equal(new object?[] { "Util", "//written" }, upload.Arguments);
    }

    [Fact]
    public async Task ScriptTimesComeFromTheServerFolders()
    {
        FakeServer server = StandardServer();
        server.Roots.Add(new FileBrowserFolder("r", "weapons/*"));
        server.Roots.Add(new FileBrowserFolder("r", "scripts/*.txt"));
        server.Listings["weapons"] = [new FileBrowserEntry("weaponBow.txt", "r", 10, 1600000000)];
        server.Listings["scripts"] = [new FileBrowserEntry("Util.txt", "r", 10, 1600000100)];
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings(), server);
        await harness.SyncNowAsync();
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1600000000).UtcDateTime, File.GetLastWriteTimeUtc(harness.Local(BowPath)));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1600000100).UtcDateTime, File.GetLastWriteTimeUtc(harness.Local(UtilPath)));
        Assert.True(SyncManifest.TryParse(File.ReadAllText(SyncManifest.PathFor(harness.Workspace)), out SyncManifest manifest, out _));
        Assert.Equal(1600000000, manifest.Scripts.Single(script => script.Name == "Bow").Modified);
    }

    [Fact]
    public async Task UploadOnlyDoesNotPull()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings { Direction = SyncDirection.TwoWay }, StandardServer());
        await harness.SyncNowAsync();
        harness.Engine.UpdateSettings(settings => settings with { Direction = SyncDirection.UploadOnly });
        await harness.OnContext(harness.Session.ClearCalls);
        harness.WriteLocal(UtilPath, "//up only");
        await harness.SyncNowAsync();
        Assert.False(harness.Session.WasCalled(nameof(IRcSession.RequestClassScriptAsync)));
        Assert.False(harness.Session.WasCalled(nameof(IRcSession.RequestWeaponScriptAsync)));
        Assert.Equal(new object?[] { "Util", "//up only" }, Assert.Single(harness.Session.CallsTo(nameof(IRcSession.UpdateClassAsync))).Arguments);
    }

    [Fact]
    public async Task ExcludedFilesAreNeitherDownloadedNorUploaded()
    {
        FakeServer server = StandardServer();
        server.AddFile("levels/skip.bak", "old", 10);
        server.AddFile("levels/Thumbs.db", "x", 10);
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings { Scripts = false, Files = true, ExcludeFiles = "*.bak; thumbs.db" }, server);
        await harness.SyncNowAsync();

        Assert.True(File.Exists(harness.Local("files/levels/a.nw")));
        Assert.False(File.Exists(harness.Local("files/levels/skip.bak")));
        Assert.False(File.Exists(harness.Local("files/levels/Thumbs.db")));
        Assert.DoesNotContain(harness.Session.CallsTo("SyncFileBrowser.DownloadAsync"), call => call.Argument<string>(0).EndsWith(".bak", StringComparison.Ordinal));
        Assert.False(harness.Session.WasCalled(nameof(IRcSession.RequestWeaponScriptAsync)));
    }

    [Fact]
    public async Task DeletionNeedsPermission()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings { Direction = SyncDirection.TwoWay }, StandardServer());
        await harness.SyncNowAsync();
        File.Delete(harness.Local(UtilPath));
        await harness.SyncNowAsync();
        Assert.False(harness.Session.WasCalled(nameof(IRcSession.DeleteClassAsync)));
        Assert.Equal("//util", harness.ReadLocal(UtilPath));

        harness.Engine.UpdateSettings(settings => settings with { AllowRemoteDeletes = true });
        File.Delete(harness.Local(UtilPath));
        await harness.SyncNowAsync();
        Assert.Equal(new object?[] { "Util" }, Assert.Single(harness.Session.CallsTo(nameof(IRcSession.DeleteClassAsync))).Arguments);
        Assert.DoesNotContain(await harness.Engine.GetItemStatusesAsync(), item => item.Key == "class:Util");
        Assert.False(File.Exists(harness.Local(UtilPath)));
        Assert.False(harness.Session.WasCalled(nameof(IRcSession.DeleteWeaponAsync)));
    }

    [Fact]
    public async Task ManyDeletionsAtOnceWaitForConfirmation()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings { Direction = SyncDirection.TwoWay, AllowRemoteDeletes = true }, StandardServer(),
            configure: options => options with { MaxAutomaticDeletions = 1 });
        await harness.SyncNowAsync();
        File.Delete(harness.Local(UtilPath));
        File.Delete(harness.Local(BowPath));
        await harness.SyncNowAsync();

        Assert.False(harness.Session.WasCalled(nameof(IRcSession.DeleteClassAsync)));
        Assert.False(harness.Session.WasCalled(nameof(IRcSession.DeleteWeaponAsync)));
        Assert.Equal(SyncItemState.PendingDeletion, (await harness.StatusOf("class:Util")).State);
        Assert.Equal(SyncItemState.PendingDeletion, (await harness.StatusOf("weapon:Bow")).State);
        Assert.False(File.Exists(harness.Local(UtilPath)));

        harness.Engine.ConfirmPendingDeletions();
        await harness.WaitUntilAsync(() => harness.Session.WasCalled(nameof(IRcSession.DeleteClassAsync)) && harness.Session.WasCalled(nameof(IRcSession.DeleteWeaponAsync)), "the deletions");
        await harness.IdleAsync();
        Assert.DoesNotContain(await harness.Engine.GetItemStatusesAsync(), item => item.Key is "class:Util" or "weapon:Bow");
    }

    [Fact]
    public async Task AnEditMadeWhileTheProgramWasClosedIsNotOverwritten()
    {
        using var folder = new TempFolder();
        FakeServer server = StandardServer();
        await using (SyncHarness first = await SyncHarness.CreateAsync(new SyncSettings(), server, folder))
        {
            await first.SyncNowAsync();
        }

        // Edited with the program closed.
        string edited = ScriptDocument.BuildWeapon("Bow", "bow.png", "//edited offline");
        string path = Path.Combine(folder.Path, "ws", "weapons", "weaponBow.txt");
        File.WriteAllText(path, edited, System.Text.Encoding.Latin1);

        await using SyncHarness second = await SyncHarness.CreateAsync(new SyncSettings(), server, folder);
        await second.SyncNowAsync();
        Assert.Equal(edited, second.ReadLocal(BowPath));
        Assert.Equal(SyncItemState.LocallyModified, (await second.StatusOf("weapon:Bow")).State);
        Assert.False(second.Session.WasCalled(nameof(IRcSession.UpdateWeaponAsync)));

        server.Weapons["Bow"] = ("bow.png", "//changed on the server");
        await second.SyncNowAsync();
        Assert.Equal(edited, second.ReadLocal(BowPath));
        Assert.Equal(SyncItemState.Conflict, (await second.StatusOf("weapon:Bow")).State);
    }

    [Fact]
    public async Task AnOfflineEditIsUploadedInTwoWayMode()
    {
        using var folder = new TempFolder();
        FakeServer server = StandardServer();
        await using (SyncHarness first = await SyncHarness.CreateAsync(new SyncSettings { Direction = SyncDirection.TwoWay }, server, folder))
        {
            await first.SyncNowAsync();
        }

        File.WriteAllText(Path.Combine(folder.Path, "ws", "classes", "Util.txt"), "//offline");
        await using SyncHarness second = await SyncHarness.CreateAsync(new SyncSettings { Direction = SyncDirection.TwoWay }, server, folder);
        await second.SyncNowAsync();
        Assert.Equal(new object?[] { "Util", "//offline" }, Assert.Single(second.Session.CallsTo(nameof(IRcSession.UpdateClassAsync))).Arguments);
        Assert.Equal("//offline", second.ReadLocal(UtilPath));
    }

    [Fact]
    public async Task ACxxManifestWithoutHashesDoesNotLoseOfflineEdits()
    {
        using var folder = new TempFolder();
        string workspace = Path.Combine(folder.Path, "ws");
        Directory.CreateDirectory(Path.Combine(workspace, "classes"));
        Directory.CreateDirectory(Path.Combine(workspace, ".graal-sync"));
        File.WriteAllText(Path.Combine(workspace, "classes", "Util.txt"), "//edited after the C++ synced");
        File.WriteAllText(SyncManifest.PathFor(workspace),
            "[class:Util]\ntype=class\nname=Util\nimage=\nnpcType=\nlevel=\nid=0\nmodified=0\npath=classes/Util.txt\n");
        FakeServer server = StandardServer();
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings(), server, folder);
        await harness.SyncNowAsync();
        Assert.Equal("//edited after the C++ synced", harness.ReadLocal(UtilPath));
        Assert.Equal(SyncItemState.Conflict, (await harness.StatusOf("class:Util")).State);
        Assert.Equal("//util", harness.ReadLocal(".graal-sync/conflicts/" + UtilPath));
    }

    [Fact]
    public async Task AFolderTheServerRefusesStaysInTheSelection()
    {
        // Alpaxis answers a folder it does not open for the account with its reason and an empty listing. That is no empty folder:
        // taken for one, the folder left the saved selection for good, also after the right came back.
        FakeServer server = StandardServer();
        server.RefusedFolders["gone"] = true;
        await using SyncHarness harness = await SyncHarness.CreateAsync(Everything() with { FileFolders = "levels; gone" }, server);
        await harness.SyncNowAsync();
        await harness.WaitUntilAsync(() => harness.Engine.Snapshot.Phase == "Sync complete", "the end of the pass");

        SyncSnapshot last = await harness.SnapshotAsync();
        Assert.Contains(harness.Session.CallsTo("SyncFileBrowser.ChangeFolderAsync"), call => call.Argument<string>(0).Trim('/') == "gone");
        Assert.Equal(["gone", "levels"], SyncNames.SplitFolders(last.Settings.FileFolders));
        Assert.Equal("abc", harness.ReadLocal("files/levels/a.nw"));
    }

    [Fact]
    public async Task AnEmptyFolderLeavesTheSelection()
    {
        FakeServer server = StandardServer();
        server.Listings["bare"] = [];
        await using SyncHarness harness = await SyncHarness.CreateAsync(Everything() with { FileFolders = "levels; bare" }, server);
        await harness.SyncNowAsync();
        await harness.WaitUntilAsync(() => harness.Engine.Snapshot.Phase == "Sync complete", "the end of the pass");

        SyncSnapshot last = await harness.SnapshotAsync();
        Assert.Equal(["levels"], SyncNames.SplitFolders(last.Settings.FileFolders));
    }

    [Fact]
    public async Task DisconnectStopsCleanlyAndAReattachContinues()
    {
        FakeServer server = StandardServer();
        server.SilentFolders["levels"] = true;
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings { Scripts = false, Files = true }, server);
        harness.Engine.Start();
        FakeRcSession first = harness.Session;
        await harness.WaitUntilAsync(() => first.CallsTo("SyncFileBrowser.ChangeFolderAsync").Any(), "the listing request");
        await harness.OnContext(() => first.RaiseDisconnected("Connection lost"));
        await harness.WaitUntilAsync(() => harness.Engine.Snapshot.State == SyncRunState.Detached, "the detach");
        Assert.False(File.Exists(harness.Local("files/levels/a.nw")));
        int callsAfterDetach = first.Calls.Count;
        await Task.Delay(100);
        Assert.Equal(callsAfterDetach, first.Calls.Count);

        server.SilentFolders.Clear();
        FakeRcSession second = harness.NewSession();
        await harness.AttachAsync(second);
        await harness.WaitUntilAsync(() => File.Exists(harness.Local("files/levels/sub/b.nw")), "the downloads after the reattach");
        Assert.Equal("abc", harness.ReadLocal("files/levels/a.nw"));
        SyncSnapshot resumed = await harness.SnapshotAsync();
        Assert.Equal(SyncRunState.Running, resumed.State);
        Assert.DoesNotContain(resumed.Log, line => line.Contains("Internal error", StringComparison.Ordinal));

        // A replaced session can no longer reach the engine.
        FakeRcSession third = harness.NewSession();
        await harness.AttachAsync(third);
        harness.Engine.UpdateSettings(settings => settings with { Scripts = true });
        await harness.OnContext(() => second.RaiseScript(ScriptKind.Class, "Ghost", "//ghost"));
        await harness.IdleAsync();
        Assert.False(File.Exists(harness.Local("classes/Ghost.txt")));
    }

    [Fact]
    public async Task ServerNamesThatLeaveTheFolderAreRefused()
    {
        var server = new FakeServer();
        server.Roots.Add(new FileBrowserFolder("rw", "levels/*"));
        server.Listings["levels"] =
        [
            new FileBrowserEntry("../../evil.txt", "rw", 4, 1),
            new FileBrowserEntry("..\\..\\evil2.txt", "rw", 4, 1),
            new FileBrowserEntry("/etc/evil3.txt", "rw", 4, 1),
            new FileBrowserEntry("C:/evil4.txt", "rw", 4, 1),
            new FileBrowserEntry("con.txt", "rw", 4, 1),
            new FileBrowserEntry("a:b.txt", "rw", 4, 1),
            new FileBrowserEntry("ok.txt", "rw", 2, 1),
        ];
        foreach (string name in new[] { "../../evil.txt", "evil.txt", "levels/evil2.txt", "levels/etc/evil3.txt", "levels/C:/evil4.txt", "levels/con.txt", "levels/a:b.txt" })
            server.Files[name] = "evil"u8.ToArray();
        server.Files["levels/ok.txt"] = "ok"u8.ToArray();
        server.Weapons["../../../evil"] = (string.Empty, "//w");
        server.Classes["con"] = "//c";
        server.Classes["../../x"] = "//x";
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings { Files = true }, server);
        await harness.SyncNowAsync();

        Assert.Equal("ok", harness.ReadLocal("files/levels/ok.txt"));
        string[] outside = Directory.GetFiles(harness.Folder.Path, "*", SearchOption.AllDirectories)
            .Where(file => !file.StartsWith(harness.Workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !file.StartsWith(harness.UserDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(outside);
        string[] written = Directory.GetFiles(harness.Workspace, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(harness.Workspace, file).Replace('\\', '/')).ToArray();
        Assert.DoesNotContain(written, file => file.Contains("evil", StringComparison.Ordinal) && file.StartsWith("files/", StringComparison.Ordinal) && !file.StartsWith("files/levels/", StringComparison.Ordinal));
        Assert.DoesNotContain("files/levels/con.txt", written);
        Assert.DoesNotContain("files/levels/a:b.txt", written);
        Assert.Contains((await harness.SnapshotAsync()).Log, line => line.Contains("Refused server file name levels/con.txt", StringComparison.Ordinal));
        Assert.Contains(written, file => file.StartsWith("classes/con-", StringComparison.Ordinal));
        Assert.Contains("weapons/weapon%046%046%047%046%046%047%046%046%047evil.txt", written);
        Assert.Contains("classes/.._.._x.txt", written);
    }

    [Fact]
    public async Task NoWorkspaceMeansNothingRuns()
    {
        using var folder = new TempFolder();
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings(), StandardServer(), folder, attach: false);
        SyncSettingsFile.Save(SyncSettingsFile.PathFor(harness.UserDirectory, SyncHarness.ServerName), new SyncSettings());
        await harness.AttachAsync(harness.Session);
        harness.Engine.Start();
        await harness.IdleAsync();
        await harness.WaitUntilAsync(() => harness.Engine.Snapshot.Log.Any(line => line.EndsWith("Choose a sync workspace first.", StringComparison.Ordinal)), "the message");
        Assert.Equal(SyncRunState.Stopped, (await harness.SnapshotAsync()).State);
        Assert.False(harness.Session.WasCalled(nameof(IRcSession.RequestWeaponScriptAsync)));
    }

    [Fact]
    public async Task SettingsChangesAreSaved()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(new SyncSettings(), StandardServer());
        harness.Engine.UpdateSettings(settings => settings with { Direction = SyncDirection.UploadOnly, AllowRemoteDeletes = true, ExcludeFiles = "*.bak", AutoStart = true });
        string path = SyncSettingsFile.PathFor(harness.UserDirectory, SyncHarness.ServerName);
        await harness.WaitUntilAsync(() => SyncSettingsFile.Load(path).Settings.ExcludeFiles == "*.bak", "the saved settings");
        SyncSettings saved = SyncSettingsFile.Load(path).Settings;
        Assert.Equal((SyncDirection.UploadOnly, true, true), (saved.Direction, saved.AllowRemoteDeletes, saved.AutoStart));
        Assert.Equal(harness.Workspace, saved.Workspace);
    }
}

public sealed class GitTests
{
    private static Dictionary<string, string> Identity(string home)
    {
        string config = Path.Combine(home, "gitconfig");
        File.WriteAllText(config, string.Empty);
        return new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["GIT_CONFIG_GLOBAL"] = config,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_AUTHOR_NAME"] = "Sync Test",
            ["GIT_AUTHOR_EMAIL"] = "sync@test.invalid",
            ["GIT_COMMITTER_NAME"] = "Sync Test",
            ["GIT_COMMITTER_EMAIL"] = "sync@test.invalid",
        };
    }

    [Fact]
    public async Task BacksUpThroughGitWithTheStoredRemote()
    {
        if (GitBackup.FindGit(AppContext.BaseDirectory) is null) return;
        using var folder = new TempFolder();
        string home = Path.Combine(folder.Path, "home");
        Directory.CreateDirectory(home);
        Dictionary<string, string> environment = Identity(home);
        string bare = Path.Combine(folder.Path, "remote.git");
        Directory.CreateDirectory(bare);
        Assert.True((await new GitBackup(bare) { Environment = environment }.RunAsync(["init", "--bare"])).Success);

        var settings = new SyncSettings { GitAutoCommit = true, GitAutoPush = true, GitRemoteUrl = bare };
        await using SyncHarness harness = await SyncHarness.CreateAsync(settings, null, folder, options => options with { GitEnvironment = environment, GitTimeout = TimeSpan.FromSeconds(20) });
        harness.Server.Weapons["Bow"] = ("bow.png", "//bow");
        Assert.Null(await harness.Engine.SaveGitTokenAsync("tok-SECRET-123"));

        GitResult init = await harness.Engine.GitInitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(init.Success, init.Output);
        Assert.True(Directory.Exists(Path.Combine(harness.Workspace, ".git")));

        var completed = new List<SyncGitEventArgs>();
        harness.Engine.GitCompleted += (_, e) =>
        {
            lock (completed) completed.Add(e);
        };
        await harness.SyncNowAsync();
        await harness.WaitUntilAsync(() =>
        {
            lock (completed) return completed.Any(e => e.Operation == "Commit and push");
        }, "the automatic commit", 30);
        SyncGitEventArgs auto;
        lock (completed) auto = completed.Single(e => e.Operation == "Commit and push");
        Assert.True(auto.Result.Success, auto.Result.Output);

        var reader = new GitBackup(harness.Workspace) { Environment = environment };
        GitResult log = await reader.RunAsync(["log", "--format=%s"]);
        Assert.Equal("[graal-sync] " + SyncHarness.ServerName, log.Output.Trim());
        GitResult files = await reader.RunAsync(["ls-files"]);
        Assert.Contains("weapons/weaponBow.txt", files.Output);
        Assert.Contains(".graal-sync/manifest.conf", files.Output);
        GitResult remote = await new GitBackup(bare) { Environment = environment }.RunAsync(["log", "--all", "--format=%s"]);
        Assert.Equal("[graal-sync] " + SyncHarness.ServerName, remote.Output.Trim());

        harness.WriteLocal("weapons/weaponBow.txt", ScriptDocument.BuildWeapon("Bow", "bow.png", "//changed"));
        GitResult commit = await harness.Engine.GitCommitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(commit.Success, commit.Output);
        GitResult push = await harness.Engine.GitPushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(push.Success, push.Output);
        GitResult pull = await harness.Engine.GitPullAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(pull.Success, pull.Output);

        Assert.False(File.Exists(Path.Combine(harness.Workspace, ".graal-sync", ".git-askpass.sh")));
        Assert.False(File.Exists(Path.Combine(harness.Workspace, ".graal-sync", ".git-ssh-key")));
        SyncSnapshot final = await harness.SnapshotAsync();
        Assert.DoesNotContain(final.Log, line => line.Contains("tok-SECRET", StringComparison.Ordinal));
        Assert.True(final.HasGitToken);
    }

    [Fact]
    public async Task RefusesCredentialsInTheUrlAndReportsAMissingWorkspace()
    {
        using var folder = new TempFolder();
        var git = new SyncGit(string.Empty, "S", null);
        Assert.False((await git.CommitAsync()).Success);
        var withUrl = new SyncGit(folder.Path, "S", null) { RemoteUrl = "https://user:pw@example.invalid/repo.git" };
        GitResult result = await withUrl.PushAsync();
        Assert.False(result.Success);
        Assert.Contains("Credentials must not be embedded", result.Output);
    }
}
