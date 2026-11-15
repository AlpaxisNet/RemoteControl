using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Text;
using RemoteControl.Protocol.Testing;
using RemoteControl.Protocol.Wire;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Players.Tests;

public sealed class BanAndAccountTests
{
    [AvaloniaFact]
    public async Task The_new_ban_type_shows_four_tabs_and_sends_setban()
    {
        using var fixture = new Fixture();
        fixture.Fake.RaiseBanList("bantypes", string.Empty, "Short,3600\nLong,864000\nForever,999999999");
        long release = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 7200 + 30;
        fixture.Fake.RaiseBanData("Graal7", "1234", $"account=Graal7,world=local,bantype=Long,releasetime={release},reason=cheating\naccount=pc:1234,world=all,bantype=Forever,reason=alt");
        BanWindow window = fixture.Latest<BanWindow>();
        Assert.Equal("Edit Access of Graal7 (computer: 1234)", window.Title);
        Assert.True(window.UseNewBanType);
        Assert.All(Enumerable.Range(0, 4), index => Assert.True(window.ScopeAt(index).Tab.IsVisible));
        BanWindow.Scope local = window.ScopeAt(0);
        Assert.True(local.Banned.IsChecked);
        Assert.Equal("Long (10 days)", ((BanType)local.Type.SelectedItem!).Label);
        Assert.Equal("Ban time left: 2 hours", local.TimeLeft.Text);
        Assert.Equal("cheating", local.Reason.Text);
        Assert.True(window.ScopeAt(3).Banned.IsChecked);
        Assert.False(window.ScopeAt(1).Banned.IsChecked);
        Shots.Save(window, "ban-new");

        local.Reason.Text = "still cheating";
        Assert.True(await window.ApplyAsync(0));
        RecordedCall call = fixture.Fake.LastCall("SetBanAsync")!;
        Assert.Equal(new object?[] { "Graal7", "local", true, "Long", release.ToString(System.Globalization.CultureInfo.InvariantCulture), "still cheating" }, call.Arguments);
        Assert.False(window.IsVisible);

        window.ScopeAt(3).ResetTime.IsChecked = true;
        window.ScopeAt(3).Banned.IsChecked = false;
        Assert.True(await window.ApplyAsync(3));
        Assert.Equal(new object?[] { "pc:1234", "all", false, "Forever", "", "alt" }, fixture.Fake.LastCall("SetBanAsync")!.Arguments);
        Fixture.Click(TestUi.Find<Button>(window, "HistoryButton"));
        Assert.Equal("Graal7", fixture.Fake.LastCall("RequestBanHistoryAsync")!.Argument<string>(0));
        Fixture.Click(TestUi.Find<Button>(window, "ActivityButton"));
        Assert.Equal("Graal7", fixture.Fake.LastCall("RequestStaffActivityAsync")!.Argument<string>(0));
        Assert.Same(window, fixture.Context.Windows.Find("ban"));
    }

    [AvaloniaFact]
    public async Task The_legacy_ban_type_shows_only_the_local_ban()
    {
        using var fixture = new Fixture(options: o => o with { UseNewBanType = false });
        fixture.Fake.RaiseBanData("Graal7", string.Empty, BanText.FormatLegacyDetails(true, "old reason"));
        BanWindow window = fixture.Latest<BanWindow>();
        Assert.False(window.UseNewBanType);
        Assert.Equal("Edit Access of Graal7", window.Title);
        Assert.True(window.ScopeAt(0).Tab.IsVisible);
        Assert.All(Enumerable.Range(1, 3), index => Assert.False(window.ScopeAt(index).Tab.IsVisible));
        Assert.True(window.ScopeAt(0).Banned.IsChecked);
        Shots.Save(window, "ban-legacy");
        window.ScopeAt(0).Banned.IsChecked = false;
        window.ScopeAt(0).Reason.Text = "appeal accepted";
        Assert.True(await window.ApplyAsync(0));
        Assert.Equal(new object?[] { "Graal7", false, "appeal accepted" }, fixture.Fake.LastCall("SetLegacyPlayerBanAsync")!.Arguments);
        Assert.False(fixture.Fake.WasCalled("SetBanAsync"));
    }

    [AvaloniaFact]
    public async Task The_ban_window_after_a_disconnect_says_so()
    {
        using var fixture = new Fixture();
        fixture.Fake.RaiseBanData("Graal7", string.Empty, string.Empty);
        BanWindow window = fixture.Latest<BanWindow>();
        fixture.Fake.RaiseDisconnected();
        Task<bool> apply = window.ApplyAsync(0);
        TestUi.FindDialog().Accept();
        Assert.False(await apply);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void The_accounts_list_fills_from_the_reply_and_its_menu_sends_every_call()
    {
        using var fixture = new Fixture();
        var list = (AccountsListWindow)fixture.Context.OpenTool(ToolIds.Accounts)!;
        Assert.Equal("Accounts List - Test Server", list.Title);
        fixture.Fake.RaiseAccountList(new[] { "Alpha", "Beta", "Gamma" });
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, list.Accounts);
        Shots.Save(list, "accounts-list");
        Fixture.FocusList(TestUi.Find<ListBox>(list, "AccountList"));
        TestUi.TypeText(list, "gam");
        Assert.Equal("Gamma", TestUi.Find<ListBox>(list, "AccountList").SelectedItem);

        fixture.Fake.ClearCalls();
        List<Control> menu = fixture.Players.AccountMenu(list, "Beta");
        Assert.Equal(new[] { "Edit Attributes", "-", "Edit Account", "Edit Rights", "Edit Comments", "Edit Access", "Ban History", "Staff Activity", "-", "Reset", "Delete" },
            menu.Select(item => item is MenuItem menuItem ? (string)menuItem.Header! : "-"));
        foreach ((string header, string method) in new[]
        {
            ("Edit Attributes", "RequestPlayerAttributesAsync"), ("Edit Account", "RequestAccountAsync"), ("Edit Rights", "RequestPlayerRightsAsync"),
            ("Edit Comments", "RequestPlayerCommentsAsync"), ("Ban History", "RequestBanHistoryAsync"), ("Staff Activity", "RequestStaffActivityAsync"),
        })
        {
            Fixture.Click(Fixture.Item(menu, header));
            TestUi.WaitUntil(() => fixture.Fake.WasCalled(method), what: method);
            Assert.Equal("Beta", fixture.Fake.LastCall(method)!.Arguments[0]);
        }
        Fixture.Click(Fixture.Item(menu, "Edit Access"));
        TestUi.WaitUntil(() => fixture.Fake.WasCalled("RequestNewPlayerBanAsync"));
        Assert.Equal(new object?[] { "Beta", -1 }, fixture.Fake.LastCall("RequestNewPlayerBanAsync")!.Arguments);

        Fixture.Click(Fixture.Item(menu, "Reset"));
        RcDialog reset = TestUi.FindDialog();
        Assert.Equal("Do you really want to reset the attributes of Beta?", reset.Message);
        reset.Accept();
        TestUi.WaitUntil(() => fixture.Fake.WasCalled("ResetPlayerAsync"));
        Fixture.Click(Fixture.Item(menu, "Delete"));
        RcDialog delete = TestUi.FindDialog();
        Assert.Equal("Do you really want to delete the account Beta?", delete.Message);
        delete.Accept();
        TestUi.WaitUntil(() => fixture.Fake.WasCalled("SendRawPacketAsync"));
        RecordedCall raw = fixture.Fake.LastCall("SendRawPacketAsync")!;
        Assert.Equal((int)ClientPacketId.AccountDelete, raw.Argument<int>(0));
        Assert.Equal("Beta"u8.ToArray(), raw.Argument<byte[]>(1));
    }

    [AvaloniaFact]
    public async Task Get_accounts_list_sends_the_specification()
    {
        using var fixture = new Fixture();
        var list = (AccountsListWindow)fixture.Context.OpenTool(ToolIds.Accounts)!;
        Fixture.Click(TestUi.Find<Button>(list, "GetAccountsButton"));
        GetAccountsWindow query = fixture.Latest<GetAccountsWindow>();
        Assert.Equal("Get Accounts List - Test Server", query.Title);
        Shots.Save(query, "get-accounts");
        query.Specification = "Stef%";
        query.Conditions = "adminlevel>0";
        Assert.True(await query.GetListAsync());
        Assert.Equal(new object?[] { "Stef%", "adminlevel>0" }, fixture.Fake.LastCall("RequestAccountListAsync")!.Arguments);
    }

    [AvaloniaFact]
    public async Task The_account_editor_edits_and_adds()
    {
        using var fixture = new Fixture();
        var data = new AccountData { Account = "Beta", Email = "b@example.invalid", AdminLevel = 2, AdminWorlds = "main", Banned = true, Guest = false, BanLength = "3 days", BanReason = "reason\nmore" };
        fixture.Fake.RaiseAccount(data);
        AccountEditorWindow editor = fixture.Latest<AccountEditorWindow>();
        Assert.Equal("Edit account of Beta - Test Server", editor.Title);
        Assert.Equal(data with { Password = string.Empty }, editor.Current with { Password = string.Empty });
        Assert.Equal(data.Account, AccountText.Parse(editor.EditorText).Account);
        Assert.True(AccountText.Parse(editor.EditorText).Banned);
        Shots.Save(editor, "account-editor");
        TestUi.Find<TextBox>(editor, "EmailBox").Text = "new@example.invalid";
        Assert.True(await editor.ApplyAsync());
        RecordedCall set = fixture.Fake.LastCall("SetAccountAsync")!;
        Assert.Equal("Beta", set.Argument<string>(0));
        Assert.Equal("new@example.invalid", set.Argument<AccountData>(1).Email);
        Assert.True(editor.IsVisible);

        fixture.Fake.RaiseAccount(data with { Email = "again@example.invalid" });
        Assert.Same(editor, fixture.Latest<AccountEditorWindow>());
        Assert.Equal("again@example.invalid", editor.Current.Email);

        var list = (AccountsListWindow)fixture.Context.OpenTool(ToolIds.Accounts)!;
        Fixture.Click(TestUi.Find<Button>(list, "AddButton"));
        AccountEditorWindow added = fixture.Context.Windows.All.OfType<AccountEditorWindow>().Single(window => window.IsNew);
        Assert.Equal("Add new account - Test Server", added.Title);
        Assert.Equal(0, added.Current.AdminLevel);
        Assert.Equal("all", added.Current.AdminWorlds);
        TestUi.Find<TextBox>(added, "NameBox").Text = "Newbie";
        TestUi.Find<TextBox>(added, "PasswordBox").Text = "secret";
        Assert.True(await added.ApplyAsync());
        AccountData sent = fixture.Fake.LastCall("AddAccountAsync")!.Argument<AccountData>(0);
        Assert.Equal(("Newbie", "secret"), (sent.Account, sent.Password));
    }

    [AvaloniaFact]
    public async Task Apply_after_a_reconnect_goes_to_the_new_connection_for_accounts_too()
    {
        using var fixture = new Fixture();
        fixture.Fake.RaiseAccount(new AccountData { Account = "Beta" });
        AccountEditorWindow editor = fixture.Latest<AccountEditorWindow>();
        var second = new FakeRcSession("tester");
        fixture.Context.Replace(second, fixture.Test.Login);
        Assert.True(await editor.ApplyAsync());
        Assert.True(second.WasCalled("SetAccountAsync"));
        Assert.False(fixture.Fake.WasCalled("SetAccountAsync"));
        second.RaiseAccountList(new[] { "Late" });
        Assert.Equal(new[] { "Late" }, fixture.Latest<AccountsListWindow>().Accounts);
    }

    [AvaloniaFact]
    public void Pictures_in_two_themes()
    {
        foreach (string theme in new[] { "dracula", "light" })
        {
            using var fixture = new Fixture(theme);
            fixture.Fake.RaiseBanList("bantypes", string.Empty, "Short,3600\nLong,864000");
            fixture.Fake.RaiseBanData("Graal7", "1234", "account=Graal7,world=all,bantype=Short,reason=spam");
            Shots.Save(fixture.Latest<BanWindow>(), "ban-" + theme);
            fixture.Fake.RaisePlayerRights("Graal7", new PlayerRights(0b1011, "*", "rw levels/*"));
            Shots.Save(fixture.Latest<RightsWindow>(), "rights-" + theme);
            fixture.Fake.RaiseAccount(new AccountData { Account = "Beta", Email = "b@example.invalid" });
            Shots.Save(fixture.Latest<AccountEditorWindow>(), "account-" + theme);
            fixture.Fake.RaisePlayerComments("Graal7", "A comment");
            Shots.Save(fixture.Latest<CommentsWindow>(), "comments-" + theme);
            fixture.Fake.RaiseProfile("Graal7", new[] { "Bob", "21", "male", "DE", "", "", "", "", "quote", "100", "a.nw", "1", "2" });
            Shots.Save(fixture.Latest<ProfileWindow>(), "profile-" + theme);
            fixture.Fake.RaiseBanList("banhistory", "Graal7", "2026-01-02 10:11:12 by Admin:\nbanned");
            Shots.Save(fixture.Latest<TextViewerWindow>(), "banhistory-" + theme);
            fixture.Fake.RaiseAccountList(new[] { "Alpha", "Beta" });
            Shots.Save(fixture.Latest<AccountsListWindow>(), "accounts-" + theme);
        }
    }
}
