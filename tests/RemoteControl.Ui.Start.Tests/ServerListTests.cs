using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Core;
using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Start.Tests;

public sealed class ServerListTests
{
    private static async Task<ServerListWindow> LoggedInAsync(StartFixture fixture)
    {
        fixture.SaveAccount("Staffer", "staff-secret", fixture.TestProfile.Association);
        ConnectResult result = await fixture.Controller.SubmitLoginAsync("Staffer", "Nick");
        Assert.True(result.Success, result.Error);
        return fixture.Controller.ServerList!;
    }

    [AvaloniaFact]
    public async Task The_list_shows_icons_names_players_sorted_by_players_and_the_details()
    {
        using var fixture = new StartFixture("nord");
        ServerListWindow list = await LoggedInAsync(fixture);

        Assert.Equal(["Beta", "Alpha", "Gamma"], list.Rows.Select(row => row.Name));
        Assert.Equal(["30", "12", "2"], list.Rows.Select(row => row.PlayersText));
        Assert.Equal(2, ServerRow.IconIndex(list.Rows[0].Server.Kind));
        Assert.Equal(0, ServerRow.IconIndex(list.Rows[1].Server.Kind));
        Assert.Equal(1, ServerRow.IconIndex(list.Rows[2].Server.Kind));
        Assert.Equal(3, ServerRow.IconIndex(ServerKind.Silver));
        Assert.All(list.Rows, row => Assert.NotNull(row.Icon));

        ListBox servers = TestUi.Find<ListBox>(list, "ServerListField");
        servers.SelectedIndex = 1;
        TestUi.Settle();
        Assert.Equal("English", TestUi.Find<TextBox>(list, "LanguageField").Text);
        Assert.Equal("1.0", TestUi.Find<TextBox>(list, "VersionField").Text);
        Assert.Equal("The Alpha server.", TestUi.Find<TextBox>(list, "DescriptionField").Text);
        Assert.Equal("https://alpha.example/", TestUi.Find<TextBox>(list, "HomepageField").Text);
        Shots.Save(list, "server-list-nord");

        ExternalPrograms.ClearSuppressed();
        TestUi.Click(TestUi.Find<Button>(list, "HomepageButton"));
        Assert.Contains("url https://alpha.example/", ExternalPrograms.Suppressed);

        TestUi.Click(TestUi.Find<Button>(list, "SortByNameButton"));
        Assert.Equal(["Alpha", "Beta", "Gamma"], list.Rows.Select(row => row.Name));
        Assert.Equal((ServerSort.Name, false), list.Sort);
        Assert.Equal("Alpha", ((ServerRow)servers.SelectedItem!).Name);
        list.SortBy(ServerSort.Name);
        Assert.Equal(["Gamma", "Beta", "Alpha"], list.Rows.Select(row => row.Name));
        list.SortBy(ServerSort.Players);
        Assert.Equal(["Beta", "Alpha", "Gamma"], list.Rows.Select(row => row.Name));
    }

    [AvaloniaFact]
    public async Task Refresh_updates_the_rows_in_place_and_keeps_the_selection()
    {
        using var fixture = new StartFixture();
        ServerListWindow list = await LoggedInAsync(fixture);
        ServerRow alpha = list.Rows.Single(row => row.Name == "Alpha");
        TestUi.Find<ListBox>(list, "ServerListField").SelectedItem = alpha;
        fixture.Server.Servers[0] = fixture.Server.Servers[0] with { Players = 99 };
        fixture.Server.Servers.RemoveAt(2);

        ConnectResult result = await list.RefreshAsync();
        Assert.True(result.Success);
        Assert.Equal(["Alpha", "Beta"], list.Rows.Select(row => row.Name));
        Assert.Same(alpha, list.Rows[0]);
        Assert.Equal("99", alpha.PlayersText);
        Assert.Same(alpha, TestUi.Find<ListBox>(list, "ServerListField").SelectedItem);
    }

    [AvaloniaFact]
    public async Task Connect_hands_the_choice_to_the_program_and_closes_the_list()
    {
        using var fixture = new StartFixture();
        ServerListWindow list = await LoggedInAsync(fixture);
        list.Select("Gamma");
        TestUi.Click(TestUi.Find<Button>(list, "ConnectButton"));
        await TestUi.WaitUntilAsync(() => list.IsClosed, what: "the list to close");

        ServerChoice choice = fixture.Choices.Single();
        Assert.Equal("U Gamma", choice.Login.Server.Name);
        Assert.Equal(14902, choice.Login.Server.Port);
        Assert.Equal("Staffer", choice.Login.Account);
        Assert.Equal("staff-secret", choice.Login.Password);
        Assert.Equal("Nick", choice.Login.Nickname);
        Assert.Equal("Test", choice.Login.ListServer);
        Assert.Null(choice.SwitchFor);
        Assert.True(list.ClosedAfterConnect);
        Assert.False(fixture.Controller.IsLoginVisible);
    }

    [AvaloniaFact]
    public async Task A_failed_connect_is_shown_and_the_list_stays()
    {
        using var fixture = new StartFixture();
        fixture.Connect = _ => Task.FromResult(ConnectResult.Failed("The server is full."));
        ServerListWindow list = await LoggedInAsync(fixture);
        list.Select("Alpha");
        ConnectResult result = await list.ConnectSelectedAsync(additional: false);

        Assert.False(result.Success);
        Assert.False(list.IsClosed);
        Assert.Equal("The server is full.", list.Status);
        Assert.True(list.StatusIsError);
        Assert.True(TestUi.Find<Button>(list, "ConnectButton").IsEnabled);

        fixture.Connect = _ => throw new InvalidOperationException("boom");
        ConnectResult thrown = await list.ConnectSelectedAsync(additional: false);
        Assert.Equal("boom", thrown.Error);
        Assert.False(list.IsClosed);
    }

    [AvaloniaFact]
    public async Task Switch_server_logs_in_with_the_sessions_account_and_passes_the_session()
    {
        using var fixture = new StartFixture();
        SessionContext session = fixture.CreateSession();
        ConnectResult login = await fixture.Controller.ShowServerListAsync(session);
        Assert.True(login.Success, login.Error);
        Assert.Equal("Staff", fixture.Server.Logins.Single().Account);
        Assert.Equal("staff-pw", fixture.Server.Logins.Single().Password);

        ServerListWindow list = fixture.Controller.ServerList!;
        list.Select("Beta");
        ConnectResult connected = await list.ConnectSelectedAsync(additional: false);
        Assert.True(connected.Success);
        Assert.Same(session, fixture.Choices.Single().SwitchFor);
        Assert.Equal("StaffNick", fixture.Choices.Single().Login.Nickname);
    }

    [AvaloniaFact]
    public async Task An_additional_connection_or_an_ended_session_is_a_new_session()
    {
        using var fixture = new StartFixture();
        SessionContext session = fixture.CreateSession();
        await fixture.Controller.ShowServerListAsync(session);
        ServerListWindow list = fixture.Controller.ServerList!;
        list.Select("Beta");
        fixture.Connect = _ => Task.FromResult(ConnectResult.Failed("again"));
        await list.ConnectSelectedAsync(additional: true);
        Assert.Null(fixture.Choices[0].SwitchFor);

        session.End();
        await list.ConnectSelectedAsync(additional: false);
        Assert.Null(fixture.Choices[1].SwitchFor);
    }

    [AvaloniaFact]
    public async Task Switch_server_for_a_session_whose_list_server_is_gone_asks_first()
    {
        using var fixture = new StartFixture();
        SessionContext session = fixture.CreateSession(listServer: "Removed");
        Task<ConnectResult> shown = fixture.Controller.ShowServerListAsync(session);
        RcDialog dialog = TestUi.FindDialog();
        Assert.Contains("Removed", dialog.Message);
        Assert.Empty(fixture.Server.Logins);
        TestUi.Find<ListBox>(dialog, Dialogs.ListName).SelectedIndex = 1;
        dialog.Accept();
        ConnectResult result = await shown;
        Assert.True(result.Success, result.Error);
        Assert.Equal("Staff", fixture.Server.Logins.Single().Account);
    }

    [AvaloniaFact]
    public async Task Show_server_list_reuses_the_last_login_or_shows_the_login_window()
    {
        using var fixture = new StartFixture();
        fixture.Controller.ShowServerList(null);
        TestUi.Settle();
        Assert.True(fixture.Controller.IsLoginVisible);
        Assert.Null(fixture.Controller.ServerList);

        ServerListWindow first = await LoggedInAsync(fixture);
        first.Close();
        TestUi.Settle();
        fixture.Controller.ShowServerList(null);
        ServerListWindow second = await fixture.WaitForServersAsync();
        Assert.NotSame(first, second);
        Assert.Equal(2, fixture.Server.Logins.Count);
        Assert.All(fixture.Server.Logins, login => Assert.Equal("Staffer", login.Account));
    }

    [AvaloniaFact]
    public async Task Closing_the_list_with_a_session_open_does_not_bring_the_login_back()
    {
        using var fixture = new StartFixture();
        ServerListWindow list = await LoggedInAsync(fixture);
        fixture.CreateSession();
        list.Close();
        TestUi.Settle();
        Assert.False(fixture.Controller.IsLoginVisible);
    }

    [AvaloniaFact]
    public async Task Direct_connect_from_the_list_uses_the_lists_account()
    {
        using var fixture = new StartFixture();
        ServerListWindow list = await LoggedInAsync(fixture);
        TestUi.Click(TestUi.Find<Button>(list, "DirectButton"));
        RcDialog prompt = TestUi.FindDialog();
        TestUi.Find<TextBox>(prompt, Dialogs.InputName).Text = "not an address";
        prompt.Accept();
        await TestUi.WaitUntilAsync(() => list.StatusIsError, what: "the address error");
        Assert.Empty(fixture.Choices);

        ConnectResult result = await list.DirectConnectAsync("game.test", 14800);
        Assert.True(result.Success);
        ServerChoice choice = fixture.Choices.Single();
        Assert.Equal("game.test", choice.Login.Server.Host);
        Assert.Equal(14800, choice.Login.Server.Port);
        Assert.Equal("Staffer", choice.Login.Account);
    }

    [AvaloniaFact]
    public async Task Connect_using_another_account_of_the_same_list_server()
    {
        using var fixture = new StartFixture();
        ServerListWindow list = await LoggedInAsync(fixture);
        int other = fixture.SaveAccount("Second", "second-pw", fixture.TestProfile.Association);
        fixture.SaveAccount("Elsewhere", "x", ListServerProfile.Default.Association);
        var accounts = fixture.Controller.AccountsForProfile(fixture.TestProfile);
        Assert.Equal(["Staffer", "Second"], accounts.Select(a => a.Account.Name));

        list.Select("Alpha");
        ConnectResult result = await list.ConnectUsingAccountAsync(other);
        Assert.True(result.Success);
        Assert.Equal("Second", fixture.Choices.Single().Login.Account);
        Assert.Equal("second-pw", fixture.Choices.Single().Login.Password);
        Assert.Null(fixture.Choices.Single().SwitchFor);
    }

    [AvaloniaFact]
    public async Task Open_another_server_list_logs_in_with_that_accounts_own_list_server()
    {
        using var fixture = new StartFixture();
        ServerListWindow list = await LoggedInAsync(fixture);
        int other = fixture.SaveAccount("Second", "second-pw", fixture.TestProfile.Association);
        ConnectResult result = await fixture.Controller.OpenServerListForAccountAsync(other, list);
        Assert.True(result.Success, result.Error);
        Assert.Equal(2, fixture.Controller.ServerLists.Count);
        Assert.Equal("Second", fixture.Server.Logins.Last().Account);
        Assert.Same(list, fixture.Controller.ServerList);
    }

    [AvaloniaFact]
    public async Task The_context_menu_has_the_cpp_entries()
    {
        using var fixture = new StartFixture();
        ServerListWindow list = await LoggedInAsync(fixture);
        list.Select("Alpha");
        ContextMenu menu = TestUi.Find<ListBox>(list, "ServerListField").ContextMenu!;
        list.BuildContextMenu(menu);
        var headers = menu.Items.OfType<MenuItem>().Select(item => (string)item.Header!).ToArray();
        Assert.Equal(["Open another server list", "Open additional connection", "Connect", "Connect using account"], headers);
        var another = (MenuItem)menu.Items[0]!;
        Assert.Equal(["Staffer [Test]"], another.Items.OfType<MenuItem>().Select(item => (string)item.Header!));
    }

    [AvaloniaFact]
    public async Task The_gear_opens_the_list_server_settings()
    {
        using var fixture = new StartFixture();
        ServerListWindow list = await LoggedInAsync(fixture);
        TestUi.Click(TestUi.Find<Button>(list, "SettingsButton"));
        Assert.NotNull(fixture.Controller.Settings);
    }
}
