using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteControl.Core;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Start;

/// <summary>
/// The login window (<c>TStartFrame</c>, review section 11), one per program: nickname, the saved accounts with their list-server
/// badge and the "Manage accounts" pencil, the password, "Don't save password", "Graphical Menu", the list-server settings gear,
/// OK and Close. It holds the login form; what OK does is <see cref="StartController.SubmitFromWindowAsync"/>. Closing it (Close or
/// the window manager) only hides it and tells the controller, which tells the program.
/// </summary>
internal sealed partial class LoginWindow : RcWindow
{
    private readonly StartController _controller;
    private readonly AppServices _services;
    private bool _updating;
    private bool _allowClose;
    private int _selectedIndex = -1;

    /// <summary>For the XAML previewer only.</summary>
    public LoginWindow() : this(null!, null!)
    {
    }

    /// <summary>Creates the window.</summary>
    /// <param name="controller">The controller.</param>
    /// <param name="services">The program's services.</param>
    public LoginWindow(StartController controller, AppServices services) : base(services)
    {
        InitializeComponent();
        _controller = controller;
        _services = services;
        AccountList.ItemsSource = Rows;
        if (services is null) return;

        RcOptions options = services.Options.Current;
        NicknameField.Text = options.Nickname;
        DontSavePasswordCheck.IsChecked = options.DontSavePassword;
        GraphicalMenuCheck.IsChecked = options.GraphicalMenu;

        OkButton.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => _controller.SubmitFromWindowAsync(), "Login.OK");
        CloseButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => CloseByUser(), "Login.Close");
        ListServerSettingsButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => _controller.ShowListServerSettings(this), "Login.ListServerSettings");
        ManageAccountsButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => _controller.ShowAccountsManager(), "Login.ManageAccounts");
        AccountField.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) UiSafe.Run(OnAccountTyped, "Login.AccountTyped");
        };
        AccountList.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => OnAccountPicked(), "Login.AccountPicked");
        Listen(h => services.Accounts.Changed += h, h => services.Accounts.Changed -= h, (_, _) => RefreshAccounts(), "Login.AccountsChanged");
        Listen(h => services.Profiles.Changed += h, h => services.Profiles.Changed -= h, (_, _) => RefreshAccounts(), "Login.ProfilesChanged");

        RefreshAccounts();
        int active = services.Accounts.ActiveIndex;
        if (active < 0 && Rows.Count > 0) active = 0;
        if (active >= 0) SelectAccount(active);
        Opened += (_, _) => (string.IsNullOrEmpty(AccountField.Text) ? AccountField : PasswordField).Focus();
    }

    /// <summary>The rows of the saved accounts (drop-down).</summary>
    internal ObservableCollection<AccountRow> Rows { get; } = new();

    /// <summary>The store index of the selected account, or -1 when the typed name is not a saved account.</summary>
    internal int SelectedIndex => _selectedIndex;

    /// <summary>The account field.</summary>
    internal string AccountText
    {
        get => AccountField.Text ?? string.Empty;
        set => AccountField.Text = value;
    }

    /// <summary>The nickname field.</summary>
    internal string NicknameText
    {
        get => NicknameField.Text ?? string.Empty;
        set => NicknameField.Text = value;
    }

    /// <summary>The password field.</summary>
    internal string PasswordText
    {
        get => PasswordField.Text ?? string.Empty;
        set => PasswordField.Text = value;
    }

    /// <summary>"Don't save password".</summary>
    internal bool DontSavePassword => DontSavePasswordCheck.IsChecked == true;

    /// <summary>"Graphical Menu".</summary>
    internal bool GraphicalMenu => GraphicalMenuCheck.IsChecked == true;

    /// <summary>Whether a login started from this window is running (OK is disabled meanwhile).</summary>
    internal bool IsBusy
    {
        get => !OkButton.IsEnabled;
        set => OkButton.IsEnabled = !value;
    }

    /// <summary>Lets <see cref="Window.Close()"/> close the window for good (the controller is disposed).</summary>
    internal void CloseForGood()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>Reads the saved accounts again, keeping the selected account (by id) and the typed text.</summary>
    internal void RefreshAccounts()
    {
        ulong selectedId = _selectedIndex >= 0 && _selectedIndex < Rows.Count ? Rows[_selectedIndex].Id : 0;
        List<AccountRow> rows = AccountRow.From(_services.Accounts, _services.Profiles.Profiles);
        _updating = true;
        try
        {
            SyncRows(rows);
            _selectedIndex = selectedId == 0 ? -1 : rows.FindIndex(row => row.Id == selectedId);
            if (_selectedIndex >= 0 && !string.Equals(rows[_selectedIndex].Name, AccountText, StringComparison.Ordinal)) _selectedIndex = -1;
            AccountList.SelectedIndex = _selectedIndex;
        }
        finally
        {
            _updating = false;
        }
        UpdateTitle();
    }

    /// <summary>
    /// Selects the saved account at <paramref name="index"/> (<c>selectAccount</c>): its name, its saved password, "Don't save password"
    /// when it has none, and the title with its list server.
    /// </summary>
    /// <param name="index">The store index.</param>
    internal void SelectAccount(int index)
    {
        if (index < 0 || index >= Rows.Count) return;
        _updating = true;
        try
        {
            _selectedIndex = index;
            AccountList.SelectedIndex = index;
            AccountField.Text = Rows[index].Name;
            string password = _services.Accounts.GetPassword(index);
            PasswordField.Text = password;
            DontSavePasswordCheck.IsChecked = password.Length == 0;
        }
        finally
        {
            _updating = false;
        }
        UpdateTitle();
    }

    /// <summary>Selects the first saved account named <paramref name="name"/>, or types the name as a new account (with no password).</summary>
    /// <param name="name">An account name.</param>
    internal void SelectAccount(string name)
    {
        int index = _services.Accounts.IndexOfName(name);
        if (index >= 0)
        {
            SelectAccount(index);
            return;
        }
        _updating = true;
        try
        {
            // As selectAccount did: a name that is no saved account has no saved password.
            _selectedIndex = -1;
            AccountList.SelectedIndex = -1;
            AccountField.Text = name;
            PasswordField.Text = string.Empty;
        }
        finally
        {
            _updating = false;
        }
        UpdateTitle();
    }

    /// <summary>Focuses the account field (OK with an empty account).</summary>
    internal void FocusAccount() => AccountField.Focus();

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _allowClose) return;
        e.Cancel = true;
        CloseByUser();
    }

    private void CloseByUser()
    {
        HideAndRemember();
        _controller.OnLoginClosedByUser();
    }

    private void OnAccountTyped()
    {
        if (_updating) return;
        string typed = AccountText;
        if (_selectedIndex >= 0 && _selectedIndex < Rows.Count && string.Equals(Rows[_selectedIndex].Name, typed, StringComparison.Ordinal)) return;
        if (_selectedIndex < 0) return;
        _selectedIndex = -1;
        _updating = true;
        try
        {
            AccountList.SelectedIndex = -1;
        }
        finally
        {
            _updating = false;
        }
        UpdateTitle();
    }

    private void OnAccountPicked()
    {
        if (_updating) return;
        int index = AccountList.SelectedIndex;
        if (index < 0) return;
        SelectAccount(index);
        AccountDropButton.Flyout?.Hide();
        PasswordField.Focus();
    }

    private void UpdateTitle()
    {
        IReadOnlyList<SavedAccount> accounts = _services.Accounts.Accounts;
        SavedAccount? selected = _selectedIndex >= 0 && _selectedIndex < accounts.Count ? accounts[_selectedIndex] : null;
        Title = AccountPresentation.Title(selected, _services.Profiles.Profiles);
        ToolTip.SetTip(AccountField, selected is null ? "Type an account name or choose a saved account" : AccountPresentation.Detail(selected, _services.Profiles.Profiles));
    }

    private void SyncRows(List<AccountRow> rows)
    {
        for (int index = 0; index < rows.Count; index++)
        {
            if (index < Rows.Count)
            {
                if (Rows[index] != rows[index]) Rows[index] = rows[index];
            }
            else Rows.Add(rows[index]);
        }
        while (Rows.Count > rows.Count) Rows.RemoveAt(Rows.Count - 1);
    }
}
