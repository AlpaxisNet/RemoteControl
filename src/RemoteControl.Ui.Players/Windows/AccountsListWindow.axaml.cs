using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>
/// "Accounts List - &lt;server&gt;" (<c>TAccountsWindow</c>), one per session: the accounts of the last account list (type to search),
/// Get Accounts, Add and Close. Double click or Enter asks for the account; the context menu has the account actions. Closing only
/// hides it.
/// </summary>
internal sealed partial class AccountsListWindow : RcWindow
{
    private readonly PlayersSession? _owner;

    /// <summary>For the designer.</summary>
    public AccountsListWindow() : this(null)
    {
    }

    /// <summary>Creates the window.</summary>
    /// <param name="owner">The session.</param>
    public AccountsListWindow(PlayersSession? owner) : base(owner?.App)
    {
        InitializeComponent();
        _owner = owner;
        HideOnClose = true;
        Title = "Accounts List" + (owner is null || owner.Context.ServerName.Length == 0 ? string.Empty : " - " + owner.Context.ServerName);
        AccountList.ItemsSource = Accounts;
        Track(ListSearch.Attach(AccountList));
        GetAccountsButton.Click += On.Click(() => _owner?.ShowGetAccounts(), "Accounts.Get");
        AddButton.Click += On.Click(() => _owner?.ShowNewAccount(), "Accounts.Add");
        CloseButton.Click += On.Click(HideAndRemember, "Accounts.Close");
        AccountList.DoubleTapped += UiSafe.Handler<TappedEventArgs>((_, e) => UiSafe.Fire(() => RequestAccountAsync(AccountAt(e.Source)), "Accounts.Open"), "Accounts.DoubleTapped");
        AccountList.KeyDown += UiSafe.Handler<KeyEventArgs>((_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return) || AccountList.SelectedItem is not string account) return;
            e.Handled = true;
            UiSafe.Fire(() => RequestAccountAsync(account), "Accounts.Enter");
        }, "Accounts.Keys");
        AccountList.ContextRequested += UiSafe.Handler<ContextRequestedEventArgs>((_, e) =>
        {
            string? account = AccountAt(e.Source) ?? AccountList.SelectedItem as string;
            if (account is null || _owner is null) return;
            AccountList.SelectedItem = account;
            var menu = new ContextMenu { ItemsSource = _owner.AccountMenu(this, account) };
            menu.Open(AccountList);
            e.Handled = true;
        }, "Accounts.Menu");
    }

    /// <summary>The accounts shown.</summary>
    public ObservableCollection<string> Accounts { get; } = new();

    /// <summary>Shows the accounts of an account list (and the window).</summary>
    /// <param name="accounts">The accounts.</param>
    public void SetAccounts(IReadOnlyList<string> accounts)
    {
        Accounts.Clear();
        foreach (string account in accounts)
            if (!string.IsNullOrEmpty(account)) Accounts.Add(account);
        ShowAndActivate();
    }

    /// <summary>Asks the server for an account (it opens the account editor).</summary>
    /// <param name="account">The account.</param>
    /// <returns>Whether it was sent.</returns>
    public Task<bool> RequestAccountAsync(string? account)
    {
        if (_owner is null || string.IsNullOrEmpty(account)) return Task.FromResult(false);
        return _owner.SendAsync(this, session => session.RequestAccountAsync(account));
    }

    private static string? AccountAt(object? source) =>
        (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as string;
}
