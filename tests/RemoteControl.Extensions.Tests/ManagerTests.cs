using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace RemoteControl.Extensions.Tests;

/// <summary>The extension manager with real child processes (python3) speaking JSON lines.</summary>
public sealed class ManagerTests
{
    private const string EchoScript = """
        import sys, json, os, time
        out = sys.stdout
        def say(value):
            out.write(json.dumps(value) + "\n")
            out.flush()
        say({"type": "status", "state": "booting"})
        for raw in sys.stdin:
            line = raw.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except Exception:
                say({"display": "bad json: " + line})
                continue
            if msg.get("type") == "start":
                say({"display": "started " + msg.get("protocol", "")})
                say({"type": "progress", "value": 5})
                sys.stderr.write("stderr line\n")
                sys.stderr.flush()
                print("plain output", flush=True)
            elif msg.get("type") == "stop":
                say({"display": "stopping"})
                break
            elif msg.get("method") == "window/action":
                params = msg["params"]
                ctx = params["context"]
                if params["action"] == "crash":
                    os._exit(3)
                say({"display": "action %s id=%s name=%s type=%s kind=%s window=%s title=%s len=%d sel=%s" % (params["action"], msg["id"], ctx["scriptName"], ctx["scriptType"], ctx["kind"], ctx["windowId"], ctx["title"], len(ctx["text"]), ctx["selection"])})
        """;

    private const string DeafScript = """
        import sys, time, json
        print(json.dumps({"display": "deaf"}), flush=True)
        time.sleep(60)
        """;

    private const string SpamScript = """
        import sys, json
        n = 0
        while True:
            n += 1
            sys.stdout.write(json.dumps({"display": "spam %d" % n}) + "\n")
            sys.stdout.flush()
        """;

    private const string IgnoreStopScript = """
        import sys, json, signal, time
        print(json.dumps({"display": "stubborn"}), flush=True)
        for raw in sys.stdin:
            pass
        time.sleep(60)
        """;

    private const string HostScript = """
        #!/bin/sh
        echo '{"type":"ready"}'
        while IFS= read -r line; do
          echo "{\"display\":\"host got: $(printf '%s' "$line" | sed 's/"/'"'"'/g')\"}"
          case "$line" in *shutdown*) exit 0;; esac
        done
        """;

    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<int> _pids = new();

        public Harness(TempFolder folder, Action<Action>? post = null)
        {
            Folder = folder;
            Program = folder.Combine("program-extensions");
            User = folder.Combine("user-extensions");
            Directory.CreateDirectory(Program);
            Directory.CreateDirectory(User);
            StateFile = folder.Combine("user", "extensions.state");
            Manager = new ExtensionManager(new ExtensionManagerOptions { PackageRoots = new[] { Program, User }, StateFile = StateFile, StopTimeout = TimeSpan.FromMilliseconds(500), MaximumQueuedMessages = 8, MaximumQueuedBytes = 4 * 1024 * 1024 }, post ?? (action => action()));
            Manager.LogLine += (_, e) => { Log.Enqueue(e.ExtensionId + ": " + e.Line); EventCount.Increment(); };
            Manager.OutputLine += (_, e) => Tab.Enqueue(e.ExtensionName + ": " + e.Line);
            Manager.OutputTabOpened += (_, e) => Tabs.Enqueue("open " + e.ExtensionName + " " + e.Backlog.Count);
            Manager.OutputTabClosed += (_, e) => Tabs.Enqueue("close " + e.ExtensionName);
            Manager.Changed += (_, _) => EventCount.Increment();
        }

        public TempFolder Folder { get; }

        public string Program { get; }

        public string User { get; }

        public string StateFile { get; }

        public ExtensionManager Manager { get; }

        public ConcurrentQueue<string> Log { get; } = new();

        public ConcurrentQueue<string> Tab { get; } = new();

        public ConcurrentQueue<string> Tabs { get; } = new();

        public Counter EventCount { get; } = new();

        public string AddPython(string id, string script, string root = "program", string? extra = null)
        {
            string folder = Path.Combine(root == "program" ? Program : User, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "main.py"), script);
            File.WriteAllText(Path.Combine(folder, "extension.json"), "{\"id\":\"" + id + "\",\"name\":\"" + id + " name\",\"version\":\"1.0\",\"runtime\":\"" + Processes.Python + "\",\"entry\":\"main.py\"" + (extra ?? "") + ",\"windowActions\":[{\"id\":\"lint\",\"label\":\"Lint\"},{\"id\":\"crash\"}]}");
            return folder;
        }

        public void SetState(string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, text);
        }

        public async Task<int> RunningAsync(string id)
        {
            await Wait.UntilAsync(() => Manager.Find(id)?.Status == ExtensionStatus.Running, id + " running");
            int pid = Manager.Find(id)!.ProcessId!.Value;
            _pids.Add(pid);
            return pid;
        }

        public Task LoggedAsync(string text) => Wait.UntilAsync(() => Log.Any(line => line.Contains(text, StringComparison.Ordinal)), "log line '" + text + "' in:\n" + string.Join("\n", Log));

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Wait.WithTimeout(Manager.DisposeAsync().AsTask(), TimeSpan.FromSeconds(10));
            }
            finally
            {
                foreach (int pid in _pids) Processes.KillQuietly(pid);
            }
        }
    }

    internal sealed class Counter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public void Increment() => Interlocked.Increment(ref _value);
    }

    [PythonFact]
    public async Task StartsTalksBothWaysCapturesStderrAndStops()
    {
        using var folder = new TempFolder();
        await using var harness = new Harness(folder);
        harness.AddPython("echo.ext", EchoScript);
        harness.SetState("echo.ext 1 1 script-read\n");
        await Wait.WithTimeout(harness.Manager.StartAsync());
        Assert.Contains("open echo.ext name 0", harness.Tabs);
        int pid = await harness.RunningAsync("echo.ext");
        await harness.LoggedAsync("started json-lines");
        await harness.LoggedAsync("stderr line");
        await harness.LoggedAsync("plain output");
        await harness.LoggedAsync("type=progress, value=5");
        Assert.DoesNotContain(harness.Log, line => line.Contains("booting", StringComparison.Ordinal));
        Assert.Contains("echo.ext name: started json-lines", harness.Tab);

        ExtensionWindowActionBinding binding = Assert.Single(harness.Manager.GetWindowActions("script-editor"), action => action.Action.Id == "lint");
        Assert.Equal("Run Lint from echo.ext name", binding.Tooltip);
        ExtensionCallResult sent = harness.Manager.InvokeWindowAction(binding, ExtensionWindowContext.ForScriptEditor("weapons", "bow", "Weapon: bow", "a\"b\nc", "a\"b"));
        Assert.True(sent.Success, sent.Error);
        await harness.LoggedAsync("action lint id=window-action-");
        await harness.LoggedAsync("name=bow type=weapons kind=script-editor window=weapons:bow title=Weapon: bow len=5 sel=a\"b");
        Assert.True(harness.Manager.Find("echo.ext")!.HasLog);
        Assert.Contains("stderr line", harness.Manager.GetLog("echo.ext"));

        ExtensionCallResult undeclared = harness.Manager.InvokeWindowAction(binding with { Action = binding.Action with { Id = "nope" } }, ExtensionWindowContext.ForScriptEditor("weapons", "bow", "t", "", ""));
        Assert.Equal("Window action is not declared by the extension", undeclared.Error);
        Assert.Equal(ExtensionManager.NotInstalledMessage, harness.Manager.InvokeWindowAction(binding with { ExtensionId = "gone" }, ExtensionWindowContext.ForScriptEditor("w", "n", "t", "", "")).Error);

        Assert.True(harness.Manager.SetEnabled("echo.ext", false).Success);
        await harness.LoggedAsync("stopping");
        await Wait.UntilAsync(() => !Processes.IsAlive(pid), "the runtime to end");
        Assert.Equal(ExtensionStatus.Disabled, harness.Manager.Find("echo.ext")!.Status);
        Assert.Empty(harness.Manager.GetWindowActions("script-editor"));
        Assert.Equal("Extension is disabled or has an invalid manifest", harness.Manager.InvokeWindowAction(binding, ExtensionWindowContext.ForScriptEditor("w", "n", "t", "", "")).Error);
        await harness.Manager.FlushAsync();
        Assert.Equal("echo.ext 0 1 script-read\n", File.ReadAllText(harness.StateFile));

        Assert.True(harness.Manager.SetOutputTab("echo.ext", false).Success);
        Assert.Contains("close echo.ext name", harness.Tabs);
        Assert.True(harness.Manager.SetOutputTab("echo.ext", true).Success);
        Assert.Contains(harness.Tabs, line => line.StartsWith("open echo.ext name ", StringComparison.Ordinal) && line != "open echo.ext name 0");
        Assert.True(harness.Manager.SetApprovedCapabilities("echo.ext", new[] { "net", "file-read" }).Success);
        await harness.Manager.FlushAsync();
        Assert.Equal("echo.ext 0 1\n", File.ReadAllText(harness.StateFile));

        Assert.True(harness.Manager.SetEnabled("echo.ext", true).Success);
        int second = await harness.RunningAsync("echo.ext");
        Assert.NotEqual(pid, second);
    }

    [PythonFact]
    public async Task EventsAreRaisedOnTheGivenContext()
    {
        using var folder = new TempFolder();
        using var ui = new UiThread();
        await using var harness = new Harness(folder, ui.Post);
        var wrongThread = new Counter();
        harness.Manager.LogLine += (_, _) => { if (!ui.IsCurrent) wrongThread.Increment(); };
        harness.Manager.Changed += (_, _) => { if (!ui.IsCurrent) wrongThread.Increment(); };
        harness.AddPython("ctx.ext", EchoScript);
        harness.SetState("ctx.ext 1 0\n");
        await Wait.WithTimeout(harness.Manager.StartAsync());
        await harness.RunningAsync("ctx.ext");
        await harness.LoggedAsync("stderr line");
        Assert.Equal(0, wrongThread.Value);
        Assert.Empty(ui.Errors);
    }

    [PythonFact]
    public async Task AProcessThatDiesInTheMiddleIsReported()
    {
        using var folder = new TempFolder();
        await using var harness = new Harness(folder);
        harness.AddPython("dies.ext", EchoScript);
        harness.SetState("dies.ext 1 0\n");
        await Wait.WithTimeout(harness.Manager.StartAsync());
        int pid = await harness.RunningAsync("dies.ext");
        await harness.LoggedAsync("started");
        ExtensionWindowActionBinding crash = harness.Manager.GetWindowActions("script-editor").Single(action => action.Action.Id == "crash");
        Assert.True(harness.Manager.InvokeWindowAction(crash, ExtensionWindowContext.ForScriptEditor("classes", "c", "Class: c", "x", "")).Success);
        await Wait.UntilAsync(() => harness.Manager.Find("dies.ext")!.Status == ExtensionStatus.Failed, "the failure");
        ExtensionInfo info = harness.Manager.Find("dies.ext")!;
        Assert.Equal("Runtime exited unexpectedly", info.Error);
        Assert.Equal("dies.ext name 1.0 (failed) - Runtime exited unexpectedly", info.DisplayText);
        await harness.LoggedAsync("Runtime exited unexpectedly (exit code 3)");
        Assert.False(Processes.IsAlive(pid));
        ExtensionCallResult after = harness.Manager.InvokeWindowAction(crash, ExtensionWindowContext.ForScriptEditor("classes", "c", "Class: c", "x", ""));
        Assert.Equal("Extension runtime is not running", after.Error);
        // Disabling clears the error; enabling starts a new runtime (no automatic restart).
        Assert.True(harness.Manager.SetEnabled("dies.ext", false).Success);
        Assert.Equal(ExtensionStatus.Disabled, harness.Manager.Find("dies.ext")!.Status);
        Assert.True(harness.Manager.SetEnabled("dies.ext", true).Success);
        await harness.RunningAsync("dies.ext");
    }

    [PythonFact]
    public async Task AProcessThatNeverReadsCannotBlockTheCaller()
    {
        using var folder = new TempFolder();
        await using var harness = new Harness(folder);
        harness.AddPython("deaf.ext", DeafScript);
        harness.SetState("deaf.ext 1 0\n");
        await Wait.WithTimeout(harness.Manager.StartAsync());
        int pid = await harness.RunningAsync("deaf.ext");
        await harness.LoggedAsync("deaf");
        ExtensionWindowActionBinding lint = harness.Manager.GetWindowActions("script-editor").Single(action => action.Action.Id == "lint");
        string big = new('x', 512 * 1024);
        var watch = Stopwatch.StartNew();
        int refused = 0;
        for (int index = 0; index < 60; index++)
        {
            ExtensionCallResult result = harness.Manager.InvokeWindowAction(lint, ExtensionWindowContext.ForScriptEditor("weapons", "w", "t", big, ""));
            if (!result.Success)
            {
                Assert.Equal(ExtensionRuntime.QueueFullMessage, result.Error);
                refused++;
            }
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), "sending took " + watch.Elapsed);
        Assert.True(refused > 40, "refused " + refused);
        Assert.Contains(ExtensionRuntime.QueueFullMessage, harness.Manager.GetLog("deaf.ext"));
        watch.Restart();
        Assert.True(harness.Manager.SetEnabled("deaf.ext", false).Success);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => !Processes.IsAlive(pid), "the deaf runtime to be killed");
    }

    [PythonFact]
    public async Task ARuntimeThatIgnoresStopIsKilledAfterTheTimeout()
    {
        using var folder = new TempFolder();
        await using var harness = new Harness(folder);
        harness.AddPython("stubborn.ext", IgnoreStopScript);
        harness.SetState("stubborn.ext 1 0\n");
        await Wait.WithTimeout(harness.Manager.StartAsync());
        int pid = await harness.RunningAsync("stubborn.ext");
        await harness.LoggedAsync("stubborn");
        var watch = Stopwatch.StartNew();
        harness.Manager.SetEnabled("stubborn.ext", false);
        await Wait.UntilAsync(() => !Processes.IsAlive(pid), "the stubborn runtime to be killed");
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(400), "killed after " + watch.Elapsed);
    }

    [PythonFact]
    public async Task DisposalDuringTrafficStopsEverything()
    {
        using var folder = new TempFolder();
        var harness = new Harness(folder);
        int pid = 0;
        try
        {
            harness.AddPython("spam.ext", SpamScript);
            harness.AddPython("echo.ext", EchoScript);
            harness.SetState("spam.ext 1 1\necho.ext 1 0\n");
            await Wait.WithTimeout(harness.Manager.StartAsync());
            pid = await harness.RunningAsync("spam.ext");
            int echo = await harness.RunningAsync("echo.ext");
            await Wait.UntilAsync(() => harness.Tab.Count > 200, "spam in the tab");
            ExtensionWindowActionBinding lint = harness.Manager.GetWindowActions("script-editor").First(action => action.ExtensionId == "echo.ext" && action.Action.Id == "lint");
            var sender = Task.Run(() =>
            {
                for (int index = 0; index < 200; index++) harness.Manager.InvokeWindowAction(lint, ExtensionWindowContext.ForScriptEditor("weapons", "w", "t", "text", ""));
            });
            await Wait.WithTimeout(harness.Manager.DisposeAsync().AsTask(), TimeSpan.FromSeconds(10));
            await Wait.WithTimeout(sender);
            int count = harness.EventCount.Value;
            await Task.Delay(300);
            Assert.Equal(count, harness.EventCount.Value);
            Assert.False(Processes.IsAlive(pid));
            Assert.False(Processes.IsAlive(echo));
            Assert.True(harness.Manager.IsDisposed);
            Assert.Empty(harness.Manager.Extensions);
            Assert.Empty(harness.Manager.GetWindowActions("script-editor"));
            Assert.Equal(ExtensionManager.ClosedMessage, harness.Manager.SetEnabled("echo.ext", true).Error);
            Assert.Equal(ExtensionManager.ClosedMessage, harness.Manager.InvokeWindowAction(lint, ExtensionWindowContext.ForScriptEditor("w", "n", "t", "", "")).Error);
            Assert.Equal(ExtensionManager.ClosedMessage, (await harness.Manager.RemoveAsync("echo.ext")).Error);
            Assert.Equal(string.Empty, harness.Manager.GetLog("echo.ext"));
            await harness.Manager.StartAsync();
            Assert.Empty(harness.Manager.Extensions);
            harness.Manager.Dispose();
        }
        finally
        {
            await harness.DisposeAsync();
            Processes.KillQuietly(pid);
        }
    }

    [PythonFact]
    public async Task RemovingAPackageDeletesOnlyItsFolder()
    {
        using var folder = new TempFolder();
        await using var harness = new Harness(folder);
        string package = harness.AddPython("remove.ext", EchoScript, "user");
        harness.AddPython("keep.ext", EchoScript, "user");
        harness.SetState("remove.ext 1 1\nkeep.ext 0 0\nnot.installed 1 0 cap\n");
        await Wait.WithTimeout(harness.Manager.StartAsync());
        int pid = await harness.RunningAsync("remove.ext");
        ExtensionCallResult removed = await Wait.WithTimeout(harness.Manager.RemoveAsync("remove.ext"));
        Assert.True(removed.Success, removed.Error);
        Assert.False(Directory.Exists(package));
        Assert.True(Directory.Exists(Path.Combine(harness.User, "keep.ext")));
        Assert.False(Processes.IsAlive(pid));
        Assert.Contains("close remove.ext name", harness.Tabs);
        Assert.Null(harness.Manager.Find("remove.ext"));
        await harness.Manager.FlushAsync();
        Assert.Equal("keep.ext 0 0\nnot.installed 1 0 cap\n", File.ReadAllText(harness.StateFile));
        Assert.Equal(ExtensionManager.NotInstalledMessage, (await harness.Manager.RemoveAsync("remove.ext")).Error);
    }

    [UnixFact]
    public async Task RemovingALinkedPackageKeepsTheLinkTarget()
    {
        using var folder = new TempFolder();
        await using var harness = new Harness(folder);
        string source = folder.Combine("developer-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "extension.ini"), "id=linked\nname=Linked\nversion=1\nentry=main.py\n");
        File.WriteAllText(Path.Combine(source, "main.py"), "");
        string link = Path.Combine(harness.User, "linked");
        Directory.CreateSymbolicLink(link, source);
        await Wait.WithTimeout(harness.Manager.StartAsync());
        Assert.NotNull(harness.Manager.Find("linked"));
        ExtensionCallResult removed = await Wait.WithTimeout(harness.Manager.RemoveAsync("linked"));
        Assert.True(removed.Success, removed.Error);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(source, "main.py")));
    }

    [Fact]
    public async Task InvalidPackagesAreListedAndNeverLaunched()
    {
        using var folder = new TempFolder();
        await using var harness = new Harness(folder);
        Directory.CreateDirectory(Path.Combine(harness.Program, "broken"));
        File.WriteAllText(Path.Combine(harness.Program, "broken", "extension.json"), "{\"id\":\"broken\"}");
        Directory.CreateDirectory(Path.Combine(harness.Program, "noentry"));
        File.WriteAllText(Path.Combine(harness.Program, "noentry", "extension.ini"), "id=noentry\nname=No entry\nversion=1\nentry=missing.py\nruntime=python3\n");
        harness.SetState("broken 1 1\nnoentry 1 0\n");
        await Wait.WithTimeout(harness.Manager.StartAsync());
        ExtensionInfo broken = harness.Manager.Find("broken")!;
        Assert.Equal(ExtensionStatus.Failed, broken.Status);
        Assert.Equal("Manifest requires id, name, version, runtime, and entry", broken.Error);
        Assert.DoesNotContain("open broken 0", harness.Tabs);
        Assert.StartsWith("The manifest is invalid", harness.Manager.SetEnabled("broken", true).Error);
        ExtensionInfo noEntry = harness.Manager.Find("noentry")!;
        Assert.Equal(ExtensionStatus.Failed, noEntry.Status);
        Assert.StartsWith("Entry not found: ", noEntry.Error);
        Assert.Contains("Entry not found", harness.Manager.GetLog("noentry"));
        Assert.Equal(ExtensionManager.NotInstalledMessage, harness.Manager.SetEnabled("absent", true).Error);
    }

    [Fact]
    public async Task AnUnreadableStateFileIsNeverOverwritten()
    {
        using var folder = new TempFolder();
        await using var harness = new Harness(folder);
        Directory.CreateDirectory(harness.StateFile);
        Directory.CreateDirectory(Path.Combine(harness.Program, "x"));
        File.WriteAllText(Path.Combine(harness.Program, "x", "extension.ini"), "id=x\nname=X\nversion=1\nentry=x.py\n");
        await Wait.WithTimeout(harness.Manager.StartAsync());
        Assert.NotNull(harness.Manager.StateFileError);
        Assert.True(harness.Manager.SetOutputTab("x", true).Success);
        await harness.Manager.FlushAsync();
        Assert.True(Directory.Exists(harness.StateFile));
    }

    [UnixFact]
    public async Task TheGs2EngineHostGetsItsStartAfterReadyAndItsShutdown()
    {
        if (Processes.Find("sed") is null || OperatingSystem.IsWindows()) return;
        using var folder = new TempFolder();
        await using var harness = new Harness(folder);
        string package = Path.Combine(harness.Program, "gs2");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "main.gs2"), "");
        string host = Path.Combine(package, "GS2Engine");
        File.WriteAllText(host, HostScript.Replace("\r", string.Empty, StringComparison.Ordinal) + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(package, "extension.json"), "{\"id\":\"gs2.host\",\"name\":\"Host\",\"version\":\"1\",\"runtime\":\"gs2engine-stdio\",\"entry\":\"main.gs2\",\"api\":1}");
        harness.SetState("gs2.host 1 0\n");
        await Wait.WithTimeout(harness.Manager.StartAsync());
        int pid = await harness.RunningAsync("gs2.host");
        await harness.LoggedAsync("host got: {'id':'1','method':'start','params':{'extensionId':'gs2.host','entry':'" + Path.Combine(package, "main.gs2") + "'}}");
        Assert.DoesNotContain(harness.Log, line => line.Contains("json-lines", StringComparison.Ordinal));
        harness.Manager.SetEnabled("gs2.host", false);
        await harness.LoggedAsync("host got: {'id':'2','method':'stop'}");
        await Wait.UntilAsync(() => !Processes.IsAlive(pid), "the host to end");
    }
}
