using Avalonia.Controls;
using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.TestKit;

/// <summary>
/// A server session for window tests: a <see cref="TestEnvironment"/> (temporary user folder, the program's pictures, the theme),
/// a <see cref="FakeRcSession"/> that records what windows ask of it and lets the test raise any event, a
/// <see cref="FakeAppShell"/>, an empty <see cref="ToolRegistry"/> and the <see cref="SessionContext"/> over them.
/// </summary>
/// <example>
/// <code>
/// [AvaloniaFact]
/// public void Chat_line_is_shown()
/// {
///     using var test = new TestSession();
///     var window = test.Show(new MainWindow(test.Context));
///     test.Fake.RaiseRcChat("Server: hello");
///     Assert.Contains("hello", window.Chat.GetText());
///     Shots.Save(window, "main-chat");
/// }
/// </code>
/// </example>
public sealed class TestSession : IDisposable
{
    /// <summary>Creates the session.</summary>
    /// <param name="theme">The theme, or null for the default.</param>
    /// <param name="server">The server's name.</param>
    /// <param name="account">The account.</param>
    /// <param name="nickname">The nickname.</param>
    /// <param name="options">Changes to the default options.</param>
    public TestSession(string? theme = null, string server = "Test Server", string account = "tester", string nickname = "Tester", Func<Core.RcOptions, Core.RcOptions>? options = null)
    {
        Environment = new TestEnvironment(theme, options);
        Fake = new FakeRcSession(account);
        Shell = new FakeAppShell();
        Tools = new ToolRegistry();
        Login = new SessionLogin(account, "test-password", nickname, new ServerInfo(server, "English", "A test server", "https://example.invalid", "test", 3, "127.0.0.1", 14900), "Alpaxis");
        Context = new SessionContext(Environment.Services, Shell, Tools, Fake, Login);
        Shell.Add(Context);
    }

    /// <summary>The temporary user folder, options, theme and pictures.</summary>
    public TestEnvironment Environment { get; }

    /// <summary>The program's services (<see cref="TestEnvironment.Services"/>).</summary>
    public AppServices Services => Environment.Services;

    /// <summary>The connection: records calls, has settable caches, raises events.</summary>
    public FakeRcSession Fake { get; }

    /// <summary>The program, recording what windows asked of it.</summary>
    public FakeAppShell Shell { get; }

    /// <summary>What can be opened; empty until the test registers something.</summary>
    public ToolRegistry Tools { get; }

    /// <summary>What the session logged in with.</summary>
    public SessionLogin Login { get; }

    /// <summary>The session as windows get it.</summary>
    public SessionContext Context { get; }

    /// <summary>Shows a window at a size and lets it lay out (<see cref="TestEnvironment.Show{T}"/>), tracking it as one of the session's.</summary>
    /// <typeparam name="T">The window's type.</typeparam>
    /// <param name="window">The window.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The window.</returns>
    public T Show<T>(T window, double width = 800, double height = 600) where T : Window
    {
        Context.Windows.Track(window);
        return Environment.Show(window, width, height);
    }

    /// <summary>
    /// Registers a tool that only records that it was asked for (<see cref="OpenedTools"/>), for testing a window that opens
    /// windows of another group.
    /// </summary>
    /// <param name="toolIds">The tool names.</param>
    public void RecordTools(params string[] toolIds)
    {
        foreach (string toolId in toolIds)
        {
            Tools.Register(toolId, (_, argument) =>
            {
                OpenedTools.Add((toolId, argument));
                return null;
            });
        }
    }

    /// <summary>The tools asked for through <see cref="RecordTools"/>, with their arguments.</summary>
    public List<(string ToolId, object? Argument)> OpenedTools { get; } = [];

    /// <inheritdoc />
    public void Dispose()
    {
        Context.End();
        Environment.Dispose();
    }
}

/// <summary>The program as window tests see it: keeps the sessions and records every request.</summary>
public sealed class FakeAppShell : IAppShell
{
    private readonly List<SessionContext> _sessions = [];
    private readonly Dictionary<Type, object> _services = [];
    private SessionContext? _active;

    /// <summary>What was asked, in order: <c>"ShowLogin"</c>, <c>"ShowServerList"</c>, <c>"SignOut"</c>, <c>"Quit"</c>, <c>"Reconnect"</c>, <c>"ShowListServerSettings"</c>, <c>"Attention:Alert"</c>...</summary>
    public List<string> Requests { get; } = [];

    /// <summary>How often <see cref="NoteActivity"/> was called.</summary>
    public int ActivityCount { get; private set; }

    /// <summary>What <see cref="ReconnectAsync"/> answers; by default it fails with "not connected in tests".</summary>
    public Func<SessionContext, Task<ConnectResult>> Reconnect { get; set; } = _ => Task.FromResult(ConnectResult.Failed("not connected in tests"));

    /// <inheritdoc />
    public IReadOnlyList<SessionContext> Sessions => _sessions;

    /// <inheritdoc />
    public SessionContext? ActiveSession
    {
        get => _active;
        set
        {
            _active = value;
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public event EventHandler? SessionsChanged;

    /// <summary>Adds a session and makes it the active one.</summary>
    /// <param name="session">The session.</param>
    public void Add(SessionContext session)
    {
        _sessions.Add(session);
        ActiveSession = session;
    }

    /// <summary>Offers a program-wide service for <see cref="GetService{T}"/>.</summary>
    /// <typeparam name="T">The interface.</typeparam>
    /// <param name="service">The object.</param>
    public void Provide<T>(T service) where T : class => _services[typeof(T)] = service;

    /// <inheritdoc />
    public bool TrayAvailable { get; set; } = true;

    /// <inheritdoc />
    public void ShowLogin() => Requests.Add("ShowLogin");

    /// <inheritdoc />
    public void ShowServerList(SessionContext? switchFor) => Requests.Add("ShowServerList");

    /// <inheritdoc />
    public void ShowListServerSettings(Window? owner) => Requests.Add("ShowListServerSettings");

    /// <inheritdoc />
    public Task<ConnectResult> ReconnectAsync(SessionContext session)
    {
        Requests.Add("Reconnect");
        return Reconnect(session);
    }

    /// <inheritdoc />
    public void SignOut(SessionContext session)
    {
        Requests.Add("SignOut");
        _sessions.Remove(session);
        if (ReferenceEquals(_active, session)) _active = _sessions.LastOrDefault();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Quit() => Requests.Add("Quit");

    /// <inheritdoc />
    public void NoteActivity()
    {
        ActivityCount++;
        LastActivity = Clock();
    }

    /// <summary>The time source for <see cref="LastActivity"/>; a test replaces it to control the away timer.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public void RequestAttention(SessionContext session, AttentionKind kind) => Requests.Add("Attention:" + kind);

    /// <inheritdoc />
    public T? GetService<T>() where T : class => _services.TryGetValue(typeof(T), out object? service) ? (T)service : null;
}
