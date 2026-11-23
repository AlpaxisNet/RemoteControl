using System.Text;
using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;
using static RemoteControl.Ui.Session.Tests.TestHelpers;

namespace RemoteControl.Ui.Session.Tests;

public sealed class SessionControllerTests
{
    private static (TestSession Test, SessionController Controller, Clock Clock) Start(Func<Core.RcOptions, Core.RcOptions>? options = null)
    {
        var test = new TestSession(options: options);
        var clock = new Clock();
        test.Shell.LastActivity = clock.Now;
        test.Shell.Clock = () => clock.Now;
        var controller = new SessionController(test.Context, () => clock.Now);
        controller.Start(timer: false);
        return (test, controller, clock);
    }

    [AvaloniaFact]
    public void After_the_login_the_hello_the_options_and_the_nickname_are_sent()
    {
        (TestSession test, SessionController controller, _) = Start(o => o with { GlobalPms = false, BuddyTracking = true });
        using (test)
        using (controller)
        {
            FakeRcSession fake = test.Fake;
            Assert.Equal("Tester", fake.LastCall("SetNicknameAsync")!.Argument<string>(0));
            Assert.True(fake.WasCalled("IrcLoginAsync"));
            Assert.Contains(Sent(fake), line => line.StartsWith("/npc newrc,", StringComparison.Ordinal) && line.Length > 11);
            string lister = Encoding.Latin1.GetString(Raw(fake, 154).Single());
            Assert.Equal("GraalEngine,lister,options,globalpms=false,buddytracking=true,showbuddies=false", lister);
            Assert.Equal(new[] { (byte)'A', (byte)' ' }, Raw(fake, 2).Single());

            fake.ClearCalls();
            fake.RaiseConnected();
            Assert.False(fake.WasCalled("IrcLoginAsync"));
        }
    }

    [AvaloniaFact]
    public void Mass_pms_are_rejected_with_the_options_and_the_change_is_sent_at_once()
    {
        (TestSession test, SessionController controller, _) = Start();
        using (test)
        using (controller)
        {
            test.Fake.ClearCalls();
            test.Environment.SetOptions(o => o with { NoMassMessages = true });
            Assert.Equal(new[] { (byte)'A', (byte)'!' }, Raw(test.Fake, 2).Single());
            test.Fake.ClearCalls();
            test.Environment.SetOptions(o => o with { ShowBuddies = true });
            Assert.EndsWith("showbuddies=true", Encoding.Latin1.GetString(Raw(test.Fake, 154).Single()), StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public void Mass_pms_if_client_is_on_follow_the_own_client_flag()
    {
        (TestSession test, SessionController controller, _) = Start(o => o with { NoMassIfClientOn = true });
        using (test)
        using (controller)
        {
            test.Fake.PlayerList.Add(Player(7, "tester"));
            test.Fake.ClearCalls();
            test.Fake.RaisePlayerPropertiesChanged(7, [(byte)'q', 0x20]);
            Assert.Equal(new[] { (byte)'A', (byte)'!' }, Raw(test.Fake, 2).Single());
            test.Fake.ClearCalls();
            test.Fake.RaisePlayerPropertiesChanged(7, [(byte)'q', 0x21]);
            Assert.Equal(new[] { (byte)'A', (byte)' ' }, Raw(test.Fake, 2).Single());
        }
    }

    [AvaloniaFact]
    public void A_keep_alive_is_sent_every_sixty_seconds_while_logged_in()
    {
        (TestSession test, SessionController controller, Clock clock) = Start();
        using (test)
        using (controller)
        {
            controller.Tick();
            Assert.Empty(Raw(test.Fake, SessionController.KeepAlivePacketId));
            clock.Advance(TimeSpan.FromSeconds(59));
            controller.Tick();
            Assert.Empty(Raw(test.Fake, SessionController.KeepAlivePacketId));
            clock.Advance(TimeSpan.FromSeconds(1));
            controller.Tick();
            Assert.Single(Raw(test.Fake, SessionController.KeepAlivePacketId));
            Assert.Empty(Raw(test.Fake, SessionController.KeepAlivePacketId)[0]);
            clock.Advance(TimeSpan.FromSeconds(60));
            controller.Tick();
            Assert.Equal(2, Raw(test.Fake, SessionController.KeepAlivePacketId).Count);

            test.Fake.IsAuthenticated = false;
            clock.Advance(TimeSpan.FromSeconds(120));
            controller.Tick();
            Assert.Equal(2, Raw(test.Fake, SessionController.KeepAlivePacketId).Count);
            Assert.Null(controller.NextKeepAlive);
        }
    }

    [AvaloniaFact]
    public void Inactivity_sets_the_away_nickname_and_activity_restores_it()
    {
        (TestSession test, SessionController controller, Clock clock) = Start(o => o with { AfkEnabled = true, AfkTimeout = 15 });
        using (test)
        using (controller)
        {
            test.Fake.ClearCalls();
            clock.Advance(TimeSpan.FromMinutes(14));
            controller.Tick();
            Assert.False(controller.IsAway);
            clock.Advance(TimeSpan.FromMinutes(1));
            controller.Tick();
            Assert.True(controller.IsAway);
            Assert.Equal("Tester - Away", test.Fake.LastCall("SetNicknameAsync")!.Argument<string>(0));
            Assert.Equal("Tester - Away", test.Context.Nickname);
            Assert.Equal(new[] { (byte)'U', (byte)33 }, Raw(test.Fake, 2).Last());

            test.Shell.NoteActivity();
            controller.Tick();
            Assert.False(controller.IsAway);
            Assert.Equal("Tester", test.Fake.LastCall("SetNicknameAsync")!.Argument<string>(0));
            Assert.Equal("Tester", test.Context.Nickname);
            Assert.Equal(new[] { (byte)'U', (byte)32 }, Raw(test.Fake, 2).Last());
        }
    }

    [AvaloniaFact]
    public void An_away_nickname_is_not_marked_again_and_turning_afk_off_restores()
    {
        (TestSession test, SessionController controller, Clock clock) = Start(o => o with { AfkEnabled = true, AfkTimeout = 5 });
        using (test)
        using (controller)
        {
            clock.Advance(TimeSpan.FromMinutes(6));
            controller.Tick();
            Assert.True(controller.IsAway);
            test.Environment.SetOptions(o => o with { AfkEnabled = false });
            Assert.False(controller.IsAway);
            Assert.Equal("Tester", test.Context.Nickname);

            test.Context.SetNickname("Tester (busy)");
            test.Fake.ClearCalls();
        }
        using var busy = new TestSession(nickname: "Mod (busy)", options: o => o with { AfkEnabled = true, AfkTimeout = 1 });
        var time = new Clock();
        busy.Shell.LastActivity = time.Now;
        using var second = new SessionController(busy.Context, () => time.Now);
        second.Start(timer: false);
        time.Advance(TimeSpan.FromMinutes(5));
        second.Tick();
        Assert.False(second.IsAway);
    }

    [AvaloniaFact]
    public void The_nc_is_connected_when_the_server_names_it_and_again_after_it_dropped()
    {
        (TestSession test, SessionController controller, Clock clock) = Start();
        using (test)
        using (controller)
        {
            controller.Tick();
            Assert.False(test.Fake.WasCalled("ConnectNcAsync"));
            test.Fake.NcServerAddress = "127.0.0.1,14901";
            controller.Tick();
            Assert.Single(test.Fake.CallsTo("ConnectNcAsync"));
            Assert.True(test.Fake.IsNcAuthenticated);
            controller.Tick();
            Assert.True(controller.IsNcActive);

            test.Fake.RaiseNcDisconnected("gone");
            controller.Tick();
            Assert.Single(test.Fake.CallsTo("ConnectNcAsync"));
            clock.Advance(SessionController.NcReconnectDelay);
            test.Fake.NcConnectResult = false;
            controller.Tick();
            Assert.Equal(2, test.Fake.CallsTo("ConnectNcAsync").Count);
            clock.Advance(SessionController.NcRetryInterval);
            test.Fake.NcConnectResult = true;
            controller.Tick();
            Assert.Equal(3, test.Fake.CallsTo("ConnectNcAsync").Count);
            Assert.True(test.Fake.IsNcAuthenticated);
        }
    }

    [AvaloniaFact]
    public void A_nc_that_keeps_dropping_soon_after_it_connects_is_retried_after_longer_waits()
    {
        (TestSession test, SessionController controller, Clock clock) = Start();
        using (test)
        using (controller)
        {
            test.Fake.NcServerAddress = "127.0.0.1,14901";
            controller.Tick();
            controller.Tick();
            Assert.Single(test.Fake.CallsTo("ConnectNcAsync"));

            // First drop: the usual one second.
            test.Fake.RaiseNcDisconnected("gone");
            clock.Advance(SessionController.NcReconnectDelay);
            controller.Tick();
            Assert.Equal(2, test.Fake.CallsTo("ConnectNcAsync").Count);
            controller.Tick();

            // Second drop right after the reconnect: five seconds, not one.
            test.Fake.RaiseNcDisconnected("gone again");
            clock.Advance(SessionController.NcReconnectDelay);
            controller.Tick();
            Assert.Equal(2, test.Fake.CallsTo("ConnectNcAsync").Count);
            clock.Advance(TimeSpan.FromSeconds(4));
            controller.Tick();
            Assert.Equal(3, test.Fake.CallsTo("ConnectNcAsync").Count);
            controller.Tick();

            // A connection that stays up for a minute starts the series again.
            clock.Advance(SessionController.NcStableAfter);
            controller.Tick();
            test.Fake.RaiseNcDisconnected("gone later");
            clock.Advance(SessionController.NcReconnectDelay);
            controller.Tick();
            Assert.Equal(4, test.Fake.CallsTo("ConnectNcAsync").Count);
        }
    }

    [AvaloniaFact]
    public void Without_auto_reconnect_a_dropped_nc_stays_down()
    {
        (TestSession test, SessionController controller, Clock clock) = Start(o => o with { AutoReconnectNc = false });
        using (test)
        using (controller)
        {
            test.Fake.NcServerAddress = "127.0.0.1,14901";
            controller.Tick();
            controller.Tick();
            test.Fake.RaiseNcDisconnected("gone");
            clock.Advance(TimeSpan.FromSeconds(30));
            controller.Tick();
            Assert.Single(test.Fake.CallsTo("ConnectNcAsync"));
        }
    }

    [AvaloniaFact]
    public void Nc_disconnect_stops_reconnects_and_nc_connect_queries_the_npc_server()
    {
        (TestSession test, SessionController controller, Clock clock) = Start();
        using (test)
        using (controller)
        {
            test.Fake.NcServerAddress = "127.0.0.1,14901";
            controller.Tick();
            controller.DisconnectNc();
            Assert.True(controller.IsNcManuallyDisconnected);
            Assert.False(controller.IsNcActive);
            clock.Advance(TimeSpan.FromMinutes(1));
            controller.Tick();
            Assert.Single(test.Fake.CallsTo("ConnectNcAsync"));

            test.Fake.PlayerList.Add(Player(200, "(npcserver)(server)"));
            controller.ReconnectNc();
            byte[] query = Raw(test.Fake, 94).Single();
            Assert.Equal(new byte[] { (byte)((200 >> 7) + 32), (byte)((200 & 127) + 32) }, query[..2]);
            Assert.Equal("location", Encoding.Latin1.GetString(query[2..]));
            controller.Tick();
            Assert.Single(test.Fake.CallsTo("ConnectNcAsync"));
            clock.Advance(SessionController.NcQueryDelay);
            controller.Tick();
            Assert.Equal(2, test.Fake.CallsTo("ConnectNcAsync").Count);
        }
    }

    [AvaloniaFact]
    public async Task A_disconnect_asks_once_and_reconnects_through_the_shell()
    {
        (TestSession test, SessionController controller, _) = Start();
        using (test)
        using (controller)
        {
            var asked = new List<string>();
            var answer = new TaskCompletionSource<bool?>();
            controller.AskReconnect = reason =>
            {
                asked.Add(reason);
                return answer.Task;
            };
            var second = new FakeRcSession("tester");
            test.Shell.Reconnect = context =>
            {
                context.Replace(second, context.Login);
                return Task.FromResult(ConnectResult.Ok);
            };
            test.Fake.RaiseDisconnected("Server shut down");
            test.Fake.RaiseDisconnected("again");
            Assert.Equal(["Server shut down"], asked);
            answer.SetResult(true);
            await TestUi.WaitUntilAsync(() => test.Shell.Requests.Contains("Reconnect"));
            Assert.Same(second, test.Context.Session);
            Assert.True(second.WasCalled("IrcLoginAsync"));
            Assert.Single(asked);
        }
    }

    [AvaloniaFact]
    public async Task Closing_the_disconnect_dialog_opens_the_server_list_and_a_requested_disconnect_asks_nothing()
    {
        (TestSession test, SessionController controller, _) = Start();
        using (test)
        using (controller)
        {
            int asked = 0;
            controller.AskReconnect = _ =>
            {
                asked++;
                return Task.FromResult<bool?>(false);
            };
            test.Fake.RaiseDisconnected("bye");
            await TestUi.WaitUntilAsync(() => test.Shell.Requests.Contains("ShowServerList"));
            Assert.Equal(1, asked);
            Assert.DoesNotContain("Reconnect", test.Shell.Requests);
        }
        (TestSession quiet, SessionController quietController, _) = Start();
        using (quiet)
        using (quietController)
        {
            int asked = 0;
            quietController.AskReconnect = _ =>
            {
                asked++;
                return Task.FromResult<bool?>(false);
            };
            quiet.Fake.Disconnect();
            TestUi.Settle();
            Assert.Equal(0, asked);
        }
    }

    [AvaloniaFact]
    public async Task A_failed_reconnect_asks_again_with_the_reason()
    {
        (TestSession test, SessionController controller, _) = Start();
        using (test)
        using (controller)
        {
            var reasons = new List<string>();
            controller.AskReconnect = reason =>
            {
                reasons.Add(reason);
                return Task.FromResult<bool?>(reasons.Count == 1);
            };
            test.Fake.RaiseDisconnected("first");
            await TestUi.WaitUntilAsync(() => reasons.Count == 2);
            Assert.Equal(["first", "not connected in tests"], reasons);
        }
    }

    [AvaloniaFact]
    public void Events_of_a_replaced_connection_do_nothing()
    {
        (TestSession test, SessionController controller, _) = Start();
        using (test)
        using (controller)
        {
            int asked = 0;
            controller.AskReconnect = _ =>
            {
                asked++;
                return Task.FromResult<bool?>(false);
            };
            FakeRcSession old = test.Fake;
            var second = new FakeRcSession("tester");
            test.Context.Replace(second, test.Context.Login);
            old.ClearCalls();
            old.RaiseConnected();
            old.RaiseDisconnected("old one");
            TestUi.Settle();
            Assert.Equal(0, asked);
            Assert.Empty(old.Calls);
            Assert.True(second.WasCalled("IrcLoginAsync"));
        }
    }

    [AvaloniaFact]
    public async Task The_reconnect_command_suppresses_the_old_connections_disconnect()
    {
        (TestSession test, SessionController controller, _) = Start();
        using (test)
        using (controller)
        {
            int asked = 0;
            controller.AskReconnect = _ =>
            {
                asked++;
                return Task.FromResult<bool?>(false);
            };
            var lines = new List<string>();
            controller.ChatLine += (_, line) => lines.Add(line);
            FakeRcSession old = test.Fake;
            test.Shell.Reconnect = context =>
            {
                old.RaiseDisconnected("replaced");
                context.Replace(new FakeRcSession("tester"), context.Login);
                return Task.FromResult(ConnectResult.Ok);
            };
            await controller.ReconnectServerAsync();
            Assert.Equal(0, asked);
            Assert.Empty(lines);

            test.Shell.Reconnect = _ => Task.FromResult(ConnectResult.Failed("refused"));
            await controller.ReconnectServerAsync();
            Assert.Equal(["refused"], lines);
        }
    }

    [AvaloniaFact]
    public void Joined_channels_are_joined_again_on_a_new_connection()
    {
        (TestSession test, SessionController controller, _) = Start();
        using (test)
        using (controller)
        {
            controller.NoteChannelJoined("#graal");
            var second = new FakeRcSession("tester");
            test.Context.Replace(second, test.Context.Login);
            Assert.Equal("#graal", second.LastCall("IrcJoinAsync")!.Argument<string>(0));
            controller.LeaveChannel("#graal", part: true);
            Assert.Equal("#graal", second.LastCall("IrcPartAsync")!.Argument<string>(0));
            Assert.Empty(controller.JoinedIrcChannels);
        }
    }

    [AvaloniaFact]
    public void A_nickname_option_change_is_sent_at_once()
    {
        (TestSession test, SessionController controller, _) = Start();
        using (test)
        using (controller)
        {
            test.Environment.SetOptions(o => o with { Nickname = "NewNick" });
            Assert.Equal("NewNick", test.Fake.LastCall("SetNicknameAsync")!.Argument<string>(0));
            Assert.Equal("NewNick", test.Context.Nickname);
        }
    }

    [AvaloniaFact]
    public void Nothing_runs_after_dispose()
    {
        (TestSession test, SessionController controller, Clock clock) = Start();
        using (test)
        {
            controller.Dispose();
            test.Fake.ClearCalls();
            clock.Advance(TimeSpan.FromMinutes(5));
            controller.Tick();
            test.Fake.RaiseConnected();
            test.Environment.SetOptions(o => o with { NoMassMessages = true });
            Assert.Empty(test.Fake.Calls);
        }
    }

    [Fact]
    public void The_client_flag_is_read_from_the_property_stream()
    {
        Assert.Null(SessionController.ClientConnected([]));
        Assert.True(SessionController.ClientConnected([(byte)'q', 0x21]));
        Assert.False(SessionController.ClientConnected([(byte)'#', 1, 2, 3, (byte)'q', 0x22]));
        Assert.True(SessionController.ClientConnected([(byte)' ', 0x22, (byte)'a', (byte)'b', (byte)'q', 0x29]));
    }
}
