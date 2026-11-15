using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Session;

/// <summary>
/// The debug information of a session (F1, <c>showDebugInfoPopup</c>, <c>buildDebugInfoString</c>, review section 3.9): server,
/// session, memory and client sections in INI form, Refresh (F1), Copy to Clipboard, Close (Esc). One per session.
/// </summary>
internal sealed partial class DebugInfoWindow : RcWindow
{
    private readonly SessionContext _context;
    private readonly TextEditor _text;

    /// <summary>For the XAML previewer only.</summary>
    public DebugInfoWindow() : this(null!)
    {
    }

    /// <summary>Creates the window.</summary>
    /// <param name="context">The session.</param>
    public DebugInfoWindow(SessionContext context) : base(context?.App)
    {
        InitializeComponent();
        _context = context!;
        _text = IniView.Create("DebugText", string.Empty);
        TextHost.Child = _text;
        if (context is null) return;
        Title = string.IsNullOrEmpty(context.ServerName) ? "Debug Information" : "Debug Information - " + context.ServerName;
        RefreshButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Refresh(true), "DebugInfo.Refresh");
        CopyButton.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => CopyAsync(), "DebugInfo.Copy");
        CloseButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Close(), "DebugInfo.Close");
        AddHandler(KeyDownEvent, UiSafe.Handler<KeyEventArgs>((_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
            else if (e.Key == Key.F1)
            {
                e.Handled = true;
                Refresh(true);
            }
        }, "DebugInfo.Key"), RoutingStrategies.Tunnel);
        ActivityHook.Attach(this, context);
        Refresh(false);
    }

    /// <summary>The text shown (tests).</summary>
    internal string Text => _text.Text;

    /// <summary>The status line (tests).</summary>
    internal string Status => StatusLabel.Text ?? string.Empty;

    /// <summary>Builds the text again.</summary>
    /// <param name="announce">Whether the status line says so.</param>
    internal void Refresh(bool announce)
    {
        _text.Text = Build(_context);
        if (announce) StatusLabel.Text = "Debug stats refreshed.";
    }

    private async Task CopyAsync()
    {
        string text = Build(_context);
        if (await ClipboardHelper.SetTextAsync(this, text).ConfigureAwait(true)) StatusLabel.Text = "Debug information copied to clipboard!";
    }

    /// <summary>The text of the debug information.</summary>
    /// <param name="context">The session.</param>
    /// <returns>The INI text.</returns>
    internal static string Build(SessionContext context)
    {
        IRcSession session = context.Session;
        IMainWindowInfo? main = context.Services.Get<IMainWindowInfo>();
        RcOptions options = context.App.Options.Current;
        string ip = context.Login.Server.Host;
        int port = context.Login.Server.Port;
        string mainStatus = !session.IsConnected ? "Disconnected" : session.IsAuthenticated ? "Connected (Authenticated)" : "Connected (Authenticating...)";
        string ncStatus = session.IsNcConnected
            ? session.IsNcAuthenticated ? "Connected (Authenticated)" : "Connected (Authenticating...)"
            : main?.Controller.IsNcConnecting == true ? "Connecting..." : "Disconnected";
        var builder = new StringBuilder();
        void Line(string text) => builder.Append(text).Append('\n');
        Line("; ===================================================");
        Line("; Remote Control Debug Information");
        Line("; ===================================================");
        Line(string.Empty);
        Line("[Server]");
        Line("Server Name = " + (string.IsNullOrEmpty(context.ServerName) ? "(None)" : context.ServerName));
        Line("IP = " + (string.IsNullOrEmpty(ip) ? "Unknown" : ip));
        Line("Port = " + (port > 0 ? port.ToString(CultureInfo.InvariantCulture) : "Unknown"));
        if (!string.IsNullOrEmpty(ip) && port > 0) Line("Address = " + ip + ":" + port.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(context.Login.ListServer)) Line("List Server = " + context.Login.ListServer);
        Line("Main Connection = " + mainStatus);
        Line("NPC Server = " + ncStatus);
        if (!string.IsNullOrEmpty(session.NcServerAddress)) Line("NPC Server Address = " + session.NcServerAddress);
        Line("Protocol = " + (session.UseNewBanProtocol ? "New Protocol (v2)" : "Classic Protocol"));
        Line(string.Empty);
        Line("[Session]");
        Line("Account = " + (string.IsNullOrEmpty(context.Account) ? "(None)" : context.Account));
        Line("Nickname = " + (string.IsNullOrEmpty(context.Nickname) ? "(None)" : context.Nickname));
        Line("Activity Status = " + (main?.Controller.IsAway == true ? "Away" : "Active"));
        Line("Players Online = " + session.Players.Count.ToString(CultureInfo.InvariantCulture));
        Line("Session Uptime = " + (main is null ? "0s" : SessionText.Uptime(main.Controller.Now - main.Controller.ConnectedAt)));
        Line(string.Empty);
        Line("[Memory Usage]");
        try
        {
            using Process process = Process.GetCurrentProcess();
            Line("Working Set (RAM) = " + Megabytes(process.WorkingSet64));
            Line("Peak Working Set = " + Megabytes(process.PeakWorkingSet64));
            Line("Private Commit = " + Megabytes(process.PrivateMemorySize64));
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            Line("Status = Unavailable on this platform");
        }
        Line("Managed Heap = " + Megabytes(GC.GetTotalMemory(false)));
        Line(string.Empty);
        Line("[Client & Environment]");
        Line("RC Version = " + BuildInfo.Version);
        Line("Build Date = " + BuildInfo.Date);
        Line("Avalonia Version = " + (typeof(Window).Assembly.GetName().Version?.ToString() ?? "unknown"));
        Line(".NET Version = " + Environment.Version);
        Line("Operating System = " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        if (main is not null)
        {
            Window window = main.MainWindow;
            Line("Window Size = " + ((int)window.ClientSize.Width).ToString(CultureInfo.InvariantCulture) + "x" + ((int)window.ClientSize.Height).ToString(CultureInfo.InvariantCulture)
                + (window.WindowState == WindowState.Maximized ? " (Maximized)" : " (Normal)"));
            Line("Active Tabs = " + main.TabCount.ToString(CultureInfo.InvariantCulture));
            Line("Joined IRC Channels = " + main.JoinedChannelCount.ToString(CultureInfo.InvariantCulture));
        }
        Line("Open Sessions = " + context.Shell.Sessions.Count.ToString(CultureInfo.InvariantCulture));
        Line("Theme = " + options.Theme + (options.DarkMode ? " (Dark Mode)" : " (Light Mode)"));
        return builder.ToString();
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
}
