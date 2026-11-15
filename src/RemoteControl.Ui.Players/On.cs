using Avalonia.Interactivity;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>Click handlers that go through <see cref="UiSafe"/> (rule 4).</summary>
internal static class On
{
    /// <summary>A click handler that runs an action.</summary>
    public static EventHandler<RoutedEventArgs> Click(Action action, string context) => UiSafe.Handler<RoutedEventArgs>((_, _) => action(), context);

    /// <summary>A click handler that runs asynchronous work.</summary>
    public static EventHandler<RoutedEventArgs> ClickAsync(Func<Task> action, string context) => UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => action(), context);
}
