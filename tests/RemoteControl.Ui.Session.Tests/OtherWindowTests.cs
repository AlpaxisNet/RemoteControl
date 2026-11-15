using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;
using static RemoteControl.Ui.Session.Tests.TestHelpers;

namespace RemoteControl.Ui.Session.Tests;

public sealed class ToallsWindowTests
{
    [AvaloniaFact]
    public void Enter_sends_to_all_shows_it_once_and_hides_the_echo()
    {
        using var test = new TestSession();
        ToallsWindow window = test.Show(new ToallsWindow(test.Context), 360, 240);
        Assert.Equal("Toalls - Test Server", window.Title);
        TextBox entry = TestUi.Find<TextBox>(window, "ToallEntry");
        entry.Focus();
        entry.Text = "server restart";
        TestUi.PressKey(window, Key.Enter);
        Assert.Equal("server restart", test.Fake.LastCall("SendToAllAsync")!.Argument<string>(0));
        Assert.Equal(string.Empty, entry.Text);
        Assert.Equal("Tester: server restart", window.Chat.Text);
        test.Fake.RaiseToAll("Tester: server restart");
        Assert.Equal("Tester: server restart", window.Chat.Text);
        test.Fake.RaiseToAll("Bob: ok");
        Assert.Equal("Tester: server restart\nBob: ok", window.Chat.Text);
        Shots.Save(window, "toalls-dark");
    }

    [AvaloniaFact]
    public void A_refused_send_keeps_the_text_and_close_hides()
    {
        using var test = new TestSession();
        ToallsWindow window = test.Show(new ToallsWindow(test.Context), 360, 240);
        test.Fake.SendResult = false;
        TestUi.Find<TextBox>(window, "ToallEntry").Text = "nope";
        window.SendEntry();
        TestUi.Settle();
        Assert.Equal("nope", TestUi.Find<TextBox>(window, "ToallEntry").Text);
        Assert.Equal(string.Empty, window.Chat.Text);
        TestUi.Click(TestUi.Find<Button>(window, "CloseButton"));
        Assert.False(window.IsVisible);
        Assert.False(window.IsClosed);
    }

    [AvaloniaFact]
    public void After_a_reconnect_it_sends_on_the_new_connection_and_never_the_old_one()
    {
        using var test = new TestSession(theme: "light");
        ToallsWindow window = test.Show(new ToallsWindow(test.Context), 360, 240);
        FakeRcSession old = test.Fake;
        var second = new FakeRcSession("tester");
        test.Context.Replace(second, test.Context.Login);
        old.RaiseToAll("Old: ignored");
        second.RaiseToAll("New: shown");
        Assert.Equal("New: shown", window.Chat.Text);
        TestUi.Find<TextBox>(window, "ToallEntry").Text = "hello";
        window.SendEntry();
        Assert.False(old.WasCalled("SendToAllAsync"));
        Assert.True(second.WasCalled("SendToAllAsync"));
        Shots.Save(window, "toalls-light");
    }
}

public sealed class LocalNpcsDialogTests
{
    [AvaloniaFact]
    public void Ok_requests_the_level_in_lower_case()
    {
        using var test = new TestSession();
        LocalNpcsDialog dialog = test.Show(new LocalNpcsDialog(test.Context), 320, 110);
        Assert.Equal("Local NPCs - Test Server", dialog.Title);
        Shots.Save(dialog, "local-npcs-dialog-dark");
        TestUi.Find<TextBox>(dialog, "LevelBox").Text = "OnlineStart.NW";
        TestUi.Click(TestUi.Find<Button>(dialog, "OkButton"));
        Assert.Equal("onlinestart.nw", test.Fake.LastCall("RequestLocalNpcsAsync")!.Argument<string>(0));
        Assert.True(dialog.IsClosed);
    }

    [AvaloniaFact]
    public void Cancel_and_an_empty_level_request_nothing()
    {
        using var test = new TestSession(theme: "light");
        LocalNpcsDialog dialog = test.Show(new LocalNpcsDialog(test.Context), 320, 110);
        Shots.Save(dialog, "local-npcs-dialog-light");
        dialog.Submit();
        Assert.False(test.Fake.WasCalled("RequestLocalNpcsAsync"));
        LocalNpcsDialog second = test.Show(new LocalNpcsDialog(test.Context), 320, 110);
        TestUi.Find<TextBox>(second, "LevelBox").Text = "x.nw";
        TestUi.Click(TestUi.Find<Button>(second, "CancelButton"));
        Assert.False(test.Fake.WasCalled("RequestLocalNpcsAsync"));
    }

    [AvaloniaFact]
    public void The_dialog_needs_the_nc_and_a_disconnected_session_is_never_asked()
    {
        using var test = new TestSession();
        SessionFeature.Register(test.Tools);
        Assert.Null(test.Context.OpenTool(ToolIds.LocalNpcs));
        test.Fake.IsNcConnected = test.Fake.IsNcAuthenticated = true;
        var dialog = (LocalNpcsDialog)test.Context.OpenTool(ToolIds.LocalNpcs)!;
        test.Fake.RaiseDisconnected("gone");
        foreach (RcDialog question in Dialogs()) question.Cancel();
        TestUi.Find<TextBox>(dialog, "LevelBox").Text = "a.nw";
        dialog.Submit();
        Assert.False(test.Fake.WasCalled("RequestLocalNpcsAsync"));
    }

    [AvaloniaFact]
    public void The_result_window_in_light_theme()
    {
        using var test = new TestSession(theme: "light");
        var result = test.Show(new LocalNpcsResultWindow(test.Context, "a.nw", "[npc]\nimage = block.png\nx = 30"), 520, 380);
        Shots.Save(result, "local-npcs-result-light");
        TestUi.Click(TestUi.Find<Button>(result, "CloseButton"));
        Assert.True(result.IsClosed);
    }
}

public sealed class DebugInfoWindowTests
{
    [AvaloniaFact]
    public async Task It_shows_the_sections_and_refreshes_and_copies()
    {
        using var test = new TestSession();
        test.RecordTools(ToolIds.PlayerList);
        test.Fake.PlayerList.Add(Player(1, "alice"));
        MainWindow main = ShowMain(test);
        test.Fake.RaiseIrcMessage("#a", "<x> y");
        DebugInfoWindow window = test.Show(new DebugInfoWindow(test.Context), 560, 520);
        Assert.Equal("Debug Information - Test Server", window.Title);
        string text = window.Text;
        foreach (string expected in new[] { "[Server]", "Server Name = Test Server", "IP = 127.0.0.1", "Port = 14900", "Address = 127.0.0.1:14900", "Main Connection = Connected (Authenticated)",
                     "NPC Server = Disconnected", "Protocol = New Protocol (v2)", "[Session]", "Account = tester", "Nickname = Tester", "Activity Status = Active", "Players Online = 1",
                     "[Memory Usage]", "Working Set (RAM) = ", "[Client & Environment]", "Build Date = " + BuildInfo.Date, "Active Tabs = 2", "Joined IRC Channels = 1" })
            Assert.Contains(expected, text);
        Shots.Save(window, "debug-info-dark");
        TestUi.Find<Button>(window, "RefreshButton").Focus();
        TestUi.PressKey(window, Key.F1);
        Assert.Equal("Debug stats refreshed.", window.Status);
        TestUi.Click(TestUi.Find<Button>(window, "CopyButton"));
        await TestUi.WaitUntilAsync(() => window.Status == "Debug information copied to clipboard!");
        TestUi.PressKey(window, Key.Escape);
        Assert.True(window.IsClosed);
        _ = main;
    }

    [AvaloniaFact]
    public void F1_in_the_main_window_opens_it_once()
    {
        using var test = new TestSession(theme: "light");
        SessionFeature.Register(test.Tools);
        MainWindow main = ShowMain(test);
        TestUi.PressKey(main, Key.F1);
        TestUi.PressKey(main, Key.F1);
        DebugInfoWindow window = Assert.Single(RcWindow.OpenWindows.OfType<DebugInfoWindow>());
        window.Width = 560;
        window.Height = 520;
        TestUi.Settle();
        Shots.Save(window, "debug-info-light");
        test.Fake.RaiseDisconnected("gone");
        foreach (RcDialog dialog in Dialogs()) dialog.Cancel();
        window.Refresh(true);
        Assert.Contains("Main Connection = Disconnected", window.Text);
    }
}

public sealed class SessionFeatureTests
{
    [AvaloniaFact]
    public void Register_adds_every_tool_of_the_group_and_each_opens_once()
    {
        using var test = new TestSession();
        SessionFeature.Register(test.Tools);
        foreach (string tool in new[] { ToolIds.MainWindow, ToolIds.Toalls, ToolIds.RcOptions, ToolIds.LocalNpcs, ToolIds.DebugInfo })
            Assert.True(test.Tools.IsRegistered(tool), tool);
        Window main = test.Context.OpenTool(ToolIds.MainWindow)!;
        Assert.IsType<MainWindow>(main);
        Assert.Same(main, test.Context.OpenTool(ToolIds.MainWindow));
        Window toalls = test.Context.OpenTool(ToolIds.Toalls)!;
        Assert.Same(toalls, test.Context.OpenTool(ToolIds.Toalls));
        Window options = test.Context.OpenTool(ToolIds.RcOptions)!;
        Assert.Same(options, test.Context.OpenTool(ToolIds.RcOptions));
        Assert.IsType<DebugInfoWindow>(test.Context.OpenTool(ToolIds.DebugInfo));
    }

    [AvaloniaFact]
    public void Ending_the_session_closes_every_window_without_asking()
    {
        using var test = new TestSession();
        test.Shell.TrayAvailable = false;
        SessionFeature.Register(test.Tools);
        var main = (MainWindow)test.Context.OpenTool(ToolIds.MainWindow)!;
        var toalls = (ToallsWindow)test.Context.OpenTool(ToolIds.Toalls)!;
        test.Context.End();
        TestUi.Settle();
        Assert.True(main.IsClosed);
        Assert.True(toalls.IsClosed);
        Assert.Empty(Dialogs());
        test.Fake.RaiseRcChat("after the end");
    }

    [AvaloniaFact]
    public void Closing_the_main_window_in_the_middle_of_a_dialog_does_not_act_on_the_session()
    {
        using var test = new TestSession();
        MainWindow main = ShowMain(test);
        test.Fake.RaiseDisconnected("bye");
        RcDialog dialog = TestUi.FindDialog();
        main.Close();
        dialog.Accept();
        TestUi.Settle();
        Assert.DoesNotContain("Reconnect", test.Shell.Requests);
        Assert.DoesNotContain("ShowServerList", test.Shell.Requests);
    }
}
