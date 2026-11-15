using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>
/// "Edit Comments of &lt;acc&gt;" (<c>handlePlayerText</c>, type <c>comments</c>): the text, Close and Save. Save sends with the
/// connection in use then, keeps a backup, and leaves the window open (as the C++ did).
/// </summary>
internal sealed partial class CommentsWindow : RcWindow
{
    private readonly PlayersSession? _owner;

    /// <summary>For the designer.</summary>
    public CommentsWindow() : this(null, "account", string.Empty)
    {
    }

    /// <summary>Creates the dialog for the comments the server sent.</summary>
    /// <param name="owner">The session.</param>
    /// <param name="account">The account.</param>
    /// <param name="comments">The comments.</param>
    public CommentsWindow(PlayersSession? owner, string account, string comments) : base(owner?.App)
    {
        InitializeComponent();
        _owner = owner;
        Account = account;
        Title = "Edit Comments of " + account;
        CommentsBox.Text = comments ?? string.Empty;
        SaveButton.Click += On.ClickAsync(SaveAsync, "Comments.Save");
        CloseButton.Click += On.Click(Close, "Comments.Close");
    }

    /// <summary>The account the dialog was opened for.</summary>
    public string Account { get; }

    /// <summary>The comments shown now.</summary>
    public string Comments
    {
        get => (CommentsBox.Text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        set => CommentsBox.Text = value;
    }

    /// <summary>Sends the comments; the window stays open.</summary>
    /// <returns>Whether they were sent.</returns>
    public async Task<bool> SaveAsync()
    {
        if (_owner is null || IsClosed) return false;
        string comments = Comments;
        bool sent = await _owner.SendAsync(this, session => session.SetPlayerCommentsAsync(Account, comments)).ConfigureAwait(true);
        if (sent)
        {
            _owner.Backup("comments", Account, comments, modified: true);
            StatusText.Text = "Saved.";
        }
        return sent;
    }
}
