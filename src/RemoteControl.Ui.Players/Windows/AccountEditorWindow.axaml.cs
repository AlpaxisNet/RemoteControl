using System.Globalization;
using RemoteControl.Protocol.Text;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>
/// The account editor (<c>TAccountsWindow::openEditor</c>): "Edit account of &lt;acc&gt; - &lt;server&gt;" for an account the server
/// sent, "Add new account - &lt;server&gt;" for a new one; one window per account, any number at once. Apply sends
/// <see cref="Protocol.Session.IRcSession.AddAccountAsync"/> (new) or <see cref="Protocol.Session.IRcSession.SetAccountAsync"/> with
/// the connection in use then; the window stays open. Closing only hides it.
/// </summary>
internal sealed partial class AccountEditorWindow : RcWindow
{
    private readonly PlayersSession? _owner;

    /// <summary>For the designer.</summary>
    public AccountEditorWindow() : this(null, null)
    {
    }

    /// <summary>Creates the editor.</summary>
    /// <param name="owner">The session.</param>
    /// <param name="account">The account the server sent, or null for a new account.</param>
    public AccountEditorWindow(PlayersSession? owner, AccountData? account) : base(owner?.App)
    {
        InitializeComponent();
        _owner = owner;
        HideOnClose = true;
        ApplyButton.Click += On.ClickAsync(ApplyAsync, "AccountEditor.Apply");
        CloseButton.Click += On.Click(HideAndRemember, "AccountEditor.Close");
        Load(account);
    }

    /// <summary>The account the editor was loaded with, or null for a new account.</summary>
    public AccountData? Data { get; private set; }

    /// <summary>The account being edited; empty for a new account.</summary>
    public string EditingAccount { get; private set; } = string.Empty;

    /// <summary>Whether this adds a new account.</summary>
    public bool IsNew => EditingAccount.Length == 0;

    /// <summary>Fills the fields (<c>openEditor</c>): defaults admin level 0 and admin worlds <c>all</c>.</summary>
    /// <param name="account">The account, or null for a new one.</param>
    public void Load(AccountData? account)
    {
        Data = account;
        EditingAccount = account?.Account ?? string.Empty;
        string server = _owner?.Context.ServerName ?? string.Empty;
        string title = IsNew ? "Add new account" : "Edit account of " + EditingAccount;
        Title = server.Length == 0 ? title : title + " - " + server;
        NameBox.Text = account?.Account ?? string.Empty;
        PasswordBox.Text = account?.Password ?? string.Empty;
        EmailBox.Text = account?.Email ?? string.Empty;
        LevelBox.Text = (account?.AdminLevel ?? 0).ToString(CultureInfo.InvariantCulture);
        WorldsBox.Text = string.IsNullOrEmpty(account?.AdminWorlds) ? "all" : account.AdminWorlds;
        BannedCheck.IsChecked = account?.Banned ?? false;
        GuestCheck.IsChecked = account?.Guest ?? false;
        BanTimeBox.Text = account?.BanLength ?? string.Empty;
        ReasonBox.Text = account?.BanReason ?? string.Empty;
        StatusText.Text = string.Empty;
        if (IsVisible) ShowAndActivate();
    }

    /// <summary>The account as the fields show it now.</summary>
    public AccountData Current => new()
    {
        Account = (NameBox.Text ?? string.Empty).Trim(),
        Password = PasswordBox.Text ?? string.Empty,
        Email = (EmailBox.Text ?? string.Empty).Trim(),
        AdminLevel = int.TryParse((LevelBox.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int level) ? Math.Clamp(level, 0, 223) : 0,
        AdminWorlds = (WorldsBox.Text ?? string.Empty).Trim(),
        Banned = BannedCheck.IsChecked == true,
        Guest = GuestCheck.IsChecked == true,
        BanLength = BanTimeBox.Text ?? string.Empty,
        BanReason = (ReasonBox.Text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal),
    };

    /// <summary>The account editor text of the current fields (<see cref="AccountText.Format"/>).</summary>
    public string EditorText => AccountText.Format(Current);

    /// <summary>Adds or changes the account.</summary>
    /// <returns>Whether it was sent.</returns>
    public async Task<bool> ApplyAsync()
    {
        if (_owner is null || IsClosed) return false;
        AccountData data = Current;
        string editing = EditingAccount;
        bool sent = await _owner.SendAsync(this, session => editing.Length == 0 ? session.AddAccountAsync(data) : session.SetAccountAsync(editing, data)).ConfigureAwait(true);
        if (sent) StatusText.Text = editing.Length == 0 ? "Added." : "Saved.";
        return sent;
    }
}
