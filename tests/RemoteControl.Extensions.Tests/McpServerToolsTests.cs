using System.Text;
using System.Text.Json;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Protocol.Text;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Extensions.Tests;

/// <summary>
/// An NC of a fake session: weapons, classes and NPCs it answers requests for and stores writes into. As with the real session, the
/// session's weapon list changes only when the list is asked for again: adding or deleting a weapon does not touch it.
/// </summary>
internal sealed class FakeNc
{
    private readonly FakeRcSession _session;

    public FakeNc(FakeRcSession session)
    {
        _session = session;
        session.IsNcConnected = true;
        session.IsNcAuthenticated = true;
        session.NcServerAddress = "127.0.0.1,14901";
    }

    public Dictionary<string, (string Image, string Script)> Weapons { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Classes { get; } = new(StringComparer.Ordinal);

    public Dictionary<int, (string Name, string Script)> Npcs { get; } = new();

    /// <summary>Fills the session's lists from the dictionaries.</summary>
    public void Publish()
    {
        _session.WeaponList.Clear();
        _session.WeaponList.AddRange(Weapons.Select(pair => new RcWeapon(pair.Key, string.Empty, string.Empty)));
        _session.ClassList.Clear();
        _session.ClassList.AddRange(Classes.Keys.Select(name => new RcClass(name, string.Empty)));
        _session.NpcList.Clear();
        _session.NpcList.AddRange(Npcs.Select(pair => new RcNpc(pair.Key, pair.Value.Name, "OBJECT", "start.nw", string.Empty)));
    }

    public void Handle(RecordedCall call)
    {
        switch (call.Method)
        {
            case nameof(IRcSession.RequestWeaponListAsync):
                _session.RaiseWeaponList(Weapons.Select(pair => new RcWeapon(pair.Key, string.Empty, string.Empty)).ToArray());
                break;
            case nameof(IRcSession.RequestWeaponScriptAsync):
                if (Weapons.TryGetValue(call.Argument<string>(0), out var weapon)) _session.RaiseScript(ScriptKind.Weapon, call.Argument<string>(0), weapon.Script, image: weapon.Image);
                break;
            case nameof(IRcSession.RequestClassScriptAsync):
                if (Classes.TryGetValue(call.Argument<string>(0), out string? script)) _session.RaiseScript(ScriptKind.Class, call.Argument<string>(0), script);
                break;
            case nameof(IRcSession.RequestNpcScriptAsync):
                if (Npcs.TryGetValue(call.Argument<int>(0), out var npc)) _session.RaiseScript(ScriptKind.Npc, npc.Name, npc.Script, call.Argument<int>(0));
                break;
            case nameof(IRcSession.UpdateWeaponAsync) or nameof(IRcSession.AddWeaponAsync):
            {
                string name = call.Argument<string>(0);
                bool added = !Weapons.ContainsKey(name);
                Weapons[name] = (call.Argument<string>(1), call.Argument<string>(2));
                _session.RaiseNcChat("Weapon " + name + (added ? " added" : " updated") + " by " + _session.Account);
                break;
            }
            case nameof(IRcSession.UpdateClassAsync) or nameof(IRcSession.AddClassAsync):
            {
                string name = call.Argument<string>(0);
                bool added = !Classes.ContainsKey(name);
                Classes[name] = call.Argument<string>(1);
                if (added) _session.RaiseClassAdded(name);
                break;
            }
            case nameof(IRcSession.UpdateNpcScriptAsync):
                Npcs[call.Argument<int>(0)] = (Npcs.TryGetValue(call.Argument<int>(0), out var old) ? old.Name : string.Empty, call.Argument<string>(1));
                break;
            case nameof(IRcSession.DeleteWeaponAsync):
                Weapons.Remove(call.Argument<string>(0));
                break;
            case nameof(IRcSession.DeleteClassAsync):
                Classes.Remove(call.Argument<string>(0));
                _session.RaiseClassDeleted(call.Argument<string>(0));
                break;
        }
    }
}

/// <summary>A bridge with a UI thread and fake sessions whose requests are answered as a server would.</summary>
internal sealed class ServerRig : IAsyncDisposable
{
    private readonly List<Action<RecordedCall>> _handlers = new();

    public ServerRig(Func<RcOptions, RcOptions>? options = null)
    {
        Folder = new TempFolder();
        Ui = new UiThread();
        Setup = new BridgeSetup(Folder, options ?? (o => o with { McpEnabled = true, McpFullControl = true, McpApprove = false }), Ui);
        (Fake, Nc) = AddSession("Alpaxis");
    }

    public TempFolder Folder { get; }

    public UiThread Ui { get; }

    public BridgeSetup Setup { get; }

    public FakeRcSession Fake { get; }

    public FakeNc Nc { get; }

    /// <summary>Requests that arrived off the UI thread.</summary>
    public int WrongThread;

    public (FakeRcSession Session, FakeNc Nc) AddSession(string server)
    {
        var session = new FakeRcSession("Staff");
        var nc = new FakeNc(session);
        session.OnCall = call =>
        {
            if (!Ui.IsCurrent) Interlocked.Increment(ref WrongThread);
            nc.Handle(call);
            foreach (Action<RecordedCall> handler in _handlers.ToArray()) handler(call);
        };
        Ui.InvokeAsync(() => Setup.Host.AddSession(server, session)).Wait();
        return (session, nc);
    }

    /// <summary>Answers a request of every session.</summary>
    public void Answer(string method, Action<RecordedCall> answer) => _handlers.Add(call =>
    {
        if (call.Method == method) answer(call);
    });

    /// <summary>Gives a session's MCP file browser folders and files (paths <c>folder/name</c>) and the server's upload answers.</summary>
    public static void InstallFiles(FakeRcSession session, IEnumerable<FileBrowserFolder> rules, params (string Path, byte[] Content)[] files)
    {
        FakeFileBrowser browser = session.FakeMcpFileBrowser;
        browser.ServerAnswersUploads = true;
        foreach ((string path, byte[] content) in files) browser.RemoteFiles[path] = content;
        FileBrowserFolder[] folders = rules.ToArray();
        browser.OnCall = call =>
        {
            if (call.Method == "McpFileBrowser.StartAsync") browser.RaiseFoldersReceived(folders);
            else if (call.Method == "McpFileBrowser.ChangeFolderAsync")
            {
                string folder = call.Argument<string>(0);
                browser.RaiseFilesReceived(folder, browser.RemoteFiles.Where(pair => pair.Key.StartsWith(folder, StringComparison.Ordinal) && !pair.Key[folder.Length..].Contains('/'))
                    .Select(pair => new FileBrowserEntry(pair.Key[folder.Length..], "rw", pair.Value.Length, 1_700_000_000)).ToArray());
            }
        };
    }

    public Task OnUi(Action action) => Ui.InvokeAsync(action);

    public Task<JsonElement> CallAsync(string tool, string arguments = "{}") => Setup.ToolAsync(tool, arguments);

    /// <summary>Calls a tool that must succeed and returns its result object.</summary>
    public async Task<JsonElement> OkAsync(string tool, string arguments = "{}")
    {
        string text = BridgeSetup.ToolText(await CallAsync(tool, arguments));
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Calls a tool that must fail and returns its message.</summary>
    public async Task<string> FailAsync(string tool, string arguments = "{}") => BridgeSetup.ToolText(await CallAsync(tool, arguments), expectError: true);

    public string[] BackupFiles() => Directory.Exists(Setup.Paths.McpBackupDirectory) ? Directory.GetFiles(Setup.Paths.McpBackupDirectory, "*.data", SearchOption.AllDirectories) : Array.Empty<string>();

    public async ValueTask DisposeAsync()
    {
        await Setup.DisposeAsync();
        Ui.Dispose();
        Folder.Dispose();
    }
}

public sealed class McpServerToolsTests
{
    private static RcPlayer Player(int id, string account, string nickname, bool external = false) =>
        new(id, account, nickname, "start.nw", external ? PlayerListCategory.External : PlayerListCategory.None, -1, string.Empty, new Dictionary<int, string> { [0] = nickname });

    [Fact]
    public async Task Session_info_events_and_commands()
    {
        await using var rig = new ServerRig();
        JsonElement info = await rig.OkAsync("rc_session_info");
        Assert.Equal("Alpaxis", info.GetProperty("server").GetString());
        Assert.True(info.GetProperty("nc").GetProperty("authenticated").GetBoolean());

        await rig.OnUi(() => rig.Fake.RaiseRcChat("Server: hello"));
        JsonElement events = await rig.OkAsync("rc_read_events");
        JsonElement first = Assert.Single(events.GetProperty("events").EnumerateArray());
        Assert.Equal("rc", first.GetProperty("kind").GetString());
        Assert.Equal("Server: hello", first.GetProperty("text").GetString());
        long next = events.GetProperty("next").GetInt64();

        rig.Answer(nameof(IRcSession.SendRcChatAsync), call => rig.Fake.RaiseRcChat("Found: " + call.Argument<string>(0)));
        JsonElement command = await rig.OkAsync("rc_command", """{"text":"/find bob","collectMilliseconds":50}""");
        Assert.Contains(command.GetProperty("events").EnumerateArray(), item => item.GetProperty("text").GetString() == "Found: /find bob");

        // Waiting: an event that arrives while the client waits ends the wait.
        long after = command.GetProperty("next").GetInt64();
        _ = Task.Delay(300).ContinueWith(_ => rig.Ui.Post(() => rig.Fake.RaiseNcChat("Script compiler output for Weapon bow: error")));
        JsonElement waited = await rig.OkAsync("rc_read_events", "{\"since\":" + after + ",\"kinds\":[\"nc\"],\"waitSeconds\":5}");
        Assert.Equal("Script compiler output for Weapon bow: error", Assert.Single(waited.GetProperty("events").EnumerateArray()).GetProperty("text").GetString());
        Assert.True(next < after);

        Assert.Equal("Connected RC server session not found: Nowhere (rc_list_connections lists them).", await rig.FailAsync("rc_session_info", """{"server":"Nowhere"}"""));
        Assert.StartsWith("Unknown event kind", await rig.FailAsync("rc_read_events", """{"kinds":["weather"]}"""));
        Assert.Equal(0, rig.WrongThread);
    }

    [Fact]
    public async Task The_server_scope_applies_to_the_named_session()
    {
        await using var rig = new ServerRig(o => o with { McpEnabled = true, McpFullControl = true, McpApprove = false, McpServerScope = "alpaxis" });
        rig.AddSession("Other");
        Assert.Equal("Alpaxis", (await rig.OkAsync("rc_session_info")).GetProperty("server").GetString());
        Assert.Equal((-32003, "Current server is outside configured MCP server scope"), BridgeSetup.ErrorOf(await rig.CallAsync("rc_session_info", """{"server":"Other"}""")));
        Assert.Contains(rig.Setup.AuditLines(), line => line.Contains("\"tool\":\"rc_session_info\",\"outcome\":\"server-scope-denied\"", StringComparison.Ordinal));
        Assert.Contains("outside the configured MCP server scope", await rig.FailAsync("rc_copy", """{"from":"Other","weapons":["bow"]}"""));
    }

    [Fact]
    public async Task Players_and_everything_about_one()
    {
        await using var rig = new ServerRig();
        rig.Fake.PlayerList.AddRange(new[] { Player(1, "bob", "Bobby"), Player(2, "ann", "Ann", external: true) });
        JsonElement list = await rig.OkAsync("rc_list_players", """{"includeExternal":false}""");
        Assert.Equal(1, list.GetProperty("count").GetInt32());
        Assert.Equal("bob", list.GetProperty("players")[0].GetProperty("account").GetString());

        rig.Answer(nameof(IRcSession.RequestAccountAsync), call => rig.Fake.RaiseAccount(new AccountData { Account = "Bob", Email = "bob@example.test", AdminLevel = 2 }));
        rig.Answer(nameof(IRcSession.RequestPlayerRightsAsync), call => rig.Fake.RaisePlayerRights("bob", new PlayerRights(3, "*", "rw levels/*\nr images/*")));
        rig.Answer(nameof(IRcSession.RequestPlayerCommentsAsync), call => rig.Fake.RaisePlayerComments("bob", "helpful"));
        JsonElement info = await rig.OkAsync("rc_get_player_info", """{"id":1,"parts":["player","account","rights","comments","ban"],"timeoutSeconds":1}""");
        Assert.Equal("bob", info.GetProperty("account").GetString());
        Assert.Equal(1, info.GetProperty("player").GetProperty("id").GetInt32());
        Assert.Equal("Bobby", info.GetProperty("player").GetProperty("properties").GetProperty("0").GetString());
        JsonElement parts = info.GetProperty("parts");
        Assert.Equal("bob@example.test", parts.GetProperty("account").GetProperty("email").GetString());
        Assert.Contains("bob@example.test", parts.GetProperty("account").GetProperty("text").GetString());
        Assert.Equal(new[] { "rw levels/*", "r images/*" }, parts.GetProperty("rights").GetProperty("folders").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("Folder Access:", parts.GetProperty("rights").GetProperty("text").GetString());
        Assert.Equal("helpful", parts.GetProperty("comments").GetProperty("text").GetString());
        Assert.False(parts.TryGetProperty("ban", out _));
        Assert.Contains("did not answer", info.GetProperty("errors").GetProperty("ban").GetString());
        Assert.StartsWith("Unknown part", await rig.FailAsync("rc_get_player_info", """{"account":"bob","parts":["shoes"]}"""));
        Assert.StartsWith("No player has the id 9", await rig.FailAsync("rc_get_player_info", """{"id":9}"""));
    }

    [Fact]
    public async Task Player_texts_are_written_back_after_a_backup()
    {
        await using var rig = new ServerRig();
        rig.Answer(nameof(IRcSession.RequestPlayerRightsAsync), call => rig.Fake.RaisePlayerRights("bob", new PlayerRights(1 << 20, "*", "r levels/*")));
        string text = PlayerRightsText.Format(new PlayerRights(0, "10.0.*", "rw levels/*\nrw images/*"));
        JsonElement written = await rig.OkAsync("rc_write_player_info", JsonSerializer.Serialize(new { account = "bob", part = "rights", text, collectMilliseconds = 0 }));
        Assert.True(written.GetProperty("ok").GetBoolean());
        PlayerRights sent = rig.Fake.LastCall(nameof(IRcSession.SetPlayerRightsAsync))!.Argument<PlayerRights>(1);
        Assert.Equal("10.0.*", sent.IpRange);
        Assert.Equal("rw levels/*\nrw images/*", sent.Folders);
        Assert.True(sent.Has(20), "a right without a name in the text is kept from the current rights");
        string backup = Assert.Single(rig.BackupFiles());
        Assert.Contains("r levels/*", File.ReadAllText(backup));

        // Without the current attributes nothing is reset (they could not be backed up), unless force is given.
        Assert.Contains("could not be read for its backup", await rig.FailAsync("rc_write_player_info", """{"account":"bob","part":"attributes","reset":true,"timeoutSeconds":1}"""));
        Assert.False(rig.Fake.WasCalled(nameof(IRcSession.ResetPlayerAsync)));
        await rig.OkAsync("rc_write_player_info", """{"account":"bob","part":"attributes","reset":true,"collectMilliseconds":0,"timeoutSeconds":1,"force":true}""");
        Assert.Equal("bob", rig.Fake.LastCall(nameof(IRcSession.ResetPlayerAsync))!.Argument<string>(0));
        Assert.StartsWith("part must be one of", await rig.FailAsync("rc_write_player_info", """{"account":"bob","part":"ban","text":"x"}"""));
    }

    [Fact]
    public async Task Messages_bans_and_player_actions()
    {
        await using var rig = new ServerRig();
        rig.Fake.PlayerList.AddRange(new[] { Player(1, "bob", "Bobby"), Player(2, "ann", "Ann", external: true) });
        await rig.OkAsync("rc_send_message", """{"accounts":["BOB"],"message":"hi","collectMilliseconds":0}""");
        Assert.Equal(1, rig.Fake.LastCall(nameof(IRcSession.SendPrivateMessageAsync))!.Argument<int>(0));
        await rig.OkAsync("rc_send_message", """{"ids":[1,2],"message":"all of you","collectMilliseconds":0}""");
        Assert.Equal(new[] { 1, 2 }, rig.Fake.LastCall(nameof(IRcSession.SendMassPrivateMessageAsync))!.Argument<IReadOnlyList<int>>(0));
        await rig.OkAsync("rc_send_message", """{"kind":"admin","all":true,"message":"restart soon","collectMilliseconds":0}""");
        Assert.True(rig.Fake.WasCalled(nameof(IRcSession.SendAdminMessageToAllAsync)));
        await rig.OkAsync("rc_send_message", """{"kind":"toall","message":"event!","collectMilliseconds":0}""");
        Assert.Equal("event!", rig.Fake.LastCall(nameof(IRcSession.SendToAllAsync))!.Argument<string>(0));

        await rig.OkAsync("rc_player_action", """{"action":"warp","account":"bob","level":"jail.nw","x":30,"y":31.5,"collectMilliseconds":0}""");
        RecordedCall warp = rig.Fake.LastCall(nameof(IRcSession.WarpPlayerAsync))!;
        Assert.Equal((1, "jail.nw", 30.0, 31.5), (warp.Argument<int>(0), warp.Argument<string>(1), warp.Argument<double>(2), warp.Argument<double>(3)));
        Assert.Contains("is on another server", await rig.FailAsync("rc_player_action", """{"action":"disconnect","account":"ann"}"""));

        await rig.OkAsync("rc_set_ban", """{"target":"bob","type":"cheating","releaseSeconds":3600,"reason":"speed hack","collectMilliseconds":0}""");
        RecordedCall ban = rig.Fake.LastCall(nameof(IRcSession.SetBanAsync))!;
        Assert.Equal(new object?[] { "bob", "local", true, "cheating", "3600", "speed hack" }, ban.Arguments);
        Assert.StartsWith("A ban needs a type", await rig.FailAsync("rc_set_ban", """{"target":"bob"}"""));
        await rig.OkAsync("rc_set_ban", """{"target":"bob","banned":false,"world":"all","collectMilliseconds":0}""");
        Assert.Equal(new object?[] { "bob", "all", false, string.Empty, string.Empty, string.Empty }, rig.Fake.LastCall(nameof(IRcSession.SetBanAsync))!.Arguments);
    }

    [Fact]
    public async Task A_ban_on_every_server_is_always_asked_and_IRC_and_other_servers_are_reached()
    {
        await using var rig = new ServerRig();
        rig.Setup.Host.Approve = false;
        Assert.Equal((-32002, "Mutation denied by user"), BridgeSetup.ErrorOf(await rig.CallAsync("rc_set_ban", """{"target":"bob","world":"all","noEnd":true}""")));
        Assert.False(rig.Fake.WasCalled(nameof(IRcSession.SetBanAsync)));
        Assert.Contains(rig.Setup.Host.Calls, call => call.StartsWith("approve Ban bob on every server of the network", StringComparison.Ordinal));
        rig.Setup.Host.Approve = true;
        await rig.OkAsync("rc_set_ban", """{"target":"bob","world":"all","noEnd":true,"collectMilliseconds":0}""");
        Assert.Equal("0", rig.Fake.LastCall(nameof(IRcSession.SetBanAsync))!.Argument<string>(4));

        rig.Answer(nameof(IRcSession.IrcJoinAsync), call => rig.Fake.RaiseIrcMessage(call.Argument<string>(0), "* Staff joined"));
        JsonElement joined = await rig.OkAsync("rc_irc", """{"action":"join","channel":"#graal","collectMilliseconds":50}""");
        JsonElement line = Assert.Single(joined.GetProperty("events").EnumerateArray());
        Assert.Equal(("irc", "#graal"), (line.GetProperty("kind").GetString(), line.GetProperty("from").GetString()));
        await rig.OkAsync("rc_irc", """{"action":"send","channel":"#graal","text":"hello","collectMilliseconds":0}""");
        Assert.Equal(new[] { "#graal", "hello" }, rig.Fake.LastCall(nameof(IRcSession.SendIrcTextAsync))!.Argument<string[]>(1));
        Assert.Contains("needs a channel", await rig.FailAsync("rc_irc", """{"action":"join"}"""));

        rig.Answer(nameof(IRcSession.RequestPmServerPlayersAsync), call => rig.Fake.RaisePlayerJoined(Player(70, "far", "Far Away", external: true)));
        JsonElement players = await rig.OkAsync("rc_list_players", """{"pmServer":"Elsewhere"}""");
        Assert.Equal("far", Assert.Single(players.GetProperty("players").EnumerateArray()).GetProperty("account").GetString());
        Assert.Equal("Elsewhere", rig.Fake.LastCall(nameof(IRcSession.RequestPmServerPlayersAsync))!.Argument<string>(0));
    }

    [Fact]
    public async Task Server_commands_scriptscan_and_scripthelp_gather_the_servers_answer()
    {
        await using var rig = new ServerRig();
        bool answerHelp = false;
        rig.Answer(nameof(IRcSession.SendRcChatAsync), call =>
        {
            string command = call.Argument<string>(0);
            if (command == "/help" && answerHelp)
            {
                rig.Fake.RaiseRcChat("Available commands for GServer:");
                rig.Fake.RaiseRcChat("/stats: Shows the statistics of the server and of its scripts.");
                rig.Fake.RaiseRcChat("/find file: Finds a file.  Accepts wildcards.");
            }
            else if (command.StartsWith("/scriptscan ", StringComparison.Ordinal))
            {
                rig.Fake.RaiseRcChat("levels/start.nw (npc 3): this.chest = 1;");
                _ = Task.Delay(200).ContinueWith(_ => rig.Ui.Post(() => rig.Fake.RaiseRcChat("weapons/-Chest: this.chest = 0;")));
            }
            else if (command == "/scripthelp showimg") rig.Fake.RaiseRcChat("showimg(index, image, x, y) - shows an image");
        });

        JsonElement fallback = await rig.OkAsync("rc_server_commands", """{"collectMilliseconds":300}""");
        Assert.False(fallback.GetProperty("fromServer").GetBoolean());
        Assert.Contains(fallback.GetProperty("commands").EnumerateArray(), item => item.GetProperty("command").GetString() == "/scriptscan weapons/npcs/classes/levels/all text");

        answerHelp = true;
        JsonElement help = await rig.OkAsync("rc_server_commands");
        Assert.True(help.GetProperty("fromServer").GetBoolean());
        Assert.Equal(new[] { "/stats", "/find file" }, help.GetProperty("commands").EnumerateArray().Select(item => item.GetProperty("command").GetString()));

        JsonElement scan = await rig.OkAsync("rc_search_scripts", """{"query":"this.chest","serverSide":true,"kinds":["level"]}""");
        Assert.Equal("/scriptscan levels this.chest", scan.GetProperty("command").GetString());
        Assert.Equal(new[] { "levels/start.nw (npc 3): this.chest = 1;", "weapons/-Chest: this.chest = 0;" }, scan.GetProperty("output").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("serverSide", await rig.FailAsync("rc_search_scripts", """{"query":"x","kinds":["level"]}"""));

        JsonElement serverHelp = await rig.OkAsync("rc_script_help", """{"name":"showimg","fromServer":true}""");
        Assert.Equal("showimg(index, image, x, y) - shows an image", Assert.Single(serverHelp.GetProperty("output").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Scripts_need_the_NC()
    {
        await using var rig = new ServerRig();
        rig.Fake.IsNcAuthenticated = false;
        Assert.Contains("connect it with rc_nc_connect", await rig.FailAsync("rc_list_scripts"));
        rig.Answer(nameof(IRcSession.SendRcChatAsync), call =>
        {
            if (call.Argument<string>(0) != "/nc connect") return;
            rig.Fake.IsNcAuthenticated = true;
            rig.Fake.RaiseNcConnected();
        });
        Assert.StartsWith("The NC is connected", (await rig.OkAsync("rc_nc_connect")).GetProperty("message").GetString());
        Assert.Contains("line Alpaxis /nc connect", rig.Setup.Host.Calls);
    }

    [Fact]
    public async Task Scripts_are_listed_read_searched_written_and_deleted()
    {
        await using var rig = new ServerRig();
        rig.Nc.Weapons["bow"] = ("bow.png", "function onCreated() {\n  this.player = player;\n}\n");
        rig.Nc.Weapons["-System/Login"] = ("", "// login\n");
        rig.Nc.Classes["util"] = "function playerName() {\n  return player.account;\n}\n";
        rig.Nc.Npcs[1000] = ("door", "//#CLIENTSIDE\nfunction onPlayerTouchsMe() {}\n");
        rig.Nc.Publish();

        JsonElement list = await rig.OkAsync("rc_list_scripts");
        Assert.Equal(new[] { "-System/Login", "bow" }, list.GetProperty("weapons").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(1000, list.GetProperty("npcs")[0].GetProperty("id").GetInt32());

        JsonElement bow = await rig.OkAsync("rc_read_script", """{"kind":"weapon","name":"BOW"}""");
        Assert.Equal("bow", bow.GetProperty("name").GetString());
        Assert.Equal("bow.png", bow.GetProperty("image").GetString());
        Assert.Equal(rig.Nc.Weapons["bow"].Script, bow.GetProperty("script").GetString());
        Assert.Equal("2:   this.player = player;\n", (await rig.OkAsync("rc_read_script", """{"kind":"weapon","name":"bow","start":2,"end":2}""")).GetProperty("numbered").GetString());
        Assert.Contains("No weapon named \"axe\"", await rig.FailAsync("rc_read_script", """{"kind":"weapon","name":"axe"}"""));

        JsonElement found = await rig.OkAsync("rc_search_scripts", """{"query":"player\\b","regex":true}""");
        Assert.Equal(new[] { "bow", "util" }, found.GetProperty("results").EnumerateArray().Select(item => item.GetProperty("name").GetString()).Order());
        Assert.Equal(4, found.GetProperty("searched").GetInt32());

        // Guarded edits of the server's text keep the image; the old script is backed up; the NC's answer comes back.
        JsonElement written = await rig.OkAsync("rc_write_script", """{"kind":"weapon","name":"bow","edits":[{"expected":"this.player = player;","replacement":"this.owner = player;"}],"collectMilliseconds":50}""");
        Assert.Equal("function onCreated() {\n  this.owner = player;\n}\n", rig.Nc.Weapons["bow"].Script);
        Assert.Equal("bow.png", rig.Nc.Weapons["bow"].Image);
        Assert.Contains(written.GetProperty("events").EnumerateArray(), item => item.GetProperty("text").GetString() == "Weapon bow updated by Staff");
        Assert.False(written.GetProperty("created").GetBoolean());
        Assert.Contains("this.player = player;", File.ReadAllText(Assert.Single(rig.BackupFiles())));

        rig.Fake.ClearCalls();
        Assert.Contains("not unique", await rig.FailAsync("rc_write_script", """{"kind":"class","name":"util","expected":"player","replacement":"x"}"""));
        Assert.False(rig.Fake.WasCalled(nameof(IRcSession.UpdateClassAsync)));
        Assert.Contains("rc_delete_script", await rig.FailAsync("rc_write_script", """{"kind":"class","name":"util","script":"  \n"}"""));

        JsonElement created = await rig.OkAsync("rc_write_script", """{"kind":"weapon","name":"sword","script":"// new\r\nfunction a() {}\r\n","image":"sword.png","collectMilliseconds":0}""");
        Assert.True(created.GetProperty("created").GetBoolean());
        Assert.Equal(("sword.png", "// new\nfunction a() {}\n"), rig.Nc.Weapons["sword"]);
        Assert.Contains("create is false", await rig.FailAsync("rc_write_script", """{"kind":"weapon","name":"axe","script":"x","create":false}"""));

        // An open editor with unsaved changes of the same script is named.
        JsonElement util = await rig.OkAsync("rc_write_script", """{"kind":"class","name":"util","script":"function playerName() { return \"x\"; }\n","collectMilliseconds":0}""");
        Assert.Equal("Class: util", Assert.Single(util.GetProperty("openEditorsWithChanges").EnumerateArray()).GetString());

        await rig.OkAsync("rc_write_script", """{"kind":"npc","id":1000,"script":"//#CLIENTSIDE\n","collectMilliseconds":0}""");
        Assert.Equal("//#CLIENTSIDE\n", rig.Nc.Npcs[1000].Script);

        await rig.OkAsync("rc_delete_script", """{"kind":"weapon","name":"sword","collectMilliseconds":0}""");
        Assert.False(rig.Nc.Weapons.ContainsKey("sword"));
        Assert.Equal(4, rig.BackupFiles().Length);
        Assert.Equal(0, rig.WrongThread);
    }

    [Fact]
    public async Task Script_approvals_use_the_saved_types_and_deletes_always_ask()
    {
        await using var rig = new ServerRig(o => o with { McpEnabled = true, McpFullControl = true, McpApprove = true });
        rig.Nc.Weapons["bow"] = ("", "a\n");
        rig.Nc.Publish();
        rig.Setup.Host.ScriptApproval = McpApproval.AlwaysAllow;
        await rig.OkAsync("rc_write_script", """{"kind":"weapon","name":"bow","script":"b\n","collectMilliseconds":0}""");
        Assert.Contains(rig.Setup.Host.Calls, call => call.StartsWith("approve-script Write Weapon: bow on Alpaxis", StringComparison.Ordinal) && call.EndsWith(" Weapon", StringComparison.Ordinal));
        Assert.True(rig.Setup.Options.Current.McpApproveWeapon);
        int questions = rig.Setup.Host.ApprovalQuestions;
        await rig.OkAsync("rc_write_script", """{"kind":"weapon","name":"bow","script":"c\n","collectMilliseconds":0}""");
        Assert.Equal(questions, rig.Setup.Host.ApprovalQuestions);

        rig.Setup.Host.ScriptApproval = McpApproval.Deny;
        Assert.Equal((-32002, "Mutation denied by user"), BridgeSetup.ErrorOf(await rig.CallAsync("rc_delete_script", """{"kind":"weapon","name":"bow"}""")));
        Assert.Contains(rig.Setup.Host.Calls, call => call == "approve-script Delete Weapon: bow on Alpaxis ");
        Assert.True(rig.Nc.Weapons.ContainsKey("bow"));

        rig.Setup.Host.Approve = false;
        Assert.Equal((-32002, "Mutation denied by user"), BridgeSetup.ErrorOf(await rig.CallAsync("rc_send_message", """{"kind":"toall","message":"x"}""")));
        Assert.False(rig.Fake.WasCalled(nameof(IRcSession.SendToAllAsync)));
        Assert.Contains(rig.Setup.AuditLines(), line => line.Contains("\"tool\":\"rc_send_message\",\"outcome\":\"approval-denied\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Server_texts_are_merged_by_key_or_edited()
    {
        await using var rig = new ServerRig();
        rig.Answer(nameof(IRcSession.RequestServerFlagsAsync), _ => rig.Fake.RaiseServerFlags("server.a=1\nserver.b=2\n"));
        rig.Answer(nameof(IRcSession.RequestServerOptionsAsync), _ => rig.Fake.RaiseServerOptions("name = Test\nmaxplayers = 100\n"));
        rig.Answer(nameof(IRcSession.RequestFolderConfigAsync), _ => rig.Fake.RaiseFolderConfig("file levels/*.nw\n"));

        Assert.Equal("server.a=1\nserver.b=2\n", (await rig.OkAsync("rc_read_server_text", """{"kind":"flags"}""")).GetProperty("text").GetString());
        JsonElement flags = await rig.OkAsync("rc_write_server_text", """{"kind":"flags","set":{"server.b":"3","server.c":"4"},"remove":["server.a"],"collectMilliseconds":0}""");
        Assert.Equal("server.b=3\nserver.c=4\n", rig.Fake.LastCall(nameof(IRcSession.UploadServerFlagsAsync))!.Argument<string>(0));
        Assert.Equal(new[] { "set server.b", "added server.c", "removed server.a" }, flags.GetProperty("changes").EnumerateArray().Select(item => item.GetString()));
        Assert.NotEmpty(flags.GetProperty("backup").GetString()!);

        await rig.OkAsync("rc_write_server_text", """{"kind":"options","set":{"MAXPLAYERS":"200","motd":"hi"},"collectMilliseconds":0}""");
        Assert.Equal("name = Test\nmaxplayers = 200\nmotd = hi\n", rig.Fake.LastCall(nameof(IRcSession.UploadServerOptionsAsync))!.Argument<string>(0));

        await rig.OkAsync("rc_write_server_text", """{"kind":"folderconfig","edits":[{"expected":"file levels/*.nw","replacement":"file levels/*.nw\nfile images/*.png"}],"collectMilliseconds":0}""");
        Assert.Equal("file levels/*.nw\nfile images/*.png\n", rig.Fake.LastCall(nameof(IRcSession.UploadFolderConfigAsync))!.Argument<string>(0));
        Assert.Contains("has no keys", await rig.FailAsync("rc_write_server_text", """{"kind":"folderconfig","set":{"a":"b"}}"""));
        Assert.Contains("nothing was sent", (await rig.OkAsync("rc_write_server_text", """{"kind":"flags","text":"server.a=1\nserver.b=2\n"}""")).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Files_are_listed_read_edited_raw_and_deleted_with_backups()
    {
        await using var rig = new ServerRig();
        byte[] level = RcText.GetBytes("GLEVNW01\r\nNPC café 10 20\r\nNPCEND\r\n");
        byte[] picture = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0, 1 };
        ServerRig.InstallFiles(rig.Fake, new[] { new FileBrowserFolder("rw", "levels/*"), new FileBrowserFolder("r", "images/*.png") },
            ("levels/start.nw", level), ("images/a.png", picture));

        JsonElement folders = await rig.OkAsync("rc_list_files");
        Assert.Equal(new[] { "levels/", "images/" }, folders.GetProperty("folders").EnumerateArray().Select(item => item.GetProperty("folder").GetString()));
        JsonElement files = await rig.OkAsync("rc_list_files", """{"folder":"levels"}""");
        Assert.Equal("levels/start.nw", Assert.Single(files.GetProperty("files").EnumerateArray()).GetProperty("path").GetString());
        Assert.Empty(rig.Fake.CallsTo("FileBrowser.ChangeFolderAsync"));

        JsonElement read = await rig.OkAsync("rc_read_file", """{"path":"levels/start.nw"}""");
        Assert.Equal("windows-1252", read.GetProperty("encoding").GetString());
        Assert.Contains("NPC café 10 20", read.GetProperty("text").GetString());
        Assert.Contains("binary file", await rig.FailAsync("rc_read_file", """{"path":"images/a.png"}"""));
        Assert.Equal(Convert.ToBase64String(picture), (await rig.OkAsync("rc_read_file", """{"path":"images/a.png","encoding":"base64"}""")).GetProperty("base64").GetString());

        // A guarded edit keeps the file's encoding and line breaks; the old file is backed up; the server's answer is read.
        JsonElement edited = await rig.OkAsync("rc_write_file", """{"path":"levels/start.nw","edits":[{"expected":"café 10 20","replacement":"café 11 21"}]}""");
        Assert.True(edited.GetProperty("confirmed").GetBoolean());
        Assert.Equal(RcText.GetBytes("GLEVNW01\r\nNPC café 11 21\r\nNPCEND\r\n"), rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/start.nw"]);
        Assert.Equal(level, File.ReadAllBytes(Assert.Single(rig.BackupFiles())));
        Assert.Contains("rc_update_levels", edited.GetProperty("hint").GetString());

        Assert.Contains("refused", await rig.FailAsync("rc_write_file", """{"path":"images/b.png","base64":"AAEC"}"""));
        rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/empty.txt"] = Array.Empty<byte>();
        rig.Fake.FakeMcpFileBrowser.DownloadHandler = path => Task.FromResult(path.EndsWith("empty.txt", StringComparison.Ordinal) ? null : rig.Fake.FakeMcpFileBrowser.RemoteFiles.GetValueOrDefault(path));
        Assert.Equal(string.Empty, (await rig.OkAsync("rc_read_file", """{"path":"levels/empty.txt"}""")).GetProperty("text").GetString());
        rig.Fake.FakeMcpFileBrowser.DownloadHandler = null;
        Assert.Contains("cannot hold", await rig.FailAsync("rc_write_file", """{"path":"levels/start.nw","text":"✓","encoding":"windows-1252"}"""));
        await rig.OkAsync("rc_write_file", """{"path":"levels/new.txt","text":"new ✓"}""");
        Assert.Equal(Encoding.UTF8.GetBytes("new ✓"), rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/new.txt"]);
        Assert.Contains("is not a file path", await rig.FailAsync("rc_read_file", """{"path":"start.nw"}"""));

        await rig.OkAsync("rc_move_file", """{"path":"levels/new.txt","newName":"renamed.txt","collectMilliseconds":0}""");
        Assert.Equal(new object?[] { "levels/new.txt", "renamed.txt" }, rig.Fake.LastCall("McpFileBrowser.RenameAsync")!.Arguments);

        JsonElement found = await rig.OkAsync("rc_search_files", """{"folder":"levels/","pattern":"*.nw","query":"CAFÉ"}""");
        Assert.Equal("2: NPC café 11 21\n", Assert.Single(found.GetProperty("results").EnumerateArray()).GetProperty("matches").GetString());

        await rig.OkAsync("rc_delete_file", """{"path":"levels/start.nw","collectMilliseconds":0}""");
        Assert.False(rig.Fake.FakeMcpFileBrowser.RemoteFiles.ContainsKey("levels/start.nw"));
        Assert.Equal(2, rig.BackupFiles().Length);
        Assert.Empty(rig.Fake.CallsTo("FileBrowser.DownloadAsync"));
        Assert.Equal(0, rig.WrongThread);
    }

    [Fact]
    public async Task Scripts_and_files_are_copied_between_servers()
    {
        await using var rig = new ServerRig();
        (FakeRcSession source, FakeNc sourceNc) = rig.AddSession("Source");
        sourceNc.Weapons["bow"] = ("bow.png", "// bow from source\n");
        sourceNc.Classes["util"] = "function u() {}\n";
        sourceNc.Npcs[5] = ("door", "// door\n");
        sourceNc.Publish();
        rig.Nc.Classes["util"] = "// old util\n";
        rig.Nc.Publish();
        FileBrowserFolder[] rules = { new("rw", "levels/*"), new("rw", "images/*") };
        ServerRig.InstallFiles(source, rules, ("levels/a.nw", Encoding.ASCII.GetBytes("A")), ("levels/b.nw", Encoding.ASCII.GetBytes("B")), ("levels/c.txt", Encoding.ASCII.GetBytes("C")), ("images/x.png", new byte[] { 1, 2 }));
        ServerRig.InstallFiles(rig.Fake, rules, ("levels/b.nw", Encoding.ASCII.GetBytes("old B")));

        JsonElement copied = await rig.OkAsync("rc_copy", """{"from":"Source","weapons":["bow"],"classes":["util"],"scripts":[{"kind":"npc","id":5}],"files":["levels/*.nw","images/"],"collectMilliseconds":0}""");
        Assert.Equal(("bow.png", "// bow from source\n"), rig.Nc.Weapons["bow"]);
        Assert.Equal("function u() {}\n", rig.Nc.Classes["util"]);
        Assert.Equal("A", Encoding.ASCII.GetString(rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/a.nw"]));
        Assert.Equal("B", Encoding.ASCII.GetString(rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/b.nw"]));
        Assert.Equal(new byte[] { 1, 2 }, rig.Fake.FakeMcpFileBrowser.RemoteFiles["images/x.png"]);
        Assert.False(rig.Fake.FakeMcpFileBrowser.RemoteFiles.ContainsKey("levels/c.txt"));
        JsonElement counts = copied.GetProperty("counts");
        Assert.Equal(3, counts.GetProperty("created").GetInt32());
        Assert.Equal(2, counts.GetProperty("replaced").GetInt32());
        Assert.Equal(1, counts.GetProperty("failed").GetInt32());
        Assert.Contains(copied.GetProperty("items").EnumerateArray(), item => item.GetProperty("outcome").GetString() == "failed" && item.GetProperty("message").GetString()!.Contains("create it with rc_npc_action"));
        Assert.Equal(2, rig.BackupFiles().Length);

        JsonElement again = await rig.OkAsync("rc_copy", """{"from":"Source","weapons":["bow"],"files":["levels/a.nw"],"overwrite":false,"collectMilliseconds":0}""");
        Assert.Equal(2, again.GetProperty("counts").GetProperty("skipped").GetInt32());
        Assert.Equal(0, rig.WrongThread);
    }

    [Fact]
    public async Task Export_edit_locally_and_import_what_changed()
    {
        await using var rig = new ServerRig();
        rig.Nc.Weapons["bow"] = ("bow.png", "// bow\n");
        rig.Nc.Weapons["-System/Login"] = ("", "// login\n");
        rig.Nc.Classes["util"] = "// util\n";
        rig.Nc.Publish();
        ServerRig.InstallFiles(rig.Fake, new[] { new FileBrowserFolder("rw", "levels/*") }, ("levels/a.nw", Encoding.ASCII.GetBytes("GLEVNW01\n")));

        JsonElement exported = await rig.OkAsync("rc_export", """{"localFolder":"work","scripts":true,"files":["levels/*.nw"]}""");
        string folder = exported.GetProperty("folder").GetString()!;
        Assert.Equal(Path.Combine(rig.Setup.Paths.McpWorkspaceDirectory, "work"), folder);
        Assert.Equal("// bow\n", File.ReadAllText(Path.Combine(folder, "weapons", "bow.gs2")));
        Assert.True(File.Exists(Path.Combine(folder, "weapons", "-System%2FLogin.gs2")));
        Assert.Equal("GLEVNW01\n", File.ReadAllText(Path.Combine(folder, "files", "levels", "a.nw")));
        Assert.Equal(4, exported.GetProperty("counts").GetProperty("exported").GetInt32());

        File.WriteAllText(Path.Combine(folder, "weapons", "-System%2FLogin.gs2"), "// login v2\n");
        File.WriteAllText(Path.Combine(folder, "classes", "fresh.gs2"), "// a new class\n");
        File.WriteAllText(Path.Combine(folder, "files", "levels", "a.nw"), "GLEVNW01\nBOARD\n");

        JsonElement dry = await rig.OkAsync("rc_import", """{"localFolder":"work","dryRun":true}""");
        Assert.Equal(3, dry.GetProperty("counts").GetProperty("would-send").GetInt32());
        Assert.Equal("// login\n", rig.Nc.Weapons["-System/Login"].Script);

        JsonElement imported = await rig.OkAsync("rc_import", """{"localFolder":"work","collectMilliseconds":0}""");
        Assert.True(imported.GetProperty("ok").GetBoolean(), imported.ToString());
        Assert.Equal("// login v2\n", rig.Nc.Weapons["-System/Login"].Script);
        Assert.Equal("// a new class\n", rig.Nc.Classes["fresh"]);
        Assert.Equal("GLEVNW01\nBOARD\n", Encoding.ASCII.GetString(rig.Fake.FakeMcpFileBrowser.RemoteFiles["levels/a.nw"]));
        Assert.Equal("// bow\n", rig.Nc.Weapons["bow"].Script);
        Assert.Equal("Nothing to send (no file changed since the export).", (await rig.OkAsync("rc_import", """{"localFolder":"work"}""")).GetProperty("message").GetString());

        // Into another server everything goes.
        (FakeRcSession other, FakeNc otherNc) = rig.AddSession("Other");
        ServerRig.InstallFiles(other, new[] { new FileBrowserFolder("rw", "levels/*") });
        JsonElement elsewhere = await rig.OkAsync("rc_import", """{"localFolder":"work","server":"Other","collectMilliseconds":0}""");
        Assert.Equal(5, elsewhere.GetProperty("counts").GetProperty("created").GetInt32());
        Assert.Equal(("bow.png", "// bow\n"), otherNc.Weapons["bow"]);

        Assert.Contains("outside the folders MCP may use", await rig.FailAsync("rc_export", "{\"localFolder\":" + JsonSerializer.Serialize(Path.GetTempPath()) + "}"));
        Assert.Contains("leaves its folder", await rig.FailAsync("rc_export", """{"localFolder":"../escape"}"""));
    }

    [Fact]
    public async Task Backups_are_listed_read_and_restored()
    {
        await using var rig = new ServerRig();
        rig.Nc.Weapons["bow"] = ("bow.png", "// version 1\n");
        rig.Nc.Publish();
        string id = (await rig.OkAsync("rc_write_script", """{"kind":"weapon","name":"bow","script":"// version 2\n","collectMilliseconds":0}""")).GetProperty("backup").GetString()!;
        JsonElement list = await rig.OkAsync("rc_backups");
        Assert.Equal(id, Assert.Single(list.GetProperty("backups").EnumerateArray()).GetProperty("id").GetString());
        JsonElement one = await rig.OkAsync("rc_backups", JsonSerializer.Serialize(new { id }));
        Assert.Equal("// version 1\n", one.GetProperty("text").GetString());
        Assert.Equal("bow.png", one.GetProperty("backup").GetProperty("image").GetString());

        await rig.OkAsync("rc_restore_backup", JsonSerializer.Serialize(new { id, collectMilliseconds = 0 }));
        Assert.Equal(("bow.png", "// version 1\n"), rig.Nc.Weapons["bow"]);
        Assert.Equal(2, (await rig.OkAsync("rc_backups")).GetProperty("backups").GetArrayLength());
        Assert.Contains("No MCP backup", await rig.FailAsync("rc_backups", """{"id":"../../etc/passwd"}"""));
    }

    /// <summary>A script API file in the shape of api.gscript.dev, given as the option autocompletesource (a local path: no network).</summary>
    private static Func<RcOptions, RcOptions> WithLocalApi(TempFolder folder) => options =>
    {
        string api = folder.Write("api/scriptapi.json", """
            {
              "showimg": {"name":"showimg","type":"function","params":["index","image","x","y"],"returns":"void","scope":"clientside","description":"TServerLevel.showimg(index, imageFile, x, y) - Creates an image.\nMore text.","example":"showimg(200, \"fire.gif\", 1, 2);"},
              "findimg": {"name":"findimg","type":"function","params":["int"],"returns":"object","scope":"clientside","description":"Retrieves a TShowImg.","example":""},
              "echo": {"name":"echo","type":"function","params":["text"],"returns":"void","scope":"global","description":"Writes to the log.","example":""},
              "player.chat": {"name":"player.chat","type":"variable","params":[],"returns":"string","scope":"global","description":"What the player says.","example":""}
            }
            """);
        return options with { McpEnabled = true, McpFullControl = true, McpApprove = false, AutocompleteSource = api };
    };

    [Fact]
    public async Task Script_help_reads_the_script_api_and_the_check_uses_it()
    {
        var folder = new TempFolder();
        await using var rig = new ServerRig(WithLocalApi(folder));
        JsonElement showimg = await rig.OkAsync("rc_script_help", """{"name":"SHOWIMG"}""");
        JsonElement definition = showimg.GetProperty("definition");
        Assert.Equal("showimg", definition.GetProperty("name").GetString());
        Assert.Equal(new[] { "index", "image", "x", "y" }, definition.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("clientside", definition.GetProperty("scope").GetString());
        Assert.Contains("fire.gif", definition.GetProperty("example").GetString());
        Assert.Equal("source", showimg.GetProperty("origin").GetString());

        JsonElement found = await rig.OkAsync("rc_script_help", """{"query":"*img","scope":"clientside"}""");
        Assert.Equal(new[] { "findimg", "showimg" }, found.GetProperty("definitions").EnumerateArray().Select(item => item.GetProperty("name").GetString()).Order());
        Assert.Equal("TServerLevel.showimg(index, imageFile, x, y) - Creates an image.", found.GetProperty("definitions").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "showimg").GetProperty("summary").GetString());
        Assert.Equal("echo", Assert.Single((await rig.OkAsync("rc_script_help", """{"query":"log","inDescriptions":true}""")).GetProperty("definitions").EnumerateArray()).GetProperty("name").GetString());
        Assert.Contains("No definition is named", (await rig.OkAsync("rc_script_help", """{"name":"showim"}""")).GetProperty("note").GetString());

        // With the API the check also knows which functions exist.
        JsonElement check = await rig.OkAsync("rc_check_script", """{"text":"function onCreated() {\n  shwoimg(200, \"a.png\", 1, 2);\n}\n"}""");
        Assert.Contains(check.GetProperty("diagnostics").EnumerateArray(), item => item.GetProperty("line").GetInt32() == 2 && item.GetProperty("message").GetString()!.Contains("shwoimg", StringComparison.Ordinal));
        JsonElement syntaxOnly = await rig.OkAsync("rc_check_script", """{"text":"function onCreated() {\n  shwoimg(200, \"a.png\", 1, 2);\n}\n","api":false}""");
        Assert.DoesNotContain(syntaxOnly.GetProperty("diagnostics").EnumerateArray(), item => item.GetProperty("message").GetString()!.Contains("shwoimg", StringComparison.Ordinal));
        Assert.Equal("not used", syntaxOnly.GetProperty("api").GetString());

        // A source that fails falls back to the editors' cache file; without the cache nothing is there.
        rig.Setup.Options.Update(options => options with { AutocompleteSource = folder.Combine("api", "missing.json") });
        JsonElement cached = await rig.OkAsync("rc_script_help", """{"name":"echo"}""");
        Assert.Equal("cache", cached.GetProperty("origin").GetString());
        Assert.Contains("missing.json", cached.GetProperty("sourceError").GetString());
        File.Delete(rig.Setup.Paths.EditorCacheDirectory + "/scriptapi.json");
        Assert.Contains("could not be loaded", await rig.FailAsync("rc_script_help", """{"name":"echo","refresh":true}"""));
        folder.Dispose();
    }

    [Fact]
    public async Task The_GS2_reference_is_listed_searched_read_and_served_as_resources()
    {
        var extra = new TempFolder();
        await using var rig = new ServerRig(o => o with { McpEnabled = true, McpFullControl = true, McpApprove = false, McpReferenceFolders = extra.Path });
        rig.Folder.Write("reference/README.md", "| [GraalScript_Reference.md](docs/GraalScript_Reference.md) | The comprehensive guide |\n| [CORE.md](CORE.md) | The core reference. |\n");
        rig.Folder.Write("reference/CORE.md", "# Core\n\nUse showimg for images.\n");
        rig.Folder.Write("reference/docs/GraalScript_Reference.md", "# Guide\n\n## Images\n\nshowimg(index, image, x, y) draws an image.\n\n### Deep\n\nmore about showimg\n\n## Timers\n\nsetTimer(0.05);\n");
        rig.Folder.Write("reference/basics/strings.md", "# Strings\n\nTokenize with tokenize().\n");
        extra.Write("ghidra/TShowImg/TShowImg.txt", "void TShowImg::showimg(int index) { }\n");

        JsonElement list = await rig.OkAsync("rc_gs2_docs");
        JsonElement[] documents = list.GetProperty("documents").EnumerateArray().ToArray();
        Assert.Equal("docs/GraalScript_Reference.md", documents[0].GetProperty("document").GetString());
        Assert.True(documents[0].GetProperty("verified").GetBoolean());
        Assert.Equal("The comprehensive guide", documents[0].GetProperty("description").GetString());
        Assert.Equal("CORE.md", documents[1].GetProperty("document").GetString());
        Assert.Equal(1, list.GetProperty("otherFolders")[0].GetProperty("documents").GetInt32());

        JsonElement section = await rig.OkAsync("rc_gs2_docs", """{"document":"GraalScript_Reference.md","section":"images"}""");
        Assert.Equal("Images", section.GetProperty("section").GetString());
        Assert.Equal("## Images\n\nshowimg(index, image, x, y) draws an image.\n\n### Deep\n\nmore about showimg\n", section.GetProperty("text").GetString());
        JsonElement outline = await rig.OkAsync("rc_gs2_docs", """{"document":"docs/GraalScript_Reference.md","outline":true}""");
        Assert.Equal(new[] { "Guide", "Images", "Deep", "Timers" }, outline.GetProperty("outline").EnumerateArray().Select(item => item.GetProperty("heading").GetString()));
        Assert.Contains("No reference document", await rig.FailAsync("rc_gs2_docs", """{"document":"nope.md"}"""));

        JsonElement search = await rig.OkAsync("rc_gs2_search", """{"query":"showimg","contextLines":0}""");
        JsonElement[] results = search.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(new[] { "docs/GraalScript_Reference.md", "CORE.md", Path.GetFileName(extra.Path) + "/ghidra/TShowImg/TShowImg.txt" }, results.Select(item => item.GetProperty("document").GetString()));
        Assert.Equal(new[] { "Images", "Deep" }, results[0].GetProperty("sections").EnumerateArray().Select(item => item.GetString()));
        Assert.True(results[2].GetProperty("local").GetBoolean());
        Assert.Single((await rig.OkAsync("rc_gs2_search", """{"query":"showimg","documents":"CORE*"}""")).GetProperty("results").EnumerateArray());

        JsonElement resources = (await rig.Setup.CallAsync("""{"jsonrpc":"2.0","id":1,"method":"resources/list"}""")).GetProperty("result").GetProperty("resources");
        Assert.Equal("gs2ref://reference/docs/GraalScript_Reference.md", resources[0].GetProperty("uri").GetString());
        Assert.Equal(1.0, resources[0].GetProperty("annotations").GetProperty("priority").GetDouble());
        Assert.DoesNotContain(resources.EnumerateArray(), item => item.GetProperty("uri").GetString()!.Contains("ghidra", StringComparison.Ordinal));
        JsonElement read = (await rig.Setup.CallAsync("""{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"gs2ref://reference/CORE.md"}}""")).GetProperty("result");
        Assert.Equal("# Core\n\nUse showimg for images.\n", read.GetProperty("contents")[0].GetProperty("text").GetString());
        Assert.Equal((-32602, "Resource not found: gs2ref://reference/../x"), BridgeSetup.ErrorOf(await rig.Setup.CallAsync("""{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"gs2ref://reference/../x"}}""")));
        Assert.True((await rig.Setup.CallAsync("""{"jsonrpc":"2.0","id":4,"method":"initialize"}""")).GetProperty("result").GetProperty("capabilities").TryGetProperty("resources", out _));
        extra.Dispose();
    }

    [Fact]
    public void The_bundled_reference_has_the_verified_guide_and_its_source()
    {
        string folder = Path.Combine(RepositoryRoot(), "assets", "runtime", "gs2-reference");
        var reference = new McpReference(folder, string.Empty);
        IReadOnlyList<McpReferenceDocument> documents = reference.Documents();
        McpReferenceDocument verified = documents[0];
        Assert.Equal(McpReference.VerifiedId, verified.Id);
        Assert.True(verified.Verified);
        Assert.True(verified.Size > 100_000);
        Assert.Contains(documents, document => document.Id == "CORE.md" && document.Description.Length > 0);
        Assert.Contains(documents, document => document.Id == "SOURCE.md");
        Assert.True(McpReference.Outline(McpReference.Read(verified)!).Count > 50);
        Assert.DoesNotContain(documents, document => document.Id.StartsWith("ghidra/", StringComparison.Ordinal) || document.Id.StartsWith("refs/", StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteControl.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository was not found.");
    }

    [Fact]
    public async Task Scripts_are_checked_without_a_server()
    {
        await using var rig = new ServerRig(o => o with { McpEnabled = true, McpFullControl = true, McpApprove = false, AutocompleteSource = string.Empty });
        JsonElement check = await rig.OkAsync("rc_check_script", JsonSerializer.Serialize(new { text = "function onCreated() {\n  if (x {\n    echo(1);\n  }\n}\n", format = true, api = false }));
        Assert.True(check.GetProperty("errors").GetInt32() > 0);
        JsonElement diagnostic = check.GetProperty("diagnostics")[0];
        Assert.True(diagnostic.GetProperty("line").GetInt32() >= 2);
        Assert.False(string.IsNullOrEmpty(diagnostic.GetProperty("message").GetString()));
        Assert.Equal(0, (await rig.OkAsync("rc_check_script", """{"text":"function onCreated() {\n  echo(\"hi\");\n}\n","api":false}""")).GetProperty("errors").GetInt32());
    }

    [Fact]
    public async Task NPCs_are_read_created_and_their_flags_merged()
    {
        await using var rig = new ServerRig();
        rig.Nc.Npcs[1000] = ("door", "// door\n");
        rig.Nc.Publish();
        rig.Answer(nameof(IRcSession.RequestNpcAttributesAsync), call => rig.Fake.RaiseNpcAttributes(call.Argument<int>(0), "x=30\ny=30"));
        rig.Answer(nameof(IRcSession.RequestNpcFlagsAsync), call => rig.Fake.RaiseNpcFlags(call.Argument<int>(0), "open=0\n"));
        rig.Answer(nameof(IRcSession.CreateNpcAsync), call => rig.Fake.RaiseNpcAdded(new RcNpc(call.Argument<int>(1), call.Argument<string>(0), call.Argument<string>(2), call.Argument<string>(4), string.Empty)));
        JsonElement npc = await rig.OkAsync("rc_read_npc", """{"name":"door","parts":["attributes","flags","script"]}""");
        Assert.Equal("x=30\ny=30", npc.GetProperty("attributes").GetString());
        Assert.Equal("// door\n", npc.GetProperty("script").GetString());

        await rig.OkAsync("rc_npc_action", """{"action":"write_flags","id":1000,"set":{"open":"1"},"collectMilliseconds":0}""");
        Assert.Equal("open=1\n", rig.Fake.LastCall(nameof(IRcSession.SetNpcFlagsAsync))!.Argument<string>(1));

        JsonElement created = await rig.OkAsync("rc_npc_action", """{"action":"create","name":"shop","level":"town.nw","x":10,"y":12,"script":"// shop\n","collectMilliseconds":0}""");
        Assert.Equal(1001, created.GetProperty("id").GetInt32());
        Assert.Equal(new object?[] { "shop", 1001, "OBJECT", "Staff", "town.nw", "10", "12" }, rig.Fake.LastCall(nameof(IRcSession.CreateNpcAsync))!.Arguments);
        Assert.Equal(new object?[] { 1001, "// shop\n" }, rig.Fake.LastCall(nameof(IRcSession.UpdateNpcScriptAsync))!.Arguments);
        Assert.Contains("rc_delete_script", await rig.FailAsync("rc_npc_action", """{"action":"delete","id":1000}"""));
    }

    [Fact]
    public void Every_server_tool_is_described_with_its_permission()
    {
        foreach (string name in McpTools.ServerToolNames)
        {
            Assert.True(McpTools.TryGet(name, out McpToolDefinition? definition));
            using JsonDocument document = JsonDocument.Parse(definition.Json);
            JsonElement tool = document.RootElement;
            Assert.Equal(name, tool.GetProperty("name").GetString());
            Assert.True(tool.GetProperty("description").GetString()!.Length > 30, name);
            JsonElement schema = tool.GetProperty("inputSchema");
            Assert.Equal("object", schema.GetProperty("type").GetString());
            if (definition.Session != McpSessionUse.None) Assert.True(schema.GetProperty("properties").TryGetProperty("server", out _), name);
            if (schema.TryGetProperty("required", out JsonElement required))
                foreach (JsonElement key in required.EnumerateArray()) Assert.True(schema.GetProperty("properties").TryGetProperty(key.GetString()!, out _), name + " requires " + key);
            Assert.Equal(definition.Group == McpToolGroup.Read, tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        }
        Assert.Equal(McpTools.AllNames.Count, McpTools.AllNames.Distinct().Count());
        Assert.True(McpTools.ServerToolNames.Count >= 40);
    }

    [Fact]
    public async Task The_journal_keeps_the_last_entries_and_wakes_waiters()
    {
        var journal = new McpSessionJournal();
        for (int index = 0; index < McpSessionJournal.Capacity + 5; index++) journal.Add(McpSessionJournal.Rc, "line " + index);
        Assert.Equal(McpSessionJournal.Capacity + 5, journal.Sequence);
        Assert.Equal(6, journal.Oldest);
        Assert.Equal("line 5", journal.Read(0, limit: 1)[0].Text);
        Assert.Empty(journal.Read(journal.Sequence));
        Assert.False(await journal.WaitAsync(journal.Sequence, TimeSpan.FromMilliseconds(50)));
        Task<bool> waiting = journal.WaitAsync(journal.Sequence, TimeSpan.FromSeconds(5));
        journal.Add(McpSessionJournal.Nc, "new");
        Assert.True(await Wait.WithTimeout(waiting));
        Assert.Equal("new", Assert.Single(journal.Read(journal.Sequence - 1, new[] { McpSessionJournal.Nc })).Text);
    }

    [Fact]
    public void Settings_merge_keeps_spacing_and_comments()
    {
        using JsonDocument arguments = JsonDocument.Parse("""{"set":{"b":"20"},"remove":["c"]}""");
        string merged = McpServerTools.MergeSettings("# comment = here\na=1\nb = 2\nc=3\n", new McpArguments(arguments.RootElement), ignoreCase: false, out List<string> notes);
        Assert.Equal("# comment = here\na=1\nb = 20\n", merged);
        Assert.Equal(new[] { "set b", "removed c" }, notes);
        Assert.Equal("Uploaded file a.nw", McpServerTools.ReadUploadAnswer("levels/", "a.nw", "Uploaded file a.nw")?.Message);
        Assert.False(McpServerTools.ReadUploadAnswer("levels/", "a.nw", "Could not upload levels/a.nw: too large")!.Accepted);
        Assert.Null(McpServerTools.ReadUploadAnswer("levels/", "a.nw", "Uploaded file b.nw"));
        Assert.Equal("-System%2FLogin", McpServerTools.FriendlyFileName("-System/Login"));
        Assert.Equal("-System/Login", McpServerTools.UnescapeFileName("-System%2FLogin"));
        Assert.Equal("Wei%C3%9F", McpServerTools.FriendlyFileName("Weiß"));
        Assert.Equal("Weiß", McpServerTools.UnescapeFileName("Wei%C3%9F"));
    }
}
