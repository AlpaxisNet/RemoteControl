using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RemoteControl.Scripting;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;
using static RemoteControl.Ui.Session.Tests.TestHelpers;

namespace RemoteControl.Ui.Session.Tests;

public sealed class ChatCommandTests
{
    private static (TestSession Test, MainWindow Window) Open(Func<Core.RcOptions, Core.RcOptions>? options = null)
    {
        var test = new TestSession(options: options);
        test.RecordTools(ToolIds.DebugInfo, ToolIds.FileBrowser);
        return (test, ShowMain(test));
    }

    private static TextBox Entry(MainWindow window) => TestUi.Find<TextBox>(window, "ChatEntry");

    [AvaloniaFact]
    public void Plain_text_and_server_commands_are_sent_and_the_entry_cleared()
    {
        (TestSession test, MainWindow window) = Open();
        using (test)
        {
            Enter(window, "  hello world  ");
            Assert.Equal("hello world", Sent(test.Fake).Last());
            Assert.Equal(string.Empty, Entry(window).Text);
            Enter(window, "/stats");
            Assert.Equal("/stats", Sent(test.Fake).Last());
            Enter(window, "   ");
            Assert.Equal(2, Sent(test.Fake).Count(line => !line.StartsWith("/npc newrc", StringComparison.Ordinal)));
        }
    }

    [AvaloniaFact]
    public void A_refused_send_shows_the_reason()
    {
        (TestSession test, MainWindow window) = Open();
        using (test)
        {
            test.Fake.SendResult = false;
            test.Fake.LastError = "Text too long";
            Enter(window, "something");
            Assert.Contains("Text too long", window.MainChat.Text);
        }
    }

    [AvaloniaTheory]
    [InlineData("/open")]
    [InlineData("/openacc")]
    [InlineData("/opencomments")]
    [InlineData("/openrights")]
    [InlineData("/openaccess")]
    [InlineData("/openprofile")]
    [InlineData("/openban")]
    [InlineData("/reset")]
    public void Account_commands_without_an_account_get_your_own(string command)
    {
        (TestSession test, MainWindow window) = Open();
        using (test)
        {
            Enter(window, command);
            Assert.Equal(command + " tester", Sent(test.Fake).Last());
            Enter(window, command + " alice");
            Assert.Equal(command + " alice", Sent(test.Fake).Last());
        }
    }

    [AvaloniaFact]
    public void Rchelp_and_help_show_the_program_commands()
    {
        (TestSession test, MainWindow window) = Open();
        using (test)
        {
            Enter(window, "/rchelp");
            Assert.Contains("RC commands:", window.MainChat.Text);
            Assert.Contains("/nc connect (or /nc c)", window.MainChat.Text);
            Assert.Contains("Emotes: type Kappa, PMNormal, or :v in chat", window.MainChat.Text);
            Assert.DoesNotContain("/rchelp", Sent(test.Fake));
            Enter(window, "/help");
            Assert.Equal("/help", Sent(test.Fake).Last());
            Assert.Contains("RC: /rchelp - show Remote Control commands", window.MainChat.Text);
        }
    }

    [AvaloniaFact]
    public void Clear_and_clear_all_empty_the_tabs()
    {
        (TestSession test, MainWindow window) = Open();
        using (test)
        {
            test.Fake.RaiseRcChat("one");
            test.Fake.RaiseIrcMessage("#c", "<a> two");
            Enter(window, "/clear");
            Assert.Equal(string.Empty, window.MainChat.Text);
            Assert.Contains("two", window.TabNamed("#c")!.View!.Text);
            test.Fake.RaiseRcChat("three");
            Enter(window, "/clear all");
            Assert.Equal(string.Empty, window.MainChat.Text);
            Assert.Equal(string.Empty, window.TabNamed("#c")!.View!.Text);
            Assert.DoesNotContain("/clear", Sent(test.Fake));
        }
    }

    [AvaloniaFact]
    public void Nc_commands_work_and_others_show_the_usage()
    {
        (TestSession test, MainWindow window) = Open();
        using (test)
        {
            test.Fake.NcServerAddress = "127.0.0.1,1";
            window.Controller.Tick();
            Assert.True(test.Fake.IsNcAuthenticated);
            Enter(window, "/NC dc");
            Assert.True(test.Fake.WasCalled("DisconnectNc"));
            Assert.True(window.Controller.IsNcManuallyDisconnected);
            Enter(window, "/nc disconnect");
            Assert.Contains("The NPC server is not connected.", window.MainChat.Text);
            Enter(window, "/nc connect");
            Assert.False(window.Controller.IsNcManuallyDisconnected);
            window.Controller.Tick();
            Assert.Equal(2, test.Fake.CallsTo("ConnectNcAsync").Count);
            Enter(window, "/nc rc");
            Enter(window, "/nc c");
            Enter(window, "/nc what");
            Assert.Contains("Usage: /nc connect, /nc disconnect, or /nc rc", window.MainChat.Text);
            Assert.DoesNotContain(Sent(test.Fake), line => line.StartsWith("/nc", StringComparison.OrdinalIgnoreCase));
        }
    }

    [AvaloniaFact]
    public void Reconnect_goes_through_the_shell()
    {
        (TestSession test, MainWindow window) = Open();
        using (test)
        {
            Enter(window, "/rc");
            TestUi.WaitUntil(() => test.Shell.Requests.Contains("Reconnect"));
            TestUi.WaitUntil(() => window.MainChat.Text.Contains("not connected in tests", StringComparison.Ordinal));
            Assert.DoesNotContain("/rc", Sent(test.Fake));
            test.Shell.Requests.Clear();
            Enter(window, "/reconnect");
            TestUi.WaitUntil(() => test.Shell.Requests.Contains("Reconnect"));
        }
    }

    [AvaloniaFact]
    public async Task Scripthelp2_searches_the_script_api_locally()
    {
        ScriptHelp.Reset();
        ScriptHelp.Source = _ => Task.FromResult<IReadOnlyList<ApiDefinition>>(
        [
            new ApiDefinition("findplayer", "function", ["account"], "object", "server", "Finds a player"),
            new ApiDefinition("findnpc", "function", ["name"], "object", "server", "Finds an NPC"),
        ]);
        try
        {
            (TestSession test, MainWindow window) = Open();
            using (test)
            {
                Enter(window, "/scripthelp2 findplayer");
                await window.PendingScriptHelp!;
                Assert.Contains("[Script Help] findplayer(account) [SERVER]", window.MainChat.Text);
                Assert.Contains("Returns: object", window.MainChat.Text);
                Enter(window, "/scripthelp2 find");
                await window.PendingScriptHelp!;
                Assert.Contains("[Script Help] 2 entries matching 'find':", window.MainChat.Text);
                Enter(window, "/scripthelp2 nothing");
                await window.PendingScriptHelp!;
                Assert.Contains("No API entries match 'nothing'", window.MainChat.Text);
                Assert.DoesNotContain(Sent(test.Fake), line => line.StartsWith("/scripthelp2", StringComparison.Ordinal));
            }
        }
        finally
        {
            ScriptHelp.Source = null;
            ScriptHelp.Reset();
        }
    }

    [AvaloniaFact]
    public void Not_connected_keeps_the_text_but_nc_commands_are_handled()
    {
        (TestSession test, MainWindow window) = Open();
        using (test)
        {
            test.Fake.RaiseDisconnected("gone");
            foreach (Common.RcDialog dialog in Dialogs()) dialog.Cancel();
            TestUi.Settle();
            test.Fake.ClearCalls();
            Enter(window, "hello?");
            Assert.Equal("hello?", Entry(window).Text);
            Assert.Empty(Sent(test.Fake));
            Enter(window, "/nc what");
            Assert.Equal(string.Empty, Entry(window).Text);
        }
    }

    [AvaloniaFact]
    public void Up_and_down_walk_the_history()
    {
        (TestSession test, MainWindow window) = Open();
        using (test)
        {
            Enter(window, "first");
            Enter(window, "second");
            Enter(window, "second");
            Assert.Equal(["second", "first"], window.History);
            TestUi.PressKey(window, Key.Up);
            Assert.Equal("second", Entry(window).Text);
            TestUi.PressKey(window, Key.Up);
            Assert.Equal("first", Entry(window).Text);
            TestUi.PressKey(window, Key.Up);
            Assert.Equal("first", Entry(window).Text);
            TestUi.PressKey(window, Key.Down);
            Assert.Equal("second", Entry(window).Text);
            TestUi.PressKey(window, Key.Down);
            Assert.Equal(string.Empty, Entry(window).Text);
            for (int index = 0; index < 40; index++) window.Execute("line " + index);
            Assert.Equal(MainWindow.HistoryLimit, window.History.Count);
        }
    }

    [AvaloniaFact]
    public void Tab_cycles_command_completions_and_shift_tab_goes_back()
    {
        (TestSession test, MainWindow window) = Open();
        using (test)
        {
            TextBox entry = Entry(window);
            entry.Focus();
            entry.Text = "/openr";
            entry.CaretIndex = 6;
            TestUi.PressKey(window, Key.Tab);
            Assert.Equal("/openrights ", entry.Text);
            entry.Text = "/nc ";
            entry.CaretIndex = 4;
            TestUi.PressKey(window, Key.Tab);
            Assert.Equal("/nc connect ", entry.Text);
            TestUi.PressKey(window, Key.Tab);
            Assert.Equal("/nc disconnect ", entry.Text);
            TestUi.PressKey(window, Key.Tab);
            Assert.Equal("/nc rc ", entry.Text);
            TestUi.PressKey(window, Key.Tab);
            Assert.Equal("/nc connect ", entry.Text);
            TestUi.PressKey(window, Key.Tab, Avalonia.Input.RawInputModifiers.Shift);
            Assert.Equal("/nc rc ", entry.Text);
        }
    }

    [AvaloniaFact]
    public void Mentions_and_account_arguments_complete_to_players()
    {
        (TestSession test, MainWindow window) = Open();
        using (test)
        {
            test.Fake.PlayerList.Add(Player(1, "Graal123", "Bob", "Knights"));
            test.Fake.PlayerList.Add(Player(2, "alice", "Ally"));
            test.Fake.PlayerList.Add(Player(3, "tester", "Me"));
            TextBox entry = Entry(window);
            entry.Focus();
            entry.Text = "hi @kni";
            entry.CaretIndex = entry.Text.Length;
            TestUi.PressKey(window, Key.Tab);
            Assert.Equal("hi @Graal123 ", entry.Text);
            entry.Text = "/open ali";
            entry.CaretIndex = entry.Text.Length;
            TestUi.PressKey(window, Key.Tab);
            Assert.Equal("/open alice", entry.Text);
            entry.Text = "@te";
            entry.CaretIndex = 3;
            Assert.False(window.CycleCompletion(true));
        }
    }

    [AvaloniaFact]
    public void Typing_shows_the_completion_list_and_enter_takes_the_selected_row()
    {
        (TestSession test, MainWindow window) = Open();
        using (test)
        {
            TextBox entry = Entry(window);
            entry.Focus();
            TestUi.TypeText(window, "/rch");
            Assert.True(window.IsCompletionOpen);
            Assert.Equal(["/rchelp"], window.CompletionRows.Select(row => row.Insertion));
            TestUi.PressKey(window, Key.Down);
            TestUi.PressKey(window, Key.Enter);
            Assert.Equal("/rchelp ", entry.Text);
            Assert.False(window.IsCompletionOpen);
            Assert.DoesNotContain("/rchelp", Sent(test.Fake));
            TestUi.PressKey(window, Key.Enter);
            Assert.Contains("RC commands:", window.MainChat.Text);
            TestUi.TypeText(window, "/zz");
            Assert.False(window.IsCompletionOpen);
            entry.Text = string.Empty;
            TestUi.TypeText(window, "/cl");
            Assert.True(window.IsCompletionOpen);
            TestUi.PressKey(window, Key.Escape);
            Assert.False(window.IsCompletionOpen);
        }
    }

    [AvaloniaFact]
    public void Ctrl_f_opens_the_find_bar_of_the_chat()
    {
        (TestSession test, MainWindow window) = Open();
        using (test)
        {
            test.Fake.RaiseRcChat("find me");
            TestUi.PressKey(window, Key.F, Avalonia.Input.RawInputModifiers.Control);
            Assert.NotNull(window.ChatTabList[0].Search);
            Assert.True(window.ChatTabList[0].Search!.IsOpened);
        }
    }

    [Fact]
    public void Completion_matching_follows_the_cpp_rules()
    {
        IReadOnlyList<CompletionItem> players = ChatCompletion.PlayerRows([Player(1, "Graal5", "Nick"), Player(2, "graal5", "Dup"), Player(3, "me")], "ME");
        Assert.Single(players);
        Assert.Equal("Graal5 - Nick", players[0].Display);
        Assert.Equal(50, ChatCompletion.Commands.Count);
        Assert.Contains(ChatCompletion.Match("/sc", 3, players), item => item.Insertion == "/scripthelp2");
        Assert.All(ChatCompletion.Match("/sc", 3, players), item => Assert.Equal(CompletionKind.Command, item.Kind));
        Assert.Empty(ChatCompletion.Match("hello", 5, players));
        Assert.Single(ChatCompletion.Match("@nic", 4, players));
        Assert.Empty(ChatCompletion.Match("/stats gr", 9, players));
        Assert.Single(ChatCompletion.Match("/disconnect gr", 14, players));
        Assert.Equal(["/nc disconnect"], ChatCompletion.Match("/nc d", 5, players).Select(item => item.Insertion));
        Assert.Equal(("/nc disconnect  now", 15), ChatCompletion.Apply("/nc d now", 5, ChatCompletion.Commands.Single(item => item.Insertion == "/nc disconnect")) is var applied ? (applied.Text, applied.Caret) : default);
        Assert.Equal("/disconnect <account> <reason>", ChatCompletion.Usage("/disconnect"));
        Assert.Equal("security", ChatCompletion.Commands.Single(item => item.Insertion == "/openrights").Category);
        Assert.Equal(["account", "reason"], ChatCompletion.Commands.Single(item => item.Insertion == "/disconnect").ParameterChips);
    }
}
