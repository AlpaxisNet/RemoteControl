using System.Reflection;
using Avalonia.Headless.XUnit;
using RemoteControl.Core;
using RemoteControl.Extensions;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.App.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void Mcp_arguments_choose_the_stdio_server_without_any_window()
    {
        StartupArguments equals = StartupArguments.Parse(["--mcp", "--mcp-instance=4242"]);
        StartupArguments separate = StartupArguments.Parse(["--mcp-instance", "17", "--mcp"]);

        Assert.Equal(StartMode.McpServer, equals.Mode);
        Assert.Equal(4242, equals.McpInstance);
        Assert.Equal(StartMode.McpServer, separate.Mode);
        Assert.Equal(17, separate.McpInstance);

        var ran = new List<string>();
        int code = Program.Dispatch(equals, _ => { ran.Add("mcp"); return 3; }, _ => { ran.Add("windows"); return 0; });
        Assert.Equal(3, code);
        Assert.Equal(new[] { "mcp" }, ran);
    }

    [Fact]
    public void Without_mcp_the_windows_start_with_debug_and_packet_log()
    {
        StartupArguments none = StartupArguments.Parse([]);
        StartupArguments debug = StartupArguments.Parse(["--debug", "--other"]);
        StartupArguments packets = StartupArguments.Parse(["--packetlog"]);
        StartupArguments instanceOnly = StartupArguments.Parse(["--mcp-instance=abc"]);

        Assert.Equal(StartMode.Windows, none.Mode);
        Assert.False(none.Debug);
        Assert.True(debug.Debug);
        Assert.False(debug.PacketLog);
        Assert.Equal(new[] { "--other" }, debug.Remaining);
        Assert.True(packets.Debug);
        Assert.True(packets.PacketLog);
        Assert.Equal(StartMode.Windows, instanceOnly.Mode);
        Assert.Equal(0, instanceOnly.McpInstance);

        var ran = new List<string>();
        Program.Dispatch(debug, _ => { ran.Add("mcp"); return 3; }, _ => { ran.Add("windows"); return 0; });
        Assert.Equal(new[] { "windows" }, ran);
    }

    [Fact]
    public void Headless_runs_the_program_without_a_desktop()
    {
        StartupArguments headless = StartupArguments.Parse(["--headless", "--debug"]);
        Assert.Equal(StartMode.Windows, headless.Mode);
        Assert.True(headless.Headless);
        Assert.True(headless.Debug);
        Assert.Empty(headless.Remaining);
        Assert.False(StartupArguments.Parse([]).Headless);
        // --mcp still wins: the stdio server has no Avalonia at all, headless or not.
        Assert.Equal(StartMode.McpServer, StartupArguments.Parse(["--headless", "--mcp"]).Mode);
    }

    [Fact]
    public void Packet_and_debug_lines_have_the_cpp_format()
    {
        Assert.Equal("[RemoteControl packet] id=6 length=3 data=00FF7A", DebugOutput.FormatPacket(6, [0x00, 0xFF, 0x7A]));
        Assert.Equal("[RemoteControl packet] id=0 length=0 data=", DebugOutput.FormatPacket(0, []));
        Assert.Equal("[RemoteControl] debug logging enabled", DebugOutput.FormatLine("debug logging enabled"));

        var writer = new StringWriter();
        var debugOnly = new DebugOutput(writer, debug: true, packets: false);
        debugOnly.Packet(1, [1]);
        debugOnly.Line("hello");
        Assert.Equal("[RemoteControl] hello\n", writer.ToString());

        var packetWriter = new StringWriter();
        var packets = new DebugOutput(packetWriter, debug: false, packets: true);
        Assert.True(packets.Debug);
        packets.Packet(255, [0xAB]);
        Assert.Equal("[RemoteControl packet] id=255 length=1 data=AB\n", packetWriter.ToString());
    }

    [Fact]
    public void Debug_output_copies_new_log_lines()
    {
        var log = new AppLog();
        log.Add("before");
        var writer = new StringWriter();
        using (var debug = new DebugOutput(writer, debug: true, packets: false))
        {
            debug.Follow(log);
            log.Add("one");
            log.Add("two");
            debug.Pump();
            debug.Pump();
        }
        Assert.Equal("[RemoteControl] one\n[RemoteControl] two\n", writer.ToString());
    }
}

public sealed class StartupTests
{
    private static AppRuntime Start(TestEnvironment env, FakeDesktop desktop, FakeTrayIcon tray, FakeHotkeyBackend hotkey, FakeTimers timers, List<string> shown) =>
        AppRuntime.Start(new AppRuntimeSettings
        {
            Paths = env.Paths,
            Services = env.Services,
            Factory = options => new FakeRcSession(options.Account),
            Desktop = _ => desktop,
            Tray = _ => tray,
            Hotkey = () => hotkey,
            Approvals = new FakeApprovals(),
            Timers = timers,
            ShowProblems = problems =>
            {
                shown.AddRange(problems);
                return Task.CompletedTask;
            },
            ShowHotkeyProblem = _ => Task.CompletedTask,
        });

    [AvaloniaFact]
    public async Task Start_up_registers_every_tool_and_shows_the_login_window()
    {
        using var env = new TestEnvironment(options: options => options with { GlobalHotkey = "ctrl+alt+r" });
        var desktop = new FakeDesktop();
        var tray = new FakeTrayIcon();
        var hotkey = new FakeHotkeyBackend();
        var timers = new FakeTimers();
        AppRuntime runtime = Start(env, desktop, tray, hotkey, timers, []);

        foreach (FieldInfo field in typeof(ToolIds).GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.IsLiteral))
            Assert.True(runtime.Tools.IsRegistered((string)field.GetValue(null)!), field.Name);
        Assert.True(runtime.StartController.IsLoginVisible);
        Assert.Same(runtime.Extensions, runtime.Shell.GetService<IExtensionActions>());
        Assert.Equal(new[] { "Open", "-", "Quit" }, tray.Menu.Select(entry => entry.Label.Length == 0 ? "-" : entry.Label).ToArray());
        Assert.True(runtime.Shell.TrayAvailable);
        await runtime.Hotkey.Current;
        Assert.Equal(Hotkeys.Hotkey.Parse("ctrl+alt+r"), Assert.Single(hotkey.Requests));
        Assert.Null(runtime.Mcp.Bridge);
        Shots.Save(RcWindow.OpenWindows.Last(window => window.IsVisible), "app-login");

        Assert.True(await runtime.Shell.QuitAsync());
        Assert.True(tray.Disposed);
        Assert.True(hotkey.Disposed);
        Assert.Equal(1, desktop.Shutdowns);
        Assert.False(runtime.StartController.IsLoginVisible);
    }

    [AvaloniaFact]
    public async Task A_headless_runtime_makes_no_tray_or_hotkey_and_denies_approvals_it_cannot_ask()
    {
        using var env = new TestEnvironment();
        int trays = 0;
        int hotkeys = 0;
        AppRuntime runtime = AppRuntime.Start(new AppRuntimeSettings
        {
            Paths = env.Paths,
            Services = env.Services,
            Factory = options => new FakeRcSession(options.Account),
            Desktop = _ => new FakeDesktop(),
            Tray = _ =>
            {
                trays++;
                return new FakeTrayIcon();
            },
            Hotkey = () =>
            {
                hotkeys++;
                return new FakeHotkeyBackend();
            },
            Timers = new FakeTimers(),
            ShowProblems = _ => Task.CompletedTask,
            ShowHotkeyProblem = _ => Task.CompletedTask,
            Headless = true,
        });

        Assert.Equal((0, 0), (trays, hotkeys));
        Assert.Null(runtime.Tray);
        Assert.False(runtime.Shell.TrayAvailable);
        Assert.True(runtime.StartController.IsLoginVisible);
        // MCP is off in a fresh options file: a headless program says that nothing can reach it.
        Assert.Contains(env.Log.Snapshot(), line => line.Contains("MCP is switched off", StringComparison.Ordinal));
        int before = RcWindow.OpenWindows.Count;
        Assert.False(await runtime.McpHost.ApproveAsync("Send RC chat text"));
        Assert.Equal(McpApproval.Deny, await runtime.McpHost.ApproveScriptAsync(McpScriptAction.Write, "Weapon: bow", "Weapon"));
        Assert.Equal(before, RcWindow.OpenWindows.Count);
        Assert.Contains(env.Log.Snapshot(), line => line.Contains("needs approval was denied", StringComparison.Ordinal) && line.Contains("Send RC chat text", StringComparison.Ordinal));

        Assert.True(await runtime.Shell.QuitAsync());
    }

    [AvaloniaFact]
    public async Task A_connect_from_the_start_windows_makes_a_session_with_every_group()
    {
        using var env = new TestEnvironment();
        var tray = new FakeTrayIcon();
        AppRuntime runtime = Start(env, new FakeDesktop(), tray, new FakeHotkeyBackend(), new FakeTimers(), []);

        var choice = new ServerChoice(ShellHarness.Login("Alpha"), null);
        Protocol.Session.ConnectResult result = await runtime.Shell.ConnectAsync(choice);

        Assert.True(result.Success);
        SessionContext session = Assert.Single(runtime.Shell.Sessions);
        Assert.NotNull(session.Services.Get<IPrivateMessages>());
        Assert.NotNull(session.Services.Get<IScriptEditors>());
        Assert.NotNull(session.Services.Get<ISyncStatus>());
        Assert.NotNull(session.Windows.Find(ToolIds.MainWindow));
        Assert.False(runtime.StartController.IsLoginVisible);
        Assert.NotNull(FakeTrayIcon.Find(tray.Menu, "Alpha"));

        runtime.Shell.SignOut(session);
        Assert.True(runtime.StartController.IsLoginVisible);
        Assert.True(await runtime.Shell.QuitAsync());
    }

    [AvaloniaFact]
    public async Task Problems_of_the_users_files_are_shown_and_the_file_is_kept()
    {
        string root = Path.Combine(Path.GetTempPath(), "rc-app-tests", Guid.NewGuid().ToString("N"));
        string user = Path.Combine(root, "user");
        Directory.CreateDirectory(user);
        var paths = new AppPaths(user, Path.Combine(TestEnvironment.RepositoryRoot, "assets", "runtime"));
        File.WriteAllBytes(paths.OptionsFile, [(byte)'t', (byte)'h', 0, 0, (byte)'\n']);
        var shown = new List<string>();
        try
        {
            AppRuntime runtime = AppRuntime.Start(new AppRuntimeSettings
            {
                Paths = paths,
                ServicesSettings = new AppServicesSettings { EnableSounds = false, LogToFile = false, Log = new AppLog() },
                Factory = options => new FakeRcSession(options.Account),
                Desktop = _ => new FakeDesktop(),
                Hotkey = () => new FakeHotkeyBackend(),
                Approvals = new FakeApprovals(),
                Timers = new FakeTimers(),
                ShowProblems = problems =>
                {
                    shown.AddRange(problems);
                    return Task.CompletedTask;
                },
            });
            await runtime.ProblemsShown;

            string problem = Assert.Single(runtime.Problems);
            Assert.Equal(problem, Assert.Single(shown));
            Assert.Contains("could not be read", problem);
            Assert.Contains(Directory.GetFiles(user), file => Path.GetFileName(file).StartsWith(AppPaths.OptionsFileName, StringComparison.Ordinal) && file != paths.OptionsFile);
            Assert.False(runtime.Shell.TrayAvailable);
            Assert.True(await runtime.Shell.QuitAsync());
        }
        finally
        {
            foreach (RcWindow window in RcWindow.OpenWindows.ToArray()) window.Close();
            UiSafe.Configure(AppLog.Default, null);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [AvaloniaFact]
    public async Task The_hotkey_toggles_the_program_like_a_tray_click()
    {
        using var env = new TestEnvironment();
        var hotkey = new FakeHotkeyBackend();
        AppRuntime runtime = Start(env, new FakeDesktop(), new FakeTrayIcon(), hotkey, new FakeTimers(), []);
        Assert.True(runtime.StartController.IsLoginVisible);

        hotkey.Press();
        await TestUi.WaitUntilAsync(() => !runtime.StartController.IsLoginVisible);

        hotkey.Press();
        await TestUi.WaitUntilAsync(() => runtime.StartController.IsLoginVisible);
        Assert.True(await runtime.Shell.QuitAsync());
    }

    [AvaloniaFact]
    public async Task Closing_the_login_window_quits()
    {
        using var env = new TestEnvironment();
        var desktop = new FakeDesktop();
        AppRuntime runtime = Start(env, desktop, new FakeTrayIcon(), new FakeHotkeyBackend(), new FakeTimers(), []);
        RcWindow login = RcWindow.OpenWindows.Last(window => window.IsVisible);

        login.Close();

        await TestUi.WaitUntilAsync(() => desktop.Shutdowns == 1);
        Assert.True(runtime.Shell.IsQuitting);
    }
}
