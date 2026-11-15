using System.Collections.ObjectModel;
using Avalonia.Interactivity;
using RemoteControl.Core;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Start;

/// <summary>
/// The Add/Edit Account dialog (<c>editAccount</c>, review section 11): account name, password, "Don't save password", "Direct Mode",
/// the list-server profile with the "Manage saved list servers" pencil, Close and Save. A new account starts with the profile selected
/// at that time (<see cref="StartController.CurrentProfile"/>, Alpaxis by default). The profile combo follows changes of the profiles
/// at once; the list-server settings it opens are not modal (D16).
/// </summary>
internal sealed partial class AccountEditWindow : RcWindow
{
    private readonly StartController _controller;
    private readonly AppServices _services;
    private readonly int _index;
    private readonly Action<int> _saved;
    private string _selectedValue = string.Empty;
    private bool _updating;

    /// <summary>For the XAML previewer only.</summary>
    public AccountEditWindow() : this(null!, null!, -1, _ => { })
    {
    }

    /// <summary>Creates the dialog.</summary>
    /// <param name="controller">The controller.</param>
    /// <param name="services">The program's services.</param>
    /// <param name="index">The store index of the account to edit, or -1 to add one.</param>
    /// <param name="saved">Called with the account's index after it was saved.</param>
    public AccountEditWindow(StartController controller, AppServices services, int index, Action<int> saved) : base(services)
    {
        InitializeComponent();
        _controller = controller;
        _services = services;
        _index = index;
        _saved = saved;
        ListServerCombo.ItemsSource = Choices;
        if (services is null) return;

        IReadOnlyList<SavedAccount> accounts = services.Accounts.Accounts;
        SavedAccount? account = index >= 0 && index < accounts.Count ? accounts[index] : null;
        _index = account is null ? -1 : index;
        Title = account is null ? "Add Account" : "Edit Account";
        if (account is not null)
        {
            NameField.Text = account.Name;
            string password = services.Accounts.GetPassword(index);
            PasswordField.Text = password;
            DontSavePasswordCheck.IsChecked = password.Length == 0;
            DirectModeCheck.IsChecked = account.DirectMode;
            _selectedValue = account.ListServer;
        }
        else _selectedValue = controller.CurrentProfile.Association;
        LoadChoices();

        SaveButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Save(), "AccountEdit.Save");
        CloseButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Close(), "AccountEdit.Close");
        ManageListServersButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => _controller.ShowListServerSettings(this), "AccountEdit.ManageListServers");
        ListServerCombo.SelectionChanged += UiSafe.Handler<Avalonia.Controls.SelectionChangedEventArgs>((_, _) =>
        {
            if (!_updating && ListServerCombo.SelectedItem is ProfileChoice choice) _selectedValue = choice.Value;
        }, "AccountEdit.Picked");
        Listen(h => services.Profiles.Changed += h, h => services.Profiles.Changed -= h, (_, _) => LoadChoices(), "AccountEdit.ProfilesChanged");
        Listen(h => controller.ListServerSettingsClosed += h, h => controller.ListServerSettingsClosed -= h, (_, _) => LoadChoices(), "AccountEdit.SettingsClosed");
        Opened += (_, _) => NameField.Focus();
    }

    /// <summary>The entries of the list-server combo.</summary>
    internal ObservableCollection<ProfileChoice> Choices { get; } = new();

    /// <summary>The association that will be saved.</summary>
    internal string SelectedValue => _selectedValue;

    /// <summary>The store index edited, or -1 when adding.</summary>
    internal int Index => _index;

    /// <summary>Fills the dialog (tests and the MCP path do not type).</summary>
    /// <param name="name">The account name.</param>
    /// <param name="password">The password.</param>
    /// <param name="dontSavePassword">"Don't save password".</param>
    /// <param name="directMode">"Direct Mode".</param>
    /// <param name="listServer">A profile name or association, or null to keep the selection.</param>
    internal void Fill(string name, string password, bool dontSavePassword, bool directMode, string? listServer = null)
    {
        NameField.Text = name;
        PasswordField.Text = password;
        DontSavePasswordCheck.IsChecked = dontSavePassword;
        DirectModeCheck.IsChecked = directMode;
        if (listServer is null) return;
        ListServerProfile? profile = _services.Profiles.Find(listServer);
        _selectedValue = profile?.Association ?? listServer;
        LoadChoices();
    }

    /// <summary>Saves the account (the Save button); closes the dialog when it was saved.</summary>
    /// <returns>Whether it was saved.</returns>
    internal bool Save()
    {
        string name = (NameField.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            ShowStatus("Enter the account name.");
            NameField.Focus();
            return false;
        }
        bool direct = DirectModeCheck.IsChecked == true;
        bool dontSave = DontSavePasswordCheck.IsChecked == true;
        string listServer = _selectedValue;
        if (direct && !ListServerAssociation.TryParseDirect(ListServerAssociation.ToDirect(listServer), _services.Profiles.Profiles, out _, out _))
        {
            ShowStatus("Direct Mode needs a list-server profile whose host and port are the RC server's.");
            return false;
        }
        string password = PasswordField.Text ?? string.Empty;
        AccountSaveStatus status = _index >= 0
            ? _services.Accounts.UpdateAt(_index, name, password, dontSave, listServer, direct)
            : _services.Accounts.Update(string.Empty, name, password, dontSave, listServer, direct);
        if (status != AccountSaveStatus.Saved)
        {
            ShowStatus(StartController.SaveProblem(_services.Accounts, status));
            return false;
        }
        int savedIndex = _index >= 0 ? _index : _services.Accounts.ActiveIndex;
        Close();
        _saved(savedIndex);
        return true;
    }

    /// <summary>The message shown under the fields, or empty.</summary>
    internal string Status => StatusText.IsVisible ? StatusText.Text ?? string.Empty : string.Empty;

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = text.Length > 0;
    }

    private void LoadChoices()
    {
        IReadOnlyList<ListServerProfile> profiles = _services.Profiles.Profiles;
        _updating = true;
        try
        {
            Choices.Clear();
            foreach (ListServerProfile profile in profiles) Choices.Add(new ProfileChoice(profile.Name, profile.Association, profile));
            string normalized = string.IsNullOrEmpty(_selectedValue) ? string.Empty : ListServerAssociation.Normalize(_selectedValue, profiles);
            if (normalized.Length > 0) _selectedValue = normalized;
            int selected = -1;
            for (int index = 0; index < Choices.Count; index++)
                if (string.Equals(Choices[index].Value, _selectedValue, StringComparison.OrdinalIgnoreCase)) selected = index;
            if (selected < 0 && _selectedValue.Length > 0)
            {
                Choices.Add(new ProfileChoice(_selectedValue, _selectedValue, null));
                selected = Choices.Count - 1;
            }
            ListServerCombo.SelectedIndex = selected;
        }
        finally
        {
            _updating = false;
        }
    }
}
