using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Sync.ImportMissing;

namespace RemoteControl.Sync.Tests;

/// <summary>Import Missing: the local search, the choice, the plan and the upload (the specification's sections 3 and 5).</summary>
public sealed class ImportMissingTests
{
    private static string Write(string root, string relative, int size = 3)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private static MissingFile Missing(string name, MissingFileKind kind = MissingFileKind.Image, string folder = "world/images/") => new(name, kind, folder, 1, "level a.nw");

    private static readonly FileBrowserFolder[] Rights = { new("rw", "world/images/*"), new("rw", "world/heads/head*.png"), new("r", "world/ganis/*") };

    // ---- The local search --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_index_finds_files_by_name_without_regard_to_case_in_every_folder_below()
    {
        using var temp = new TempFolder();
        string a = Write(temp.Path, "x/Door.GIF");
        string b = Write(temp.Path, "y/z/door.gif");
        Write(temp.Path, "y/other.gif");
        LocalFileIndex index = await LocalFileIndex.BuildAsync(temp.Path);
        Assert.Equal(new[] { a, b }.Order(), index.Find("DOOR.gif").Order());
        Assert.Empty(index.Find("missing.gif"));
        Assert.Equal(3, index.Count);
        Assert.Equal(3, index.FilesSeen);
        Assert.Equal(0, index.FoldersSkipped);
    }

    [Fact]
    public async Task The_index_can_be_limited_to_wanted_names_and_then_holds_only_those()
    {
        using var temp = new TempFolder();
        for (int number = 0; number < 300; number++) Write(temp.Path, $"f{number % 7}/file{number}.png");
        LocalFileIndex index = await LocalFileIndex.BuildAsync(temp.Path, new HashSet<string> { "FILE5.png", "file299.png" });
        Assert.Equal(2, index.Count);
        Assert.Equal(300, index.FilesSeen);
        Assert.Single(index.Find("file5.png"));
    }

    [Fact]
    public async Task The_index_follows_no_link_to_a_folder()
    {
        using var temp = new TempFolder();
        using var outside = new TempFolder();
        Write(outside.Path, "secret.png");
        Write(temp.Path, "real/a.png");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(temp.Path, "link"), outside.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        LocalFileIndex index = await LocalFileIndex.BuildAsync(temp.Path);
        Assert.Empty(index.Find("secret.png"));
        Assert.Single(index.Find("a.png"));
        Assert.Equal(0, index.FoldersSkipped);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_read_is_skipped_and_counted()
    {
        using var temp = new TempFolder();
        Write(temp.Path, "ok/a.png");
        string locked = Path.GetDirectoryName(Write(temp.Path, "locked/b.png"))!;
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            _ = Directory.EnumerateFileSystemEntries(locked).ToList();
            // A superuser reads it anyway: then there is nothing to skip.
        }
        catch (UnauthorizedAccessException)
        {
            LocalFileIndex index = await LocalFileIndex.BuildAsync(temp.Path);
            Assert.Equal(1, index.FoldersSkipped);
            Assert.Single(index.Find("a.png"));
            Assert.Empty(index.Find("b.png"));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task A_root_that_is_not_a_folder_gives_an_empty_index_with_one_skipped_folder()
    {
        using var temp = new TempFolder();
        LocalFileIndex index = await LocalFileIndex.BuildAsync(Path.Combine(temp.Path, "nothing"));
        Assert.Equal(0, index.Count);
        Assert.Equal(1, index.FoldersSkipped);
    }

    [Fact]
    public async Task The_search_can_be_cancelled_before_and_during()
    {
        using var temp = new TempFolder();
        for (int number = 0; number < 20; number++) Write(temp.Path, $"d{number}/a.png");
        using var before = new CancellationTokenSource();
        before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalFileIndex.BuildAsync(temp.Path, null, null, before.Token));

        using var during = new CancellationTokenSource();
        int reports = 0;
        var progress = new SyncProgress<LocalScanProgress>(_ =>
        {
            if (++reports == 3) during.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalFileIndex.BuildAsync(temp.Path, null, progress, during.Token));
        Assert.Equal(3, reports);
    }

    // ---- The choice --------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_path_with_a_folder_named_like_the_kind_wins_then_the_shortest_then_the_ordinal_order()
    {
        string root = Path.Combine(Path.GetTempPath(), "ugd");
        string P(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        LocalChoice choice = LocalFileChooser.Choose(MissingFileKind.Head, root, new[] { P("a/h.png"), P("old/Heads/h.png"), P("zzzzzzzz/heads/h.png"), P("b.png") })!;
        Assert.Equal(P("old/Heads/h.png"), choice.Path);
        Assert.Equal(new[] { P("zzzzzzzz/heads/h.png"), P("b.png"), P("a/h.png") }, choice.Alternatives);

        LocalChoice tie = LocalFileChooser.Choose(MissingFileKind.Other, root, new[] { P("b/x.png"), P("a/x.png"), P("c/dd/x.png") })!;
        Assert.Equal(P("a/x.png"), tie.Path);
        Assert.Equal(new[] { P("b/x.png"), P("c/dd/x.png") }, tie.Alternatives);
        Assert.Null(LocalFileChooser.Choose(MissingFileKind.Head, root, Array.Empty<string>()));
    }

    [Fact]
    public void Only_the_folders_below_the_chosen_root_count_for_the_kind()
    {
        string root = Path.Combine(Path.GetTempPath(), "images");
        string deep = Path.Combine(root, "sub", "pics", "a.png");
        string shallow = Path.Combine(root, "z.png");
        // The root's own name is no folder of the kind: the shortest path wins.
        Assert.Equal(shallow, LocalFileChooser.Choose(MissingFileKind.Image, root, new[] { deep, shallow })!.Path);
    }

    // ---- The plan ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_row_is_ticked_or_says_why_not()
    {
        using var temp = new TempFolder();
        Write(temp.Path, "images/ok.png", 5);
        Write(temp.Path, "empty.png", 0);
        Write(temp.Path, "big.png", 50);
        Write(temp.Path, "noright.dat", 5);
        Write(temp.Path, "nofolder.png", 5);
        Write(temp.Path, "ganis/walk.gani", 5);
        LocalFileIndex index = await LocalFileIndex.BuildAsync(temp.Path);
        MissingFile[] missing =
        {
            Missing("ok.png"),
            Missing("empty.png"),
            Missing("big.png"),
            Missing("absent.png"),
            Missing("noright.dat", MissingFileKind.Other, "world/"),
            Missing("nofolder.png", MissingFileKind.Image, string.Empty),
            Missing("walk.gani", MissingFileKind.Gani, "world/ganis/"),
            Missing("OK.PNG", MissingFileKind.Image, "WORLD/IMAGES"),
            Missing("../evil.png"),
            Missing("evil.png", MissingFileKind.Image, "../world/"),
            Missing("evil2.png", MissingFileKind.Image, "/etc/"),
            Missing("evil3.png", MissingFileKind.Image, "C:/x/"),
        };
        ImportPlan plan = ImportPlanner.Plan(missing, index, Rights, maxUploadSize: 20);
        Assert.Equal(
            new[]
            {
                ImportProblem.None, ImportProblem.Empty, ImportProblem.TooLarge, ImportProblem.NotFound, ImportProblem.NoRight, ImportProblem.NoServerFolder,
                ImportProblem.NoRight, ImportProblem.None, ImportProblem.UnsafeServerPath, ImportProblem.UnsafeServerPath, ImportProblem.UnsafeServerPath, ImportProblem.UnsafeServerPath,
            },
            plan.Rows.Select(row => row.Problem).ToArray());
        Assert.All(plan.Rows, row => Assert.Equal(row.Problem == ImportProblem.None, row.Reason.Length == 0));
        Assert.Equal("not found in the folder", plan.Rows[3].Reason);
        Assert.Equal("the file is empty", plan.Rows[1].Reason);
        Assert.StartsWith("no folder on the server", plan.Rows[5].Reason, StringComparison.Ordinal);
        Assert.Equal("larger than the server's limit", plan.Rows[2].Reason);
        Assert.Equal("no folder right with w for it", plan.Rows[4].Reason);
        Assert.Equal((2, 10L), ImportPlan.Summarize(plan.TickedByDefault));
        Assert.Equal("world/images/ok.png", plan.Rows[0].ServerPath);
    }

    [Fact]
    public async Task Picking_an_alternative_evaluates_the_row_again()
    {
        using var temp = new TempFolder();
        string small = Write(temp.Path, "a/x.png", 4);
        string bigger = Write(temp.Path, "bb/x.png", 400);
        LocalFileIndex index = await LocalFileIndex.BuildAsync(temp.Path);
        ImportRow row = Assert.Single(ImportPlanner.Plan(new[] { Missing("x.png") }, index, Rights, 100).Rows);
        Assert.Equal(small, row.LocalPath);
        Assert.Equal(new[] { bigger }, row.Alternatives);
        ImportRow other = ImportPlanner.WithLocalFile(row, bigger, Rights, 100);
        Assert.Equal(ImportProblem.TooLarge, other.Problem);
        Assert.Equal(new[] { small }, other.Alternatives);
    }

    // ---- The request and the rights ----------------------------------------------------------------------------------------

    private static MissingFilesImporterOptions Quick() => new() { RequestTimeout = TimeSpan.FromMilliseconds(100), UploadAnswerTimeout = TimeSpan.FromMilliseconds(100), RightsTimeout = TimeSpan.FromMilliseconds(100) };

    [Fact]
    public async Task The_request_returns_the_list_the_error_or_a_time_out()
    {
        using var session = new FakeRcSession();
        var importer = new MissingFilesImporter(session, Quick());
        session.OnCall = call =>
        {
            if (call.Method == nameof(IRcSession.RequestMissingFilesAsync)) session.RaiseMissingFiles(new[] { Missing("a.png") });
        };
        MissingFilesAnswer list = await importer.RequestAsync();
        Assert.Equal("a.png", Assert.Single(list.Files).Name);
        Assert.False(list.TimedOut);

        session.OnCall = call =>
        {
            if (call.Method == nameof(IRcSession.RequestMissingFilesAsync)) session.RaiseMissingFilesError("No right.");
        };
        MissingFilesAnswer refused = await importer.RequestAsync();
        Assert.Equal("No right.", refused.Error);

        session.OnCall = null;
        MissingFilesAnswer silent = await importer.RequestAsync();
        Assert.True(silent.TimedOut);
        Assert.Null(silent.Error);

        session.SendResult = false;
        Assert.NotNull((await importer.RequestAsync()).Error);
    }

    [Fact]
    public async Task The_rights_come_from_the_backup_view_and_leave_the_other_views_alone()
    {
        using var session = new FakeRcSession();
        var importer = new MissingFilesImporter(session, Quick());
        session.FakeBackupFileBrowser.OnCall = call =>
        {
            if (call.Method.EndsWith("StartAsync", StringComparison.Ordinal)) session.FakeBackupFileBrowser.RaiseFoldersReceived(Rights);
        };
        IReadOnlyList<FileBrowserFolder> rights = await importer.LoadRightsAsync();
        Assert.Equal(Rights, rights);
        Assert.DoesNotContain(session.Calls, call => call.Method.StartsWith("FileBrowser.", StringComparison.Ordinal) || call.Method.StartsWith("SyncFileBrowser.", StringComparison.Ordinal));

        session.FakeBackupFileBrowser.OnCall = null;
        Assert.Empty(await importer.LoadRightsAsync());
    }

    // ---- The upload --------------------------------------------------------------------------------------------------------

    private static (FakeRcSession Session, MissingFilesImporter Importer) Server()
    {
        var session = new FakeRcSession();
        session.FakeBackupFileBrowser.ServerAnswersUploads = true;
        session.FakeBackupFileBrowser.FolderList.AddRange(Rights);
        return (session, new MissingFilesImporter(session, Quick()));
    }

    private static ImportRow Row(string name, string? local, string folder = "world/images/", MissingFileKind kind = MissingFileKind.Image, long size = 3) =>
        new(Missing(name, kind, folder), local, size, Array.Empty<string>(), ImportProblem.None);

    [Fact]
    public async Task Rows_are_uploaded_one_after_the_other_into_their_folders_with_a_result_each()
    {
        using var temp = new TempFolder();
        string a = Write(temp.Path, "a.png", 3);
        string big = Write(temp.Path, "sub/big.png", 40000);
        string head = Write(temp.Path, "head1.png", 3);
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        {
            var seen = new List<ImportProgress>();
            ImportRun run = await importer.UploadAsync(
                new[] { Row("a.png", a), Row("big.png", big, size: 40000), Row("head1.png", head, "world/heads/", MissingFileKind.Head), Row("nope.png", head, "world/other/") },
                temp.Path, new SyncProgress<ImportProgress>(seen.Add));
            Assert.False(run.Cancelled);
            Assert.Equal(new[] { ImportOutcome.Uploaded, ImportOutcome.Uploaded, ImportOutcome.Uploaded, ImportOutcome.Refused }, run.Results.Select(result => result.Outcome).ToArray());
            Assert.Equal("uploaded", run.Results[0].Message);
            Assert.Equal("not in your folder rights", run.Results[3].Message);
            Assert.Equal(
                new[] { "world/images/a.png", "world/images/big.png", "world/heads/head1.png", "world/other/nope.png" },
                session.CallsTo("BackupFileBrowser.UploadAsync").Select(call => call.Argument<string>(0)).ToArray());
            Assert.DoesNotContain(session.Calls, call => call.Method.StartsWith("FileBrowser.", StringComparison.Ordinal) || call.Method.StartsWith("SyncFileBrowser.", StringComparison.Ordinal));
            Assert.Equal(40000, session.FakeBackupFileBrowser.RemoteFiles["world/images/big.png"].Length);
            Assert.Equal(new[] { 0, 1, 2, 3 }, seen.Where(item => item.Result is not null).Select(item => item.Index).ToArray());
            Assert.All(seen, item => Assert.Equal(4, item.Count));
        }
    }

    [Fact]
    public async Task A_row_the_server_never_answers_ends_as_no_answer_and_the_run_goes_on()
    {
        using var temp = new TempFolder();
        string a = Write(temp.Path, "a.png");
        string b = Write(temp.Path, "b.png");
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        {
            session.FakeBackupFileBrowser.UploadAnswer = (path, _) => path.EndsWith("a.png", StringComparison.Ordinal) ? null : "\0";
            ImportRun run = await importer.UploadAsync(new[] { Row("a.png", a), Row("b.png", b) }, temp.Path);
            Assert.Equal(ImportOutcome.NoAnswer, run.Results[0].Outcome);
            Assert.Equal("the server did not answer", run.Results[0].Message);
            Assert.Equal(ImportOutcome.Uploaded, run.Results[1].Outcome);
        }
    }

    [Fact]
    public async Task An_answer_about_another_file_does_not_end_the_wait()
    {
        using var temp = new TempFolder();
        string a = Write(temp.Path, "a.png");
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        {
            session.FakeBackupFileBrowser.UploadAnswer = (_, _) =>
            {
                session.RaiseFileBrowserMessage("Uploaded file b.png");
                session.RaiseFileBrowserMessage("Could not upload world/images/b.png: a file of that name exists");
                session.RaiseFileBrowserMessage("Folder world/images/ is not in your folder rights");
                return "Could not upload world/images/a.png: the disk refused it";
            };
            ImportRun run = await importer.UploadAsync(new[] { Row("a.png", a) }, temp.Path);
            Assert.Equal(ImportOutcome.Refused, run.Results[0].Outcome);
            Assert.Equal("the disk refused it", run.Results[0].Message);
        }
    }

    [Fact]
    public async Task Cancelling_stops_between_files()
    {
        using var temp = new TempFolder();
        string a = Write(temp.Path, "a.png");
        string b = Write(temp.Path, "b.png");
        string c = Write(temp.Path, "c.png");
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        using (var cancel = new CancellationTokenSource())
        {
            var progress = new SyncProgress<ImportProgress>(item =>
            {
                if (item.Result is not null && item.Index == 0) cancel.Cancel();
            });
            ImportRun run = await importer.UploadAsync(new[] { Row("a.png", a), Row("b.png", b), Row("c.png", c) }, temp.Path, progress, cancel.Token);
            Assert.True(run.Cancelled);
            ImportResult only = Assert.Single(run.Results);
            Assert.Equal(ImportOutcome.Uploaded, only.Outcome);
            Assert.Single(session.CallsTo("BackupFileBrowser.UploadAsync"));
        }
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_the_server_ends_the_run_with_a_cancelled_row()
    {
        using var temp = new TempFolder();
        string a = Write(temp.Path, "a.png");
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        using (var cancel = new CancellationTokenSource())
        {
            var slow = new MissingFilesImporter(session, new MissingFilesImporterOptions { UploadAnswerTimeout = TimeSpan.FromSeconds(30) });
            session.FakeBackupFileBrowser.UploadAnswer = (_, _) =>
            {
                cancel.Cancel();
                return null;
            };
            ImportRun run = await slow.UploadAsync(new[] { Row("a.png", a), Row("a.png", a) }, temp.Path, null, cancel.Token);
            Assert.True(run.Cancelled);
            Assert.Equal(ImportOutcome.Cancelled, Assert.Single(run.Results).Outcome);
        }
    }

    [Fact]
    public async Task A_folder_the_server_sends_is_used_only_when_it_is_a_plain_relative_path()
    {
        using var temp = new TempFolder();
        string a = Write(temp.Path, "a.png");
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        {
            ImportRun run = await importer.UploadAsync(
                new[] { Row("a.png", a, "../x/"), Row("a.png", a, "/abs/"), Row("a.png", a, "C:/x/"), Row("a.png", a, "w\\x/"), Row("a.png", a, "world/../x/"), Row("a.png", a, ""), Row("..", a), Row("d/a.png", a), Row("a:b.png", a) },
                temp.Path);
            Assert.All(run.Results, result => Assert.Equal(ImportOutcome.Failed, result.Outcome));
            Assert.Equal(9, run.Results.Count);
            Assert.Empty(session.CallsTo("BackupFileBrowser.UploadAsync"));
        }
    }

    [Fact]
    public async Task A_local_file_is_read_only_under_the_chosen_folder()
    {
        using var temp = new TempFolder();
        using var outside = new TempFolder();
        string inside = Write(temp.Path, "in.png");
        string foreign = Write(outside.Path, "out.png");
        string sibling = temp.Path + "-sibling";
        Directory.CreateDirectory(sibling);
        string lookalike = Write(sibling, "x.png");
        string traversal = Path.Combine(temp.Path, "..", Path.GetFileName(outside.Path), "out.png");
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        try
        {
            using (session)
            {
                var rows = new List<ImportRow> { Row("out.png", foreign), Row("x.png", lookalike), Row("out.png", traversal), Row("in.png", inside) };
                string link = Path.Combine(temp.Path, "linked.png");
                bool linked = false;
                try
                {
                    File.CreateSymbolicLink(link, foreign);
                    linked = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                }

                if (linked) rows.Add(Row("linked.png", link));
                ImportRun run = await importer.UploadAsync(rows, temp.Path);
                Assert.Equal(ImportOutcome.Failed, run.Results[0].Outcome);
                Assert.Equal(ImportOutcome.Failed, run.Results[1].Outcome);
                Assert.Equal(ImportOutcome.Failed, run.Results[2].Outcome);
                Assert.Equal(ImportOutcome.Uploaded, run.Results[3].Outcome);
                if (linked) Assert.Equal(ImportOutcome.Failed, run.Results[4].Outcome);
                Assert.Equal(new[] { "world/images/in.png" }, session.CallsTo("BackupFileBrowser.UploadAsync").Select(call => call.Argument<string>(0)).ToArray());
            }
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public async Task A_file_that_is_gone_empty_or_over_the_limit_when_it_is_read_is_not_sent()
    {
        using var temp = new TempFolder();
        string gone = Path.Combine(temp.Path, "gone.png");
        string empty = Write(temp.Path, "empty.png", 0);
        string big = Write(temp.Path, "big.png", 50);
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        {
            session.MaxUploadFileSize = 10;
            ImportRun run = await importer.UploadAsync(new[] { Row("gone.png", gone), Row("empty.png", empty), Row("big.png", big), Row("none.png", null) }, temp.Path);
            Assert.All(run.Results, result => Assert.Equal(ImportOutcome.Failed, result.Outcome));
            Assert.Empty(session.CallsTo("BackupFileBrowser.UploadAsync"));
        }
    }

    [Fact]
    public async Task An_upload_that_could_not_be_sent_is_a_failed_row_and_the_handlers_are_removed()
    {
        using var temp = new TempFolder();
        string a = Write(temp.Path, "a.png");
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        {
            session.SendResult = false;
            ImportRun run = await importer.UploadAsync(new[] { Row("a.png", a) }, temp.Path);
            Assert.Equal(ImportOutcome.Failed, Assert.Single(run.Results).Outcome);
        }
    }
}

/// <summary>An <see cref="IProgress{T}"/> that calls its handler at once on the reporting thread (the test contexts have no UI thread).</summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
