using RemoteControl.Protocol.Session;

namespace RemoteControl.Extensions;

/// <summary>One thing a session saw, as an MCP client reads it with <c>rc_read_events</c>.</summary>
/// <param name="Sequence">Its number: every journal counts from 1, one up per entry.</param>
/// <param name="Time">When it arrived.</param>
/// <param name="Kind">What it is: one of <see cref="McpSessionJournal.Kinds"/>.</param>
/// <param name="Text">The line as the server sent it.</param>
/// <param name="Source">Who or what it came from (the sender of a private message, the IRC channel, the player), or empty.</param>
public sealed record McpJournalEntry(long Sequence, DateTimeOffset Time, string Kind, string Text, string Source);

/// <summary>
/// The recent events of one server session for MCP clients: RC and NC chat, private, admin and to-all messages, IRC lines, server
/// texts, file-browser messages, refused sends, connection changes and players joining and leaving. It keeps the last
/// <see cref="Capacity"/> entries. A client reads what came after the number it last saw, and may wait for something new, so it can
/// see what the server answered a command or a saved script.
/// </summary>
/// <remarks>
/// Used on the session's context only (the UI thread in the program), as the session's events are raised there; it needs no lock.
/// <see cref="Attach"/> subscribes to a connection; the program attaches the journal of a session again after a reconnect, so one
/// journal covers the session's whole life.
/// </remarks>
public sealed class McpSessionJournal
{
    /// <summary>How many entries are kept.</summary>
    public const int Capacity = 2000;

    /// <summary>RC chat lines.</summary>
    public const string Rc = "rc";

    /// <summary>NC chat lines (script compiler output, NPC-server messages).</summary>
    public const string Nc = "nc";

    /// <summary>Private messages received.</summary>
    public const string Pm = "pm";

    /// <summary>Admin messages received.</summary>
    public const string Admin = "admin";

    /// <summary>To-all messages.</summary>
    public const string ToAll = "toall";

    /// <summary>IRC lines.</summary>
    public const string Irc = "irc";

    /// <summary>Server texts nothing else handled.</summary>
    public const string Server = "server";

    /// <summary>File-browser messages (uploads, deletes, refusals).</summary>
    public const string Files = "files";

    /// <summary>Sends the session refused and packets it could not read.</summary>
    public const string Error = "error";

    /// <summary>Connects and disconnects of the RC and the NC.</summary>
    public const string Connection = "connection";

    /// <summary>Players joining and leaving.</summary>
    public const string Players = "players";

    /// <summary>Every kind, in the order they are documented.</summary>
    public static IReadOnlyList<string> Kinds { get; } = new[] { Rc, Nc, Pm, Admin, ToAll, Irc, Server, Files, Error, Connection, Players };

    private readonly Queue<McpJournalEntry> _entries = new();
    private readonly List<TaskCompletionSource<bool>> _waiters = new();
    private readonly Func<DateTimeOffset> _clock;
    private long _sequence;

    /// <summary>Creates an empty journal.</summary>
    /// <param name="clock">The clock of the entries (tests); the current time by default.</param>
    public McpSessionJournal(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The number of the last entry, or 0.</summary>
    public long Sequence => _sequence;

    /// <summary>The number of the oldest entry still kept, or <see cref="Sequence"/> + 1 when there is none.</summary>
    public long Oldest => _entries.Count > 0 ? _entries.Peek().Sequence : _sequence + 1;

    /// <summary>Records the events of a connection until the returned object is disposed.</summary>
    /// <param name="session">The connection.</param>
    /// <returns>Dispose it to stop recording.</returns>
    public IDisposable Attach(IRcSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        EventHandler<TextEventArgs> rc = (_, e) => Add(Rc, e.Text);
        EventHandler<TextEventArgs> nc = (_, e) => Add(Nc, e.Text);
        EventHandler<PrivateMessageEventArgs> pm = (_, e) => Add(Pm, e.Text, (e.Nickname.Length > 0 ? e.Nickname + " " : string.Empty) + "(" + e.Account + ", id " + e.PlayerId + ")" + (e.Kind == PrivateMessageKind.Normal ? string.Empty : " " + e.Kind.ToString().ToLowerInvariant()));
        EventHandler<TextEventArgs> admin = (_, e) => Add(Admin, e.Text.Replace('§', '\n'));
        EventHandler<TextEventArgs> toall = (_, e) => Add(ToAll, e.Text);
        EventHandler<IrcMessageEventArgs> irc = (_, e) => Add(Irc, e.Line, e.Channel);
        EventHandler<TextEventArgs> server = (_, e) => Add(Server, e.Text);
        EventHandler<TextEventArgs> files = (_, e) => Add(Files, e.Text);
        EventHandler<TextEventArgs> error = (_, e) => Add(Error, e.Text);
        EventHandler connected = (_, _) => Add(Connection, "RC connected");
        EventHandler<DisconnectedEventArgs> disconnected = (_, e) => Add(Connection, "RC disconnected: " + e.Reason);
        EventHandler ncConnected = (_, _) => Add(Connection, "NC connected");
        EventHandler<DisconnectedEventArgs> ncDisconnected = (_, e) => Add(Connection, "NC disconnected: " + e.Reason);
        EventHandler<PlayerEventArgs> joined = (_, e) => Add(Players, "joined " + Describe(e.Player), e.Player.Account);
        EventHandler<PlayerEventArgs> left = (_, e) => Add(Players, "left " + Describe(e.Player), e.Player.Account);
        session.RcChatReceived += rc;
        session.NcChatReceived += nc;
        session.PrivateMessageReceived += pm;
        session.AdminMessageReceived += admin;
        session.ToAllReceived += toall;
        session.IrcMessageReceived += irc;
        session.ServerTextReceived += server;
        session.FileBrowserMessageReceived += files;
        session.ProtocolError += error;
        session.Connected += connected;
        session.Disconnected += disconnected;
        session.NcConnected += ncConnected;
        session.NcDisconnected += ncDisconnected;
        session.PlayerJoined += joined;
        session.PlayerLeft += left;
        return new Detach(() =>
        {
            session.RcChatReceived -= rc;
            session.NcChatReceived -= nc;
            session.PrivateMessageReceived -= pm;
            session.AdminMessageReceived -= admin;
            session.ToAllReceived -= toall;
            session.IrcMessageReceived -= irc;
            session.ServerTextReceived -= server;
            session.FileBrowserMessageReceived -= files;
            session.ProtocolError -= error;
            session.Connected -= connected;
            session.Disconnected -= disconnected;
            session.NcConnected -= ncConnected;
            session.NcDisconnected -= ncDisconnected;
            session.PlayerJoined -= joined;
            session.PlayerLeft -= left;
        });
    }

    /// <summary>Adds an entry (the program and tests may add their own).</summary>
    /// <param name="kind">One of <see cref="Kinds"/>.</param>
    /// <param name="text">The text.</param>
    /// <param name="source">Who or what it came from, or empty.</param>
    public void Add(string kind, string? text, string? source = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        _entries.Enqueue(new McpJournalEntry(++_sequence, _clock(), kind, text ?? string.Empty, source ?? string.Empty));
        while (_entries.Count > Capacity) _entries.Dequeue();
        if (_waiters.Count == 0) return;
        TaskCompletionSource<bool>[] waiters = _waiters.ToArray();
        _waiters.Clear();
        foreach (TaskCompletionSource<bool> waiter in waiters) waiter.TrySetResult(true);
    }

    /// <summary>
    /// Forgets every entry (the session went to another server): the numbering goes on, so a client that reads after the number it last saw
    /// gets only what came since and learns from <c>missed</c> that older entries are gone.
    /// </summary>
    public void Clear() => _entries.Clear();

    /// <summary>The entries after <paramref name="since"/>, oldest first.</summary>
    /// <param name="since">The number last seen (0 for everything kept).</param>
    /// <param name="kinds">The kinds wanted, or empty for every kind.</param>
    /// <param name="limit">The most entries returned (the oldest first).</param>
    /// <returns>The entries.</returns>
    public IReadOnlyList<McpJournalEntry> Read(long since, IReadOnlyCollection<string>? kinds = null, int limit = Capacity)
    {
        var result = new List<McpJournalEntry>();
        if (limit <= 0) return result;
        foreach (McpJournalEntry entry in _entries)
        {
            if (entry.Sequence <= since) continue;
            if (kinds is { Count: > 0 } && !kinds.Contains(entry.Kind)) continue;
            result.Add(entry);
            if (result.Count >= limit) break;
        }
        return result;
    }

    /// <summary>Waits until an entry after <paramref name="since"/> arrives, the time is up or the token is cancelled (which ends the wait without an exception).</summary>
    /// <param name="since">The number last seen.</param>
    /// <param name="timeout">The longest wait.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>Whether there is an entry after <paramref name="since"/>.</returns>
    public async Task<bool> WaitAsync(long since, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_sequence > since) return true;
        if (timeout <= TimeSpan.Zero || cancellationToken.IsCancellationRequested) return false;
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add(waiter);
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task timer = Task.Delay(timeout, delay.Token);
        await Task.WhenAny(waiter.Task, timer).ConfigureAwait(true);
        delay.Cancel();
        _waiters.Remove(waiter);
        return _sequence > since;
    }

    private static string Describe(RcPlayer player) =>
        player.Account + (player.Nickname.Length > 0 && player.Nickname != player.Account ? " (" + player.Nickname + ")" : string.Empty) + " id " + player.Id + (player.IsExternal ? " on another server" : string.Empty);

    private sealed class Detach(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
