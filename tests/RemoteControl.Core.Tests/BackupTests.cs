namespace RemoteControl.Core.Tests;

public sealed class EditorBackupTests
{
    [Fact]
    public async Task Writes_originals_and_modified_texts_like_the_cpp()
    {
        using var folder = new TempFolder();
        var backup = new EditorBackup(folder.Path, "My Server");
        Assert.True(await backup.WriteAsync("weapon", "-System/Tools", "original text", modified: false));
        Assert.True(await backup.WriteAsync("serveroptions", "", "options", modified: true));
        Assert.Equal("original text", File.ReadAllText(Path.Combine(folder.Path, "My%032Server", "original", "weapon", "%045System%047Tools.txt")));
        Assert.Equal("options", File.ReadAllText(Path.Combine(folder.Path, "My%032Server", "modified", "serveroptions", "serveroptions.txt")));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("npcdump")]
    [InlineData("banhistory")]
    [InlineData("staffactivity")]
    [InlineData("localbans")]
    [InlineData("")]
    public async Task Skips_the_types_the_cpp_skipped(string type)
    {
        using var folder = new TempFolder();
        Assert.False(await new EditorBackup(folder.Path, "Server").WriteAsync(type, "x", "y", false));
        Assert.Empty(Directory.GetFileSystemEntries(folder.Path));
    }

    [Fact]
    public async Task Writes_nothing_without_a_server()
    {
        using var folder = new TempFolder();
        Assert.False(await new EditorBackup(folder.Path, "").WriteAsync("weapon", "x", "y", false));
    }
}

public sealed class GitBackupTests
{
    private static Dictionary<string, string> IsolatedEnvironment(string folder) => new()
    {
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_CONFIG_GLOBAL"] = Path.Combine(folder, "no-global-config"),
        ["HOME"] = folder,
        ["XDG_CONFIG_HOME"] = Path.Combine(folder, "xdg"),
        ["GIT_AUTHOR_NAME"] = "Test",
        ["GIT_AUTHOR_EMAIL"] = "test@example.invalid",
        ["GIT_COMMITTER_NAME"] = "Test",
        ["GIT_COMMITTER_EMAIL"] = "test@example.invalid",
    };

    [Fact]
    public async Task A_missing_git_is_a_result_not_an_exception()
    {
        using var folder = new TempFolder();
        GitResult result = await new GitBackup(folder.Path, Path.Combine(folder.Path, "no-such-git")).RunAsync(["status"]);
        Assert.False(result.Success);
        Assert.Equal("Git executable was not found.", result.Output);
        GitResult noWorkspace = await new GitBackup("", "/bin/true").RunAsync(["status"]);
        Assert.Equal("Choose a sync workspace first.", noWorkspace.Output);
    }

    [Fact]
    public async Task Credentials_in_the_upstream_url_are_refused()
    {
        using var folder = new TempFolder();
        var git = new GitBackup(folder.Path, Path.Combine(folder.Path, "no-such-git")) { RemoteUrl = "https://user:secret@example.invalid/repo.git" };
        GitResult result = await git.PushAsync();
        Assert.Equal("Credentials must not be embedded in the upstream URL.", result.Output);
    }

    [Fact]
    public async Task Init_commit_and_remote_in_a_temporary_repository()
    {
        string? gitPath = GitBackup.FindGit(AppContext.BaseDirectory);
        if (gitPath is null) return;
        using var folder = new TempFolder();
        string workspace = folder.Combine("workspace");
        foreach (string sub in new[] { "weapons", "classes", "npcs", "files", "config", ".graal-sync" }) Directory.CreateDirectory(Path.Combine(workspace, sub));
        File.WriteAllText(Path.Combine(workspace, "weapons", "-System.txt"), "//#CLIENTSIDE");
        File.WriteAllText(Path.Combine(workspace, ".graal-sync", "manifest.conf"), "x");
        var git = new GitBackup(workspace, gitPath) { Environment = IsolatedEnvironment(folder.Path), RemoteUrl = Path.Combine(folder.Path, "upstream.git"), Timeout = TimeSpan.FromSeconds(30) };

        GitResult init = await git.InitializeAsync();
        Assert.True(init.Success, init.Output);
        GitResult commit = await git.CommitAsync("Test Server");
        Assert.True(commit.Success, commit.Output);
        GitResult log = await git.RunAsync(["log", "--format=%s"]);
        Assert.Equal("[graal-sync] Test Server", log.Output);
        GitResult origin = await git.RunAsync(["remote", "get-url", "origin"]);
        Assert.Equal(Path.Combine(folder.Path, "upstream.git"), origin.Output);
        GitResult nothing = await git.CommitAsync("Test Server");
        Assert.False(nothing.Success);
    }

    [Fact]
    public async Task A_command_that_takes_too_long_is_stopped()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh")) return;
        using var folder = new TempFolder();
        string fakeGit = folder.Combine("slow-git");
        File.WriteAllText(fakeGit, "#!/bin/sh\nexec sleep 30\n");
        File.SetUnixFileMode(fakeGit, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var git = new GitBackup(folder.Path, fakeGit) { Timeout = TimeSpan.FromMilliseconds(300) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        GitResult result = await git.RunAsync(["status"]);
        Assert.True(result.TimedOut);
        Assert.False(result.Success);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }
}
