using System.Net.Sockets;
using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Net;
using RemoteControl.Protocol.Text;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Session;

/// <summary>
/// The Remote Control session over real sockets (see <see cref="IRcSession"/>). Sockets are read on background tasks; every packet is
/// handed to the session's context, which parses it, updates the caches and raises the events. Each connection has one writer
/// that sends frames in the order they were asked for.
/// </summary>
public sealed partial class RcSession : IRcSession
{
    /// <summary>The Gen-5 key the RC login announces (sent as <c>'v'</c>, 0x56 + 32).</summary>
    internal const byte RcKey = 0x56;

    private static readonly Task<bool> FalseTask = Task.FromResult(false);

    private readonly RcSessionOptions _options;
    private readonly EventPoster _poster;
    private readonly RcFileBrowser _browser;
    private readonly RcFileBrowser _sync;
    private volatile FramedConnection? _rc;
    private volatile FramedConnection? _nc;
    private int _used;
    private volatile bool _closed;
    private volatile bool _disposed;
    private volatile bool _connected;
    private volatile bool _authenticated;
    private volatile bool _ncConnected;
    private volatile bool _ncAuthenticated;
    private volatile bool _ncConnecting;
    private volatile string? _lastError;
    private volatile string? _ncServerAddress;
    private volatile ServerInfo? _server;
    private TaskCompletionSource<ConnectResult>? _login;
    private TaskCompletionSource<bool>? _ncLogin;
    private int _ncGeneration;

    /// <summary>Creates a session; nothing is connected until <see cref="ConnectAsync(ServerInfo, CancellationToken)"/>.</summary>
    /// <param name="options">The account, the context and the time limits.</param>
    public RcSession(RcSessionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _poster = EventPoster.Create(options.SynchronizationContext, options.Post, "Remote Control session");
        _browser = new RcFileBrowser(this, isSync: false);
        _sync = new RcFileBrowser(this, isSync: true);
    }

    /// <inheritdoc />
    public string Account => _options.Account;

    /// <inheritdoc />
    public ServerInfo? Server => _server;

    /// <inheritdoc />
    public bool IsConnected => _connected && !_closed;

    /// <inheritdoc />
    public bool IsAuthenticated => _authenticated && !_closed;

    /// <inheritdoc />
    public bool IsNcConnected => _ncConnected && !_closed;

    /// <inheritdoc />
    public bool IsNcAuthenticated => _ncAuthenticated && !_closed;

    /// <inheritdoc />
    public bool HasNcServer => !string.IsNullOrEmpty(_ncServerAddress);

    /// <inheritdoc />
    public string? NcServerAddress => _ncServerAddress;

    /// <inheritdoc />
    public bool UseNewBanProtocol { get; set; } = true;

    /// <inheritdoc />
    public string? LastError => _lastError;

    /// <inheritdoc />
    public IRcFileBrowser FileBrowser => _browser;

    /// <inheritdoc />
    public IRcFileBrowser SyncFileBrowser => _sync;

    internal RcSessionOptions Options => _options;

    internal bool IsClosed => _closed;

    // ---- Events ------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public event EventHandler? Connected;

    /// <inheritdoc />
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <inheritdoc />
    public event EventHandler? NcConnected;

    /// <inheritdoc />
    public event EventHandler<DisconnectedEventArgs>? NcDisconnected;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? ProtocolError;

    /// <inheritdoc />
    public event EventHandler<RawPacketEventArgs>? RawPacketReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? RcChatReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? NcChatReceived;

    /// <inheritdoc />
    public event EventHandler<PrivateMessageEventArgs>? PrivateMessageReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? AdminMessageReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? ToAllReceived;

    /// <inheritdoc />
    public event EventHandler<IrcMessageEventArgs>? IrcMessageReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? ServerTextReceived;

    /// <inheritdoc />
    public event EventHandler<ServerDataEventArgs>? ServerDataReceived;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<int>>? WorldTimeReceived;

    /// <inheritdoc />
    public event EventHandler<PlayerEventArgs>? PlayerJoined;

    /// <inheritdoc />
    public event EventHandler<PlayerEventArgs>? PlayerUpdated;

    /// <inheritdoc />
    public event EventHandler<PlayerEventArgs>? PlayerLeft;

    /// <inheritdoc />
    public event EventHandler? ChannelsChanged;

    /// <inheritdoc />
    public event EventHandler<PlayerPropertyChangedEventArgs>? PlayerPropertyChanged;

    /// <inheritdoc />
    public event EventHandler<PlayerPropertiesChangedEventArgs>? PlayerPropertiesChanged;

    /// <inheritdoc />
    public event EventHandler<PlayerAttributesEventArgs>? PlayerAttributesReceived;

    /// <inheritdoc />
    public event EventHandler<PlayerRightsEventArgs>? PlayerRightsReceived;

    /// <inheritdoc />
    public event EventHandler<AccountTextEventArgs>? PlayerCommentsReceived;

    /// <inheritdoc />
    public event EventHandler<AccountDataEventArgs>? AccountReceived;

    /// <inheritdoc />
    public event EventHandler<ProfileEventArgs>? ProfileReceived;

    /// <inheritdoc />
    public event EventHandler<AccountListEventArgs>? AccountListReceived;

    /// <inheritdoc />
    public event EventHandler<BanDataEventArgs>? BanDataReceived;

    /// <inheritdoc />
    public event EventHandler<BanListEventArgs>? BanListReceived;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<int>>? PmServersUpdated;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<int>>? PmGuildsUpdated;

    /// <inheritdoc />
    public event EventHandler<PmServerPlayersEventArgs>? PmServerPlayersReceived;

    /// <inheritdoc />
    public event EventHandler? GameServersUpdated;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? ServerOptionsReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? ServerFlagsReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? FolderConfigReceived;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<long>>? MaxUploadFileSizeReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? FileBrowserMessageReceived;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<int>>? WeaponListReceived;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<string>>? ClassAdded;

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<string>>? ClassDeleted;

    /// <inheritdoc />
    public event EventHandler<NpcEventArgs>? NpcAdded;

    /// <inheritdoc />
    public event EventHandler<NpcEventArgs>? NpcDeleted;

    /// <inheritdoc />
    public event EventHandler<NpcTextEventArgs>? NpcAttributesReceived;

    /// <inheritdoc />
    public event EventHandler<NpcTextEventArgs>? NpcFlagsReceived;

    /// <inheritdoc />
    public event EventHandler<ScriptReceivedEventArgs>? ScriptReceived;

    /// <inheritdoc />
    public event EventHandler<LocalNpcsEventArgs>? LocalNpcsReceived;

    /// <inheritdoc />
    public event EventHandler<TextEventArgs>? LevelListReceived;

    // ---- Connecting --------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<ConnectResult> ConnectAsync(ServerInfo server, CancellationToken cancellationToken = default) =>
        server is null ? Task.FromResult(ConnectResult.Failed("No server.")) : ConnectCoreAsync(server, cancellationToken);

    /// <inheritdoc />
    public Task<ConnectResult> ConnectAsync(string host, int port, CancellationToken cancellationToken = default) =>
        ConnectCoreAsync(ServerInfo.Direct(host ?? string.Empty, port), cancellationToken);

    private async Task<ConnectResult> ConnectCoreAsync(ServerInfo server, CancellationToken cancellationToken)
    {
        if (_closed) return ConnectResult.Failed("The session is closed.");
        if (Interlocked.Exchange(ref _used, 1) != 0) return ConnectResult.Failed("A session connects once; create a new session to connect again.");
        _server = server;
        if (string.IsNullOrWhiteSpace(server.Host) || server.Port is <= 0 or > 65535)
            return Fail("Invalid server address.");

        byte[] login;
        try
        {
            login = new PacketWriter(128).WriteGByte(RcKey).WriteText(_options.RcVersion).WriteGString(_options.Account).WriteGString(_options.Password)
                .WriteText(_options.PcIdList ?? PcIdList.Generate()).ToArray();
        }
        catch (PacketTooLongException exception)
        {
            return Fail(exception.Message);
        }

        TcpClient client;
        try
        {
            client = await FramedConnection.ConnectAsync(server.Host, server.Port, _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Fail("The connect was cancelled.", requested: true);
        }
        catch (TimeoutException)
        {
            return Fail("Failed to connect to server: timed out");
        }
        catch (SocketException exception)
        {
            return Fail(exception.SocketErrorCode == SocketError.HostNotFound
                ? "Failed to resolve server host"
                : $"Failed to connect to server: {exception.Message}");
        }

        if (_closed)
        {
            client.Dispose();
            return ConnectResult.Failed(_lastError ?? "The session is closed.");
        }

        var connection = new FramedConnection(client, new GameClientCodec(RcKey)) { Log = Log };
        var loginWait = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _login = loginWait;
        connection.PacketReceived = packet => _poster.Post(() => HandleRcPacket(packet));
        connection.Closed = reason => _poster.Post(() => EndSession(reason, requested: false));
        _rc = connection;
        _connected = true;
        connection.Start();

        SendOutcome sent = await connection.SendAsync(new[] { Frames.Packet((int)ClientPacketId.ToAll, login) }, plainFirst: true).ConfigureAwait(false);
        if (!sent.Success) return Fail("Failed to send login");

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_options.LoginTimeout);
        using (limit.Token.Register(() => loginWait.TrySetResult(ConnectResult.Failed(cancellationToken.IsCancellationRequested
                   ? "The connect was cancelled."
                   : "The server did not answer the login in time."))))
        {
            ConnectResult result = await loginWait.Task.ConfigureAwait(false);
            if (!result.Success) return Fail(result.Error ?? "The login failed.", requested: cancellationToken.IsCancellationRequested);
            return result;
        }
    }

    private ConnectResult Fail(string reason, bool requested = false)
    {
        string final = _lastError is { } previous && _closed ? previous : reason;
        PostOrRun(() => EndSession(reason, requested));
        return ConnectResult.Failed(final);
    }

    /// <inheritdoc />
    public Task<bool> ConnectNcAsync(CancellationToken cancellationToken = default) =>
        Run(() => ConnectNcCoreAsync(cancellationToken), false);

    /// <inheritdoc />
    public bool DisconnectNc()
    {
        if (_closed) return false;
        bool open = _nc is not null || _ncConnected || _ncConnecting;
        PostOrRun(() => EndNc("Disconnected", requested: true, generation: _ncGeneration));
        return open;
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        if (_closed) return;
        PostOrRun(() => EndSession("Disconnected", requested: true));
    }

    /// <summary>Closes the sockets and stops the session without raising any event.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        bool wasClosed = _closed;
        _closed = true;
        if (!wasClosed)
        {
            CloseSockets();
            PostOrRun(AbortWaits);
        }

        _poster.Post(() => _poster.Dispose());
    }

    /// <summary>Ends the session: raises <see cref="Disconnected"/> once (unless disposed) and nothing after it. Runs on the context.</summary>
    internal void EndSession(string reason, bool requested)
    {
        if (_closed) return;
        _closed = true;
        _lastError = reason;
        _login?.TrySetResult(ConnectResult.Failed(reason));
        CleanUp();
        if (_disposed) return;
        EventHandler<DisconnectedEventArgs>? handler = Disconnected;
        try
        {
            handler?.Invoke(this, new DisconnectedEventArgs(reason, requested));
        }
        catch (Exception exception)
        {
            Log($"A Disconnected handler failed: {exception}");
        }
    }

    private void CleanUp()
    {
        CloseSockets();
        AbortWaits();
    }

    private void AbortWaits()
    {
        _login?.TrySetResult(ConnectResult.Failed(_lastError ?? "The session is closed."));
        _ncLogin?.TrySetResult(false);
        AbortTransfers();
    }

    private void CloseSockets()
    {
        _connected = false;
        _authenticated = false;
        _ncConnected = false;
        _ncAuthenticated = false;
        _ncConnecting = false;
        FramedConnection? rc = _rc, nc = _nc;
        if (rc is not null)
        {
            rc.Closed = null;
            rc.PacketReceived = null;
            rc.Close("Closed");
        }

        if (nc is not null)
        {
            nc.Closed = null;
            nc.PacketReceived = null;
            nc.Close("Closed");
        }
    }

    // ---- Context and sending helpers ---------------------------------------------------------------------------------------

    internal void Log(string line)
    {
        try
        {
            _options.Log?.Invoke(line);
        }
        catch (Exception)
        {
            // A log sink must not take the session down.
        }
    }

    /// <summary>Runs work on the context: inline when already there, else posted.</summary>
    internal void PostOrRun(Action action)
    {
        if (_poster.IsOnContext)
        {
            action();
            return;
        }

        if (!_poster.Post(action) && !_disposed) action();
    }

    internal void Post(Action action) => _poster.Post(action);

    /// <summary>Runs <paramref name="body"/> on the context and returns its result; <paramref name="fallback"/> once the session is closed.</summary>
    internal Task<T> Run<T>(Func<Task<T>> body, T fallback)
    {
        if (_closed) return Task.FromResult(fallback);
        if (_poster.IsOnContext) return Guard(body, fallback);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool posted = _poster.Post(() =>
        {
            if (_closed)
            {
                completion.TrySetResult(fallback);
                return;
            }

            Guard(body, fallback).ContinueWith(task => completion.TrySetResult(task.Result), TaskScheduler.Default);
        });
        if (!posted) completion.TrySetResult(fallback);
        return completion.Task;
    }

    private async Task<T> Guard<T>(Func<Task<T>> body, T fallback)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException) Log($"A session call failed: {exception}");
            return fallback;
        }
    }

    internal void Raise<T>(EventHandler<T>? handler, T args) where T : EventArgs
    {
        if (_closed || handler is null) return;
        try
        {
            handler(this, args);
        }
        catch (Exception exception)
        {
            Log($"An event handler failed: {exception}");
        }
    }

    internal void Raise(EventHandler? handler)
    {
        if (_closed || handler is null) return;
        try
        {
            handler(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            Log($"An event handler failed: {exception}");
        }
    }

    /// <summary>Reports a refused send or an unreadable packet.</summary>
    internal void Refuse(string message)
    {
        if (_closed) return;
        _lastError = message;
        Log(message);
        PostOrRun(() => Raise(ProtocolError, new TextEventArgs(message)));
    }

    /// <summary>Builds a payload; a value too long for its field is refused here.</summary>
    internal bool TryBuild(Action<PacketWriter>? build, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        try
        {
            var writer = new PacketWriter();
            build?.Invoke(writer);
            payload = writer.ToArray();
            return true;
        }
        catch (PacketTooLongException exception)
        {
            Refuse(exception.Message);
            return false;
        }
    }

    /// <summary>Sends packets on the RC connection, each in its own frame, without anything in between. On the context.</summary>
    internal Task<bool> SendRcNow(IReadOnlyList<byte[]> packets, Action<int>? progress = null)
    {
        FramedConnection? connection = _rc;
        if (_closed || connection is null || !_authenticated) return FalseTask;
        return Complete(connection.SendAsync(packets, progress: progress));
    }

    internal Task<bool> SendRcNow(ClientPacketId id, Action<PacketWriter>? build = null) =>
        TryBuild(build, out byte[] payload) ? SendRcNow(new[] { Frames.Packet((int)id, payload) }) : FalseTask;

    internal Task<bool> SendRc(ClientPacketId id, Action<PacketWriter>? build = null) => Run(() => SendRcNow(id, build), false);

    internal Task<bool> SendNcNow(ClientPacketId id, Action<PacketWriter>? build = null, bool requireAuthenticated = true)
    {
        FramedConnection? connection = _nc;
        if (_closed || connection is null || (requireAuthenticated && !_ncAuthenticated)) return FalseTask;
        return TryBuild(build, out byte[] payload) ? Complete(connection.SendAsync(new[] { Frames.Packet((int)id, payload) })) : FalseTask;
    }

    internal Task<bool> SendNc(ClientPacketId id, Action<PacketWriter>? build = null) => Run(() => SendNcNow(id, build), false);

    private async Task<bool> Complete(Task<SendOutcome> send)
    {
        SendOutcome outcome = await send.ConfigureAwait(false);
        if (!outcome.Success && outcome.Error is not null && !_closed && !string.Equals(outcome.Error, "The connection is closed.", StringComparison.Ordinal))
            Refuse(outcome.Error);
        return outcome.Success;
    }
}
