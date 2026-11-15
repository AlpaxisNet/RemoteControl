using RemoteControl.Extensions;

namespace RemoteControl.App;

/// <summary>What the process does: run the program with its windows, or be the MCP server on standard input and output.</summary>
internal enum StartMode
{
    /// <summary>The program with its windows.</summary>
    Windows,

    /// <summary>The <c>--mcp</c> process: no window, no Avalonia (<c>TMain.cpp:721</c>).</summary>
    McpServer,
}

/// <summary>
/// The command line of the C++ (<c>TMain.cpp:656-685</c>): <c>--mcp</c>, <c>--mcp-instance=N</c> / <c>--mcp-instance N</c> (read by
/// <see cref="McpCommandLine"/>), <c>--debug</c> (log lines to standard error) and <c>--packetlog</c> (implies <c>--debug</c>; every received
/// packet to standard error). Everything else is passed on to Avalonia.
/// </summary>
/// <param name="Mode">What the process does.</param>
/// <param name="McpInstance">The process id of <c>--mcp-instance</c>, or 0.</param>
/// <param name="Debug">Whether log lines go to standard error.</param>
/// <param name="PacketLog">Whether every received packet goes to standard error.</param>
/// <param name="Remaining">The other arguments, in order.</param>
internal sealed record StartupArguments(StartMode Mode, int McpInstance, bool Debug, bool PacketLog, IReadOnlyList<string> Remaining)
{
    /// <summary>The arguments of a start without any.</summary>
    public static StartupArguments Default { get; } = new(StartMode.Windows, 0, false, false, Array.Empty<string>());

    /// <summary>Reads the command line.</summary>
    /// <param name="arguments">The arguments without the program name.</param>
    /// <returns>What to do.</returns>
    public static StartupArguments Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        McpCommandLine mcp = McpCommandLine.Parse(arguments);
        bool debug = false;
        bool packetLog = false;
        var remaining = new List<string>();
        foreach (string argument in mcp.Remaining)
        {
            if (argument == "--debug") debug = true;
            else if (argument == "--packetlog") packetLog = debug = true;
            else remaining.Add(argument);
        }
        return new StartupArguments(mcp.McpMode ? StartMode.McpServer : StartMode.Windows, mcp.Instance, debug, packetLog, remaining);
    }
}
