using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RemoteControl.Core;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Start;

/// <summary>
/// The accounts manager (<c>openAccountManager</c>, review section 11): "Select an account to sign in", the saved accounts (bold name,
/// dim list-server badge), Add Account, Edit (also double click), Delete (after a confirmation; the C++ deleted at once), Close and
/// Select (puts the account into the login window). Not modal (the C++ ran a nested loop, D6). When the saved accounts could not be
/// read the store's message is shown with "Start fresh…", which moves the old files aside only after the user confirmed.
/// </summary>
internal sealed partial class AccountsWindow : RcWindow
{
    private readonly StartController _controller;
    private readonly AppServices _services;
    private AccountEditWindow? _editor;

    /// <summary>For the XAML previewer only.</summary>
    public AccountsWindow() : this(null!, null!)
    {
    }

    /// <summary>Creates the window.</summary>
    /// <param name="controller">The controller.</param>
    /// <param name="services">The program's services.</param>
    public AccountsWindow(StartController controller, AppServices services) : base(services)
    {
        InitializeComponent();
        _controller = controller;
        _services = services;
        AccountsList.ItemsSource = Rows;
        if (services is null) return;

        AddButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => OpenEditor(-1), "Accounts.Add");
        EditButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => EditSelected(), "Accounts.Edit");
        DeleteButton.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => DeleteSelectedAsync(), "Accounts.Delete");
        CloseButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Close(), "Accounts.Close");
        SelectButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => SelectSelected(), "Accounts.Select");
        StartFreshButton.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => StartFreshAsync(), "Accounts.StartFresh");
        AccountsList.DoubleTapped += UiSafe.Handler<TappedEventArgs>((_, _) => EditSelected(), "Accounts.DoubleClick");
        AccountsList.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => UpdateButtons(), "Accounts.Selection");
        Listen(h => services.Accounts.Changed += h, h => services.Accounts.Changed -= h, (_, _) => Refresh(), "Accounts.Changed");
        Listen(h => services.Profiles.Changed += h, h => services.Profiles.Changed -= h, (_, _) => Refresh(), "Accounts.ProfilesChanged");
        OnClosedOnce(() => _editor?.Close());
        Refresh();
    }

    /// <summary>The rows shown.</summary>
    internal ObservableCollection<AccountRow> Rows { get; } = new();

    /// <summary>The open Add/Edit dialog, or null.</summary>
    internal AccountEditWindow? Editor => _editor is { IsClosed: false } editor ? editor : null;

    /// <summary>Selects the row of a store index (tests, after a save).</summary>
    /// <param name="index">The store index.</param>
    internal void SelectRow(int index) => AccountsList.SelectedIndex = index >= 0 && index < Rows.Count ? index : -1;

    /// <summary>Reads the accounts again, keeping the selection by id.</summary>
    internal void Refresh()
    {
        ulong selectedId = AccountsList.SelectedItem is AccountRow selected ? selected.Id : 0;
        List<AccountRow> rows = AccountRow.From(_services.Accounts, _services.Profiles.Profiles);
        Rows.Clear();
        foreach (AccountRow row in rows) Rows.Add(row);
        int index = selectedId == 0 ? -1 : rows.FindIndex(row => row.Id == selectedId);
        if (index < 0 && rows.Count > 0)
        {
            string active = _services.Accounts.ActiveName;
            index = Math.Max(0, rows.FindIndex(row => string.Equals(row.Name, active, StringComparison.OrdinalIgnoreCase)));
        }
        AccountsList.SelectedIndex = index;
        EmptyText.IsVisible = rows.Count == 0;

        AccountLoadResult load = _services.Accounts.LastLoad;
        bool attention = load.NeedsAttention && !_services.Accounts.CanSave;
        AttentionPanel.IsVisible = attention;
        AttentionText.Text = attention ? load.Message ?? "The saved accounts could not be read; they are kept as they are." : string.Empty;
        UpdateButtons();
    }

    /// <summary>Opens the Add (index -1) or Edit dialog.</summary>
    /// <param name="index">The store index to edit, or -1 to add.</param>
    /// <returns>The dialog.</returns>
    internal AccountEditWindow OpenEditor(int index)
    {
        if (Editor is { } open) open.Close();
        var editor = new AccountEditWindow(_controller, _services, index, saved =>
        {
            Refresh();
            SelectRow(saved);
        });
        _editor = editor;
        editor.ShowAndActivate(this);
        return editor;
    }

    /// <summary>Deletes the selected account after asking (the C++ asked nothing).</summary>
    /// <returns>Completes when done.</returns>
    internal async Task DeleteSelectedAsync()
    {
        if (AccountsList.SelectedItem is not AccountRow row) return;
        bool confirmed = await Dialogs.ConfirmAsync("Delete Account", $"Delete the saved account \"{row.Name}\" and its saved password?", this, "Delete", "Cancel");
        if (!confirmed || IsClosed) return;
        int index = _services.Accounts.IndexOf(row.Id);
        if (index < 0) return;
        AccountSaveStatus status = _services.Accounts.RemoveAt(index);
        if (status != AccountSaveStatus.Saved) await ShowSaveProblemAsync(status);
        Refresh();
    }

    private void EditSelected()
    {
        if (AccountsList.SelectedItem is AccountRow row) OpenEditor(row.Index);
    }

    private void SelectSelected()
    {
        if (AccountsList.SelectedItem is not AccountRow row) return;
        _controller.SelectAccountInLogin(row.Index);
        Close();
    }

    private async Task StartFreshAsync()
    {
        string message = "Start with no saved accounts? The account files that could not be read are kept beside the new ones "
            + "(renamed to \"<name>.old-<date>\"); nothing is deleted.";
        if (!await Dialogs.ConfirmAsync("Saved Accounts", message, this, "Start fresh", "Cancel") || IsClosed) return;
        AccountStartFreshResult result = _services.Accounts.StartFresh();
        if (result.Success)
        {
            string moved = result.MovedFiles.Count == 0 ? "No file had to be moved." : "The old files are kept as:\n" + string.Join("\n", result.MovedFiles);
            await Dialogs.ShowMessageAsync("Saved Accounts", moved, this);
        }
        else await Dialogs.ShowErrorAsync("Saved Accounts", result.Error ?? "The old account files could not be moved aside.", this);
        if (!IsClosed) Refresh();
    }

    private Task ShowSaveProblemAsync(AccountSaveStatus status) =>
        Dialogs.ShowErrorAsync("Saved Accounts", StartController.SaveProblem(_services.Accounts, status), this);

    private void UpdateButtons()
    {
        bool selected = AccountsList.SelectedItem is AccountRow;
        EditButton.IsEnabled = selected;
        DeleteButton.IsEnabled = selected;
        SelectButton.IsEnabled = selected;
    }
}
