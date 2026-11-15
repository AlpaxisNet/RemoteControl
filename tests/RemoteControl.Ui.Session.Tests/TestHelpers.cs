using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Session.Tests;

/// <summary>What the tests share.</summary>
internal static class TestHelpers
{
    /// <summary>A controllable clock.</summary>
    public sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan time) => Now += time;
    }

    /// <summary>A player for the fake's list.</summary>
    public static RcPlayer Player(int id, string account, string nick = "", string community = "") =>
        new(id, account, nick, "onlinestartlocal.nw", PlayerListCategory.None, -1, community, ImmutableDictionary<int, string>.Empty);

    /// <summary>Opens a main window of the test session at the C++ size.</summary>
    public static MainWindow ShowMain(TestSession test, Func<DateTimeOffset>? clock = null, double width = 500, double height = 350) =>
        test.Show(new MainWindow(test.Context, clock, startTimer: false), width, height);

    /// <summary>Clicks a menu item.</summary>
    public static void ClickMenu(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        TestUi.Settle();
    }

    /// <summary>Types a line into the chat entry and presses Enter.</summary>
    public static void Enter(MainWindow window, string text)
    {
        TextBox entry = TestUi.Find<TextBox>(window, "ChatEntry");
        entry.Focus();
        entry.Text = text;
        entry.CaretIndex = text.Length;
        TestUi.Settle();
        TestUi.PressKey(window, Avalonia.Input.Key.Enter);
    }

    /// <summary>The raw packets of an id the fake sent.</summary>
    public static IReadOnlyList<byte[]> Raw(FakeRcSession fake, int packetId) =>
        fake.CallsTo("SendRawPacketAsync").Where(call => call.Argument<int>(0) == packetId).Select(call => call.Argument<byte[]>(1)).ToArray();

    /// <summary>The RC chat lines the fake sent.</summary>
    public static IReadOnlyList<string> Sent(FakeRcSession fake) => fake.CallsTo("SendRcChatAsync").Select(call => call.Argument<string>(0)).ToArray();

    /// <summary>The visible dialogs.</summary>
    public static IReadOnlyList<RcDialog> Dialogs() => RcWindow.OpenWindows.OfType<RcDialog>().Where(dialog => dialog.IsVisible).ToArray();
}
