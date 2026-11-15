using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.App.Tests;

public sealed class ShellTests
{
    [AvaloniaFact]
    public async Task Connect_creates_a_session_starts_its_groups_and_opens_the_main_window()
    {
        using var harness = new ShellHarness();
        int changes = 0;
        harness.Shell.SessionsChanged += (_, _) => changes++;

        ConnectResult result = await harness.ConnectAsync("Server A");

        Assert.True(result.Success);
        SessionContext session = Assert.Single(harness.Shell.Sessions);
        Assert.Same(session, harness.Shell.ActiveSession);
        Assert.Equal("Server A", session.ServerName);
        Assert.Same(harness.Made[0], session.Session);
        Assert.True(harness.Made[0].WasCalled(nameof(IRcSession.ConnectAsync)));
        Assert.Equal("staff", harness.MadeWith[0].Account);
        Assert.Equal("secret", harness.MadeWith[0].Password);
        Assert.NotNull(harness.MadeWith[0].Post);
        Assert.NotNull(session.Services.Get<IPrivateMessages>());
        Assert.NotNull(session.Services.Get<IScriptEditors>());
        Assert.NotNull(session.Services.Get<ISyncStatus>());
        Window? main = session.Windows.Find(ToolIds.MainWindow);
        Assert.NotNull(main);
        Assert.True(main!.IsVisible);
        Assert.NotNull(session.Services.Get<ISessionChat>());
        Assert.Contains("HideLogin", harness.Start.Calls);
        Assert.True(changes > 0);
        Shots.Save(main, "app-main-window");
    }

    [AvaloniaFact]
    public async Task A_failed_connect_disposes_the_connection_and_leaves_no_session()
    {
        using var harness = new ShellHarness();
        harness.NextResults.Enqueue(ConnectResult.Failed("Wrong password"));

        ConnectResult result = await harness.ConnectAsync("Server A");

        Assert.False(result.Success);
        Assert.Equal("Wrong password", result.Error);
        Assert.Empty(harness.Shell.Sessions);
        Assert.Null(harness.Shell.ActiveSession);
        Assert.True(harness.Made[0].IsClosed);
        Assert.DoesNotContain("HideLogin", harness.Start.Calls);
    }

    [AvaloniaFact]
    public async Task Switch_server_replaces_the_connection_in_the_same_context()
    {
        using var harness = new ShellHarness();
        SessionContext session = await harness.ConnectedAsync("Server A");
        Window? main = session.Windows.Find(ToolIds.MainWindow);
        int replaced = 0;
        session.SessionReplaced += (_, _) => replaced++;

        ConnectResult result = await harness.ConnectAsync("Server B", switchFor: session);

        Assert.True(result.Success);
        Assert.Same(session, Assert.Single(harness.Shell.Sessions));
        Assert.Same(harness.Made[1], session.Session);
        Assert.Equal("Server B", session.ServerName);
        Assert.True(harness.Made[0].IsClosed);
        Assert.False(harness.Made[1].IsClosed);
        Assert.Equal(1, replaced);
        Assert.Same(main, session.Windows.Find(ToolIds.MainWindow));
    }

    [AvaloniaFact]
    public async Task A_failed_switch_keeps_the_old_connection()
    {
        using var harness = new ShellHarness();
        SessionContext session = await harness.ConnectedAsync("Server A");
        harness.NextResults.Enqueue(ConnectResult.Failed("Server is full"));

        ConnectResult result = await harness.ConnectAsync("Server B", switchFor: session);

        Assert.False(result.Success);
        Assert.Same(harness.Made[0], session.Session);
        Assert.False(harness.Made[0].IsClosed);
        Assert.True(harness.Made[1].IsClosed);
    }

    [AvaloniaFact]
    public async Task A_switch_for_a_session_that_ended_makes_a_new_session()
    {
        using var harness = new ShellHarness();
        SessionContext first = await harness.ConnectedAsync("Server A");
        harness.Shell.SignOut(first);

        await harness.ConnectAsync("Server B", switchFor: first);

        SessionContext session = Assert.Single(harness.Shell.Sessions);
        Assert.NotSame(first, session);
        Assert.Equal("Server B", session.ServerName);
    }

    [AvaloniaFact]
    public async Task Reconnect_puts_a_new_connection_into_the_session_and_disposes_the_old_one()
    {
        using var harness = new ShellHarness();
        SessionContext session = await harness.ConnectedAsync("Server A");
        harness.Made[0].RaiseDisconnected("Connection lost");

        ConnectResult result = await harness.Shell.ReconnectAsync(session);

        Assert.True(result.Success);
        Assert.Same(harness.Made[1], session.Session);
        Assert.True(harness.Made[0].IsClosed);
        Assert.Equal("staff", harness.MadeWith[1].Account);
        Assert.Equal("secret", harness.MadeWith[1].Password);
        Assert.Equal("Server A", ((Protocol.ListServer.ServerInfo)harness.Made[1].LastCall(nameof(IRcSession.ConnectAsync))!.Arguments[0]!).Name);
    }

    [AvaloniaFact]
    public async Task A_failed_reconnect_disposes_both_connections_and_keeps_the_session()
    {
        using var harness = new ShellHarness();
        SessionContext session = await harness.ConnectedAsync("Server A");
        harness.NextResults.Enqueue(ConnectResult.Failed("Server is down"));

        ConnectResult result = await harness.Shell.ReconnectAsync(session);

        Assert.False(result.Success);
        Assert.Equal("Server is down", result.Error);
        Assert.Same(session, Assert.Single(harness.Shell.Sessions));
        Assert.True(harness.Made[0].IsClosed);
        Assert.True(harness.Made[1].IsClosed);
    }

    [AvaloniaFact]
    public async Task Sign_out_ends_the_session_and_shows_the_login_when_none_is_left()
    {
        using var harness = new ShellHarness();
        SessionContext first = await harness.ConnectedAsync("Server A");
        SessionContext second = await harness.ConnectedAsync("Server B");
        Assert.Same(second, harness.Shell.ActiveSession);
        harness.Start.Calls.Clear();

        harness.Shell.SignOut(second);

        Assert.True(second.IsEnded);
        Assert.True(harness.Made[1].IsClosed);
        Assert.Same(first, harness.Shell.ActiveSession);
        Assert.DoesNotContain("ShowLogin", harness.Start.Calls);
        Assert.Null(second.Windows.Find(ToolIds.MainWindow));

        harness.Shell.SignOut(first);

        Assert.Empty(harness.Shell.Sessions);
        Assert.Null(harness.Shell.ActiveSession);
        Assert.Contains("ShowLogin", harness.Start.Calls);

        harness.Shell.SignOut(first);
        Assert.Single(harness.Start.Calls, call => call == "ShowLogin");
    }

    [AvaloniaFact]
    public async Task Quit_with_unsaved_editors_asks_first_and_stays_when_cancelled()
    {
        using var harness = new ShellHarness();
        SessionContext session = await harness.ConnectedAsync("Server A");
        var editors = new FakeEditors();
        editors.Add("Weapon: bow", "//bow", modified: true);
        editors.Add("Class: util", "//util");
        using IDisposable provided = session.Services.Provide<IScriptEditors>(editors);
        harness.ConfirmAnswer = false;

        bool quit = await harness.Shell.QuitAsync();

        Assert.False(quit);
        string question = Assert.Single(harness.Questions);
        Assert.Contains("Weapon: bow", question);
        Assert.DoesNotContain("Class: util", question);
        Assert.False(session.IsEnded);
        Assert.Equal(0, harness.Desktop.Shutdowns);

        harness.ConfirmAnswer = true;
        quit = await harness.Shell.QuitAsync();

        Assert.True(quit);
        Assert.True(session.IsEnded);
        Assert.True(harness.Made[0].IsClosed);
        Assert.Empty(harness.Shell.Sessions);
        Assert.Equal(1, harness.Desktop.Shutdowns);
    }

    [AvaloniaFact]
    public async Task Quit_without_unsaved_editors_does_not_ask_and_releases_every_component()
    {
        using var harness = new ShellHarness();
        await harness.ConnectedAsync("Server A");
        var component = new Disposable();
        var later = new Disposable();
        harness.Shell.Own(component);
        harness.Shell.Own(later);
        var order = new List<Disposable>();
        component.Disposed += () => order.Add(component);
        later.Disposed += () => order.Add(later);

        Assert.True(await harness.Shell.QuitAsync());

        Assert.Empty(harness.Questions);
        Assert.Equal(new[] { later, component }, order);
        Assert.Equal(1, harness.Desktop.Shutdowns);
        Assert.True(harness.Shell.IsQuitting);
        ConnectResult after = await harness.ConnectAsync("Server B");
        Assert.False(after.Success);
        Assert.Empty(harness.Shell.Sessions);
    }

    [AvaloniaFact]
    public async Task Attention_flashes_beeps_or_blinks_only_while_no_window_is_active()
    {
        using var harness = new ShellHarness(options => options with { NotificationSounds = true });
        SessionContext session = await harness.ConnectedAsync("Server A");
        Window main = session.Windows.Find(ToolIds.MainWindow)!;
        var pms = new List<SessionContext>();
        harness.Shell.PrivateMessageAttention += (_, s) => pms.Add(s);

        harness.Desktop.Active = true;
        harness.Shell.RequestAttention(session, AttentionKind.Alert);
        harness.Shell.RequestAttention(session, AttentionKind.AlertWithSound);
        harness.Shell.RequestAttention(session, AttentionKind.PrivateMessage);
        Assert.Empty(harness.Desktop.Flashed);
        Assert.Equal(0, harness.Desktop.Beeps);
        Assert.Empty(pms);

        harness.Desktop.Active = false;
        harness.Shell.RequestAttention(session, AttentionKind.Alert);
        Assert.Same(main, Assert.Single(harness.Desktop.Flashed));
        Assert.Equal(0, harness.Desktop.Beeps);

        harness.Shell.RequestAttention(session, AttentionKind.AlertWithSound);
        Assert.Equal(2, harness.Desktop.Flashed.Count);
        Assert.Equal(1, harness.Desktop.Beeps);

        harness.Shell.RequestAttention(session, AttentionKind.PrivateMessage);
        Assert.Same(session, Assert.Single(pms));
        Assert.Equal(2, harness.Desktop.Flashed.Count);

        harness.Environment.SetOptions(options => options with { NotificationSounds = false });
        harness.Shell.RequestAttention(session, AttentionKind.AlertWithSound);
        Assert.Equal(1, harness.Desktop.Beeps);
        Assert.Equal(3, harness.Desktop.Flashed.Count);

        harness.Shell.SignOut(session);
        harness.Shell.RequestAttention(session, AttentionKind.Alert);
        Assert.Equal(3, harness.Desktop.Flashed.Count);
    }

    [AvaloniaFact]
    public void Activity_moves_the_clock_of_the_away_timer()
    {
        using var harness = new ShellHarness();
        DateTimeOffset start = harness.Shell.LastActivity;
        harness.Now = harness.Now.AddMinutes(5);

        harness.Shell.NoteActivity();

        Assert.Equal(start.AddMinutes(5), harness.Shell.LastActivity);
    }

    [AvaloniaFact]
    public async Task Login_server_list_and_settings_go_to_the_start_windows()
    {
        using var harness = new ShellHarness();
        SessionContext session = await harness.ConnectedAsync("Server A");
        harness.Start.Calls.Clear();

        harness.Shell.ShowLogin();
        harness.Shell.ShowServerList(session);
        harness.Shell.ShowListServerSettings(null);

        Assert.Equal(new[] { "ShowLogin", "ShowServerList", "ShowListServerSettings" }, harness.Start.Calls);
        Assert.Same(session, harness.Start.LastSwitch);
        harness.Shell.SignOut(session);
        harness.Shell.ShowServerList(session);
        Assert.Null(harness.Start.LastSwitch);
    }

    [AvaloniaFact]
    public void The_extension_actions_are_a_service_of_the_program()
    {
        using var harness = new ShellHarness();

        Assert.Same(harness.Extensions, harness.Shell.GetService<IExtensionActions>());
        Assert.Null(harness.Shell.GetService<ISyncStatus>());
    }

    [AvaloniaFact]
    public async Task Toggle_hides_and_shows_the_active_main_window_or_the_login()
    {
        using var harness = new ShellHarness();
        harness.Start.IsLoginVisible = true;
        harness.Shell.ToggleVisibility();
        Assert.False(harness.Start.IsLoginVisible);
        harness.Shell.ToggleVisibility();
        Assert.True(harness.Start.IsLoginVisible);

        SessionContext session = await harness.ConnectedAsync("Server A");
        Window main = session.Windows.Find(ToolIds.MainWindow)!;
        Assert.True(main.IsVisible);
        harness.Shell.ToggleVisibility();
        Assert.False(main.IsVisible);
        Assert.False(session.IsEnded);
        harness.Shell.ToggleVisibility();
        Assert.True(main.IsVisible);
    }

    [AvaloniaFact]
    public async Task Toggle_with_primary_sessions_hides_all_of_them_when_one_is_shown()
    {
        using var harness = new ShellHarness();
        SessionContext first = await harness.ConnectedAsync("Server A");
        SessionContext second = await harness.ConnectedAsync("Server B");
        SessionContext third = await harness.ConnectedAsync("Server C");
        harness.Shell.TogglePrimary(first);
        harness.Shell.TogglePrimary(second);
        AppShell.HideMainWindow(second);

        harness.Shell.ToggleVisibility();

        Assert.False(AppShell.IsMainWindowVisible(first));
        Assert.False(AppShell.IsMainWindowVisible(second));
        Assert.True(AppShell.IsMainWindowVisible(third));

        harness.Shell.ToggleVisibility();

        Assert.True(AppShell.IsMainWindowVisible(first));
        Assert.True(AppShell.IsMainWindowVisible(second));
    }

    [AvaloniaFact]
    public async Task Packet_log_writes_every_received_packet_of_every_connection()
    {
        var writer = new StringWriter();
        var debug = new DebugOutput(writer, debug: false, packets: true);
        using var harness = new ShellHarness();
        var shell = new AppShell(harness.Environment.Services, harness.Tools, options =>
        {
            var fake = new FakeRcSession(options.Account);
            harness.Made.Add(fake);
            return fake;
        }, harness.Desktop, new AppShellSettings { StartFeatures = _ => { }, Debug = debug, Post = action => action() });
        shell.AttachStart(harness.Start);

        await shell.ConnectAsync(new ServerChoice(ShellHarness.Login("Server A"), null));
        SessionContext session = shell.Sessions[0];
        harness.Made[0].RaiseRawPacket(RcConnectionKind.Rc, 42, [0x01, 0xAB]);
        await shell.ConnectAsync(new ServerChoice(ShellHarness.Login("Server B"), session));
        harness.Made[0].RaiseRawPacket(RcConnectionKind.Rc, 1, [0x00]);
        harness.Made[1].RaiseRawPacket(RcConnectionKind.Nc, 7, []);

        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("[RemoteControl packet] id=42 length=2 data=01AB", lines);
        Assert.Contains("[RemoteControl packet] id=7 length=0 data=", lines);
        Assert.DoesNotContain("[RemoteControl packet] id=1 length=1 data=00", lines);
        foreach (SessionContext open in shell.Sessions.ToArray()) shell.SignOut(open);
    }

    private sealed class Disposable : IDisposable
    {
        public event Action? Disposed;

        public void Dispose() => Disposed?.Invoke();
    }
}
