using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Core;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Start.Tests;

public sealed class ListServerSettingsTests
{
    private static ListServerSettingsWindow Open(StartFixture fixture)
    {
        fixture.Controller.ShowListServerSettings(null);
        TestUi.Settle();
        return fixture.Controller.Settings!;
    }

    [AvaloniaTheory]
    [InlineData("dark")]
    [InlineData("solarized")]
    public void The_window_is_compact_and_shows_the_built_in_profile_as_protected(string theme)
    {
        using var fixture = new StartFixture(theme);
        ListServerSettingsWindow settings = Open(fixture);
        Assert.Equal("RC settings", settings.Title);
        Assert.Equal(["Alpaxis — listserver.alpaxis.net:14922 (built in)", "Test — 127.0.0.1:" + fixture.Server.Port], settings.Choices.Select(c => c.Text));
        Assert.Equal(0, TestUi.Find<ComboBox>(settings, "ProfileCombo").SelectedIndex);
        Assert.False(TestUi.Find<TextBox>(settings, "NameField").IsEnabled);
        Assert.False(TestUi.Find<Button>(settings, "DeleteProfileButton").IsEnabled);
        Assert.False(TestUi.Find<Button>(settings, "MoveUpButton").IsEnabled);
        Assert.Contains("built in", settings.Status);
        double natural = StartFixture.NaturalWidth(settings);
        Assert.True(natural <= 300, "natural width " + natural);
        Shots.Save(settings, "rc-settings-" + theme);
    }

    [AvaloniaFact]
    public async Task Adding_editing_and_removing_profiles_is_saved_after_the_delay()
    {
        using var fixture = new StartFixture();
        ListServerSettingsWindow settings = Open(fixture);
        TestUi.Click(TestUi.Find<Button>(settings, "NewProfileButton"));
        Assert.Null(settings.EditingName);
        Assert.Equal("14922", TestUi.Find<TextBox>(settings, "PortField").Text);
        settings.Edit("Mine", "mine.test", "15000");
        Assert.True(settings.IsSavePending);
        await TestUi.WaitUntilAsync(() => fixture.Services.Profiles.Find("Mine") is not null, what: "the debounced save");
        Assert.Equal("Saved.", settings.Status);
        Assert.Equal(new ListServerProfile("Mine", "mine.test", 15000), fixture.Services.Profiles.Find("Mine"));
        Assert.Equal("Mine", fixture.Controller.CurrentProfile.Name);
        Assert.Contains("Mine\tmine.test\t15000", await File.ReadAllTextAsync(fixture.Services.Profiles.FilePath));

        settings.Edit("Mine2", "mine2.test", "15001");
        bool pending = settings.IsSavePending;
        settings.FlushPendingSave();
        Assert.True(fixture.Services.Profiles.Find("Mine") is null, $"pending {pending} status {settings.Status} editing {settings.EditingName} log {string.Join(" | ", fixture.Environment.Log.Snapshot())}");
        Assert.Equal(15001, fixture.Services.Profiles.Find("Mine2")!.Port);

        settings.SelectProfile("Mine2");
        TestUi.Click(TestUi.Find<Button>(settings, "DeleteProfileButton"));
        Assert.Null(fixture.Services.Profiles.Find("Mine2"));
        Assert.Equal("Removed.", settings.Status);
    }

    [AvaloniaFact]
    public void Invalid_and_duplicate_values_are_refused_with_a_reason()
    {
        using var fixture = new StartFixture();
        ListServerSettingsWindow settings = Open(fixture);
        settings.SelectProfile("Test");
        settings.Edit("Test", "127.0.0.1", "0");
        settings.FlushPendingSave();
        Assert.Contains("1 to 65535", settings.Status);
        Assert.Equal(fixture.Server.Port, fixture.Services.Profiles.Find("Test")!.Port);

        settings.Edit("Test", "127.0.0.1", "70000");
        settings.FlushPendingSave();
        Assert.Contains("1 to 65535", settings.Status);

        settings.Edit("Test", string.Empty, "14922");
        settings.FlushPendingSave();
        Assert.Equal("Complete the name, host, and port.", settings.Status);

        settings.NewProfile();
        settings.Edit("test", "other.test", "14922");
        settings.FlushPendingSave();
        Assert.Contains("already a profile named", settings.Status);
        settings.Edit("Alpaxis", "other.test", "14922");
        settings.FlushPendingSave();
        Assert.Contains("already a profile named", settings.Status);
        Assert.Equal(2, fixture.Services.Profiles.Profiles.Count);
    }

    [AvaloniaFact]
    public void Profiles_can_be_reordered_but_the_built_in_one_stays_first()
    {
        using var fixture = new StartFixture();
        fixture.Services.Profiles.Add(new ListServerProfile("Second", "second.test", 14922));
        fixture.Services.Profiles.Add(new ListServerProfile("Third", "third.test", 14922));
        ListServerSettingsWindow settings = Open(fixture);

        settings.SelectProfile("Third");
        TestUi.Click(TestUi.Find<Button>(settings, "MoveUpButton"));
        Assert.Equal(["Test", "Third", "Second"], fixture.Services.Profiles.UserProfiles.Select(p => p.Name));
        settings.MoveSelected(-1);
        Assert.Equal(["Third", "Test", "Second"], fixture.Services.Profiles.UserProfiles.Select(p => p.Name));
        Assert.False(TestUi.Find<Button>(settings, "MoveUpButton").IsEnabled);
        TestUi.Click(TestUi.Find<Button>(settings, "MoveDownButton"));
        Assert.Equal(["Test", "Third", "Second"], fixture.Services.Profiles.UserProfiles.Select(p => p.Name));
        Assert.Equal("Alpaxis", fixture.Services.Profiles.Profiles[0].Name);
    }

    [AvaloniaFact]
    public void The_built_in_Alpaxis_profile_cannot_be_changed_moved_or_removed()
    {
        using var fixture = new StartFixture();
        ListServerSettingsWindow settings = Open(fixture);
        settings.SelectProfile("Alpaxis");
        settings.DeleteSelected();
        Assert.Contains("built in", settings.Status);
        settings.MoveSelected(1);
        Assert.Contains("built in", settings.Status);
        settings.Edit("Hacked", "evil.test", "1");
        settings.FlushPendingSave();
        Assert.Equal(ListServerProfile.Default, fixture.Services.Profiles.Profiles[0]);
        Assert.Null(fixture.Services.Profiles.Find("Hacked"));
        Assert.Equal(ProfileChangeStatus.BuiltIn, fixture.Services.Profiles.Remove("Alpaxis"));
    }

    [AvaloniaFact]
    public void A_pending_edit_is_saved_when_another_profile_is_picked_or_the_window_closes()
    {
        using var fixture = new StartFixture();
        fixture.Services.Profiles.Add(new ListServerProfile("Second", "second.test", 14922));
        ListServerSettingsWindow settings = Open(fixture);
        settings.SelectProfile("Second");
        settings.Edit("Second", "renamed.test", "14922");
        settings.SelectProfile("Test");
        TestUi.Settle();
        Assert.Equal("renamed.test", fixture.Services.Profiles.Find("Second")!.Host);
        Assert.Equal("Test", settings.EditingName);
        Assert.Equal("127.0.0.1", TestUi.Find<TextBox>(settings, "HostField").Text);

        settings.Edit("Test", "127.0.0.2", fixture.Server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        int closed = 0;
        fixture.Controller.ListServerSettingsClosed += (_, _) => closed++;
        TestUi.Click(TestUi.Find<Button>(settings, "CloseButton"));
        Assert.True(settings.IsClosed);
        Assert.Equal("127.0.0.2", fixture.Services.Profiles.Find("Test")!.Host);
        Assert.Equal(1, closed);
        Assert.Null(fixture.Controller.Settings);
    }

    [AvaloniaFact]
    public void The_window_is_not_modal_and_opens_once()
    {
        using var fixture = new StartFixture();
        LoginWindow login = fixture.ShowLogin();
        fixture.Controller.ShowListServerSettings(login);
        ListServerSettingsWindow settings = fixture.Controller.Settings!;
        fixture.Controller.ShowListServerSettings(null);
        Assert.Same(settings, fixture.Controller.Settings);
        Assert.True(login.IsEnabled);
        Assert.True(login.IsVisible);
        Assert.Same(login, settings.Owner);
    }

    [AvaloniaFact]
    public void The_theme_picker_changes_the_shared_options_and_the_script_theme()
    {
        using var fixture = new StartFixture("dark");
        ListServerSettingsWindow settings = Open(fixture);
        ComboBox themes = TestUi.Find<ComboBox>(settings, "ThemeCombo");
        Assert.Equal("Dark", ((ThemeChoice)themes.SelectedItem!).Text);
        Assert.Equal("System (OS theme)", settings.Themes[0].Text);
        Assert.Equal(13, settings.Themes.Count);

        themes.SelectedItem = settings.Themes.Single(t => t.Id == "nord");
        TestUi.Settle();
        Assert.Equal("nord", fixture.Services.Options.Current.Theme);
        Assert.True(fixture.Services.Options.Current.DarkMode);
        Assert.Equal("nord", fixture.Services.Theme.ThemeId);
        if (fixture.Services.Options.Current.SyncSyntaxTheme) Assert.Equal("nord", fixture.Services.Options.Current.SyntaxTheme);

        themes.SelectedItem = settings.Themes.Single(t => t.Id == "dark");
        TestUi.Settle();
        if (fixture.Services.Options.Current.SyncSyntaxTheme) Assert.Equal("language-spec", fixture.Services.Options.Current.SyntaxTheme);
        themes.SelectedItem = settings.Themes.Single(t => t.Id == "light");
        TestUi.Settle();
        Assert.False(fixture.Services.Options.Current.DarkMode);
        Shots.Save(settings, "rc-settings-light");

        fixture.Services.Theme.ChooseTheme("gruvbox");
        TestUi.Settle();
        Assert.Equal("gruvbox", ((ThemeChoice)themes.SelectedItem!).Id);
    }
}
