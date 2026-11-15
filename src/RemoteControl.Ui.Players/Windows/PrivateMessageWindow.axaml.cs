using Avalonia.Input;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>
/// The conversation with one player (<c>openPrivateMessage</c>, <c>TPlayerList.cpp:1523-1595</c>), one per player id per session:
/// "PM: nick (account)" (plus " (on server)" for a player of another server), the conversation above (a <see cref="ChatView"/>,
/// at most 500 lines, HTML shown as safe text), the reply below (Enter sends, Ctrl+Enter starts a new line), History and Send.
/// </summary>
internal sealed partial class PrivateMessageWindow : RcWindow
{
    /// <summary>The most lines the conversation keeps (<c>maxPrivateMessageRows</c>).</summary>
    public const int MaximumLines = 500;

    private readonly PlayersSession? _owner;

    /// <summary>For the designer.</summary>
    public PrivateMessageWindow() : this(null, 1, "account", "nick", string.Empty)
    {
    }

    /// <summary>Creates the window.</summary>
    /// <param name="owner">The session.</param>
    /// <param name="playerId">The player.</param>
    /// <param name="account">The player's account.</param>
    /// <param name="nick">The player's nickname.</param>
    /// <param name="sourceServer">The other server the player is on, or empty.</param>
    public PrivateMessageWindow(PlayersSession? owner, int playerId, string account, string nick, string sourceServer) : base(owner?.App)
    {
        InitializeComponent();
        _owner = owner;
        PlayerId = playerId;
        Account = account;
        Nick = nick;
        string title = "PM: " + PmText.Identity(account, nick);
        string server = owner?.Context.ServerName ?? string.Empty;
        if (sourceServer.Length > 0 && !string.Equals(sourceServer, server, StringComparison.OrdinalIgnoreCase)) title += " (on " + sourceServer + ")";
        Title = title;
        Conversation.Services = owner?.App;
        Conversation.MaximumLines = MaximumLines;
        if (owner is not null) Conversation.LinkClicked += UiSafe.Handler<ChatLinkEventArgs>((_, e) => owner.App.Urls.Open(e.Url), "PM.Link");
        SendButton.Click += On.ClickAsync(SendAsync, "PM.Send");
        HistoryButton.Click += On.Click(() => _owner?.ShowHistory(Account, Nick), "PM.History");
        Reply.AddHandler(KeyDownEvent, OnReplyKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Activated += UiSafe.Handler((_, _) => _owner?.MarkRead(PlayerId), "PM.Activated");
    }

    /// <summary>The player.</summary>
    public int PlayerId { get; }

    /// <summary>The player's account.</summary>
    public string Account { get; }

    /// <summary>The player's nickname.</summary>
    public string Nick { get; }

    /// <summary>The conversation (for tests).</summary>
    internal ChatView ConversationView => Conversation;

    /// <summary>The reply text.</summary>
    public string ReplyText
    {
        get => Reply.Text ?? string.Empty;
        set => Reply.Text = value;
    }

    /// <summary>Shows a message the player sent.</summary>
    /// <param name="message">The message as received.</param>
    public void AddIncoming(string message) => AddLine(Nick.Length > 0 ? Nick : Account, message);

    /// <summary>Puts the focus into the reply.</summary>
    public void FocusReply() => UiSafe.Run(() => Reply.Focus(), "PM.Focus");

    /// <summary>Sends the reply with the connection in use now; on success it is written to the history, shown and cleared.</summary>
    /// <returns>Whether it was sent.</returns>
    public async Task<bool> SendAsync()
    {
        if (_owner is null || IsClosed) return false;
        string text = ReplyText;
        if (text.Length == 0) return false;
        bool sent = await _owner.SendAsync(this, session => session.SendPrivateMessageAsync(PlayerId, text)).ConfigureAwait(true);
        if (!sent || IsClosed) return false;
        string me = _owner.Context.Account.Length > 0 ? _owner.Context.Account : "You";
        _ = _owner.History.AppendAsync(Account, me, text);
        AddLine(me, text);
        if (ReplyText == text) ReplyText = string.Empty;
        FocusReply();
        return true;
    }

    private void AddLine(string sender, string message)
    {
        var options = _owner?.Options;
        string text = PmText.ToDisplay(PmText.StripLabel(message), options?.NoHtmlInPms ?? false, options?.NoHtmlImages ?? false);
        Conversation.Append(sender + ": " + text, new ChatAppendOptions { SuppressUrgency = true });
    }

    private void OnReplyKeyDown(object? sender, KeyEventArgs e)
    {
        UiSafe.Run(() =>
        {
            if (e.Key is not (Key.Enter or Key.Return)) return;
            e.Handled = true;
            if ((e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                int caret = Math.Clamp(Reply.CaretIndex, 0, ReplyText.Length);
                ReplyText = ReplyText.Insert(caret, "\n");
                Reply.CaretIndex = caret + 1;
                return;
            }
            UiSafe.Fire(SendAsync, "PM.Enter");
        }, "PM.Key");
    }
}
