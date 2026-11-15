using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Text;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Players.Tests;

public sealed class EditDialogTests
{
    private static PlayerAttributes SampleAttributes() => new()
    {
        Account = "Graal7",
        World = "main",
        Level = "castle.nw",
        X = 30.5,
        Y = 12,
        Hearts = 3,
        MaxPower = 5,
        Rupees = 120,
        Arrows = 10,
        Bombs = 4,
        GlovePower = 1,
        SwordPower = 2,
        SwordImage = "sword2.png",
        ShieldPower = 1,
        ShieldImage = "shield1.png",
        HeadImage = "head3.png",
        BodyImage = "body.png",
        Animation = "idle",
        Colors = new[] { 2, 0, 10, 4, 18 },
        Status = 4 | 16 | 2,
        Kills = 7,
        Deaths = 2,
        OnlineSeconds = 3600,
        Rating = 1500,
        RatingDeviation = 350,
        LastIp = "1.2.3.4",
        ApCounter = 50,
        MagicPoints = 10,
        Chests = { "10:20:chest.nw" },
        Weapons = { "Bow", "Bomb" },
        Flags = { "quest.done=1" },
    };

    [AvaloniaFact]
    public async Task Rights_open_from_the_reply_round_trip_and_apply()
    {
        using var fixture = new Fixture();
        var rights = new PlayerRights((1 << 13) | (1 << 2) | (1 << 19) | (1 << 25), "1.2.*", "rw levels/*.nw\nr images/*");
        fixture.Fake.RaisePlayerRights("Graal7", rights);
        RightsWindow window = fixture.Latest<RightsWindow>();
        Assert.Equal("Edit Rights of Graal7", window.Title);
        Assert.Equal(rights, window.Current);
        Assert.Equal(rights.Rights, PlayerRightsText.Parse(window.EditorText, rights.Rights).Rights);
        Assert.True(window.CheckOf(13).IsChecked);
        Shots.Save(window, "rights");

        window.CheckOf(4).IsChecked = true;
        Assert.True(await window.ApplyAsync());
        PlayerRights sent = fixture.Fake.LastCall("SetPlayerRightsAsync")!.Argument<PlayerRights>(1);
        Assert.Equal("Graal7", fixture.Fake.LastCall("SetPlayerRightsAsync")!.Argument<string>(0));
        Assert.Equal(rights.Rights | (1 << 4), sent.Rights);
        Assert.Equal("1.2.*", sent.IpRange);
        Assert.Equal(rights.Folders, sent.Folders);
        Assert.True(window.IsClosed);
    }

    [AvaloniaFact]
    public void The_rights_dialog_has_a_check_box_for_every_bit_and_the_presets()
    {
        using var fixture = new Fixture();
        fixture.Fake.RaisePlayerRights("Graal7", new PlayerRights(0, string.Empty, string.Empty));
        RightsWindow window = fixture.Latest<RightsWindow>();
        Assert.Equal(Enumerable.Range(0, 20), RightsWindow.Layout.Select(item => item.Bit).OrderBy(bit => bit));
        for (int bit = 0; bit < 20; bit++)
        {
            window.SetBits(0);
            window.CheckOf(bit).IsChecked = true;
            Assert.Equal(1 << bit, window.Current.Rights);
        }
        Fixture.Click(TestUi.Find<Button>(window, "Preset2"));
        Assert.Equal(0b1110_1111, window.Current.Rights);
        Fixture.Click(TestUi.Find<Button>(window, "Preset4"));
        Assert.Equal((1 << 20) - 1, window.Current.Rights);
        Fixture.Click(TestUi.Find<Button>(window, "ClearAllButton"));
        Assert.Equal(0, window.Current.Rights);
    }

    [AvaloniaFact]
    public async Task Any_number_of_dialogs_each_bound_to_its_account()
    {
        using var fixture = new Fixture();
        fixture.Fake.RaisePlayerRights("One", new PlayerRights(1, string.Empty, string.Empty));
        fixture.Fake.RaisePlayerRights("Two", new PlayerRights(2, string.Empty, string.Empty));
        RightsWindow[] windows = fixture.Context.Windows.All.OfType<RightsWindow>().ToArray();
        Assert.Equal(new[] { "One", "Two" }, windows.Select(window => window.Account));
        await windows[0].ApplyAsync();
        Assert.Equal("One", fixture.Fake.LastCall("SetPlayerRightsAsync")!.Argument<string>(0));
        Assert.False(windows[1].IsClosed);
    }

    [AvaloniaFact]
    public async Task Apply_after_a_reconnect_uses_the_new_connection()
    {
        using var fixture = new Fixture();
        fixture.Fake.RaisePlayerComments("Graal7", "old comment");
        CommentsWindow window = fixture.Latest<CommentsWindow>();
        var second = new FakeRcSession("tester");
        fixture.Context.Replace(second, fixture.Test.Login);
        window.Comments = "new comment";
        Assert.True(await window.SaveAsync());
        Assert.False(fixture.Fake.WasCalled("SetPlayerCommentsAsync"));
        RecordedCall call = second.LastCall("SetPlayerCommentsAsync")!;
        Assert.Equal(("Graal7", "new comment"), (call.Argument<string>(0), call.Argument<string>(1)));
        Assert.False(window.IsClosed);
    }

    [AvaloniaFact]
    public async Task Apply_after_a_disconnect_shows_a_message_and_does_not_throw()
    {
        using var fixture = new Fixture();
        fixture.Fake.RaisePlayerRights("Graal7", new PlayerRights(1, string.Empty, string.Empty));
        RightsWindow window = fixture.Latest<RightsWindow>();
        fixture.Fake.RaiseDisconnected("Connection lost");
        Task<bool> apply = window.ApplyAsync();
        RcDialog dialog = TestUi.FindDialog();
        Assert.Equal(PlayersSession.NotConnectedTitle, dialog.Title);
        dialog.Accept();
        Assert.False(await apply);
        Assert.False(window.IsClosed);
        Assert.False(fixture.Fake.WasCalled("SetPlayerRightsAsync"));
    }

    [AvaloniaFact]
    public async Task A_refused_send_shows_the_session_error()
    {
        using var fixture = new Fixture();
        fixture.Fake.RaisePlayerComments("Graal7", "text");
        CommentsWindow window = fixture.Latest<CommentsWindow>();
        fixture.Fake.SendResult = false;
        fixture.Fake.LastError = "The text is too long for its field.";
        Task<bool> save = window.SaveAsync();
        RcDialog dialog = TestUi.FindDialog();
        Assert.Equal("The text is too long for its field.", dialog.Message);
        dialog.Accept();
        Assert.False(await save);
    }

    [AvaloniaFact]
    public async Task Attributes_open_from_the_reply_round_trip_and_apply_with_backups()
    {
        using var fixture = new Fixture();
        PlayerAttributes original = SampleAttributes();
        fixture.Fake.RaisePlayerAttributes(original);
        AttributesWindow window = fixture.Latest<AttributesWindow>();
        Assert.Equal("Edit Attributes of Graal7", window.Title);
        Assert.Equal("castle.nw", ((TextBox)window.Field("Level")).Text);
        Assert.Equal("Orange", ((ComboBox)window.Field("Skin Color")).Text);
        Assert.True(TestUi.Find<CheckBox>(window, "MaleCheck").IsChecked);
        Assert.Equal("Bow\nBomb\n", TestUi.Find<TextBox>(window, "WeaponsBox").Text);
        Assert.Equal(new[] { "chest.nw" }, TestUi.Find<ListBox>(window, "ChestList").ItemsSource!.Cast<string>());

        PlayerAttributes current = window.Current;
        Assert.Equal(PlayerAttributesText.Format(original), PlayerAttributesText.Format(current));
        Assert.Equal("main", current.World);
        foreach (int tab in Enumerable.Range(0, 6))
        {
            TestUi.Find<TabControl>(window, "Tabs").SelectedIndex = tab;
            Shots.Save(window, "attributes-" + tab);
        }

        ((TextBox)window.Field("Gralats")).Text = "999";
        Assert.True(await window.ApplyAsync());
        RecordedCall call = fixture.Fake.LastCall("SetPlayerAttributesAsync")!;
        PlayerAttributes sent = call.Argument<PlayerAttributes>(1);
        Assert.Equal("Graal7", call.Argument<string>(0));
        Assert.Equal(999, sent.Rupees);
        Assert.Equal(original.Status, sent.Status);
        Assert.Equal(new[] { "10:20:chest.nw" }, sent.Chests);
        Assert.True(window.IsClosed);

        var backup = new RemoteControl.Core.EditorBackup(fixture.Test.Services.Paths.BackupDirectory, fixture.Context.ServerName);
        await TestUi.WaitUntilAsync(() => File.Exists(backup.PathFor("attributes", "Graal7", modified: true)!) && File.Exists(backup.PathFor("attributes", "Graal7", modified: false)!));
    }

    [AvaloniaFact]
    public async Task Profile_opens_from_the_reply_and_applies()
    {
        using var fixture = new Fixture();
        string[] fields = { "Bob Smith", "21", "male", "Germany", "icq", "bob@example.invalid", "www.example.invalid", "the bar", "carpe diem", "3600", "castle.nw", "7", "2", "5", "1500", "60", "120", "2", "1" };
        fixture.Fake.RaiseProfile("Graal7", fields);
        ProfileWindow window = fixture.Latest<ProfileWindow>();
        Assert.Equal("Profile of Graal7", window.Title);
        Assert.Equal("Graal7: Bob Smith", TestUi.Find<TextBlock>(window, "HeaderText").Text);
        Assert.Equal(fields.Take(10), window.Current);
        Assert.Equal("castle.nw", TestUi.Find<TextBox>(window, "LevelBox").Text);
        Shots.Save(window, "profile");
        TestUi.Find<TextBox>(window, "Profile3").Text = "France";
        Assert.True(await window.ApplyAsync());
        IReadOnlyList<string> sent = fixture.Fake.LastCall("SetProfileAsync")!.Argument<IReadOnlyList<string>>(1);
        Assert.Equal("France", sent[3]);
        Assert.Equal("carpe diem", sent[8]);
        Assert.True(window.IsClosed);
    }

    [AvaloniaFact]
    public async Task Comments_open_from_the_reply_and_save()
    {
        using var fixture = new Fixture();
        fixture.Fake.RaisePlayerComments("Graal7", "line 1\nline 2");
        CommentsWindow window = fixture.Latest<CommentsWindow>();
        Assert.Equal("Edit Comments of Graal7", window.Title);
        Assert.Equal("line 1\nline 2", window.Comments);
        Shots.Save(window, "comments");
        Assert.True(await window.SaveAsync());
        Assert.Equal("line 1\nline 2", fixture.Fake.LastCall("SetPlayerCommentsAsync")!.Argument<string>(1));
    }

    [AvaloniaFact]
    public void Ban_history_and_staff_activity_windows()
    {
        using var fixture = new Fixture();
        fixture.Fake.RaiseBanList("banhistory", "Graal7", "2026-01-02 10:11:12 by Admin:\nbanned for cheating\n2026-02-03 01:02:03 by Admin:\nunbanned");
        TextViewerWindow history = fixture.Latest<TextViewerWindow>();
        Assert.Equal("Ban History of Graal7", history.Title);
        Assert.Equal("2026-01-02 10:11:12 by Admin:\nbanned for cheating\n\n2026-02-03 01:02:03 by Admin:\nunbanned\n", history.Text);
        Shots.Save(history, "ban-history");
        fixture.Fake.RaiseBanList("staffactivity", "Admin", string.Empty);
        TextViewerWindow activity = fixture.Latest<TextViewerWindow>();
        Assert.Equal("Staff Activity of Admin", activity.Title);
        Assert.Equal("(none)", activity.Text);
    }
}
