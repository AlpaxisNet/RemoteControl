using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Players.Tests;

/// <summary>A session with the players group registered and started.</summary>
internal sealed class Fixture : IDisposable
{
    public Fixture(string? theme = null, Func<RcOptions, RcOptions>? options = null)
    {
        Test = new TestSession(theme, options: options);
        PlayersFeature.Register(Test.Tools);
        PlayersFeature.Start(Test.Context);
        Players = PlayersSession.For(Test.Context)!;
    }

    public TestSession Test { get; }

    public PlayersSession Players { get; }

    public FakeRcSession Fake => Test.Fake;

    public SessionContext Context => Test.Context;

    public static RcPlayer Player(int id, string account, string nick = "", string level = "onlinestartlocal.nw", int status = -1, PlayerListCategory category = PlayerListCategory.None) =>
        new(id, account, nick, level, category, status, string.Empty, new Dictionary<int, string>());

    public PlayerListWindow OpenList()
    {
        var window = (PlayerListWindow)Context.OpenTool(ToolIds.PlayerList)!;
        TestUi.Settle();
        return window;
    }

    public T Latest<T>() where T : Window
    {
        T? window = null;
        TestUi.WaitUntil(() => (window = Context.Windows.All.OfType<T>().LastOrDefault(item => item.IsVisible)) is not null, what: typeof(T).Name);
        return window!;
    }

    public static TextInputWindow Prompt()
    {
        TextInputWindow? window = null;
        TestUi.WaitUntil(() => (window = RcWindow.OpenWindows.OfType<TextInputWindow>().LastOrDefault(item => item.IsVisible)) is not null, what: "a text prompt");
        return window!;
    }

    /// <summary>Puts the keyboard focus into a list (on its first row: the list itself takes no focus).</summary>
    public static void FocusList(ListBox list)
    {
        TestUi.Settle();
        Control? first = list.ContainerFromIndex(0);
        (first ?? list).Focus();
        TestUi.Settle();
    }

    public static void Click(Control item) => item.RaiseEvent(new RoutedEventArgs(item is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));

    public static MenuItem Item(IEnumerable<Control> menu, string header) =>
        menu.OfType<MenuItem>().Single(item => (string?)item.Header == header);

    public void Dispose() => Test.Dispose();
}
