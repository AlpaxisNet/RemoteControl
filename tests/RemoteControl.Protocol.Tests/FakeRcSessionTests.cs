using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Protocol.Text;

namespace RemoteControl.Protocol.Tests;

public sealed class FakeRcSessionTests
{
    [Fact]
    public async Task Calls_are_recorded_with_their_arguments()
    {
        using var session = new FakeRcSession();
        Assert.True(await session.SendRcChatAsync("/stats"));
        Assert.True(await session.WarpPlayerAsync(3, "a.nw", 30, 31.5));
        Assert.True(await session.SendRawPacketAsync(79, new byte[] { 1, 2 }));
        RecordedCall warp = Assert.Single(session.CallsTo(nameof(IRcSession.WarpPlayerAsync)));
        Assert.Equal(new object?[] { 3, "a.nw", 30.0, 31.5 }, warp.Arguments);
        Assert.Equal("/stats", session.LastCall(nameof(IRcSession.SendRcChatAsync))!.Argument<string>(0));
        Assert.Equal(new byte[] { 1, 2 }, session.LastCall(nameof(IRcSession.SendRawPacketAsync))!.Argument<byte[]>(1));
        session.SendResult = false;
        Assert.False(await session.RequestServerOptionsAsync());
    }

    [Fact]
    public void Raise_methods_update_the_caches_and_raise()
    {
        using var session = new FakeRcSession();
        var joined = new List<RcPlayer>();
        session.PlayerJoined += (_, args) => joined.Add(args.Player);
        var player = new RcPlayer(4, "Bob", "Bobby", "a.nw", PlayerListCategory.None, 0, "", new Dictionary<int, string>());
        session.RaisePlayerJoined(player);
        Assert.Equal(new[] { player }, joined);
        Assert.Equal(new[] { player }, session.Players);
        session.RaisePlayerLeft(player);
        Assert.Empty(session.Players);

        string? options = null;
        session.ServerOptionsReceived += (_, args) => options = args.Text;
        session.RaiseServerOptions("a=1");
        Assert.Equal("a=1", options);
        Assert.Equal("a=1", session.ServerOptions);

        PlayerAttributesEventArgs? attributes = null;
        session.PlayerAttributesReceived += (_, args) => attributes = args;
        session.RaisePlayerAttributes(new PlayerAttributes { Account = "Bob", Level = "x.nw" });
        Assert.Contains("Level: x.nw", attributes!.EditorText);
    }

    [Fact]
    public async Task File_browser_views_record_separately_and_answer_from_remote_files()
    {
        using var session = new FakeRcSession();
        session.FakeSyncFileBrowser.RemoteFiles["levels/a.nw"] = "a"u8.ToArray();
        byte[]? received = null;
        session.SyncFileBrowser.FileReceived += (_, args) => received = args.Content;
        Assert.Equal("a"u8.ToArray(), await session.SyncFileBrowser.DownloadAsync("levels/a.nw"));
        Assert.Equal("a"u8.ToArray(), received);
        Assert.Null(await session.FileBrowser.DownloadAsync("levels/a.nw"));
        Assert.True(session.WasCalled("SyncFileBrowser.DownloadAsync"));
        Assert.True(await session.FileBrowser.UploadAsync("levels/b.nw", "b"u8.ToArray()));
        Assert.Equal("b"u8.ToArray(), session.FakeFileBrowser.RemoteFiles["levels/b.nw"]);
    }

    [Fact]
    public async Task After_Disconnected_the_fake_raises_nothing_and_returns_false()
    {
        using var session = new FakeRcSession();
        int chats = 0, disconnects = 0;
        session.RcChatReceived += (_, _) => chats++;
        session.Disconnected += (_, _) => disconnects++;
        session.RaiseDisconnected("bye");
        session.RaiseDisconnected("again");
        session.RaiseRcChat("late");
        Assert.Equal((0, 1), (chats, disconnects));
        Assert.False(await session.SendRcChatAsync("x"));
        Assert.False(session.IsConnected);
    }
}
