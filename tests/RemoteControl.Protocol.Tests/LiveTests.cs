using System.Collections.Concurrent;
using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Session;
using Xunit.Abstractions;

namespace RemoteControl.Protocol.Tests;

/// <summary>A fact that runs only when RC_LIVE_LISTSERVER, RC_LIVE_ACCOUNT and RC_LIVE_PASSWORD_FILE are all set.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RC_LIVE_LISTSERVER")) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RC_LIVE_ACCOUNT"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RC_LIVE_PASSWORD_FILE")))
            Skip = "Set RC_LIVE_LISTSERVER (host:port), RC_LIVE_ACCOUNT and RC_LIVE_PASSWORD_FILE to run against a real list server.";
    }
}

public sealed class LiveTests(ITestOutputHelper output)
{
    [LiveFact]
    public async Task List_server_rc_chat_stats_and_nc()
    {
        string listServer = Environment.GetEnvironmentVariable("RC_LIVE_LISTSERVER")!;
        string account = Environment.GetEnvironmentVariable("RC_LIVE_ACCOUNT")!;
        string password = File.ReadAllText(Environment.GetEnvironmentVariable("RC_LIVE_PASSWORD_FILE")!).Trim('\r', '\n');
        string? wanted = Environment.GetEnvironmentVariable("RC_LIVE_SERVER");
        int colon = listServer.LastIndexOf(':');
        Assert.True(colon > 0, "RC_LIVE_LISTSERVER must be host:port");
        string host = listServer[..colon];
        int port = int.Parse(listServer[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        ListServerResult list = await ListServerClient.LoginAsync(host, port, account, password, TimeSpan.FromSeconds(15));
        Assert.True(list.Success, list.Error);
        output.WriteLine($"List server: {list.Servers.Count} servers. {list.StatusText}");
        foreach (ServerInfo info in list.Servers) output.WriteLine($"  {info.Name} ({info.Players}) {info.Host}:{info.Port}");
        ServerInfo server = wanted is { Length: > 0 }
            ? list.Servers.First(item => item.Name == wanted || item.DisplayName == wanted)
            : list.Servers.First();

        var chat = new ConcurrentQueue<string>();
        using var session = new RcSession(new RcSessionOptions { Account = account, Password = password, Log = line => output.WriteLine("log: " + line) });
        session.RcChatReceived += (_, args) => chat.Enqueue(args.Text);
        session.Disconnected += (_, args) => output.WriteLine("Disconnected: " + args.Reason);
        ConnectResult result = await session.ConnectAsync(server);
        Assert.True(result.Success, result.Error);
        output.WriteLine($"Connected to {server.DisplayName}.");

        await WaitAsync(() => !chat.IsEmpty, TimeSpan.FromSeconds(10));
        await Task.Delay(1000);
        while (chat.TryDequeue(out string? line)) output.WriteLine("chat: " + line);

        Assert.True(await session.SendRcChatAsync("/stats"));
        await WaitAsync(() => !chat.IsEmpty, TimeSpan.FromSeconds(10));
        await Task.Delay(1000);
        while (chat.TryDequeue(out string? line)) output.WriteLine("/stats: " + line);

        await WaitAsync(() => session.HasNcServer, TimeSpan.FromSeconds(5));
        if (!session.HasNcServer)
        {
            output.WriteLine("The server sent no NC address.");
            return;
        }

        var weapons = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.WeaponListReceived += (_, args) => weapons.TrySetResult(args.Value);
        bool nc = await session.ConnectNcAsync();
        output.WriteLine($"NC at {session.NcServerAddress}: {(nc ? "connected" : "failed: " + session.LastError)}");
        if (!nc) return;
        Task finished = await Task.WhenAny(weapons.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        output.WriteLine(finished == weapons.Task ? $"Weapon list: {weapons.Task.Result} weapons." : "No weapon list arrived.");
        session.Disconnect();
    }

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
    }
}
