using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Files.Tests;

public sealed class FileBrowserTests
{
    [AvaloniaFact]
    public void Opening_starts_the_browser_and_shows_the_tree()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenBrowser();
        Assert.Equal("File Browser - Test Server", window.Title);
        Assert.True(f.Fake.WasCalled("FileBrowser.StartAsync"));
        f.Browser.RaiseFoldersReceived([new FileBrowserFolder("rw", "levels/*"), new FileBrowserFolder("r", "levels/sub/*.nw"), new FileBrowserFolder("rw", "images/*.png")]);
        Assert.Equal(["images/", "levels/"], window.Folders.Select(node => node.Label));
        Assert.Same(window, f.OpenBrowser());
        Assert.Equal("0 files", TestUi.Find<TextBlock>(window, "StatusLabel").Text);
    }

    [AvaloniaFact]
    public void A_folder_argument_navigates_there()
    {
        using var f = new Fixture();
        f.OpenBrowser("\\levels//sub");
        Assert.Equal("levels/sub/", f.Fake.LastCall("FileBrowser.ChangeFolderAsync")!.Argument<string>(0));
    }

    [AvaloniaFact]
    public void Selecting_a_tree_node_navigates()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw"));
        var tree = TestUi.Find<TreeView>(window, "FolderTree");
        tree.SelectedItem = window.Folders.Single(node => node.Label == "images/");
        TestUi.Settle();
        Assert.Equal("images/", window.CurrentFolder);
        Assert.Equal("images/", f.Fake.LastCall("FileBrowser.ChangeFolderAsync")!.Argument<string>(0));
        Assert.True(window.Folders.Single(node => node.Label == "images/").IsOpen);
        Assert.False(window.Folders.Single(node => node.Label == "levels/").IsOpen);
        Assert.Equal("Current Folder: images/", TestUi.Find<TextBlock>(window, "FolderLabel").Text);
    }

    [AvaloniaFact]
    public void A_listing_for_another_folder_is_ignored()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenBrowser();
        window.NavigateTo("/levels\\");
        Assert.Equal("levels/", window.CurrentFolder);
        f.Browser.RaiseFilesReceived("images/", [Fixture.File("x.png")]);
        Assert.Empty(window.Rows);
        f.Browser.RaiseFilesReceived("levels/", [Fixture.File("a.nw"), Fixture.File("b.nw")]);
        Assert.Equal(["a.nw", "b.nw"], window.Rows.Select(row => row.Name));
        Assert.Equal("2 files", TestUi.Find<TextBlock>(window, "StatusLabel").Text);
    }

    [AvaloniaFact]
    public void Sort_order_and_selection_survive_a_refresh()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw", 20), Fixture.File("b.nw", 5), Fixture.File("c.nw", 300));
        TestUi.Click(TestUi.Find<Button>(window, "SortSize"));
        Assert.Equal(["b.nw", "a.nw", "c.nw"], window.Rows.Select(row => row.Name));
        var list = TestUi.Find<ListBox>(window, "FileList");
        list.SelectedItems!.Add(window.Rows[1]);
        list.SelectedItems.Add(window.Rows[2]);
        f.Browser.RaiseFilesReceived("levels/", [Fixture.File("a.nw", 20), Fixture.File("b.nw", 5), Fixture.File("c.nw", 300), Fixture.File("d.nw", 1)]);
        Assert.Equal(["d.nw", "b.nw", "a.nw", "c.nw"], window.Rows.Select(row => row.Name));
        Assert.Equal(["levels/a.nw", "levels/c.nw"], window.SelectedPaths());
        Assert.Equal("4 files  |  2 selected", TestUi.Find<TextBlock>(window, "StatusLabel").Text);
        TestUi.Click(TestUi.Find<Button>(window, "SortSize"));
        Assert.Equal(["c.nw", "a.nw", "b.nw", "d.nw"], window.Rows.Select(row => row.Name));
        Assert.Equal((2, true), window.Sort);
        Assert.Equal("Size \u25BC", TestUi.Find<Button>(window, "SortSize").Content);
    }

    [AvaloniaFact]
    public void Rows_show_size_time_and_type_icon()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw", 0, modified: 0), Fixture.File("b.txt", 42));
        FileRow empty = window.Rows[0];
        Assert.Equal(string.Empty, empty.SizeText);
        Assert.Equal(string.Empty, empty.ModifiedText);
        Assert.Equal("42", window.Rows[1].SizeText);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d$", window.Rows[1].ModifiedText);
        Assert.Same(f.Context.App.Assets.FileTypeIcon("b.txt"), window.Rows[1].Icon);
    }

    [AvaloniaFact]
    public async Task Download_writes_under_the_download_folder_with_the_server_time()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw", 5, modified: 1_600_000_000));
        f.Browser.RemoteFiles["levels/a.nw"] = Encoding.ASCII.GetBytes("GLEVNW01");
        var list = TestUi.Find<ListBox>(window, "FileList");
        list.SelectedItems!.Add(window.Rows[0]);
        ContextMenu menu = window.BuildMenu(window.SelectedPaths(), folders: false);
        Assert.Equal(["Download", "Edit", "Upload file(s)", "Move", "Rename", "Delete"], Fixture.MenuNames(menu));
        Fixture.Invoke(menu, "MenuDownload");
        string expected = Path.Combine(f.DownloadRoot, "Test Server", "levels", "a.nw");
        await TestUi.WaitUntilAsync(() => File.Exists(expected), what: "the downloaded file");
        Assert.Equal("GLEVNW01", File.ReadAllText(expected));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_000).UtcDateTime, File.GetLastWriteTimeUtc(expected));
        Assert.Contains(window.LogLines, line => line.StartsWith("Downloaded file ", StringComparison.Ordinal));
        Assert.Equal(["Download selected", "Upload file(s)", "Move selected", "Delete"], Fixture.MenuNames(window.BuildMenu(["levels/a.nw", "levels/b.nw"], false)));
        Assert.Equal(["Upload file(s)", "Rename", "Delete"], Fixture.MenuNames(window.BuildMenu(["levels"], true)));
    }

    [AvaloniaFact]
    public async Task A_different_local_copy_is_kept_aside()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw"));
        string local = Path.Combine(f.DownloadRoot, "Test Server", "levels", "a.nw");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, "my edit");
        f.Browser.RemoteFiles["levels/a.nw"] = Encoding.ASCII.GetBytes("server");
        await window.DownloadAsync(["levels/a.nw"]);
        Assert.Equal("server", File.ReadAllText(local));
        string aside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(local)!, "a.nw.local-*"));
        Assert.Equal("my edit", File.ReadAllText(aside));
    }

    [AvaloniaFact]
    public async Task A_name_that_leaves_the_folder_is_refused()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("../x.nw"), Fixture.File("a:b.nw"));
        f.Browser.RemoteFiles["levels/../x.nw"] = [1];
        f.Browser.RemoteFiles["levels/a:b.nw"] = [1];
        await window.DownloadAsync(["levels/../x.nw", "levels/a:b.nw"]);
        Assert.Equal(2, window.LogLines.Count(line => line.StartsWith("Refused to save", StringComparison.Ordinal)));
        Assert.False(File.Exists(Path.Combine(f.DownloadRoot, "Test Server", "x.nw")));
        Assert.False(Directory.Exists(Path.Combine(f.DownloadRoot, "Test Server")) && Directory.GetFiles(Path.Combine(f.DownloadRoot, "Test Server"), "*", SearchOption.AllDirectories).Length > 0);
    }

    [AvaloniaFact]
    public async Task Upload_sends_the_file_into_the_folder()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw"));
        string local = Path.Combine(f.Test.Environment.Root, "new.nw");
        File.WriteAllText(local, "content");
        window.PickUploadFiles = () => Task.FromResult<IReadOnlyList<string>>([local]);
        Fixture.Invoke(window.BuildMenu(["levels/a.nw"], false), "MenuUpload");
        await TestUi.WaitUntilAsync(() => f.Fake.WasCalled("FileBrowser.UploadAsync"));
        RecordedCall call = f.Fake.LastCall("FileBrowser.UploadAsync")!;
        Assert.Equal("levels/new.nw", call.Argument<string>(0));
        Assert.Equal("content", Encoding.ASCII.GetString(call.Argument<byte[]>(1)));
        f.Fake.ClearCalls();
        await TestUi.WaitUntilAsync(() => f.Fake.WasCalled("FileBrowser.ChangeFolderAsync"), what: "the refresh after the upload");

        Fixture.Invoke(window.BuildMenu(["images"], true), "MenuUpload");
        await TestUi.WaitUntilAsync(() => f.Fake.WasCalled("FileBrowser.UploadAsync"));
        Assert.Equal("images/new.nw", f.Fake.LastCall("FileBrowser.UploadAsync")!.Argument<string>(0));
    }

    [AvaloniaFact]
    public async Task A_too_large_upload_is_refused()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/");
        f.Fake.MaxUploadFileSize = 4;
        string local = Path.Combine(f.Test.Environment.Root, "big.nw");
        File.WriteAllText(local, "0123456789");
        await window.UploadLocalFilesAsync([local], "levels/");
        Assert.False(f.Fake.WasCalled("FileBrowser.UploadAsync"));
        Assert.Contains(window.LogLines, line => line.Contains("larger than the server accepts", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Rename_and_move_ask_and_send()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw"));
        Fixture.Invoke(window.BuildMenu(["levels/a.nw"], false), "MenuRename");
        RcDialog rename = TestUi.FindDialog();
        Assert.Equal("Rename", rename.Title);
        Assert.Equal("a.nw", rename.Text);
        TestUi.Find<TextBox>(rename, Dialogs.InputName).Text = "b.nw";
        rename.Accept();
        await TestUi.WaitUntilAsync(() => f.Fake.WasCalled("FileBrowser.RenameAsync"));
        RecordedCall call = f.Fake.LastCall("FileBrowser.RenameAsync")!;
        Assert.Equal("levels/a.nw", call.Argument<string>(0));
        Assert.Equal("b.nw", call.Argument<string>(1));

        Fixture.Invoke(window.BuildMenu(["levels/a.nw"], false), "MenuMove");
        RcDialog move = TestUi.FindDialog();
        Assert.Equal("Move levels/a.nw", move.Title);
        Assert.Equal("levels/", move.Text);
        TestUi.Find<TextBox>(move, Dialogs.InputName).Text = "images";
        move.Accept();
        await TestUi.WaitUntilAsync(() => f.Fake.WasCalled("FileBrowser.MoveAsync"));
        call = f.Fake.LastCall("FileBrowser.MoveAsync")!;
        Assert.Equal("levels/a.nw", call.Argument<string>(0));
        Assert.Equal("images/", call.Argument<string>(1));
        Assert.Contains("Moved file levels/a.nw to images/", window.LogLines);
    }

    [AvaloniaFact]
    public async Task Delete_asks_first_naming_the_files()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw"), Fixture.File("b.nw"));
        Fixture.Invoke(window.BuildMenu(["levels/a.nw", "levels/b.nw"], false), "MenuDelete");
        RcDialog dialog = TestUi.FindDialog();
        Assert.Contains("levels/a.nw", dialog.Message);
        Assert.Contains("levels/b.nw", dialog.Message);
        Shots.Save(dialog, "file-browser-delete-dialog");
        Assert.False(f.Fake.WasCalled("FileBrowser.DeleteAsync"));
        dialog.Cancel();
        TestUi.Settle();
        Assert.False(f.Fake.WasCalled("FileBrowser.DeleteAsync"));

        Fixture.Invoke(window.BuildMenu(["levels/a.nw", "levels/b.nw"], false), "MenuDelete");
        TestUi.FindDialog().Accept();
        await TestUi.WaitUntilAsync(() => f.Fake.CallsTo("FileBrowser.DeleteAsync").Count == 2);
        Assert.Equal(["levels/a.nw", "levels/b.nw"], f.Fake.CallsTo("FileBrowser.DeleteAsync").Select(call => call.Argument<string>(0)));
    }

    [AvaloniaFact]
    public async Task Double_click_downloads_opens_and_uploads_changes()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw", rights: "rw"));
        f.Browser.RemoteFiles["levels/a.nw"] = Encoding.ASCII.GetBytes("first");
        await window.OpenAsync(window.Rows[0]);
        string local = Path.GetFullPath(Path.Combine(f.DownloadRoot, "Test Server", "levels", "a.nw"));
        Assert.Contains("file " + local, ExternalPrograms.Suppressed);
        Assert.True(window.Watches.ContainsKey(local));
        f.Fake.ClearCalls();

        await window.CheckWatchedFilesAsync();
        Assert.False(f.Fake.WasCalled("FileBrowser.UploadAsync"));
        File.WriteAllText(local, "edited locally");
        File.SetLastWriteTimeUtc(local, DateTime.UtcNow.AddMinutes(1));
        await window.CheckWatchedFilesAsync();
        RecordedCall call = f.Fake.LastCall("FileBrowser.UploadAsync")!;
        Assert.Equal("levels/a.nw", call.Argument<string>(0));
        Assert.Equal("edited locally", Encoding.ASCII.GetString(call.Argument<byte[]>(1)));
    }

    [AvaloniaFact]
    public async Task A_changed_file_without_write_rights_is_not_uploaded()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw", rights: "r"));
        f.Browser.RemoteFiles["levels/a.nw"] = Encoding.ASCII.GetBytes("first");
        await window.OpenAsync(window.Rows[0]);
        string local = Path.GetFullPath(Path.Combine(f.DownloadRoot, "Test Server", "levels", "a.nw"));
        File.WriteAllText(local, "edited");
        File.SetLastWriteTimeUtc(local, DateTime.UtcNow.AddMinutes(1));
        f.Fake.ClearCalls();
        await window.CheckWatchedFilesAsync();
        Assert.False(f.Fake.WasCalled("FileBrowser.UploadAsync"));
        Assert.Contains("No write rights for levels/a.nw", window.LogLines);
    }

    [AvaloniaFact]
    public async Task A_real_double_click_on_a_row_opens_the_file()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw"));
        f.Browser.RemoteFiles["levels/a.nw"] = [1, 2, 3];
        var list = TestUi.Find<ListBox>(window, "FileList");
        Control row = list.ContainerFromIndex(0)!;
        TestUi.Click(row);
        TestUi.Click(row);
        await TestUi.WaitUntilAsync(() => ExternalPrograms.Suppressed.Any(entry => entry.StartsWith("file ", StringComparison.Ordinal)), what: "the file to be opened");
    }

    [AvaloniaFact]
    public async Task A_script_file_opens_in_the_text_editor_instead_of_running()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("run.sh"));
        f.Browser.RemoteFiles["levels/run.sh"] = Encoding.ASCII.GetBytes("echo hi");
        await window.OpenAsync(window.Rows[0]);
        Assert.DoesNotContain(ExternalPrograms.Suppressed, entry => entry.StartsWith("file ", StringComparison.Ordinal));
        (string tool, object? argument) = Assert.Single(f.Test.OpenedTools);
        Assert.Equal(ToolIds.ScriptEditor, tool);
        Assert.Equal("echo hi", Assert.IsType<ScriptRequest>(argument).Text);
    }

    [AvaloniaFact]
    public async Task Edit_opens_the_script_editor_and_save_uploads()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("notes.txt"));
        f.Browser.RemoteFiles["levels/notes.txt"] = Encoding.UTF8.GetBytes("héllo");
        Fixture.Invoke(window.BuildMenu(["levels/notes.txt"], false), "MenuEdit");
        await TestUi.WaitUntilAsync(() => f.Test.OpenedTools.Count == 1);
        var request = Assert.IsType<ScriptRequest>(f.Test.OpenedTools[0].Argument);
        Assert.Equal(RemoteControl.Ui.Shell.ScriptKind.Text, request.Kind);
        Assert.Equal("levels/notes.txt - Test Server", request.Name);
        Assert.Equal("héllo", request.Text);
        Assert.True(await request.Save!("changed é"));
        RecordedCall call = f.Fake.LastCall("FileBrowser.UploadAsync")!;
        Assert.Equal("levels/notes.txt", call.Argument<string>(0));
        Assert.Equal("changed é", Encoding.UTF8.GetString(call.Argument<byte[]>(1)));
    }

    [AvaloniaFact]
    public async Task Edit_refuses_binary_files_and_is_not_offered_for_pictures()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("data.dat"), Fixture.File("p.png"));
        Assert.DoesNotContain("Edit", Fixture.MenuNames(window.BuildMenu(["levels/p.png"], false)));
        f.Browser.RemoteFiles["levels/data.dat"] = [0x41, 0x00, 0x42];
        await window.EditAsync("levels/data.dat");
        Assert.Empty(f.Test.OpenedTools);
        Assert.Contains("levels/data.dat is not a text file; it is not opened in the editor.", window.LogLines);
    }

    [AvaloniaFact]
    public async Task Previews_wait_behind_what_the_user_asked()
    {
        using var f = new Fixture();
        var gate = new TaskCompletionSource<bool>();
        byte[] png = Fixture.Png();
        f.Browser.DownloadHandler = async path =>
        {
            if (path.EndsWith("1.png", StringComparison.Ordinal)) await gate.Task.ConfigureAwait(true);
            return path.EndsWith(".png", StringComparison.Ordinal) ? png : [7];
        };
        FileBrowserWindow window = f.OpenWithListing("images/", Fixture.File("1.png", 100), Fixture.File("2.png", 100), Fixture.File("3.png", 100), Fixture.File("a.txt"));
        Assert.Equal("preview images/1.png", window.Queue.RunningDescription);
        Task download = window.DownloadAsync(["images/a.txt"]);
        gate.SetResult(true);
        await download;
        await TestUi.WaitUntilAsync(() => !window.Queue.IsBusy && window.Previews.Count == 3);
        string[] downloads = window.Queue.History.Where(item => item.StartsWith("preview", StringComparison.Ordinal) || item.StartsWith("download", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["preview images/1.png", "download images/a.txt", "preview images/2.png", "preview images/3.png"], downloads);

        window.ShowPreview("images/2.png");
        Assert.True(window.PreviewPopup.IsOpen);
        Shots.Save(window, "file-browser-preview-dark");
        window.ShowPreview("images/a.txt");
        Assert.False(window.PreviewPopup.IsOpen);
    }

    [AvaloniaFact]
    public async Task A_broken_picture_is_ignored()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("images/");
        f.Browser.RemoteFiles["images/broken.png"] = Encoding.ASCII.GetBytes("this is not a png");
        f.Browser.RaiseFilesReceived("images/", [Fixture.File("broken.png", 17)]);
        await TestUi.WaitUntilAsync(() => !window.Queue.IsBusy);
        Assert.Equal(0, window.Previews.Count);
        Assert.False(window.QueuePreview(window.Rows[0]));
        window.ShowPreview("images/broken.png");
        Assert.False(window.PreviewPopup.IsOpen);
        Assert.Single(f.Fake.CallsTo("FileBrowser.DownloadAsync"));
    }

    [AvaloniaFact]
    public async Task Previews_come_from_the_disk_cache_the_second_time()
    {
        using var f = new Fixture();
        f.Browser.RemoteFiles["images/p.png"] = Fixture.Png();
        FileBrowserWindow window = f.OpenWithListing("images/", Fixture.File("p.png", 100));
        await TestUi.WaitUntilAsync(() => window.Previews.Count == 1 && window.Previews.DiskFiles().Count == 1);
        window.Close();
        f.Fake.ClearCalls();
        FileBrowserWindow again = f.OpenWithListing("images/", Fixture.File("p.png", 100));
        await TestUi.WaitUntilAsync(() => again.Previews.Count == 1);
        Assert.False(f.Fake.WasCalled("FileBrowser.DownloadAsync"));
    }

    [AvaloniaFact]
    public async Task The_modern_view_follows_the_option_live()
    {
        using var f = new Fixture();
        f.Browser.RemoteFiles["levels/p.png"] = Fixture.Png(40, 40);
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("b.nw"), Fixture.File("p.png", 100));
        Assert.False(window.IsModern);
        f.Test.Environment.SetOptions(o => o with { ModernFileBrowser = true, FileBrowserThumbnails = true });
        Assert.True(window.IsModern);
        Assert.True(TestUi.Find<DockPanel>(window, "AddressRow").IsVisible);
        Assert.Equal("levels/", TestUi.Find<TextBox>(window, "AddressBox").Text);
        Assert.Equal(["sub", "b.nw", "p.png"], window.ModernItems.Select(item => item.Name));
        Assert.True(window.ModernItems[0].IsFolder);
        await TestUi.WaitUntilAsync(() => window.ModernItems[2].ImageSize == PreviewRules.ThumbnailSize, what: "the thumbnail");
        Shots.Save(window, "file-browser-modern-dark");

        TestUi.Click(TestUi.Find<Button>(window, "UpButton"));
        Assert.Equal(string.Empty, window.CurrentFolder);
        var address = TestUi.Find<TextBox>(window, "AddressBox");
        address.Text = "images";
        address.Focus();
        TestUi.PressKey(window, Avalonia.Input.Key.Enter);
        Assert.Equal("images/", window.CurrentFolder);
        f.Fake.ClearCalls();
        TestUi.Click(TestUi.Find<Button>(window, "RefreshButton"));
        Assert.Equal("images/", f.Fake.LastCall("FileBrowser.ChangeFolderAsync")!.Argument<string>(0));

        f.Test.Environment.SetOptions(o => o with { ModernFileBrowser = false });
        Assert.False(window.IsModern);
        Assert.True(TestUi.Find<TextBlock>(window, "FolderLabel").IsVisible);
    }

    [AvaloniaFact]
    public void Typing_in_the_modern_view_selects_a_match()
    {
        using var f = new Fixture(options: o => o with { ModernFileBrowser = true });
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("alpha.nw"), Fixture.File("beta.nw"), Fixture.File("gamma.nw"));
        var grid = TestUi.Find<ListBox>(window, "ModernView");
        grid.ContainerFromIndex(1)!.Focus();
        TestUi.TypeText(window, "gam");
        Assert.Equal("gamma.nw", (grid.SelectedItem as ModernItem)?.Name);
    }

    [AvaloniaFact]
    public async Task Dragging_onto_a_folder_moves_and_dropping_local_files_uploads()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw"));
        var list = TestUi.Find<ListBox>(window, "FileList");
        list.SelectedItems!.Add(window.Rows[0]);
        TestUi.Settle();
        var tree = TestUi.Find<TreeView>(window, "FolderTree");
        Control images = tree.ContainerFromIndex(0)!;
        Control row = list.ContainerFromIndex(0)!;
        TestUi.MouseDown(row);
        TestUi.MouseUp(images);
        await TestUi.WaitUntilAsync(() => f.Fake.WasCalled("FileBrowser.MoveAsync"), what: "the move");
        Assert.Equal("images/", f.Fake.LastCall("FileBrowser.MoveAsync")!.Argument<string>(1));
        Assert.Equal("images/", window.FolderAt(TestUi.CenterOf(images)));

        string local = Path.Combine(f.Test.Environment.Root, "dropped.txt");
        File.WriteAllText(local, "x");
        await window.UploadLocalFilesAsync([local], window.CurrentFolder);
        Assert.Equal("levels/dropped.txt", f.Fake.LastCall("FileBrowser.UploadAsync")!.Argument<string>(0));
    }

    [AvaloniaFact]
    public async Task Not_connected_is_a_message()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw"));
        f.Fake.IsConnected = false;
        f.Fake.ClearCalls();
        await window.DownloadAsync(["levels/a.nw"]);
        window.NavigateTo("images/");
        await TestUi.WaitUntilAsync(() => !window.Queue.IsBusy);
        Assert.False(f.Fake.WasCalled("FileBrowser.DownloadAsync"));
        Assert.False(f.Fake.WasCalled("FileBrowser.ChangeFolderAsync"));
        Assert.Contains("Not connected.", window.LogLines);
        Fixture.Invoke(window.BuildMenu(["levels/a.nw"], false), "MenuDelete");
        Assert.DoesNotContain(RcWindow.OpenWindows, item => item is RcDialog { IsVisible: true });
    }

    [AvaloniaFact]
    public void Server_messages_and_progress_are_logged_in_steps()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/");
        f.Fake.RaiseFileBrowserMessage("File uploaded: levels/a.nw");
        for (int received = 0; received <= 1000; received += 50) f.Browser.RaiseTransferProgress("levels/big.nw", received, 1000);
        Assert.Contains("File uploaded: levels/a.nw", window.LogLines);
        Assert.Equal(11, window.LogLines.Count(line => line.StartsWith("Received chunk:", StringComparison.Ordinal)));
        f.Browser.RaiseTransferFailed("levels/x.nw", "No such file.");
        Assert.Contains("Transfer failed: levels/x.nw (No such file.)", window.LogLines);
    }

    [AvaloniaFact]
    public void The_log_keeps_at_most_5000_lines()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenBrowser();
        for (int index = 0; index < 5100; index++) window.AppendLog("line " + index);
        Assert.Equal(5000, window.LogLines.Count);
        Assert.Equal("line 100", window.LogLines[0]);
    }

    [AvaloniaFact]
    public void A_reconnect_reads_the_roots_again()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw"));
        var second = new FakeRcSession("tester");
        f.Context.Replace(second, f.Test.Login);
        TestUi.Settle();
        Assert.True(second.WasCalled("FileBrowser.StartAsync"));
        Assert.Equal("levels/", second.LastCall("FileBrowser.ChangeFolderAsync")!.Argument<string>(0));
        Assert.Empty(window.Folders);
        second.FakeFileBrowser.RaiseFoldersReceived([new FileBrowserFolder("rw", "ganis/*")]);
        Assert.Equal(["ganis/"], window.Folders.Select(node => node.Label));
        f.Browser.RaiseFoldersReceived([new FileBrowserFolder("rw", "old/*")]);
        Assert.Equal(["ganis/"], window.Folders.Select(node => node.Label));
    }

    [AvaloniaFact]
    public async Task Closing_during_a_download_writes_nothing()
    {
        using var f = new Fixture();
        var gate = new TaskCompletionSource<bool>();
        f.Browser.DownloadHandler = async _ =>
        {
            await gate.Task.ConfigureAwait(true);
            return [1, 2, 3];
        };
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw"));
        Task download = window.DownloadAsync(["levels/a.nw"]);
        window.Close();
        gate.SetResult(true);
        await download;
        Assert.False(File.Exists(Path.Combine(f.DownloadRoot, "Test Server", "levels", "a.nw")));
        Assert.Equal(0, UiSafe.ReportedCount);
    }

    [AvaloniaFact]
    public void The_session_ending_closes_the_browser()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenBrowser();
        f.Context.End();
        Assert.True(window.IsClosed);
        Assert.Null(f.Context.OpenTool(ToolIds.FileBrowser));
    }

    [AvaloniaFact]
    public void The_tool_refuses_during_a_file_sync()
    {
        using var f = new Fixture();
        var status = new FakeSyncStatus { IsFileSyncActive = true };
        using IDisposable provided = f.Context.Services.Provide<ISyncStatus>(status);
        Assert.Null(f.Context.OpenTool(ToolIds.FileBrowser));
        RcDialog dialog = TestUi.FindDialog();
        Assert.Contains("not available while files are being synchronized", dialog.Message);
        dialog.Accept();
        Assert.DoesNotContain(f.Context.Windows.All, window => window is FileBrowserWindow);
    }

    [AvaloniaFact]
    public void Pictures_of_the_file_browser()
    {
        foreach (string theme in new[] { "light", "nord" })
        {
            using var f = new Fixture(theme);
            FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("start.nw", 1234), Fixture.File("world.gmap", 99), Fixture.File("serveroptions.txt", 5), Fixture.File("font.ttf", 70000), Fixture.File("pack.zip", 1_000_000), Fixture.File("script.graal", 400), Fixture.File("blob.gs2bc", 30));
            var list = TestUi.Find<ListBox>(window, "FileList");
            list.SelectedItems!.Add(window.Rows[1]);
            window.Folders[1].IsExpanded = true;
            window.AppendLog("File downloaded: levels/start.nw");
            Shots.Save(window, "file-browser-classic-" + theme);
            f.Test.Environment.SetOptions(o => o with { ModernFileBrowser = true });
            Shots.Save(window, "file-browser-modern-" + theme);
        }
    }

    private sealed class FakeSyncStatus : ISyncStatus
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
