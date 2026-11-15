using Avalonia.Controls;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.Start;

namespace RemoteControl.App;

/// <summary>What the program uses of the windows that belong to no session (Ui.Start); a fake in tests.</summary>
internal interface IStartWindows
{
    /// <summary>Whether the login window is shown.</summary>
    bool IsLoginVisible { get; }

    /// <summary>The account in the login window.</summary>
    string SelectedAccount { get; }

    /// <summary>The nickname in the login window.</summary>
    string Nickname { get; }

    /// <summary>Whether the login window has a password.</summary>
    bool HasPassword { get; }

    /// <summary>The servers of the list received last.</summary>
    IReadOnlyList<string> Servers { get; }

    /// <summary>Shows the login window.</summary>
    void ShowLogin();

    /// <summary>Hides the login window.</summary>
    void HideLogin();

    /// <summary>Shows the server list (for a switch of a session, or for a new session).</summary>
    /// <param name="switchFor">The session, or null.</param>
    void ShowServerList(SessionContext? switchFor);

    /// <summary>Shows the list-server settings.</summary>
    /// <param name="owner">The window that asked, or null.</param>
    void ShowListServerSettings(Window? owner);

    /// <summary>Submits the login form (MCP).</summary>
    /// <param name="account">The account, or empty.</param>
    /// <param name="nickname">The nickname, or empty.</param>
    /// <returns>The outcome.</returns>
    Task<ConnectResult> SubmitLoginAsync(string account, string nickname);

    /// <summary>Connects a server of the list shown (MCP).</summary>
    /// <param name="serverName">The server.</param>
    /// <returns>The outcome.</returns>
    Task<ConnectResult> ConnectAsync(string serverName);
}

/// <summary>The real <see cref="IStartWindows"/>: the <see cref="StartController"/> of Ui.Start.</summary>
/// <param name="controller">The controller.</param>
internal sealed class StartWindows(StartController controller) : IStartWindows
{
    /// <summary>The controller.</summary>
    public StartController Controller { get; } = controller ?? throw new ArgumentNullException(nameof(controller));

    public bool IsLoginVisible => Controller.IsLoginVisible;

    public string SelectedAccount => Controller.SelectedAccount;

    public string Nickname => Controller.Nickname;

    public bool HasPassword => Controller.HasPassword;

    public IReadOnlyList<string> Servers => Controller.Servers;

    public void ShowLogin() => Controller.ShowLogin();

    public void HideLogin() => Controller.HideLogin();

    public void ShowServerList(SessionContext? switchFor) => Controller.ShowServerList(switchFor);

    public void ShowListServerSettings(Window? owner) => Controller.ShowListServerSettings(owner);

    public Task<ConnectResult> SubmitLoginAsync(string account, string nickname) => Controller.SubmitLoginAsync(account, nickname);

    public Task<ConnectResult> ConnectAsync(string serverName) => Controller.ConnectAsync(serverName);
}
