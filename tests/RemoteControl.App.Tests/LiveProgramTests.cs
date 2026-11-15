using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Extensions;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.Start;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.App.Tests;

/// <summary>
/// The assembled program against a real network, without a screen: the real groups of windows, the real start controller and
/// real connections. It runs only when <c>RC_LIVE_LISTSERVER</c> (host:port), <c>RC_LIVE_ACCOUNT</c> and
/// <c>RC_LIVE_PASSWORD_FILE</c> (a file holding the password) are set, and joins the server named by <c>RC_LIVE_SERVER</c> (or
/// the first). Use it with a test network of your own; it never runs by itself.
/// </summary>
public sealed class LiveProgramTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [AvaloniaFact]
    public async Task The_assembled_program_logs_in_chats_and_opens_its_windows()
    {
        string? listServer = Environment.GetEnvironmentVariable("RC_LIVE_LISTSERVER");
        string? account = Environment.GetEnvironmentVariable("RC_LIVE_ACCOUNT");
        string? passwordFile = Environment.GetEnvironmentVariable("RC_LIVE_PASSWORD_FILE");
        if (string.IsNullOrEmpty(listServer) || string.IsNullOrEmpty(account) || string.IsNullOrEmpty(passwordFile)) return;

        string[] endpoint = listServer.Split(':');
        string password = (await File.ReadAllTextAsync(passwordFile)).Trim();
        using var environment = new TestEnvironment(options: o => o with { AutoReconnectNc = true, GraphicalMenu = true });
        AppServices services = environment.Services;

        // A list-server profile of the user's own beside the built-in Alpaxis one, and an account saved for it.
        var profile = new ListServerProfile("Live test", endpoint[0], int.Parse(endpoint[1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(ProfileChangeStatus.Saved, services.Profiles.Add(profile));
        Assert.Equal(ListServerProfile.DefaultName, services.Profiles.Profiles[0].Name);
        services.Accounts.Save(account, password, false, profile.Association);

        var tools = new ToolRegistry();
        var shell = new AppShell(services, tools, options => new RcSession(options), new FakeDesktop(), new AppShellSettings
        {
            Confirm = (_, _) => Task.FromResult(true),
            Post = action => Dispatcher.UIThread.Post(action),
        });
        using var extensions = new ExtensionsHost(services, shell);
        shell.SetService<IExtensionActions>(extensions);
        GroupFeatures.RegisterAll(tools, extensions);
        using var start = new StartController(services, shell, shell.ConnectAsync);
        shell.AttachStart(new StartWindows(start));

        // Log in at the list server and pick the server, as the login and server-list windows do.
        ConnectResult login = await start.SubmitLoginAsync(account, "LiveTest").WaitAsync(Wait);
        Assert.True(login.Success, login.Error);
        Assert.NotEmpty(start.Servers);
        string? wanted = Environment.GetEnvironmentVariable("RC_LIVE_SERVER");
        string server = start.Servers.FirstOrDefault(name => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) ?? start.Servers[0];
        ConnectResult joined = await start.ConnectAsync(server).WaitAsync(Wait);
        Assert.True(joined.Success, joined.Error);

        SessionContext session = Assert.Single(shell.Sessions);
        ISessionChat chat = Assert.IsAssignableFrom<ISessionChat>(session.Services.Get<ISessionChat>());
        Window main = Assert.IsAssignableFrom<Window>(chat.Window);
        ChatView view = main.GetVisualDescendants().OfType<ChatView>().First();
        Assert.NotNull(session.Services.Get<IPrivateMessages>());
        Assert.NotNull(session.Services.Get<IScriptEditors>());

        // The server's greeting, then two RC commands of the server typed into the chat.
        await TestUi.WaitUntilAsync(() => view.Text.Contains("Welcome", StringComparison.OrdinalIgnoreCase), Wait);
        Assert.True(chat.Send("/stats"));
        await TestUi.WaitUntilAsync(() => view.Text.Contains("GServer statistics:", StringComparison.Ordinal), Wait);
        Assert.True(chat.Send("/scriptscan all function"));
        await TestUi.WaitUntilAsync(() => view.Text.Contains("Script scan done:", StringComparison.Ordinal), Wait);

        // The NPC-Control connects by itself; then the weapon list and one weapon's script in an editor.
        await TestUi.WaitUntilAsync(() => session.Session.IsNcAuthenticated, Wait);
        await TestUi.WaitUntilAsync(() => session.Session.Weapons.Count > 0, Wait);
        Window? weapons = session.OpenTool(ToolIds.Weapons);
        Assert.NotNull(weapons);
        string weapon = session.Session.Weapons[0].Name;
        Window? editor = session.OpenTool(ToolIds.ScriptEditor, new ScriptRequest(RemoteControl.Ui.Shell.ScriptKind.Weapon, weapon));
        Assert.NotNull(editor);
        IScriptEditors editors = session.Services.Get<IScriptEditors>()!;
        await TestUi.WaitUntilAsync(() => editors.Editors.Any(open => open.Name == weapon && open.Text.Length > 0 && !open.Text.StartsWith("Loading", StringComparison.Ordinal)), Wait);

        // The player list and the file browser with the server's folders.
        Window? players = session.OpenTool(ToolIds.PlayerList);
        Assert.NotNull(players);
        Window? files = session.OpenTool(ToolIds.FileBrowser);
        Assert.NotNull(files);
        await TestUi.WaitUntilAsync(() => session.Session.FileBrowser.Folders.Count > 0, Wait);

        Shots.Save(main, "live-main");
        Shots.Save(weapons!, "live-weapons");
        Shots.Save(editor!, "live-editor");
        Shots.Save(players!, "live-players");
        Shots.Save(files!, "live-files");
        Console.WriteLine("Live: joined " + server + "; weapons " + session.Session.Weapons.Count + "; folders " + session.Session.FileBrowser.Folders.Count + "; players " + session.Session.Players.Count);

        // Nothing went wrong on the way, and signing out leaves nothing behind.
        Assert.DoesNotContain(environment.Log.Snapshot(), line => line.Contains("Exception", StringComparison.Ordinal));
        shell.SignOut(session);
        Assert.Empty(shell.Sessions);
        Assert.True(session.IsEnded);
    }
}
