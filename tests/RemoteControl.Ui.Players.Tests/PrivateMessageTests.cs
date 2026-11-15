using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Players.Tests;

public sealed class PrivateMessageTests
{
    private sealed class Chat : ISessionChat
    {
        public List<string> Lines { get; } = new();
        public IReadOnlyList<string> Tabs => new[] { "RC" };
        public void AppendLine(string text) => Lines.Add(text);
        public void AppendToTab(string tab, string text) => Lines.Add(tab + ":" + text);
        public void RemoveTab(string tab) { }
        public bool Send(string text) => true;
        public void Activate() { }
        public Avalonia.Controls.Window? Window => null;
    }

    [AvaloniaFact]
    public async Task An_incoming_pm_without_any_window_is_unread_asks_for_attention_and_is_written_to_the_history()
    {
        using var fixture = new Fixture();
        var chat = new Chat();
        using IDisposable provided = fixture.Context.Services.Provide<ISessionChat>(chat);
        IPrivateMessages messages = fixture.Context.Services.Get<IPrivateMessages>()!;
        int changes = 0;
        messages.UnreadChanged += (_, _) => changes++;

        fixture.Fake.RaisePrivateMessage(12, "Graal12", "Bob", "Private message:\nhello there");

        Assert.True(messages.HasUnread);
        Assert.Equal(1, changes);
        Assert.Contains("Attention:PrivateMessage", fixture.Test.Shell.Requests);
        // The alert line in the RC chat is the main window's; this group must not post a second one.
        Assert.Empty(chat.Lines);
        Assert.Equal(12, fixture.Players.LatestPmPlayerId);
        Assert.Empty(fixture.Context.Windows.All);
        await fixture.Players.History.FlushAsync();
        string file = fixture.Test.Services.Paths.PmHistoryFile("Graal12");
        string text = await File.ReadAllTextAsync(file);
        Assert.Matches(@"^Bob \(\w{3} \w{3} \d\d \d\d:\d\d:\d\d \d{4}\):\nhello there\n\n$", text);

        messages.ClearUnread();
        Assert.False(messages.HasUnread);
        Assert.True(fixture.Players.Pending.ContainsKey(12));
    }

    [AvaloniaFact]
    public void Open_latest_opens_the_window_of_the_latest_sender_with_its_messages()
    {
        using var fixture = new Fixture();
        IPrivateMessages messages = fixture.Players;
        Assert.False(messages.OpenLatest());
        fixture.Fake.RaisePrivateMessage(12, "Graal12", "Bob", "first");
        fixture.Fake.RaisePrivateMessage(15, "Graal15", "Ann", "second");
        fixture.Fake.RaisePrivateMessage(15, "Graal15", "Ann", "third");

        Assert.True(messages.OpenLatest());
        PrivateMessageWindow window = fixture.Latest<PrivateMessageWindow>();
        Assert.Equal(15, window.PlayerId);
        Assert.Equal("PM: Ann (Graal15)", window.Title);
        Assert.Contains("Ann: second", window.ConversationView.Text);
        Assert.Contains("Ann: third", window.ConversationView.Text);
        Assert.False(fixture.Players.Pending.ContainsKey(15));
        Assert.False(messages.HasUnread);
        Assert.Equal(12, fixture.Players.LatestPmPlayerId);
        Assert.True(messages.OpenLatest());
        Assert.Equal(12, fixture.Latest<PrivateMessageWindow>().PlayerId);
        Assert.False(messages.OpenLatest());
    }

    [AvaloniaFact]
    public void A_pm_for_an_open_window_goes_into_it()
    {
        using var fixture = new Fixture();
        fixture.Fake.PlayerList.Add(Fixture.Player(12, "Graal12", "Bob"));
        var window = (PrivateMessageWindow)fixture.Context.OpenTool(ToolIds.PrivateMessage, 12)!;
        fixture.Test.Shell.Requests.Clear();
        fixture.Fake.RaisePrivateMessage(12, "Graal12", "Bob", "are you there?");
        Assert.Contains("Bob: are you there?", window.ConversationView.Text);
        Assert.Empty(fixture.Players.Pending);
        Assert.False(fixture.Players.HasUnread);
        Assert.DoesNotContain("Attention:PrivateMessage", fixture.Test.Shell.Requests);
        Assert.Same(window, fixture.Context.OpenTool(ToolIds.PrivateMessage, 12));
        Assert.Null(fixture.Context.OpenTool(ToolIds.PrivateMessage, 999));
    }

    [AvaloniaFact]
    public async Task Sending_writes_the_history_and_shows_the_line()
    {
        using var fixture = new Fixture();
        fixture.Fake.PlayerList.Add(Fixture.Player(12, "Graal12", "Bob"));
        var window = (PrivateMessageWindow)fixture.Context.OpenTool(ToolIds.PrivateMessage, 12)!;
        window.ReplyText = "hi Bob";
        TestUi.Find<Avalonia.Controls.TextBox>(window, "Reply").Focus();
        TestUi.PressKey(window, Key.Enter);
        await TestUi.WaitUntilAsync(() => fixture.Fake.WasCalled("SendPrivateMessageAsync"));
        RecordedCall call = fixture.Fake.LastCall("SendPrivateMessageAsync")!;
        Assert.Equal((12, "hi Bob"), (call.Argument<int>(0), call.Argument<string>(1)));
        Assert.Equal(string.Empty, window.ReplyText);
        Assert.Contains("tester: hi Bob", window.ConversationView.Text);
        await fixture.Players.History.FlushAsync();
        Assert.StartsWith("tester (", await File.ReadAllTextAsync(fixture.Test.Services.Paths.PmHistoryFile("Graal12")));

        window.ReplyText = "a";
        TestUi.Find<Avalonia.Controls.TextBox>(window, "Reply").CaretIndex = 1;
        TestUi.PressKey(window, Key.Enter, Avalonia.Input.RawInputModifiers.Control);
        Assert.Equal("a\n", window.ReplyText);
        Shots.Save(window, "pm-window");
    }

    [AvaloniaFact]
    public async Task Sending_after_a_disconnect_shows_a_message()
    {
        using var fixture = new Fixture();
        fixture.Fake.PlayerList.Add(Fixture.Player(12, "Graal12", "Bob"));
        var window = (PrivateMessageWindow)fixture.Context.OpenTool(ToolIds.PrivateMessage, 12)!;
        fixture.Fake.RaiseDisconnected();
        window.ReplyText = "hello?";
        Task<bool> send = window.SendAsync();
        TestUi.FindDialog().Accept();
        Assert.False(await send);
        Assert.Equal("hello?", window.ReplyText);
    }

    [AvaloniaFact]
    public void Html_in_a_pm_is_shown_as_safe_text()
    {
        using var fixture = new Fixture();
        fixture.Fake.PlayerList.Add(Fixture.Player(12, "Graal12", "Bob"));
        var window = (PrivateMessageWindow)fixture.Context.OpenTool(ToolIds.PrivateMessage, 12)!;
        fixture.Fake.RaisePrivateMessage(12, "Graal12", "Bob", "<b>bold</b> &amp; <img src=\"http://evil.invalid/x.png\"><br>next");
        Assert.Contains("Bob: bold & [image: x.png]", window.ConversationView.Text);
        Assert.Contains("\nnext", window.ConversationView.Text);
        Assert.DoesNotContain("<b>", window.ConversationView.Text);

        fixture.Test.Environment.SetOptions(o => o with { NoHtmlImages = true });
        fixture.Fake.RaisePrivateMessage(12, "Graal12", "Bob", "pic <img src='a.png'> gone");
        Assert.Contains("Bob: pic  gone", window.ConversationView.Text);
        fixture.Test.Environment.SetOptions(o => o with { NoHtmlInPms = true });
        fixture.Fake.RaisePrivateMessage(12, "Graal12", "Bob", "<i>raw</i>");
        Assert.Contains("Bob: <i>raw</i>", window.ConversationView.Text);
    }

    [AvaloniaFact]
    public void Mass_pms_are_dropped_when_ignored()
    {
        using var fixture = new Fixture(options: o => o with { NoMassMessages = true });
        fixture.Fake.RaisePrivateMessage(12, "Graal12", "Bob", "buy stuff", PrivateMessageKind.Mass);
        Assert.Empty(fixture.Players.Pending);
        Assert.False(fixture.Players.HasUnread);
    }

    [AvaloniaFact]
    public async Task An_unread_pm_blinks_in_the_player_list_and_an_offline_sender_stays_listed()
    {
        using var fixture = new Fixture();
        PlayerListWindow list = fixture.OpenList();
        fixture.Fake.RaisePlayerJoined(Fixture.Player(12, "Graal12", "Bob"));
        fixture.Fake.RaisePrivateMessage(12, "Graal12", "Bob", "hey", PrivateMessageKind.Guild);
        var assets = fixture.Test.Services.Assets;
        Assert.Same(assets.Get("pmicon_guild.png"), list.RowOf(12)!.Icon);
        await TestUi.WaitUntilAsync(() => !list.BlinkShowsPmIcon, TimeSpan.FromSeconds(2), "the blink");
        Assert.Same(assets.Get("plisticononline.png"), list.RowOf(12)!.Icon);

        fixture.Fake.RaisePlayerLeft(Fixture.Player(12, "Graal12"));
        PlayerRow offline = list.RowOf(12)!;
        Assert.True(offline.IsOffline);
        Assert.Equal("Offline", offline.Level);
        Assert.Contains(offline, list.Items);
        Assert.Equal(new[] { 12 }, list.MassPmTargets().Ids);

        list.MenuFor(offline).OfType<Avalonia.Controls.MenuItem>().First().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
        Assert.Equal("PM: Bob (Graal12)", fixture.Latest<PrivateMessageWindow>().Title);
        Assert.Null(list.RowOf(12));
    }

    [AvaloniaFact]
    public async Task The_history_window_shows_the_file()
    {
        using var fixture = new Fixture();
        await fixture.Players.History.AppendAsync("Graal12", "Bob", "one");
        await fixture.Players.History.AppendAsync("Graal12", "tester", "two");
        TextViewerWindow window = fixture.Players.ShowHistory("Graal12", "Bob")!;
        await TestUi.WaitUntilAsync(() => window.Text.Contains("two", StringComparison.Ordinal));
        Assert.Equal(540, window.Width);
        string[] lines = window.Text.Split('\n');
        Assert.True(PmHistory.IsHeader(lines[0]));
        Assert.Equal("one", lines[1]);
        window.OpenFind();
        Assert.True(window.IsFindOpen);
        Shots.Save(window, "pm-history");
    }

    [AvaloniaFact]
    public void Start_twice_offers_one_service_and_a_new_session_after_the_end_is_ignored()
    {
        var fixture = new Fixture();
        PlayersFeature.Start(fixture.Context);
        Assert.Same(fixture.Players, PlayersSession.For(fixture.Context));
        Assert.Same(fixture.Players, fixture.Context.Services.Get<IPrivateMessages>());
        fixture.Context.End();
        Assert.Null(fixture.Context.Services.Get<IPrivateMessages>());
        PlayersFeature.Start(fixture.Context);
        Assert.Null(PlayersSession.For(fixture.Context));
        fixture.Dispose();
    }

    [AvaloniaFact]
    public void Pictures_in_two_themes()
    {
        foreach (string theme in new[] { "nord", "light" })
        {
            using var fixture = new Fixture(theme);
            fixture.Fake.PlayerList.Add(Fixture.Player(12, "Graal12", "Bob"));
            fixture.Fake.RaisePrivateMessage(12, "Graal12", "Bob", "Hello! Did you see www.example.com ?");
            fixture.Fake.RaisePrivateMessage(12, "Graal12", "Bob", "Kappa second line\nand a third");
            var window = (PrivateMessageWindow)fixture.Context.OpenTool(ToolIds.PrivateMessage, 12)!;
            window.ReplyText = "Sure, on my way";
            Shots.Save(window, "pm-" + theme);
        }
    }
}
