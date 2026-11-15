using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Start.Tests;

public sealed class AccountsTests
{
    [AvaloniaFact]
    public void The_manager_lists_the_accounts_with_their_badges()
    {
        using var fixture = new StartFixture("dracula");
        fixture.SaveAccount("Staffer", "pw", fixture.TestProfile.Association);
        fixture.SaveAccount("Plain", string.Empty, string.Empty);
        fixture.ShowLogin();
        AccountsWindow manager = fixture.Controller.ShowAccountsManager();
        TestUi.Settle();
        Assert.Equal("Accounts", manager.Title);
        Assert.Equal(["Staffer", "Plain"], manager.Rows.Select(row => row.Name));
        Assert.Equal(["Test", "Unassigned"], manager.Rows.Select(row => row.Badge));
        Assert.False(manager.AttentionPanel.IsVisible);
        Assert.Same(manager, fixture.Controller.ShowAccountsManager());
        Shots.Save(manager, "accounts-dracula");
    }

    [AvaloniaFact]
    public void Add_starts_with_the_current_profile_and_saves_the_account()
    {
        using var fixture = new StartFixture("light");
        fixture.ShowLogin();
        AccountsWindow manager = fixture.Controller.ShowAccountsManager();
        TestUi.Click(TestUi.Find<Button>(manager, "AddButton"));
        AccountEditWindow editor = manager.Editor!;
        Assert.Equal("Add Account", editor.Title);
        Assert.Equal(ListServerProfile.Default.Association, editor.SelectedValue);
        Assert.Equal(["Alpaxis", "Test"], editor.Choices.Select(choice => choice.Text));
        double natural = StartFixture.NaturalWidth(editor);
        Assert.True(natural <= 320, "natural width " + natural);
        Shots.Save(editor, "account-add-light");

        Assert.False(editor.Save());
        Assert.Equal("Enter the account name.", editor.Status);

        editor.Fill("Alice", "alice-pw", dontSavePassword: false, directMode: false, listServer: "Test");
        TestUi.Click(TestUi.Find<Button>(editor, "SaveButton"));
        Assert.True(editor.IsClosed);
        SavedAccount saved = fixture.Services.Accounts.Accounts.Single();
        Assert.Equal("Alice", saved.Name);
        Assert.Equal(fixture.TestProfile.Association, saved.ListServer);
        Assert.Equal("alice-pw", fixture.Services.Accounts.GetPassword(0));
        Assert.Equal(["Alice"], manager.Rows.Select(row => row.Name));
        Assert.Equal("Alice", fixture.Controller.Login.Rows.Single().Name);
    }

    [AvaloniaFact]
    public void Edit_changes_the_account_and_double_click_opens_it()
    {
        using var fixture = new StartFixture();
        fixture.SaveAccount("Alice", "alice-pw", fixture.TestProfile.Association);
        fixture.SaveAccount("Bob", string.Empty, fixture.TestProfile.Association);
        AccountsWindow manager = fixture.Controller.ShowAccountsManager();
        manager.SelectRow(0);
        TestUi.Click(TestUi.Find<Button>(manager, "EditButton"));
        AccountEditWindow editor = manager.Editor!;
        Assert.Equal("Edit Account", editor.Title);
        Assert.Equal(0, editor.Index);
        Assert.Equal("Alice", TestUi.Find<TextBox>(editor, "NameField").Text);
        Assert.Equal("alice-pw", TestUi.Find<TextBox>(editor, "PasswordField").Text);
        Assert.Equal(fixture.TestProfile.Association, editor.SelectedValue);
        Shots.Save(editor, "account-edit-dark");

        editor.Fill("Alicia", "new-pw", dontSavePassword: false, directMode: false, listServer: "Alpaxis");
        Assert.True(editor.Save());
        SavedAccount changed = fixture.Services.Accounts.Accounts[0];
        Assert.Equal("Alicia", changed.Name);
        Assert.Equal(ListServerProfile.Default.Association, changed.ListServer);
        Assert.Equal("new-pw", fixture.Services.Accounts.GetPassword(0));

        manager.SelectRow(1);
        AccountEditWindow second = manager.OpenEditor(1);
        TestUi.Find<CheckBox>(second, "DontSavePasswordCheck").IsChecked = true;
        TestUi.Find<TextBox>(second, "PasswordField").Text = "ignored";
        Assert.True(second.Save());
        Assert.False(fixture.Services.Accounts.Accounts[1].HasPassword);
    }

    [AvaloniaFact]
    public void Direct_mode_needs_a_server_address()
    {
        using var fixture = new StartFixture();
        AccountsWindow manager = fixture.Controller.ShowAccountsManager();
        AccountEditWindow editor = manager.OpenEditor(-1);
        editor.Fill("Direct", "pw", dontSavePassword: false, directMode: true, listServer: "Test");
        Assert.True(editor.Save());
        SavedAccount saved = fixture.Services.Accounts.Accounts.Single();
        Assert.True(saved.DirectMode);
        Assert.Equal(fixture.TestProfile.Association, saved.ListServer);
    }

    [AvaloniaFact]
    public async Task Delete_asks_first()
    {
        using var fixture = new StartFixture();
        fixture.SaveAccount("Alice", "pw", fixture.TestProfile.Association);
        fixture.SaveAccount("Bob", "pw", fixture.TestProfile.Association);
        AccountsWindow manager = fixture.Controller.ShowAccountsManager();
        manager.SelectRow(0);

        TestUi.Click(TestUi.Find<Button>(manager, "DeleteButton"));
        RcDialog confirm = TestUi.FindDialog();
        Assert.Contains("Alice", confirm.Message);
        confirm.Cancel();
        TestUi.Settle();
        Assert.Equal(2, fixture.Services.Accounts.Accounts.Count);

        Task deleting = manager.DeleteSelectedAsync();
        TestUi.FindDialog().Accept();
        await deleting;
        Assert.Equal(["Bob"], fixture.Services.Accounts.Names);
        Assert.Equal(["Bob"], manager.Rows.Select(row => row.Name));
    }

    [AvaloniaFact]
    public void Select_puts_the_account_into_the_login_window()
    {
        using var fixture = new StartFixture();
        fixture.SaveAccount("Alice", "alice-pw", fixture.TestProfile.Association);
        fixture.SaveAccount("Bob", "bob-pw", fixture.TestProfile.Association);
        LoginWindow login = fixture.ShowLogin();
        Assert.Equal("Bob", login.AccountText);
        AccountsWindow manager = fixture.Controller.ShowAccountsManager();
        manager.SelectRow(0);
        TestUi.Click(TestUi.Find<Button>(manager, "SelectButton"));
        Assert.True(manager.IsClosed);
        Assert.Equal("Alice", login.AccountText);
        Assert.Equal("alice-pw", login.PasswordText);
        Assert.Equal(0, login.SelectedIndex);
    }

    [AvaloniaFact]
    public void The_profile_combo_follows_the_list_server_settings()
    {
        using var fixture = new StartFixture();
        AccountsWindow manager = fixture.Controller.ShowAccountsManager();
        AccountEditWindow editor = manager.OpenEditor(-1);
        TestUi.Click(TestUi.Find<Button>(editor, "ManageListServersButton"));
        ListServerSettingsWindow settings = fixture.Controller.Settings!;
        settings.NewProfile();
        settings.Edit("Later", "later.test", "15000");
        settings.FlushPendingSave();
        Assert.Contains(editor.Choices, choice => choice.Text == "Later");
        settings.Close();
        TestUi.Settle();
        Assert.False(editor.IsClosed);
        Assert.Contains(editor.Choices, choice => choice.Text == "Later");
    }

    [AvaloniaFact]
    public async Task Unreadable_saved_accounts_are_reported_and_never_overwritten()
    {
        using var fixture = new StartFixture();
        AccountStore store = fixture.Services.Accounts;
        byte[] key = [1, 2, 3, 4, 5];
        byte[] data = "GSRCACC1 this is not a container"u8.ToArray();
        await File.WriteAllBytesAsync(store.KeyFile, key);
        await File.WriteAllBytesAsync(store.DataFile, data);
        Assert.True(store.Load().NeedsAttention);

        AccountsWindow manager = fixture.Controller.ShowAccountsManager();
        TestUi.Settle();
        Assert.True(manager.AttentionPanel.IsVisible);
        Assert.False(string.IsNullOrEmpty(manager.AttentionText.Text));
        Shots.Save(manager, "accounts-attention");

        AccountEditWindow editor = manager.OpenEditor(-1);
        editor.Fill("Alice", "pw", dontSavePassword: false, directMode: false, listServer: "Test");
        Assert.False(editor.Save());
        Assert.Contains("Nothing was changed", editor.Status);
        editor.Close();

        // A login still works, but the account is not written over the unreadable files.
        ConnectResult login = await fixture.Controller.SubmitLoginAsync(string.Empty, string.Empty);
        Assert.False(login.Success);
        LoginWindow form = fixture.Controller.Login;
        form.AccountText = "Typed";
        form.PasswordText = "pw";
        fixture.Controller.CurrentProfile = fixture.TestProfile;
        Assert.True((await fixture.Controller.SubmitLoginAsync(string.Empty, string.Empty)).Success);
        Assert.Equal(key, await File.ReadAllBytesAsync(store.KeyFile));
        Assert.Equal(data, await File.ReadAllBytesAsync(store.DataFile));

        TestUi.Click(TestUi.Find<Button>(manager, "StartFreshButton"));
        TestUi.FindDialog().Cancel();
        TestUi.Settle();
        Assert.True(File.Exists(store.DataFile));
        Assert.False(store.CanSave);

        TestUi.Click(TestUi.Find<Button>(manager, "StartFreshButton"));
        TestUi.FindDialog().Accept();
        RcDialog moved = TestUi.FindDialog();
        Assert.Contains(".old-", moved.Message);
        moved.Accept();
        TestUi.Settle();
        Assert.True(store.CanSave);
        Assert.False(manager.AttentionPanel.IsVisible);
        string[] kept = Directory.GetFiles(store.Directory, "accounts.*.old-*").Concat(Directory.GetFiles(store.Directory, "accounts.*old*")).Distinct().ToArray();
        Assert.NotEmpty(kept);
        Assert.Contains(kept, path => File.ReadAllBytes(path).SequenceEqual(data));
    }
}
