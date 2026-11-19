using System.Collections.Concurrent;
using System.Text;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;

namespace RemoteControl.Sync.Tests;

/// <summary>A fake server for the backup: folder rules, listings by folder (as the server spells them) and file contents by path.</summary>
internal sealed class BackupServer
{
    public const long Time = 1_700_000_000;

    public BackupServer()
    {
        Session.FakeBackupFileBrowser.OnCall = Answer;
        Session.FakeBackupFileBrowser.DownloadHandler = path =>
        {
            Downloads.Enqueue(path);
            return Download?.Invoke(path) ?? Task.FromResult(Files.TryGetValue(path, out byte[]? content) ? content : null);
        };
    }

    public FakeRcSession Session { get; } = new("Staff");

    public List<FileBrowserFolder> Rules { get; } = new();

    public Dictionary<string, List<FileBrowserEntry>> Listings { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Downloads { get; } = new();

    public HashSet<string> Silent { get; } = new(StringComparer.Ordinal);

    public bool SendsFolders { get; set; } = true;

    /// <summary>Answers downloads instead of <see cref="Files"/> when set.</summary>
    public Func<string, Task<byte[]?>>? Download { get; set; }

    public void Add(string path, string content, string rights = "rw", long modified = Time)
    {
        int slash = path.LastIndexOf('/');
        string folder = path[..(slash + 1)];
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        Files[path] = bytes;
        if (!Listings.TryGetValue(folder, out List<FileBrowserEntry>? entries)) Listings[folder] = entries = new List<FileBrowserEntry>();
        entries.RemoveAll(entry => entry.Name == path[(slash + 1)..]);
        entries.Add(new FileBrowserEntry(path[(slash + 1)..], rights, bytes.Length, modified));
    }

    private void Answer(RecordedCall call)
    {
        switch (call.Method)
        {
            case "BackupFileBrowser.StartAsync":
                if (SendsFolders) Session.FakeBackupFileBrowser.RaiseFoldersReceived(Rules.ToArray());
                break;
            case "BackupFileBrowser.ChangeFolderAsync":
                string folder = call.Argument<string>(0);
                if (!Silent.Contains(folder)) Session.FakeBackupFileBrowser.RaiseFilesReceived(folder, Listings.TryGetValue(folder, out List<FileBrowserEntry>? entries) ? entries.ToArray() : []);
                break;
        }
    }
}

public sealed class ServerBackupTests
{
    private static readonly ServerBackupOptions Quick = new()
    {
        FolderListTimeout = TimeSpan.FromMilliseconds(300),
        ListingTimeout = TimeSpan.FromMilliseconds(300),
        Now = () => new DateTime(2026, 11, 19, 14, 30, 5),
    };

    private static BackupServer Server()
    {
        var server = new BackupServer();
        server.Rules.AddRange([new FileBrowserFolder("rw", "levels/*"), new FileBrowserFolder("r", "levels/*.nw"), new FileBrowserFolder("r", "images/*.png"), new FileBrowserFolder("rw", "config/serveroptions.txt")]);
        server.Add("levels/start.nw", "GLEVNW01 start");
        server.Add("levels/house.nw", "GLEVNW01 house");
        server.Add("images/logo.png", "png bytes");
        server.Add("config/serveroptions.txt", "name=Test");
        return server;
    }

    private static string Read(TempFolder folder, string path) => File.ReadAllText(Path.Combine(folder.Path, path));

    [Fact]
    public void The_folders_are_those_of_the_rules_without_root_wildcard_or_unsafe_ones()
    {
        IReadOnlyList<string> folders = ServerBackup.FoldersOf(
        [
            new FileBrowserFolder("rw", "levels/*"), new FileBrowserFolder("r", "levels/*.nw"), new FileBrowserFolder("r", "images/"),
            new FileBrowserFolder("rw", "*"), new FileBrowserFolder("rw", "start.nw"), new FileBrowserFolder("r", "levels/*/x.nw"),
            new FileBrowserFolder("r", "../secret/*"), new FileBrowserFolder("r", "/etc/*"), new FileBrowserFolder("r", "C:/x/*"), new FileBrowserFolder("r", "config\\*.txt"),
        ]);
        Assert.Equal(["config/", "images/", "levels/"], folders);
    }

    [Fact]
    public async Task Every_readable_file_is_saved_in_the_servers_folders_with_its_time()
    {
        using var folder = new TempFolder();
        BackupServer server = Server();
        var lines = new List<string>();
        var backup = new ServerBackup(server.Session, folder.Path, Quick);
        backup.LogLine += (_, e) => lines.Add(e.Text);

        ServerBackupResult result = await backup.RunAsync();

        Assert.Equal(ServerBackupOutcome.Completed, result.Outcome);
        Assert.Equal(4, result.Totals.Saved);
        Assert.Equal(0, result.Totals.Failed);
        Assert.Equal(3, result.Totals.FoldersDone);
        Assert.Equal("GLEVNW01 start", Read(folder, "levels/start.nw"));
        Assert.Equal("GLEVNW01 house", Read(folder, "levels/house.nw"));
        Assert.Equal("png bytes", Read(folder, "images/logo.png"));
        Assert.Equal("name=Test", Read(folder, "config/serveroptions.txt"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(BackupServer.Time).UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(folder.Path, "levels/start.nw")));
        Assert.Equal(["config/", "images/", "levels/"], server.Session.CallsTo("BackupFileBrowser.ChangeFolderAsync").Select(call => call.Argument<string>(0)));
        // Only the backup's own view was used: the windows and the synchroniser saw nothing.
        Assert.DoesNotContain(server.Session.Calls, call => call.Method.StartsWith("FileBrowser.", StringComparison.Ordinal) || call.Method.StartsWith("SyncFileBrowser.", StringComparison.Ordinal));
        Assert.Contains("Backup finished: 4 saved, 0 already up to date.", lines);
        Assert.Equal(result.Message, lines[^1]);
    }

    [Fact]
    public async Task A_second_run_fetches_only_what_changed_and_keeps_the_earlier_copy()
    {
        using var folder = new TempFolder();
        BackupServer server = Server();
        Assert.Equal(ServerBackupOutcome.Completed, (await new ServerBackup(server.Session, folder.Path, Quick).RunAsync()).Outcome);
        while (server.Downloads.TryDequeue(out _))
        {
        }

        server.Add("levels/house.nw", "GLEVNW01 house v2", modified: BackupServer.Time + 60);
        ServerBackupResult second = await new ServerBackup(server.Session, folder.Path, Quick).RunAsync();

        Assert.Equal(1, second.Totals.Saved);
        Assert.Equal(3, second.Totals.UpToDate);
        Assert.Equal(["levels/house.nw"], server.Downloads.ToArray());
        Assert.Equal("GLEVNW01 house v2", Read(folder, "levels/house.nw"));
        Assert.Equal("GLEVNW01 house", Read(folder, Path.Combine(ServerBackup.EarlierCopiesFolder, "2026-11-19 14-30-05", "levels", "house.nw")));
    }

    [Fact]
    public async Task A_file_with_the_same_bytes_but_another_time_is_left_and_gets_the_servers_time()
    {
        using var folder = new TempFolder();
        BackupServer server = Server();
        Directory.CreateDirectory(Path.Combine(folder.Path, "levels"));
        File.WriteAllText(Path.Combine(folder.Path, "levels/start.nw"), "GLEVNW01 start");

        ServerBackupResult result = await new ServerBackup(server.Session, folder.Path, Quick).RunAsync();

        Assert.Equal(3, result.Totals.Saved);
        Assert.Equal(1, result.Totals.UpToDate);
        Assert.False(Directory.Exists(Path.Combine(folder.Path, ServerBackup.EarlierCopiesFolder)));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(BackupServer.Time).UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(folder.Path, "levels/start.nw")));
    }

    [Fact]
    public async Task Unreadable_duplicate_empty_and_unsafe_entries_are_handled_without_asking_the_server_in_vain()
    {
        using var folder = new TempFolder();
        var server = new BackupServer();
        server.Rules.Add(new FileBrowserFolder("rw", "levels/*"));
        server.Add("levels/a.nw", "a");
        server.Add("levels/writeonly.txt", "w", rights: "w");
        server.Listings["levels/"].Add(new FileBrowserEntry("a.nw", "w", 1, BackupServer.Time));
        server.Listings["levels/"].Add(new FileBrowserEntry("empty.txt", "rw", 0, BackupServer.Time));
        server.Listings["levels/"].Add(new FileBrowserEntry("..", "rw", 5, BackupServer.Time));
        server.Listings["levels/"].Add(new FileBrowserEntry("bad:name.txt", "rw", 5, BackupServer.Time));
        var lines = new List<string>();
        var backup = new ServerBackup(server.Session, folder.Path, Quick);
        backup.LogLine += (_, e) => lines.Add(e.Text);

        ServerBackupResult result = await backup.RunAsync();

        Assert.Equal(["levels/a.nw"], server.Downloads.ToArray());
        Assert.Equal("a", Read(folder, "levels/a.nw"));
        Assert.Equal(0, new FileInfo(Path.Combine(folder.Path, "levels/empty.txt")).Length);
        Assert.Equal(2, result.Totals.Saved);
        Assert.Equal(1, result.Totals.Skipped);
        Assert.Equal(2, result.Totals.Failed);
        Assert.Contains(lines, line => line.Contains("may not be read", StringComparison.Ordinal));
        Assert.Equal(new[] { "a.nw", "empty.txt" }, Directory.GetFiles(Path.Combine(folder.Path, "levels")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Listed_subfolders_are_visited_and_a_silent_folder_is_left_out()
    {
        using var folder = new TempFolder();
        var server = new BackupServer();
        server.Rules.AddRange([new FileBrowserFolder("rw", "levels/*"), new FileBrowserFolder("r", "silent/*")]);
        server.Add("levels/a.nw", "a");
        server.Listings["levels/"].Add(new FileBrowserEntry("sub/", "rw", 0, 0));
        server.Add("levels/sub/b.nw", "b");
        server.Silent.Add("silent/");

        ServerBackupResult result = await new ServerBackup(server.Session, folder.Path, Quick).RunAsync();

        Assert.Equal(ServerBackupOutcome.Completed, result.Outcome);
        Assert.Equal("b", Read(folder, "levels/sub/b.nw"));
        Assert.Equal(3, result.Totals.FoldersDone);
    }

    [Fact]
    public async Task No_folder_list_ends_with_a_reason()
    {
        using var folder = new TempFolder();
        BackupServer server = Server();
        server.SendsFolders = false;

        ServerBackupResult result = await new ServerBackup(server.Session, folder.Path, Quick).RunAsync();

        Assert.Equal(ServerBackupOutcome.Failed, result.Outcome);
        Assert.Contains("no folder list", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Not_connected_does_nothing()
    {
        using var folder = new TempFolder();
        BackupServer server = Server();
        server.Session.IsConnected = false;

        ServerBackupResult result = await new ServerBackup(server.Session, folder.Path, Quick).RunAsync();

        Assert.Equal(ServerBackupOutcome.Failed, result.Outcome);
        Assert.Empty(server.Session.Calls);
    }

    [Fact]
    public async Task A_disconnect_ends_the_backup()
    {
        using var folder = new TempFolder();
        BackupServer server = Server();
        server.Download = path =>
        {
            if (path == "images/logo.png") server.Session.RaiseDisconnected("Server closed the connection");
            return Task.FromResult<byte[]?>(null);
        };

        ServerBackupResult result = await new ServerBackup(server.Session, folder.Path, Quick).RunAsync();

        Assert.Equal(ServerBackupOutcome.Disconnected, result.Outcome);
        Assert.Contains("Server closed the connection", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder.Path, "levels/start.nw")));
    }

    [Fact]
    public async Task Stopping_gives_up_the_file_on_its_way()
    {
        using var folder = new TempFolder();
        BackupServer server = Server();
        using var stop = new CancellationTokenSource();
        server.Download = _ =>
        {
            stop.Cancel();
            return new TaskCompletionSource<byte[]?>().Task;
        };

        ServerBackupResult result = await new ServerBackup(server.Session, folder.Path, Quick).RunAsync(stop.Token);

        Assert.Equal(ServerBackupOutcome.Cancelled, result.Outcome);
        Assert.Single(server.Downloads);
    }

    [Fact]
    public async Task A_refusal_message_ends_the_wait_for_that_file()
    {
        using var folder = new TempFolder();
        BackupServer server = Server();
        server.Download = path =>
        {
            if (path != "config/serveroptions.txt") return Task.FromResult(server.Files.TryGetValue(path, out byte[]? content) ? content : null);
            server.Session.RaiseFileBrowserMessage("Insufficient rights to download/view serveroptions.txt");
            return new TaskCompletionSource<byte[]?>().Task;
        };

        ServerBackupResult result = await new ServerBackup(server.Session, folder.Path, Quick).RunAsync();

        Assert.Equal(ServerBackupOutcome.Completed, result.Outcome);
        Assert.Equal(["config/serveroptions.txt"], result.FailedPaths);
        Assert.Equal(3, result.Totals.Saved);
    }

    [Fact]
    public async Task It_runs_once()
    {
        using var folder = new TempFolder();
        var backup = new ServerBackup(Server().Session, folder.Path, Quick);
        await backup.RunAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => backup.RunAsync());
    }
}
