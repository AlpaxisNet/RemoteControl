using System.Globalization;
using System.Net.Sockets;
using RemoteControl.Core;

namespace RemoteControl.Extensions;

/// <summary>The MCP arguments of the program's command line (<c>TMain.cpp:657-666</c>).</summary>
/// <param name="McpMode">Whether <c>--mcp</c> was given: the process is an MCP server on standard input and output, not the program.</param>
/// <param name="Instance">The process id of <c>--mcp-instance=&lt;pid&gt;</c> or <c>--mcp-instance &lt;pid&gt;</c>, or 0 to pick the best running program.</param>
/// <param name="Remaining">The other arguments, in order.</param>
public sealed record McpCommandLine(bool McpMode, int Instance, IReadOnlyList<string> Remaining)
{
    /// <summary>Reads the MCP arguments; an instance that is not a number counts as 0, as <c>strtoul</c> did.</summary>
    /// <param name="arguments">The command-line arguments without the program name.</param>
    /// <returns>The parsed arguments.</returns>
    public static McpCommandLine Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        bool mcp = false;
        int instance = 0;
        var remaining = new List<string>();
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument == "--mcp") mcp = true;
            else if (argument.StartsWith("--mcp-instance=", StringComparison.Ordinal)) instance = ParseId(argument["--mcp-instance=".Length..]);
            else if (argument == "--mcp-instance" && index + 1 < arguments.Count) instance = ParseId(arguments[++index]);
            else remaining.Add(argument);
        }
        return new McpCommandLine(mcp, instance, remaining);
    }

    private static int ParseId(string text)
    {
        int length = 0;
        while (length < text.Length && char.IsAsciiDigit(text[length])) length++;
        return length > 0 && int.TryParse(text.AsSpan(0, length), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }
}

/// <summary>
/// The <c>--mcp</c> process (<c>runMcpServer</c>): an MCP server on standard input and output that an MCP client starts. It picks a
/// running program (the one given by <c>--mcp-instance</c>, otherwise the best by <see cref="McpProtocol.InstanceScore"/>), then
/// forwards every request line to it and copies one response line back for every request that has an <c>id</c>.
/// </summary>
public static class McpStdioServer
{
    /// <summary>The exit code after standard input ended or the program went away.</summary>
    public const int ExitOk = 0;

    /// <summary>The exit code when MCP is disabled in the options.</summary>
    public const int ExitDisabled = 2;

    /// <summary>The exit code when no running program with MCP enabled answered.</summary>
    public const int ExitNoBridge = 3;

    /// <summary>Runs on the process's own standard input, output and error until standard input ends.</summary>
    /// <param name="options">The options (only <c>mcpenabled</c> is read here; the running program checks the rest).</param>
    /// <param name="requestedInstance">The process id of <c>--mcp-instance</c>, or 0.</param>
    /// <param name="cancellationToken">Ends the server.</param>
    /// <returns>The exit code (<see cref="ExitOk"/>, <see cref="ExitDisabled"/> or <see cref="ExitNoBridge"/>).</returns>
    public static async Task<int> RunAsync(RcOptions options, int requestedInstance = 0, CancellationToken cancellationToken = default)
    {
        await using Stream input = Console.OpenStandardInput();
        await using Stream output = Console.OpenStandardOutput();
        return await RunAsync(options, input, output, Console.Error, requestedInstance, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs on the given streams until <paramref name="input"/> ends.</summary>
    /// <param name="options">The options (only <c>mcpenabled</c> is read here).</param>
    /// <param name="input">The MCP client's requests, one JSON-RPC message per line.</param>
    /// <param name="output">Where the responses go, one per line.</param>
    /// <param name="error">Where the messages for a person go.</param>
    /// <param name="requestedInstance">The process id of the program to use, or 0 for the best.</param>
    /// <param name="transport">Where the programs listen; the C++ names when <c>null</c>.</param>
    /// <param name="cancellationToken">Ends the server.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(RcOptions options, Stream input, Stream output, TextWriter error, int requestedInstance, McpTransportOptions? transport, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (!options.McpEnabled)
        {
            await error.WriteLineAsync("RC MCP integration is disabled in Options.").ConfigureAwait(false);
            return ExitDisabled;
        }
        transport ??= new McpTransportOptions();
        var selected = await McpTransport.SelectAsync(transport, requestedInstance, cancellationToken).ConfigureAwait(false);
        if (selected is not { } bridge)
        {
            await error.WriteLineAsync("RC MCP bridge unavailable: no running enabled RC GUI client.").ConfigureAwait(false);
            return ExitNoBridge;
        }
        await using (bridge.Stream.ConfigureAwait(false))
        {
            var requests = new BoundedLineReader(input, McpTransportOptions.MaximumMessageLength);
            try
            {
                while (await requests.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } request)
                {
                    if (request.Truncated)
                    {
                        await error.WriteLineAsync("RC MCP: a request longer than the limit was dropped.").ConfigureAwait(false);
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(request.Text)) continue;
                    await McpTransport.WriteLineAsync(bridge.Stream, request.Text, cancellationToken).ConfigureAwait(false);
                    if (!McpRequestProcessor.ExpectsResponse(request.Text)) continue;
                    ReadLine? response = await bridge.Reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (response is null) break;
                    await McpTransport.WriteLineAsync(output, response.Value.Text, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
            {
                await error.WriteLineAsync("RC MCP bridge connection closed: " + exception.Message).ConfigureAwait(false);
            }
        }
        return ExitOk;
    }
}
