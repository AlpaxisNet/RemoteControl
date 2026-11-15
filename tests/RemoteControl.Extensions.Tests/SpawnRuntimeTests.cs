using System.Collections.Concurrent;

namespace RemoteControl.Extensions.Tests;

/// <summary>
/// Port of tests/GSpawnRuntimeTests.cpp (a shell command and <c>git init</c> can be launched) and of the Windows-only GS2Engine host smoke
/// test of tests/ExtensionsTests.cpp, on the C# process runtime.
/// </summary>
public sealed class SpawnRuntimeTests
{
    private sealed class Recorder
    {
        public ConcurrentQueue<string> Output { get; } = new();

        public ConcurrentQueue<string> Problems { get; } = new();

        public TaskCompletionSource<int?> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExtensionRuntime.Callbacks Callbacks => new((_, line) => Output.Enqueue(line), (_, line) => Output.Enqueue("err: " + line), (_, code) => Exit.TrySetResult(code), (_, message) => Problems.Enqueue(message));
    }

    private static ExtensionRuntime Create(string folder, Recorder recorder, params string[] arguments) =>
        new(folder, arguments, recorder.Callbacks, 16, 1024 * 1024, 1024 * 1024);

    [Fact]
    public async Task AShellCommandRunsAndExitsWithZero()
    {
        using var folder = new TempFolder();
        var recorder = new Recorder();
        ExtensionRuntime runtime = OperatingSystem.IsWindows()
            ? Create(folder.Path, recorder, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe"), "/c", "exit", "0")
            : Create(folder.Path, recorder, "/bin/sh", "-c", "exit 0");
        try
        {
            Assert.Null(await Wait.WithTimeout(runtime.StartAsync()));
            Assert.Equal(0, await Wait.WithTimeout(recorder.Exit.Task));
        }
        finally
        {
            await Wait.WithTimeout(runtime.StopAsync(Array.Empty<string>(), TimeSpan.Zero));
        }
    }

    [Fact]
    public async Task GitInitCreatesARepository()
    {
        string? git = Processes.Find(OperatingSystem.IsWindows() ? "git.exe" : "git");
        if (git is null) return;
        using var folder = new TempFolder();
        string workspace = folder.Combine("git-spawn-runtime-test");
        Directory.CreateDirectory(workspace);
        var recorder = new Recorder();
        ExtensionRuntime runtime = Create(folder.Path, recorder, git, "-C", workspace, "init", "--quiet");
        try
        {
            Assert.Null(await Wait.WithTimeout(runtime.StartAsync()));
            Assert.Equal(0, await Wait.WithTimeout(recorder.Exit.Task));
            Assert.True(Directory.Exists(Path.Combine(workspace, ".git")));
        }
        finally
        {
            await Wait.WithTimeout(runtime.StopAsync(Array.Empty<string>(), TimeSpan.Zero));
        }
    }

    [Fact]
    public async Task AMissingProgramIsAnErrorNotAnException()
    {
        using var folder = new TempFolder();
        var recorder = new Recorder();
        ExtensionRuntime runtime = Create(folder.Path, recorder, folder.Combine("no-such-program"));
        string? error = await Wait.WithTimeout(runtime.StartAsync());
        Assert.StartsWith("Failed to launch extension process", error);
        Assert.False(runtime.TrySend("{}", out string sendError));
        Assert.Equal("Extension runtime is not running", sendError);
        await Wait.WithTimeout(runtime.StopAsync(Array.Empty<string>(), TimeSpan.Zero));
        Assert.Equal("Extension command is empty", await Create(folder.Path, new Recorder(), "").StartAsync());
    }

    [Fact]
    public async Task LongLinesAndInvalidUtf8AreBounded()
    {
        if (OperatingSystem.IsWindows()) return;
        using var folder = new TempFolder();
        var recorder = new Recorder();
        var runtime = new ExtensionRuntime(folder.Path, new[] { "/bin/sh", "-c", "head -c 5000 /dev/zero | tr '\\0' 'a'; echo; printf 'bad \\377\\376 bytes\\n'; printf 'no newline at end'" }, recorder.Callbacks, 4, 1024, 100);
        try
        {
            Assert.Null(await Wait.WithTimeout(runtime.StartAsync()));
            await Wait.WithTimeout(recorder.Exit.Task);
            string[] lines = recorder.Output.ToArray();
            Assert.Equal(new string('a', 100), lines[0]);
            Assert.Equal("bad �� bytes", lines[1]);
            Assert.Equal("no newline at end", lines[2]);
            Assert.Contains(recorder.Problems, problem => problem.Contains("longer than 100 bytes", StringComparison.Ordinal));
        }
        finally
        {
            await Wait.WithTimeout(runtime.StopAsync(Array.Empty<string>(), TimeSpan.Zero));
        }
    }

    // The C++ ran this only on Windows when GS2ENGINE_EXTENSION_HOST names a real host; it is kept with the same condition (any system).
    [Fact]
    public async Task RealGs2EngineHostSmoke()
    {
        string? host = Environment.GetEnvironmentVariable("GS2ENGINE_EXTENSION_HOST");
        if (string.IsNullOrEmpty(host) || !File.Exists(host)) return;
        using var folder = new TempFolder();
        string entry = folder.Write("smoke/main.gs2", "echo(\"RC GS2 extension smoke ready\");\n");
        var recorder = new Recorder();
        ExtensionRuntime runtime = Create(Path.GetDirectoryName(host)!, recorder, host, "--extension-stdio");
        try
        {
            Assert.Null(await Wait.WithTimeout(runtime.StartAsync()));
            await Wait.UntilAsync(() => recorder.Output.Any(line => line.Contains("\"type\":\"ready\"", StringComparison.Ordinal)), "ready");
            string start = "{\"id\":\"1\",\"method\":\"start\",\"params\":{\"extensionId\":\"test.extension\",\"entry\":" + System.Text.Json.JsonSerializer.Serialize(entry) + "}}";
            Assert.True(runtime.TrySend(start, out _));
            await Wait.UntilAsync(() => recorder.Output.Any(line => line.Contains("RC GS2 extension smoke ready", StringComparison.Ordinal)), "the smoke output");
            Assert.True(runtime.TrySend("{\"id\":\"2\",\"method\":\"shutdown\"}", out _));
            await Wait.WithTimeout(recorder.Exit.Task, TimeSpan.FromSeconds(5));
        }
        finally
        {
            await Wait.WithTimeout(runtime.StopAsync(Array.Empty<string>(), TimeSpan.Zero));
        }
    }
}
