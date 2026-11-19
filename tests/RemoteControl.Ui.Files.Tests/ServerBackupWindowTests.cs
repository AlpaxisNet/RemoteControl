using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Sync;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Files.Tests;

public sealed class ServerBackupWindowTests
{
    private const long Time = 1_700_000_000;

    /// <summary>Plays the server for the backup's view: two rule folders with a file each.</summary>
    private static void Serve(Fixture f, Func<string, Task<byte[]?>>? download = null)
    {
        FakeFileBrowser browser = f.Fake.FakeBackupFileBrowser;
        browser.RemoteFiles["levels/start.nw"] = Encoding.UTF8.GetBytes("GLEVNW01");
        browser.RemoteFiles["images/logo.png"] = Encoding.UTF8.GetBytes("png");
        if (download is not null) browser.DownloadHandler = download;
        browser.OnCall = call =>
        {
            if (call.Method == "BackupFileBrowser.StartAsync") browser.RaiseFoldersReceived([new FileBrowserFolder("rw", "levels/*"), new FileBrowserFolder("r", "images/*.png")]);
            else if (call.Method == "BackupFileBrowser.ChangeFolderAsync")
                browser.RaiseFilesReceived(call.Argument<string>(0), call.Argument<string>(0) == "levels/" ? new[] { new FileBrowserEntry("start.nw", "rw", 8, Time) } : new[] { new FileBrowserEntry("logo.png", "r", 3, Time) });
        };
    }

    private static ServerBackupWindow Open(Fixture f, string? folder = null)
    {
        var window = (ServerBackupWindow)f.Context.OpenTool(ToolIds.ServerBackup)!;
        window.BackupOptions = new ServerBackupOptions { FolderListTimeout = TimeSpan.FromSeconds(5), ListingTimeout = TimeSpan.FromSeconds(5) };
        if (folder is not null) TestUi.Find<TextBox>(window, "FolderBox").Text = folder;
        TestUi.Settle();
        return window;
    }

    private static string BackupFolder(Fixture f) => Path.Combine(f.Test.Environment.Root, "backup");

    [AvaloniaFact]
    public void It_opens_once_per_session_with_the_default_folder()
    {
        using var f = new Fixture();
        ServerBackupWindow window = Open(f);
        Assert.Equal("Server Backup - Test Server", window.Title);
        Assert.Equal(Path.Combine(f.DownloadRoot, "Test Server backup"), TestUi.Find<TextBox>(window, "FolderBox").Text);
        Assert.Same(window, f.Context.OpenTool(ToolIds.ServerBackup));
        Assert.Equal("0 saved · 0 up to date", TestUi.Find<TextBlock>(window, "CountsLabel").Text);
    }

    [AvaloniaFact]
    public async Task Start_downloads_every_file_into_the_servers_folders()
    {
        using var f = new Fixture();
        Serve(f);
        ServerBackupWindow window = Open(f, BackupFolder(f));
        TestUi.Click(TestUi.Find<Button>(window, "StartButton"));
        await TestUi.WaitUntilAsync(() => window.Running.IsCompleted && !window.IsRunning, what: "the backup");

        ServerBackupResult result = (await window.Running)!;
        Assert.Equal(ServerBackupOutcome.Completed, result.Outcome);
        Assert.Equal("GLEVNW01", File.ReadAllText(Path.Combine(BackupFolder(f), "levels", "start.nw")));
        Assert.Equal("png", File.ReadAllText(Path.Combine(BackupFolder(f), "images", "logo.png")));
        Assert.Equal(result.Message, TestUi.Find<TextBlock>(window, "PhaseLabel").Text);
        Assert.StartsWith("2 saved · 0 up to date", TestUi.Find<TextBlock>(window, "CountsLabel").Text, StringComparison.Ordinal);
        Assert.Contains("Saved levels/start.nw", window.LogLines);
        Assert.True(TestUi.Find<Button>(window, "StartButton").IsEnabled);
        Assert.False(TestUi.Find<Button>(window, "StopButton").IsEnabled);
        Assert.False(f.Fake.WasCalled("FileBrowser.StartAsync"));
        Shots.Save(window, "server-backup");

        TestUi.Click(TestUi.Find<Button>(window, "OpenFolderButton"));
        Assert.Contains("file " + BackupFolder(f), ExternalPrograms.Suppressed);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("relative/folder")]
    public void A_folder_that_is_no_full_path_is_refused(string folder)
    {
        using var f = new Fixture();
        Serve(f);
        ServerBackupWindow window = Open(f, folder);
        TestUi.Click(TestUi.Find<Button>(window, "StartButton"));
        TestUi.Settle();
        Assert.False(window.IsRunning);
        Assert.Contains(window.LogLines, line => line.StartsWith("Choose the folder for the backup first", StringComparison.Ordinal));
        Assert.Empty(f.Fake.CallsTo("BackupFileBrowser.StartAsync"));
    }

    [AvaloniaFact]
    public void Not_connected_or_while_files_sync_nothing_starts()
    {
        using var f = new Fixture();
        ServerBackupWindow window = Open(f, BackupFolder(f));
        var status = new SyncStatus { IsFileSyncActive = true };
        using (f.Context.Services.Provide<ISyncStatus>(status))
        {
            TestUi.Click(TestUi.Find<Button>(window, "StartButton"));
            TestUi.Settle();
            Assert.Contains("Files are being synchronised; stop the file sync in the Sync window first.", window.LogLines);
        }

        f.Fake.IsConnected = false;
        TestUi.Click(TestUi.Find<Button>(window, "StartButton"));
        TestUi.Settle();
        Assert.Contains("Not connected.", window.LogLines);
        Assert.False(f.Fake.WasCalled("BackupFileBrowser.StartAsync"));
    }

    [AvaloniaFact]
    public async Task Stop_ends_the_backup()
    {
        using var f = new Fixture();
        Serve(f, _ => new TaskCompletionSource<byte[]?>().Task);
        ServerBackupWindow window = Open(f, BackupFolder(f));
        TestUi.Click(TestUi.Find<Button>(window, "StartButton"));
        await TestUi.WaitUntilAsync(() => f.Fake.WasCalled("BackupFileBrowser.DownloadAsync"), what: "the first download");
        Assert.True(window.IsRunning);
        Assert.False(TestUi.Find<Button>(window, "StartButton").IsEnabled);
        Assert.True(TestUi.Find<TextBox>(window, "FolderBox").IsReadOnly);

        TestUi.Click(TestUi.Find<Button>(window, "StopButton"));
        await TestUi.WaitUntilAsync(() => !window.IsRunning, what: "the stop");
        Assert.Equal(ServerBackupOutcome.Cancelled, (await window.Running)!.Outcome);
        Assert.Equal("The backup was stopped.", TestUi.Find<TextBlock>(window, "PhaseLabel").Text);
    }

    [AvaloniaFact]
    public async Task Closing_while_it_runs_asks_and_then_stops_it()
    {
        using var f = new Fixture();
        Serve(f, _ => new TaskCompletionSource<byte[]?>().Task);
        ServerBackupWindow window = Open(f, BackupFolder(f));
        TestUi.Click(TestUi.Find<Button>(window, "StartButton"));
        await TestUi.WaitUntilAsync(() => f.Fake.WasCalled("BackupFileBrowser.DownloadAsync"), what: "the first download");

        TestUi.Click(TestUi.Find<Button>(window, "CloseButton"));
        RcDialog question = TestUi.FindDialog();
        Assert.Contains("A backup is running", question.Message, StringComparison.Ordinal);
        question.Cancel();
        TestUi.Settle();
        Assert.True(window.IsRunning);
        Assert.False(window.IsClosed);

        TestUi.Click(TestUi.Find<Button>(window, "CloseButton"));
        TestUi.FindDialog().Accept();
        await TestUi.WaitUntilAsync(() => window.IsClosed, what: "the window to close");
        Assert.Equal(ServerBackupOutcome.Cancelled, (await window.Running)!.Outcome);
    }

    [AvaloniaFact]
    public void The_files_menu_of_the_main_window_names_it()
    {
        string xaml = File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot, "src", "RemoteControl.Ui.Session", "MainWindow.axaml"));
        Assert.Contains("<MenuItem Name=\"MenuServerBackup\" Header=\"Server Backup\" />", xaml, StringComparison.Ordinal);
    }

    private sealed class SyncStatus : ISyncStatus
    {
        public int? Percent => null;

        public bool IsFileSyncActive { get; set; }

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
