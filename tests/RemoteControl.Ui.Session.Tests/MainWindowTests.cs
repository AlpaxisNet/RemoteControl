using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Scripting;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;
using static RemoteControl.Ui.Session.Tests.TestHelpers;

namespace RemoteControl.Ui.Session.Tests;

public sealed class MainWindowTests
{
    private static readonly string[] s_allTools =
    [
        ToolIds.PlayerList, ToolIds.Accounts, ToolIds.Toalls, ToolIds.RcOptions, ToolIds.FileBrowser, ToolIds.Sync, ToolIds.ServerOptions, ToolIds.ServerFlags,
        ToolIds.FolderConfig, ToolIds.Npcs, ToolIds.Classes, ToolIds.Weapons, ToolIds.Levels, ToolIds.LocalNpcs, ToolIds.Extensions, ToolIds.DebugInfo, ToolIds.ScriptEditor,
    ];

    private static TestSession NewSession(Func<Core.RcOptions, Core.RcOptions>? options = null, string? theme = null)
    {
        var test = new TestSession(theme: theme, options: options);
        test.RecordTools(s_allTools);
        return test;
    }

    [AvaloniaFact]
    public void The_title_shows_server_players_sync_and_build_date()
    {
        using TestSession test = NewSession();
        test.Fake.PlayerList.Add(Player(1, "alice"));
        test.Fake.PlayerList.Add(Player(2, "bob"));
        MainWindow window = ShowMain(test);
        Assert.Equal("Test Server [Players: 2] - " + BuildInfo.Date, window.Title);
        Assert.Equal("Players: 2", window.PlayersLabel.Text);
        Assert.Equal("Server: Test Server", window.ServerLabel.Text);

        var sync = new FakeSync { Percent = 40 };
        using IDisposable offered = test.Context.Services.Provide<ISyncStatus>(sync);
        TestUi.Settle();
        Assert.Equal("Test Server [Players: 2] [Sync: 40%] - " + BuildInfo.Date, window.Title);
    }

    [AvaloniaFact]
    public void Every_menu_item_opens_its_tool()
    {
        using TestSession test = NewSession(o => o with { GraphicalMenu = false, ExtensionsEnabled = true });
        test.Fake.IsNcConnected = test.Fake.IsNcAuthenticated = true;
        MainWindow window = ShowMain(test);
        window.Controller.Tick();
        TestUi.Settle();
        var expected = new (string Menu, string Tool)[]
        {
            ("MenuPlayerList", ToolIds.PlayerList), ("MenuAccounts", ToolIds.Accounts), ("MenuToalls", ToolIds.Toalls), ("MenuFileBrowser", ToolIds.FileBrowser),
            ("MenuRcOptions", ToolIds.RcOptions), ("MenuServerOptions", ToolIds.ServerOptions), ("MenuFolderConfig", ToolIds.FolderConfig), ("MenuNpcs", ToolIds.Npcs),
            ("MenuClasses", ToolIds.Classes), ("MenuWeapons", ToolIds.Weapons), ("MenuServerFlags", ToolIds.ServerFlags), ("MenuLocalNpcs", ToolIds.LocalNpcs),
            ("MenuExtensions", ToolIds.Extensions),
        };
        foreach ((string menu, string tool) in expected)
        {
            test.OpenedTools.Clear();
            ClickMenu(TestUi.Find<MenuItem>(window, menu));
            Assert.Equal([tool], test.OpenedTools.Select(opened => opened.ToolId));
        }
        ClickMenu(TestUi.Find<MenuItem>(window, "MenuServerList"));
        Assert.Contains("ShowServerList", test.Shell.Requests);
        ClickMenu(TestUi.Find<MenuItem>(window, "MenuSignOut"));
        Assert.Contains("SignOut", test.Shell.Requests);
        Assert.True(TestUi.Find<Menu>(window, "MainMenu").IsVisible);
        Shots.Save(window, "main-classic-dark");
    }

    [AvaloniaFact]
    public void Every_header_button_opens_its_tool()
    {
        using TestSession test = NewSession(o => o with { ExtensionsEnabled = true, SyncEnabled = true, LevelListEnabled = true });
        test.Fake.IsNcConnected = test.Fake.IsNcAuthenticated = true;
        MainWindow window = ShowMain(test);
        window.Controller.Tick();
        TestUi.Settle();
        for (int index = 0; index < 15; index++)
        {
            ImageButton button = window.HeaderButtons[index];
            Assert.True(button.IsVisible, Core.RcOptions.ButtonNames[index]);
            test.OpenedTools.Clear();
            TestUi.Click(button);
            Assert.Equal([HeaderLayout.Tools[index]], test.OpenedTools.Select(opened => opened.ToolId));
        }
        Assert.Equal("Options", ToolTip.GetTip(window.HeaderButtons[4]));
        Shots.Save(window, "main-graphical-nc-dark");
    }

    [AvaloniaFact]
    public void A_header_button_shows_its_pressed_picture_while_held()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        ImageButton help = window.HeaderButtons[0];
        TestUi.MouseDown(help);
        Assert.True(help.IsPressed);
        Assert.Empty(test.OpenedTools);
        TestUi.MouseUp(help);
        Assert.False(help.IsPressed);
        Assert.Single(test.OpenedTools);
    }

    [AvaloniaFact]
    public void Nc_buttons_follow_the_nc_state()
    {
        using TestSession test = NewSession(o => o with { LevelListEnabled = true });
        MainWindow window = ShowMain(test);
        int[] ncButtons = [8, 9, 10, 11, 13];
        Assert.All(ncButtons, index => Assert.False(window.HeaderButtons[index].IsVisible));
        Assert.False(window.NpcServerLabel.IsVisible);
        TestUi.Click(window.HeaderButtons[0]);
        test.OpenedTools.Clear();
        window.OpenTool(ToolIds.Npcs);
        Assert.Empty(test.OpenedTools);

        test.Fake.NcServerAddress = "127.0.0.1,1";
        window.Controller.Tick();
        TestUi.Settle();
        Assert.All(ncButtons, index => Assert.True(window.HeaderButtons[index].IsVisible));
        Assert.True(window.NpcServerLabel.IsVisible);
        Assert.Equal(460, Canvas.GetLeft(window.HeaderButtons[13]));
        Assert.Equal(360, Canvas.GetLeft(window.HeaderButtons[11]));

        test.Fake.RaiseNcDisconnected("down");
        window.Controller.Tick();
        TestUi.Settle();
        Assert.All(ncButtons, index => Assert.False(window.HeaderButtons[index].IsVisible));
    }

    [AvaloniaFact]
    public void Optional_buttons_follow_the_options_and_right_buttons_follow_the_width()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test, width: 700);
        Assert.False(window.HeaderButtons[12].IsVisible);
        Assert.False(window.HeaderButtons[14].IsVisible);
        test.Environment.SetOptions(o => o with { ExtensionsEnabled = true, SyncEnabled = true });
        Assert.True(window.HeaderButtons[12].IsVisible);
        Assert.True(window.HeaderButtons[14].IsVisible);
        Assert.Equal(104, Canvas.GetLeft(window.HeaderButtons[12]));
        Assert.Equal(394 + 200, Canvas.GetLeft(window.HeaderButtons[4]));
        Assert.Equal(5, Canvas.GetLeft(window.HeaderButtons[0]));
    }

    [AvaloniaFact]
    public void The_file_browser_is_refused_while_files_are_synchronised()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        var sync = new FakeSync { IsFileSyncActive = true, Percent = 10 };
        using IDisposable offered = test.Context.Services.Provide<ISyncStatus>(sync);
        TestUi.Settle();
        Assert.False(window.HeaderButtons[1].IsEnabled);
        window.OpenTool(ToolIds.FileBrowser);
        Assert.Empty(test.OpenedTools);
        sync.IsFileSyncActive = false;
        sync.Raise();
        TestUi.Settle();
        Assert.True(window.HeaderButtons[1].IsEnabled);
        window.OpenTool(ToolIds.FileBrowser);
        Assert.Single(test.OpenedTools);
    }

    [AvaloniaFact]
    public void Nothing_opens_while_not_connected_but_the_options()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        test.Fake.RaiseDisconnected("gone");
        foreach (RcDialog dialog in Dialogs()) dialog.Cancel();
        TestUi.Settle();
        test.OpenedTools.Clear();
        window.OpenTool(ToolIds.PlayerList);
        window.OpenTool(ToolIds.Toalls);
        Assert.Empty(test.OpenedTools);
        window.OpenTool(ToolIds.RcOptions);
        Assert.Equal([ToolIds.RcOptions], test.OpenedTools.Select(opened => opened.ToolId));
    }

    [AvaloniaFact]
    public void F1_opens_the_debug_information_and_F8_the_server_list()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        TestUi.PressKey(window, Key.F1);
        Assert.Equal([ToolIds.DebugInfo], test.OpenedTools.Select(opened => opened.ToolId));
        TestUi.PressKey(window, Key.F8);
        Assert.Contains("ShowServerList", test.Shell.Requests);
    }

    [AvaloniaFact]
    public void Server_chat_is_shown_and_engine_texts_are_hidden()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        test.Fake.RaiseRcChat("Admin: hello there");
        test.Fake.RaiseRcChat("GraalEngine,lister,ping");
        test.Fake.RaiseServerText("Server: restart soon");
        Assert.Contains("Admin: hello there", window.MainChat.Text);
        Assert.Contains("Server: restart soon", window.MainChat.Text);
        Assert.DoesNotContain("GraalEngine", window.MainChat.Text);
    }

    [AvaloniaFact]
    public void Alert_prefixes_ask_for_attention_with_the_right_kind()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        test.Fake.RaiseRcChat("#ALERT Server is restarting");
        Assert.Equal(["Attention:Alert"], test.Shell.Requests);
        test.Shell.Requests.Clear();
        test.Fake.RaiseRcChat("#ALERTSP ding");
        Assert.Equal(["Attention:AlertWithSound"], test.Shell.Requests);
        test.Shell.Requests.Clear();
        test.Fake.RaiseRcChat("#ALERTSF quiet flash");
        Assert.Equal(["Attention:Alert"], test.Shell.Requests);
        test.Shell.Requests.Clear();
        test.Fake.RaiseRcChat("plain line");
        Assert.Empty(test.Shell.Requests);
        Assert.Contains("Server is restarting", window.MainChat.Text);
        Assert.DoesNotContain("#ALERT", window.MainChat.Text);
    }

    [AvaloniaFact]
    public void An_admin_message_is_an_alert_line_of_the_chat()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        test.Fake.RaiseAdminMessage("Admin Staff:\nThe server restarts in 5 minutes.");
        Assert.Contains("Admin Staff: The server restarts in 5 minutes.", window.MainChat.Text);
        Assert.Equal(["Attention:Alert"], test.Shell.Requests);
        test.Fake.RaiseAdminMessage("Maintenance tonight");
        Assert.Contains("Admin message: Maintenance tonight", window.MainChat.Text);
        test.Fake.RaiseAdminMessage("Server message:\nThere is a message limit of 1024 characters.");
        Assert.Contains("Server message: There is a message limit of 1024 characters.", window.MainChat.Text);
        Assert.DoesNotContain("Admin message: Server message", window.MainChat.Text);
        test.Fake.RaiseAdminMessage("\n ");
        Assert.DoesNotContain("#ALERT", window.MainChat.Text);
    }

    [AvaloniaFact]
    public void A_private_message_without_its_window_alerts_in_the_chat()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        test.Fake.RaisePrivateMessage(3, "alice", "Alice", "hi");
        Assert.Contains("New PM from Alice (alice)", window.MainChat.Text);
        Assert.Equal(["Attention:PrivateMessage"], test.Shell.Requests);
        test.Shell.Requests.Clear();
        test.Environment.SetOptions(o => o with { NoMassMessages = true });
        test.Fake.RaisePrivateMessage(4, "bob", "Bob", "everyone", PrivateMessageKind.Mass);
        Assert.Empty(test.Shell.Requests);
    }

    [AvaloniaFact]
    public async Task A_main_chat_line_is_written_to_the_chat_log()
    {
        using TestSession test = NewSession();
        string log = Path.Combine(test.Environment.UserDirectory, "logs", "chat.txt");
        test.Environment.SetOptions(o => o with { LogRcChat = true, ChatLogFile = log, RcTimestamps = false });
        MainWindow window = ShowMain(test);
        test.Fake.RaiseRcChat("#ALERT logged line");
        test.Fake.RaiseRcChat("second line");
        await ChatLog.FlushAsync();
        Assert.Equal("#ALERT logged line\nsecond line\n", await File.ReadAllTextAsync(log));
        test.Environment.SetOptions(o => o with { LogRcChat = false });
        test.Fake.RaiseRcChat("not logged");
        await ChatLog.FlushAsync();
        Assert.DoesNotContain("not logged", await File.ReadAllTextAsync(log));
        _ = window;
    }

    [AvaloniaFact]
    public void An_option_change_re_themes_and_re_fonts_the_chat_at_once()
    {
        using TestSession test = NewSession(o => o with { ColorChatBack = "#102030", ChatFontSize = 9 });
        MainWindow window = ShowMain(test);
        TestUi.Settle();
        var editor = window.MainChat.Editor;
        Assert.Equal(Color.Parse("#102030"), ((ISolidColorBrush)editor.Background!).Color);
        double before = editor.FontSize;
        test.Environment.SetOptions(o => o with { ColorChatBack = "#405060", ChatFontSize = 14 });
        Assert.Equal(Color.Parse("#405060"), ((ISolidColorBrush)editor.Background!).Color);
        Assert.True(editor.FontSize > before);
    }

    [AvaloniaFact]
    public void The_graphical_menu_option_switches_the_layout_at_once()
    {
        using TestSession test = NewSession(o => o with { GraphicalMenu = true });
        MainWindow window = ShowMain(test);
        Assert.False(TestUi.Find<Menu>(window, "MainMenu").IsVisible);
        Assert.True(TestUi.Find<Panel>(window, "Header").IsVisible);
        test.Environment.SetOptions(o => o with { GraphicalMenu = false });
        Assert.True(TestUi.Find<Menu>(window, "MainMenu").IsVisible);
        Assert.False(TestUi.Find<Panel>(window, "Header").IsVisible);
        test.Environment.SetOptions(o => o with { LabelServers = "Srv:" , GraphicalMenu = true });
        Assert.Equal("Srv: Test Server", window.ServerLabel.Text);
    }

    [AvaloniaFact]
    public void Closing_with_a_tray_hides_and_without_asks_before_signing_out()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        test.Shell.TrayAvailable = true;
        window.RequestClose();
        TestUi.Settle();
        Assert.False(window.IsVisible);
        Assert.False(window.IsClosed);
        Assert.DoesNotContain("SignOut", test.Shell.Requests);

        window.ShowAndActivate();
        test.Shell.TrayAvailable = false;
        window.RequestClose();
        RcDialog question = TestUi.FindDialog();
        Assert.Equal("Sign out of Test Server?", question.Message);
        question.Cancel();
        TestUi.Settle();
        Assert.DoesNotContain("SignOut", test.Shell.Requests);
        window.RequestClose();
        TestUi.FindDialog().Accept();
        TestUi.WaitUntil(() => test.Shell.Requests.Contains("SignOut"));
    }

    [AvaloniaFact]
    public void A_disconnect_shows_one_dialog_and_reconnect_goes_through_the_shell()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        var second = new FakeRcSession("tester");
        second.PlayerList.Add(Player(9, "zed"));
        test.Shell.Reconnect = context =>
        {
            context.Replace(second, context.Login);
            return Task.FromResult(ConnectResult.Ok);
        };
        test.Fake.RaiseDisconnected("Kicked by server");
        RcDialog dialog = TestUi.FindDialog();
        Assert.Single(Dialogs());
        Assert.Equal("Connection Error", dialog.Title);
        Assert.Equal("Kicked by server", dialog.Message);
        Shots.Save(dialog, "connection-error");
        dialog.Accept();
        TestUi.WaitUntil(() => test.Shell.Requests.Contains("Reconnect"));
        Assert.Same(second, test.Context.Session);
        TestUi.Settle();
        Assert.Contains("[Players: 1]", window.Title);
        second.RaiseRcChat("Back: online");
        Assert.Contains("Back: online", window.MainChat.Text);
    }

    [AvaloniaFact]
    public void The_window_offers_and_withdraws_the_session_chat()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        ISessionChat chat = test.Context.Services.Get<ISessionChat>()!;
        Assert.Same(window, chat);
        Assert.Same(window, chat.Window);
        Assert.Equal([MainWindow.MainTabName], chat.Tabs);
        chat.AppendLine("Ext: from an extension");
        Assert.Contains("Ext: from an extension", window.MainChat.Text);
        chat.AppendToTab("MyExtension", "output line");
        Assert.Equal([MainWindow.MainTabName, "MyExtension"], chat.Tabs);
        Assert.Contains("output line", window.TabNamed("MyExtension")!.View!.Text);
        chat.AppendToTab(MainWindow.MainTabName, "to main");
        Assert.Contains("to main", window.MainChat.Text);
        chat.RemoveTab(MainWindow.MainTabName);
        chat.RemoveTab("MyExtension");
        Assert.Equal([MainWindow.MainTabName], chat.Tabs);
        Assert.True(chat.Send("hello all"));
        Assert.Equal("hello all", Sent(test.Fake).Last());
        window.Hide();
        chat.Activate();
        Assert.True(window.IsVisible);
        window.Close();
        Assert.Null(test.Context.Services.Get<ISessionChat>());
    }

    [AvaloniaFact]
    public void Focus_clears_the_unread_private_messages()
    {
        using TestSession test = NewSession();
        var pms = new FakePrivateMessages();
        using IDisposable offered = test.Context.Services.Provide<IPrivateMessages>(pms);
        MainWindow window = ShowMain(test);
        window.Activate();
        TestUi.Settle();
        Assert.True(pms.Cleared > 0);
    }

    [AvaloniaFact]
    public void Key_input_is_reported_as_activity()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        int before = test.Shell.ActivityCount;
        TestUi.Find<TextBox>(window, "ChatEntry").Focus();
        TestUi.PressKey(window, Key.A);
        Assert.True(test.Shell.ActivityCount > before);
        window.Close();
        int closed = test.Shell.ActivityCount;
        TestUi.Settle();
        Assert.Equal(closed, test.Shell.ActivityCount);
    }

    [AvaloniaFact]
    public void Irc_lines_get_a_tab_that_sends_and_closes()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        test.Fake.RaiseIrcMessage("#graal", "<bob> hey");
        ChatTab tab = window.TabNamed("#graal")!;
        Assert.Contains("<bob>: hey", tab.View!.Text);
        Assert.Contains("#graal", window.Controller.JoinedIrcChannels);
        TestUi.Find<TabControl>(window, "ChatTabs").SelectedItem = tab.Item;
        Enter(window, "hello channel");
        RecordedCall irc = test.Fake.LastCall("SendIrcTextAsync")!;
        Assert.Equal("privmsg", irc.Argument<string>(0));
        Assert.Equal(["#graal", "hello channel"], irc.Argument<string[]>(1));

        test.Fake.SendResult = false;
        Enter(window, "lost");
        Assert.Contains("* Failed to send message", tab.View.Text);
        test.Fake.SendResult = true;

        MenuItem close = ((ContextMenu)tab.Item.ContextMenu!).Items.OfType<MenuItem>().Single();
        ClickMenu(close);
        Assert.Null(window.TabNamed("#graal"));
        Assert.Equal(["part"], test.Fake.CallsTo("IrcPartAsync").Select(_ => "part"));

        test.Fake.RaiseIrcMessage("#other", "<x> y");
        test.Fake.RaiseIrcMessage("#other", "* Left #other");
        Assert.Null(window.TabNamed("#other"));
    }

    [AvaloniaFact]
    public void Nc_chat_goes_to_its_own_tab_with_separate_nc()
    {
        using TestSession test = NewSession(o => o with { SeparateNc = true });
        MainWindow window = ShowMain(test);
        test.Fake.RaiseNcChat("NC: before");
        Assert.Contains("NC: before", window.MainChat.Text);
        test.Fake.NcServerAddress = "127.0.0.1,1";
        window.Controller.Tick();
        TestUi.Settle();
        Assert.NotNull(window.TabNamed(MainWindow.NcTabName));
        test.Fake.RaiseNcChat("NC: after");
        Assert.Contains("NC: after", window.TabNamed(MainWindow.NcTabName)!.View!.Text);
        test.Environment.SetOptions(o => o with { SeparateNc = false });
        Assert.Null(window.TabNamed(MainWindow.NcTabName));
    }

    [AvaloniaFact]
    public void A_local_npc_dump_answer_opens_a_result_window()
    {
        using TestSession test = NewSession();
        ShowMain(test);
        test.Fake.RaiseLocalNpcs("onlinestartlocal.nw", "[npc1]\nx = 30");
        LocalNpcsResultWindow result = RcWindow.OpenWindows.OfType<LocalNpcsResultWindow>().Single();
        Assert.Equal("Local NPCs - Test Server", result.Title);
        Assert.Contains("x = 30", result.Editor.Text);
        Shots.Save(result, "local-npcs-result-dark");
        result.Close();
    }

    [AvaloniaFact]
    public void Find_results_go_to_their_tab_and_link_to_the_folder()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        Enter(window, "/find *.nw");
        Assert.Equal("/find *.nw", Sent(test.Fake).Last());
        ChatTab find = window.TabNamed(MainWindow.FindTabName)!;
        test.Fake.RaiseRcChat("Game files found (relative to levels/, max 100):");
        test.Fake.RaiseRcChat("[12:00] maps/start.nw: 1234 byte, 2026-01-01");
        test.Fake.RaiseRcChat("Admin: normal chat");
        Assert.Equal(2, find.Results!.Count);
        Assert.Equal("levels/maps/", find.Results[1].Folder);
        Assert.Contains("normal chat", window.MainChat.Text);
        Assert.DoesNotContain("start.nw", window.MainChat.Text);
        TestUi.Settle();
        Shots.Save(window, "main-find-results-dark");
        Button link = TestUi.FindAll<Button>(window).First(button => button.Name == "FindLink");
        TestUi.Click(link);
        Assert.Equal((ToolIds.FileBrowser, (object?)"levels/maps/"), test.OpenedTools.Last());
    }

    [AvaloniaFact]
    public void A_url_in_the_chat_opens_through_the_url_opener()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        test.Fake.RaiseRcChat("See www.example.invalid/page now");
        int offset = window.MainChat.Text.IndexOf("www.", StringComparison.Ordinal);
        Assert.Equal("https://www.example.invalid/page", window.MainChat.LinkAt(offset + 2));
        // A test never starts a browser: the opener only records what it was asked for (ExternalPrograms).
        Assert.True(RemoteControl.Ui.Common.ExternalPrograms.Disabled);
        RemoteControl.Ui.Common.ExternalPrograms.ClearSuppressed();
        Assert.True(window.MainChat.ClickLinkAt(offset + 2));
        Assert.Contains("url https://www.example.invalid/page", RemoteControl.Ui.Common.ExternalPrograms.Suppressed);
    }

    [AvaloniaTheory]
    [InlineData("catppuccin", true)]
    [InlineData("light", true)]
    [InlineData("nord", false)]
    [InlineData("dark", false)]
    public void Pictures_of_the_main_window(string theme, bool graphical)
    {
        using TestSession test = NewSession(o => o with { GraphicalMenu = graphical, ExtensionsEnabled = true, SyncEnabled = true }, theme);
        test.Fake.NcServerAddress = "127.0.0.1,1";
        test.Fake.PlayerList.Add(Player(1, "alice", "Alice"));
        MainWindow window = ShowMain(test, width: 560, height: 400);
        window.Controller.Tick();
        test.Fake.RaiseRcChat("Admin: Welcome to the server Kappa");
        test.Fake.RaiseRcChat("#ALERT Restart in 5 minutes");
        test.Fake.RaiseRcChat("Info: see https://example.invalid/news for more");
        test.Fake.RaiseIrcMessage("#staff", "<bob> hello");
        TestUi.Settle();
        Shots.Save(window, "main-" + (graphical ? "graphical" : "classic") + "-" + theme);
        TextBox entry = TestUi.Find<TextBox>(window, "ChatEntry");
        entry.Focus();
        TestUi.TypeText(window, "/op");
        TestUi.Settle();
        Assert.True(window.IsCompletionOpen);
        Shots.Save(window, "main-completion-" + theme);
    }

    [AvaloniaFact]
    public void The_emoji_menu_inserts_at_the_cursor()
    {
        using TestSession test = NewSession();
        MainWindow window = ShowMain(test);
        TextBox entry = TestUi.Find<TextBox>(window, "ChatEntry");
        entry.Text = "hi ";
        entry.CaretIndex = 3;
        window.ShowEmojiFlyout();
        TestUi.Settle();
        Button first = window.EmojiPanel!.Children.OfType<Button>().First();
        first.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        TestUi.Settle();
        Assert.Equal("hi " + MainWindow.Emoji[0], entry.Text);
    }

    internal sealed class FakeSync : ISyncStatus
    {
        public int? Percent { get; set; }

        public bool IsFileSyncActive { get; set; }

        public event EventHandler? Changed;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    internal sealed class FakePrivateMessages : IPrivateMessages
    {
        public int Cleared { get; private set; }

        public bool HasUnread => false;

        public event EventHandler? UnreadChanged
        {
            add { }
            remove { }
        }

        public bool OpenLatest() => false;

        public void ClearUnread() => Cleared++;
    }

    internal static ApiDefinition Definition(string name, string description) => new(name, "function", ["a"], "int", "server", description);
}
