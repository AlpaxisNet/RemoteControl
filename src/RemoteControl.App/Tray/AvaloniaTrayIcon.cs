using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using RemoteControl.Core;
using RemoteControl.Ui.Common;

namespace RemoteControl.App.Tray;

/// <summary>
/// The real tray icon: Avalonia's <see cref="TrayIcon"/> with a <see cref="NativeMenu"/> (the Win32 notification area on Windows, a
/// StatusNotifierItem on the session bus on Linux, the status bar on macOS). Never made in tests.
/// </summary>
internal sealed class AvaloniaTrayIcon : ITrayIcon
{
    private readonly Application _application;
    private readonly TrayIcon _icon;
    private readonly WindowIcon? _normal;
    private readonly WindowIcon? _alert;
    private readonly Func<bool> _shown;
    private readonly IDisposable? _companion;
    private bool _alertShown;
    private bool _disposed;

    /// <summary>Creates the icon and shows it.</summary>
    /// <param name="application">The application.</param>
    /// <param name="paths">Where the pictures are (<c>images/rcicon.png</c>, <c>images/pmicon_tray.png</c>).</param>
    /// <param name="log">Where a missing picture is logged.</param>
    /// <param name="shown">Whether the desktop really shows the icon (see <see cref="TrayAvailability"/>).</param>
    /// <param name="companion">Disposed with the icon (the availability check), or null.</param>
    public AvaloniaTrayIcon(Application application, AppPaths paths, AppLog log, Func<bool> shown, IDisposable? companion = null)
    {
        _companion = companion;
        _application = application ?? throw new ArgumentNullException(nameof(application));
        ArgumentNullException.ThrowIfNull(paths);
        _shown = shown ?? throw new ArgumentNullException(nameof(shown));
        _normal = Load(paths.ResolveImage("rcicon.png"), log);
        _alert = Load(paths.ResolveImage("pmicon_tray.png"), log) ?? _normal;
        _icon = new TrayIcon { Icon = _normal, ToolTipText = "Remote Control", IsVisible = true, Menu = new NativeMenu() };
        _icon.Clicked += (_, _) => Raise();
        TrayIcon.SetIcons(_application, new TrayIcons { _icon });
    }

    public bool IsShown => !_disposed && _shown();

    public event EventHandler? Clicked;

    public void SetToolTip(string text)
    {
        if (!_disposed) _icon.ToolTipText = text;
    }

    public void SetAlert(bool alert)
    {
        if (_disposed || alert == _alertShown) return;
        _alertShown = alert;
        _icon.Icon = alert ? _alert : _normal;
    }

    public void SetMenu(IReadOnlyList<TrayMenuEntry> entries, Action<TrayMenuEntry> invoke)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(invoke);
        if (_disposed) return;
        _icon.Menu = Build(entries, invoke);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _icon.IsVisible = false;
            TrayIcon.SetIcons(_application, new TrayIcons());
            _icon.Dispose();
            _companion?.Dispose();
        }
        catch (Exception exception)
        {
            UiSafe.Log.Warn("The tray icon could not be removed.", exception);
        }
    }

    private static NativeMenu Build(IReadOnlyList<TrayMenuEntry> entries, Action<TrayMenuEntry> invoke)
    {
        var menu = new NativeMenu();
        foreach (TrayMenuEntry entry in entries)
        {
            if (entry.Command == TrayCommand.Separator)
            {
                menu.Items.Add(new NativeMenuItemSeparator());
                continue;
            }
            var item = new NativeMenuItem { Header = entry.Label };
            if (entry.Command == TrayCommand.Submenu) item.Menu = Build(entry.Items, invoke);
            else
            {
                if (entry.Checked is { } isChecked)
                {
                    item.ToggleType = entry.Command == TrayCommand.ShowSession ? NativeMenuItemToggleType.Radio : NativeMenuItemToggleType.CheckBox;
                    item.IsChecked = isChecked;
                }
                TrayMenuEntry chosen = entry;
                item.Click += (_, _) => OnUiThread(() => invoke(chosen));
            }
            menu.Items.Add(item);
        }
        return menu;
    }

    private void Raise() => OnUiThread(() => Clicked?.Invoke(this, EventArgs.Empty));

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) UiSafe.Run(action, "tray");
        else UiDispatch.Post(action, "tray");
    }

    private static WindowIcon? Load(string path, AppLog log)
    {
        try
        {
            return File.Exists(path) ? new WindowIcon(path) : null;
        }
        catch (Exception exception)
        {
            log.Warn("The tray picture " + path + " could not be read.", exception);
            return null;
        }
    }
}
