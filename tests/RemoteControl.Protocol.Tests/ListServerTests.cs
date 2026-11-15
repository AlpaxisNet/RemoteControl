using System.Net;
using System.Net.Sockets;
using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Testing;

namespace RemoteControl.Protocol.Tests;

public sealed class ListServerTests
{
    [Fact]
    public async Task Login_returns_the_servers_and_the_status_texts()
    {
        await using var server = new FakeListServer();
        server.Servers.Add(new ServerInfo("P Gold Server", "English", "A gold server", "http://gold", "6.0", 12, "127.0.0.1", 14900));
        server.Servers.Add(new ServerInfo("Classic", "German", "d", "h", "v", 3, "10.0.0.1", 14901));

        ListServerResult result = await ListServerClient.LoginAsync(server.Host, server.Port, "Staff", "secret", TimeSpan.FromSeconds(3)).Within();

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Servers.Count);
        ServerInfo gold = result.Servers[0];
        Assert.Equal("P Gold Server", gold.Name);
        Assert.Equal("Gold Server", gold.DisplayName);
        Assert.Equal(ServerKind.Gold, gold.Kind);
        Assert.Equal(12, gold.Players);
        Assert.Equal(14900, gold.Port);
        Assert.Equal(ServerKind.Bronze, result.Servers[1].Kind);
        Assert.Equal("Welcome to the test hub.\nThere are 1 server(s) online.", result.StatusText);
        Assert.Equal("https://example.invalid/", result.SiteUrl);
        ListServerLogin login = Assert.Single(server.Logins);
        Assert.Equal(new ListServerLogin(4, "G3D30123rc2", "Staff", "secret"), login);
    }

    [Fact]
    public async Task An_error_packet_is_reported()
    {
        await using var server = new FakeListServer { Error = "Account name or password is invalid" };
        ListServerResult result = await ListServerClient.LoginAsync(server.Host, server.Port, "Staff", "wrong", TimeSpan.FromSeconds(3)).Within();
        Assert.False(result.Success);
        Assert.Equal("Account name or password is invalid", result.Error);
        Assert.Empty(result.Servers);
    }

    [Fact]
    public async Task A_silent_list_server_times_out()
    {
        await using var server = new FakeListServer { Silent = true };
        ListServerResult result = await ListServerClient.LoginAsync(server.Host, server.Port, "Staff", "secret", TimeSpan.FromMilliseconds(500)).Within();
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        await using var server = new FakeListServer { Silent = true };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ListServerClient.LoginAsync(server.Host, server.Port, "Staff", "secret", TimeSpan.FromSeconds(10), cancel.Token)).Within();
    }

    [Fact]
    public async Task A_closed_port_fails_quickly()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        // Linux refuses at once; Windows tries a closed local port for about two seconds first, so the limit leaves room for that.
        ListServerResult result = await ListServerClient.LoginAsync("127.0.0.1", port, "Staff", "secret", TimeSpan.FromSeconds(10)).Within(TimeSpan.FromSeconds(20));
        Assert.False(result.Success);
        Assert.StartsWith("Failed to connect", result.Error);
    }

    [Fact]
    public async Task Overlong_credentials_are_refused_before_connecting()
    {
        ListServerResult result = await ListServerClient.LoginAsync("127.0.0.1", 1, new string('a', 300), "x").Within();
        Assert.False(result.Success);
    }
}
