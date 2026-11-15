using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Start.Tests;

public sealed class LoginTests
{
    public static IEnumerable<object[]> AllThemes => ThemeManager.ThemeIds.Select(id => new object[] { id });

    [AvaloniaTheory]
    [MemberData(nameof(AllThemes))]
    public void Login_window_stays_compact_in_every_theme(string theme)
    {
        using var fixture = new StartFixture(theme);
        fixture.SaveAccount("A rather long account name for the badge", "pw", fixture.TestProfile.Association);
        LoginWindow login = fixture.ShowLogin();
        double natural = StartFixture.NaturalWidth(login);
        Assert.True(natural <= 340, $"natural width {natural} in {theme}");
        Assert.True(login.Bounds.Width <= 340);
        if (theme is "dark" or "light" or "catppuccin") Shots.Save(login, "login-" + theme);
    }

    [AvaloniaFact]
    public void Login_window_shows_the_options_and_the_active_account()
    {
        using var fixture = new StartFixture();
        fixture.Services.Options.Update(o => o with { Nickname = "Nick", GraphicalMenu = false });
        fixture.SaveAccount("First", "one", fixture.TestProfile.Association);
        fixture.SaveAccount("Second", string.Empty, ListServerProfile.Default.Association);
        LoginWindow login = fixture.ShowLogin();

        Assert.Equal("Nick", fixture.Controller.Nickname);
        Assert.False(login.GraphicalMenu);
        Assert.Equal("Second", fixture.Controller.SelectedAccount);
        Assert.False(fixture.Controller.HasPassword);
        Assert.True(login.DontSavePassword);
        Assert.Equal("Alpaxis - RemoteControl", login.Title);
        Assert.Equal(2, login.Rows.Count);
        Assert.Equal("[Test]", login.Rows[0].BadgeText);
    }

    [AvaloniaFact]
    public void Choosing_a_saved_account_fills_its_password_and_typing_detaches_it()
    {
        using var fixture = new StartFixture();
        fixture.SaveAccount("First", "secret-1", fixture.TestProfile.Association);
        fixture.SaveAccount("Second", string.Empty, string.Empty);
        LoginWindow login = fixture.ShowLogin();

        login.AccountList.SelectedIndex = 0;
        TestUi.Settle();
        Assert.Equal("First", login.AccountText);
        Assert.Equal("secret-1", login.PasswordText);
        Assert.False(login.DontSavePassword);
        Assert.Equal(0, login.SelectedIndex);
        Assert.Equal("Test - RemoteControl", login.Title);

        login.AccountList.SelectedIndex = 1;
        TestUi.Settle();
        Assert.Equal(string.Empty, login.PasswordText);
        Assert.True(login.DontSavePassword);
        Assert.Equal("RemoteControl", login.Title);

        TestUi.TypeInto(TestUi.Find<TextBox>(login, "AccountField"), "x");
        Assert.Equal("Secondx", login.AccountText);
        Assert.Equal(-1, login.SelectedIndex);
        Assert.Equal("RemoteControl", login.Title);
        Assert.Equal(-1, login.AccountList.SelectedIndex);
    }

    [AvaloniaFact]
    public async Task OK_with_a_saved_account_logs_in_at_its_list_server_and_shows_the_servers()
    {
        using var fixture = new StartFixture();
        fixture.SaveAccount("Other", "x", ListServerProfile.Default.Association);
        int index = fixture.SaveAccount("Staffer", "staff-secret", fixture.TestProfile.Association);
        LoginWindow login = fixture.ShowLogin();
        login.SelectAccount(index);
        login.NicknameText = "StaffNick";
        TestUi.Find<CheckBox>(login, "GraphicalMenuCheck").IsChecked = false;

        TestUi.Click(TestUi.Find<Button>(login, "OkButton"));
        ServerListWindow list = await fixture.WaitForServersAsync();

        Assert.Single(fixture.Server.Logins);
        Assert.Equal("Staffer", fixture.Server.Logins[0].Account);
        Assert.Equal("staff-secret", fixture.Server.Logins[0].Password);
        Assert.Equal("G3D30123rc2", fixture.Server.Logins[0].Edition);
        Assert.False(fixture.Controller.IsLoginVisible);
        Assert.True(list.IsVisible);
        Assert.Equal("Test Servers", list.Title);
        Assert.Contains("Welcome to the test hub.", list.Status);
        Assert.Equal(["Alpha", "Beta", "Gamma"], fixture.Controller.Servers);
        Assert.Equal("StaffNick", fixture.Services.Options.Current.Nickname);
        Assert.False(fixture.Services.Options.Current.GraphicalMenu);
        Assert.Equal(fixture.TestProfile.Association, fixture.Services.Accounts.Accounts[index].ListServer);
        Shots.Save(list, "server-list-dark");
    }

    [AvaloniaFact]
    public async Task A_new_account_logs_in_at_the_profile_chosen_in_the_settings()
    {
        using var fixture = new StartFixture();
        fixture.Controller.ShowListServerSettings(null);
        fixture.Controller.Settings!.SelectProfile("Test");
        TestUi.Settle();
        Assert.Equal("Test", fixture.Controller.CurrentProfile.Name);
        fixture.Controller.Settings!.Close();

        LoginWindow login = fixture.ShowLogin();
        login.AccountText = "Newcomer";
        login.PasswordText = "new-pw";
        TestUi.Click(TestUi.Find<Button>(login, "OkButton"));
        await fixture.WaitForServersAsync();

        Assert.Equal("Newcomer", fixture.Server.Logins.Single().Account);
        SavedAccount saved = fixture.Services.Accounts.Accounts.Single(a => a.Name == "Newcomer");
        Assert.Equal(fixture.TestProfile.Association, saved.ListServer);
        Assert.True(saved.HasPassword);
    }

    [AvaloniaFact]
    public void An_empty_account_focuses_the_field_and_sends_nothing()
    {
        using var fixture = new StartFixture();
        LoginWindow login = fixture.ShowLogin();
        login.AccountText = string.Empty;
        TestUi.Click(TestUi.Find<Button>(login, "OkButton"));
        Assert.True(fixture.Controller.IsLoginVisible);
        Assert.Empty(fixture.Server.Connections);
    }

    [AvaloniaFact]
    public async Task An_account_whose_list_server_matches_no_profile_asks_first_and_sends_nothing_until_chosen()
    {
        using var fixture = new StartFixture();
        int index = fixture.SaveAccount("Wanderer", "wander-pw", "Retired network");
        LoginWindow login = fixture.ShowLogin();
        login.SelectAccount(index);

        TestUi.Click(TestUi.Find<Button>(login, "OkButton"));
        RcDialog dialog = TestUi.FindDialog();
        Assert.Contains("Retired network", dialog.Message);
        Assert.Contains("Alpaxis (listserver.alpaxis.net:14922)", dialog.Message);
        Shots.Save(dialog, "choose-list-server");
        await Task.Delay(100);
        TestUi.Settle();
        Assert.Empty(fixture.Server.Connections);
        Assert.Empty(fixture.Server.Logins);
        Assert.True(fixture.Controller.IsLoginVisible);

        ListBox choices = TestUi.Find<ListBox>(dialog, Dialogs.ListName);
        Assert.Equal(2, choices.ItemCount);
        choices.SelectedIndex = 1;
        dialog.Accept();
        await fixture.WaitForServersAsync();

        Assert.Equal("wander-pw", fixture.Server.Logins.Single().Password);
        Assert.Equal(fixture.TestProfile.Association, fixture.Services.Accounts.Accounts[index].ListServer);
    }

    [AvaloniaFact]
    public async Task An_account_saved_for_an_unknown_endpoint_can_be_cancelled_or_kept_as_a_profile()
    {
        using var fixture = new StartFixture();
        string association = "Old hub — 127.0.0.1:" + fixture.Server.Port;
        Assert.Equal(ProfileChangeStatus.Saved, fixture.Services.Profiles.Remove("Test"));
        int index = fixture.SaveAccount("Veteran", "vet-pw", association);
        LoginWindow login = fixture.ShowLogin();
        login.SelectAccount(index);

        TestUi.Click(TestUi.Find<Button>(login, "OkButton"));
        RcDialog dialog = TestUi.FindDialog();
        Assert.Contains("which is not a saved profile", dialog.Message);
        dialog.Cancel();
        TestUi.Settle();
        Assert.Empty(fixture.Server.Logins);
        Assert.True(fixture.Controller.IsLoginVisible);
        Assert.True(login.IsBusy == false);

        TestUi.Click(TestUi.Find<Button>(login, "OkButton"));
        dialog = TestUi.FindDialog();
        ListBox choices = TestUi.Find<ListBox>(dialog, Dialogs.ListName);
        Assert.Equal(choices.ItemCount - 1, choices.SelectedIndex);
        dialog.Accept();
        await fixture.WaitForServersAsync();

        Assert.Equal("Veteran", fixture.Server.Logins.Single().Account);
        ListServerProfile kept = fixture.Services.Profiles.Find("Old hub")!;
        Assert.Equal(fixture.Server.Port, kept.Port);
        Assert.Equal(kept.Association, fixture.Services.Accounts.Accounts[index].ListServer);
    }

    [AvaloniaFact]
    public async Task The_MCP_login_refuses_an_unconfirmed_list_server_without_asking()
    {
        using var fixture = new StartFixture();
        fixture.SaveAccount("Wanderer", "wander-pw", string.Empty);
        ConnectResult result = await fixture.Controller.SubmitLoginAsync("Wanderer", "Nick");
        Assert.False(result.Success);
        Assert.Contains("Nothing was sent", result.Error);
        Assert.DoesNotContain(RcWindow.OpenWindows, window => window is RcDialog);
        Assert.Empty(fixture.Server.Connections);
    }

    [AvaloniaFact]
    public async Task The_MCP_login_works_without_the_controls()
    {
        using var fixture = new StartFixture();
        fixture.SaveAccount("Staffer", "staff-secret", fixture.TestProfile.Association);
        ConnectResult missing = await fixture.Controller.SubmitLoginAsync("Nobody", "Nick");
        Assert.False(missing.Success);
        Assert.Equal("The existing login form has no password", missing.Error);

        ConnectResult result = await fixture.Controller.SubmitLoginAsync("Staffer", "McpNick");
        Assert.True(result.Success, result.Error);
        Assert.Equal("Staffer", fixture.Server.Logins.Single().Account);
        Assert.Equal(["Alpha", "Beta", "Gamma"], fixture.Controller.Servers);

        ConnectResult unknown = await fixture.Controller.ConnectAsync("Nowhere");
        Assert.Equal("Named server was not found", unknown.Error);
        ConnectResult connected = await fixture.Controller.ConnectAsync("beta");
        Assert.True(connected.Success);
        ServerChoiceAssert(fixture, "H Beta", "Staffer", "staff-secret", "McpNick");
        Assert.Null(fixture.Choices.Single().SwitchFor);
        Assert.Null(fixture.Controller.ServerList);
        Assert.False(fixture.Controller.IsLoginVisible);
    }

    [AvaloniaFact]
    public async Task Direct_mode_connects_without_the_list_server()
    {
        using var fixture = new StartFixture();
        int index = fixture.SaveAccount("Direct", "direct-pw", "127.0.0.1:14999", directMode: true);
        LoginWindow login = fixture.ShowLogin();
        login.SelectAccount(index);
        login.NicknameText = "DirectNick";

        TestUi.Click(TestUi.Find<Button>(login, "OkButton"));
        await TestUi.WaitUntilAsync(() => fixture.Choices.Count == 1, what: "the direct connect");

        ServerChoiceAssert(fixture, "127.0.0.1", "Direct", "direct-pw", "DirectNick");
        Assert.Equal(14999, fixture.Choices[0].Login.Server.Port);
        Assert.Equal(string.Empty, fixture.Choices[0].Login.ListServer);
        Assert.Empty(fixture.Server.Connections);
        Assert.False(fixture.Controller.IsLoginVisible);
        Assert.Null(fixture.Controller.ServerList);
    }

    [AvaloniaFact]
    public void A_failed_direct_connect_shows_the_login_window_again_with_the_reason()
    {
        using var fixture = new StartFixture();
        fixture.Connect = _ => Task.FromResult(ConnectResult.Failed("Connection refused"));
        int index = fixture.SaveAccount("Direct", "direct-pw", "127.0.0.1:14999", directMode: true);
        LoginWindow login = fixture.ShowLogin();
        login.SelectAccount(index);
        TestUi.Click(TestUi.Find<Button>(login, "OkButton"));

        RcDialog error = TestUi.FindDialog();
        Assert.Equal("Connection Error", error.Title);
        Assert.Contains("Connection refused", error.Message);
        error.Accept();
        Assert.True(fixture.Controller.IsLoginVisible);
    }

    [AvaloniaFact]
    public async Task A_direct_account_without_a_server_address_is_refused()
    {
        using var fixture = new StartFixture();
        int index = fixture.SaveAccount("Direct", "direct-pw", "nowhere", directMode: true);
        fixture.ShowLogin().SelectAccount(index);
        ConnectResult result = await fixture.Controller.SubmitLoginAsync(string.Empty, string.Empty);
        Assert.False(result.Success);
        Assert.Contains("Direct Mode", result.Error);
        Assert.Empty(fixture.Choices);
    }

    [AvaloniaFact]
    public async Task A_wrong_password_shows_the_list_servers_error_and_keeps_the_window_usable()
    {
        using var fixture = new StartFixture();
        fixture.Server.Error = "Account name or password is invalid";
        fixture.SaveAccount("Staffer", "wrong", fixture.TestProfile.Association);
        ConnectResult result = await fixture.Controller.SubmitLoginAsync("Staffer", string.Empty);

        Assert.False(result.Success);
        Assert.Equal("Account name or password is invalid", result.Error);
        ServerListWindow list = fixture.Controller.ServerList!;
        Assert.Equal("Account name or password is invalid", list.Status);
        Assert.True(list.StatusIsError);
        Assert.True(TestUi.Find<Button>(list, "RefreshButton").IsEnabled);
        Shots.Save(list, "server-list-error");

        fixture.Server.Error = null;
        TestUi.Click(TestUi.Find<Button>(list, "RefreshButton"));
        await fixture.WaitForServersAsync();
        Assert.False(list.StatusIsError);
        Assert.Equal(2, fixture.Server.Logins.Count);
    }

    [AvaloniaFact]
    public async Task Closing_the_server_list_during_a_login_cancels_it_and_brings_the_login_back()
    {
        using var fixture = new StartFixture();
        fixture.Server.Silent = true;
        fixture.SaveAccount("Staffer", "pw", fixture.TestProfile.Association);
        Task<ConnectResult> login = fixture.Controller.SubmitLoginAsync("Staffer", string.Empty);
        await TestUi.WaitUntilAsync(() => fixture.Server.Logins.Count == 1, what: "the login packet");
        ServerListWindow list = fixture.Controller.ServerList!;
        Assert.True(list.IsLoggingIn);
        Assert.StartsWith("Connecting to Test", list.Status);
        Assert.False(TestUi.Find<Button>(list, "RefreshButton").IsEnabled);

        list.Close();
        ConnectResult result = await login;
        Assert.False(result.Success);
        Assert.Contains("cancelled", result.Error);
        Assert.True(await fixture.Server.Connections[0].WaitForCloseAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(fixture.Controller.ServerList);
        Assert.True(fixture.Controller.IsLoginVisible);
    }

    [AvaloniaFact]
    public void Close_hides_the_login_window_and_tells_the_program()
    {
        using var fixture = new StartFixture();
        int closed = 0;
        fixture.Controller.LoginClosed += (_, _) => closed++;
        LoginWindow login = fixture.ShowLogin();
        TestUi.Click(TestUi.Find<Button>(login, "CloseButton"));
        Assert.Equal(1, closed);
        Assert.False(fixture.Controller.IsLoginVisible);
        Assert.False(login.IsClosed);

        fixture.ShowLogin();
        login.Close();
        TestUi.Settle();
        Assert.Equal(2, closed);
        Assert.False(login.IsClosed);
        fixture.Controller.HideLogin();
        Assert.False(fixture.Controller.IsLoginVisible);
    }

    [AvaloniaFact]
    public void The_gear_and_the_pencil_open_their_windows()
    {
        using var fixture = new StartFixture();
        LoginWindow login = fixture.ShowLogin();
        TestUi.Click(TestUi.Find<Button>(login, "ListServerSettingsButton"));
        Assert.NotNull(fixture.Controller.Settings);
        Assert.True(fixture.Controller.Settings!.IsVisible);
        TestUi.Click(TestUi.Find<Button>(login, "ManageAccountsButton"));
        Assert.NotNull(fixture.Controller.AccountsManager);
    }

    internal static void ServerChoiceAssert(StartFixture fixture, string server, string account, string password, string nickname)
    {
        SessionLoginCheck(fixture.Choices.Last().Login, server, account, password, nickname);
    }

    private static void SessionLoginCheck(RemoteControl.Ui.Shell.SessionLogin login, string server, string account, string password, string nickname)
    {
        Assert.Equal(server, login.Server.Name);
        Assert.Equal(account, login.Account);
        Assert.Equal(password, login.Password);
        Assert.Equal(nickname, login.Nickname);
    }
}
