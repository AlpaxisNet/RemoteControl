using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using RemoteControl.Core;
using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Start;

/// <summary>Who logs in at which list server; kept in memory only.</summary>
/// <param name="Account">The account.</param>
/// <param name="Password">The password.</param>
/// <param name="Nickname">The nickname for the RC.</param>
/// <param name="Profile">The list server.</param>
internal sealed record ServerListLogin(string Account, string Password, string Nickname, ListServerProfile Profile)
{
    /// <summary>Never shows the password.</summary>
    /// <returns>Account and list server.</returns>
    public override string ToString() => Account + "@" + Profile.Name;
}

/// <summary>The columns the server list can be sorted by.</summary>
internal enum ServerSort
{
    /// <summary>By name.</summary>
    Name,

    /// <summary>By player count.</summary>
    Players,
}

/// <summary>
/// The server list (<c>TServerList</c>, review sections 1.4 and protocol section 2): logs in at a list server
/// (<see cref="ListServerClient.LoginAsync"/>, cancelled when the window closes), shows the servers with their kind's icon, name and
/// player count (sorted by players, descending, at first; the column headers sort), the details of the selected server, Refresh,
/// Connect (button, double click, Enter; Ctrl+click opens an additional connection), a direct connection by host and port, and the
/// list-server settings gear. Connect hands a <see cref="ServerChoice"/> to the program; a failure is shown here and the window stays.
/// </summary>
/// <remarks>
/// D-3 (a double free when the window was destroyed while a refresh result was pending) cannot happen: the result is a managed object
/// that the awaiting method receives; when the window closed in between, <see cref="RcWindow.ClosedToken"/> has cancelled the login
/// and the result is dropped. D-25 (a freeze on close while the list server hangs) is gone with it: nothing joins a thread.
/// </remarks>
internal sealed partial class ServerListWindow : RcWindow
{
    private readonly StartController _controller;
    private readonly AppServices _services;
    private IReadOnlyList<ServerInfo> _servers = Array.Empty<ServerInfo>();
    private Task<ConnectResult>? _login;
    private CancellationTokenSource? _loginCancel;
    private int _generation;
    private bool _connecting;
    private ServerSort _sort = ServerSort.Players;
    private bool _descending = true;

    /// <summary>For the XAML previewer only.</summary>
    public ServerListWindow() : this(null!, null!, new ServerListLogin(string.Empty, string.Empty, string.Empty, ListServerProfile.Default), null)
    {
    }

    /// <summary>Creates the window; <see cref="RefreshAsync"/> logs in.</summary>
    /// <param name="controller">The controller.</param>
    /// <param name="services">The program's services.</param>
    /// <param name="login">Who logs in where.</param>
    /// <param name="switchFor">The session whose connection a chosen server replaces, or null.</param>
    public ServerListWindow(StartController controller, AppServices services, ServerListLogin login, SessionContext? switchFor) : base(services)
    {
        InitializeComponent();
        _controller = controller;
        _services = services;
        Login = login;
        SwitchFor = switchFor;
        ServerListField.ItemsSource = Rows;
        Title = login.Profile.Name + " Servers";
        if (services is null) return;

        RefreshButton.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => RefreshAsync(), "ServerList.Refresh");
        ConnectButton.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => ConnectSelectedAsync(additional: false), "ServerList.Connect");
        DirectButton.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => PromptDirectAsync(), "ServerList.Direct");
        SettingsButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => _controller.ShowListServerSettings(this), "ServerList.Settings");
        HomepageButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => OpenHomepage(), "ServerList.Homepage");
        SortByNameButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => SortBy(ServerSort.Name), "ServerList.SortName");
        SortByPlayersButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => SortBy(ServerSort.Players), "ServerList.SortPlayers");
        ServerListField.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => ShowSelected(), "ServerList.Selection");
        ServerListField.DoubleTapped += UiSafe.AsyncHandler<TappedEventArgs>((_, e) => RowAt(e.Source) is null ? Task.CompletedTask : ConnectSelectedAsync(additional: false), "ServerList.DoubleClick");
        ServerListField.AddHandler(PointerReleasedEvent, UiSafe.AsyncHandler<PointerReleasedEventArgs>((_, e) => OnPointerReleasedAsync(e), "ServerList.CtrlClick"), RoutingStrategies.Tunnel);
        var menu = new ContextMenu();
        menu.Opening += (_, _) => UiSafe.Run(() => BuildContextMenu(menu), "ServerList.Menu");
        ServerListField.ContextMenu = menu;
        UpdateSortHeaders();
        UpdateButtons();
    }

    /// <summary>Who logs in where.</summary>
    internal ServerListLogin Login { get; private set; }

    /// <summary>The session whose connection the chosen server replaces, or null for a new session.</summary>
    internal SessionContext? SwitchFor { get; set; }

    /// <summary>The rows shown, in display order.</summary>
    internal ObservableCollection<ServerRow> Rows { get; } = new();

    /// <summary>The servers last received, in the list server's order.</summary>
    internal IReadOnlyList<ServerInfo> Servers => _servers;

    /// <summary>Whether a list-server login is running.</summary>
    internal bool IsLoggingIn => _login is { IsCompleted: false };

    /// <summary>Whether a connect is running.</summary>
    internal bool IsConnecting => _connecting;

    /// <summary>The status line (the list server's welcome text, "Connecting…", an error).</summary>
    internal string Status => StatusText.Text ?? string.Empty;

    /// <summary>Whether the status line shows an error.</summary>
    internal bool StatusIsError => StatusText.Classes.Contains("error");

    /// <summary>Whether the window closed because a server was connected (then the login window is not shown again).</summary>
    internal bool ClosedAfterConnect { get; private set; }

    /// <summary>The current sort.</summary>
    internal (ServerSort Column, bool Descending) Sort => (_sort, _descending);

    /// <summary>Uses another account or list server for the next <see cref="RefreshAsync"/> (switch server, a new login).</summary>
    /// <param name="login">Who logs in where.</param>
    internal void UseLogin(ServerListLogin login)
    {
        if (login == Login) return;
        Login = login;
        Title = login.Profile.Name + " Servers";
        _generation++;
        CancelLogin();
        ApplyServers(Array.Empty<ServerInfo>());
    }

    /// <summary>Logs in at the list server and shows its servers; while one runs, the same task is returned.</summary>
    /// <returns>Whether the list was received, and the list server's error if not.</returns>
    internal Task<ConnectResult> RefreshAsync()
    {
        if (IsClosed) return Task.FromResult(ConnectResult.Failed("The server list is closed."));
        if (_login is { IsCompleted: false } running) return running;
        if (_connecting) return Task.FromResult(ConnectResult.Failed("A connection is being made."));
        Task<ConnectResult> login = RunLoginAsync();
        if (!login.IsCompleted) _login = login;
        UpdateButtons();
        return login;
    }

    /// <summary>Connects to the selected server.</summary>
    /// <param name="additional">Open it as a new session even when the list was opened to switch a session's server.</param>
    /// <returns>The outcome.</returns>
    internal Task<ConnectResult> ConnectSelectedAsync(bool additional)
    {
        if (ServerListField.SelectedItem is not ServerRow row) return Task.FromResult(ConnectResult.Failed("No server is selected."));
        return ConnectAsync(row.Server, additional);
    }

    /// <summary>Selects a server by name (prefix ignored, case ignored).</summary>
    /// <param name="name">The name.</param>
    /// <returns>The server, or null.</returns>
    internal ServerInfo? Select(string name)
    {
        foreach (ServerRow row in Rows)
        {
            if (string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(row.Server.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                ServerListField.SelectedItem = row;
                return row.Server;
            }
        }
        return null;
    }

    /// <summary>
    /// Hands a server to the program (<see cref="ServerChoice"/>); closes the window when it is connected, shows the failure otherwise.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="additional">A new session even when switching.</param>
    /// <param name="login">Another login to use (Connect using account), or null for the list's.</param>
    /// <returns>The outcome.</returns>
    internal async Task<ConnectResult> ConnectAsync(ServerInfo server, bool additional, SessionLogin? login = null)
    {
        if (IsClosed) return ConnectResult.Failed("The server list is closed.");
        if (_connecting) return ConnectResult.Failed("A connection is already being made.");
        if (IsLoggingIn) return ConnectResult.Failed("The server list is still loading.");
        _connecting = true;
        UpdateButtons();
        ShowStatus("Connecting to " + server.DisplayName + "…");
        SessionContext? switchFor = additional || SwitchFor is not { IsEnded: false } session ? null : session;
        var choice = new ServerChoice(login ?? new SessionLogin(Login.Account, Login.Password, Login.Nickname, server, Login.Profile.Name), switchFor);
        ConnectResult result;
        try
        {
            result = await _controller.ConnectServerAsync(choice) ?? ConnectResult.Failed("Unable to connect.");
        }
        catch (Exception exception)
        {
            _services.Log.Error("Connecting to " + server.DisplayName + " failed.", exception);
            result = ConnectResult.Failed(exception.Message);
        }
        finally
        {
            _connecting = false;
        }
        if (IsClosed) return result;
        UpdateButtons();
        if (result.Success)
        {
            ShowStatus(string.Empty);
            ClosedAfterConnect = true;
            Close();
        }
        else ShowStatus(result.Error is { Length: > 0 } error ? error : "Unable to connect to " + server.DisplayName + ".", error: true);
        return result;
    }

    /// <summary>Connects directly to an RC server by host and port, with the list's account.</summary>
    /// <param name="host">The host.</param>
    /// <param name="port">The port.</param>
    /// <returns>The outcome.</returns>
    internal Task<ConnectResult> DirectConnectAsync(string host, int port) => ConnectAsync(ServerInfo.Direct(host, port), additional: false);

    /// <summary>Sorts by a column; the same column again turns the order around.</summary>
    /// <param name="column">The column.</param>
    internal void SortBy(ServerSort column)
    {
        if (_sort == column) _descending = !_descending;
        else
        {
            _sort = column;
            _descending = column == ServerSort.Players;
        }
        UpdateSortHeaders();
        ApplyServers(_servers);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _generation++;
        base.OnClosed(e);
        _controller?.OnServerListClosed(this);
    }

    private async Task<ConnectResult> RunLoginAsync()
    {
        int generation = ++_generation;
        ServerListLogin login = Login;
        ShowStatus($"Connecting to {login.Profile.Name} ({login.Profile.Endpoint})…");
        EmptyText.Text = "Loading server list...";
        EmptyText.IsVisible = Rows.Count == 0;
        UpdateButtons();
        ListServerResult result;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ClosedToken);
        _loginCancel = cancel;
        try
        {
            result = await ListServerClient.LoginAsync(login.Profile.Host, login.Profile.Port, login.Account, login.Password, _controller.ListServerTimeout, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return ConnectResult.Failed("The list-server login was cancelled.");
        }
        catch (Exception exception)
        {
            _services.Log.Error("The list-server login failed.", exception);
            if (IsClosed || generation != _generation) return ConnectResult.Failed(exception.Message);
            FinishLogin();
            ShowStatus(exception.Message, error: true);
            return ConnectResult.Failed(exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_loginCancel, cancel)) _loginCancel = null;
        }
        if (IsClosed || generation != _generation) return ConnectResult.Failed("The list-server login was cancelled.");
        FinishLogin();
        if (!result.Success)
        {
            string error = result.Error ?? "Server rejected connection";
            EmptyText.Text = "No servers.";
            EmptyText.IsVisible = Rows.Count == 0;
            ShowStatus(error, error: true);
            return ConnectResult.Failed(error);
        }
        ApplyServers(result.Servers);
        _controller.NoteServers(this);
        ShowStatus(result.StatusText?.Trim() ?? string.Empty);
        if (ServerListField.SelectedIndex < 0 && Rows.Count > 0) ServerListField.SelectedIndex = 0;
        return ConnectResult.Ok;

        void FinishLogin()
        {
            _login = null;
            UpdateButtons();
        }
    }

    private void CancelLogin()
    {
        CancellationTokenSource? cancel = _loginCancel;
        _loginCancel = null;
        _login = null;
        try
        {
            cancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The login has just finished.
        }
    }

    private void ApplyServers(IReadOnlyList<ServerInfo> servers)
    {
        _servers = servers;
        IEnumerable<ServerInfo> ordered = _sort == ServerSort.Players
            ? (_descending ? servers.OrderByDescending(s => s.Players) : servers.OrderBy(s => s.Players)).ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            : _descending ? servers.OrderByDescending(s => s.DisplayName, StringComparer.OrdinalIgnoreCase) : servers.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase);

        object? selected = ServerListField.SelectedItem;
        var available = new Dictionary<string, Queue<ServerRow>>(StringComparer.Ordinal);
        foreach (ServerRow row in Rows)
        {
            if (!available.TryGetValue(row.Server.Name, out Queue<ServerRow>? queue)) available[row.Server.Name] = queue = new Queue<ServerRow>();
            queue.Enqueue(row);
        }
        var desired = new List<ServerRow>();
        foreach (ServerInfo server in ordered)
        {
            Bitmap icon = _services.Assets.ServerTypeIcon(ServerRow.IconIndex(server.Kind));
            if (available.TryGetValue(server.Name, out Queue<ServerRow>? queue) && queue.Count > 0)
            {
                ServerRow row = queue.Dequeue();
                row.Update(server, icon);
                desired.Add(row);
            }
            else desired.Add(new ServerRow(server, icon));
        }
        for (int index = 0; index < desired.Count; index++)
        {
            if (index < Rows.Count && ReferenceEquals(Rows[index], desired[index])) continue;
            int at = -1;
            for (int search = index + 1; search < Rows.Count; search++)
                if (ReferenceEquals(Rows[search], desired[index])) { at = search; break; }
            if (at >= 0) Rows.Move(at, index);
            else Rows.Insert(index, desired[index]);
        }
        while (Rows.Count > desired.Count) Rows.RemoveAt(Rows.Count - 1);
        if (selected is ServerRow keep && Rows.Contains(keep) && !ReferenceEquals(ServerListField.SelectedItem, keep)) ServerListField.SelectedItem = keep;
        EmptyText.Text = "No servers.";
        EmptyText.IsVisible = Rows.Count == 0 && !IsLoggingIn;
        ShowSelected();
        UpdateButtons();
    }

    private void ShowSelected()
    {
        ServerInfo? server = (ServerListField.SelectedItem as ServerRow)?.Server;
        LanguageField.Text = server?.Language ?? string.Empty;
        VersionField.Text = server?.Version ?? string.Empty;
        HomepageField.Text = server?.Homepage ?? string.Empty;
        DescriptionField.Text = server?.Description ?? string.Empty;
        UpdateButtons();
    }

    private void OpenHomepage()
    {
        string homepage = (HomepageField.Text ?? string.Empty).Trim();
        if (homepage.Length == 0) return;
        if (!_services.Urls.Open(homepage)) ShowStatus("The homepage could not be opened: " + homepage, error: true);
    }

    private async Task PromptDirectAsync()
    {
        string? text = await Dialogs.PromptAsync("Direct Connection", "RC server (host:port):", string.Empty, this);
        if (text is null || IsClosed) return;
        if (!ListServerAssociation.TrySplitEndpoint(text.Trim(), out string host, out int port))
        {
            ShowStatus("Enter the RC server as host:port (port 1 to 65535).", error: true);
            return;
        }
        await DirectConnectAsync(host, port);
    }

    private async Task OnPointerReleasedAsync(PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || (e.KeyModifiers & KeyModifiers.Control) == 0) return;
        if (RowAt(e.Source) is not { } row) return;
        e.Handled = true;
        ServerListField.SelectedItem = row;
        await ConnectSelectedAsync(additional: true);
    }

    private static ServerRow? RowAt(object? source) =>
        (source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ServerRow;

    /// <summary>Fills the context menu of the list (when it opens).</summary>
    /// <param name="menu">The menu.</param>
    internal void BuildContextMenu(ContextMenu menu)
    {
        var items = new List<object>();
        bool canConnect = ServerListField.SelectedItem is ServerRow && !_connecting && !IsLoggingIn;

        var another = new MenuItem { Header = "Open another server list" };
        var accounts = new List<object>();
        IReadOnlyList<SavedAccount> saved = _services.Accounts.Accounts;
        IReadOnlyList<ListServerProfile> profiles = _services.Profiles.Profiles;
        for (int index = 0; index < saved.Count; index++)
        {
            int accountIndex = index;
            SavedAccount account = saved[index];
            var item = new MenuItem { Header = account.Name + " [" + AccountPresentation.Badge(account, profiles) + "]" };
            item.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => _controller.OpenServerListForAccountAsync(accountIndex, this), "ServerList.AnotherList");
            accounts.Add(item);
        }
        if (accounts.Count == 0) accounts.Add(new MenuItem { Header = "No saved accounts", IsEnabled = false });
        another.ItemsSource = accounts;
        items.Add(another);

        var additional = new MenuItem { Header = "Open additional connection", IsEnabled = canConnect };
        additional.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => ConnectSelectedAsync(additional: true), "ServerList.Additional");
        items.Add(additional);
        var connect = new MenuItem { Header = "Connect", IsEnabled = canConnect };
        connect.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => ConnectSelectedAsync(additional: false), "ServerList.MenuConnect");
        items.Add(connect);

        var usingAccount = new MenuItem { Header = "Connect using account", IsEnabled = canConnect };
        var choices = new List<object>();
        foreach ((int accountIndex, SavedAccount account) in _controller.AccountsForProfile(Login.Profile))
        {
            var item = new MenuItem { Header = account.Name + " [" + Login.Profile.Name + "]", IsEnabled = account.HasPassword };
            if (!account.HasPassword) item.Header += " (no saved password)";
            item.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => ConnectUsingAccountAsync(accountIndex), "ServerList.UsingAccount");
            choices.Add(item);
        }
        if (choices.Count == 0) choices.Add(new MenuItem { Header = "No other account of " + Login.Profile.Name, IsEnabled = false });
        usingAccount.ItemsSource = choices;
        items.Add(usingAccount);
        menu.ItemsSource = items;
    }

    /// <summary>Connects to the selected server with another saved account of this list server, as an additional session.</summary>
    /// <param name="accountIndex">The account's store index.</param>
    /// <returns>The outcome.</returns>
    internal Task<ConnectResult> ConnectUsingAccountAsync(int accountIndex)
    {
        if (ServerListField.SelectedItem is not ServerRow row) return Task.FromResult(ConnectResult.Failed("No server is selected."));
        IReadOnlyList<SavedAccount> accounts = _services.Accounts.Accounts;
        if (accountIndex < 0 || accountIndex >= accounts.Count) return Task.FromResult(ConnectResult.Failed("The account is not saved any more."));
        SavedAccount account = accounts[accountIndex];
        var login = new SessionLogin(account.Name, _services.Accounts.GetPassword(accountIndex), Login.Nickname, row.Server, Login.Profile.Name);
        return ConnectAsync(row.Server, additional: true, login);
    }

    private void UpdateSortHeaders()
    {
        string arrow = _descending ? " ↓" : " ↑";
        SortByNameButton.Content = "Server" + (_sort == ServerSort.Name ? arrow : string.Empty);
        SortByPlayersButton.Content = "Players" + (_sort == ServerSort.Players ? arrow : string.Empty);
    }

    private void UpdateButtons()
    {
        bool busy = IsLoggingIn || _connecting;
        RefreshButton.IsEnabled = !busy;
        ConnectButton.IsEnabled = !busy && ServerListField.SelectedItem is ServerRow;
        DirectButton.IsEnabled = !busy;
    }

    private void ShowStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        ToolTip.SetTip(StatusText, text.Length == 0 ? null : text);
        StatusText.Classes.Set("error", error);
    }
}
