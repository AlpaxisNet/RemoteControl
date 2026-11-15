using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Sync;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Files.Tests;

/// <summary>A session with the files group registered and started.</summary>
internal sealed class Fixture : IDisposable
{
    public Fixture(string? theme = null, Func<RcOptions, RcOptions>? options = null)
    {
        FilesSession.ConfigureEngine = engine => engine with
        {
            WatchFileSystem = false,
            FileRootsTimeout = TimeSpan.FromSeconds(2),
            FolderListTimeout = TimeSpan.FromSeconds(2),
            GitTimeout = TimeSpan.FromSeconds(10),
            GitExecutable = GitScript,
        };
        Test = new TestSession(theme, options: options);
        FilesFeature.Register(Test.Tools);
        Test.RecordTools(ToolIds.ScriptEditor);
        FilesFeature.Start(Test.Context);
        ExternalPrograms.ClearSuppressed();
    }

    public TestSession Test { get; }

    public FakeRcSession Fake => Test.Fake;

    public FakeFileBrowser Browser => Test.Fake.FakeFileBrowser;

    public SessionContext Context => Test.Context;

    public FilesSession Files => FilesSession.For(Context)!;

    /// <summary>A harmless "git" for the sync tests: a script in the test's folder that prints nothing and succeeds.</summary>
    public string? GitScript { get; private set; }

    public string DownloadRoot => Context.App.Paths.DownloadDirectory(Context.App.Options.Current);

    public FileBrowserWindow OpenBrowser(string? folder = null)
    {
        var window = (FileBrowserWindow)Context.OpenTool(ToolIds.FileBrowser, folder)!;
        TestUi.Settle();
        return window;
    }

    /// <summary>Opens the browser and shows a folder with files.</summary>
    public FileBrowserWindow OpenWithListing(string folder, params FileBrowserEntry[] files)
    {
        FileBrowserWindow window = OpenBrowser();
        Browser.RaiseFoldersReceived([new FileBrowserFolder("rw", "levels/*"), new FileBrowserFolder("r", "levels/sub/*.nw"), new FileBrowserFolder("rw", "images/*.png")]);
        window.NavigateTo(folder);
        Browser.RaiseFilesReceived(folder, files);
        TestUi.Settle();
        return window;
    }

    public static FileBrowserEntry File(string name, long size = 10, string rights = "rw", long modified = 1_700_000_000) => new(name, rights, size, modified);

    public static byte[] Png(int width = 8, int height = 6)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    public static void Invoke(ContextMenu menu, string name)
    {
        MenuItem item = menu.Items.OfType<MenuItem>().Single(entry => entry.Name == name);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        TestUi.Settle();
    }

    public static IReadOnlyList<string> MenuNames(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray();

    /// <summary>Writes the harmless git script into the test's folder.</summary>
    public void CreateGitScript()
    {
        // A shell script on Unix, a batch file on Windows (which starts .cmd files itself; it cannot start a shell script).
        string script = Path.Combine(Test.Environment.Root, OperatingSystem.IsWindows() ? "fake-git.cmd" : "fake-git.sh");
        System.IO.File.WriteAllText(script, OperatingSystem.IsWindows() ? "@exit /b 0\r\n" : "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows()) System.IO.File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitScript = script;
    }

    public void Dispose()
    {
        Test.Dispose();
        FilesSession.ConfigureEngine = null;
    }
}
