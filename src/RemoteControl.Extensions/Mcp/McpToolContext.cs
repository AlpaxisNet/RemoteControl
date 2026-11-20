using System.Text;
using System.Text.Json;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Extensions;

/// <summary>What the server tools share across requests: backups, the local folders, the saved approvals, the log.</summary>
internal sealed class McpToolServices
{
    public McpToolServices(McpBackupStore backups, string workspace, Action<string> saveScriptTypeApproval, int processId, AppLog log, string? referenceDirectory = null, McpScriptApi? scriptApi = null)
    {
        Backups = backups;
        Workspace = workspace;
        SaveScriptTypeApproval = saveScriptTypeApproval;
        ProcessId = processId;
        Log = log;
        ReferenceDirectory = referenceDirectory ?? Path.Combine(AppContext.BaseDirectory, "gs2-reference");
        ScriptApi = scriptApi ?? new McpScriptApi(null);
    }

    /// <summary>The GS2 reference beside the program.</summary>
    public string ReferenceDirectory { get; }

    /// <summary>The script API of <c>autocompletesource</c>, loaded once and shared by every request.</summary>
    public McpScriptApi ScriptApi { get; }

    /// <summary>The GS2 reference with the folders the options add.</summary>
    public McpReference Reference(RcOptions options) => new(ReferenceDirectory, options.McpReferenceFolders, Workspace);

    public McpBackupStore Backups { get; }

    public string Workspace { get; }

    public Action<string> SaveScriptTypeApproval { get; }

    public int ProcessId { get; }

    public AppLog Log { get; }

    /// <summary>The local folders the options allow now.</summary>
    public McpLocalFiles LocalFiles(RcOptions options) => new(Workspace, options.McpFileRoots);
}

/// <summary>
/// One call of a server tool. It is made, and the tool runs, on the host's context (the UI thread in the program), where the session's
/// events are raised and its caches change (AGENTS.md rule 1); every await inside a tool returns to that context.
/// </summary>
internal sealed class McpToolContext
{
    /// <summary>The time a tool waits for the server's answer unless <c>timeoutSeconds</c> says otherwise.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly McpSessionTarget? _target;

    public McpToolContext(IMcpHost host, McpArguments arguments, RcOptions options, McpSessionTarget? target, McpToolServices services, CancellationToken token, TimeSpan? defaultTimeout = null)
    {
        Host = host;
        Args = arguments;
        Options = options;
        _target = target;
        Services = services;
        Token = token;
        Timeout = arguments.TryInt("timeoutSeconds", out int seconds) ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 600)) : defaultTimeout ?? DefaultTimeout;
    }

    public IMcpHost Host { get; }

    public McpArguments Args { get; }

    public RcOptions Options { get; }

    public McpToolServices Services { get; }

    public CancellationToken Token { get; }

    /// <summary>How long a request waits for the server's answer.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// The session of this call. The server scope (<c>mcpserverscope</c>) is checked here, when a tool uses its session, so that a tool
    /// that may work without one (checking a given text) is not refused for the session that happens to be selected.
    /// </summary>
    /// <exception cref="McpToolException">There is no session.</exception>
    /// <exception cref="McpScopeException">The session is outside the server scope.</exception>
    public McpSessionTarget Target
    {
        get
        {
            McpSessionTarget target = _target ?? throw new McpToolException("No connected RC server session: connect one (rc_connect_server) or name it in server.");
            if (Options.McpServerScope.Length > 0 && !string.Equals(target.ServerName, Options.McpServerScope, StringComparison.OrdinalIgnoreCase)) throw new McpScopeException();
            return target;
        }
    }

    public IRcSession Session => Target.Session;

    public string Server => Target.ServerName;

    /// <summary>Fails unless the RC of <paramref name="target"/> (this call's session by default) is logged in.</summary>
    public void RequireRc(McpSessionTarget? target = null)
    {
        McpSessionTarget session = target ?? Target;
        if (!session.Session.IsAuthenticated) throw new McpToolException("The RC is not connected to " + session.ServerName + ".");
    }

    /// <summary>Fails unless the NC of <paramref name="target"/> (this call's session by default) is logged in.</summary>
    public void RequireNc(McpSessionTarget? target = null)
    {
        McpSessionTarget session = target ?? Target;
        RequireRc(session);
        if (!session.Session.IsNcAuthenticated)
            throw new McpToolException("The NC (NPC-Control) of " + session.ServerName + " is not connected" + (session.Session.HasNcServer ? "; connect it with rc_nc_connect." : ": the server named no NPC server."));
    }

    /// <summary>Another connected session (a copy's source or destination); the server scope applies to it too.</summary>
    public async Task<McpSessionTarget> FindSessionAsync(string server)
    {
        McpSessionTarget? found = await Host.FindSessionAsync(server ?? string.Empty).ConfigureAwait(true);
        if (found is null) throw new McpToolException(string.IsNullOrEmpty(server) ? "No connected RC server session." : "Connected RC server session not found: " + server);
        if (Options.McpServerScope.Length > 0 && !string.Equals(found.ServerName, Options.McpServerScope, StringComparison.OrdinalIgnoreCase))
            throw new McpToolException("The server " + found.ServerName + " is outside the configured MCP server scope.");
        return found;
    }

    /// <summary>
    /// Asks "Allow this MCP action?" while <c>mcpapprove</c> is on (or always, with <paramref name="always"/>). An answer that comes after
    /// the bridge stopped does nothing: the request was given up.
    /// </summary>
    /// <exception cref="McpDeniedException">The user denied it.</exception>
    public async Task ApproveAsync(string description, bool always = false)
    {
        if (!Options.McpApprove && !always) return;
        bool allowed = await Host.ApproveAsync(description + ForceNote).ConfigureAwait(true);
        Token.ThrowIfCancellationRequested();
        if (!allowed) throw new McpDeniedException();
    }

    /// <summary>What an approval question adds when the call gives <c>force: true</c>: the backup it otherwise promises may not be made.</summary>
    private string ForceNote => Args.Bool("force", false) ? "\n\nforce: true: this goes ahead even when the old version cannot be read or backed up." : string.Empty;

    /// <summary>
    /// Reads the version a change replaces, for its backup. When it cannot be read nothing is changed, unless the call gives
    /// <c>force: true</c>: then the change goes ahead without a backup (<c>null</c>).
    /// </summary>
    /// <exception cref="McpToolException">It could not be read and force was not given.</exception>
    public async Task<T?> ReadForBackupAsync<T>(Func<Task<T>> read, string what) where T : class
    {
        try
        {
            return await read().ConfigureAwait(true);
        }
        catch (McpToolException exception) when (!Args.Bool("force", false))
        {
            throw new McpToolException("The current " + what + " could not be read for its backup (" + exception.Message + "), so nothing was changed; give force: true to change it without a backup.");
        }
        catch (McpToolException)
        {
            return null;
        }
    }

    /// <summary>The text a change carries out, for an approval question: whole, with its line breaks, and how many lines it has.</summary>
    public static string Whole(string text)
    {
        string normalized = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        int lines = normalized.Length == 0 ? 0 : normalized.TrimEnd('\n').Count(character => character == '\n') + 1;
        return (lines > 1 ? "(" + lines + " lines)\n" : string.Empty) + normalized;
    }

    /// <summary>Claims the answer a request waits for, so that the program's windows do not show it (<see cref="IMcpHost.ClaimAnswer"/>).</summary>
    public IDisposable Claim(McpSessionTarget target, string kind, string key) => Host.ClaimAnswer(target, kind, string.IsNullOrEmpty(key) ? "*" : key);

    /// <summary>
    /// Keeps the version a change replaces (<see cref="McpBackupStore"/>). When it cannot be written the change does not go ahead, unless
    /// the call gives <c>force: true</c>.
    /// </summary>
    /// <exception cref="McpToolException">The backup could not be written and force was not given.</exception>
    public async Task<McpBackupEntry?> BackupAsync(string server, string kind, string name, int npcId, string image, byte[] content, string reason)
    {
        McpBackupEntry? entry = await Services.Backups.SaveAsync(server, kind, name, npcId, image, content, reason).ConfigureAwait(true);
        if (entry is null && !Args.Bool("force", false))
            throw new McpToolException("The current " + kind + " " + name + " could not be backed up (the program's log says why), so nothing was changed; give force: true to change it without a backup.");
        return entry;
    }

    /// <summary>Asks the script question while <c>mcpapprove</c> is on, unless the type was always allowed (never for a delete).</summary>
    /// <param name="action">Write or delete.</param>
    /// <param name="type"><c>Weapon</c>, <c>Class</c> or <c>NPC</c>.</param>
    /// <param name="what">What is written, as the question shows it (the type's prefix is added).</param>
    /// <exception cref="McpDeniedException">The user denied it.</exception>
    public async Task ApproveScriptAsync(McpScriptAction action, string type, string what)
    {
        if (!Options.McpApprove) return;
        bool saved = action != McpScriptAction.Delete && type switch
        {
            "Weapon" => Options.McpApproveWeapon,
            "Class" => Options.McpApproveClass,
            "NPC" => Options.McpApproveNpc,
            _ => false,
        };
        if (saved) return;
        McpApproval answer = await Host.ApproveScriptAsync(action, type + ": " + what + ForceNote, action == McpScriptAction.Delete ? string.Empty : type).ConfigureAwait(true);
        Token.ThrowIfCancellationRequested();
        if (answer == McpApproval.AlwaysAllow && action != McpScriptAction.Delete && type.Length > 0) Services.SaveScriptTypeApproval(type);
        if (answer is not (McpApproval.AllowOnce or McpApproval.AlwaysAllow)) throw new McpDeniedException();
    }

    /// <summary>
    /// Sends a request and waits for the event that answers it: subscribes first (a server, or a test, may answer at once), sends, then
    /// waits up to <see cref="Timeout"/>.
    /// </summary>
    /// <exception cref="McpToolException">The request could not be sent, or nothing answered in time.</exception>
    public async Task<TArgs> RequestAsync<TArgs>(IRcSession session, Action<EventHandler<TArgs>> add, Action<EventHandler<TArgs>> remove, Func<TArgs, bool> matches, Func<Task<bool>> send, string what)
        where TArgs : EventArgs
    {
        var answer = new TaskCompletionSource<TArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<TArgs> handler = (_, e) =>
        {
            if (matches(e)) answer.TrySetResult(e);
        };
        EventHandler<DisconnectedEventArgs> ended = (_, e) => answer.TrySetException(new McpToolException("The connection ended while waiting for " + what + ": " + e.Reason));
        add(handler);
        session.Disconnected += ended;
        try
        {
            if (!await send().ConfigureAwait(true))
            {
                if (answer.Task.IsFaulted) await answer.Task.ConfigureAwait(true);
                throw new McpToolException("The request for " + what + " could not be sent" + Because(session) + ".");
            }
            return await WaitAsync(answer.Task, what, () => session.IsConnected).ConfigureAwait(true);
        }
        finally
        {
            remove(handler);
            session.Disconnected -= ended;
            // A disconnect that came while nothing waited any more is no error of its own: mark it seen.
            if (answer.Task.IsFaulted) _ = answer.Task.Exception;
        }
    }

    /// <summary>
    /// Waits for a task up to <see cref="Timeout"/>. With <paramref name="alive"/> the wait also ends, every half second, once the connection
    /// is gone: a connection the program replaced (a reconnect) is disposed without a disconnect event.
    /// </summary>
    /// <exception cref="McpToolException">The time ran out, or the connection is gone.</exception>
    public async Task<T> WaitAsync<T>(Task<T> task, string what, Func<bool>? alive = null)
    {
        DateTimeOffset end = DateTimeOffset.UtcNow + Timeout;
        while (!task.IsCompleted)
        {
            TimeSpan left = end - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) throw new McpToolException("The server did not answer " + what + " within " + (int)Timeout.TotalSeconds + " seconds.");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
            TimeSpan slice = alive is null ? left : left < TimeSpan.FromMilliseconds(500) ? left : TimeSpan.FromMilliseconds(500);
            await Task.WhenAny(task, Task.Delay(slice, stop.Token)).ConfigureAwait(true);
            stop.Cancel();
            Token.ThrowIfCancellationRequested();
            if (!task.IsCompleted && alive is not null && !alive()) throw new McpToolException("The connection ended while waiting for " + what + ".");
        }
        return await task.ConfigureAwait(true);
    }

    /// <summary>
    /// Waits <c>collectMilliseconds</c> (or <paramref name="defaultMilliseconds"/>; at most 30 seconds) for what the server says after a
    /// change, and returns the journal entries of the kinds given that arrived after <paramref name="since"/>.
    /// </summary>
    public async Task<IReadOnlyList<McpJournalEntry>> CollectAsync(McpSessionTarget target, long since, int defaultMilliseconds, params string[] kinds)
    {
        int milliseconds = Math.Clamp(Args.Int("collectMilliseconds", defaultMilliseconds), 0, 30000);
        if (milliseconds > 0)
        {
            try
            {
                await Task.Delay(milliseconds, Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<McpJournalEntry>();
            }
        }
        return target.Journal.Read(since, kinds, 200);
    }

    /// <summary>": the last error" of a session, or empty.</summary>
    public static string Because(IRcSession session) => string.IsNullOrEmpty(session.LastError) ? string.Empty : ": " + session.LastError;

    /// <summary>Fails with the session's last error when a send was refused.</summary>
    public static void Sent(bool sent, IRcSession session, string what)
    {
        if (!sent) throw new McpToolException(what + " could not be sent" + Because(session) + ".");
    }
}

/// <summary>JSON pieces the server tools write.</summary>
internal static class McpJson
{
    public static void Entries(Utf8JsonWriter writer, string name, IReadOnlyList<McpJournalEntry> entries)
    {
        writer.WriteStartArray(name);
        foreach (McpJournalEntry entry in entries) Entry(writer, entry);
        writer.WriteEndArray();
    }

    public static void Entry(Utf8JsonWriter writer, McpJournalEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteNumber("seq", entry.Sequence);
        writer.WriteString("time", entry.Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteString("kind", entry.Kind);
        if (entry.Source.Length > 0) writer.WriteString("from", JsonText.Clean(entry.Source));
        writer.WriteString("text", JsonText.Clean(entry.Text));
        writer.WriteEndObject();
    }

    public static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string value in values) writer.WriteStringValue(JsonText.Clean(value));
        writer.WriteEndArray();
    }

    public static void Text(Utf8JsonWriter writer, string name, string? value) => writer.WriteString(name, JsonText.Clean(value));
}

/// <summary>How server file bytes become text and back: UTF-8 when the bytes are valid UTF-8, otherwise Windows-1252 (the servers' encoding).</summary>
internal static class McpFileText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The encodings a tool accepts: <c>auto</c>, <c>utf8</c>, <c>windows-1252</c> (also <c>latin1</c>, <c>ansi</c>), <c>base64</c>.</summary>
    public static string Normalize(string encoding) => (encoding ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "auto" or "text" => "auto",
        "utf8" or "utf-8" => "utf8",
        "utf8-bom" or "utf-8-bom" => "utf8-bom",
        "windows-1252" or "cp1252" or "1252" or "latin1" or "latin-1" or "ansi" or "iso-8859-1" => "windows-1252",
        "base64" or "binary" => "base64",
        string other => throw new McpToolException("Unknown encoding \"" + other + "\": use auto, utf8, utf8-bom, windows-1252 or base64."),
    };

    /// <summary>Whether the bytes look like a binary file (a NUL byte).</summary>
    public static bool IsBinary(ReadOnlySpan<byte> content) => content.IndexOf((byte)0) >= 0;

    /// <summary>Whether the bytes are plain ASCII without a byte-order mark: then they are UTF-8 and Windows-1252 alike, and have no encoding of their own.</summary>
    public static bool IsAscii(ReadOnlySpan<byte> content) => content.IndexOfAnyInRange((byte)0x80, (byte)0xFF) < 0;

    /// <summary>Decodes; returns the encoding used (<c>utf8</c>, <c>utf8-bom</c> or <c>windows-1252</c>).</summary>
    public static string Decode(byte[] content, string encoding, out string text)
    {
        bool bom = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF;
        switch (encoding)
        {
            case "windows-1252":
                text = RcText.GetString(content);
                return "windows-1252";
            case "utf8":
            case "utf8-bom":
                // Asked for UTF-8, bytes that are not UTF-8 are refused: decoding them as U+FFFD and writing that back would lose them.
                try
                {
                    text = StrictUtf8.GetString(bom ? content.AsSpan(3) : content);
                }
                catch (DecoderFallbackException)
                {
                    throw new McpToolException("The bytes are not valid UTF-8 (they look like Windows-1252); use encoding auto or windows-1252.");
                }
                return bom ? "utf8-bom" : "utf8";
            default:
                try
                {
                    text = StrictUtf8.GetString(bom ? content.AsSpan(3) : content);
                    return bom ? "utf8-bom" : "utf8";
                }
                catch (DecoderFallbackException)
                {
                    text = RcText.GetString(content);
                    return "windows-1252";
                }
        }
    }

    /// <summary>Encodes text as <paramref name="encoding"/> (<c>auto</c> is UTF-8 when the text has characters Windows-1252 lacks, otherwise Windows-1252).</summary>
    public static byte[] Encode(string text, string encoding)
    {
        switch (encoding)
        {
            case "utf8":
                return new UTF8Encoding(false).GetBytes(text);
            case "utf8-bom":
                return Encoding.UTF8.GetPreamble().Concat(new UTF8Encoding(false).GetBytes(text)).ToArray();
            case "windows-1252":
                return RcText.GetBytes(text);
            default:
                byte[] single = RcText.GetBytes(text);
                return RcText.GetString(single) == text ? single : new UTF8Encoding(false).GetBytes(text);
        }
    }
}
