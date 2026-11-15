using Avalonia.Input;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>
/// "Get Accounts List - &lt;server&gt;": the account name specification and the conditions; Get List asks for the account list with
/// the connection in use then (the answer fills the accounts list). Closing only hides it.
/// </summary>
internal sealed partial class GetAccountsWindow : RcWindow
{
    private readonly PlayersSession? _owner;

    /// <summary>For the designer.</summary>
    public GetAccountsWindow() : this(null)
    {
    }

    /// <summary>Creates the window.</summary>
    /// <param name="owner">The session.</param>
    public GetAccountsWindow(PlayersSession? owner) : base(owner?.App)
    {
        InitializeComponent();
        _owner = owner;
        HideOnClose = true;
        Title = "Get Accounts List" + (owner is null || owner.Context.ServerName.Length == 0 ? string.Empty : " - " + owner.Context.ServerName);
        GetListButton.Click += On.ClickAsync(GetListAsync, "GetAccounts.Get");
        CloseButton.Click += On.Click(HideAndRemember, "GetAccounts.Close");
        KeyDown += UiSafe.Handler<KeyEventArgs>((_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                e.Handled = true;
                UiSafe.Fire(GetListAsync, "GetAccounts.Enter");
            }
        }, "GetAccounts.Keys");
    }

    /// <summary>The account name specification.</summary>
    public string Specification
    {
        get => SpecBox.Text ?? string.Empty;
        set => SpecBox.Text = value;
    }

    /// <summary>The conditions.</summary>
    public string Conditions
    {
        get => ConditionsBox.Text ?? string.Empty;
        set => ConditionsBox.Text = value;
    }

    /// <summary>Puts the focus into the specification.</summary>
    public void FocusSpecification() => UiSafe.Run(() => SpecBox.Focus(), "GetAccounts.Focus");

    /// <summary>Asks for the account list.</summary>
    /// <returns>Whether it was sent.</returns>
    public Task<bool> GetListAsync()
    {
        if (_owner is null) return Task.FromResult(false);
        string specification = Specification, conditions = Conditions;
        return _owner.SendAsync(this, session => session.RequestAccountListAsync(specification, conditions));
    }
}
