using System.Text;
using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Files.Tests;

public sealed class LogicTests
{
    [Theory]
    [InlineData("levels", "levels/")]
    [InlineData("/levels/", "levels/")]
    [InlineData("\\levels\\sub", "levels/sub/")]
    [InlineData("levels//sub///", "levels/sub/")]
    [InlineData("///", "")]
    [InlineData(".", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Folders_are_normalised(string? input, string expected) => Assert.Equal(expected, RemotePaths.NormalizeFolder(input));

    [Fact]
    public void Parent_and_names()
    {
        Assert.Equal("levels/", RemotePaths.Parent("levels/sub/"));
        Assert.Equal(string.Empty, RemotePaths.Parent("levels/"));
        Assert.Equal("a.nw", RemotePaths.NameOf("levels/a.nw"));
        Assert.Equal("levels/", RemotePaths.FolderOf("levels/a.nw"));
        Assert.True(RemotePaths.Matches("a.nw", "levels/a.nw"));
        Assert.True(RemotePaths.Matches("levels/a.nw", "a.nw"));
        Assert.False(RemotePaths.Matches("b.nw", "levels/a.nw"));
    }

    [Fact]
    public void Tree_is_built_from_the_folder_rules()
    {
        var roots = FolderTree.Build([
            new FileBrowserFolder("rw", "levels/*"),
            new FileBrowserFolder("r", "levels/sub/*.nw"),
            new FileBrowserFolder("rw", "images/*.png"),
            new FileBrowserFolder("r", "*"),
            new FileBrowserFolder("r", "ganis/"),
        ]);
        Assert.Equal(["ganis/", "images/", "levels/"], roots.Select(node => node.Label));
        FolderNode levels = roots[2];
        Assert.Equal("rw", levels.Rights);
        Assert.Equal("levels/", levels.Folder);
        FolderNode sub = Assert.Single(levels.Children);
        Assert.Equal("sub/", sub.Label);
        Assert.Equal("levels/sub", sub.FullPath);
        Assert.Equal("r", sub.Rights);
        Assert.Equal(["images", "levels", "levels/sub", "ganis"], RemotePaths.FoldersOfRules([
            new FileBrowserFolder("rw", "images/*.png"), new FileBrowserFolder("rw", "levels/*"), new FileBrowserFolder("r", "levels/sub/*"), new FileBrowserFolder("r", "ganis/"), new FileBrowserFolder("r", "images/*.gif")]));
        Assert.Equal(["sub"], RemotePaths.SubfoldersOf(["images", "levels", "levels/sub/deep"], "levels/"));
    }

    [Theory]
    [InlineData("server_config.txt", "rcfiles_conf.png")]
    [InlineData("levels/mysettings.nw", "rcfiles_conf.png")]
    [InlineData("OPTIONS.png", "rcfiles_conf.png")]
    [InlineData("a.bin", "rcfiles_binary.png")]
    [InlineData("a.gs2bc", "rcfiles_binary.png")]
    [InlineData("readme.TXT", "rcfiles_text2.png")]
    [InlineData("font.ttf", "rcfiles_ttf.png")]
    [InlineData("font.otf", "rcfiles_ttf.png")]
    [InlineData("a.tar", "rcfiles_archive.png")]
    [InlineData("a.zip", "rcfiles_archive.png")]
    [InlineData("a.rar", "rcfiles_archive.png")]
    [InlineData("a.7z", "rcfiles_archive.png")]
    [InlineData("server.conf", "rcfiles_conf.png")]
    [InlineData("world.gmap", "rcfiles_gmap.png")]
    [InlineData("script.graal", "rcfiles_graal.png")]
    [InlineData("level.nw", "rcfiles_nw.png")]
    [InlineData("picture.png", "rcfiles_unknown.png")]
    [InlineData("noextension", "rcfiles_unknown.png")]
    public void File_type_icons_follow_the_cpp(string name, string icon) => Assert.Equal(icon, AssetStore.FileTypeIconName(name));

    [Theory]
    [InlineData("a.png", 100, true)]
    [InlineData("a.JPG", 100, true)]
    [InlineData("a.jpeg", 100, true)]
    [InlineData("a.webp", 100, true)]
    [InlineData("a.bmp", 100, true)]
    [InlineData("a.ico", 100, true)]
    [InlineData("a.gif", 100, false)]
    [InlineData("a.mng", 100, false)]
    [InlineData("a.png", 0, false)]
    [InlineData("a.nw", 100, false)]
    [InlineData("a.png", 512 * 1024, true)]
    [InlineData("a.png", 512 * 1024 + 1, false)]
    [InlineData("pics_tileset.png", 128 * 1024, true)]
    [InlineData("pics_tileset.png", 128 * 1024 + 1, false)]
    [InlineData("pics_tiles.png", 200 * 1024, false)]
    [InlineData("pics-tiles.png", 200 * 1024, false)]
    public void Preview_eligibility(string name, long size, bool expected) => Assert.Equal(expected, PreviewRules.CanAutoPreview(name, size));

    [Fact]
    public void Gif_and_mng_are_pictures_but_not_previewed_automatically()
    {
        Assert.True(PreviewRules.IsImage("anim.gif"));
        Assert.True(PreviewRules.IsImage("anim.mng"));
        Assert.False(PreviewRules.CanAutoPreview("anim.gif", 10));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("levels/../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x")]
    [InlineData("levels/a:stream")]
    [InlineData("")]
    public void A_name_that_leaves_the_download_folder_is_refused(string remote)
    {
        string root = Path.Combine(Path.GetTempPath(), "rc-files-tests-" + Guid.NewGuid().ToString("N"));
        Assert.False(LocalCopies.TryTarget(root, "Server", remote, out _, out string reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void A_download_goes_under_server_and_folders()
    {
        string root = Path.Combine(Path.GetTempPath(), "rc-files-tests");
        Assert.True(LocalCopies.TryTarget(root, "My:Server?", "levels/sub/a.nw", out string path, out _));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "My_Server_", "levels", "sub", "a.nw")), path);
        Assert.Equal("Server", LocalCopies.SafeComponent("..", "Server"));
    }

    [Fact]
    public void Runnable_files_are_not_handed_to_the_desktop()
    {
        foreach (string name in new[] { "a.exe", "a.bat", "a.sh", "a.cmd", "a.vbs", "a.ps1", "a.js", "a.lnk", "a.jar", "a.desktop" }) Assert.True(LocalCopies.IsRunnable(name), name);
        foreach (string name in new[] { "a.png", "a.nw", "a.txt", "a.gmap" }) Assert.False(LocalCopies.IsRunnable(name), name);
    }

    [Fact]
    public void Editable_text_keeps_its_encoding()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("héllo");
        EditableText text = EditableText.Decode(utf8)!;
        Assert.True(text.Utf8);
        Assert.Equal(utf8, text.Encode("héllo"));
        byte[] latin = [0x68, 0xE9, 0x80];
        EditableText windows = EditableText.Decode(latin)!;
        Assert.False(windows.Utf8);
        Assert.Equal("hé€", windows.Text);
        Assert.Equal(latin, windows.Encode(windows.Text));
        byte[] bom = [0xEF, 0xBB, 0xBF, 0x61];
        EditableText withBom = EditableText.Decode(bom)!;
        Assert.Equal("a", withBom.Text);
        Assert.Equal(bom, withBom.Encode("a"));
        Assert.Null(EditableText.Decode([0x50, 0x4E, 0x00, 0x47]));
    }

    [AvaloniaFact]
    public async Task The_queue_runs_one_at_a_time_with_user_requests_before_previews()
    {
        using var queue = new TransferQueue();
        var gate = new TaskCompletionSource<bool>();
        int running = 0;
        int most = 0;
        var order = new List<string>();
        Func<CancellationToken, Task<bool>> Job(string name, Task? wait = null) => async _ =>
        {
            running++;
            most = Math.Max(most, running);
            order.Add(name);
            if (wait is not null) await wait.ConfigureAwait(true);
            else await Task.Yield();
            running--;
            return true;
        };

        Task<bool> first = queue.Enqueue(TransferPriority.Preview, "preview 1", Job("preview 1", gate.Task), false);
        Task<bool> second = queue.Enqueue(TransferPriority.Preview, "preview 2", Job("preview 2"), false);
        Task<bool> user1 = queue.Enqueue(TransferPriority.User, "user 1", Job("user 1"), false);
        Task<bool> user2 = queue.Enqueue(TransferPriority.User, "user 2", Job("user 2"), false);
        Assert.True(queue.IsBusy);
        Assert.Equal(3, queue.PendingCount);
        gate.SetResult(true);
        await TestUi.WaitUntilAsync(() => second.IsCompleted && user1.IsCompleted && user2.IsCompleted && first.IsCompleted);
        Assert.Equal(["preview 1", "user 1", "user 2", "preview 2"], order);
        Assert.Equal(1, most);
        Assert.False(queue.IsBusy);
    }

    [AvaloniaFact]
    public async Task Clearing_previews_drops_the_waiting_ones()
    {
        using var queue = new TransferQueue();
        var gate = new TaskCompletionSource<bool>();
        Task<bool> first = queue.Enqueue(TransferPriority.Preview, "preview 1", async _ => await gate.Task.ConfigureAwait(true), false);
        Task<bool> second = queue.Enqueue(TransferPriority.Preview, "preview 2", _ => Task.FromResult(true), false);
        Assert.True(queue.HasPreview("preview 2"));
        queue.ClearPreviews();
        Assert.False(await second);
        gate.SetResult(true);
        Assert.True(await first);
        queue.Dispose();
        Assert.False(await queue.Enqueue(TransferPriority.User, "late", _ => Task.FromResult(true), false));
    }

    [AvaloniaFact]
    public async Task The_preview_cache_is_bounded()
    {
        using var env = new TestEnvironment();
        string folder = Path.Combine(env.Root, "previews");
        var cache = new PreviewCache(folder, diskCapacity: 3);
        PreviewImage image = PreviewDecoder.Decode("a.png", Fixture.Png())!;
        for (int index = 0; index < PreviewCache.MemoryCapacity + 10; index++) cache.Put("key" + index, image);
        Assert.Equal(PreviewCache.MemoryCapacity, cache.Count);
        Assert.Null(cache.Get("key0"));
        Assert.NotNull(cache.Get("key59"));
        for (int index = 0; index < 5; index++) await cache.WriteAsync("disk" + index, [1, 2, 3]);
        Assert.Equal(3, cache.DiskFiles().Count);
        Assert.NotNull(await cache.ReadAsync("disk4"));
        Assert.Null(await cache.ReadAsync("missing"));
        Assert.NotEqual(PreviewCache.KeyOf("s", "a.png", 1, 2), PreviewCache.KeyOf("s", "a.png", 1, 3));
    }

    [AvaloniaFact]
    public void Pictures_decode_and_broken_ones_give_nothing()
    {
        PreviewImage? image = PreviewDecoder.Decode("a.png", Fixture.Png(720, 260));
        Assert.NotNull(image);
        Assert.Equal(360, image!.Preview.PixelSize.Width);
        Assert.Equal(88, image.Thumbnail.PixelSize.Width);
        Assert.Null(PreviewDecoder.Decode("broken.png", Encoding.ASCII.GetBytes("not a picture")));
        Assert.Null(PreviewDecoder.Decode("empty.png", []));
        Assert.Null(PreviewDecoder.Decode("broken.mng", [0x8A, 0x4D, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2]));
    }
}
