using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Files.Tests;

public sealed class FileBrowserDropTests
{
    [AvaloniaFact]
    public async Task Files_dropped_from_the_desktop_are_uploaded_into_the_folder_shown()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw"));
        Assert.True(DragDrop.GetAllowDrop(window));
        string local = Path.Combine(f.Test.Environment.Root, "dropped.nw");
        File.WriteAllText(local, "GLEVNW01");
        IFileDropTarget target = window;

        Assert.True(target.FileDragOver(new Point(1, 1)));
        Border hint = TestUi.Find<Border>(window, "DropHint");
        Assert.True(hint.IsVisible);
        Assert.Equal("Drop to upload into levels/", TestUi.Find<TextBlock>(window, "DropHintText").Text);
        Shots.Save(window, "file-browser-drop-hint");

        target.FilesDropped(new Point(1, 1), [local]);
        Assert.False(hint.IsVisible);
        await TestUi.WaitUntilAsync(() => f.Fake.WasCalled("FileBrowser.UploadAsync"), what: "the upload");
        RecordedCall call = f.Fake.LastCall("FileBrowser.UploadAsync")!;
        Assert.Equal("levels/dropped.nw", call.Argument<string>(0));
        Assert.Equal("GLEVNW01"u8.ToArray(), call.Argument<byte[]>(1));
        Assert.Contains("Uploading the dropped file into levels/", window.LogLines);
    }

    [AvaloniaFact]
    public void Without_a_folder_or_a_connection_a_drop_is_refused()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenBrowser();
        IFileDropTarget target = window;
        Assert.False(target.FileDragOver(new Point(1, 1)));
        Assert.False(TestUi.Find<Border>(window, "DropHint").IsVisible);
        target.FilesDropped(new Point(1, 1), [Path.Combine(f.Test.Environment.Root, "x.nw")]);
        Assert.Contains(window.LogLines, line => line.StartsWith("Open a folder first", StringComparison.Ordinal));
        Assert.False(f.Fake.WasCalled("FileBrowser.UploadAsync"));

        window.NavigateTo("levels/");
        f.Browser.RaiseFilesReceived("levels/", [Fixture.File("a.nw")]);
        f.Fake.IsConnected = false;
        Assert.False(target.FileDragOver(new Point(1, 1)));
    }

    [AvaloniaFact]
    public async Task A_dropped_folder_is_named_and_the_files_beside_it_still_go()
    {
        using var f = new Fixture();
        FileBrowserWindow window = f.OpenWithListing("levels/", Fixture.File("a.nw"));
        string folder = Directory.CreateDirectory(Path.Combine(f.Test.Environment.Root, "a folder")).FullName;
        string file = Path.Combine(f.Test.Environment.Root, "b.nw");
        File.WriteAllText(file, "b");
        ((IFileDropTarget)window).FilesDropped(new Point(1, 1), [folder, file]);
        await TestUi.WaitUntilAsync(() => f.Fake.WasCalled("FileBrowser.UploadAsync"), what: "the upload");
        Assert.Contains("Folders cannot be uploaded (the server takes files): " + folder, window.LogLines);
        Assert.Equal("levels/b.nw", f.Fake.LastCall("FileBrowser.UploadAsync")!.Argument<string>(0));
    }
}
