using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;

namespace RemoteControl.Protocol.Tests;

internal static class TestHelpers
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<T> Within<T>(this Task<T> task, TimeSpan? timeout = null)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(timeout ?? Timeout));
        if (finished != task) throw new TimeoutException("The operation did not finish in time.");
        return await task;
    }

    public static async Task Within(this Task task, TimeSpan? timeout = null)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(timeout ?? Timeout));
        if (finished != task) throw new TimeoutException("The operation did not finish in time.");
        await task;
    }

    /// <summary>Waits for the next raise of an event whose arguments match.</summary>
    public static Task<T> Next<T>(Action<EventHandler<T>> subscribe, Action<EventHandler<T>> unsubscribe, Func<T, bool>? where = null)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<T>? handler = null;
        handler = (_, args) =>
        {
            if (where is not null && !where(args)) return;
            unsubscribe(handler!);
            completion.TrySetResult(args);
        };
        subscribe(handler);
        return completion.Task.Within();
    }

    public static RcSessionOptions Options(TimeSpan? loginTimeout = null, TimeSpan? downloadTimeout = null, Action<string>? log = null) => new()
    {
        Account = "Staff",
        Password = "secret",
        PcIdList = "linux,abc,def,",
        ConnectTimeout = TimeSpan.FromSeconds(2),
        LoginTimeout = loginTimeout ?? TimeSpan.FromSeconds(3),
        NcLoginTimeout = TimeSpan.FromSeconds(3),
        DownloadInactivityTimeout = downloadTimeout ?? TimeSpan.FromSeconds(3),
        Log = log,
    };

    /// <summary>Connects a session to a fake game server and waits for the server to see the login.</summary>
    public static async Task<(RcSession Session, FakeConnection Server)> ConnectAsync(FakeGameServer server, RcSessionOptions? options = null)
    {
        var session = new RcSession(options ?? Options());
        ConnectResult result = await session.ConnectAsync(server.Host, server.Port).Within();
        Assert.True(result.Success, result.Error);
        FakeConnection connection = await server.WaitForConnectionAsync().Within();
        return (session, connection);
    }

    /// <summary>Waits until a condition holds (polling), for state that changes on the session's context.</summary>
    public static async Task Eventually(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not hold in time.");
            await Task.Delay(10);
        }
    }

    public static byte[] RandomBytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
