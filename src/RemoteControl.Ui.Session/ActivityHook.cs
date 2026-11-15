using Avalonia.Input;
using Avalonia.Interactivity;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Session;

/// <summary>
/// Tells the program about key and pointer input in one window (<see cref="IAppShell.NoteActivity"/>) so the away timer of every
/// session starts again. One hook per window, added once when the window is made and removed when it closes (defect D2: the C++
/// added handlers to every window every 50 ms and kept them on destroyed frames).
/// </summary>
internal static class ActivityHook
{
    /// <summary>The shortest time between two reports of pointer movement.</summary>
    private static readonly TimeSpan MoveInterval = TimeSpan.FromSeconds(1);

    /// <summary>Hooks a window.</summary>
    /// <param name="window">The window.</param>
    /// <param name="context">The session (its shell is told).</param>
    public static void Attach(RcWindow window, SessionContext context)
    {
        DateTime lastMove = DateTime.MinValue;
        void Note() => UiSafe.Run(context.Shell.NoteActivity, "note activity");
        EventHandler<KeyEventArgs> key = (_, _) => Note();
        EventHandler<PointerPressedEventArgs> press = (_, _) => Note();
        EventHandler<PointerWheelEventArgs> wheel = (_, _) => Note();
        EventHandler<PointerEventArgs> move = (_, _) =>
        {
            DateTime now = DateTime.UtcNow;
            if (now - lastMove < MoveInterval) return;
            lastMove = now;
            Note();
        };
        const RoutingStrategies tunnel = RoutingStrategies.Tunnel;
        window.AddHandler(InputElement.KeyDownEvent, key, tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerPressedEvent, press, tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerWheelChangedEvent, wheel, tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerMovedEvent, move, tunnel, handledEventsToo: true);
        window.OnClosedOnce(() =>
        {
            window.RemoveHandler(InputElement.KeyDownEvent, key);
            window.RemoveHandler(InputElement.PointerPressedEvent, press);
            window.RemoveHandler(InputElement.PointerWheelChangedEvent, wheel);
            window.RemoveHandler(InputElement.PointerMovedEvent, move);
        });
    }
}
