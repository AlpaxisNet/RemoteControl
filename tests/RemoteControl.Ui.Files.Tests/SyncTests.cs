using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Sync;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Files.Tests;

public sealed class SyncTests
{
    /// <summary>Scrolls a control of a scrolled page into view, then clicks it.</summary>
    private static void Click(Control control)
    {
        control.BringIntoView();
        TestUi.Settle();
        TestUi.Click(control);
    }

    private static Fixture WithSync(string? theme = null)
    {
        var f = new Fixture(theme);
        f.CreateGitScript();
        f.Test.Environment.SetOptions(o => o with { SyncEnabled = true });
        return f;
    }

    private static async Task<SyncWindow> OpenAsync(Fixture f)
    {
        await f.Files.Attached;
        var window = (SyncWindow)f.Context.OpenTool(ToolIds.Sync)!;
        await TestUi.WaitUntilAsync(() => window.Shown.State != SyncRunState.Detached, what: "the engine to attach");
        return window;
    }

    [AvaloniaFact]
    public void Sync_status_follows_the_option()
    {
        using var f = new Fixture();
        ISyncStatus status = f.Context.Services.Get<ISyncStatus>()!;
        Assert.Null(f.Files.Engine);
        Assert.Null(status.Percent);
        Assert.False(status.IsFileSyncActive);
        FilesFeature.Start(f.Context);
        Assert.Same(status, f.Context.Services.Get<ISyncStatus>());

        f.Test.Environment.SetOptions(o => o with { SyncEnabled = true });
        Assert.NotNull(f.Files.Engine);
        f.Test.Environment.SetOptions(o => o with { SyncEnabled = false });
        Assert.Null(f.Files.Engine);
        Assert.Null(status.Percent);
    }

    [AvaloniaFact]
    public void With_sync_off_the_window_says_so()
    {
        using var f = new Fixture();
        Assert.Null(f.Context.OpenTool(ToolIds.Sync));
        RcDialog dialog = TestUi.FindDialog();
        Assert.Contains("Sync is turned off", dialog.Message);
        dialog.Accept();
    }

    [AvaloniaFact]
    public async Task The_window_shows_the_engine_and_its_settings_controls_change_it()
    {
        using Fixture f = WithSync();
        SyncWindow window = await OpenAsync(f);
        SyncEngine engine = f.Files.Engine!;
        Assert.StartsWith("Sync & Git Backups (Alpha) - Test Server", window.Title);
        Assert.Contains("State: Stopped", TestUi.Find<TextBlock>(window, "StatusText").Text);

        string workspace = Path.Combine(f.Test.Environment.Root, "workspace");
        Directory.CreateDirectory(workspace);
        window.PickWorkspace = () => Task.FromResult<string?>(workspace);
        Click(TestUi.Find<Button>(window, "ChooseWorkspaceButton"));
        await TestUi.WaitUntilAsync(() => engine.Settings.Workspace == workspace && TestUi.Find<TextBox>(window, "WorkspaceBox").Text == workspace);

        Click(TestUi.Find<CheckBox>(window, "FileSyncBox"));
        await TestUi.WaitUntilAsync(() => engine.Settings.Files);
        Click(TestUi.Find<CheckBox>(window, "ConfigSyncBox"));
        await TestUi.WaitUntilAsync(() => engine.Settings.ServerConfig);
        Click(TestUi.Find<CheckBox>(window, "ScriptSyncBox"));
        await TestUi.WaitUntilAsync(() => !engine.Settings.Scripts);
        Click(TestUi.Find<CheckBox>(window, "AutoStartBox"));
        await TestUi.WaitUntilAsync(() => engine.Settings.AutoStart);
        var allowDeletes = TestUi.Find<CheckBox>(window, "AllowDeletesBox");
        Assert.False(allowDeletes.IsEnabled);
        TestUi.Find<ComboBox>(window, "DirectionCombo").SelectedIndex = 2;
        await TestUi.WaitUntilAsync(() => engine.Settings.Direction == SyncDirection.TwoWay && allowDeletes.IsEnabled);
        Click(allowDeletes);
        await TestUi.WaitUntilAsync(() => engine.Settings.AllowRemoteDeletes);

        TestUi.TypeInto(TestUi.Find<TextBox>(window, "ExcludeBox"), "*.tmp");
        await TestUi.WaitUntilAsync(() => engine.Settings.ExcludeFiles == "*.tmp", what: "the exclusions");
        TestUi.Find<TabControl>(window, "Pages").SelectedIndex = 2;
        TestUi.Settle();
        TestUi.TypeInto(TestUi.Find<TextBox>(window, "RemoteUrlBox"), "git@example.invalid:o/r.git");
        await TestUi.WaitUntilAsync(() => engine.Settings.GitRemoteUrl == "git@example.invalid:o/r.git", what: "the upstream");
        Click(TestUi.Find<CheckBox>(window, "AutoCommitBox"));
        Click(TestUi.Find<CheckBox>(window, "AutoPushBox"));
        Click(TestUi.Find<CheckBox>(window, "AutoPullBox"));
        await TestUi.WaitUntilAsync(() => engine.Settings.GitAutoCommit && engine.Settings.GitAutoPush && engine.Settings.GitAutoPull);
        Shots.Save(window, "sync-git-dark");
    }

    [AvaloniaFact]
    public async Task Start_stop_and_sync_now_call_the_engine()
    {
        using Fixture f = WithSync();
        SyncWindow window = await OpenAsync(f);
        SyncEngine engine = f.Files.Engine!;
        string workspace = Path.Combine(f.Test.Environment.Root, "ws");
        window.PickWorkspace = () => Task.FromResult<string?>(workspace);

        Click(TestUi.Find<Button>(window, "StartButton"));
        await TestUi.WaitUntilAsync(() => engine.Settings.Workspace == workspace && window.Shown.State is SyncRunState.Running or SyncRunState.WaitingForNcServer, what: "running");
        Assert.Contains(window.Shown.Log, line => line.Contains("Synchronization started", StringComparison.Ordinal));
        Assert.True(TestUi.Find<Button>(window, "StopButton").IsEnabled);
        Click(TestUi.Find<Button>(window, "StopButton"));
        await TestUi.WaitUntilAsync(() => window.Shown.State == SyncRunState.Stopped, what: "stopped");
        Click(TestUi.Find<Button>(window, "SyncNowButton"));
        await TestUi.WaitUntilAsync(() => window.Shown.Log.Any(line => line.Contains("One-shot synchronization started", StringComparison.Ordinal)), what: "the one-shot");
        TestUi.Find<TabControl>(window, "Pages").SelectedIndex = 3;
        await TestUi.WaitUntilAsync(() => TestUi.Find<LogView>(window, "ActivityLog").Lines.Any(line => line.Contains("One-shot", StringComparison.Ordinal)));
        Shots.Save(window, "sync-activity-dark");
        engine.Stop();
    }

    /// <summary>How long a git step may take: its first run on a build machine (Windows above all) can need many seconds; the wait ends as soon as git answered.</summary>
    private static readonly TimeSpan GitWait = TimeSpan.FromSeconds(90);

    [AvaloniaFact]
    public async Task Git_buttons_and_credentials_call_the_engine()
    {
        using Fixture f = WithSync();
        SyncWindow window = await OpenAsync(f);
        SyncEngine engine = f.Files.Engine!;
        string workspace = Path.Combine(f.Test.Environment.Root, "ws");
        engine.UpdateSettings(s => s with { Workspace = workspace });
        await TestUi.WaitUntilAsync(() => window.Shown.Settings.Workspace == workspace);

        TestUi.Find<TabControl>(window, "Pages").SelectedIndex = 2;
        TestUi.Settle();
        Click(TestUi.Find<Button>(window, "GitInitButton"));
        await TestUi.WaitUntilAsync(() => window.GitResult == "Done.", GitWait, "git init");
        Assert.Contains(window.Shown.Log, line => line.Contains("Git repository initialized", StringComparison.Ordinal));
        Click(TestUi.Find<Button>(window, "GitCommitButton"));
        await TestUi.WaitUntilAsync(() => window.Shown.Log.Any(line => line.Contains("Sync changes committed", StringComparison.Ordinal)), GitWait, "git commit");

        TestUi.Find<TabControl>(window, "Pages").SelectedIndex = 4;
        TestUi.Settle();
        string key = Path.Combine(f.Test.Environment.Root, "id_test");
        File.WriteAllText(key, "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n");
        window.PickKeyFile = () => Task.FromResult<string?>(key);
        Click(TestUi.Find<Button>(window, "ImportKeyButton"));
        await TestUi.WaitUntilAsync(() => window.Shown.HasGitKey, GitWait, "the key");
        TestUi.Find<TextBox>(window, "TokenBox").Text = "ghp_testtoken";
        Click(TestUi.Find<Button>(window, "SaveTokenButton"));
        await TestUi.WaitUntilAsync(() => window.Shown.HasGitToken, GitWait, "the token");
        Assert.Equal(string.Empty, TestUi.Find<TextBox>(window, "TokenBox").Text);
        Shots.Save(window, "sync-credentials-dark");
        Click(TestUi.Find<Button>(window, "RemoveTokenButton"));
        Click(TestUi.Find<Button>(window, "RemoveKeyButton"));
        await TestUi.WaitUntilAsync(() => !window.Shown.HasGitToken && !window.Shown.HasGitKey, what: "removed credentials");
    }

    [AvaloniaFact]
    public async Task A_file_that_is_no_key_is_refused()
    {
        using Fixture f = WithSync();
        SyncWindow window = await OpenAsync(f);
        string file = Path.Combine(f.Test.Environment.Root, "notakey.txt");
        File.WriteAllText(file, "hello");
        window.PickKeyFile = () => Task.FromResult<string?>(file);
        TestUi.Find<TabControl>(window, "Pages").SelectedIndex = 4;
        TestUi.Settle();
        Click(TestUi.Find<Button>(window, "ImportKeyButton"));
        RcDialog dialog = TestUi.FindDialog();
        Assert.Contains("not a private key", dialog.Message);
        dialog.Accept();
        Assert.False(window.Shown.HasGitKey);
    }

    [AvaloniaFact]
    public async Task The_folder_picker_lists_the_server_roots()
    {
        using Fixture f = WithSync();
        SyncWindow window = await OpenAsync(f);
        Click(TestUi.Find<Button>(window, "ChooseFoldersButton"));
        FolderPickerWindow picker = null!;
        await TestUi.WaitUntilAsync(() => (picker = f.Context.Windows.All.OfType<FolderPickerWindow>().FirstOrDefault()!) is not null && f.Fake.WasCalled("SyncFileBrowser.StartAsync"), what: "the picker");
        f.Fake.FakeSyncFileBrowser.RaiseFoldersReceived([new FileBrowserFolder("rw", "levels/*"), new FileBrowserFolder("r", "images/*.png")]);
        await TestUi.WaitUntilAsync(() => picker.Roots.Count == 2, what: "the roots");
        Assert.Equal(["levels/", "images/"], picker.Roots.Select(node => node.Name));
        Shots.Save(picker, "sync-folder-picker-dark");
        picker.Roots[0].IsChecked = true;
        Assert.True(TestUi.Find<Button>(picker, "ApplyButton").IsEnabled);
        Click(TestUi.Find<Button>(picker, "ApplyButton"));
        await TestUi.WaitUntilAsync(() => f.Files.Engine!.Settings.FileFolders == "levels", what: "the folders");
        Assert.True(picker.IsClosed);
        Assert.Equal("levels", TestUi.Find<TextBox>(window, "FoldersBox").Text);
    }

    [AvaloniaFact]
    public async Task Conflicts_and_pending_deletions_are_listed_and_answered()
    {
        using Fixture f = WithSync();
        SyncWindow window = await OpenAsync(f);
        var conflict = new SyncItemStatus("weapon:Bow", SyncItemKind.Weapon, "Bow", "weapons/weaponBow.txt", SyncItemState.Conflict, "Changed on both sides.", ".graal-sync/conflicts/weaponBow.txt");
        var deletion = new SyncItemStatus("file:levels/a.nw", SyncItemKind.File, "levels/a.nw", "files/levels/a.nw", SyncItemState.PendingDeletion, null, null);
        window.Render(window.Shown with { Items = ImmutableArray.Create(conflict, deletion), Conflicts = 1 });
        TestUi.Find<TabControl>(window, "Pages").SelectedIndex = 1;
        TestUi.Settle();
        Assert.Equal(2, window.Items.Count);
        Assert.True(window.Items[0].IsConflict);
        Assert.Equal("Weapon: Bow", window.Items[0].Title);
        Assert.Contains("server copy", window.Items[0].Description);
        Assert.True(TestUi.Find<Border>(window, "PendingBar").IsVisible);
        Assert.Equal("Changes (2)", TestUi.Find<TabItem>(window, "ItemsTab").Header);
        Shots.Save(window, "sync-changes-dark");
        window.Items[0].KeepLocal.Execute(null);
        window.Items[0].TakeServer.Execute(null);
        TestUi.Settle();
        Assert.Equal(0, UiSafe.ReportedCount);

        Click(TestUi.Find<Button>(window, "ConfirmDeletionsButton"));
        RcDialog dialog = TestUi.FindDialog();
        Assert.Contains("File: levels/a.nw", dialog.Message);
        dialog.Accept();
        TestUi.Settle();
        window.Render(window.Shown with { Items = ImmutableArray<SyncItemStatus>.Empty, Conflicts = 0 });
        Assert.Empty(window.Items);
        Assert.Equal("Everything is in sync.", TestUi.Find<TextBlock>(window, "ItemsSummary").Text);
    }

    [AvaloniaFact]
    public async Task A_configuration_change_while_its_editor_is_open_is_told()
    {
        using Fixture f = WithSync();
        SyncWindow window = await OpenAsync(f);
        var editor = new Window();
        f.Context.Windows.ShowSingle(ToolIds.ServerOptions, () => editor);
        Assert.True(f.Files.IsEditorOpen(SyncConfigKind.ServerOptions));
        Assert.False(f.Files.IsEditorOpen(SyncConfigKind.ServerFlags));
        editor.Close();
        Assert.False(f.Files.IsEditorOpen(SyncConfigKind.ServerOptions));
        Assert.False(TestUi.Find<Border>(window, "NoticeBar").IsVisible);
    }

    [AvaloniaFact]
    public async Task A_reconnect_attaches_the_engine_to_the_new_connection()
    {
        using Fixture f = WithSync();
        SyncWindow window = await OpenAsync(f);
        f.Fake.RaiseDisconnected("lost");
        await TestUi.WaitUntilAsync(() => window.Shown.State == SyncRunState.Detached, what: "detached");
        var second = new FakeRcSession("tester");
        f.Context.Replace(second, f.Test.Login);
        await f.Files.Attached;
        await TestUi.WaitUntilAsync(() => window.Shown.State == SyncRunState.Stopped, what: "attached again");
        f.Context.End();
        Assert.Null(f.Files);
        Assert.True(window.IsClosed);
    }

    [AvaloniaFact]
    public async Task Turning_sync_off_closes_the_window()
    {
        using Fixture f = WithSync();
        SyncWindow window = await OpenAsync(f);
        f.Test.Environment.SetOptions(o => o with { SyncEnabled = false });
        Assert.True(window.IsClosed);
        Assert.Null(f.Context.Services.Get<ISyncStatus>()!.Percent);
    }

    [AvaloniaFact]
    public async Task Pictures_of_the_sync_window()
    {
        foreach (string theme in new[] { "light", "nord" })
        {
            using Fixture f = WithSync(theme);
            SyncWindow window = await OpenAsync(f);
            Shots.Save(window, "sync-setup-" + theme);
            TestUi.Find<TabControl>(window, "Pages").SelectedIndex = 2;
            Shots.Save(window, "sync-git-" + theme);
            TestUi.Find<TabControl>(window, "Pages").SelectedIndex = 4;
            Shots.Save(window, "sync-credentials-" + theme);
        }
    }
}
