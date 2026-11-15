using System.Text;
using Avalonia.Threading;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Wire;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Session;

/// <summary>
/// What the C++ main window did for its connection besides showing it (<c>open</c>, <c>processEvents</c>, <c>onConnected</c>,
/// <c>handleDisconnected</c>, <c>applyOptions</c>, <c>TRemoteFrame.cpp:818-935, 1614-1768, 2040-2093, 2179-2190, 2756-2808</c>): the hello
/// and the options sent after the login, the 60-second keep-alive, connecting the NC (and again after it drops, with
/// <c>autoreconnectnc</c>), the away nickname, the mass-PM acceptance, the disconnect dialog and option changes applied at once.
/// It lives as long as the main window and follows the session's connection through <see cref="SessionContext.Attach"/>: a handler
/// that is called for a connection that was replaced does nothing, and nothing runs after <see cref="Dispose"/>.
/// </summary>
internal sealed class SessionController : IDisposable
{
    /// <summary>PLI_RC_UNKNOWN162: the empty packet the C++ sent every minute as a keep-alive.</summary>
    public const int KeepAlivePacketId = 162;

    /// <summary>The time between two keep-alives.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(60);

    /// <summary>How long after the NC dropped it is connected again.</summary>
    public static readonly TimeSpan NcReconnectDelay = TimeSpan.FromSeconds(1);

    /// <summary>The time between two attempts while the NC cannot be reached.</summary>
    public static readonly TimeSpan NcRetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long <c>/nc connect</c> waits for the NPC server's location before connecting.</summary>
    public static readonly TimeSpan NcQueryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The words that keep the away suffix off a nickname.</summary>
    private static readonly string[] s_awayWords = ["away", "dnd", "do not disturb", "busy"];

    private readonly SessionContext _context;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _closed = new();
    private readonly HashSet<string> _joinedChannels = new(StringComparer.Ordinal);
    private IDisposable? _attachment;
    private RepeatingTimer? _timer;
    private bool _disposed;

    private IRcSession? _bound;
    private IRcSession? _helloSentFor;
    private IRcSession? _disconnectShownFor;
    private string _serverName;
    private string _baseNickname;
    private DateTimeOffset? _nextKeepAlive;
    private bool _ncAttempted;
    private bool _ncManuallyDisconnected;
    private bool _ncWasAuthenticated;
    private bool _ncRetrying;
    private bool _ncConnecting;
    private bool _lastNcActive;
    private DateTimeOffset? _nextNcAttempt;
    private bool _awayApplied;
    private DateTimeOffset _awayActivity;
    private bool? _ownClientConnected;
    private bool _reconnecting;

    /// <summary>Creates the controller; call <see cref="Start"/> to follow the connection.</summary>
    /// <param name="context">The session.</param>
    /// <param name="clock">The time (tests control it); <see cref="DateTimeOffset.UtcNow"/> by default.</param>
    public SessionController(SessionContext context, Func<DateTimeOffset>? clock = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _serverName = context.ServerName;
        _baseNickname = context.Nickname;
        ConnectedAt = _clock();
    }

    /// <summary>
    /// Asks whether to connect again after the connection dropped, with the reason: true reconnects, false shows the server list (as
    /// closing the C++ error window did), null does nothing (the window was closed meanwhile). Without it the server list is shown.
    /// </summary>
    public Func<string, Task<bool?>>? AskReconnect { get; set; }

    /// <summary>Raised with a line for the RC chat (an error of a command).</summary>
    public event EventHandler<string>? ChatLine;

    /// <summary>Raised when the NC state, the away state or the connection changed (the window updates its buttons and labels).</summary>
    public event EventHandler? StateChanged;

    /// <summary>Whether the NC is connected and logged in and was not disconnected with <c>/nc disconnect</c>.</summary>
    public bool IsNcActive => !_ncManuallyDisconnected && _context.Session.IsNcAuthenticated;

    /// <summary>Whether an NC connect is under way.</summary>
    public bool IsNcConnecting => _ncConnecting;

    /// <summary>Whether the NC was disconnected with <c>/nc disconnect</c>.</summary>
    public bool IsNcManuallyDisconnected => _ncManuallyDisconnected;

    /// <summary>Whether the NC was tried at least once on this connection.</summary>
    public bool NcAttempted => _ncAttempted;

    /// <summary>Whether the away nickname is applied.</summary>
    public bool IsAway => _awayApplied;

    /// <summary>When the current connection was taken over (for the uptime of the debug information).</summary>
    public DateTimeOffset ConnectedAt { get; private set; }

    /// <summary>The time now, as the controller sees it.</summary>
    public DateTimeOffset Now => _clock();

    /// <summary>The IRC channels joined on this server (joined again after a reconnect).</summary>
    public IReadOnlyCollection<string> JoinedIrcChannels => _joinedChannels;

    /// <summary>When the next keep-alive is due, or null while not logged in (tests).</summary>
    internal DateTimeOffset? NextKeepAlive => _nextKeepAlive;

    /// <summary>When the next NC connect is due, or null (tests).</summary>
    internal DateTimeOffset? NextNcAttempt => _nextNcAttempt;

    /// <summary>Follows the connection; with <paramref name="timer"/> a timer calls <see cref="Tick"/> four times a second.</summary>
    /// <param name="timer">Whether to start the timer (tests call <see cref="Tick"/> themselves).</param>
    public void Start(bool timer = true)
    {
        if (_disposed || _attachment is not null) return;
        _context.App.Options.Changed += OnOptionsChanged;
        _attachment = _context.Attach(Bind);
        if (timer) _timer = UiDispatch.Repeat(TimeSpan.FromMilliseconds(250), Tick, "SessionController.Tick");
    }

    /// <summary>Notes that an IRC channel was joined.</summary>
    /// <param name="channel">The channel.</param>
    public void NoteChannelJoined(string channel)
    {
        if (!string.IsNullOrEmpty(channel)) _joinedChannels.Add(channel);
    }

    /// <summary>Leaves an IRC channel (the tab's Close, <c>removeChannel</c>).</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="part">Whether to tell the server (not when the server said the channel was left).</param>
    public void LeaveChannel(string channel, bool part)
    {
        if (!_joinedChannels.Remove(channel) || !part) return;
        IRcSession session = _context.Session;
        if (session.IsConnected) Send(session, () => session.IrcPartAsync(channel), "IRC part");
    }

    /// <summary>The periodic work of <c>processEvents</c>: away, keep-alive, NC connect.</summary>
    public void Tick()
    {
        if (_disposed) return;
        IRcSession session = _context.Session;
        DateTimeOffset now = _clock();
        if (!session.IsConnected)
        {
            _nextKeepAlive = null;
            NotifyIfNcChanged();
            return;
        }
        TickAway(session, now);
        TickKeepAlive(session, now);
        TickNc(session, now);
        NotifyIfNcChanged();
    }

    /// <summary><c>/nc connect</c>, <c>/nc c</c>, <c>/nc rc</c> (<c>reconnectNPCServer</c>).</summary>
    public void ReconnectNc()
    {
        IRcSession session = _context.Session;
        if (!session.IsConnected) return;
        DateTimeOffset now = _clock();
        _ncManuallyDisconnected = false;
        _ncWasAuthenticated = false;
        _ncRetrying = false;
        if (session.IsNcConnected) session.DisconnectNc();
        _ncAttempted = true;
        _nextNcAttempt = now;
        RcPlayer? npcServer = session.Players.FirstOrDefault(player => player.Account.Contains("(server)", StringComparison.OrdinalIgnoreCase));
        if (npcServer is not null)
        {
            byte[] query = new PacketWriter().WriteGShort(Math.Min(npcServer.Id, 0x6fff)).WriteText("location").ToArray();
            Send(session, () => session.SendRawPacketAsync((int)ClientPacketId.NpcServerQuery, query), "NPC server query");
            _nextNcAttempt = now + NcQueryDelay;
        }
        RaiseStateChanged();
    }

    /// <summary><c>/nc disconnect</c>, <c>/nc dc</c> (<c>disconnectNPCServer</c>): no automatic reconnect until <c>/nc connect</c>.</summary>
    public void DisconnectNc()
    {
        IRcSession session = _context.Session;
        if (!session.IsConnected) return;
        _ncManuallyDisconnected = true;
        _nextNcAttempt = null;
        _ncWasAuthenticated = false;
        _ncRetrying = false;
        if (!session.DisconnectNc()) RaiseChatLine(string.IsNullOrEmpty(session.LastError) ? "The NPC server is not connected." : session.LastError!);
        _ncAttempted = true;
        RaiseStateChanged();
    }

    /// <summary><c>/reconnect</c>, <c>/rc</c>: connects again through the program (also for direct connections, defect D19).</summary>
    /// <returns>Completes when the reconnect ended.</returns>
    public async Task ReconnectServerAsync()
    {
        if (_disposed || _context.IsEnded) return;
        _reconnecting = true;
        ConnectResult result;
        try
        {
            result = await _context.Shell.ReconnectAsync(_context).ConfigureAwait(true);
        }
        finally
        {
            _reconnecting = false;
        }
        if (!result.Success && !_disposed) RaiseChatLine(string.IsNullOrEmpty(result.Error) ? "The connection could not be made." : result.Error!);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.App.Options.Changed -= OnOptionsChanged;
        _timer?.Dispose();
        _attachment?.Dispose();
        try
        {
            _closed.Cancel();
        }
        catch (AggregateException exception)
        {
            UiSafe.Log.Warn("Cancelling the session controller failed.", exception);
        }
        _closed.Dispose();
    }

    // ---- The connection --------------------------------------------------------------------------------------------------

    private IDisposable Bind(IRcSession session)
    {
        _bound = session;
        if (!string.Equals(_serverName, _context.ServerName, StringComparison.Ordinal)) _joinedChannels.Clear();
        _serverName = _context.ServerName;
        if (_awayApplied && _context.Nickname == _baseNickname + " - Away") _context.SetNickname(_baseNickname);
        else _baseNickname = _context.Nickname;
        _awayApplied = false;
        _nextKeepAlive = null;
        _ncAttempted = false;
        _ncManuallyDisconnected = false;
        _ncWasAuthenticated = false;
        _ncRetrying = false;
        _ncConnecting = false;
        _nextNcAttempt = null;
        _ownClientConnected = null;
        ConnectedAt = _clock();

        EventHandler connected = (_, _) => Guard(session, () => OnConnected(session));
        EventHandler<DisconnectedEventArgs> disconnected = (_, e) => Guard(session, () => OnDisconnected(session, e));
        EventHandler ncConnected = (_, _) => Guard(session, RaiseStateChanged);
        EventHandler<DisconnectedEventArgs> ncDisconnected = (_, _) => Guard(session, () => OnNcDisconnected(session));
        EventHandler<PlayerPropertiesChangedEventArgs> properties = (_, e) => Guard(session, () => OnPlayerProperties(session, e));
        session.Connected += connected;
        session.Disconnected += disconnected;
        session.NcConnected += ncConnected;
        session.NcDisconnected += ncDisconnected;
        session.PlayerPropertiesChanged += properties;
        if (session.IsAuthenticated) OnConnected(session);
        RaiseStateChanged();
        return new Unsubscriber(() =>
        {
            session.Connected -= connected;
            session.Disconnected -= disconnected;
            session.NcConnected -= ncConnected;
            session.NcDisconnected -= ncDisconnected;
            session.PlayerPropertiesChanged -= properties;
        });
    }

    /// <summary>Runs a handler only for the connection in use now (rule 5).</summary>
    private void Guard(IRcSession session, Action action)
    {
        if (_disposed || !ReferenceEquals(session, _context.Session) || !ReferenceEquals(session, _bound)) return;
        UiSafe.Run(action, "SessionController event");
    }

    /// <summary><c>onConnected</c>: nickname, IRC login and channels, mass-PM acceptance, list-server options, the hello line.</summary>
    private void OnConnected(IRcSession session)
    {
        if (ReferenceEquals(_helloSentFor, session)) return;
        _helloSentFor = session;
        string nickname = _context.Nickname;
        string[] channels = _joinedChannels.ToArray();
        UiSafe.Fire(async () =>
        {
            if (nickname.Length > 0) await session.SetNicknameAsync(nickname).ConfigureAwait(true);
            await session.IrcLoginAsync().ConfigureAwait(true);
            foreach (string channel in channels) await session.IrcJoinAsync(channel).ConfigureAwait(true);
            await SendMassPmAcceptance(session).ConfigureAwait(true);
            await SendServerListOptions(session).ConfigureAwait(true);
            await session.SendRcChatAsync("/npc newrc," + BuildInfo.Date).ConfigureAwait(true);
        }, "SessionController.OnConnected");
        RaiseStateChanged();
    }

    /// <summary><c>handleDisconnected</c>: the reason once in a dialog for this connection, then reconnect or the server list.</summary>
    private void OnDisconnected(IRcSession session, DisconnectedEventArgs e)
    {
        _nextKeepAlive = null;
        RaiseStateChanged();
        if (_reconnecting || e.Requested || _context.IsEnded || ReferenceEquals(_disconnectShownFor, session)) return;
        _disconnectShownFor = session;
        string reason = string.IsNullOrWhiteSpace(e.Reason) ? "You have been disconnected!" : e.Reason;
        UiSafe.Fire(() => HandleDisconnectAsync(session, reason), "SessionController.Disconnected");
    }

    private async Task HandleDisconnectAsync(IRcSession session, string reason)
    {
        while (true)
        {
            Func<string, Task<bool?>>? ask = AskReconnect;
            bool? again = ask is null ? false : await ask(reason).ConfigureAwait(true);
            if (again is null || _disposed || _context.IsEnded || !ReferenceEquals(_context.Session, session)) return;
            if (again == false)
            {
                _context.Shell.ShowServerList(_context);
                return;
            }
            _reconnecting = true;
            ConnectResult result;
            try
            {
                result = await _context.Shell.ReconnectAsync(_context).ConfigureAwait(true);
            }
            finally
            {
                _reconnecting = false;
            }
            if (result.Success || _disposed || _context.IsEnded || !ReferenceEquals(_context.Session, session)) return;
            reason = string.IsNullOrWhiteSpace(result.Error) ? "The connection could not be made." : result.Error!;
        }
    }

    private void OnNcDisconnected(IRcSession session)
    {
        if (_ncWasAuthenticated && !_ncManuallyDisconnected && _context.App.Options.Current.AutoReconnectNc)
        {
            _nextNcAttempt = _clock() + NcReconnectDelay;
            _ncRetrying = true;
        }
        _ncWasAuthenticated = false;
        RaiseStateChanged();
    }

    /// <summary>Notes whether your own account has a game client on (property <c>q</c>, bit 1), for <c>nomassifclienton</c>.</summary>
    private void OnPlayerProperties(IRcSession session, PlayerPropertiesChangedEventArgs e)
    {
        RcPlayer? player = session.Players.FirstOrDefault(candidate => candidate.Id == e.PlayerId);
        if (player is null || !string.Equals(player.Account, _context.Account, StringComparison.OrdinalIgnoreCase)) return;
        bool? connected = ClientConnected(e.Properties);
        if (connected is null || connected == _ownClientConnected) return;
        _ownClientConnected = connected;
        Send(session, () => SendMassPmAcceptance(session), "mass PM acceptance");
    }

    /// <summary>Reads the client flag from a raw property stream as <c>TServerPlayer::setProperties</c> did.</summary>
    internal static bool? ClientConnected(byte[] properties)
    {
        bool? result = null;
        for (int offset = 0; offset + 1 < properties.Length;)
        {
            byte property = properties[offset];
            byte next = properties[offset + 1];
            if (property == (byte)'q')
            {
                result = ((next - 0x20) & 0x01) != 0;
                offset += 2;
            }
            else if (property is (byte)'#' or (byte)'l') offset += 4;
            else if (property is (byte)' ' or (byte)'R' or (byte)'T' or (byte)'r' || property >= (byte)'V' && property <= (byte)'k') offset += 2 + (next >= 0x20 ? next - 0x20 : 0);
            else offset += 2;
        }
        return result;
    }

    // ---- Periodic work ------------------------------------------------------------------------------------------------------

    private void TickAway(IRcSession session, DateTimeOffset now)
    {
        RcOptions options = _context.App.Options.Current;
        DateTimeOffset lastActivity = _context.Shell.LastActivity;
        if (_awayApplied)
        {
            if (lastActivity > _awayActivity) RestoreFromAway(session);
            return;
        }
        if (!options.AfkEnabled || now - lastActivity < TimeSpan.FromMinutes(Math.Max(1, options.AfkTimeout))) return;
        string lower = _baseNickname.ToLowerInvariant();
        if (_baseNickname.Length == 0 || s_awayWords.Any(word => lower.Contains(word, StringComparison.Ordinal))) return;
        _awayApplied = true;
        _awayActivity = lastActivity;
        string away = _baseNickname + " - Away";
        _context.SetNickname(away);
        Send(session, () => session.SetNicknameAsync(away), "away nickname");
        Send(session, () => session.SendRawPacketAsync((int)ClientPacketId.PlayerProps, new byte[] { (byte)'U', 1 + 32 }), "away status");
        RaiseStateChanged();
    }

    private void RestoreFromAway(IRcSession session)
    {
        if (!_awayApplied) return;
        _awayApplied = false;
        _context.SetNickname(_baseNickname);
        if (session.IsConnected)
        {
            string nickname = _baseNickname;
            Send(session, () => session.SetNicknameAsync(nickname), "nickname");
            Send(session, () => session.SendRawPacketAsync((int)ClientPacketId.PlayerProps, new byte[] { (byte)'U', 32 }), "online status");
        }
        RaiseStateChanged();
    }

    private void TickKeepAlive(IRcSession session, DateTimeOffset now)
    {
        if (!session.IsAuthenticated)
        {
            _nextKeepAlive = null;
            return;
        }
        if (_nextKeepAlive is null)
        {
            _nextKeepAlive = now + KeepAliveInterval;
            return;
        }
        if (now < _nextKeepAlive) return;
        _nextKeepAlive = now + KeepAliveInterval;
        Send(session, () => session.SendRawPacketAsync(KeepAlivePacketId, ReadOnlyMemory<byte>.Empty), "keep-alive");
    }

    private void TickNc(IRcSession session, DateTimeOffset now)
    {
        bool autoReconnect = _context.App.Options.Current.AutoReconnectNc;
        if (session.IsNcAuthenticated)
        {
            _ncWasAuthenticated = true;
            _nextNcAttempt = null;
            _ncRetrying = false;
        }
        else if (_ncWasAuthenticated)
        {
            _ncWasAuthenticated = false;
            if (!_ncManuallyDisconnected && autoReconnect)
            {
                _nextNcAttempt = now + NcReconnectDelay;
                _ncRetrying = true;
            }
        }
        if (!autoReconnect && _ncRetrying)
        {
            _nextNcAttempt = null;
            _ncRetrying = false;
        }
        bool due = _nextNcAttempt is { } next && now >= next;
        bool first = _nextNcAttempt is null && !_ncAttempted;
        if (_ncManuallyDisconnected || _ncConnecting || !(first || due) || !session.HasNcServer || session.IsNcConnected) return;
        _ncAttempted = true;
        _nextNcAttempt = null;
        ConnectNc(session);
    }

    private void ConnectNc(IRcSession session)
    {
        _ncConnecting = true;
        bool retrying = _ncRetrying;
        CancellationToken token = _closed.Token;
        RaiseStateChanged();
        UiSafe.Fire(async () =>
        {
            bool connected;
            try
            {
                connected = await session.ConnectNcAsync(token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(session, _bound)) _ncConnecting = false;
            }
            if (_disposed || !ReferenceEquals(session, _context.Session)) return;
            if (!connected && retrying && !_ncManuallyDisconnected && _context.App.Options.Current.AutoReconnectNc)
            {
                _ncRetrying = true;
                _nextNcAttempt = _clock() + NcRetryInterval;
            }
            RaiseStateChanged();
        }, "SessionController.ConnectNc");
    }

    private void NotifyIfNcChanged()
    {
        bool active = IsNcActive;
        if (active == _lastNcActive) return;
        _lastNcActive = active;
        RaiseStateChanged();
    }

    // ---- What is sent --------------------------------------------------------------------------------------------------------

    /// <summary><c>updateMassPMAcceptance</c>: <c>'A'</c> then <c>'!'</c> (reject) or <c>' '</c> (accept).</summary>
    private Task<bool> SendMassPmAcceptance(IRcSession session)
    {
        RcOptions options = _context.App.Options.Current;
        bool reject = options.NoMassMessages || options.NoMassIfClientOn && _ownClientConnected == false;
        return session.SendRawPacketAsync((int)ClientPacketId.PlayerProps, new[] { (byte)'A', reject ? (byte)'!' : (byte)' ' });
    }

    /// <summary><c>sendServerListOptions</c>: the list server's PM and buddy options.</summary>
    private Task<bool> SendServerListOptions(IRcSession session)
    {
        RcOptions options = _context.App.Options.Current;
        string text = "GraalEngine,lister,options,globalpms=" + Flag(options.GlobalPms) + ",buddytracking=" + Flag(options.BuddyTracking) + ",showbuddies=" + Flag(options.ShowBuddies);
        return session.SendRawPacketAsync((int)ClientPacketId.SendText, new PacketWriter().WriteText(text).ToArray());
    }

    private static string Flag(bool value) => value ? "true" : "false";

    private void Send(IRcSession session, Func<Task<bool>> send, string what) =>
        UiSafe.Fire(async () =>
        {
            if (!ReferenceEquals(session, _context.Session)) return;
            await send().ConfigureAwait(true);
        }, "SessionController " + what);

    // ---- Options ----------------------------------------------------------------------------------------------------------

    private void OnOptionsChanged(object? sender, OptionsChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) UiSafe.Run(() => ApplyOptions(e), "SessionController options");
        else UiDispatch.Post(() => ApplyOptions(e), "SessionController options");
    }

    /// <summary><c>applyOptions</c>: what the connection needs to hear about at once.</summary>
    private void ApplyOptions(OptionsChangedEventArgs e)
    {
        if (_disposed || _context.IsEnded) return;
        IRcSession session = _context.Session;
        RcOptions options = e.Current;
        if (e.Contains(OptionKeys.Nickname) && options.Nickname.Length > 0 && options.Nickname != _baseNickname)
        {
            bool wasAway = _awayApplied;
            if (wasAway) RestoreFromAway(session);
            _baseNickname = options.Nickname;
            _context.SetNickname(_baseNickname);
            if (session.IsConnected)
            {
                string nickname = _baseNickname;
                Send(session, () => session.SetNicknameAsync(nickname), "nickname");
            }
        }
        if (e.Contains(OptionKeys.AfkEnabled) && !options.AfkEnabled && _awayApplied) RestoreFromAway(session);
        if (!session.IsConnected) return;
        if (e.Contains(OptionKeys.NoMassMessages, OptionKeys.NoMassIfClientOn)) Send(session, () => SendMassPmAcceptance(session), "mass PM acceptance");
        if (e.Contains(OptionKeys.GlobalPms, OptionKeys.BuddyTracking, OptionKeys.ShowBuddies)) Send(session, () => SendServerListOptions(session), "list-server options");
        if (e.Contains(OptionKeys.UseNewBanType)) session.UseNewBanProtocol = options.UseNewBanType;
        if (e.Contains(OptionKeys.AutoReconnectNc) && !options.AutoReconnectNc && _ncRetrying)
        {
            _ncRetrying = false;
            _nextNcAttempt = null;
        }
        RaiseStateChanged();
    }

    private void RaiseChatLine(string line)
    {
        if (_disposed) return;
        EventHandler<string>? handler = ChatLine;
        if (handler is not null) UiSafe.Run(() => handler(this, line), "SessionController.ChatLine");
    }

    private void RaiseStateChanged()
    {
        if (_disposed) return;
        _lastNcActive = IsNcActive;
        EventHandler? handler = StateChanged;
        if (handler is not null) UiSafe.Run(() => handler(this, EventArgs.Empty), "SessionController.StateChanged");
    }
}

/// <summary>Text helpers shared by the session windows.</summary>
internal static class SessionText
{
    /// <summary>Whether a text is one of the engine's own protocol texts that the chat never shows (<c>isInternalProtocolText</c>).</summary>
    /// <param name="value">The text.</param>
    /// <returns>Whether to hide it.</returns>
    public static bool IsInternalProtocolText(string value)
    {
        if (value.StartsWith("GraalEngine", StringComparison.Ordinal) || value.StartsWith("raalEngine", StringComparison.Ordinal)) return true;
        int separator = value.IndexOfAny(['\x01', '\n']);
        if (separator < 0) return false;
        string name = value[..separator];
        return name is "GraalEngine" or "raalEngine";
    }

    /// <summary>The bytes of a text as the servers read it.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Bytes(string text) => new PacketWriter().WriteText(text).ToArray();

    /// <summary>A short text for a length of time, as the debug information shows it (<c>1h 2m 3s</c>).</summary>
    /// <param name="elapsed">The time.</param>
    /// <returns>The text.</returns>
    public static string Uptime(TimeSpan elapsed)
    {
        long total = Math.Max(0, (long)elapsed.TotalSeconds);
        long hours = total / 3600, minutes = total % 3600 / 60, seconds = total % 60;
        var builder = new StringBuilder();
        if (hours > 0) builder.Append(hours).Append("h ").Append(minutes).Append("m ");
        else if (minutes > 0) builder.Append(minutes).Append("m ");
        return builder.Append(seconds).Append('s').ToString();
    }
}
