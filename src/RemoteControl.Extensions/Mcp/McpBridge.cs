using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using RemoteControl.Core;

namespace RemoteControl.Extensions;

/// <summary>Settings of an <see cref="McpBridge"/>.</summary>
public sealed record McpBridgeOptions
{
    /// <summary>What the tools do in the program (the windows implement it).</summary>
    public required IMcpHost Host { get; init; }

    /// <summary>The options: read for every request (permissions, approvals, scope, audit) and updated when the user always allows a script type.</summary>
    public required OptionsStore Options { get; init; }

    /// <summary>Runs an action on the host's context (the UI thread), in order, without waiting (for example <c>Dispatcher.UIThread.Post</c>).</summary>
    public required Action<Action> Post { get; init; }

    /// <summary>The audit log file (<see cref="AppPaths.McpAuditLogFile"/>), appended to while <c>mcpaudit</c> is on.</summary>
    public required string AuditLogFile { get; init; }

    /// <summary>Where to listen; the C++ names by default.</summary>
    public McpTransportOptions Transport { get; init; } = new();

    /// <summary>The process id the bridge listens and answers under (the current process by default; tests use others).</summary>
    public int ProcessId { get; init; } = Environment.ProcessId;

    /// <summary>
    /// Where the server tools keep the previous version of what they overwrite or delete (<see cref="AppPaths.McpBackupDirectory"/>);
    /// <c>mcp-backups</c> beside the audit log when not set.
    /// </summary>
    public string? BackupDirectory { get; init; }

    /// <summary>
    /// The MCP workspace folder (<see cref="AppPaths.McpWorkspaceDirectory"/>): relative entries of <c>mcpfileroots</c> are taken inside it;
    /// <c>mcp-files</c> beside the audit log when not set.
    /// </summary>
    public string? WorkspaceDirectory { get; init; }

    /// <summary>The GS2 reference beside the program (<see cref="AppPaths.Gs2ReferenceDirectory"/>); <c>gs2-reference</c> beside the executable when not set.</summary>
    public string? ReferenceDirectory { get; init; }

    /// <summary>The cache file of the script API (the editors' <c>scriptapi.json</c>); <c>cache/editors/scriptapi.json</c> beside the audit log when not set.</summary>
    public string? ScriptApiCacheFile { get; init; }

    /// <summary>The HTTP handler the script API is downloaded with (tests); a shared one with certificate checks when not set.</summary>
    public HttpMessageHandler? ScriptApiHandler { get; init; }

    /// <summary>Where problems are logged; <see cref="AppLog.Default"/> when not set.</summary>
    public AppLog? Log { get; init; }

    /// <summary>
    /// Why the host denies approvals without asking anyone, added to the message of every denial (a headless program: "no one can answer
    /// approval questions ..."); empty when the user is asked.
    /// </summary>
    public string? DeniedNote { get; init; }
}

/// <summary>
/// The MCP bridge inside the running program (<c>startMcpGuiBridge</c>): listens on the per-user socket (pipe on Windows) for the
/// <c>--mcp</c> processes, reads one JSON-RPC request per line from each, handles it and writes one response line per request
/// (none for notifications).
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> Connections are served on background tasks; every <see cref="IMcpHost"/> call is posted to
/// <see cref="McpBridgeOptions.Post"/> (the UI thread) and awaited there, so the host needs no locks (AGENTS.md rule 1). The C++ blocked
/// a client thread on the GTK thread for every request and ran modal approvals inside an idle callback (D-19); here an approval is
/// just an awaited host call.</para>
/// <para><b>Lifetime.</b> <see cref="DisposeAsync"/> stops listening, closes every connection, cancels the host calls not yet
/// answered and removes the socket file. A host call that completes after that is ignored.</para>
/// </remarks>
public sealed class McpBridge : IAsyncDisposable, IDisposable
{
    private readonly McpBridgeOptions _options;
    private readonly AppLog _log;
    private readonly McpRequestProcessor _processor;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<Stream> _clients = new();
    private readonly HashSet<Task> _tasks = new();
    private readonly SemaphoreSlim _auditGate = new(1, 1);
    private Socket? _listener;
    private string? _socketPath;
    private Task _acceptTask = Task.CompletedTask;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a bridge; call <see cref="Start"/> to listen.</summary>
    /// <param name="options">The host, options, context and transport.</param>
    public McpBridge(McpBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _log = options.Log ?? AppLog.Default;
        string data = Path.GetDirectoryName(Path.GetFullPath(options.AuditLogFile)) ?? Path.GetTempPath();
        var services = new McpToolServices(new McpBackupStore(options.BackupDirectory ?? Path.Combine(data, "mcp-backups"), _log), options.WorkspaceDirectory ?? Path.Combine(data, "mcp-files"),
            SaveScriptTypeApproval, options.ProcessId, _log, options.ReferenceDirectory,
            new McpScriptApi(options.ScriptApiCacheFile ?? Path.Combine(data, "cache", "editors", "scriptapi.json"), options.ScriptApiHandler));
        _processor = new McpRequestProcessor(new Invoker(this), () => options.Options.Current, SaveScriptTypeApproval, options.ProcessId, services: services, deniedNote: options.DeniedNote);
    }

    /// <summary>Where the bridge listens: the socket path, or the pipe name on Windows.</summary>
    public string Endpoint => OperatingSystem.IsWindows() ? _options.Transport.PipeName(_options.ProcessId) : _options.Transport.SocketPath(_options.ProcessId);

    /// <summary>Whether the bridge listens.</summary>
    public bool IsListening
    {
        get { lock (_gate) return _started && !_disposed; }
    }

    /// <summary>Starts listening. Never throws: a failure is logged and returned.</summary>
    /// <returns>Success, or why the bridge could not listen.</returns>
    public McpActionResult Start()
    {
        lock (_gate)
        {
            if (_disposed) return McpActionResult.Fail("The MCP bridge is closed");
            if (_started) return McpActionResult.Ok;
            try
            {
                if (OperatingSystem.IsWindows()) _acceptTask = Task.Run(() => OperatingSystem.IsWindows() ? AcceptPipesAsync(_stop.Token) : Task.CompletedTask);
                else
                {
                    _listener = ListenSocket();
                    _acceptTask = Task.Run(() => AcceptSocketsAsync(_listener, _stop.Token));
                }
                _started = true;
                return McpActionResult.Ok;
            }
            catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException or ArgumentException)
            {
                _log.Error("The MCP bridge could not listen on " + Endpoint, exception);
                _listener?.Dispose();
                _listener = null;
                return McpActionResult.Fail(exception.Message);
            }
        }
    }

    /// <summary>Handles one request line as a connection would (for tests and for in-process use).</summary>
    /// <param name="line">One JSON-RPC request.</param>
    /// <returns>The response line, or <c>null</c> for a notification.</returns>
    public async Task<string?> HandleAsync(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        McpOutcome outcome = await _processor.ProcessAsync(line).ConfigureAwait(false);
        if (outcome.AuditLine is not null) await AppendAuditAsync(outcome.AuditLine).ConfigureAwait(false);
        return outcome.Response;
    }

    /// <summary>Stops listening and closes every connection without waiting.</summary>
    public void Dispose() => BeginDispose();

    /// <summary>Stops listening, closes every connection and waits (a few seconds at most) for the connection tasks to end.</summary>
    /// <returns>A task that ends when the bridge is down.</returns>
    public async ValueTask DisposeAsync()
    {
        Task all = BeginDispose();
        await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }

    private Task BeginDispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                try { _stop.Cancel(); } catch (AggregateException exception) { _log.Warn("Stopping the MCP bridge failed", exception); }
                _listener?.Dispose();
                foreach (Stream client in _clients) client.Dispose();
                _clients.Clear();
                if (_socketPath is not null)
                {
                    try { File.Delete(_socketPath); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _log.Warn("The MCP socket could not be removed", exception); }
                }
            }
            return Task.WhenAll(_tasks.Append(_acceptTask).ToArray());
        }
    }

    private Socket ListenSocket()
    {
        string directory = _options.Transport.SocketDirectory;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string path = _options.Transport.SocketPath(_options.ProcessId);
        if (File.Exists(path)) File.Delete(path);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(path));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            socket.Listen(8);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        _socketPath = path;
        return socket;
    }

    private async Task AcceptSocketsAsync(Socket listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (cancellationToken.IsCancellationRequested) return;
                _log.Warn("The MCP bridge failed to accept a connection", exception);
                try { await Task.Delay(100, cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            Serve(new NetworkStream(client, ownsSocket: true));
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task AcceptPipesAsync(CancellationToken cancellationToken)
    {
        string name = _options.Transport.PipeName(_options.ProcessId);
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 1024 * 1024, 1024 * 1024);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log.Error("The MCP pipe could not be created", exception);
                return;
            }
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) return;
                continue;
            }
            Serve(pipe);
        }
    }

    private void Serve(Stream stream)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                stream.Dispose();
                return;
            }
            _clients.Add(stream);
            Task task = Task.Run(() => ServeAsync(stream, _stop.Token));
            _tasks.Add(task);
            task.ContinueWith(done => { lock (_gate) { _tasks.Remove(done); _clients.Remove(stream); } }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var reader = new BoundedLineReader(stream, McpTransportOptions.MaximumMessageLength);
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadLine? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (line.Value.Truncated)
                {
                    _log.Warn("An MCP request was longer than the limit; the connection is closed");
                    break;
                }
                if (string.IsNullOrWhiteSpace(line.Value.Text)) continue;
                string? response = await HandleAsync(line.Value.Text).ConfigureAwait(false);
                if (response is not null) await McpTransport.WriteLineAsync(stream, response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The client went away or the bridge stops.
        }
        catch (Exception exception)
        {
            _log.Error("An MCP connection failed", exception);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task AppendAuditAsync(string line)
    {
        await _auditGate.WaitAsync().ConfigureAwait(false);
        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(_options.AuditLogFile));
            if (folder is not null) Directory.CreateDirectory(folder);
            await File.AppendAllTextAsync(_options.AuditLogFile, line + "\n", new UTF8Encoding(false)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warn("The MCP audit log could not be written", exception);
        }
        finally
        {
            _auditGate.Release();
        }
    }

    private void SaveScriptTypeApproval(string type)
    {
        try
        {
            _options.Options.Update(options => type switch
            {
                "Weapon" => options with { McpApproveWeapon = true },
                "Class" => options with { McpApproveClass = true },
                "NPC" => options with { McpApproveNpc = true },
                _ => options,
            }, this);
        }
        catch (Exception exception)
        {
            _log.Error("The MCP approval could not be saved", exception);
        }
    }

    /// <summary>Posts host calls to the host's context and hands their results back.</summary>
    private sealed class Invoker : IMcpHostInvoker
    {
        private readonly McpBridge _bridge;

        public Invoker(McpBridge bridge) => _bridge = bridge;

        public CancellationToken Stopping => _bridge._stop.Token;

        public Task<T> InvokeAsync<T>(Func<IMcpHost, Task<T>> call)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken token = _bridge._stop.Token;
            if (token.IsCancellationRequested)
            {
                completion.TrySetCanceled(token);
                return completion.Task;
            }
            CancellationTokenRegistration registration = token.Register(() => completion.TrySetCanceled(token));
            completion.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                _bridge._options.Post(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(token);
                        return;
                    }
                    try
                    {
                        Task<T> task = call(_bridge._options.Host) ?? throw new InvalidOperationException("The MCP host returned no task");
                        task.ContinueWith(done =>
                        {
                            if (done.IsCanceled) completion.TrySetCanceled();
                            else if (done.IsFaulted) completion.TrySetException(done.Exception!.InnerExceptions);
                            else completion.TrySetResult(done.Result);
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                });
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            return completion.Task;
        }
    }
}
