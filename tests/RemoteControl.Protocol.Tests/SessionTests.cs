using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Protocol.Text;
using RemoteControl.Protocol.Wire;
using static RemoteControl.Protocol.Tests.TestHelpers;

namespace RemoteControl.Protocol.Tests;

public sealed class SessionLoginTests
{
    [Fact]
    public async Task Login_sends_the_rc2_login_and_raises_Connected()
    {
        await using var server = new FakeGameServer();
        using var session = new RcSession(Options());
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Connected += (_, _) => connected.TrySetResult();

        ConnectResult result = await session.ConnectAsync(server.Host, server.Port).Within();

        Assert.True(result.Success, result.Error);
        await connected.Task.Within();
        Assert.True(session.IsAuthenticated);
        Assert.Equal(new RcLogin(0x56, "GSERV025", "Staff", "secret", "linux,abc,def,"), Assert.Single(server.Logins));
    }

    [Fact]
    public async Task A_refused_login_fails_with_the_servers_text_and_one_Disconnected()
    {
        await using var server = new FakeGameServer { RejectLoginWith = "You do not have RC rights." };
        using var session = new RcSession(Options());
        var reasons = new ConcurrentQueue<string>();
        session.Disconnected += (_, args) => reasons.Enqueue(args.Reason);

        ConnectResult result = await session.ConnectAsync(server.Host, server.Port).Within();

        Assert.False(result.Success);
        Assert.Equal("You do not have RC rights.", result.Error);
        await Eventually(() => !reasons.IsEmpty);
        await Task.Delay(100);
        Assert.Equal(new[] { "You do not have RC rights." }, reasons.ToArray());
        Assert.False(session.IsConnected);
        Assert.False(await session.SendRcChatAsync("anything").Within());
    }

    [Fact]
    public async Task A_closed_port_fails_and_a_session_connects_only_once()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var session = new RcSession(Options());
        ConnectResult result = await session.ConnectAsync("127.0.0.1", port).Within();
        Assert.False(result.Success);
        Assert.StartsWith("Failed to connect", result.Error);
        Assert.False((await session.ConnectAsync("127.0.0.1", port).Within()).Success);
    }

    [Fact]
    public async Task A_server_that_never_answers_the_login_times_out()
    {
        await using var server = new FakeGameServer { AcceptLogin = false };
        using var session = new RcSession(Options(loginTimeout: TimeSpan.FromMilliseconds(300)));
        ConnectResult result = await session.ConnectAsync(server.Host, server.Port).Within();
        Assert.False(result.Success);
        Assert.Contains("in time", result.Error);
    }

    [Fact]
    public async Task Cancelling_the_connect_returns_a_failure()
    {
        await using var server = new FakeGameServer { AcceptLogin = false };
        using var session = new RcSession(Options());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        ConnectResult result = await session.ConnectAsync(server.Host, server.Port, cancel.Token).Within();
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Events_run_on_the_given_synchronization_context()
    {
        await using var server = new FakeGameServer();
        using var context = new SingleThreadContext();
        int? eventThread = null;
        using var onContext = new RcSession(new RcSessionOptions
        {
            Account = "Staff", Password = "secret", PcIdList = "x", SynchronizationContext = context, LoginTimeout = TimeSpan.FromSeconds(3),
        });
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        onContext.Connected += (_, _) =>
        {
            eventThread = Environment.CurrentManagedThreadId;
            connected.TrySetResult();
        };
        Assert.True((await onContext.ConnectAsync(server.Host, server.Port).Within()).Success);
        await connected.Task.Within();
        Assert.Equal(context.ThreadId, eventThread);
    }

    [Fact]
    public async Task An_inline_post_delegate_works()
    {
        await using var server = new FakeGameServer();
        var options = new RcSessionOptions { Account = "Staff", Password = "secret", PcIdList = "x", Post = action => action() };
        using var session = new RcSession(options);
        var chat = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RcChatReceived += (_, args) => chat.TrySetResult(args.Text);
        Assert.True((await session.ConnectAsync(server.Host, server.Port).Within()).Success);
        FakeConnection client = await server.WaitForConnectionAsync();
        await FakeGameServer.SendRcChatAsync(client, "inline");
        Assert.Equal("inline", await chat.Task.Within());
        Assert.True(await session.SendRcChatAsync("back").Within());
        Assert.Equal("back", (await client.WaitForPacketAsync(ClientPacketId.RcChat)).Text);
    }

    /// <summary>A synchronization context with one thread, like a UI thread.</summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
        private readonly Thread _thread;

        public SingleThreadContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable()) callback(state);
            }) { IsBackground = true };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void Dispose() => _queue.CompleteAdding();
    }
}

public sealed class SessionChatAndPlayerTests
{
    [Fact]
    public async Task Rc_chat_is_received_and_multi_line_chat_is_sent_line_by_line()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            Task<TextEventArgs> line = Next<TextEventArgs>(h => session.RcChatReceived += h, h => session.RcChatReceived -= h);
            await FakeGameServer.SendRcChatAsync(client, "Bob: 3 of +5 staff");
            Assert.Equal("Bob: 3 of 5 staff", (await line).Text);

            Assert.True(await session.SendRcChatAsync("/stats\r\n/version").Within());
            Assert.Equal("/stats", (await client.WaitForPacketAsync(ClientPacketId.RcChat)).Text);
            Assert.Equal("/version", (await client.WaitForPacketAsync(ClientPacketId.RcChat)).Text);
        }
    }

    [Fact]
    public async Task Player_list_follows_add_update_and_delete()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            Task<PlayerEventArgs> joined = Next<PlayerEventArgs>(h => session.PlayerJoined += h, h => session.PlayerJoined -= h);
            // A non-zero status icon: RClib read property 53 as a string and lost the nickname after it.
            await FakeGameServer.SendAddPlayerAsync(client, 5, "Bob", "Bobby", "start.nw", statusIcon: 4);
            RcPlayer bob = (await joined).Player;
            Assert.Equal(("Bob", "Bobby", "start.nw", 4), (bob.Account, bob.Nickname, bob.Level, bob.StatusIcon));

            Task<PlayerEventArgs> updated = Next<PlayerEventArgs>(h => session.PlayerUpdated += h, h => session.PlayerUpdated -= h);
            await FakeGameServer.SendAddPlayerAsync(client, 5, "Bob", "Bobby", "other.nw");
            Assert.Equal("other.nw", (await updated).Player.Level);

            Task<EventArgs> channels = NextPlain(session);
            await FakeGameServer.SendAddPlayerAsync(client, 16001, "irc:#graal", "#graal (12,3)", category: 3);
            await channels;
            RcChannel channel = Assert.Single(session.Channels);
            Assert.Equal(("#graal", "12", "3"), (channel.Name, channel.Players, channel.Id));

            Task<PlayerEventArgs> external = Next<PlayerEventArgs>(h => session.PlayerJoined += h, h => session.PlayerJoined -= h);
            await FakeGameServer.SendAddPlayerAsync(client, 16002, "Ann", "Ann (on Other)", category: 1);
            Assert.True((await external).Player.IsExternal);

            Task<PlayerEventArgs> left = Next<PlayerEventArgs>(h => session.PlayerLeft += h, h => session.PlayerLeft -= h);
            await FakeGameServer.SendDeletePlayerAsync(client, 5);
            Assert.Equal("Bob", (await left).Player.Account);
            await Eventually(() => session.Players.Count == 1);
            Assert.Equal("Ann", session.Players[0].Account);
        }
    }

    private static Task<EventArgs> NextPlain(RcSession session)
    {
        var completion = new TaskCompletionSource<EventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, args) =>
        {
            session.ChannelsChanged -= handler;
            completion.TrySetResult(args);
        };
        session.ChannelsChanged += handler;
        return completion.Task.Within();
    }

    [Fact]
    public async Task Private_messages_in_the_alpaxis_format_are_read_and_sent_as_comma_text()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            await FakeGameServer.SendAddPlayerAsync(client, 7, "Ann", "Annie");
            Task<PrivateMessageEventArgs> message = Next<PrivateMessageEventArgs>(h => session.PrivateMessageReceived += h, h => session.PrivateMessageReceived -= h);
            await FakeGameServer.SendPrivateMessageAsync(client, 7, "hello, there\nsecond line");
            PrivateMessageEventArgs pm = await message;
            Assert.Equal(("Ann", "Annie", "hello, there\nsecond line", PrivateMessageKind.Normal), (pm.Account, pm.Nickname, pm.Text, pm.Kind));

            Assert.True(await session.SendPrivateMessageAsync(7, "hi, Ann").Within());
            ReceivedPacket sent = await client.WaitForPacketAsync(ClientPacketId.PrivateMessage);
            PacketReader reader = sent.Reader();
            Assert.True(reader.TryReadGShort(out int count) && count == 1);
            Assert.True(reader.TryReadGShort(out int id) && id == 7);
            Assert.Equal("\"hi, Ann\"", reader.ReadRemainingText());
        }
    }

    [Fact]
    public async Task Server_texts_fill_the_pm_lists_and_irc()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            Task<ValueEventArgs<int>> servers = Next<ValueEventArgs<int>>(h => session.PmServersUpdated += h, h => session.PmServersUpdated -= h);
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "pmservers", "all", "Alpha, Beta", "Gamma");
            Assert.Equal(3, (await servers).Value);
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, session.PmServers);

            Task<IrcMessageEventArgs> irc = Next<IrcMessageEventArgs>(h => session.IrcMessageReceived += h, h => session.IrcMessageReceived -= h);
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "irc", "privmsg", "Bob", "#graal", "hi all");
            IrcMessageEventArgs line = await irc;
            Assert.Equal(("#graal", "<Bob> hi all"), (line.Channel, line.Line));

            Task<TextEventArgs> other = Next<TextEventArgs>(h => session.ServerTextReceived += h, h => session.ServerTextReceived -= h);
            // Malformed lister texts (RClib read parts[2] of a two-part text) end up as plain server text.
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "lister");
            Assert.Equal("GraalEngine\nlister", (await other).Text);

            Assert.True(await session.RequestServerListAsync().Within());
            Assert.Equal("GraalEngine,lister,simplelist", (await client.WaitForPacketAsync(ClientPacketId.RequestText)).Text);
            Task<EventArgs> list = NextGameServers(session);
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "lister", "simpleserverlist", "Alpha,\"P Alpha\",4", "Beta,Beta,0");
            await list;
            Assert.Equal(new[] { new SimpleServerEntry("Alpha", "P Alpha", 4), new SimpleServerEntry("Beta", "Beta", 0) }, session.GameServers);
        }
    }

    private static Task<EventArgs> NextGameServers(RcSession session)
    {
        var completion = new TaskCompletionSource<EventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.GameServersUpdated += (_, args) => completion.TrySetResult(args);
        return completion.Task.Within();
    }

    [Fact]
    public async Task Server_listconnected_asks_for_the_lister_texts()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            await client.SendAsync(ServerPacketId.ServerListConnected, Array.Empty<byte>());
            Assert.Equal("GraalEngine,lister,bantypes", (await client.WaitForPacketAsync(ClientPacketId.RequestText)).Text);
            Assert.Equal("GraalEngine,lister,pmguilds", (await client.WaitForPacketAsync(ClientPacketId.RequestText)).Text);
            Assert.Equal("GraalEngine,lister,pmservers", (await client.WaitForPacketAsync(ClientPacketId.RequestText)).Text);
        }
    }

    [Fact]
    public async Task Account_rights_and_options_replies_are_parsed()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            Assert.True(await session.RequestPlayerRightsAsync("Bob").Within());
            Assert.Equal("Bob", (await client.WaitForPacketAsync(ClientPacketId.PlayerRightsGet)).Reader().ReadGStringOrEmpty());

            Task<PlayerRightsEventArgs> rights = Next<PlayerRightsEventArgs>(h => session.PlayerRightsReceived += h, h => session.PlayerRightsReceived -= h);
            string folders = CommaText.Tokenize("rw levels/*\nr config/*");
            await client.SendAsync(ServerPacketId.PlayerRights, new PacketWriter().WriteGString("Bob").WriteGInt5(0x80001).WriteGString("*").WriteGShortString(folders));
            PlayerRightsEventArgs received = await rights;
            Assert.Equal(new PlayerRights(0x80001, "*", "rw levels/*\nr config/*"), received.Rights);

            Task<AccountDataEventArgs> account = Next<AccountDataEventArgs>(h => session.AccountReceived += h, h => session.AccountReceived -= h);
            await client.SendAsync(ServerPacketId.Account, new PacketWriter().WriteGString("Bob").WriteGString("").WriteGString("b@x").WriteGByte(1).WriteGByte(0)
                .WriteGByte(2).WriteGString("main").WriteGString("").WriteGString("\"cheat, bot\""));
            AccountData data = (await account).Account;
            Assert.Equal(("Bob", "b@x", true, 2, "main", "cheat, bot"), (data.Account, data.Email, data.Banned, data.AdminLevel, data.AdminWorlds, data.BanReason));

            Task<TextEventArgs> options = Next<TextEventArgs>(h => session.ServerOptionsReceived += h, h => session.ServerOptionsReceived -= h);
            await client.SendTextAsync(ServerPacketId.ServerOptions, CommaText.Tokenize("name = Test\nmaxplayers = 10"));
            Assert.Equal("name = Test\nmaxplayers = 10", (await options).Text);
            Assert.Equal("name = Test\nmaxplayers = 10", session.ServerOptions);

            Assert.True(await session.UploadServerFlagsAsync("a=1\n\n  \nb=2\n").Within());
            PacketReader flags = (await client.WaitForPacketAsync(ClientPacketId.ServerFlagsSet)).Reader();
            Assert.True(flags.TryReadGShort(out int count) && count == 2);
            Assert.Equal("a=1", flags.ReadGStringOrEmpty());

            Assert.True(await session.SendAdminMessageToAllAsync("line one\nline two").Within());
            Assert.Equal("line one§line two", (await client.WaitForPacketAsync(ClientPacketId.AdminMessage)).Text);
        }
    }

    [Fact]
    public async Task Player_attributes_round_trip_through_the_server()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            var attributes = new PlayerAttributes { World = "main", Level = "a.nw", X = 10.5, Status = 4, Nickname = "Bob" };
            attributes.Weapons.Add("Bow");
            Assert.True(await session.SetPlayerAttributesAsync("Bob", attributes).Within());
            ReceivedPacket set = await client.WaitForPacketAsync(ClientPacketId.PlayerPropsSetByAccount);

            Task<PlayerAttributesEventArgs> reply = Next<PlayerAttributesEventArgs>(h => session.PlayerAttributesReceived += h, h => session.PlayerAttributesReceived -= h);
            await client.SendAsync(ServerPacketId.PlayerAttributes, new PacketWriter().WriteGShort(3).WriteBytes(set.Payload));
            PlayerAttributesEventArgs received = await reply;
            Assert.Equal("Bob", received.Account);
            Assert.Equal("a.nw", received.Attributes.Level);
            Assert.Equal(10.5, received.Attributes.X);
            Assert.Contains("Level: a.nw\nX: 10.5\n", received.EditorText);
            Assert.Equal(new[] { "Bow" }, received.Attributes.Weapons);
        }
    }
}

public sealed class SessionRobustnessTests
{
    [Fact]
    public async Task Malformed_and_truncated_packets_of_every_id_never_kill_the_session()
    {
        await using var server = new FakeGameServer();
        var log = new ConcurrentQueue<string>();
        (RcSession session, FakeConnection client) = await ConnectAsync(server, Options(log: log.Enqueue));
        using (session)
        {
            var random = new Random(42);
            byte[][] shapes =
            {
                Array.Empty<byte>(), new byte[] { 0x00 }, new byte[] { 0x10, 0x10 }, new byte[] { 0x1F, 0x1F, 0x1F, 0x1F, 0x1F },
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, new byte[] { 0x20, 0x21, 0x40 },
            };
            for (int id = 0; id < 224; id++)
            {
                if (id is (int)ServerPacketId.DisconnectMessage or (int)ServerPacketId.RawData) continue;
                var batch = new List<(int, byte[])>();
                foreach (byte[] shape in shapes) batch.Add((id, shape.Where(value => value != '\n').ToArray()));
                for (int round = 0; round < 6; round++)
                {
                    byte[] noise = new byte[random.Next(0, 60)];
                    random.NextBytes(noise);
                    batch.Add((id, noise.Where(value => value != '\n').ToArray()));
                }

                await client.SendInOneFrameAsync(batch.ToArray());
            }

            // Bad raw-data headers and a frame without a newline.
            await client.SendPlaintextFrameAsync(new byte[] { 0x84, 0x10, 0x10, 0x10, (byte)'\n' });
            await client.SendPlaintextFrameAsync("Jno newline here"u8.ToArray());
            await client.SendPlaintextFrameAsync("\n"u8.ToArray());

            Task<TextEventArgs> alive = Next<TextEventArgs>(h => session.RcChatReceived += h, h => session.RcChatReceived -= h, args => args.Text == "still alive");
            await FakeGameServer.SendRcChatAsync(client, "still alive");
            await alive;
            Assert.True(session.IsAuthenticated);
            Assert.True(await session.SendRcChatAsync("pong").Within());
            Assert.Equal("pong", (await client.WaitForPacketAsync(ClientPacketId.RcChat, packet => packet.Text == "pong")).Text);
        }
    }

    [Fact]
    public async Task Concurrent_senders_never_interleave_frames_or_the_cipher()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            const int threads = 8, perThread = 40;
            var tasks = Enumerable.Range(0, threads).Select(thread => Task.Run(async () =>
            {
                for (int index = 0; index < perThread; index++)
                {
                    string text = $"t{thread}-{index}-" + new string((char)('a' + thread), index * 7 % 120);
                    Assert.True(await session.SendRcChatAsync(text));
                }
            })).ToArray();
            await Task.WhenAll(tasks).Within(TimeSpan.FromSeconds(10));
            await Eventually(() => client.Received.Count(packet => packet.Id == (int)ClientPacketId.RcChat) == threads * perThread);

            var lines = client.Received.Where(packet => packet.Id == (int)ClientPacketId.RcChat).Select(packet => packet.Text).ToList();
            for (int thread = 0; thread < threads; thread++)
            {
                var own = lines.Where(line => line.StartsWith($"t{thread}-", StringComparison.Ordinal)).ToList();
                Assert.Equal(Enumerable.Range(0, perThread).Select(index => $"t{thread}-{index}-" + new string((char)('a' + thread), index * 7 % 120)), own);
            }

            Assert.Empty(client.Errors);
        }
    }

    [Fact]
    public async Task A_packet_too_large_for_a_frame_is_refused_and_the_session_goes_on()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            Task<TextEventArgs> error = Next<TextEventArgs>(h => session.ProtocolError += h, h => session.ProtocolError -= h);
            string noise = RcText.GetString(RandomBytes(150_000, 1).Select(value => value == '\n' ? (byte)'x' : value).ToArray());
            Assert.False(await session.UploadServerOptionsAsync(noise).Within());
            Assert.Contains("not sent", (await error).Text);
            Assert.NotNull(session.LastError);

            Assert.False(await session.SendToAllAsync(new string('x', 300)).Within());
            Assert.True(await session.SendRcChatAsync("after").Within());
            Assert.Equal("after", (await client.WaitForPacketAsync(ClientPacketId.RcChat)).Text);
        }
    }

    [Fact]
    public async Task After_Disconnected_nothing_is_raised_and_every_call_returns_false()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            int events = 0;
            var disconnected = new TaskCompletionSource<DisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.RcChatReceived += (_, _) => Interlocked.Increment(ref events);
            session.Disconnected += (_, args) => disconnected.TrySetResult(args);

            await client.SendTextAsync(ServerPacketId.DisconnectMessage, "Server shutting down");
            DisconnectedEventArgs args = await disconnected.Task.Within();
            Assert.Equal("Server shutting down", args.Reason);
            Assert.False(args.Requested);
            await FakeGameServer.SendRcChatAsync(client, "too late");
            await Task.Delay(100);
            Assert.Equal(0, events);
            Assert.False(await session.SendRcChatAsync("x").Within());
            Assert.False(await session.FileBrowser.StartAsync().Within());
            Assert.Null(await session.FileBrowser.DownloadAsync("levels/a.nw").Within());
            Assert.False(await session.ConnectNcAsync().Within());
            session.Disconnect();
            Assert.False(session.DisconnectNc());
        }
    }

    [Fact]
    public async Task Disconnect_raises_one_requested_Disconnected_and_closes_the_socket()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            var reasons = new ConcurrentQueue<DisconnectedEventArgs>();
            session.Disconnected += (_, args) => reasons.Enqueue(args);
            session.Disconnect();
            session.Disconnect();
            await Eventually(() => !reasons.IsEmpty);
            Assert.True(await client.WaitForCloseAsync());
            await Task.Delay(50);
            DisconnectedEventArgs only = Assert.Single(reasons);
            Assert.True(only.Requested);
        }
    }

    [Fact]
    public async Task Dispose_raises_nothing()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        int events = 0;
        session.Disconnected += (_, _) => Interlocked.Increment(ref events);
        session.Dispose();
        Assert.True(await client.WaitForCloseAsync());
        await Task.Delay(50);
        Assert.Equal(0, events);
        Assert.False(await session.SendRcChatAsync("x").Within());
    }
}

public sealed class SessionNcTests
{
    private static async Task<(RcSession, FakeConnection, FakeNcServer)> ConnectWithNcAsync(FakeGameServer server, FakeNcServer nc)
    {
        server.NcAddress = $"127.0.0.1,{nc.Port}";
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        await FakeGameServer.SendAddPlayerAsync(client, 2, "(npcserver)", "Test (Server)");
        ReceivedPacket query = await client.WaitForPacketAsync(ClientPacketId.NpcServerQuery);
        PacketReader reader = query.Reader();
        Assert.True(reader.TryReadGShort(out int id) && id == 2);
        Assert.Equal("location", reader.ReadRemainingText());
        await Eventually(() => session.HasNcServer);
        Assert.True(await session.ConnectNcAsync().Within());
        return (session, client, nc);
    }

    [Fact]
    public async Task Nc_login_weapon_list_fetch_and_save()
    {
        await using var server = new FakeGameServer();
        await using var nc = new FakeNcServer();
        nc.Weapons["Bomb"] = ("bomb.png", "function onCreated() {\n  echo(\"hi\");\n}");
        nc.Weapons["-System"] = ("", "//#CLIENTSIDE");
        nc.Classes["util"] = "function a() {}\n\nfunction b() {}";
        nc.Npcs[1001] = ("Banker", "Database", "bank.nw");
        var weaponList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (RcSession session, FakeConnection _, FakeNcServer _) = await ConnectWithNcAsyncWatching(server, nc, weaponList);
        using (session)
        {
            await weaponList.Task.Within();
            Assert.Equal(new NcLogin("NCL21075", "Staff", "secret"), Assert.Single(nc.Logins));
            Assert.True(session.IsNcAuthenticated);
            Assert.Equal(new[] { "-System", "Bomb" }, session.Weapons.Select(weapon => weapon.Name));
            await Eventually(() => session.Classes.Count == 1 && session.Npcs.Count == 1);
            Assert.Equal(new RcNpc(1001, "Banker", "Database", "bank.nw", ""), session.Npcs[0]);

            Task<ScriptReceivedEventArgs> script = Next<ScriptReceivedEventArgs>(h => session.ScriptReceived += h, h => session.ScriptReceived -= h);
            Assert.True(await session.RequestWeaponScriptAsync("Bomb").Within());
            ScriptReceivedEventArgs weapon = await script;
            Assert.Equal((ScriptKind.Weapon, "Bomb", "bomb.png", "function onCreated() {\n  echo(\"hi\");\n}"), (weapon.Kind, weapon.Name, weapon.Image, weapon.Script));

            Task<TextEventArgs> saved = Next<TextEventArgs>(h => session.NcChatReceived += h, h => session.NcChatReceived -= h);
            Assert.True(await session.UpdateWeaponAsync("Bomb", "bomb2.png", "line1\r\nline2\n").Within());
            Assert.Contains("Bomb", (await saved).Text);
            Assert.Equal(("bomb2.png", "line1\nline2\n"), nc.Weapons["Bomb"]);

            Task<ScriptReceivedEventArgs> classScript = Next<ScriptReceivedEventArgs>(h => session.ScriptReceived += h, h => session.ScriptReceived -= h);
            Assert.True(await session.RequestClassScriptAsync("util").Within());
            Assert.Equal("function a() {}\n\nfunction b() {}", (await classScript).Script);

            Task<ValueEventArgs<string>> added = Next<ValueEventArgs<string>>(h => session.ClassAdded += h, h => session.ClassAdded -= h);
            Assert.True(await session.AddClassAsync("fresh", "x = 1;\ny = 2;").Within());
            Assert.Equal("fresh", (await added).Value);
            Assert.Equal("x = 1;\ny = 2;", nc.Classes["fresh"]);
            Assert.False(await session.AddClassAsync("long", new string('x', 300)).Within());
        }
    }

    private static async Task<(RcSession, FakeConnection, FakeNcServer)> ConnectWithNcAsyncWatching(FakeGameServer server, FakeNcServer nc, TaskCompletionSource weaponList)
    {
        server.NcAddress = $"127.0.0.1,{nc.Port}";
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        session.WeaponListReceived += (_, _) => weaponList.TrySetResult();
        await FakeGameServer.SendAddPlayerAsync(client, 2, "(npcserver)", "Test (Server)");
        await Eventually(() => session.HasNcServer);
        Assert.True(await session.ConnectNcAsync().Within());
        return (session, client, nc);
    }

    [Fact]
    public async Task A_refused_nc_login_raises_NcDisconnected_and_keeps_the_rc()
    {
        await using var server = new FakeGameServer();
        await using var nc = new FakeNcServer { RejectLoginWith = "You do not have NC rights." };
        server.NcAddress = $"localhost,{nc.Port}";
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            await FakeGameServer.SendAddPlayerAsync(client, 2, "(npcserver)", "Test (Server)");
            await Eventually(() => session.HasNcServer);
            var reasons = new ConcurrentQueue<string>();
            session.NcDisconnected += (_, args) => reasons.Enqueue(args.Reason);
            Assert.False(await session.ConnectNcAsync().Within());
            await Eventually(() => !reasons.IsEmpty);
            Assert.Equal("You do not have NC rights.", Assert.Single(reasons));
            Assert.True(session.IsAuthenticated);
            Assert.False(await session.RequestWeaponListAsync().Within());
        }
    }

    [Fact]
    public async Task Nc_connect_without_an_address_fails_quietly()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection _) = await ConnectAsync(server);
        using (session)
        {
            Assert.False(await session.ConnectNcAsync().Within());
            Assert.Equal("NC server address not received", session.LastError);
        }
    }

    [Fact]
    public async Task Malformed_nc_packets_never_kill_the_nc_connection()
    {
        await using var server = new FakeGameServer();
        await using var nc = new FakeNcServer();
        (RcSession session, FakeConnection _, FakeNcServer _) = await ConnectWithNcAsync(server, nc);
        using (session)
        {
            FakeConnection ncClient = await nc.WaitForConnectionAsync();
            var random = new Random(77);
            for (int id = 0; id < 224; id++)
            {
                if (id == (int)ServerPacketId.DisconnectMessage || id == (int)ServerPacketId.RawData) continue;
                var batch = new List<(int, byte[])> { (id, Array.Empty<byte>()), (id, new byte[] { 0x10 }), (id, new byte[] { 0xFF, 0x00, 0x20 }) };
                for (int round = 0; round < 5; round++)
                {
                    byte[] noise = new byte[random.Next(0, 50)];
                    random.NextBytes(noise);
                    batch.Add((id, noise.Where(value => value != '\n').ToArray()));
                }

                await ncClient.SendInOneFrameAsync(batch.ToArray());
            }

            Task<TextEventArgs> alive = Next<TextEventArgs>(h => session.NcChatReceived += h, h => session.NcChatReceived -= h, args => args.Text == "nc alive");
            await ncClient.SendTextAsync(ServerPacketId.RcChat, "nc alive");
            await alive;
            Assert.True(session.IsNcAuthenticated);
            Assert.True(await session.RequestWeaponListAsync().Within());
        }
    }

    [Fact]
    public async Task Disconnecting_the_nc_raises_a_requested_NcDisconnected()
    {
        await using var server = new FakeGameServer();
        await using var nc = new FakeNcServer();
        (RcSession session, FakeConnection _, FakeNcServer _) = await ConnectWithNcAsync(server, nc);
        using (session)
        {
            Task<DisconnectedEventArgs> ended = Next<DisconnectedEventArgs>(h => session.NcDisconnected += h, h => session.NcDisconnected -= h);
            Assert.True(session.DisconnectNc());
            Assert.True((await ended).Requested);
            Assert.False(session.IsNcConnected);
            Assert.True(session.IsAuthenticated);
        }
    }
}

public sealed class SessionFileTransferTests
{
    [Fact]
    public async Task Start_lists_folders_and_files_on_the_view_that_asked()
    {
        await using var server = new FakeGameServer();
        server.Files["levels/a.nw"] = "GLEVNW01\n"u8.ToArray();
        server.Files["levels/b.nw"] = new byte[10];
        (RcSession session, FakeConnection _) = await ConnectAsync(server);
        using (session)
        {
            int syncEvents = 0;
            session.SyncFileBrowser.FilesReceived += (_, _) => Interlocked.Increment(ref syncEvents);
            Task<FileBrowserFilesEventArgs> files = Next<FileBrowserFilesEventArgs>(h => session.FileBrowser.FilesReceived += h, h => session.FileBrowser.FilesReceived -= h);
            Assert.True(await session.FileBrowser.StartAsync().Within());
            FileBrowserFilesEventArgs listing = await files;
            Assert.Equal("levels/", listing.Folder);
            Assert.Equal(new[] { "a.nw", "b.nw" }, listing.Files.Select(file => file.Name));
            Assert.Equal(9, listing.Files[0].Size);
            Assert.Equal(new[] { "levels/", "config/" }, session.FileBrowser.Folders.Select(folder => folder.Folder));
            Assert.Equal(0, syncEvents);
        }
    }

    [Fact]
    public async Task Download_small_and_large_files_with_progress()
    {
        await using var server = new FakeGameServer();
        byte[] small = "line one\nline two\n\n"u8.ToArray();
        byte[] large = RandomBytes(100_000, 2);
        server.Files["levels/small.nw"] = small;
        server.Files["levels/large.png"] = large;
        (RcSession session, FakeConnection _) = await ConnectAsync(server);
        using (session)
        {
            var progress = new ConcurrentQueue<long>();
            session.FileBrowser.TransferProgressChanged += (_, args) => progress.Enqueue(args.Transferred);
            Assert.Equal(small, await session.FileBrowser.DownloadAsync("levels/small.nw").Within());
            Task<FileReceivedEventArgs> received = Next<FileReceivedEventArgs>(h => session.FileBrowser.FileReceived += h, h => session.FileBrowser.FileReceived -= h);
            Assert.Equal(large, await session.FileBrowser.DownloadAsync("levels/large.png").Within());
            Assert.Equal("levels/large.png", (await received).Path);
            Assert.Contains(100_000L, progress);
            Assert.True(progress.Count >= 4);
        }
    }

    [Fact]
    public async Task Download_of_a_missing_file_fails()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection _) = await ConnectAsync(server);
        using (session)
        {
            Task<FileTransferFailedEventArgs> failed = Next<FileTransferFailedEventArgs>(h => session.FileBrowser.TransferFailed += h, h => session.FileBrowser.TransferFailed -= h);
            Assert.Null(await session.FileBrowser.DownloadAsync("levels/missing.nw").Within());
            Assert.Equal("levels/missing.nw", (await failed).Path);
        }
    }

    [Fact]
    public async Task Upload_small_and_large_files_into_the_folder_of_the_path()
    {
        await using var server = new FakeGameServer();
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            byte[] small = "small\ncontent\n"u8.ToArray();
            byte[] large = RandomBytes(150_000, 3);
            Assert.True(await session.FileBrowser.UploadAsync("config/small.txt", small).Within());
            Assert.True(await session.FileBrowser.UploadAsync("config/large.bin", large).Within());
            await Eventually(() => server.Files.ContainsKey("config/large.bin"));
            Assert.Equal(small, server.Files["config/small.txt"]);
            Assert.Equal(large, server.Files["config/large.bin"]);
            Assert.Equal("config/", (await client.WaitForPacketAsync(ClientPacketId.FileBrowserCd)).Text);
            Assert.False(await session.FileBrowser.UploadAsync("config/../evil", small).Within());
            Assert.Empty(client.Errors);
        }
    }

    [Fact]
    public async Task The_sync_view_gets_its_own_listings_and_downloads()
    {
        await using var server = new FakeGameServer();
        server.Files["config/server.txt"] = "x=1"u8.ToArray();
        server.Files["levels/a.nw"] = "a"u8.ToArray();
        (RcSession session, FakeConnection _) = await ConnectAsync(server);
        using (session)
        {
            int browserFiles = 0, browserReceived = 0;
            session.FileBrowser.FilesReceived += (_, _) => Interlocked.Increment(ref browserFiles);
            session.FileBrowser.FileReceived += (_, _) => Interlocked.Increment(ref browserReceived);
            Task<FileBrowserFilesEventArgs> syncFiles = Next<FileBrowserFilesEventArgs>(h => session.SyncFileBrowser.FilesReceived += h, h => session.SyncFileBrowser.FilesReceived -= h);
            Assert.True(await session.SyncFileBrowser.ChangeFolderAsync("config/").Within());
            Assert.Equal("config/", (await syncFiles).Folder);
            Assert.Equal("config/", session.SyncFileBrowser.CurrentFolder);
            Assert.Equal("x=1"u8.ToArray(), await session.SyncFileBrowser.DownloadAsync("config/server.txt").Within());
            // A download in another folder changes the server's folder first; that listing goes to nobody.
            Assert.Equal("a"u8.ToArray(), await session.SyncFileBrowser.DownloadAsync("levels/a.nw").Within());
            await Task.Delay(100);
            Assert.Equal(0, browserFiles);
            Assert.Equal(0, browserReceived);
            Assert.Equal(string.Empty, session.FileBrowser.CurrentFolder);
        }
    }

    [Fact]
    public async Task Disconnect_in_the_middle_of_a_transfer_ends_the_download()
    {
        await using var server = new FakeGameServer { EmulateFileBrowser = false };
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            var events = new ConcurrentQueue<string>();
            session.Disconnected += (_, args) => events.Enqueue("disconnected");
            session.FileBrowser.TransferFailed += (_, _) => events.Enqueue("failed");
            Task<byte[]?> download = session.FileBrowser.DownloadAsync("huge.bin");
            await client.WaitForPacketAsync(ClientPacketId.FileBrowserDown);
            await client.SendTextAsync(ServerPacketId.LargeFileStart, "huge.bin");
            await client.SendAsync(ServerPacketId.LargeFileSize, new PacketWriter().WriteGInt5(1_000_000));
            await client.SendRawDataAsync(ServerPacketId.File, new PacketWriter().WriteGInt5(0).WriteGString("huge.bin").WriteBytes(new byte[1000]).ToArray());
            await Eventually(() => session.FileBrowser.TryGetTransferProgress("huge.bin", out TransferProgress p) && p.Transferred == 1000);
            Assert.True(session.FileBrowser.TryGetTransferProgress("huge.bin", out TransferProgress progress));
            Assert.Equal(1_000_000, progress.Total);
            client.Close();
            Assert.Null(await download.Within());
            await Eventually(() => events.Contains("disconnected"));
            Assert.Equal(new[] { "disconnected" }, events.ToArray());
        }
    }

    [Fact]
    public async Task A_raw_block_that_starts_in_one_frame_and_ends_in_the_next_is_read()
    {
        await using var server = new FakeGameServer { EmulateFileBrowser = false };
        (RcSession session, FakeConnection client) = await ConnectAsync(server);
        using (session)
        {
            Task<byte[]?> download = session.FileBrowser.DownloadAsync("split.txt");
            await client.WaitForPacketAsync(ClientPacketId.FileBrowserDown);
            byte[] content = "first\nsecond\n"u8.ToArray();
            byte[] inner = Frames.Packet((int)ServerPacketId.File, new PacketWriter().WriteGInt5(0).WriteGString("split.txt").WriteBytes(content).ToArray());
            byte[] header = new byte[3];
            GEncoding.EncodeGInt3(inner.Length, header);
            byte[] all = Frames.Packet((int)ServerPacketId.RawData, header).Concat(inner).ToArray();
            await client.SendPlaintextFrameAsync(all[..9]);
            await client.SendPlaintextFrameAsync(all[9..]);
            Assert.Equal(content, await download.Within());
        }
    }

    [Fact]
    public async Task A_download_the_server_ignores_times_out()
    {
        await using var server = new FakeGameServer { EmulateFileBrowser = false };
        (RcSession session, FakeConnection _) = await ConnectAsync(server, Options(downloadTimeout: TimeSpan.FromMilliseconds(300)));
        using (session)
        {
            Task<FileTransferFailedEventArgs> failed = Next<FileTransferFailedEventArgs>(h => session.FileBrowser.TransferFailed += h, h => session.FileBrowser.TransferFailed -= h);
            Assert.Null(await session.FileBrowser.DownloadAsync("ignored.nw").Within());
            Assert.Equal("ignored.nw", (await failed).Path);
        }
    }

    [Fact]
    public async Task A_cancelled_download_returns_null()
    {
        await using var server = new FakeGameServer { EmulateFileBrowser = false };
        (RcSession session, FakeConnection _) = await ConnectAsync(server);
        using (session)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            Assert.Null(await session.FileBrowser.DownloadAsync("slow.nw", cancel.Token).Within());
        }
    }
}
