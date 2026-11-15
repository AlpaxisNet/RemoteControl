using Avalonia.Headless.XUnit;
using RemoteControl.App.Tray;
using RemoteControl.Ui.Shell;

namespace RemoteControl.App.Tests;

public sealed class TrayMenuModelTests
{
    private static TraySessionState Session(string name, bool active = false, bool primary = false, bool nc = false, bool connected = true, bool unread = false, int players = 0) =>
        new(name, connected, nc, primary, active, unread, players);

    private static string[] Labels(IReadOnlyList<TrayMenuEntry> entries) => entries.Select(entry => entry.Command == TrayCommand.Separator ? "-" : entry.Label).ToArray();

    [Fact]
    public void Without_sessions_the_menu_is_open_and_quit()
    {
        IReadOnlyList<TrayMenuEntry> menu = TrayMenuModel.Build(new TrayMenuState([]));

        Assert.Equal(new[] { "Open", "-", "Quit" }, Labels(menu));
        Assert.Equal("Remote Control", TrayMenuModel.ToolTip(new TrayMenuState([])));
    }

    [Fact]
    public void One_session_has_sign_out_its_entries_and_the_primary_submenu()
    {
        var state = new TrayMenuState([Session("Classic", active: true, players: 1)]);

        IReadOnlyList<TrayMenuEntry> menu = TrayMenuModel.Build(state);

        Assert.Equal(new[] { "Open", "Sign out", "-", "Classic", "Sign out Classic", "Primary Windows", "-", "Quit" }, Labels(menu));
        Assert.True(menu[3].Checked);
        Assert.Equal(TrayCommand.ShowSession, menu[3].Command);
        Assert.Equal(0, menu[3].Session);
        Assert.Equal(TrayCommand.SignOutSession, menu[4].Command);
        TrayMenuEntry primary = Assert.Single(menu[5].Items);
        Assert.Equal(TrayCommand.TogglePrimary, primary.Command);
        Assert.False(primary.Checked);
        Assert.Equal("Classic: 1 player", TrayMenuModel.ToolTip(state));
    }

    [Fact]
    public void Three_sessions_show_the_marker_the_server_list_the_unread_entry_and_the_disconnected_one()
    {
        var state = new TrayMenuState([
            Session("Alpha", primary: true),
            Session("Beta", active: true, nc: true, unread: true, players: 12),
            Session("Gamma", connected: false),
        ]);

        IReadOnlyList<TrayMenuEntry> menu = TrayMenuModel.Build(state);

        Assert.Equal(new[]
        {
            "Open", "Server List", "Sign out", "Latest Private Message", "-",
            "[*] Alpha", "Sign out [*] Alpha", "Beta", "Sign out Beta", "Gamma (disconnected)", "Sign out Gamma (disconnected)",
            "Primary Windows", "-", "Quit",
        }, Labels(menu));
        Assert.Equal(new bool?[] { false, true, false }, menu.Where(entry => entry.Command == TrayCommand.ShowSession).Select(entry => entry.Checked).ToArray());
        Assert.Equal(new bool?[] { true, false, false }, menu[11].Items.Select(entry => entry.Checked).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, menu[11].Items.Select(entry => entry.Session).ToArray());
        Assert.Equal("Beta: 12 players", TrayMenuModel.ToolTip(state));
    }

    [Fact]
    public void The_server_list_needs_the_active_sessions_nc_and_the_tool_tip_is_cut_to_64_characters()
    {
        var state = new TrayMenuState([Session(new string('x', 80), active: true, players: 2), Session("Other", nc: true)]);

        Assert.DoesNotContain("Server List", Labels(TrayMenuModel.Build(state)));
        Assert.Equal(64, TrayMenuModel.ToolTip(state).Length);
        Assert.Equal("Remote Control", TrayMenuModel.ToolTip(new TrayMenuState([Session("Down", active: true, connected: false)])));
    }

    [Fact]
    public void The_signature_changes_with_the_menu_only()
    {
        var one = new TrayMenuState([Session("A", active: true)]);
        var same = new TrayMenuState([Session("A", active: true, players: 40)]);
        var other = new TrayMenuState([Session("A", active: true, primary: true)]);

        Assert.Equal(TrayMenuModel.Signature(TrayMenuModel.Build(one)), TrayMenuModel.Signature(TrayMenuModel.Build(same)));
        Assert.NotEqual(TrayMenuModel.Signature(TrayMenuModel.Build(one)), TrayMenuModel.Signature(TrayMenuModel.Build(other)));
    }
}

public sealed class TrayControllerTests
{
    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Controller = new TrayController(Harness.Shell, Icon, Timers, () => Now);
            Harness.Shell.TrayShown = () => Icon.IsShown;
        }

        public ShellHarness Harness { get; } = new();

        public FakeTrayIcon Icon { get; } = new();

        public FakeTimers Timers { get; } = new();

        public TrayController Controller { get; }

        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Dispose()
        {
            Controller.Dispose();
            Harness.Dispose();
        }
    }

    private static string[] Labels(FakeTrayIcon icon) => icon.Menu.Select(entry => entry.Command == TrayCommand.Separator ? "-" : entry.Label).ToArray();

    [AvaloniaFact]
    public async Task The_menu_follows_the_sessions_without_stale_or_duplicated_entries()
    {
        using var rig = new Rig();
        Assert.Equal(new[] { "Open", "-", "Quit" }, Labels(rig.Icon));

        SessionContext first = await rig.Harness.ConnectedAsync("Alpha");
        SessionContext second = await rig.Harness.ConnectedAsync("Beta");
        await rig.Harness.ConnectedAsync("Gamma");
        Assert.Equal(3, rig.Icon.Menu.Count(entry => entry.Command == TrayCommand.ShowSession));
        int builds = rig.Icon.MenuBuilds;
        rig.Timers.Fire(TrayController.RefreshInterval);
        rig.Timers.Fire(TrayController.RefreshInterval);
        Assert.Equal(builds, rig.Icon.MenuBuilds);

        Action<TrayMenuEntry> oldInvoke = rig.Icon.Invoke!;
        TrayMenuEntry oldSignOutSecond = FakeTrayIcon.Find(rig.Icon.Menu, "Sign out Beta")!;
        rig.Harness.Shell.SignOut(second);

        Assert.Equal(new[] { "Alpha", "Gamma" }, rig.Icon.Menu.Where(entry => entry.Command == TrayCommand.ShowSession).Select(entry => entry.Label).ToArray());
        Assert.Single(rig.Icon.Menu, entry => entry.Label == "Quit");

        // An entry of a menu built before the sign-out acts on nothing (defect D3).
        oldInvoke(oldSignOutSecond);
        Assert.Equal(2, rig.Harness.Shell.Sessions.Count);
        Assert.False(first.IsEnded);
    }

    [AvaloniaFact]
    public async Task Menu_entries_show_sign_out_mark_primary_and_quit()
    {
        using var rig = new Rig();
        SessionContext alpha = await rig.Harness.ConnectedAsync("Alpha");
        SessionContext beta = await rig.Harness.ConnectedAsync("Beta");
        AppShell.HideMainWindow(alpha);

        rig.Icon.Choose("Alpha");
        Assert.Same(alpha, rig.Harness.Shell.ActiveSession);
        Assert.True(AppShell.IsMainWindowVisible(alpha));

        TrayMenuEntry primary = rig.Icon.Menu.Single(entry => entry.Command == TrayCommand.Submenu).Items[1];
        rig.Icon.Invoke!(primary);
        Assert.Contains(beta, rig.Harness.Shell.PrimarySessions);
        Assert.NotNull(FakeTrayIcon.Find(rig.Icon.Menu, "[*] Beta"));

        rig.Icon.Choose("Sign out");
        Assert.True(alpha.IsEnded);
        Assert.Same(beta, rig.Harness.Shell.ActiveSession);

        rig.Icon.Choose("Sign out [*] Beta");
        Assert.True(beta.IsEnded);
        Assert.Contains("ShowLogin", rig.Harness.Start.Calls);

        rig.Icon.Choose("Quit");
        await TestKitWait.UntilAsync(() => rig.Harness.Desktop.Shutdowns == 1);
    }

    [AvaloniaFact]
    public async Task Server_list_entry_asks_for_the_active_sessions_server_list()
    {
        using var rig = new Rig();
        SessionContext session = await rig.Harness.ConnectedAsync("Alpha");
        rig.Harness.Made[0].IsNcAuthenticated = true;
        rig.Timers.Fire(TrayController.RefreshInterval);

        rig.Icon.Choose("Server List");

        Assert.Same(session, rig.Harness.Start.LastSwitch);
    }

    [AvaloniaFact]
    public async Task The_icon_blinks_while_a_pm_is_unread_and_after_a_pm_attention()
    {
        using var rig = new Rig();
        SessionContext session = await rig.Harness.ConnectedAsync("Alpha");
        var messages = new FakePrivateMessages();
        using IDisposable provided = session.Services.Provide<IPrivateMessages>(messages);
        rig.Controller.Refresh();
        Assert.False(rig.Controller.IsBlinking);

        messages.SetUnread(true);
        Assert.True(rig.Controller.IsBlinking);
        Assert.True(rig.Icon.Alert);
        rig.Timers.Fire(TrayController.BlinkInterval);
        Assert.False(rig.Icon.Alert);
        rig.Timers.Fire(TrayController.BlinkInterval);
        Assert.True(rig.Icon.Alert);
        Assert.NotNull(FakeTrayIcon.Find(rig.Icon.Menu, "Latest Private Message"));

        messages.SetUnread(false);
        Assert.False(rig.Controller.IsBlinking);
        Assert.False(rig.Icon.Alert);
        Assert.Equal(0, rig.Timers.Live(TrayController.BlinkInterval));

        rig.Harness.Shell.RequestAttention(session, AttentionKind.PrivateMessage);
        Assert.True(rig.Controller.IsBlinking);
        rig.Icon.Choose("Open");
        Assert.False(rig.Controller.IsBlinking);
    }

    [AvaloniaFact]
    public async Task A_click_toggles_after_the_delay_and_a_double_click_opens_the_latest_pm()
    {
        using var rig = new Rig();
        SessionContext session = await rig.Harness.ConnectedAsync("Alpha");
        var messages = new FakePrivateMessages();
        using IDisposable provided = session.Services.Provide<IPrivateMessages>(messages);
        messages.SetUnread(true);

        rig.Icon.Click();
        Assert.True(AppShell.IsMainWindowVisible(session));
        rig.Timers.Fire(TrayController.DoubleClickDelay);
        Assert.False(AppShell.IsMainWindowVisible(session));

        rig.Icon.Click();
        rig.Icon.Click();
        Assert.Equal(1, messages.Opened);
        Assert.Equal(0, rig.Timers.Live(TrayController.DoubleClickDelay));

        // Clicks right after a double click are ignored.
        rig.Icon.Click();
        Assert.Equal(0, rig.Timers.Live(TrayController.DoubleClickDelay));
        rig.Now += TimeSpan.FromSeconds(1);
        rig.Icon.Click();
        Assert.Equal(1, rig.Timers.Live(TrayController.DoubleClickDelay));
    }

    [AvaloniaFact]
    public async Task Without_a_pm_a_double_click_opens_and_the_tool_tip_names_the_server()
    {
        using var rig = new Rig();
        SessionContext session = await rig.Harness.ConnectedAsync("Alpha");
        AppShell.HideMainWindow(session);
        rig.Harness.Made[0].PlayerList.Clear();
        rig.Timers.Fire(TrayController.RefreshInterval);

        rig.Icon.Click();
        rig.Icon.Click();

        Assert.True(AppShell.IsMainWindowVisible(session));
        Assert.Equal("Alpha: 0 players", rig.Icon.ToolTip);
    }

    [AvaloniaFact]
    public void Tray_available_is_what_the_icon_says_and_disposing_releases_it()
    {
        using var rig = new Rig();
        Assert.True(rig.Harness.Shell.TrayAvailable);
        rig.Icon.Shown = false;
        Assert.False(rig.Harness.Shell.TrayAvailable);

        rig.Controller.Dispose();
        Assert.True(rig.Icon.Disposed);
        Assert.Equal(0, rig.Timers.Live(TrayController.RefreshInterval));
    }

    [AvaloniaFact]
    public async Task Tray_availability_is_false_until_a_watcher_answers()
    {
        var log = new Core.AppLog();
        bool answer = false;
        using var availability = new TrayAvailability(log, _ => Task.FromResult(answer));
        if (TrayAvailability.AlwaysAvailable) return;

        Assert.False(availability.IsAvailable);
        Assert.False(await availability.CheckAsync(CancellationToken.None));
        answer = true;
        Assert.True(await availability.CheckAsync(CancellationToken.None));
        Assert.True(availability.IsAvailable);

        using var failing = new TrayAvailability(log, _ => throw new InvalidOperationException("no bus"));
        Assert.False(await failing.CheckAsync(CancellationToken.None));
        // A question that is never answered: it ends only by the time limit's cancellation, which is no answer.
        using var hanging = new TrayAvailability(log, async token =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return true;
        }, TimeSpan.FromMilliseconds(50));
        Assert.False(await hanging.CheckAsync(CancellationToken.None));
    }

    private sealed class FakePrivateMessages : IPrivateMessages
    {
        public bool HasUnread { get; private set; }

        public int Opened { get; private set; }

        public event EventHandler? UnreadChanged;

        public void SetUnread(bool unread)
        {
            HasUnread = unread;
            UnreadChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool OpenLatest()
        {
            Opened++;
            SetUnread(false);
            return true;
        }

        public void ClearUnread() => SetUnread(false);
    }
}

internal static class TestKitWait
{
    public static Task UntilAsync(Func<bool> condition) => RemoteControl.Ui.TestKit.TestUi.WaitUntilAsync(condition);
}
