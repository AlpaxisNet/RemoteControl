using Avalonia;
using Avalonia.Controls;
using RemoteControl.Core;
using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Start.Tests;

/// <summary>
/// One test's start windows: a temporary environment, a fake program, the controller with a recording connect function and a fake
/// list server on 127.0.0.1 with two servers and a profile "Test" pointing at it. No test may reach the real Alpaxis list server: every
/// login of these tests goes to the fake one (a test that would use the built-in profile asserts that nothing was sent).
/// </summary>
internal sealed class StartFixture : IDisposable
{
    public StartFixture(string? theme = null)
    {
        Environment = new TestEnvironment(theme);
        Shell = new FakeAppShell();
        Server = new FakeListServer();
        Server.Servers.Add(new ServerInfo("P Alpha", "English", "The Alpha server.", "https://alpha.example/", "1.0", 12, "127.0.0.1", 14900));
        Server.Servers.Add(new ServerInfo("H Beta", "German", "The Beta server.", "https://beta.example/", "2.0", 30, "127.0.0.1", 14901));
        Server.Servers.Add(new ServerInfo("U Gamma", "English", "Staff only.", string.Empty, "3.0", 2, "127.0.0.1", 14902));
        Controller = new StartController(Environment.Services, Shell, choice =>
        {
            Choices.Add(choice);
            return Connect(choice);
        })
        {
            ListServerTimeout = TimeSpan.FromSeconds(3),
        };
        Assert.Equal(ProfileChangeStatus.Saved, Services.Profiles.Add(new ListServerProfile("Test", Server.Host, Server.Port)));
        TestProfile = Services.Profiles.Find("Test")!;
    }

    public TestEnvironment Environment { get; }

    public AppServices Services => Environment.Services;

    public FakeAppShell Shell { get; }

    public FakeListServer Server { get; }

    public StartController Controller { get; }

    public ListServerProfile TestProfile { get; }

    /// <summary>What the program was asked to connect.</summary>
    public List<ServerChoice> Choices { get; } = new();

    /// <summary>The program's answer (connected by default).</summary>
    public Func<ServerChoice, Task<ConnectResult>> Connect { get; set; } = _ => Task.FromResult(ConnectResult.Ok);

    /// <summary>Saves an account and returns its index.</summary>
    public int SaveAccount(string name, string password, string listServer, bool directMode = false)
    {
        Assert.Equal(AccountSaveStatus.Saved, Services.Accounts.Update(string.Empty, name, password, password.Length == 0, listServer, directMode));
        return Services.Accounts.IndexOfName(name);
    }

    /// <summary>Shows the login window at its natural size.</summary>
    public LoginWindow ShowLogin()
    {
        Controller.ShowLogin();
        TestUi.Settle();
        Assert.True(Controller.IsLoginVisible);
        return Controller.Login;
    }

    /// <summary>Waits for the primary server list to have its servers.</summary>
    public async Task<ServerListWindow> WaitForServersAsync(int count = 3)
    {
        await TestUi.WaitUntilAsync(() => Controller.ServerList is { } list && list.Rows.Count == count && !list.IsLoggingIn, TimeSpan.FromSeconds(5), "the server list");
        return Controller.ServerList!;
    }

    /// <summary>A session of the program, as the program would create it.</summary>
    public SessionContext CreateSession(string account = "Staff", string password = "staff-pw", string listServer = "Test")
    {
        var login = new SessionLogin(account, password, "StaffNick", Server.Servers[0], listServer);
        var context = new SessionContext(Services, Shell, new ToolRegistry(), new FakeRcSession(account), login);
        Shell.Add(context);
        return context;
    }

    /// <summary>The natural width of a window's content (measured without a width limit).</summary>
    public static double NaturalWidth(Window window)
    {
        var content = (Control)window.Content!;
        content.Measure(Size.Infinity);
        double width = content.DesiredSize.Width;
        content.InvalidateMeasure();
        TestUi.Settle();
        return width;
    }

    public void Dispose()
    {
        Controller.Dispose();
        foreach (SessionContext session in Shell.Sessions.ToArray()) session.End();
        Server.Dispose();
        Environment.Dispose();
    }
}
