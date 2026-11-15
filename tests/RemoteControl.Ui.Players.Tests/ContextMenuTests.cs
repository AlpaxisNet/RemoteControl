using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Players.Tests;

public sealed class ContextMenuTests
{
    private static (Fixture Fixture, PlayerListWindow Window, List<Control> Menu) Open(Action<Fixture>? setup = null)
    {
        var fixture = new Fixture();
        setup?.Invoke(fixture);
        fixture.Fake.PlayerList.Add(Fixture.Player(7, "Graal7", "Seven", "castle.nw"));
        PlayerListWindow window = fixture.OpenList();
        List<Control> menu = window.MenuFor(window.RowOf(7)!);
        fixture.Fake.ClearCalls();
        return (fixture, window, menu);
    }

    [AvaloniaFact]
    public void The_menu_has_the_cpp_items_in_order()
    {
        (Fixture fixture, _, List<Control> menu) = Open();
        using (fixture)
        {
            Assert.Equal(new[]
            {
                "Private Message", "History", "Ban History", "Staff Activity", "Profile", "-", "Edit Attributes", "Edit Rights", "Edit Comments", "Edit Access",
                "Disconnect", "Reset", "-", "Edit Account", "Warp", "Update Level", "Admin Message", "-", "Copy Account", "Copy Nickname",
            }, menu.Select(item => item is MenuItem menuItem ? (string)menuItem.Header! : "-"));
        }
    }

    [AvaloniaTheory]
    [InlineData("Ban History", "RequestBanHistoryAsync", "Graal7")]
    [InlineData("Staff Activity", "RequestStaffActivityAsync", "Graal7")]
    [InlineData("Profile", "RequestProfileAsync", "Graal7")]
    [InlineData("Edit Attributes", "RequestPlayerAttributesAsync", 7)]
    [InlineData("Edit Rights", "RequestPlayerRightsAsync", "Graal7")]
    [InlineData("Edit Comments", "RequestPlayerCommentsAsync", "Graal7")]
    [InlineData("Edit Account", "RequestAccountAsync", "Graal7")]
    [InlineData("Update Level", "UpdateLevelAsync", "castle.nw")]
    public void A_request_item_sends_its_call(string header, string method, object argument)
    {
        (Fixture fixture, _, List<Control> menu) = Open();
        using (fixture)
        {
            Fixture.Click(Fixture.Item(menu, header));
            TestUi.WaitUntil(() => fixture.Fake.WasCalled(method), what: method);
            Assert.Equal(argument, fixture.Fake.LastCall(method)!.Arguments[0]);
            Assert.Single(fixture.Fake.Calls);
        }
    }

    [AvaloniaFact]
    public void The_menu_stays_bound_to_its_player_when_the_list_changes()
    {
        (Fixture fixture, PlayerListWindow window, List<Control> menu) = Open();
        using (fixture)
        {
            fixture.Fake.RaisePlayerLeft(Fixture.Player(7, "Graal7"));
            for (int id = 20; id < 40; id++) fixture.Fake.RaisePlayerJoined(Fixture.Player(id, "x" + id));
            window.PlayersList.SelectedItem = window.RowOf(25);
            Fixture.Click(Fixture.Item(menu, "Edit Rights"));
            Assert.Equal("Graal7", fixture.Fake.LastCall("RequestPlayerRightsAsync")!.Argument<string>(0));
        }
    }

    [AvaloniaFact]
    public void Edit_access_asks_for_the_ban_types_and_the_ban_with_the_new_type()
    {
        (Fixture fixture, _, List<Control> menu) = Open();
        using (fixture)
        {
            Fixture.Click(Fixture.Item(menu, "Edit Access"));
            TestUi.WaitUntil(() => fixture.Fake.WasCalled("RequestNewPlayerBanAsync"));
            Assert.True(fixture.Fake.WasCalled("RequestBanTypesAsync"));
            RecordedCall call = fixture.Fake.LastCall("RequestNewPlayerBanAsync")!;
            Assert.Equal(("Graal7", 7), (call.Argument<string>(0), call.Argument<int>(1)));
        }
    }

    [AvaloniaFact]
    public void Edit_access_uses_the_legacy_request_when_the_option_is_off()
    {
        (Fixture fixture, _, List<Control> menu) = Open(f => f.Test.Environment.SetOptions(o => o with { UseNewBanType = false }));
        using (fixture)
        {
            Fixture.Click(Fixture.Item(menu, "Edit Access"));
            TestUi.WaitUntil(() => fixture.Fake.WasCalled("RequestLegacyPlayerBanAsync"));
            Assert.False(fixture.Fake.WasCalled("RequestBanTypesAsync"));
            Assert.Equal("Graal7", fixture.Fake.LastCall("RequestLegacyPlayerBanAsync")!.Argument<string>(0));
        }
    }

    [AvaloniaFact]
    public void Disconnect_asks_for_a_reason_and_an_empty_reason_cancels()
    {
        (Fixture fixture, _, List<Control> menu) = Open();
        using (fixture)
        {
            Fixture.Click(Fixture.Item(menu, "Disconnect"));
            TextInputWindow prompt = Fixture.Prompt();
            Assert.Equal("Disconnect Player", prompt.Title);
            prompt.Accept();
            TestUi.Settle();
            Assert.False(fixture.Fake.WasCalled("DisconnectPlayerAsync"));

            Fixture.Click(Fixture.Item(menu, "Disconnect"));
            prompt = Fixture.Prompt();
            Shots.Save(prompt, "disconnect-prompt");
            prompt.Text = "spamming";
            prompt.Accept();
            TestUi.WaitUntil(() => fixture.Fake.WasCalled("DisconnectPlayerAsync"));
            RecordedCall call = fixture.Fake.LastCall("DisconnectPlayerAsync")!;
            Assert.Equal((7, "spamming"), (call.Argument<int>(0), call.Argument<string>(1)));
        }
    }

    [AvaloniaFact]
    public void Reset_asks_first()
    {
        (Fixture fixture, _, List<Control> menu) = Open();
        using (fixture)
        {
            Fixture.Click(Fixture.Item(menu, "Reset"));
            RcDialog dialog = TestUi.FindDialog();
            Assert.Equal("Do you really want to reset the attributes of Graal7 ?", dialog.Message);
            dialog.Cancel();
            TestUi.Settle();
            Assert.False(fixture.Fake.WasCalled("ResetPlayerAsync"));
            Fixture.Click(Fixture.Item(menu, "Reset"));
            TestUi.FindDialog().Accept();
            TestUi.WaitUntil(() => fixture.Fake.WasCalled("ResetPlayerAsync"));
            Assert.Equal("Graal7", fixture.Fake.LastCall("ResetPlayerAsync")!.Argument<string>(0));
        }
    }

    [AvaloniaFact]
    public void Admin_message_to_one_player()
    {
        (Fixture fixture, _, List<Control> menu) = Open();
        using (fixture)
        {
            Fixture.Click(Fixture.Item(menu, "Admin Message"));
            TextInputWindow prompt = Fixture.Prompt();
            Assert.Equal("Admin Message", prompt.Title);
            prompt.Text = "line one\nline two";
            prompt.Accept();
            TestUi.WaitUntil(() => fixture.Fake.WasCalled("SendAdminMessageAsync"));
            RecordedCall call = fixture.Fake.LastCall("SendAdminMessageAsync")!;
            Assert.Equal((7, "line one\nline two"), (call.Argument<int>(0), call.Argument<string>(1)));
        }
    }

    [AvaloniaFact]
    public async Task Warp_checks_the_fields_and_sends()
    {
        (Fixture fixture, _, List<Control> menu) = Open();
        using (fixture)
        {
            Fixture.Click(Fixture.Item(menu, "Warp"));
            WarpWindow warp = fixture.Latest<WarpWindow>();
            Assert.Equal("castle.nw", TestUi.Find<TextBox>(warp, "LevelBox").Text);
            Shots.Save(warp, "warp");
            TestUi.Find<TextBox>(warp, "XBox").Text = "abc";
            Assert.False(await warp.WarpAsync());
            Assert.Equal("X and Y must be finite numbers.", warp.Error);
            TestUi.Find<TextBox>(warp, "LevelBox").Text = "";
            Assert.False(await warp.WarpAsync());
            Assert.Equal("Enter a destination level.", warp.Error);
            TestUi.Find<TextBox>(warp, "LevelBox").Text = "start.nw";
            TestUi.Find<TextBox>(warp, "XBox").Text = "30.5";
            TestUi.Find<TextBox>(warp, "YBox").Text = "12";
            fixture.Fake.SendResult = false;
            fixture.Fake.LastError = "Text too long.";
            Assert.False(await warp.WarpAsync());
            Assert.Equal("Text too long.", warp.Error);
            fixture.Fake.SendResult = true;
            Assert.True(await warp.WarpAsync());
            RecordedCall call = fixture.Fake.LastCall("WarpPlayerAsync")!;
            Assert.Equal((7, "start.nw", 30.5, 12.0), (call.Argument<int>(0), call.Argument<string>(1), call.Argument<double>(2), call.Argument<double>(3)));
            Assert.True(warp.IsClosed);
        }
    }

    [AvaloniaFact]
    public void Private_message_and_history_open_their_windows()
    {
        (Fixture fixture, _, List<Control> menu) = Open();
        using (fixture)
        {
            Fixture.Click(Fixture.Item(menu, "Private Message"));
            Assert.Equal("PM: Seven (Graal7)", fixture.Latest<PrivateMessageWindow>().Title);
            Fixture.Click(Fixture.Item(menu, "History"));
            Assert.Equal("History: Graal7 - Seven", fixture.Latest<TextViewerWindow>().Title);
        }
    }

    [AvaloniaFact]
    public void Not_connected_shows_a_message_and_sends_nothing()
    {
        (Fixture fixture, _, List<Control> menu) = Open();
        using (fixture)
        {
            fixture.Fake.RaiseDisconnected("Connection lost");
            fixture.Fake.ClearCalls();
            Fixture.Click(Fixture.Item(menu, "Edit Rights"));
            RcDialog dialog = TestUi.FindDialog();
            Assert.Equal(PlayersSession.NotConnectedText, dialog.Message);
            dialog.Accept();
            Assert.Empty(fixture.Fake.Calls);
        }
    }
}
