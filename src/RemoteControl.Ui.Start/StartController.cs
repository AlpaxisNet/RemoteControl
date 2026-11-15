using Avalonia.Controls;
using RemoteControl.Core;
using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Start;

/// <summary>
/// The windows of the program that belong to no session: the login window, the accounts manager, the server list and the list-server
/// settings ("RC settings"). The program creates one controller and drives it (review sections 1.3, 1.4, 11): it shows the login window
/// at start, calls <see cref="ShowServerList"/> for "switch server" and the tray, <see cref="ShowListServerSettings"/> for the gear of
/// any window, and quits on <see cref="LoginClosed"/>. Creating, connecting and showing a session is the program's: the controller
/// calls the connect function it was given with the <see cref="ServerChoice"/> the user made and shows the result.
/// </summary>
/// <remarks>
/// <para>Everything is used on the UI thread; nothing blocks it (the list-server login is asynchronous and cancelled when its window
/// closes).</para>
/// <para>A saved password is never sent to a list server the user did not choose for that account: when an account's stored association
/// matches no saved profile (the built-in one would be substituted, or it names an endpoint that is no profile), the user is told which
/// list server the account was saved for and which would be used, and picks a profile (which is then saved with the account) or cancels.
/// The MCP path (<see cref="SubmitLoginAsync"/>) cannot ask and refuses instead.</para>
/// </remarks>
/// <example>
/// <code>
/// var start = new StartController(services, shell, choice => program.ConnectAsync(choice));
/// start.LoginClosed += (_, _) => shell.Quit();
/// start.ShowLogin();
/// </code>
/// </example>
public sealed class StartController : IDisposable
{
    private readonly AppServices _services;
    private readonly IAppShell _shell;
    private readonly Func<ServerChoice, Task<ConnectResult>> _connect;
    private readonly List<ServerListWindow> _serverLists = new();
    private LoginWindow? _login;
    private AccountsWindow? _accounts;
    private ListServerSettingsWindow? _settings;
    private ServerListWindow? _primary;
    private ServerListLogin? _lastLogin;
    private IReadOnlyList<ServerInfo> _servers = Array.Empty<ServerInfo>();
    private ListServerProfile _currentProfile = ListServerProfile.Default;
    private bool _submitting;
    private bool _disposed;

    /// <summary>Creates the controller; no window is shown yet.</summary>
    /// <param name="services">The program's services.</param>
    /// <param name="shell">The program.</param>
    /// <param name="connect">
    /// Creates the session for a chosen server, connects it and opens (or, with <see cref="ServerChoice.SwitchFor"/>, replaces) its
    /// windows; returns whether it is connected and why not.
    /// </param>
    public StartController(AppServices services, IAppShell shell, Func<ServerChoice, Task<ConnectResult>> connect)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
    }

    /// <summary>Raised when the user closes the login window (its Close button or the window's close box): the program quits.</summary>
    public event EventHandler? LoginClosed;

    /// <summary>Raised when the list-server settings closed (the account combos read the profiles again).</summary>
    internal event EventHandler? ListServerSettingsClosed;

    /// <summary>Whether the login window is shown.</summary>
    public bool IsLoginVisible => _login is { IsClosed: false, IsVisible: true };

    /// <summary>The account in the login window (a saved account selected, or a name typed).</summary>
    public string SelectedAccount => _disposed ? string.Empty : Login.AccountText;

    /// <summary>The nickname in the login window.</summary>
    public string Nickname => _disposed ? string.Empty : Login.NicknameText;

    /// <summary>Whether the login window has a password (the saved one of the selected account, or one typed).</summary>
    public bool HasPassword => !_disposed && Login.PasswordText.Length > 0;

    /// <summary>Whether a server list is shown.</summary>
    public bool IsServerListVisible => _serverLists.Any(window => !window.IsClosed && window.IsVisible);

    /// <summary>The servers of the list received last (names without their kind prefix), in the list server's order.</summary>
    public IReadOnlyList<string> Servers => _servers.Select(server => server.DisplayName).ToArray();

    /// <summary>
    /// The list-server profile chosen last in the list-server settings (Alpaxis at first): a new account typed in the login window logs
    /// in there, and the Add Account dialog starts with it.
    /// </summary>
    public ListServerProfile CurrentProfile
    {
        get
        {
            ListServerProfile? stored = _services.Profiles.Find(_currentProfile.Name);
            return stored ?? ListServerProfile.Default;
        }
        internal set => _currentProfile = value ?? ListServerProfile.Default;
    }

    /// <summary>How long a list-server login may take (shorter in tests).</summary>
    internal TimeSpan ListServerTimeout { get; set; } = ListServerClient.DefaultTimeout;

    /// <summary>The login window (created on first use, kept until <see cref="Dispose"/>).</summary>
    internal LoginWindow Login
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _login ??= new LoginWindow(this, _services);
        }
    }

    /// <summary>The accounts manager when open.</summary>
    internal AccountsWindow? AccountsManager => _accounts is { IsClosed: false } window ? window : null;

    /// <summary>The list-server settings when open.</summary>
    internal ListServerSettingsWindow? Settings => _settings is { IsClosed: false } window ? window : null;

    /// <summary>The server list the program and the MCP server act on (the one opened from the login window or for a switch).</summary>
    internal ServerListWindow? ServerList => _primary is { IsClosed: false } window ? window : null;

    /// <summary>Every open server list (the primary one and those opened with "Open another server list").</summary>
    internal IReadOnlyList<ServerListWindow> ServerLists => _serverLists.Where(window => !window.IsClosed).ToArray();

    /// <summary>Shows the login window with the saved accounts read again.</summary>
    public void ShowLogin()
    {
        if (_disposed) return;
        UiSafe.Run(() =>
        {
            LoginWindow login = Login;
            login.RefreshAccounts();
            login.ShowAndActivate();
        }, "Start.ShowLogin");
    }

    /// <summary>Hides the login window.</summary>
    public void HideLogin()
    {
        if (_login is { IsClosed: false } login) UiSafe.Run(login.HideAndRemember, "Start.HideLogin");
    }

    /// <summary>
    /// Shows the server list and logs in at the list server: for a session (switch server) with its account at its list server, the
    /// chosen server then replaces the session's connection; without one, with the login used last (the login window when there is none).
    /// </summary>
    /// <param name="switchFor">The session that asked, or null.</param>
    public void ShowServerList(SessionContext? switchFor)
    {
        if (_disposed) return;
        UiSafe.Fire(() => ShowServerListAsync(switchFor), "Start.ShowServerList");
    }

    /// <summary>Shows the list-server settings ("RC settings"), not modal; a second call brings the open one to the front.</summary>
    /// <param name="owner">The window that asked, or null.</param>
    public void ShowListServerSettings(Window? owner)
    {
        if (_disposed) return;
        UiSafe.Run(() =>
        {
            if (Settings is { } open)
            {
                open.ShowAndActivate();
                return;
            }
            _settings = new ListServerSettingsWindow(this, _services);
            _settings.ShowAndActivate(owner);
        }, "Start.ShowListServerSettings");
    }

    /// <summary>
    /// What OK does in the login window, for a saved account and without the user (the MCP server): selects the account (when given),
    /// sets the nickname (when given) and logs in at the account's list server, then shows the server list; a direct-mode account is
    /// connected at once. An account whose list server matches no saved profile is refused (only the user can choose one).
    /// </summary>
    /// <param name="account">The account name, or empty for the one in the login window.</param>
    /// <param name="nickname">The nickname, or empty to keep it.</param>
    /// <returns>Whether the server list was received (or the direct connection made), and why not.</returns>
    public Task<ConnectResult> SubmitLoginAsync(string account, string nickname)
    {
        if (_disposed) return Task.FromResult(ConnectResult.Failed("The program is closing."));
        LoginWindow login = Login;
        if (!string.IsNullOrEmpty(account)) login.SelectAccount(account);
        if (!string.IsNullOrEmpty(nickname)) login.NicknameText = nickname;
        if (login.AccountText.Trim().Length == 0) return Task.FromResult(ConnectResult.Failed("Login account is empty"));
        if (login.PasswordText.Length == 0) return Task.FromResult(ConnectResult.Failed("The existing login form has no password"));
        return SubmitCoreAsync(interactive: false);
    }

    /// <summary>Connects to a server of the list shown (by name, prefix and case ignored), as the Connect button does.</summary>
    /// <param name="serverName">The server's name.</param>
    /// <returns>Whether the program connected it, and why not.</returns>
    public Task<ConnectResult> ConnectAsync(string serverName)
    {
        ServerListWindow? list = ServerList ?? _serverLists.LastOrDefault(window => !window.IsClosed);
        if (list is null || list.Servers.Count == 0) return Task.FromResult(ConnectResult.Failed("Server list is not loaded"));
        ServerInfo? server = list.Select(serverName ?? string.Empty);
        if (server is null) return Task.FromResult(ConnectResult.Failed("Named server was not found"));
        return list.ConnectAsync(server, additional: false);
    }

    /// <summary>Closes every window of the controller.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (ServerListWindow window in _serverLists.ToArray()) UiSafe.Run(window.Close, "Start.Dispose");
        _serverLists.Clear();
        if (_settings is { IsClosed: false } settings) UiSafe.Run(settings.Close, "Start.Dispose");
        if (_accounts is { IsClosed: false } accounts) UiSafe.Run(accounts.Close, "Start.Dispose");
        if (_login is { IsClosed: false } login) UiSafe.Run(login.CloseForGood, "Start.Dispose");
    }

    /// <summary>What a refused account change means for the user.</summary>
    /// <param name="accounts">The store.</param>
    /// <param name="status">What it did.</param>
    /// <returns>A sentence.</returns>
    internal static string SaveProblem(AccountStore accounts, AccountSaveStatus status) => status switch
    {
        AccountSaveStatus.Refused => accounts.LastLoad.Message is { Length: > 0 } message
            ? message + " Nothing was changed."
            : "The saved accounts could not be read and are kept as they are, so nothing was changed.",
        AccountSaveStatus.Failed => accounts.LastError ?? "The saved accounts could not be written.",
        AccountSaveStatus.Ignored => "Nothing was saved: the account name is empty.",
        _ => string.Empty,
    };

    /// <summary>OK in the login window.</summary>
    /// <returns>The outcome.</returns>
    internal Task<ConnectResult> SubmitFromWindowAsync() => SubmitCoreAsync(interactive: true);

    /// <summary>The program's connect function, guarded.</summary>
    /// <param name="choice">The server chosen.</param>
    /// <returns>The outcome.</returns>
    internal async Task<ConnectResult> ConnectServerAsync(ServerChoice choice)
    {
        try
        {
            return await _connect(choice).ConfigureAwait(true) ?? ConnectResult.Failed("Unable to connect.");
        }
        catch (Exception exception)
        {
            _services.Log.Error("Connecting to " + choice.Login.Server.DisplayName + " failed.", exception);
            return ConnectResult.Failed(exception.Message);
        }
    }

    /// <summary>The server list for a session or the last login (see <see cref="ShowServerList"/>).</summary>
    /// <param name="switchFor">The session, or null.</param>
    /// <returns>The outcome of the list-server login.</returns>
    internal async Task<ConnectResult> ShowServerListAsync(SessionContext? switchFor)
    {
        if (_disposed) return ConnectResult.Failed("The program is closing.");
        ServerListLogin login;
        if (switchFor is { IsEnded: false })
        {
            ListServerProfile? profile = string.IsNullOrEmpty(switchFor.Login.ListServer) ? null : _services.Profiles.Find(switchFor.Login.ListServer);
            if (profile is null)
            {
                string saved = string.IsNullOrEmpty(switchFor.Login.ListServer) ? string.Empty : switchFor.Login.ListServer;
                profile = await AskForProfileAsync(switchFor.Account, saved, _services.Profiles.Resolve(saved), ServerList);
                if (profile is null) return ConnectResult.Failed("No list server was chosen.");
            }
            login = new ServerListLogin(switchFor.Account, switchFor.Login.Password, switchFor.Nickname, profile);
        }
        else if (_lastLogin is not null) login = _lastLogin;
        else
        {
            ShowLogin();
            return ConnectResult.Failed("Sign in first.");
        }
        return await OpenServerListAsync(login, switchFor is { IsEnded: false } ? switchFor : null, another: false);
    }

    /// <summary>Shows a server list for a login and logs in.</summary>
    /// <param name="login">Who logs in where.</param>
    /// <param name="switchFor">The session to switch, or null.</param>
    /// <param name="another">Open another window ("Open another server list") instead of using the primary one.</param>
    /// <returns>The outcome of the list-server login.</returns>
    internal Task<ConnectResult> OpenServerListAsync(ServerListLogin login, SessionContext? switchFor, bool another)
    {
        if (_disposed) return Task.FromResult(ConnectResult.Failed("The program is closing."));
        ServerListWindow window;
        if (!another && ServerList is { } primary)
        {
            primary.UseLogin(login);
            primary.SwitchFor = switchFor;
            window = primary;
        }
        else
        {
            window = new ServerListWindow(this, _services, login, switchFor);
            _serverLists.Add(window);
            if (!another) _primary = window;
        }
        if (!another) _lastLogin = login;
        window.ShowAndActivate();
        return window.RefreshAsync();
    }

    /// <summary>"Open another server list" with a saved account: at that account's own list server (asking when it matches no profile).</summary>
    /// <param name="index">The account's store index.</param>
    /// <param name="owner">The window that asked.</param>
    /// <returns>The outcome of the list-server login.</returns>
    internal async Task<ConnectResult> OpenServerListForAccountAsync(int index, Window? owner)
    {
        IReadOnlyList<SavedAccount> accounts = _services.Accounts.Accounts;
        if (index < 0 || index >= accounts.Count) return ConnectResult.Failed("The account is not saved any more.");
        SavedAccount account = accounts[index];
        if (account.DirectMode)
        {
            const string direct = "This account is in Direct Mode: it connects to its RC server without a server list.";
            await Dialogs.ShowMessageAsync("Open another server list", direct, owner);
            return ConnectResult.Failed(direct);
        }
        ListServerEndpoint endpoint = _services.Profiles.Resolve(account.ListServer);
        ListServerProfile? profile = endpoint is { IsFallback: false, Profile: { } matched } ? matched : await AskForProfileAsync(account.Name, account.ListServer, endpoint, owner);
        if (profile is null) return ConnectResult.Failed("No list server was chosen.");
        string password = _services.Accounts.GetPassword(index);
        if (password.Length == 0)
        {
            string? typed = await Dialogs.PromptAsync("Password", $"Password of {account.Name} at {profile.Name}:", string.Empty, owner, password: true);
            if (string.IsNullOrEmpty(typed)) return ConnectResult.Failed("No password was given.");
            password = typed;
        }
        string nickname = _services.Options.Current.Nickname;
        return await OpenServerListAsync(new ServerListLogin(account.Name, password, nickname, profile), null, another: true);
    }

    /// <summary>The saved accounts (not in direct mode) whose list server is <paramref name="profile"/>.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>Index and account.</returns>
    internal IReadOnlyList<(int Index, SavedAccount Account)> AccountsForProfile(ListServerProfile profile)
    {
        var result = new List<(int, SavedAccount)>();
        IReadOnlyList<SavedAccount> accounts = _services.Accounts.Accounts;
        for (int index = 0; index < accounts.Count; index++)
        {
            SavedAccount account = accounts[index];
            if (account.DirectMode) continue;
            ListServerEndpoint endpoint = _services.Profiles.Resolve(account.ListServer);
            if (!endpoint.IsFallback && endpoint.Profile is { } matched && string.Equals(matched.Name, profile.Name, StringComparison.OrdinalIgnoreCase))
                result.Add((index, account));
        }
        return result;
    }

    /// <summary>The servers a list received (kept for <see cref="Servers"/>).</summary>
    /// <param name="window">The server list.</param>
    internal void NoteServers(ServerListWindow window)
    {
        if (ReferenceEquals(window, _primary) || ServerList is null) _servers = window.Servers;
    }

    /// <summary>A server list closed: the login window comes back when nothing else is open (<c>TMain.cpp:760</c>).</summary>
    /// <param name="window">The window.</param>
    internal void OnServerListClosed(ServerListWindow window)
    {
        _serverLists.Remove(window);
        if (ReferenceEquals(window, _primary)) _primary = null;
        if (_disposed || window.ClosedAfterConnect) return;
        if (_shell.Sessions.Count == 0 && _serverLists.Count == 0 && !IsLoginVisible) ShowLogin();
    }

    /// <summary>The user closed the login window.</summary>
    internal void OnLoginClosedByUser() => UiSafe.Run(() => LoginClosed?.Invoke(this, EventArgs.Empty), "Start.LoginClosed");

    /// <summary>The list-server settings closed.</summary>
    /// <param name="window">The window.</param>
    internal void OnListServerSettingsClosed(ListServerSettingsWindow window)
    {
        if (ReferenceEquals(_settings, window)) _settings = null;
        if (_disposed) return;
        UiSafe.Run(() => ListServerSettingsClosed?.Invoke(this, EventArgs.Empty), "Start.SettingsClosed");
        if (_login is { IsClosed: false } login) login.RefreshAccounts();
    }

    /// <summary>Shows the accounts manager (one at a time), owned by the login window when it is shown.</summary>
    /// <returns>The window.</returns>
    internal AccountsWindow ShowAccountsManager()
    {
        if (AccountsManager is { } open)
        {
            open.ShowAndActivate();
            return open;
        }
        var window = new AccountsWindow(this, _services);
        _accounts = window;
        window.ShowAndActivate(IsLoginVisible ? _login : null);
        return window;
    }

    /// <summary>Puts a saved account into the login window (Select in the accounts manager).</summary>
    /// <param name="index">The store index.</param>
    internal void SelectAccountInLogin(int index) => Login.SelectAccount(index);

    /// <summary>
    /// The theme chosen in the list-server settings (<c>TMain.cpp:781-791</c>): written to the shared options (every session follows),
    /// and the script theme with it when <c>syncsyntaxtheme</c> is on (<c>dark</c> gives <c>language-spec</c>).
    /// </summary>
    /// <param name="id">The theme id.</param>
    /// <param name="source">The window that changed it.</param>
    internal void ChooseTheme(string id, object? source)
    {
        string theme = ThemeManager.Normalize(id);
        _services.Theme.ChooseTheme(theme, source);
        if (_services.Options.Current.SyncSyntaxTheme)
            _services.Options.Update(options => options with { SyntaxTheme = theme == "dark" ? "language-spec" : theme }, source);
    }

    private async Task<ConnectResult> SubmitCoreAsync(bool interactive)
    {
        if (_disposed) return ConnectResult.Failed("The program is closing.");
        if (_submitting) return ConnectResult.Failed("A login is already running.");
        LoginWindow form = Login;
        string account = form.AccountText.Trim();
        if (account.Length == 0)
        {
            form.FocusAccount();
            return ConnectResult.Failed("Login account is empty");
        }
        string password = form.PasswordText;
        string nickname = form.NicknameText;
        bool dontSave = form.DontSavePassword;
        bool graphical = form.GraphicalMenu;
        _services.Options.Update(options => options with { Nickname = nickname, DontSavePassword = dontSave, GraphicalMenu = graphical }, this);

        int index = form.SelectedIndex;
        if (index < 0) index = _services.Accounts.IndexOfName(account);
        IReadOnlyList<SavedAccount> accounts = _services.Accounts.Accounts;
        SavedAccount? saved = index >= 0 && index < accounts.Count ? accounts[index] : null;
        Window? owner = IsLoginVisible ? form : null;

        _submitting = true;
        form.IsBusy = true;
        try
        {
            if (saved is { DirectMode: true })
            {
                if (!ListServerAssociation.TryParseDirect(ListServerAssociation.ToDirect(saved.ListServer), _services.Profiles.Profiles, out string host, out int port))
                {
                    string invalid = $"The account \"{saved.Name}\" is in Direct Mode, but its server \"{saved.ListServer}\" is neither host:port nor a saved list-server profile. Edit the account.";
                    if (interactive) UiSafe.Fire(() => Dialogs.ShowErrorAsync("Connection Error", invalid, owner), "Start.DirectInvalid");
                    return ConnectResult.Failed(invalid);
                }
                SaveAccount(index, account, password, dontSave, saved.ListServer, directMode: true);
                HideLogin();
                var direct = new ServerChoice(new SessionLogin(saved.Name, password, nickname, ServerInfo.Direct(host, port), string.Empty), null);
                ConnectResult result = await ConnectServerAsync(direct);
                if (!result.Success && !_disposed)
                {
                    ShowLogin();
                    string reason = result.Error is { Length: > 0 } error ? error : "Unable to connect directly to the RC server.";
                    if (interactive) UiSafe.Fire(() => Dialogs.ShowErrorAsync("Connection Error", reason, _login), "Start.DirectFailed");
                }
                return result;
            }

            ListServerProfile profile;
            if (saved is not null)
            {
                ListServerEndpoint endpoint = _services.Profiles.Resolve(saved.ListServer);
                if (endpoint is { IsFallback: false, Profile: { } matched }) profile = matched;
                else
                {
                    if (!interactive) return ConnectResult.Failed(UnconfirmedListServer(saved.Name, saved.ListServer, endpoint));
                    ListServerProfile? chosen = await AskForProfileAsync(saved.Name, saved.ListServer, endpoint, owner);
                    if (chosen is null) return ConnectResult.Failed("The login was cancelled: no list server was chosen.");
                    profile = chosen;
                }
            }
            else profile = CurrentProfile;

            SaveAccount(index, account, password, dontSave, profile.Association, directMode: false);
            HideLogin();
            return await OpenServerListAsync(new ServerListLogin(account, password, nickname, profile), null, another: false);
        }
        finally
        {
            _submitting = false;
            if (!form.IsClosed) form.IsBusy = false;
        }
    }

    private void SaveAccount(int index, string name, string password, bool dontSave, string listServer, bool directMode)
    {
        AccountSaveStatus status = index >= 0
            ? _services.Accounts.SaveAt(index, password, dontSave, listServer, directMode)
            : _services.Accounts.Save(name, password, dontSave, listServer, directMode);
        if (status != AccountSaveStatus.Saved)
            _services.Log.Warn("The account was not saved: " + SaveProblem(_services.Accounts, status));
    }

    private static string SavedFor(string association) =>
        string.IsNullOrWhiteSpace(association) ? "no list server" : $"the list server \"{association}\"";

    private static string WouldUse(ListServerEndpoint endpoint) =>
        endpoint.IsFallback ? $"{endpoint.Name} ({endpoint.Endpoint})" : $"{endpoint.Endpoint}, which is not a saved profile";

    private static string UnconfirmedListServer(string account, string association, ListServerEndpoint endpoint) =>
        $"The account \"{account}\" was saved for {SavedFor(association)}, which is not one of the saved list servers; signing in would send "
        + $"its password to {WouldUse(endpoint)}. Nothing was sent. Choose the account's list server in the login window first.";

    /// <summary>
    /// Asks which list server an account signs in at when its saved association matches no profile: says what it was saved for and what
    /// would be used, offers the profiles (and the saved endpoint itself, added as a profile when chosen). Nothing is sent before.
    /// </summary>
    private async Task<ListServerProfile?> AskForProfileAsync(string account, string association, ListServerEndpoint endpoint, Window? owner)
    {
        IReadOnlyList<ListServerProfile> profiles = _services.Profiles.Profiles;
        var items = profiles.Select(profile => profile.Association).ToList();
        bool offerEndpoint = !endpoint.IsFallback && endpoint.Profile is null;
        if (offerEndpoint) items.Add($"{endpoint.Name}{ListServerAssociation.LabelSeparator}{endpoint.Endpoint} (as saved; keep it as a profile)");
        string message = $"The account \"{account}\" was saved for {SavedFor(association)}, which is not one of your saved list servers. "
            + $"Signing in now would send its password to {WouldUse(endpoint)}.\n\nChoose the list server to sign in at, or Cancel.";
        int? chosen = await Dialogs.ChooseAsync("Choose List Server", message, items, owner is { IsVisible: true } ? owner : null, offerEndpoint ? items.Count - 1 : 0);
        if (chosen is not { } index || _disposed) return null;
        if (index < profiles.Count) return profiles[index];

        var kept = new ListServerProfile(endpoint.Name, endpoint.Host, endpoint.Port);
        ProfileChangeStatus status = _services.Profiles.Add(kept);
        if (status == ProfileChangeStatus.DuplicateName)
        {
            kept = new ListServerProfile(endpoint.Endpoint, endpoint.Host, endpoint.Port);
            status = _services.Profiles.Add(kept);
        }
        if (status is not (ProfileChangeStatus.Saved or ProfileChangeStatus.DuplicateName))
            _services.Log.Warn("The list server " + kept.Association + " could not be kept as a profile: " + status);
        return kept;
    }
}
