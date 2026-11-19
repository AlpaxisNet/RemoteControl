using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Players.Tests;

/// <summary>Menus that only offer what reaches the player: a player of another server, a player who left.</summary>
public sealed class MenuTargetTests
{
    private static string[] Headers(IEnumerable<Control> menu) => menu.Select(item => item is MenuItem menuItem ? (string)menuItem.Header! : "-").ToArray();

    [AvaloniaFact]
    public void A_player_of_another_server_gets_the_servers_menu_and_is_no_admin()
    {
        using var fixture = new Fixture();
        fixture.Fake.PlayerList.Add(Fixture.Player(20, "Remote", "Far", level: string.Empty, category: PlayerListCategory.External));
        fixture.Fake.PlayerList.Add(Fixture.Player(21, "Staff", "Here", level: string.Empty));
        PlayerListWindow window = fixture.OpenList();

        PlayerRow remote = window.RowOf(20)!;
        Assert.True(remote.IsExternal);
        Assert.False(remote.InAdmins);
        Assert.True(window.RowOf(21)!.InAdmins);
        Assert.Equal(["Private Message", "History", "Ban History", "Staff Activity", "-", "Copy Account"], Headers(window.MenuFor(remote)));
    }

    [AvaloniaFact]
    public async Task A_player_who_left_gets_no_action_that_goes_by_the_old_id()
    {
        using var fixture = new Fixture();
        PlayerListWindow window = fixture.OpenList();
        fixture.Fake.RaisePlayerJoined(Fixture.Player(12, "Graal12", "Bob"));
        fixture.Fake.RaisePrivateMessage(12, "Graal12", "Bob", "hey", PrivateMessageKind.Normal);
        fixture.Fake.RaisePlayerLeft(Fixture.Player(12, "Graal12"));
        PlayerRow offline = window.RowOf(12)!;
        Assert.True(offline.IsOffline);

        List<Control> menu = window.MenuFor(offline);
        Assert.Equal(
            ["Private Message", "History", "Ban History", "Staff Activity", "Profile", "-", "Edit Attributes", "Edit Rights", "Edit Comments", "Edit Access",
             "Reset", "-", "Edit Account", "-", "Copy Account", "Copy Nickname"],
            Headers(menu));

        fixture.Fake.ClearCalls();
        Fixture.Click(Fixture.Item(menu, "Edit Attributes"));
        await TestUi.WaitUntilAsync(() => fixture.Fake.WasCalled("RequestPlayerAttributesAsync"));
        Assert.Equal("Graal12", fixture.Fake.LastCall("RequestPlayerAttributesAsync")!.Arguments[0]);

        Fixture.Click(Fixture.Item(menu, "Edit Access"));
        await TestUi.WaitUntilAsync(() => fixture.Fake.WasCalled("RequestNewPlayerBanAsync"));
        Assert.Equal(new object?[] { "Graal12", -1 }, fixture.Fake.LastCall("RequestNewPlayerBanAsync")!.Arguments);
    }

    [AvaloniaFact]
    public void An_online_player_keeps_the_whole_menu()
    {
        using var fixture = new Fixture();
        fixture.Fake.PlayerList.Add(Fixture.Player(7, "Graal7", "Seven", "castle.nw"));
        PlayerListWindow window = fixture.OpenList();
        string[] headers = Headers(window.MenuFor(window.RowOf(7)!));
        Assert.Contains("Disconnect", headers);
        Assert.Contains("Warp", headers);
        Assert.Contains("Update Level", headers);
        Assert.Contains("Admin Message", headers);
    }
}
