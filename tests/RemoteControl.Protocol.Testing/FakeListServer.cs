using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Testing;

/// <summary>What a fake list server received at a login.</summary>
/// <param name="EditionKey">The key of the edition packet.</param>
/// <param name="Edition">The edition text (<c>"G3D30123rc2"</c>).</param>
/// <param name="Account">The account.</param>
/// <param name="Password">The password.</param>
public sealed record ListServerLogin(int EditionKey, string Edition, string Account, string Password);

/// <summary>
/// A list server on 127.0.0.1 that answers a login as the Alpaxis hub does: the server list in one frame, then the status, site,
/// donation and "call us" texts in a second frame; or an error packet. It never closes the connection itself.
/// </summary>
public sealed class FakeListServer : LoopbackServer
{
    private readonly List<ListServerLogin> _logins = new();

    /// <summary>The servers to list.</summary>
    public List<ServerInfo> Servers { get; } = new();

    /// <summary>When set, a login is answered with this error text instead of the list.</summary>
    public string? Error { get; set; }

    /// <summary>The status text (sent with <c>\r</c> line breaks, as the hub does).</summary>
    public string StatusText { get; set; } = "Welcome to the test hub.\rThere are 1 server(s) online.";

    /// <summary>The site address.</summary>
    public string SiteUrl { get; set; } = "https://example.invalid/";

    /// <summary>The donation address.</summary>
    public string DonateUrl { get; set; } = "https://example.invalid/donate";

    /// <summary>When true, the server does not answer a login at all (for timeout tests).</summary>
    public bool Silent { get; set; }

    /// <summary>The logins received.</summary>
    public IReadOnlyList<ListServerLogin> Logins
    {
        get
        {
            lock (_logins) return _logins.ToArray();
        }
    }

    internal override IFrameCodec CreateCodec() => new ServerGen5Codec();

    /// <inheritdoc />
    protected override void OnPacket(FakeConnection connection, ReceivedPacket packet)
    {
        var codec = (ServerGen5Codec)connection.Codec;
        if (!codec.HasKey && packet.Id == 7)
        {
            var reader = packet.Reader();
            reader.TryReadGByte(out int key);
            connection.State["key"] = key;
            connection.State["edition"] = reader.ReadRemainingText();
            codec.SetKey((byte)key);
            return;
        }

        if (packet.Id != 1) return;
        var login = packet.Reader();
        login.TryReadGString(out string account);
        login.TryReadGString(out string password);
        lock (_logins)
            _logins.Add(new ListServerLogin(connection.State.TryGetValue("key", out object? key) ? (int)key : -1,
                connection.State.TryGetValue("edition", out object? edition) ? (string)edition : string.Empty, account, password));
        if (Silent) return;
        if (Error is not null)
        {
            _ = connection.SendAsync((4, RcText.GetBytes(Error)));
            return;
        }

        var list = new PacketWriter().WriteGByte(Servers.Count);
        foreach (ServerInfo server in Servers)
        {
            list.WriteGByte(8).WriteGString(server.Name).WriteGString(server.Language).WriteGString(server.Description).WriteGString(server.Homepage)
                .WriteGString(server.Version).WriteGString(server.Players.ToString(System.Globalization.CultureInfo.InvariantCulture)).WriteGString(server.Host)
                .WriteGString(server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        _ = SendReplyAsync(connection, list.ToArray());
    }

    private async Task SendReplyAsync(FakeConnection connection, byte[] list)
    {
        await connection.SendAsync((0, list)).ConfigureAwait(false);
        await connection.SendInOneFrameAsync((2, RcText.GetBytes(StatusText)), (3, RcText.GetBytes(SiteUrl)), (5, RcText.GetBytes(DonateUrl)),
            (6, RcText.GetBytes("000"))).ConfigureAwait(false);
    }
}
