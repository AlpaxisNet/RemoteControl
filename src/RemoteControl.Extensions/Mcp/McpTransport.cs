using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteControl.Extensions;

/// <summary>
/// Where the running program listens for the <c>--mcp</c> process, with the C++ names so that both halves find each other:
/// on Linux and macOS the Unix-domain socket <c>$XDG_RUNTIME_DIR/GScriptRC/mcp-&lt;pid&gt;.sock</c> (folder mode 0700, socket 0600;
/// without <c>XDG_RUNTIME_DIR</c> the GLib fallback <c>$XDG_CACHE_HOME</c> or <c>~/.cache</c>), on Windows the named pipe
/// <c>\\.\pipe\GraalRCMcp-&lt;pid&gt;</c> (message mode, current user only).
/// </summary>
public sealed record McpTransportOptions
{
    /// <summary>The prefix of the Windows pipe name.</summary>
    public const string DefaultPipePrefix = "GraalRCMcp-";

    /// <summary>The longest message line accepted (the C++ stopped at 1 MiB, which large editors exceeded, D-39).</summary>
    public const int MaximumMessageLength = 64 * 1024 * 1024;

    /// <summary>The folder of the sockets (Linux and macOS).</summary>
    public string SocketDirectory { get; init; } = DefaultSocketDirectory();

    /// <summary>The prefix of the pipe names (Windows).</summary>
    public string PipePrefix { get; init; } = DefaultPipePrefix;

    /// <summary>How long the <c>--mcp</c> process waits for one running program to answer its probe.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The socket folder the C++ used: <c>g_get_user_runtime_dir()/GScriptRC</c>.</summary>
    /// <returns>The folder.</returns>
    public static string DefaultSocketDirectory()
    {
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime))
        {
            runtime = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrEmpty(runtime)) runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }
        return Path.Combine(runtime, RemoteControl.Core.AppPaths.FolderName);
    }

    /// <summary>The socket of one running program.</summary>
    /// <param name="processId">Its process id.</param>
    /// <returns>The socket path.</returns>
    public string SocketPath(int processId) => Path.Combine(SocketDirectory, "mcp-" + processId.ToString(CultureInfo.InvariantCulture) + ".sock");

    /// <summary>The pipe name (without <c>\\.\pipe\</c>) of one running program.</summary>
    /// <param name="processId">Its process id.</param>
    /// <returns>The pipe name.</returns>
    public string PipeName(int processId) => PipePrefix + processId.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Opening, accepting and probing the local connections between the <c>--mcp</c> process and the running program.</summary>
internal static partial class McpTransport
{
    private static readonly UTF8Encoding Utf8 = new(false, false);

    /// <summary>The probe the <c>--mcp</c> process sends to each running program (the C++ text).</summary>
    public const string ProbeRequest = "{\"jsonrpc\":\"2.0\",\"id\":-9001,\"method\":\"rc/instance\",\"params\":{}}";

    [GeneratedRegex("^mcp-([0-9]+)\\.sock$", RegexOptions.CultureInvariant)]
    private static partial Regex SocketName();

    /// <summary>Writes one line (a line break is added) in one write, so that a Windows message-mode pipe carries it as one message.</summary>
    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        byte[] bytes = Utf8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The process ids of the running programs that may listen (from the socket names, or the pipe names and program processes on Windows).</summary>
    public static IReadOnlyList<int> Candidates(McpTransportOptions options, int requestedInstance)
    {
        if (requestedInstance != 0) return new[] { requestedInstance };
        var result = new List<int>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                foreach (string pipe in Directory.GetFiles(@"\\.\pipe\"))
                {
                    string name = Path.GetFileName(pipe);
                    if (name.StartsWith(options.PipePrefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(options.PipePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int id)) result.Add(id);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Pipe listing is not available: fall back to the program processes, as the C++ did.
            }
            if (result.Count == 0)
            {
                foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcessesByName("RemoteControl"))
                {
                    using (process)
                    {
                        if (process.Id != Environment.ProcessId) result.Add(process.Id);
                    }
                }
            }
            return result.Distinct().ToList();
        }
        try
        {
            if (!Directory.Exists(options.SocketDirectory)) return result;
            foreach (string file in Directory.EnumerateFileSystemEntries(options.SocketDirectory))
            {
                Match match = SocketName().Match(Path.GetFileName(file));
                if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int id)) result.Add(id);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder lists no program.
        }
        result.Sort();
        return result;
    }

    /// <summary>Connects to one running program; <c>null</c> when it does not listen.</summary>
    public static async Task<Stream?> ConnectAsync(McpTransportOptions options, int processId, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) return await ConnectPipeAsync(options, processId, cancellationToken).ConfigureAwait(false);
        string path = options.SocketPath(processId);
        if (!File.Exists(path)) return null;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.ProbeTimeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or IOException)
        {
            socket.Dispose();
            if (cancellationToken.IsCancellationRequested) throw;
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<Stream?> ConnectPipeAsync(McpTransportOptions options, int processId, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", options.PipeName(processId), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)Math.Min(int.MaxValue, options.ProbeTimeout.TotalMilliseconds), cancellationToken).ConfigureAwait(false);
            pipe.ReadMode = PipeTransmissionMode.Message;
            return pipe;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            return null;
        }
    }

    /// <summary>
    /// Probes every candidate with <see cref="ProbeRequest"/> and keeps the connection of the one with the best
    /// <see cref="McpProtocol.InstanceScore"/> whose answer names its own process id (<c>selectBridgeSocket</c>); the others are closed.
    /// </summary>
    public static async Task<(Stream Stream, BoundedLineReader Reader, int ProcessId)?> SelectAsync(McpTransportOptions options, int requestedInstance, CancellationToken cancellationToken)
    {
        (Stream Stream, BoundedLineReader Reader, int ProcessId)? best = null;
        int bestScore = -1;
        foreach (int candidate in Candidates(options, requestedInstance))
        {
            Stream? stream = await ConnectAsync(options, candidate, cancellationToken).ConfigureAwait(false);
            if (stream is null) continue;
            var reader = new BoundedLineReader(stream, McpTransportOptions.MaximumMessageLength);
            string? probe = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.ProbeTimeout);
                await WriteLineAsync(stream, ProbeRequest, timeout.Token).ConfigureAwait(false);
                probe = (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false))?.Text;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            int processId = probe is null ? 0 : McpProtocol.GetIntField(probe, "processId", 0);
            if (probe is null || processId != candidate)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            int score = McpProtocol.InstanceScore(McpProtocol.GetBoolField(probe, "active", false), McpProtocol.GetBoolField(probe, "serverConnected", false), McpProtocol.GetIntField(probe, "editorCount", 0));
            if (score > bestScore)
            {
                if (best is { } previous) await previous.Stream.DisposeAsync().ConfigureAwait(false);
                best = (stream, reader, processId);
                bestScore = score;
            }
            else
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        return best;
    }
}
