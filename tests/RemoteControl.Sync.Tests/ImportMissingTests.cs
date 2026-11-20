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
            Assert.Equal("Folder world/other/ is not in your folder rights", run.Results[3].Message);
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
                session.RaiseFileBrowserMessage("Folder world/heads/ is not in your folder rights");
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

    // ---- The request's waits (section 5): searching, one at a time, a lost connection ---------------------------------------------

    private static async Task<bool> StaysOpenAsync(Task task, int milliseconds = 300)
    {
        await Task.WhenAny(task, Task.Delay(milliseconds));
        return !task.IsCompleted;
    }

    [Fact]
    public async Task After_searching_the_request_waits_far_longer_than_the_time_for_a_first_answer_and_then_returns_the_result()
    {
        using var session = new FakeRcSession();
        var importer = new MissingFilesImporter(session, new MissingFilesImporterOptions { RequestTimeout = TimeSpan.FromMilliseconds(40) });
        session.OnCall = call =>
        {
            if (call.Method == nameof(IRcSession.RequestMissingFilesAsync)) session.RaiseMissingFilesSearching();
        };
        var told = new List<MissingFilesProgressEventArgs>();
        Task<MissingFilesAnswer> request = importer.RequestAsync(default, told.Add);
        Assert.True(await StaysOpenAsync(request));
        Assert.True(Assert.Single(told).Searching);
        session.RaiseMissingFiles(new[] { Missing("a.png") });
        MissingFilesAnswer answer = await request;
        Assert.False(answer.TimedOut);
        Assert.False(answer.ConnectionLost);
        Assert.Equal("a.png", Assert.Single(answer.Files).Name);
    }

    [Fact]
    public async Task A_part_of_the_result_is_a_first_answer_too_and_an_error_after_searching_ends_the_wait()
    {
        using var session = new FakeRcSession();
        var importer = new MissingFilesImporter(session, new MissingFilesImporterOptions { RequestTimeout = TimeSpan.FromMilliseconds(40) });
        session.OnCall = call =>
        {
            if (call.Method == nameof(IRcSession.RequestMissingFilesAsync)) session.RaiseMissingFilesPart(1, 2);
        };
        Task<MissingFilesAnswer> request = importer.RequestAsync();
        Assert.True(await StaysOpenAsync(request));
        session.RaiseMissingFilesError("The search failed.");
        Assert.Equal("The search failed.", (await request).Error);
    }

    [Fact]
    public async Task A_server_that_answers_nothing_at_all_is_one_that_does_not_know_the_request()
    {
        using var session = new FakeRcSession();
        var importer = new MissingFilesImporter(session, new MissingFilesImporterOptions { RequestTimeout = TimeSpan.FromMilliseconds(40) });
        MissingFilesAnswer answer = await importer.RequestAsync();
        Assert.True(answer.TimedOut);
        Assert.False(answer.ConnectionLost);
        // The request ended: the next one is sent again.
        await importer.RequestAsync();
        Assert.Equal(2, session.CallsTo(nameof(IRcSession.RequestMissingFilesAsync)).Count());
    }

    [Fact]
    public async Task A_lost_connection_ends_the_wait_with_words_that_are_not_those_of_an_old_server()
    {
        using var session = new FakeRcSession();
        var importer = new MissingFilesImporter(session, new MissingFilesImporterOptions { RequestTimeout = TimeSpan.FromMilliseconds(40) });
        session.OnCall = call =>
        {
            if (call.Method == nameof(IRcSession.RequestMissingFilesAsync)) session.RaiseMissingFilesSearching();
        };
        Task<MissingFilesAnswer> request = importer.RequestAsync();
        Assert.True(await StaysOpenAsync(request));
        session.RaiseDisconnected();
        MissingFilesAnswer answer = await request;
        Assert.True(answer.ConnectionLost);
        Assert.False(answer.TimedOut);
        Assert.Empty(answer.Files);
    }

    [Fact]
    public async Task A_replaced_session_ends_the_wait_as_a_lost_connection_and_a_closed_one_is_not_asked()
    {
        using var session = new FakeRcSession();
        using var replaced = new CancellationTokenSource();
        var importer = new MissingFilesImporter(session, new MissingFilesImporterOptions { RequestTimeout = TimeSpan.FromMilliseconds(40) }, replaced.Token);
        session.OnCall = call =>
        {
            if (call.Method == nameof(IRcSession.RequestMissingFilesAsync)) session.RaiseMissingFilesSearching();
        };
        Task<MissingFilesAnswer> request = importer.RequestAsync();
        Assert.True(await StaysOpenAsync(request));
        replaced.Cancel();
        Assert.True((await request).ConnectionLost);

        using var closed = new FakeRcSession { IsConnected = false };
        MissingFilesAnswer none = await new MissingFilesImporter(closed, Quick()).RequestAsync();
        Assert.True(none.ConnectionLost);
        Assert.Empty(closed.CallsTo(nameof(IRcSession.RequestMissingFilesAsync)));
    }

    [Fact]
    public async Task Cancelling_ends_the_wait_after_searching_and_the_request_can_be_joined_again()
    {
        using var session = new FakeRcSession();
        var importer = new MissingFilesImporter(session, new MissingFilesImporterOptions { RequestTimeout = TimeSpan.FromMilliseconds(40) });
        session.OnCall = call =>
        {
            if (call.Method == nameof(IRcSession.RequestMissingFilesAsync)) session.RaiseMissingFilesSearching();
        };
        using var cancel = new CancellationTokenSource();
        Task<MissingFilesAnswer> first = importer.RequestAsync(cancel.Token);
        Assert.True(await StaysOpenAsync(first));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        // The search still runs on the server: a new call joins the request, is told it is searching at once and gets its result.
        var told = new List<MissingFilesProgressEventArgs>();
        Task<MissingFilesAnswer> second = importer.RequestAsync(default, told.Add);
        Assert.True(Assert.Single(told).Searching);
        session.RaiseMissingFiles(new[] { Missing("a.png") });
        Assert.Single((await second).Files);
        Assert.Single(session.CallsTo(nameof(IRcSession.RequestMissingFilesAsync)));
    }

    [Fact]
    public async Task A_second_request_while_one_is_outstanding_is_not_sent_and_gets_the_same_result()
    {
        using var session = new FakeRcSession();
        var importer = new MissingFilesImporter(session, new MissingFilesImporterOptions { RequestTimeout = TimeSpan.FromMilliseconds(40) });
        var other = new MissingFilesImporter(session, new MissingFilesImporterOptions { RequestTimeout = TimeSpan.FromMilliseconds(40) });
        session.OnCall = call =>
        {
            if (call.Method == nameof(IRcSession.RequestMissingFilesAsync)) session.RaiseMissingFilesSearching();
        };
        Task<MissingFilesAnswer> first = importer.RequestAsync();
        Task<MissingFilesAnswer> second = other.RequestAsync();
        Assert.True(await StaysOpenAsync(Task.WhenAll(first, second)));
        session.RaiseMissingFiles(new[] { Missing("a.png"), Missing("b.png") });
        Assert.Equal(2, (await first).Files.Count);
        Assert.Equal(2, (await second).Files.Count);
        Assert.Single(session.CallsTo(nameof(IRcSession.RequestMissingFilesAsync)));
    }

    // ---- Folder rights, the limit, the upload's answers ---------------------------------------------------------------------------

    [Fact]
    public async Task A_right_that_ends_in_a_slash_covers_every_file_of_its_folder_as_on_the_server()
    {
        using var temp = new TempFolder();
        Write(temp.Path, "head1.png", 5);
        LocalFileIndex index = await LocalFileIndex.BuildAsync(temp.Path);
        var rights = new[] { new FileBrowserFolder("rw", "world/heads/") };
        ImportPlan plan = ImportPlanner.Plan(new[] { Missing("head1.png", MissingFileKind.Head, "world/heads/") }, index, rights, 20);
        Assert.Equal(ImportProblem.None, plan.Rows[0].Problem);
        Assert.True(rights[0].Covers("world/heads/", "anything.png"));
        Assert.False(rights[0].Covers("world/images/", "anything.png"));
        Assert.False(new FileBrowserFolder("rw", "world/*").Covers("world/heads/", "a.png"));
    }

    [Fact]
    public void The_fake_server_judges_by_the_servers_rule_so_that_a_difference_from_the_clients_method_shows()
    {
        var slash = new FileBrowserFolder("rw", "world/heads/");
        Assert.True(FakeFileBrowser.ServerRuleAllowsWrite(slash, "world/heads/", "x.png"));
        Assert.False(FakeFileBrowser.ServerRuleAllowsWrite(new FileBrowserFolder("r", "world/heads/"), "world/heads/", "x.png"));
        Assert.False(FakeFileBrowser.ServerRuleAllowsWrite(new FileBrowserFolder("rw", "*"), "world/heads/", "x.png"));
        Assert.True(FakeFileBrowser.ServerRuleAllowsWrite(new FileBrowserFolder("rw", "world/heads/h*.png"), "world/heads/head1.png", "head1.png") == false);
        // The client's method compares the folder without regard to case; the server's rule does not.
        var lower = new FileBrowserFolder("rw", "world/heads/*");
        Assert.True(lower.Covers("World/Heads/", "x.png"));
        Assert.False(FakeFileBrowser.ServerRuleAllowsWrite(lower, "World/Heads/", "x.png"));
    }

    [Fact]
    public async Task A_limit_of_zero_means_the_server_named_none_and_nothing_is_checked_against_it()
    {
        using var temp = new TempFolder();
        string big = Write(temp.Path, "big.png", 5000);
        LocalFileIndex index = await LocalFileIndex.BuildAsync(temp.Path);
        ImportPlan plan = ImportPlanner.Plan(new[] { Missing("big.png") }, index, Rights, maxUploadSize: 0);
        Assert.Equal(ImportProblem.None, plan.Rows[0].Problem);
        Assert.Equal(ImportProblem.TooLarge, ImportPlanner.Plan(new[] { Missing("big.png") }, index, Rights, maxUploadSize: 100).Rows[0].Problem);

        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        {
            session.MaxUploadFileSize = 0;
            Assert.Equal(0, importer.MaxUploadSize);
            ImportRun run = await importer.UploadAsync(plan.Rows, temp.Path);
            Assert.Equal(ImportOutcome.Uploaded, Assert.Single(run.Results).Outcome);
            Assert.Equal(5000, session.FakeBackupFileBrowser.RemoteFiles["world/images/big.png"].Length);
            Assert.NotNull(big);
        }
    }

    [Fact]
    public async Task A_file_over_what_the_program_sends_is_refused_before_it_is_read()
    {
        using var temp = new TempFolder();
        string path = Path.Combine(temp.Path, "huge.png");
        using (var stream = new FileStream(path, FileMode.CreateNew)) stream.SetLength(ImportUploader.MaximumBytes + 1);
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        {
            session.MaxUploadFileSize = 0;
            ImportRun run = await importer.UploadAsync(new[] { Row("huge.png", path) }, temp.Path);
            Assert.Equal(ImportOutcome.Failed, Assert.Single(run.Results).Outcome);
            Assert.Contains("256 MiB", run.Results[0].Message, StringComparison.Ordinal);
            Assert.Empty(session.CallsTo("BackupFileBrowser.UploadAsync"));
        }
    }

    [Fact]
    public async Task What_the_server_said_before_the_row_was_sent_is_not_its_answer()
    {
        using var temp = new TempFolder();
        string a = Write(temp.Path, "a.png");
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        {
            session.FakeBackupFileBrowser.UploadAnswer = (_, _) => null;
            // A late "Uploaded file a.png" of an earlier send arrives when the row starts, before its own upload is sent.
            var progress = new SyncProgress<ImportProgress>(p =>
            {
                if (p.Result is null && p.Sent == 0) session.RaiseFileBrowserMessage("Uploaded file a.png");
            });
            ImportRun run = await importer.UploadAsync(new[] { Row("a.png", a) }, temp.Path, progress);
            Assert.Equal(ImportOutcome.NoAnswer, Assert.Single(run.Results).Outcome);
        }
    }

    [Fact]
    public async Task A_refusal_names_the_rows_folder_and_name_and_another_folders_refusal_is_not_taken()
    {
        using var temp = new TempFolder();
        string a = Write(temp.Path, "a.png");
        string b = Write(temp.Path, "b.png");
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        {
            session.FakeBackupFileBrowser.UploadAnswer = (path, _) =>
            {
                session.RaiseFileBrowserMessage("Could not upload world/heads/a.png: the other a.png was refused");
                session.RaiseFileBrowserMessage("Could not upload a.png: no folder open");
                return path.EndsWith("a.png", StringComparison.Ordinal) ? "Could not upload world/images/a.png: the disk refused it" : "\0";
            };
            ImportRun run = await importer.UploadAsync(new[] { Row("a.png", a), Row("b.png", b) }, temp.Path);
            Assert.Equal(ImportOutcome.Refused, run.Results[0].Outcome);
            Assert.Equal("the disk refused it", run.Results[0].Message);
            Assert.Equal(ImportOutcome.Uploaded, run.Results[1].Outcome);
        }
    }

    [Fact]
    public async Task The_servers_refusal_of_the_folder_is_the_reason_the_row_shows()
    {
        using var temp = new TempFolder();
        string a = Write(temp.Path, "a.png");
        string b = Write(temp.Path, "b.png");
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        {
            // The fake plays the server's two messages: the folder's refusal, then the upload's own "no folder open".
            ImportRun run = await importer.UploadAsync(new[] { Row("a.png", a, "world/elsewhere/"), Row("b.png", b) }, temp.Path);
            Assert.Equal(ImportOutcome.Refused, run.Results[0].Outcome);
            Assert.Equal("Folder world/elsewhere/ is not in your folder rights", run.Results[0].Message);
            Assert.Equal(ImportOutcome.Uploaded, run.Results[1].Outcome);
        }
    }

    [Fact]
    public async Task Three_uploads_in_a_row_without_any_answer_end_the_run_and_an_answer_in_between_starts_the_count_again()
    {
        using var temp = new TempFolder();
        ImportRow[] rows = Enumerable.Range(0, 6).Select(number => Row($"f{number}.png", Write(temp.Path, $"f{number}.png"))).ToArray();
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        {
            session.FakeBackupFileBrowser.UploadAnswer = (_, _) => null;
            ImportRun run = await importer.UploadAsync(rows, temp.Path);
            Assert.False(run.Cancelled);
            Assert.Equal(3, run.Results.Count);
            Assert.All(run.Results, result => Assert.Equal(ImportOutcome.NoAnswer, result.Outcome));
            Assert.Contains("3 uploads in a row", run.StoppedBecause, StringComparison.Ordinal);
            Assert.Equal(3, session.CallsTo("BackupFileBrowser.UploadAsync").Count());
        }

        (FakeRcSession again, MissingFilesImporter other) = Server();
        using (again)
        {
            again.FakeBackupFileBrowser.UploadAnswer = (path, _) => path.EndsWith("f2.png", StringComparison.Ordinal) ? "\0" : null;
            ImportRun run = await other.UploadAsync(rows.Take(5).ToArray(), temp.Path);
            Assert.Equal(5, run.Results.Count);
            Assert.Null(run.StoppedBecause);
        }
    }

    [Fact]
    public async Task Whatever_goes_wrong_with_one_file_is_that_rows_result_and_the_run_goes_on()
    {
        using var temp = new TempFolder();
        string a = Write(temp.Path, "a.png");
        string b = Write(temp.Path, "b.png");
        (FakeRcSession session, MissingFilesImporter importer) = Server();
        using (session)
        {
            session.FakeBackupFileBrowser.UploadAnswer = (path, _) => path.EndsWith("a.png", StringComparison.Ordinal) ? throw new InvalidOperationException("odd") : "\0";
            ImportRun run = await importer.UploadAsync(new[] { Row("a.png", a), Row("b.png", b) }, temp.Path);
            Assert.Equal(ImportOutcome.Failed, run.Results[0].Outcome);
            Assert.Contains("odd", run.Results[0].Message, StringComparison.Ordinal);
            Assert.Equal(ImportOutcome.Uploaded, run.Results[1].Outcome);
        }
    }

    [Fact]
    public async Task The_search_stops_at_the_next_entry_when_cancelled_whatever_the_entry_is()
    {
        using var temp = new TempFolder();
        for (int number = 0; number < 50; number++) Write(temp.Path, $"unwanted{number}.png");
        using var cancel = new CancellationTokenSource();
        int entries = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalFileIndex.BuildWithHookAsync(temp.Path, new HashSet<string> { "wanted.png" }, cancel.Token, () =>
        {
            if (++entries == 5) cancel.Cancel();
        }));
        Assert.Equal(5, entries);
    }
}

/// <summary>An <see cref="IProgress{T}"/> that calls its handler at once on the reporting thread (the test contexts have no UI thread).</summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
