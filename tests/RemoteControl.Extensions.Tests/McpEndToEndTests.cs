using System.Text;
using System.Text.Json;
using RemoteControl.Core;

namespace RemoteControl.Extensions.Tests;

/// <summary>The <c>--mcp</c> server talking to a running bridge over the real Unix-domain socket in a temporary folder.</summary>
public sealed class McpEndToEndTests
{
    private static readonly RcOptions Enabled = RcOptions.Defaults with { McpEnabled = true };

    private static async Task<(int Code, string[] Output, string Error)> RunStdioAsync(McpTransportOptions transport, string[] requests, int instance = 0, RcOptions? options = null)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", requests) + "\n"));
        using var output = new MemoryStream();
        using var error = new StringWriter();
        int code = await Wait.WithTimeout(McpStdioServer.RunAsync(options ?? Enabled, input, output, error, instance, transport));
        string text = Encoding.UTF8.GetString(output.ToArray());
        string[] lines = text.Length == 0 ? Array.Empty<string>() : text.TrimEnd('\n').Split('\n');
        return (code, lines, error.ToString());
    }

    private static int ProcessIdOf(string statusResponse)
    {
        using JsonDocument document = JsonDocument.Parse(statusResponse);
        string text = document.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        using JsonDocument status = JsonDocument.Parse(text);
        return status.RootElement.GetProperty("processId").GetInt32();
    }

    private const string Status = "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"rc_status\",\"arguments\":{}}}";

    [UnixFact]
    public async Task AClientSessionOverTheSocket()
    {
        using var folder = new TempFolder(shortPath: true);
        using var ui = new UiThread();
        await using var setup = new BridgeSetup(folder, ui: ui, socketDirectory: folder.Combine("run", "GScriptRC"));
        Assert.True(setup.Bridge.Start().Success);
        Assert.True(setup.Bridge.IsListening);
        string socket = setup.Transport.SocketPath(4242);
        Assert.Equal(socket, setup.Bridge.Endpoint);
        Assert.Equal(Path.Combine(folder.Combine("run", "GScriptRC"), "mcp-4242.sock"), socket);
        Assert.True(File.Exists(socket));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(setup.Transport.SocketDirectory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(socket));
        }

        string big = new('x', 3 * 1024 * 1024);
        var requests = new[]
        {
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":2}}",
            "this is not json",
            "{\"jsonrpc\":\"2.0\",\"id\":\"w\",\"method\":\"tools/call\",\"params\":{\"name\":\"rc_write_script_editor\",\"arguments\":{\"editor\":\"Weapon: bow\",\"text\":\"" + big + "\"}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"rc_list_script_editors\",\"arguments\":{}}}",
            Status,
        };
        (int code, string[] output, string error) = await RunStdioAsync(setup.Transport, requests);
        Assert.Equal(McpStdioServer.ExitOk, code);
        Assert.Equal(string.Empty, error);
        Assert.Equal(6, output.Length);
        Assert.Contains("\"protocolVersion\":\"2025-06-18\"", output[0]);
        Assert.Contains("\"id\":2,", output[1]);
        Assert.Contains("rc_focused_window_action", output[1]);
        Assert.Contains("\"code\":-32700", output[2]);
        Assert.Contains("\"id\":\"w\"", output[3]);
        Assert.Contains("changes remain unsaved", output[3]);
        Assert.True(output[4].Length > big.Length, "the editor list holds the 3 MiB buffer");
        Assert.Equal(4242, ProcessIdOf(output[5]));
        Assert.Equal(big, setup.Host.Editors["Weapon: bow"].Text);
        Assert.Equal(0, setup.Host.WrongThread);

        await setup.Bridge.DisposeAsync();
        Assert.False(File.Exists(socket));
        Assert.False(setup.Bridge.IsListening);
        (code, _, error) = await RunStdioAsync(setup.Transport, new[] { Status });
        Assert.Equal(McpStdioServer.ExitNoBridge, code);
        Assert.Equal("RC MCP bridge unavailable: no running enabled RC GUI client.\n", error.Replace("\r", string.Empty, StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task TheBestInstanceIsChosenUnlessOneIsRequested()
    {
        using var folder = new TempFolder(shortPath: true);
        string sockets = folder.Combine("s");
        await using var idle = new BridgeSetup(folder, processId: 1001, socketDirectory: sockets);
        idle.Host.Editors.Clear();
        using var second = new TempFolder(shortPath: true);
        await using var busy = new BridgeSetup(second, processId: 1002, socketDirectory: sockets);
        busy.Host.Server = new McpServerState("Other", false);
        foreach (var window in busy.Host.Windows.ToList()) { busy.Host.Windows.Remove(window); }
        Assert.True(idle.Bridge.Start().Success);
        Assert.True(busy.Bridge.Start().Success);
        File.WriteAllText(Path.Combine(sockets, "mcp-999.sock"), "stale file, nobody listens");
        File.WriteAllText(Path.Combine(sockets, "mcp-abc.sock"), "not a process id");
        Assert.Equal(new[] { 999, 1001, 1002 }, McpTransport.Candidates(idle.Transport, 0));

        (int code, string[] output, _) = await RunStdioAsync(idle.Transport, new[] { Status });
        Assert.Equal(McpStdioServer.ExitOk, code);
        Assert.Equal(1002, ProcessIdOf(Assert.Single(output)));

        (code, output, _) = await RunStdioAsync(idle.Transport, new[] { Status }, instance: 1001);
        Assert.Equal(1001, ProcessIdOf(Assert.Single(output)));

        (code, _, _) = await RunStdioAsync(idle.Transport, new[] { Status }, instance: 999);
        Assert.Equal(McpStdioServer.ExitNoBridge, code);
    }

    [UnixFact]
    public async Task ABridgeWithMcpDisabledIsNotChosen()
    {
        using var folder = new TempFolder(shortPath: true);
        await using var setup = new BridgeSetup(folder, o => o with { McpEnabled = false }, socketDirectory: folder.Combine("s"));
        Assert.True(setup.Bridge.Start().Success);
        (int code, _, string error) = await RunStdioAsync(setup.Transport, new[] { Status });
        Assert.Equal(McpStdioServer.ExitNoBridge, code);
        Assert.Contains("unavailable", error);
    }

    [Fact]
    public async Task DisabledInTheOptionsEndsAtOnce()
    {
        using var folder = new TempFolder(shortPath: true);
        var transport = new McpTransportOptions { SocketDirectory = folder.Combine("s") };
        (int code, string[] output, string error) = await RunStdioAsync(transport, new[] { Status }, options: RcOptions.Defaults with { McpEnabled = false });
        Assert.Equal(McpStdioServer.ExitDisabled, code);
        Assert.Empty(output);
        Assert.Equal("RC MCP integration is disabled in Options.", error.Trim());
    }

    [UnixFact]
    public async Task TheSessionEndsWhenTheProgramGoesAway()
    {
        using var folder = new TempFolder(shortPath: true);
        await using var setup = new BridgeSetup(folder, socketDirectory: folder.Combine("s"));
        Assert.True(setup.Bridge.Start().Success);
        var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        using var reader = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, pipe.ClientSafePipeHandle);
        using var output = new MemoryStream();
        using var error = new StringWriter();
        Task<int> run = McpStdioServer.RunAsync(Enabled, reader, output, error, 0, setup.Transport);
        byte[] request = Encoding.UTF8.GetBytes(Status + "\n");
        await pipe.WriteAsync(request);
        await Wait.UntilAsync(() => output.Length > 0, "the first response");
        await setup.Bridge.DisposeAsync();
        await pipe.WriteAsync(request);
        await pipe.WriteAsync(request);
        int code = await Wait.WithTimeout(run);
        pipe.Dispose();
        Assert.Equal(McpStdioServer.ExitOk, code);
    }

    [Fact]
    public void TransportNames()
    {
        var transport = new McpTransportOptions { SocketDirectory = "/run/user/1000/GScriptRC" };
        Assert.Equal("/run/user/1000/GScriptRC/mcp-77.sock".Replace('/', Path.DirectorySeparatorChar), transport.SocketPath(77).Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal("GraalRCMcp-77", transport.PipeName(77));
        Assert.EndsWith(AppPaths.FolderName, McpTransportOptions.DefaultSocketDirectory());
    }
}
