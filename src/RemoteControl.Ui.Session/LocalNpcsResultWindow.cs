using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using AvaloniaEdit;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Session;

/// <summary>The answer to a level-NPC dump (<c>onLocalNPCData</c>): 520 by 380, the text read-only with INI highlighting, Close. One per answer.</summary>
internal sealed class LocalNpcsResultWindow : RcWindow
{
    /// <summary>Creates the window.</summary>
    /// <param name="context">The session.</param>
    /// <param name="level">The level asked for.</param>
    /// <param name="text">The NPCs.</param>
    public LocalNpcsResultWindow(SessionContext context, string level, string text) : base(context.App)
    {
        Title = string.IsNullOrEmpty(context.ServerName) ? "Local NPCs" : "Local NPCs - " + context.ServerName;
        Width = 520;
        Height = 380;
        Level = level;
        this.Bind(BackgroundProperty, this.GetResourceObservable(ThemeKeys.BackgroundBrush));
        Editor = IniView.Create("LocalNpcsText", text);
        var close = new Button { Name = "CloseButton", Content = "Close", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Close(), "LocalNpcsResult.Close");
        var buttons = new Border { Padding = new Thickness(6), Child = close };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var frame = new Border { BorderThickness = new Thickness(1), Child = Editor };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable(ThemeKeys.BorderBrush));
        Content = new DockPanel { Children = { buttons, frame } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        ActivityHook.Attach(this, context);
    }

    /// <summary>The level of the answer.</summary>
    public string Level { get; }

    /// <summary>The text view.</summary>
    public TextEditor Editor { get; }
}
