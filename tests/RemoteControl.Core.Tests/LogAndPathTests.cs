namespace RemoteControl.Core.Tests;

public sealed class AppLogTests
{
    [Fact]
    public void Keeps_the_last_lines_in_order()
    {
        var log = new AppLog(3);
        for (int index = 1; index <= 5; index++) log.Add("line " + index);
        Assert.Equal(["line 3", "line 4", "line 5"], log.Snapshot());
        Assert.Equal(["line 5"], log.Snapshot(1));
        log.Clear();
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void Is_safe_from_many_threads()
    {
        var log = new AppLog(100);
        Parallel.For(0, 2000, index => log.Info("message " + index));
        Assert.Equal(100, log.Snapshot().Count);
    }

    [Fact]
    public void Appends_to_a_file_and_ignores_an_unwritable_one()
    {
        using var folder = new TempFolder();
        var log = new AppLog { FilePath = folder.Combine("logs", "app.log") };
        log.Warn("careful", new InvalidOperationException("boom"));
        log.Error("failed", new InvalidOperationException("bang"));
        string text = File.ReadAllText(folder.Combine("logs", "app.log"));
        Assert.Contains("warn careful: InvalidOperationException: boom", text);
        Assert.Contains("error failed\nSystem.InvalidOperationException: bang", text.Replace("\r", string.Empty));

        File.WriteAllText(folder.Combine("blocker"), "x");
        var broken = new AppLog { FilePath = folder.Combine("blocker", "app.log") };
        for (int index = 0; index < 5; index++) broken.Info("still fine");
        Assert.Null(broken.FilePath);
        Assert.Equal(5, broken.Snapshot().Count);
    }
}

public sealed class CrashLogTests
{
    [Fact]
    public void Writes_exception_chain_environment_and_recent_lines()
    {
        using var folder = new TempFolder();
        var log = new AppLog(10);
        log.Add("before the crash");
        var crash = new CrashLog(folder.Combine("logs", "crash.log"), log) { Version = "1.0.0-test" };
        Exception exception;
        try
        {
            try
            {
                throw new FormatException("inner problem");
            }
            catch (FormatException inner)
            {
                throw new InvalidOperationException("outer problem", inner);
            }
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.True(crash.WriteStartup());
        Assert.True(crash.Write("unhandled_exception", exception, isTerminating: true));

        string text = File.ReadAllText(crash.FilePath);
        Assert.Contains("crash_logger=installed version=1.0.0-test", text);
        Assert.Contains("crash kind=unhandled_exception terminating=true", text);
        Assert.Contains("pid=" + Environment.ProcessId, text);
        Assert.Contains("version=1.0.0-test", text);
        Assert.Contains("os=", text);
        Assert.Contains("runtime=", text);
        Assert.Contains("System.InvalidOperationException: outer problem", text);
        Assert.Contains("System.FormatException: inner problem", text);
        Assert.Contains(nameof(Writes_exception_chain_environment_and_recent_lines), text);
        Assert.Contains("  before the crash", text);
    }

    [Fact]
    public void Aggregate_exceptions_list_every_inner_exception()
    {
        var crash = new CrashLog(Path.Combine(Path.GetTempPath(), "unused.log"), new AppLog(1), 0);
        string text = crash.Format("unobserved_task", new AggregateException(new IOException("first"), new TimeoutException("second")));
        Assert.Contains("System.IO.IOException: first", text);
        Assert.Contains("System.TimeoutException: second", text);
        Assert.Contains("recent log (0 lines)", text);
    }

    [Fact]
    public void Never_throws_when_the_file_cannot_be_written_or_from_many_threads()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.Combine("blocker"), "x");
        var broken = new CrashLog(folder.Combine("blocker", "crash.log"));
        Assert.False(broken.Write("unhandled_exception", new InvalidOperationException("x")));
        Assert.False(broken.Write("unhandled_exception", (object?)"not an exception", false));

        var crash = new CrashLog(folder.Combine("crash.log"), new AppLog(5));
        Parallel.For(0, 20, index => crash.Write("unobserved_task", new InvalidOperationException("parallel " + index)));
        string text = File.ReadAllText(folder.Combine("crash.log"));
        Assert.Equal(20, text.Split("crash kind=unobserved_task").Length - 1);
    }

    [Fact]
    public void Installed_handlers_can_be_removed()
    {
        using var folder = new TempFolder();
        var crash = new CrashLog(folder.Combine("crash.log"));
        IDisposable handlers = crash.InstallProcessHandlers();
        handlers.Dispose();
        handlers.Dispose();
    }
}

public sealed class AppPathsTests
{
    [Fact]
    public void Everything_lives_in_the_user_folder_except_bundled_assets()
    {
        using var install = new TestInstall(withBackground: false);
        AppPaths paths = install.Paths;
        string user = Path.GetFullPath(install.UserDirectory);
        foreach (string path in new[] { paths.OptionsFile, paths.AccountsKeyFile, paths.AccountsDataFile, paths.SelectedAccountFile, paths.ListServersFile, paths.CrashLogFile, paths.AppLogFile, paths.PmHistoryDirectory, paths.PreviewCacheDirectory, paths.EditorCacheDirectory, paths.BackupDirectory, paths.McpAuditLogFile })
            Assert.StartsWith(user + Path.DirectorySeparatorChar, path);
        Assert.Equal(Path.Combine(Path.GetFullPath(install.ProgramDirectory), "control2config.txt"), paths.BundledOptionsFile);
        Assert.Equal(Path.Combine(Path.GetFullPath(install.ProgramDirectory), "images"), paths.ImagesDirectory);
    }

    [Fact]
    public void Relative_option_values_resolve_against_the_user_folder()
    {
        using var install = new TestInstall(withBackground: false);
        AppPaths paths = install.Paths;
        string absolute = Path.Combine(Path.GetTempPath(), "absolute-downloads");
        Assert.Equal(Path.Combine(paths.UserDirectory, "logs", "log.txt"), paths.ChatLogFile(RcOptions.Defaults));
        Assert.Equal(Path.Combine(paths.UserDirectory, "Downloads"), paths.DownloadDirectory(RcOptions.Defaults));
        Assert.Equal(Path.Combine(paths.UserDirectory, "ExternalEditor"), paths.ExternalEditorDirectory(RcOptions.Defaults with { ExternalEditorWorkspace = "" }));
        Assert.Equal(absolute, paths.DownloadDirectory(RcOptions.Defaults with { DownloadFolder = absolute }));
        Assert.Equal(Path.Combine(paths.UserDirectory, "a", "b"), paths.ResolveUserPath("a/../a/b"));
    }

    [Fact]
    public void File_names_are_escaped_like_the_cpp()
    {
        Assert.Equal("Staff%032Name%046", AppPaths.EscapeFileName("Staff Name."));
        Assert.Equal("%195%169", AppPaths.EscapeFileName("é"));
        using var install = new TestInstall(withBackground: false);
        Assert.Equal(Path.Combine(install.Paths.PmHistoryDirectory, "Bob%095.txt"), install.Paths.PmHistoryFile("Bob_"));
    }

    [Fact]
    public void Default_user_folder_honours_xdg_config_home()
    {
        if (OperatingSystem.IsWindows()) return;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/xdg/config");
            Assert.Equal("/xdg/config/GScriptRC", AppPaths.DefaultUserDirectory());
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "relative/ignored");
            Assert.EndsWith("/.config/GScriptRC", AppPaths.DefaultUserDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
        }
    }
}
