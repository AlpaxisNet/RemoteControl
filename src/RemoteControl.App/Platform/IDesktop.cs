using Avalonia.Controls;

namespace RemoteControl.App.Platform;

/// <summary>
/// What the program asks of the desktop it runs on: its windows, whether one of them is active, the task-bar flash, the beep and
/// the end of the application. The real one is <see cref="AvaloniaDesktop"/>; tests use a recording fake (nothing on the desktop).
/// </summary>
internal interface IDesktop
{
    /// <summary>The program's open windows.</summary>
    IReadOnlyList<Window> Windows { get; }

    /// <summary>Whether one of the program's windows has the focus.</summary>
    bool AnyWindowActive { get; }

    /// <summary>Makes a window's task-bar entry flash until the window is activated (where the platform can).</summary>
    /// <param name="window">The window.</param>
    void Flash(Window window);

    /// <summary>Plays the alert sound.</summary>
    void Beep();

    /// <summary>Ends the application (after everything was released).</summary>
    void Shutdown();
}
