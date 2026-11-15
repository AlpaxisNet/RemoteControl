using Avalonia.Interactivity;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Files;

/// <summary>Click handlers that go through <see cref="UiSafe"/>.</summary>
internal static class On
{
    /// <summary>A click handler that runs an action.</summary>
    /// <param name="action">The action.</param>
    /// <param name="context">What it does.</param>
    /// <returns>The handler.</returns>
    public static EventHandler<RoutedEventArgs> Click(Action action, string context) => (_, _) => UiSafe.Run(action, context);

    /// <summary>A click handler that starts asynchronous work.</summary>
    /// <param name="action">The work.</param>
    /// <param name="context">What it does.</param>
    /// <returns>The handler.</returns>
    public static EventHandler<RoutedEventArgs> ClickAsync(Func<Task> action, string context) => (_, _) => UiSafe.Fire(action, context);
}
