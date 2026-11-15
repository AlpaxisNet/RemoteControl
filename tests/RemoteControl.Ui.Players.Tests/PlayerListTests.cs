using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Protocol.Wire;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Players.Tests;

public sealed class PlayerListTests
{
    [AvaloniaFact]
    public void Opens_with_the_cached_players_in_groups_and_asks_for_the_lists_once()
    {
        using var fixture = new Fixture();
        fixture.Fake.PlayerList.Add(Fixture.Player(1, "Staff", "Staffer", level: ""));
        fixture.Fake.PlayerList.Add(Fixture.Player(2, "Graal123", "Bob"));
        PlayerListWindow window = fixture.OpenList();

        Assert.Equal("Player List - Test Server", window.Title);
        Assert.Equal(new[] { "Admins", "Staffer", "Players", "Bob" }, window.Items.Select(row => row.Nick));
        Assert.Single(fixture.Fake.CallsTo("RequestPmServerListAsync"));
        Assert.Single(fixture.Fake.CallsTo("RequestPmGuildListAsync"));
        Assert.Same(window, fixture.Context.OpenTool(ToolIds.PlayerList));
        Assert.Equal(2, fixture.Fake.CallsTo("RequestPmServerListAsync").Count);
    }

    [AvaloniaFact]
    public void Three_hundred_players_joining_updating_and_leaving_keep_the_selection_and_send_no_requests()
    {
        using var fixture = new Fixture();
        PlayerListWindow window = fixture.OpenList();
        fixture.Fake.RaisePlayerJoined(Fixture.Player(1000, "Chosen", "Chosen One"));
        PlayerRow chosen = window.RowOf(1000)!;
        window.Select(chosen);
        fixture.Fake.ClearCalls();

        for (int id = 1; id <= 300; id++) fixture.Fake.RaisePlayerJoined(Fixture.Player(id, "acc" + id, "nick" + id, level: id % 3 == 0 ? "" : "level" + id));
        for (int id = 1; id <= 300; id++) fixture.Fake.RaisePlayerUpdated(Fixture.Player(id, "acc" + id, "renamed" + id, level: id % 2 == 0 ? "" : "other" + id, status: id % 5));
        fixture.Fake.RaisePlayerUpdated(Fixture.Player(1000, "Chosen", "Chosen Again", level: ""));
        for (int id = 1; id <= 300; id += 2) fixture.Fake.RaisePlayerLeft(Fixture.Player(id, "acc" + id));
        TestUi.Settle();

        Assert.Same(chosen, window.PlayersList.SelectedItem);
        Assert.Equal("Chosen Again", chosen.Nick);
        Assert.True(window.Items.IndexOf(chosen) < window.Items.IndexOf(window.PlayersGroup));
        Assert.Empty(fixture.Fake.Calls);
        Assert.Equal(150 + 1, window.Items.Count(row => !row.IsGroup));
        Assert.Contains(window.Items, row => row.Id == 2 && row.Nick == "renamed2");
        Assert.DoesNotContain(window.Items, row => row.Id == 1);
        int admins = window.Items.IndexOf(window.AdminsGroup), players = window.Items.IndexOf(window.PlayersGroup);
        Assert.All(window.Items.Skip(admins + 1).Take(players - admins - 1), row => Assert.True(string.IsNullOrWhiteSpace(row.Level)));
        Assert.All(window.Items.Skip(players + 1), row => Assert.False(string.IsNullOrWhiteSpace(row.Level)));
        Shots.Save(window, "player-list-busy");
    }

    [AvaloniaFact]
    public void Sorting_by_a_column_and_back_to_the_server_order()
    {
        using var fixture = new Fixture();
        PlayerListWindow window = fixture.OpenList();
        fixture.Fake.RaisePlayerJoined(Fixture.Player(5, "c", "Zed"));
        fixture.Fake.RaisePlayerJoined(Fixture.Player(3, "a", "Amy"));
        fixture.Fake.RaisePlayerJoined(Fixture.Player(4, "b", "Max"));

        Fixture.Click(window.PlayersHeader.Buttons[1]);
        Assert.Equal(new[] { "Amy", "Max", "Zed" }, window.Items.Where(row => !row.IsGroup).Select(row => row.Nick));
        Fixture.Click(window.PlayersHeader.Buttons[1]);
        Assert.Equal(new[] { "Zed", "Max", "Amy" }, window.Items.Where(row => !row.IsGroup).Select(row => row.Nick));
        Fixture.Click(window.PlayersHeader.Buttons[4]);
        Assert.Equal(new[] { 3, 4, 5 }, window.Items.Where(row => !row.IsGroup).Select(row => row.Id));
        fixture.Fake.RaisePlayerJoined(Fixture.Player(1, "d", "New"));
        Assert.Equal(new[] { 1, 3, 4, 5 }, window.Items.Where(row => !row.IsGroup).Select(row => row.Id));
        Fixture.Click(window.PlayersHeader.Buttons[0]);
        Assert.Equal(new[] { "Zed", "Amy", "Max", "New" }, window.Items.Where(row => !row.IsGroup).Select(row => row.Nick));
        Assert.Equal((0, false), window.PlayerSort);
    }

    [AvaloniaFact]
    public void A_column_can_be_resized()
    {
        using var fixture = new Fixture();
        PlayerListWindow window = fixture.OpenList();
        window.PlayerColumns.SetWidth(1, 240);
        Assert.Equal(240, window.PlayerColumns.Width(1));
        window.PlayerColumns.SetWidth(1, 2);
        Assert.Equal(16, window.PlayerColumns.Width(1));
    }

    [AvaloniaFact]
    public void Clicking_a_group_collapses_and_expands_it()
    {
        using var fixture = new Fixture();
        fixture.Fake.PlayerList.Add(Fixture.Player(2, "Graal123", "Bob"));
        PlayerListWindow window = fixture.OpenList();
        var group = window.PlayersGroup;
        window.PlayersList.SelectedItem = group;
        window.PlayersList.ContainerFromItem(group)!.Focus();
        TestUi.PressKey(window, Key.Enter);
        Assert.False(group.IsExpanded);
        Assert.DoesNotContain(window.Items, row => row.Id == 2);
        fixture.Fake.RaisePlayerJoined(Fixture.Player(3, "x", "Hidden"));
        Assert.DoesNotContain(window.Items, row => row.Id == 3);
        Assert.Same(group, window.PlayersList.SelectedItem);
        window.PlayersList.ContainerFromItem(group)!.Focus();
        TestUi.PressKey(window, Key.Enter);
        Assert.True(group.IsExpanded);
        Assert.Contains(window.Items, row => row.Id == 3);
        window.PlayersList.SelectedItem = window.RowOf(3);
        window.PlayersList.ContainerFromItem(window.RowOf(3)!)!.Focus();
        TestUi.PressKey(window, Key.Enter);
        Assert.Equal(3, fixture.Latest<PrivateMessageWindow>().PlayerId);
    }

    [AvaloniaFact]
    public void Typing_a_name_selects_the_player()
    {
        using var fixture = new Fixture();
        fixture.Fake.PlayerList.Add(Fixture.Player(2, "Graal123", "Bob"));
        fixture.Fake.PlayerList.Add(Fixture.Player(3, "Graal456", "Alice"));
        PlayerListWindow window = fixture.OpenList();
        Fixture.FocusList(window.PlayersList);
        TestUi.TypeText(window, "ali");
        Assert.Equal(3, (window.PlayersList.SelectedItem as PlayerRow)?.Id);
    }

    [AvaloniaFact]
    public void Status_icons_follow_the_status_list_and_map_the_raw_byte()
    {
        using var fixture = new Fixture();
        PlayerListWindow window = fixture.OpenList();
        fixture.Fake.RaiseServerData("statuslist", "Online,Away,DND,Eating");
        var assets = fixture.Test.Services.Assets;
        Assert.Same(assets.StatusIcon("Away"), window.StatusIcon(1));
        Assert.Same(assets.StatusIcon("Away"), window.StatusIcon(33));
        Assert.Same(assets.Get("plisticononline.png"), window.StatusIcon(99));
        Assert.Same(assets.Get("plisticononline.png"), window.StatusIcon(-1));
        fixture.Fake.RaisePlayerJoined(Fixture.Player(7, "a", "A", status: 2));
        Assert.Same(assets.StatusIcon("DND"), window.RowOf(7)!.Icon);
    }

    [AvaloniaFact]
    public void The_status_combo_sends_the_status_and_attach_away_sets_away_while_hidden()
    {
        using var fixture = new Fixture();
        PlayerListWindow window = fixture.OpenList();
        fixture.Fake.ClearCalls();
        TestUi.Find<ComboBox>(window, "StatusCombo").SelectedIndex = 2;
        RecordedCall call = fixture.Fake.LastCall("SendRawPacketAsync")!;
        Assert.Equal((int)ClientPacketId.PlayerProps, call.Argument<int>(0));
        Assert.Equal(new byte[] { (byte)'U', 34 }, call.Argument<byte[]>(1));

        TestUi.Find<ComboBox>(window, "StatusCombo").SelectedIndex = 0;
        fixture.Fake.ClearCalls();
        Fixture.Click(TestUi.Find<Button>(window, "CloseButton"));
        Assert.False(window.IsVisible);
        Assert.Equal(1, window.StatusIndex);
        Assert.Equal(new byte[] { (byte)'U', 33 }, fixture.Fake.LastCall("SendRawPacketAsync")!.Argument<byte[]>(1));
        fixture.Context.OpenTool(ToolIds.PlayerList);
        Assert.True(window.IsVisible);
        Assert.Equal(0, window.StatusIndex);
        Assert.Equal(new byte[] { (byte)'U', 32 }, fixture.Fake.LastCall("SendRawPacketAsync")!.Argument<byte[]>(1));
    }

    [AvaloniaFact]
    public void Guilds_servers_and_channels_tabs()
    {
        using var fixture = new Fixture();
        PlayerListWindow window = fixture.OpenList();
        fixture.Fake.RaisePmGuildsUpdated(new[] { "Zeta", "Alpha" });
        fixture.Fake.RaiseChannelsChanged(new[] { new RcChannel(900, "General", "12", "3") });
        fixture.Fake.RaisePmServersUpdated(new[] { "Other Server", "Third" });
        Assert.Equal(new[] { "Alpha", "Zeta" }, window.Guilds.Select(row => row.Text1));
        Assert.Equal(("General", "12", "3"), (window.Channels[0].Text1, window.Channels[0].Text2, window.Channels[0].Text3));
        Assert.Equal(new[] { "Other Server", "Third" }, window.Servers.Select(row => row.Text1));

        fixture.Fake.ClearCalls();
        window.ActivateServer("Other Server");
        Assert.Equal("Other Server", fixture.Fake.LastCall("RequestPmServerPlayersAsync")!.Argument<string>(0));
        fixture.Fake.RaisePmServerPlayers("Other Server", "55\tremote\tRemo\tlevel.nw\n56\tsecond\t\tx.nw");
        Assert.Equal(new[] { "Other Server", "Remo", "second", "Third" }, window.Servers.Select(row => row.Text1));

        SimpleRow remote = window.Servers[1];
        var menu = window.ServerMenuFor(remote);
        Assert.Equal(new[] { "Private Message", "History", "Ban History", "Staff Activity", "Copy Account" }, menu.OfType<MenuItem>().Select(item => (string)item.Header!));
        Fixture.Click(Fixture.Item(menu, "Ban History"));
        Assert.Equal("remote", fixture.Fake.LastCall("RequestBanHistoryAsync")!.Argument<string>(0));
        Fixture.Click(Fixture.Item(menu, "Staff Activity"));
        Assert.Equal("remote", fixture.Fake.LastCall("RequestStaffActivityAsync")!.Argument<string>(0));
        Fixture.Click(Fixture.Item(menu, "Private Message"));
        var pm = fixture.Latest<PrivateMessageWindow>();
        Assert.Equal("PM: Remo (remote) (on Other Server)", pm.Title);

        window.ActivateServer("Other Server");
        Assert.Equal("Other Server", fixture.Fake.LastCall("UnmapPmServerAsync")!.Argument<string>(0));
        Assert.Equal(new[] { "Other Server", "Third" }, window.Servers.Select(row => row.Text1));
        window.ActivateServer("Other Server");
        Assert.Equal(4, window.Servers.Count);

        fixture.Fake.ClearCalls();
        Fixture.Click(Fixture.Item(window.ServerMenuFor(window.Servers[0]), "Close server"));
        Assert.True(fixture.Fake.WasCalled("UnmapPmServerAsync"));
        Assert.Equal(2, window.Servers.Count);
        Assert.False(fixture.Players.ServerPlayers.ContainsKey("Other Server"));
        foreach (string tab in new[] { "GuildsTab", "ServersTab", "ChannelsTab" })
        {
            TestUi.Find<TabControl>(window, "Tabs").SelectedItem = TestUi.Find<TabItem>(window, tab);
            Shots.Save(window, "player-list-" + tab);
        }
    }

    [AvaloniaFact]
    public void A_reconnect_shows_the_new_connections_players()
    {
        using var fixture = new Fixture();
        fixture.Fake.PlayerList.Add(Fixture.Player(2, "old", "Old"));
        PlayerListWindow window = fixture.OpenList();
        var second = new FakeRcSession("tester");
        second.PlayerList.Add(Fixture.Player(9, "new", "New"));
        fixture.Context.Replace(second, fixture.Test.Login);
        Assert.Equal(new[] { 9 }, window.Items.Where(row => !row.IsGroup).Select(row => row.Id));
        fixture.Fake.RaisePlayerJoined(Fixture.Player(3, "stale", "Stale"));
        Assert.Null(window.RowOf(3));
        second.RaisePlayerJoined(Fixture.Player(4, "fresh", "Fresh"));
        Assert.NotNull(window.RowOf(4));
    }

    [AvaloniaFact]
    public void Mass_pm_sends_to_the_selected_players_or_to_everyone_listed()
    {
        using var fixture = new Fixture();
        for (int id = 1; id <= 4; id++) fixture.Fake.PlayerList.Add(Fixture.Player(id, "acc" + id, "nick" + id));
        PlayerListWindow window = fixture.OpenList();
        window.PlayersList.SelectedItems!.Add(window.RowOf(2)!);
        window.PlayersList.SelectedItems!.Add(window.RowOf(4)!);
        Fixture.Click(TestUi.Find<Button>(window, "MassPmButton"));
        TextInputWindow prompt = Fixture.Prompt();
        Assert.Equal("Mass PM to 2 players", prompt.Title);
        prompt.Text = "hello all";
        prompt.Accept();
        TestUi.WaitUntil(() => fixture.Fake.WasCalled("SendMassPrivateMessageAsync"));
        RecordedCall call = fixture.Fake.LastCall("SendMassPrivateMessageAsync")!;
        Assert.Equal(new[] { 2, 4 }, call.Argument<IReadOnlyList<int>>(0));
        Assert.Equal("hello all", call.Argument<string>(1));

        window.PlayersList.SelectedItems!.Clear();
        Fixture.Click(TestUi.Find<Button>(window, "MassPmButton"));
        prompt = Fixture.Prompt();
        Assert.Equal("Mass PM to all", prompt.Title);
        Shots.Save(prompt, "mass-pm-prompt");
        prompt.Text = "everyone";
        prompt.Accept();
        TestUi.WaitUntil(() => fixture.Fake.CallsTo("SendMassPrivateMessageAsync").Count == 2);
        Assert.Equal(new[] { 1, 2, 3, 4 }, fixture.Fake.LastCall("SendMassPrivateMessageAsync")!.Argument<IReadOnlyList<int>>(0));
    }

    [AvaloniaFact]
    public void Admin_message_to_all_sends_the_line()
    {
        using var fixture = new Fixture();
        PlayerListWindow window = fixture.OpenList();
        Fixture.Click(TestUi.Find<Button>(window, "AdminMessageButton"));
        TextInputWindow prompt = Fixture.Prompt();
        Assert.Equal("Admin Message to all", prompt.Title);
        Assert.False(prompt.Multiline);
        prompt.Text = "Server restart";
        TestUi.PressKey(prompt, Key.Enter);
        TestUi.WaitUntil(() => fixture.Fake.WasCalled("SendAdminMessageToAllAsync"));
        Assert.Equal("Server restart", fixture.Fake.LastCall("SendAdminMessageToAllAsync")!.Argument<string>(0));
    }

    [AvaloniaFact]
    public void Closing_the_session_closes_the_list_and_its_prompts()
    {
        var fixture = new Fixture();
        PlayerListWindow window = fixture.OpenList();
        Fixture.Click(TestUi.Find<Button>(window, "AdminMessageButton"));
        TextInputWindow prompt = Fixture.Prompt();
        fixture.Context.End();
        TestUi.Settle();
        Assert.True(window.IsClosed);
        Assert.True(prompt.IsClosed);
        Assert.False(fixture.Fake.WasCalled("SendAdminMessageToAllAsync"));
        Assert.Null(fixture.Context.OpenTool(ToolIds.PlayerList));
        fixture.Dispose();
    }

    [AvaloniaFact]
    public void Pictures_in_two_themes()
    {
        foreach (string theme in new[] { "catppuccin", "light" })
        {
            using var fixture = new Fixture(theme);
            fixture.Fake.PlayerList.Add(Fixture.Player(1, "Staff", "Staffer", level: ""));
            for (int id = 2; id < 12; id++) fixture.Fake.PlayerList.Add(Fixture.Player(id, "Graal" + id, "Player " + id, "level" + id + ".nw", status: id % 4));
            fixture.Fake.RaiseServerData("statuslist", "Online,Away,DND,Eating");
            PlayerListWindow window = fixture.OpenList();
            fixture.Fake.RaisePrivateMessage(3, "Graal3", "Player 3", "hi");
            window.Select(window.RowOf(4)!);
            Shots.Save(window, "player-list-" + theme);
        }
    }
}
