using Avalonia.Input;
using Avalonia.Interactivity;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Session;

/// <summary>
/// The level-NPC dump dialog (<c>onLocalNPCDump</c>, <c>onLocalNPCSubmit</c>, review section 3.8): asks for a level and requests its
/// local NPCs (lower case). It is not modal (defect D6) and asks the connection of the moment, never one that is gone (D5); the answer
/// is shown by the main window in a <see cref="LocalNpcsResultWindow"/>.
/// </summary>
internal sealed partial class LocalNpcsDialog : RcWindow
{
    private readonly SessionContext _context;

    /// <summary>For the XAML previewer only.</summary>
    public LocalNpcsDialog() : this(null!)
    {
    }

    /// <summary>Creates the dialog.</summary>
    /// <param name="context">The session.</param>
    public LocalNpcsDialog(SessionContext context) : base(context?.App)
    {
        InitializeComponent();
        _context = context!;
        if (context is null) return;
        Title = string.IsNullOrEmpty(context.ServerName) ? "Local NPCs" : "Local NPCs - " + context.ServerName;
        OkButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Submit(), "LocalNpcs.Ok");
        CancelButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Close(), "LocalNpcs.Cancel");
        AddHandler(KeyDownEvent, UiSafe.Handler<KeyEventArgs>((_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Submit();
            }
        }, "LocalNpcs.Key"), RoutingStrategies.Tunnel);
        ActivityHook.Attach(this, context);
        Opened += (_, _) => LevelBox.Focus();
    }

    /// <summary>OK: requests the NPCs of the level (nothing for an empty level) and closes.</summary>
    internal void Submit()
    {
        string level = (LevelBox.Text ?? string.Empty).Trim().ToLowerInvariant();
        IRcSession session = _context.Session;
        if (level.Length > 0 && session.IsConnected) UiSafe.Fire(() => session.RequestLocalNpcsAsync(level), "LocalNpcs.Request");
        Close();
    }
}
