using Avalonia.Input;
using Avalonia.Interactivity;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Session;

/// <summary>
/// The toalls window of a session (<c>TToallsWindow</c>, review section 8): the to-all messages of the server, an entry that sends one
/// to everyone, Close (hides). A message sent is shown at once as <c>&lt;nickname&gt;: text</c> and the server's echo of it is not
/// shown a second time. It talks to the connection of the moment (defect D1: the C++ kept a deleted handle).
/// </summary>
internal sealed partial class ToallsWindow : RcWindow
{
    /// <summary>The most messages waiting for their echo (defect D10: the C++ list grew when the server did not echo).</summary>
    public const int PendingLimit = 50;

    private readonly SessionContext _context;
    private readonly List<string> _pending = [];

    /// <summary>For the XAML previewer only.</summary>
    public ToallsWindow() : this(null!)
    {
    }

    /// <summary>Creates the window.</summary>
    /// <param name="context">The session.</param>
    public ToallsWindow(SessionContext context) : base(context?.App)
    {
        InitializeComponent();
        _context = context!;
        if (context is null) return;
        HideOnClose = true;
        Title = string.IsNullOrEmpty(context.ServerName) ? "Toalls" : "Toalls - " + context.ServerName;
        ToallChat.Services = context.App;
        ToallChat.EmotesEnabled = false;
        ToallChat.LinkClicked += UiSafe.Handler<ChatLinkEventArgs>((_, e) => context.App.Urls.Open(e.Url), "Toalls.Link");
        ToallEntry.AddHandler(KeyDownEvent, UiSafe.Handler<KeyEventArgs>((_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SendEntry();
        }, "Toalls.Enter"), RoutingStrategies.Tunnel);
        CloseButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => HideAndRemember(), "Toalls.Close");
        Track(context.Attach(session =>
        {
            EventHandler<TextEventArgs> handler = (_, e) =>
            {
                if (!IsClosed && ReferenceEquals(session, context.Session)) UiSafe.Run(() => Append(e.Text), "Toalls.ToAll");
            };
            session.ToAllReceived += handler;
            return new Unsubscriber(() => session.ToAllReceived -= handler);
        }));
        ActivityHook.Attach(this, context);
        Opened += (_, _) => ToallEntry.Focus();
    }

    /// <summary>The messages shown (tests).</summary>
    internal ChatView Chat => ToallChat;

    /// <summary>Shows a to-all line unless it is the echo of one just sent.</summary>
    /// <param name="line">The line, <c>nick: text</c>.</param>
    internal void Append(string line)
    {
        string nickname = _context.Nickname;
        int pending = _pending.FindIndex(value => line == value || nickname.Length > 0 && line == nickname + ": " + value);
        if (pending >= 0)
        {
            _pending.RemoveAt(pending);
            return;
        }
        ToallChat.Append(line, new ChatAppendOptions { Timestamp = false, SuppressEmotes = true });
    }

    /// <summary>Sends the entry to everyone (Enter).</summary>
    internal void SendEntry()
    {
        string message = ToallEntry.Text ?? string.Empty;
        if (message.Length == 0) return;
        IRcSession session = _context.Session;
        if (!session.IsConnected) return;
        UiSafe.Fire(async () =>
        {
            bool sent = await session.SendToAllAsync(message).ConfigureAwait(true);
            if (!sent)
            {
                _context.App.Log.Warn("A to-all message was not sent: " + (session.LastError ?? "not connected"));
                return;
            }
            if (IsClosed) return;
            string nickname = _context.Nickname;
            ToallChat.Append((nickname.Length == 0 ? "You" : nickname) + ": " + message, new ChatAppendOptions { Timestamp = false, SuppressEmotes = true });
            _pending.Add(message);
            if (_pending.Count > PendingLimit) _pending.RemoveAt(0);
            if (ToallEntry.Text == message) ToallEntry.Text = string.Empty;
        }, "Toalls.Send");
    }
}
