namespace RemoteControl.Core.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public void Writes_and_replaces_whole_files()
    {
        using var folder = new TempFolder();
        string path = folder.Combine("sub", "file.txt");
        AtomicFile.WriteAllText(path, "first");
        AtomicFile.WriteAllText(path, "second");
        Assert.Equal("second", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(folder.Combine("sub")));
    }

    [Fact]
    public void A_failing_write_leaves_the_old_file_intact_and_no_temporary_file()
    {
        using var folder = new TempFolder();
        string path = folder.Combine("options.txt");
        File.WriteAllText(path, "precious");
        Assert.Throws<IOException>(() => AtomicFile.Write(path, stream =>
        {
            stream.Write("half of the new content"u8);
            throw new IOException("disk full");
        }));
        Assert.Equal("precious", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void A_write_into_a_missing_unwritable_place_throws_and_changes_nothing()
    {
        using var folder = new TempFolder();
        string blocker = folder.Combine("blocker");
        File.WriteAllText(blocker, "a file where a folder should be");
        Assert.ThrowsAny<IOException>(() => AtomicFile.WriteAllText(Path.Combine(blocker, "x.txt"), "data"));
        Assert.Equal("a file where a folder should be", File.ReadAllText(blocker));
    }

    [Fact]
    public void TryCreateNew_never_replaces()
    {
        using var folder = new TempFolder();
        string path = folder.Combine("accounts.key");
        Assert.True(AtomicFile.TryCreateNew(path, [1, 2, 3], userOnly: true));
        Assert.False(AtomicFile.TryCreateNew(path, [4, 5, 6], userOnly: true));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(folder.Path));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void MoveAside_picks_a_free_name()
    {
        using var folder = new TempFolder();
        string path = folder.Combine("a.dat");
        var now = new DateTime(2026, 5, 6, 7, 8, 9);
        File.WriteAllText(path, "one");
        Assert.Equal(path + ".old-20260506-070809", AtomicFile.MoveAside(path, "old", now));
        File.WriteAllText(path, "two");
        Assert.Equal(path + ".old-20260506-070809-1", AtomicFile.MoveAside(path, "old", now));
        Assert.Null(AtomicFile.MoveAside(path, "old", now));
    }
}

public sealed class SafePathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "rc-safe-root");

    [Theory]
    [InlineData("file.txt", "file.txt")]
    [InlineData("levels/house.nw", "levels/house.nw")]
    [InlineData("levels\\sub\\a.png", "levels/sub/a.png")]
    [InlineData("./a//b/./c.txt", "a/b/c.txt")]
    [InlineData("icon.d/file", "icon.d/file")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("..hidden", "..hidden")]
    public void Accepts_relative_paths(string relative, string expected)
    {
        string path = SafePath.Combine(Root, relative);
        Assert.Equal(Path.GetFullPath(Path.Combine(Root, expected.Replace('/', Path.DirectorySeparatorChar))), path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("a/..")]
    [InlineData("..\\escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32")]
    [InlineData("C:\\file.txt")]
    [InlineData("c:file.txt")]
    [InlineData("a/b:stream")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("dir/COM1.log")]
    [InlineData("LPT9")]
    [InlineData("con.d/file")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("bad\u0001name")]
    [InlineData("what?.txt")]
    [InlineData(".")]
    [InlineData("./")]
    public void Refuses_unsafe_paths(string relative)
    {
        Assert.False(SafePath.TryCombine(Root, relative, out string? path, out string? reason));
        Assert.Null(path);
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.Throws<ArgumentException>(() => SafePath.Combine(Root, relative));
    }
}
