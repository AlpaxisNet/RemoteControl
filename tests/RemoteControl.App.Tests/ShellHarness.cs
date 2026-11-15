using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Extensions;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.App.Tests;

/// <summary>An <see cref="AppShell"/> over a temporary user folder, every real group of windows and connections that are fakes.</summary>
internal sealed class ShellHarness : IDisposable
{
    public ShellHarness(Func<Core.RcOptions, Core.RcOptions>? options = null)
    {
        Environment = new TestEnvironment(options: options);
        Tools = new ToolRegistry();
        Shell = new AppShell(Environment.Services, Tools, Create, Desktop, new AppShellSettings
        {
            Confirm = (title, message) =>
            {
                Questions.Add(title + ": " + message);
                return Task.FromResult(ConfirmAnswer);
            },
            Post = action => action(),
            Clock = () => Now,
        });
        Extensions = new ExtensionsHost(Environment.Services, Shell);
        Shell.SetService<IExtensionActions>(Extensions);
        GroupFeatures.RegisterAll(Tools, Extensions);
        Shell.AttachStart(Start);
    }

    public TestEnvironment Environment { get; }

    public ToolRegistry Tools { get; }

    public AppShell Shell { get; }

    public ExtensionsHost Extensions { get; }

    public FakeDesktop Desktop { get; } = new();

    public FakeStartWindows Start { get; } = new();

    /// <summary>Every connection the shell made, in order.</summary>
    public List<FakeRcSession> Made { get; } = [];

    /// <summary>The options of every connection made.</summary>
    public List<RcSessionOptions> MadeWith { get; } = [];

    /// <summary>What the next connection's connect returns.</summary>
    public Queue<ConnectResult> NextResults { get; } = new();

    public List<string> Questions { get; } = [];

    public bool ConfirmAnswer { get; set; } = true;

    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static SessionLogin Login(string server, string account = "staff") =>
        new(account, "secret", "Staff", new ServerInfo(server, "English", "A server", string.Empty, "6", 3, "127.0.0.1", 14900), "Alpaxis");

    public Task<ConnectResult> ConnectAsync(string server, SessionContext? switchFor = null) => Shell.ConnectAsync(new ServerChoice(Login(server), switchFor));

    public async Task<SessionContext> ConnectedAsync(string server)
    {
        ConnectResult result = await ConnectAsync(server).ConfigureAwait(true);
        Assert.True(result.Success, result.Error);
        return Shell.Sessions[^1];
    }

    public void Dispose()
    {
        foreach (SessionContext session in Shell.Sessions.ToArray()) session.End();
        Extensions.Dispose();
        Environment.Dispose();
    }

    private IRcSession Create(RcSessionOptions options)
    {
        var fake = new FakeRcSession(options.Account);
        if (NextResults.TryDequeue(out ConnectResult? result)) fake.ConnectResult = result;
        Made.Add(fake);
        MadeWith.Add(options);
        return fake;
    }
}
