using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Sync;
using RemoteControl.Sync.ImportMissing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Files.Tests;

/// <summary>The "Import Missing" window: the search, the list with its reasons, the choice, the confirmation, the upload and closing.</summary>
public sealed class ImportMissingWindowTests
{
    private static readonly FileBrowserFolder[] Rights = [new("rw", "world/heads/head*.png"), new("rw", "world/images/*"), new("r", "world/ganis/*")];

    private static MissingFile File(string name, MissingFileKind kind, string folder, int uses = 1, string usedBy = "level a.nw") => new(name, kind, folder, uses, usedBy);

    private static string Ugd(Fixture f) => Path.Combine(f.Test.Environment.Root, "ugd");

    private static string Local(Fixture f, string relative, int size = 5)
    {
        string path = Path.Combine(Ugd(f), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', size).ToArray());
        return path;
    }

    /// <summary>Plays the server: answers the request for missing files at once (when asked to) and the file browser's start with the rights.</summary>
    private static void Serve(Fixture f, IReadOnlyList<MissingFile>? files = null, string? error = null, bool answer = true)
    {
        f.Fake.OnCall = call =>
        {
            if (call.Method != "RequestMissingFilesAsync" || !answer) return;
            if (error is not null) f.Fake.RaiseMissingFilesError(error);
            else f.Fake.RaiseMissingFiles(files ?? []);
        };
        FakeFileBrowser browser = f.Fake.FakeBackupFileBrowser;
        browser.ServerAnswersUploads = true;
        browser.OnCall = call =>
        {
            if (call.Method == "BackupFileBrowser.StartAsync") browser.RaiseFoldersReceived(Rights);
        };
    }

    /// <summary>
    /// The time for a first answer in a test whose server stays silent until the test itself ends the wait (with the result, by
    /// closing, by cancelling). The 200 milliseconds of the other tests are for those that want the wait to run out: on a slow
    /// machine they ran out here too, in the middle of the test, and the second Find of a test then rightly started a second search
    /// (the Linux build machine, 2026-11-20).
    /// </summary>
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(60);

    private static ImportMissingWindow Open(Fixture f, bool withFolder = true, bool short_ = true, TimeSpan? requestTimeout = null)
    {
        var window = (ImportMissingWindow)f.Context.OpenTool(ToolIds.ImportMissing)!;
        if (short_) window.ImporterOptions = new MissingFilesImporterOptions { RequestTimeout = requestTimeout ?? TimeSpan.FromMilliseconds(200), UploadAnswerTimeout = TimeSpan.FromSeconds(10), RightsTimeout = TimeSpan.FromSeconds(5) };
        if (withFolder)
        {
            Directory.CreateDirectory(Ugd(f));
            TestUi.Find<TextBox>(window, "FolderBox").Text = Ugd(f);
        }

        TestUi.Settle();
        return window;
    }

    private static async Task FindAsync(ImportMissingWindow window)
    {
        TestUi.Click(TestUi.Find<Button>(window, "FindButton"));
        await TestUi.WaitUntilAsync(() => window.Running.IsCompleted && !window.IsBusy, what: "the search");
    }

    private static IReadOnlyList<MissingFile> Sample() =>
    [
        File("head1.png", MissingFileKind.Head, "world/heads/", 3, "level a.nw; weapon -Shop; npc Guard"),
        File("door.gif", MissingFileKind.Image, "world/images/"),
        File("ghost.png", MissingFileKind.Image, "world/images/"),
        File("orphan.png", MissingFileKind.Image, string.Empty),
        File("walk.gani", MissingFileKind.Gani, "world/ganis/"),
    ];

    private static void MakeLocalFiles(Fixture f)
    {
        Local(f, "heads/head1.png", 10);
        Local(f, "misc/door.gif", 20);
        Local(f, "images/door.gif", 30);
        Local(f, "orphan.png");
        Local(f, "ganis/walk.gani");
    }

    private static ImportMissingItem Item(ImportMissingWindow window, string name) => window.Items.Single(item => item.Name == name);

    private static ImportMissingWindow Found(Fixture f)
    {
        MakeLocalFiles(f);
        Serve(f, Sample());
        return Open(f);
    }

    [AvaloniaFact]
    public void It_opens_once_per_session_and_starts_with_the_folder_that_was_remembered()
    {
        using var f = new Fixture(options: o => o with { ImportMissingFolder = "/some/ugd" });
        ImportMissingWindow window = Open(f, withFolder: false);
        Assert.Equal("Import Missing - Test Server", window.Title);
        Assert.Equal("/some/ugd", TestUi.Find<TextBox>(window, "FolderBox").Text);
        Assert.Same(window, f.Context.OpenTool(ToolIds.ImportMissing));
        Assert.False(TestUi.Find<Button>(window, "UploadButton").IsEnabled);
        Assert.False(TestUi.Find<Button>(window, "CancelButton").IsEnabled);
        Assert.True(TestUi.Find<Button>(window, "FindButton").IsEnabled);
    }

    [AvaloniaFact]
    public async Task Find_shows_every_row_with_its_kind_use_folder_local_file_and_reason()
    {
        using var f = new Fixture();
        ImportMissingWindow window = Found(f);
        await FindAsync(window);

        Assert.Single(f.Fake.CallsTo("RequestMissingFilesAsync"));
        Assert.Equal(5, window.Items.Count);
        ImportMissingItem head = Item(window, "head1.png");
        Assert.Equal("head", head.KindText);
        Assert.Equal("3 uses: level a.nw; weapon -Shop; npc Guard", head.UsedText);
        Assert.Equal("world/heads/", head.FolderText);
        Assert.Equal("heads/head1.png (10 B)", head.LocalText);
        Assert.True(head.Ticked);
        Assert.Equal("ready to upload", head.Status);

        ImportMissingItem door = Item(window, "door.gif");
        Assert.Equal("images/door.gif (30 B)", door.LocalText);
        Assert.True(door.HasAlternatives);
        Assert.Equal("1 use: level a.nw", door.UsedText);

        ImportMissingItem ghost = Item(window, "ghost.png");
        Assert.False(ghost.Ticked);
        Assert.Equal("(not found)", ghost.LocalText);
        Assert.Equal("not ticked: not found in the folder", ghost.Status);

        ImportMissingItem orphan = Item(window, "orphan.png");
        Assert.False(orphan.Ticked);
        Assert.Equal("(no folder on the server)", orphan.FolderText);
        Assert.StartsWith("not ticked: no folder on the server", orphan.Status, StringComparison.Ordinal);

        ImportMissingItem gani = Item(window, "walk.gani");
        Assert.False(gani.Ticked);
        Assert.Equal("not ticked: no folder right with w for it", gani.Status);
        Assert.False(gani.CanTick);
        gani.Ticked = true;
        Assert.False(gani.Ticked);

        Assert.True(TestUi.Find<Button>(window, "UploadButton").IsEnabled);
        Assert.Equal("2 of 5 ticked · 40 B", TestUi.Find<TextBlock>(window, "CountsLabel").Text);
        Assert.Contains("5 files missing on the server, 4 found under the folder, 2 ready to upload.", window.StatusText, StringComparison.Ordinal);
        Shots.Save(window, "import-missing");
    }

    [AvaloniaFact]
    public async Task The_chosen_folder_is_remembered_in_the_options()
    {
        using var f = new Fixture();
        ImportMissingWindow window = Found(f);
        await FindAsync(window);
        Assert.Equal(Ugd(f), f.Context.App.Options.Current.ImportMissingFolder);
    }

    [AvaloniaFact]
    public async Task Browse_fills_the_folder_box()
    {
        using var f = new Fixture();
        ImportMissingWindow window = Open(f, withFolder: false);
        window.PickFolder = () => Task.FromResult<string?>("/picked/folder");
        TestUi.Click(TestUi.Find<Button>(window, "ChooseFolderButton"));
        await TestUi.WaitUntilAsync(() => TestUi.Find<TextBox>(window, "FolderBox").Text == "/picked/folder", what: "the folder");
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("relative/folder")]
    [InlineData("/no/such/folder/anywhere")]
    public async Task A_folder_that_is_missing_or_no_full_path_asks_nothing(string folder)
    {
        using var f = new Fixture();
        Serve(f, Sample());
        ImportMissingWindow window = Open(f, withFolder: false);
        TestUi.Find<TextBox>(window, "FolderBox").Text = folder;
        await window.FindAsync();
        Assert.Empty(f.Fake.CallsTo("RequestMissingFilesAsync"));
        Assert.False(window.IsBusy);
        Assert.NotEqual("Ready.", window.StatusText);
    }

    [AvaloniaFact]
    public async Task A_second_find_while_one_runs_does_nothing()
    {
        using var f = new Fixture();
        MakeLocalFiles(f);
        Serve(f, Sample(), answer: false);
        ImportMissingWindow window = Open(f, requestTimeout: Patient);
        TestUi.Click(TestUi.Find<Button>(window, "FindButton"));
        Assert.True(window.IsBusy);
        Assert.False(TestUi.Find<Button>(window, "FindButton").IsEnabled);
        Assert.True(TestUi.Find<Button>(window, "CancelButton").IsEnabled);
        // The folder is checked off the thread first: the second Find comes when the first one's request is out.
        await TestUi.WaitUntilAsync(() => f.Fake.CallsTo("RequestMissingFilesAsync").Any(), what: "the first request");
        Task second = window.FindAsync();
        await second;
        TestUi.Click(TestUi.Find<Button>(window, "FindButton"));
        TestUi.Settle();
        Assert.Single(f.Fake.CallsTo("RequestMissingFilesAsync"));
        Assert.Contains("Asking the server", window.StatusText, StringComparison.Ordinal);

        f.Fake.RaiseMissingFiles(Sample());
        await window.Running;
        Assert.Equal(5, window.Items.Count);
    }

    [AvaloniaFact]
    public async Task A_server_that_does_not_answer_is_said_not_to_know_the_request()
    {
        using var f = new Fixture();
        MakeLocalFiles(f);
        Serve(f, Sample(), answer: false);
        ImportMissingWindow window = Open(f);
        await FindAsync(window);
        Assert.Contains("does not know the request for missing files", window.StatusText, StringComparison.Ordinal);
        Assert.Empty(window.Items);
        Assert.True(TestUi.Find<Button>(window, "FindButton").IsEnabled);
        Assert.False(TestUi.Find<Button>(window, "UploadButton").IsEnabled);
    }

    [AvaloniaFact]
    public async Task While_the_server_searches_the_window_says_so_and_waits_far_longer_than_the_time_for_a_first_answer()
    {
        using var f = new Fixture();
        MakeLocalFiles(f);
        Serve(f, Sample(), answer: false);
        f.Fake.OnCall = call =>
        {
            if (call.Method == "RequestMissingFilesAsync") f.Fake.RaiseMissingFilesSearching();
        };
        ImportMissingWindow window = Open(f);
        TestUi.Click(TestUi.Find<Button>(window, "FindButton"));
        await TestUi.WaitUntilAsync(() => window.StatusText.Contains("The server is searching", StringComparison.Ordinal), what: "the searching text");
        await Task.Delay(700);
        TestUi.Settle();
        Assert.True(window.IsBusy);
        Assert.Contains("The server is searching", window.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("does not know", window.StatusText, StringComparison.Ordinal);

        f.Fake.RaiseMissingFiles(Sample());
        await window.Running;
        Assert.Equal(5, window.Items.Count);
    }

    [AvaloniaFact]
    public async Task A_connection_that_ends_while_the_server_searches_is_not_called_an_old_server()
    {
        using var f = new Fixture();
        MakeLocalFiles(f);
        Serve(f, Sample(), answer: false);
        f.Fake.OnCall = call =>
        {
            if (call.Method == "RequestMissingFilesAsync") f.Fake.RaiseMissingFilesSearching();
        };
        ImportMissingWindow window = Open(f);
        TestUi.Click(TestUi.Find<Button>(window, "FindButton"));
        await TestUi.WaitUntilAsync(() => window.StatusText.Contains("The server is searching", StringComparison.Ordinal), what: "the searching text");
        f.Fake.RaiseDisconnected();
        await window.Running;
        Assert.Contains("connection to the server ended", window.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("does not know", window.StatusText, StringComparison.Ordinal);
        Assert.False(window.IsBusy);
    }

    [AvaloniaFact]
    public async Task Cancel_ends_the_wait_after_searching()
    {
        using var f = new Fixture();
        MakeLocalFiles(f);
        Serve(f, Sample(), answer: false);
        f.Fake.OnCall = call =>
        {
            if (call.Method == "RequestMissingFilesAsync") f.Fake.RaiseMissingFilesSearching();
        };
        ImportMissingWindow window = Open(f);
        TestUi.Click(TestUi.Find<Button>(window, "FindButton"));
        await TestUi.WaitUntilAsync(() => window.StatusText.Contains("The server is searching", StringComparison.Ordinal), what: "the searching text");
        TestUi.Click(TestUi.Find<Button>(window, "CancelButton"));
        await window.Running;
        Assert.Equal("Cancelled.", window.StatusText);
        Assert.False(window.IsBusy);
    }

    [AvaloniaFact]
    public async Task The_confirmation_says_only_files_the_server_reported_missing_are_uploaded_and_does_not_promise_nothing_is_replaced()
    {
        using var f = new Fixture();
        ImportMissingWindow window = Found(f);
        await FindAsync(window);
        TestUi.Click(TestUi.Find<Button>(window, "UploadButton"));
        RcDialog question = TestUi.FindDialog();
        Assert.Contains("Only files the server reported missing are uploaded", question.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing there is replaced", question.Message, StringComparison.Ordinal);
        question.Cancel();
        await TestUi.WaitUntilAsync(() => !window.IsBusy, what: "the refusal");
    }

    [AvaloniaFact]
    public async Task Three_uploads_in_a_row_the_server_does_not_answer_end_the_run_and_the_window_says_so()
    {
        using var f = new Fixture();
        for (int number = 0; number < 5; number++) Local(f, $"images/s{number}.png");
        Serve(f, Enumerable.Range(0, 5).Select(number => File($"s{number}.png", MissingFileKind.Image, "world/images/")).ToList());
        f.Fake.FakeBackupFileBrowser.UploadAnswer = (_, _) => null;
        ImportMissingWindow window = Open(f);
        window.ImporterOptions = new MissingFilesImporterOptions { RequestTimeout = TimeSpan.FromSeconds(5), UploadAnswerTimeout = TimeSpan.FromMilliseconds(40), RightsTimeout = TimeSpan.FromSeconds(5) };
        await FindAsync(window);
        TestUi.Click(TestUi.Find<Button>(window, "UploadButton"));
        TestUi.FindDialog().Accept();
        await TestUi.WaitUntilAsync(() => window.Running.IsCompleted && !window.IsBusy, what: "the run");
        Assert.Equal(3, f.Fake.CallsTo("BackupFileBrowser.UploadAsync").Count());
        Assert.Contains("did not answer 3 uploads in a row", window.StatusText, StringComparison.Ordinal);
        Assert.Equal(3, window.Items.Count(item => item.Result?.Outcome == ImportOutcome.NoAnswer));
        Assert.Equal(2, window.Items.Count(item => item.Result is null));
    }

    [AvaloniaFact]
    public async Task A_backup_is_refused_while_import_missing_searches()
    {
        using var f = new Fixture();
        MakeLocalFiles(f);
        Serve(f, Sample(), answer: false);
        ImportMissingWindow window = Open(f, requestTimeout: Patient);
        TestUi.Click(TestUi.Find<Button>(window, "FindButton"));
        Assert.True(window.IsBusy);
        var backup = (ServerBackupWindow)f.Context.OpenTool(ToolIds.ServerBackup)!;
        TestUi.Find<TextBox>(backup, "FolderBox").Text = Path.Combine(f.Test.Environment.Root, "backup");
        await backup.StartAsync();
        Assert.Contains(backup.LogLines, line => line.Contains("Import Missing is searching or uploading", StringComparison.Ordinal));
        Assert.False(backup.IsRunning);
        f.Fake.RaiseMissingFiles([]);
        await window.Running;
    }

    [AvaloniaFact]
    public async Task The_servers_refusal_and_an_empty_result_are_told()
    {
        using var f = new Fixture();
        Serve(f, error: "You do not have the right to update levels.");
        ImportMissingWindow window = Open(f);
        await FindAsync(window);
        Assert.Equal("The server did not search: You do not have the right to update levels.", window.StatusText);

        Serve(f, []);
        await FindAsync(window);
        Assert.StartsWith("Nothing is missing", window.StatusText, StringComparison.Ordinal);
        Assert.Empty(window.Items);
    }

    [AvaloniaFact]
    public async Task Another_local_file_can_be_chosen_and_the_row_is_judged_again()
    {
        using var f = new Fixture();
        string other = Local(f, "misc/empty.gif", 0);
        System.IO.File.Move(other, Path.Combine(Ugd(f), "zzz.gif"));
        ImportMissingWindow window = Found(f);
        await FindAsync(window);
        ImportMissingItem door = Item(window, "door.gif");
        Assert.Equal("images/door.gif (30 B)", door.LocalText);

        _ = window.ChooseLocalFileAsync(door);
        RcDialog dialog = TestUi.FindDialog();
        Assert.Equal(["images/door.gif (30 B)", "misc/door.gif (20 B)"], TestUi.First<ListBox>(dialog).Items.Select(item => item?.ToString()));
        TestUi.First<ListBox>(dialog).SelectedIndex = 1;
        dialog.Accept();
        await TestUi.WaitUntilAsync(() => door.LocalText.StartsWith("misc/", StringComparison.Ordinal), what: "the choice");
        Assert.Equal("misc/door.gif (20 B)", door.LocalText);
        Assert.True(door.Ticked);
        Assert.Equal(["images/door.gif"], door.Row.Alternatives.Select(door.RelativePath));
        Assert.Equal("2 of 5 ticked · 30 B", TestUi.Find<TextBlock>(window, "CountsLabel").Text);

        // Choosing the file already chosen, or closing the question, changes nothing.
        _ = window.ChooseLocalFileAsync(door);
        TestUi.FindDialog().Cancel();
        TestUi.Settle();
        Assert.Equal("misc/door.gif (20 B)", door.LocalText);
        Assert.False(Item(window, "ghost.png").HasAlternatives);
    }

    [AvaloniaFact]
    public async Task The_chosen_file_goes_through_the_same_checks_as_the_first()
    {
        using var f = new Fixture();
        Local(f, "a/door.gif", 5);
        string empty = Path.Combine(Ugd(f), "z", "door.gif");
        Directory.CreateDirectory(Path.GetDirectoryName(empty)!);
        System.IO.File.WriteAllBytes(empty, []);
        Serve(f, [File("door.gif", MissingFileKind.Image, "world/images/")]);
        ImportMissingWindow window = Open(f);
        await FindAsync(window);
        ImportMissingItem door = Item(window, "door.gif");
        Assert.True(door.Ticked);
        _ = window.ChooseLocalFileAsync(door);
        RcDialog dialog = TestUi.FindDialog();
        TestUi.First<ListBox>(dialog).SelectedIndex = 1;
        dialog.Accept();
        await TestUi.WaitUntilAsync(() => !door.Ticked, what: "the choice");
        Assert.Equal("not ticked: the file is empty", door.Status);
        Assert.False(TestUi.Find<Button>(window, "UploadButton").IsEnabled);
    }

    [AvaloniaFact]
    public async Task Upload_asks_how_many_files_and_how_large_and_sends_nothing_when_refused()
    {
        using var f = new Fixture();
        ImportMissingWindow window = Found(f);
        await FindAsync(window);

        TestUi.Click(TestUi.Find<Button>(window, "UploadButton"));
        RcDialog question = TestUi.FindDialog();
        Assert.Contains("Upload 2 files (40 B in total)", question.Message, StringComparison.Ordinal);
        Assert.Contains("Test Server", question.Message, StringComparison.Ordinal);
        Assert.True(window.IsBusy);
        Assert.False(TestUi.Find<Button>(window, "UploadButton").IsEnabled);
        question.Cancel();
        await TestUi.WaitUntilAsync(() => !window.IsBusy, what: "the refusal");
        Assert.Empty(f.Fake.CallsTo("BackupFileBrowser.UploadAsync"));
        Assert.True(TestUi.Find<Button>(window, "UploadButton").IsEnabled);
        Assert.All(window.Items, item => Assert.Null(item.Result));
    }

    [AvaloniaFact]
    public async Task Upload_sends_the_ticked_files_into_the_servers_folders_and_shows_a_result_per_row()
    {
        using var f = new Fixture();
        ImportMissingWindow window = Found(f);
        await FindAsync(window);
        Item(window, "door.gif").Ticked = false;

        TestUi.Click(TestUi.Find<Button>(window, "UploadButton"));
        RcDialog question = TestUi.FindDialog();
        Assert.Contains("Upload 1 file (10 B in total)", question.Message, StringComparison.Ordinal);
        question.Accept();
        await TestUi.WaitUntilAsync(() => window.Running.IsCompleted && !window.IsBusy, what: "the upload");

        FakeFileBrowser browser = f.Fake.FakeBackupFileBrowser;
        Assert.Equal(10, browser.RemoteFiles["world/heads/head1.png"].Length);
        Assert.Single(browser.RemoteFiles);
        Assert.Equal("uploaded", Item(window, "head1.png").Status);
        Assert.Equal("not ticked", Item(window, "door.gif").Status);
        Assert.False(Item(window, "head1.png").Ticked);
        Assert.False(Item(window, "head1.png").CanTick);
        Assert.Contains("Uploaded 1 of 1 file.", window.StatusText, StringComparison.Ordinal);
        Assert.True(TestUi.Find<Button>(window, "FindButton").IsEnabled);
        Assert.False(TestUi.Find<Button>(window, "CancelButton").IsEnabled);
        Assert.False(TestUi.Find<Button>(window, "UploadButton").IsEnabled);
    }

    [AvaloniaFact]
    public async Task The_servers_refusal_is_shown_in_the_row_and_the_others_still_go()
    {
        using var f = new Fixture();
        ImportMissingWindow window = Found(f);
        await FindAsync(window);
        f.Fake.FakeBackupFileBrowser.UploadAnswer = (path, _) => path.EndsWith("head1.png", StringComparison.Ordinal) ? "Could not upload world/heads/head1.png: not in your folder rights" : "\0";

        TestUi.Click(TestUi.Find<Button>(window, "UploadButton"));
        TestUi.FindDialog().Accept();
        await TestUi.WaitUntilAsync(() => window.Running.IsCompleted && !window.IsBusy, what: "the upload");

        Assert.Equal("not in your folder rights", Item(window, "head1.png").Status);
        Assert.Equal(ImportOutcome.Refused, Item(window, "head1.png").Result!.Outcome);
        Assert.True(Item(window, "head1.png").Ticked);
        Assert.Equal("uploaded", Item(window, "door.gif").Status);
        Assert.True(f.Fake.FakeBackupFileBrowser.RemoteFiles.ContainsKey("world/images/door.gif"));
        Assert.Contains("Uploaded 1 of 2 files. 1 could not be uploaded", window.StatusText, StringComparison.Ordinal);

        // Find again: the window is ready for another search.
        Serve(f, [File("head1.png", MissingFileKind.Head, "world/heads/")]);
        await FindAsync(window);
        Assert.Single(window.Items);
        Assert.Null(window.Items[0].Result);
    }

    [AvaloniaFact]
    public async Task A_second_upload_click_while_the_question_or_the_upload_runs_does_nothing()
    {
        using var f = new Fixture();
        ImportMissingWindow window = Found(f);
        await FindAsync(window);
        f.Fake.FakeBackupFileBrowser.UploadAnswer = (_, _) => null;

        Task first = window.UploadAsync();
        RcDialog question = TestUi.FindDialog();
        await window.UploadAsync();
        await window.FindAsync();
        Assert.Single(RcWindow.OpenWindows.OfType<RcDialog>());
        question.Accept();
        await TestUi.WaitUntilAsync(() => f.Fake.CallsTo("BackupFileBrowser.UploadAsync").Count == 1, what: "the first upload");
        await window.UploadAsync();
        await window.FindAsync();
        Assert.Single(f.Fake.CallsTo("RequestMissingFilesAsync"));
        window.Cancel();
        await first;
        await TestUi.WaitUntilAsync(() => !window.IsBusy, what: "the end");
        Assert.Equal(ImportOutcome.Cancelled, Item(window, "head1.png").Result!.Outcome);
        Assert.Contains("Cancelled.", window.StatusText, StringComparison.Ordinal);
        Assert.Equal("not sent: cancelled", Item(window, "door.gif").Status);
    }

    [AvaloniaFact]
    public async Task Closing_in_the_middle_of_a_search_stops_it_and_a_late_answer_is_ignored()
    {
        using var f = new Fixture();
        MakeLocalFiles(f);
        Serve(f, Sample(), answer: false);
        ImportMissingWindow window = Open(f, requestTimeout: Patient);
        TestUi.Click(TestUi.Find<Button>(window, "FindButton"));
        Assert.True(window.IsBusy);
        TestUi.Click(TestUi.Find<Button>(window, "CloseButton"));
        await TestUi.WaitUntilAsync(() => window.IsClosed, what: "the window to close");
        await window.Running;
        f.Fake.RaiseMissingFiles(Sample());
        TestUi.Settle();
        Assert.Empty(window.Items);
        Assert.Empty(f.Fake.CallsTo("BackupFileBrowser.StartAsync"));
    }

    [AvaloniaFact]
    public async Task Closing_in_the_middle_of_an_upload_asks_then_stops_after_the_file_on_its_way()
    {
        using var f = new Fixture();
        ImportMissingWindow window = Found(f);
        await FindAsync(window);
        f.Fake.FakeBackupFileBrowser.UploadAnswer = (_, _) => null;
        TestUi.Click(TestUi.Find<Button>(window, "UploadButton"));
        TestUi.FindDialog().Accept();
        await TestUi.WaitUntilAsync(() => f.Fake.CallsTo("BackupFileBrowser.UploadAsync").Count == 1, what: "the first upload");

        TestUi.Click(TestUi.Find<Button>(window, "CloseButton"));
        RcDialog question = TestUi.FindDialog();
        Assert.Contains("An upload is running", question.Message, StringComparison.Ordinal);
        question.Cancel();
        TestUi.Settle();
        Assert.True(window.IsBusy);
        Assert.False(window.IsClosed);

        TestUi.Click(TestUi.Find<Button>(window, "CloseButton"));
        TestUi.FindDialog().Accept();
        await TestUi.WaitUntilAsync(() => window.IsClosed, what: "the window to close");
        await window.Running;
        Assert.Single(f.Fake.CallsTo("BackupFileBrowser.UploadAsync"));
    }

    [AvaloniaFact]
    public async Task The_window_closing_from_code_in_the_middle_of_an_upload_stops_it_without_a_question()
    {
        using var f = new Fixture();
        ImportMissingWindow window = Found(f);
        await FindAsync(window);
        f.Fake.FakeBackupFileBrowser.UploadAnswer = (_, _) => null;
        TestUi.Click(TestUi.Find<Button>(window, "UploadButton"));
        TestUi.FindDialog().Accept();
        await TestUi.WaitUntilAsync(() => f.Fake.CallsTo("BackupFileBrowser.UploadAsync").Count == 1, what: "the first upload");
        window.Close();
        await window.Running;
        Assert.True(window.IsClosed);
        Assert.Empty(RcWindow.OpenWindows.OfType<RcDialog>());
    }

    [AvaloniaFact]
    public async Task Nothing_starts_while_not_connected_or_while_files_sync_or_a_backup_runs()
    {
        using var f = new Fixture();
        Serve(f, Sample());
        ImportMissingWindow window = Open(f);
        using (IDisposable offered = f.Context.Services.Provide<ISyncStatus>(new SyncStatus { IsFileSyncActive = true }))
        {
            await window.FindAsync();
            Assert.Contains("Files are being synchronised", window.StatusText, StringComparison.Ordinal);
        }

        f.Fake.IsConnected = false;
        await window.FindAsync();
        Assert.Equal("Not connected.", window.StatusText);
        Assert.Empty(f.Fake.CallsTo("RequestMissingFilesAsync"));
    }

    [AvaloniaFact]
    public void Nothing_ticked_uploads_nothing()
    {
        using var f = new Fixture();
        ImportMissingWindow window = Open(f);
        _ = window.UploadAsync();
        Assert.Equal("Nothing is ticked.", window.StatusText);
        Assert.Empty(RcWindow.OpenWindows.OfType<RcDialog>());
    }

    [AvaloniaFact]
    public void The_file_browser_offers_it()
    {
        using var f = new Fixture();
        FileBrowserWindow browser = f.OpenBrowser();
        TestUi.Click(TestUi.Find<Button>(browser, "ImportMissingButton"));
        Assert.IsType<ImportMissingWindow>(f.Context.Windows.Find(ToolIds.ImportMissing));
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
