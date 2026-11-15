using System.Globalization;
using RemoteControl.Protocol.Net;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.ListServer;

/// <summary>One game server of the list server's list.</summary>
/// <param name="Name">The name as the list server sends it, with its type prefix (<c>"P "</c> gold, <c>"U "</c> hidden, <c>"3 "</c> 3D, <c>"H "</c> hosted).</param>
/// <param name="Language">The language.</param>
/// <param name="Description">The description.</param>
/// <param name="Homepage">The homepage.</param>
/// <param name="Version">The version text.</param>
/// <param name="Players">The player count.</param>
/// <param name="Host">The host to connect to.</param>
/// <param name="Port">The port to connect to.</param>
public sealed record ServerInfo(string Name, string Language, string Description, string Homepage, string Version, int Players, string Host, int Port)
{
    /// <summary>The kind of server, from the name's two-character prefix.</summary>
    public ServerKind Kind => Name.Length >= 2 && Name[1] == ' '
        ? Name[0] switch { 'P' => ServerKind.Gold, 'U' => ServerKind.Hidden, '3' => ServerKind.Silver, 'H' => ServerKind.Hosted, _ => ServerKind.Bronze }
        : ServerKind.Bronze;

    /// <summary>The name without its type prefix, for display.</summary>
    public string DisplayName => Name.Length >= 2 && Name[1] == ' ' && "PU3H".Contains(Name[0], StringComparison.Ordinal) ? Name[2..] : Name;

    /// <summary>A server entry for a direct connection by host and port (no list server).</summary>
    /// <param name="host">The host.</param>
    /// <param name="port">The port.</param>
    /// <returns>The entry; its name is the host.</returns>
    public static ServerInfo Direct(string host, int port) => new(host, string.Empty, string.Empty, string.Empty, string.Empty, 0, host, port);
}

/// <summary>The kind of a listed server, from its name prefix.</summary>
public enum ServerKind
{
    /// <summary>No prefix or <c>"H "</c> in the C++ program: a classic server.</summary>
    Bronze,
    /// <summary><c>"P "</c>: gold.</summary>
    Gold,
    /// <summary><c>"3 "</c>: 3D (shown as silver).</summary>
    Silver,
    /// <summary><c>"U "</c>: hidden, staff only.</summary>
    Hidden,
    /// <summary><c>"H "</c>: hosted.</summary>
    Hosted,
}

/// <summary>What a list-server login produced.</summary>
public sealed class ListServerResult
{
    internal ListServerResult(bool success, string? error, IReadOnlyList<ServerInfo> servers, string? statusText, string? siteUrl, string? donateUrl)
    {
        Success = success;
        Error = error;
        Servers = servers;
        StatusText = statusText;
        SiteUrl = siteUrl;
        DonateUrl = donateUrl;
    }

    /// <summary>Whether the list server accepted the login and sent its list.</summary>
    public bool Success { get; }

    /// <summary>The list server's error text, or a description of what failed; <c>null</c> on success.</summary>
    public string? Error { get; }

    /// <summary>The servers (empty on failure).</summary>
    public IReadOnlyList<ServerInfo> Servers { get; }

    /// <summary>The status text ("Welcome to ..."), with its carriage returns turned into newlines, if the list server sent one.</summary>
    public string? StatusText { get; }

    /// <summary>The site address, if sent.</summary>
    public string? SiteUrl { get; }

    /// <summary>The donation address, if sent.</summary>
    public string? DonateUrl { get; }
}

/// <summary>
/// Logs in at a list server and fetches the server list. The client sends the edition frame (<c>"G3D30123" "rc2"</c>, key 4) and the
/// login (packet 1: account and password), reads frames until it has the list or an error, waits briefly for the status texts that
/// follow the list, and closes. Every step has a timeout and honours the cancellation token; nothing blocks the caller.
/// </summary>
public static class ListServerClient
{
    /// <summary>The default time a whole login may take.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private const int ServersPacket = 0;
    private const int StatusPacket = 2;
    private const int SiteUrlPacket = 3;
    private const int ErrorPacket = 4;
    private const int DonateUrlPacket = 5;

    /// <summary>Logs in and fetches the list.</summary>
    /// <param name="host">The list server's host.</param>
    /// <param name="port">The list server's port.</param>
    /// <param name="account">The account.</param>
    /// <param name="password">The password.</param>
    /// <param name="timeout">How long the whole exchange may take; <see cref="DefaultTimeout"/> when <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels the login.</param>
    /// <returns>The result; failures are reported in it, not thrown (except cancellation by <paramref name="cancellationToken"/>).</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<ListServerResult> LoginAsync(string host, int port, string account, string password, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan limit = timeout ?? DefaultTimeout;
        if (RcText.ByteCount(account) > GEncoding.MaxGStringLength || RcText.ByteCount(password) > GEncoding.MaxGStringLength)
            return Failure("The account or password is longer than 223 characters.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);
        FramedConnection? connection = null;
        try
        {
            System.Net.Sockets.TcpClient client;
            try
            {
                client = await FramedConnection.ConnectAsync(host, port, limit, deadline.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return Failure("Failed to connect to listserver: timed out");
            }
            catch (System.Net.Sockets.SocketException exception)
            {
                return Failure(exception.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound
                    ? "Failed to resolve host"
                    : $"Failed to connect to listserver: {exception.Message}");
            }

            connection = new FramedConnection(client, new ListServerClientCodec());
            var packets = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
            var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.PacketReceived = packet => packets.Writer.TryWrite(packet);
            connection.Closed = reason =>
            {
                closed.TrySetResult(reason);
                packets.Writer.TryComplete();
            };
            connection.Start();

            byte[] edition = new PacketWriter().WriteGByte(7).WriteGByte(ListServerClientCodec.Key).WriteText("G3D30123").WriteText("rc2").ToArray();
            byte[] login = new PacketWriter().WriteGString(account).WriteGString(password).ToArray();
            SendOutcome editionSent = await connection.SendAsync(new[] { PlainPacket(edition) }, plainFirst: true).ConfigureAwait(false);
            if (!editionSent.Success) return Failure("Failed to send edition packet");
            SendOutcome loginSent = await connection.SendAsync(new[] { Frames.Packet(1, login) }).ConfigureAwait(false);
            if (!loginSent.Success) return Failure("Failed to send login packet");

            var servers = new List<ServerInfo>();
            bool haveServers = false;
            string? status = null, site = null, donate = null;
            CancellationToken token = deadline.Token;
            using var grace = new CancellationTokenSource();
            using var waitToken = CancellationTokenSource.CreateLinkedTokenSource(token, grace.Token);
            while (true)
            {
                byte[] packet;
                try
                {
                    if (!await packets.Reader.WaitToReadAsync(waitToken.Token).ConfigureAwait(false)) break;
                    if (!packets.Reader.TryRead(out packet!)) continue;
                }
                catch (OperationCanceledException) when (grace.IsCancellationRequested || (haveServers && !cancellationToken.IsCancellationRequested))
                {
                    break;
                }

                var reader = new PacketReader(packet);
                if (!reader.TryReadGByte(out int type)) continue;
                switch (type)
                {
                    case ServersPacket:
                        ParseServers(reader, servers);
                        haveServers = true;
                        grace.CancelAfter(TimeSpan.FromMilliseconds(400));
                        break;
                    case StatusPacket:
                        status = reader.ReadRemainingText().Replace("\r", "\n", StringComparison.Ordinal);
                        break;
                    case SiteUrlPacket:
                        site = reader.ReadRemainingText();
                        break;
                    case DonateUrlPacket:
                        donate = reader.ReadRemainingText();
                        break;
                    case ErrorPacket:
                        string text = reader.ReadRemainingText();
                        return Failure(text.Length > 0 ? text : "Server rejected connection");
                    default:
                        if (!haveServers) return Failure("Server rejected connection");
                        break;
                }

                if (haveServers && status is not null && site is not null && donate is not null) break;
            }

            if (haveServers) return new ListServerResult(true, null, servers, status, site, donate);
            string reason = closed.Task.IsCompleted ? closed.Task.Result : "no answer";
            return Failure($"Failed to read packet data ({reason})");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("The list server did not answer in time.");
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private static byte[] PlainPacket(byte[] payload)
    {
        // The edition payload is sent as it is (its first byte is the "packet id" 7), followed by a newline.
        byte[] packet = new byte[payload.Length + 1];
        payload.CopyTo(packet, 0);
        packet[^1] = (byte)'\n';
        return packet;
    }

    private static void ParseServers(PacketReader reader, List<ServerInfo> servers)
    {
        if (!reader.TryReadGByte(out int count)) return;
        for (int index = 0; index < count && !reader.AtEnd; index++)
        {
            if (!reader.TryReadGByte(out int attributeCount)) break;
            var attributes = new List<string>();
            for (int attribute = 0; attribute < attributeCount && !reader.AtEnd; attribute++)
            {
                if (!reader.TryReadGString(out string value)) break;
                attributes.Add(value);
            }

            if (attributes.Count < 8) continue;
            servers.Add(new ServerInfo(attributes[0], attributes[1], attributes[2], attributes[3], attributes[4], ParseInt(attributes[5]), attributes[6],
                ParseInt(attributes[7])));
        }
    }

    internal static int ParseInt(string text)
    {
        // atoi: leading digits, optional sign, 0 when there are none.
        text = text.Trim();
        int end = 0;
        if (end < text.Length && (text[end] == '-' || text[end] == '+')) end++;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        return int.TryParse(text.AsSpan(0, end), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }

    private static ListServerResult Failure(string error) => new(false, error, Array.Empty<ServerInfo>(), null, null, null);
}
