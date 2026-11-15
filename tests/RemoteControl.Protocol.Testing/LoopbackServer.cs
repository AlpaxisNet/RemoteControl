using System.Net;
using System.Net.Sockets;
using RemoteControl.Protocol.Net;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Testing;

/// <summary>A packet a fake server received.</summary>
/// <param name="Id">The packet id.</param>
/// <param name="Payload">The bytes after the id.</param>
public sealed record ReceivedPacket(int Id, byte[] Payload)
{
    /// <summary>The payload as text.</summary>
    public string Text => RcText.GetString(Payload);

    /// <summary>A reader over the payload.</summary>
    /// <returns>The reader.</returns>
    public PacketReader Reader() => new(Payload);

    /// <summary>The payload as comma text, un-tokenized.</summary>
    public string CommaText => Wire.CommaText.Untokenize(Text);
}

/// <summary>
/// A scripted server on <c>127.0.0.1</c> with an ephemeral port that speaks the real wire format. Every accepted connection is a
/// <see cref="FakeConnection"/> that records the packets it receives and can send any packet. Dispose it to stop listening and close
/// every connection.
/// </summary>
public abstract class LoopbackServer : IAsyncDisposable, IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<FakeConnection> _connections = new();
    private readonly object _sync = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Starts listening.</summary>
    protected LoopbackServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The host to connect to: <c>127.0.0.1</c>.</summary>
    public string Host => "127.0.0.1";

    /// <summary>The port.</summary>
    public int Port { get; }

    /// <summary>The default time a wait may take.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The connections accepted so far.</summary>
    public IReadOnlyList<FakeConnection> Connections
    {
        get
        {
            lock (_sync) return _connections.ToArray();
        }
    }

    /// <summary>Waits for the n-th connection (0 is the first).</summary>
    /// <param name="index">Which connection.</param>
    /// <param name="timeout">How long to wait; <see cref="DefaultTimeout"/> when <c>null</c>.</param>
    /// <returns>The connection.</returns>
    /// <exception cref="TimeoutException">No such connection came in time.</exception>
    public async Task<FakeConnection> WaitForConnectionAsync(int index = 0, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (_connections.Count > index) return _connections[index];
                changed = _changed.Task;
            }

            TimeSpan left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException($"No connection {index} within the time limit.");
            await Task.WhenAny(changed, Task.Delay(left)).ConfigureAwait(false);
        }
    }

    /// <summary>Creates the codec of a new connection.</summary>
    internal abstract IFrameCodec CreateCodec();

    /// <summary>Called on the connection's reader task for every packet, after it was recorded.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="packet">The packet.</param>
    protected virtual void OnPacket(FakeConnection connection, ReceivedPacket packet)
    {
    }

    /// <summary>Called when a connection was accepted, before it reads anything.</summary>
    /// <param name="connection">The connection.</param>
    protected virtual void OnConnected(FakeConnection connection)
    {
    }

    internal void Dispatch(FakeConnection connection, ReceivedPacket packet)
    {
        try
        {
            OnPacket(connection, packet);
        }
        catch (Exception exception)
        {
            connection.Errors.Add(exception);
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                var connection = new FakeConnection(this, client, CreateCodec());
                lock (_sync)
                {
                    _connections.Add(connection);
                    TaskCompletionSource changed = _changed;
                    _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    changed.TrySetResult();
                }

                OnConnected(connection);
                connection.Start();
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Stops listening and closes every connection.</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _listener.Stop();
        foreach (FakeConnection connection in Connections) connection.Close();
        GC.SuppressFinalize(this);
    }

    /// <summary>Stops listening and closes every connection.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>One connection of a fake server: what it received, and a way to send anything.</summary>
public sealed class FakeConnection
{
    private readonly LoopbackServer _server;
    private readonly FramedConnection _connection;
    private readonly List<ReceivedPacket> _received = new();
    private readonly HashSet<int> _taken = new();
    private readonly object _sync = new();
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal FakeConnection(LoopbackServer server, TcpClient client, IFrameCodec codec)
    {
        _server = server;
        Codec = codec;
        _connection = new FramedConnection(client, codec, (int)ClientPacketId.RawData);
        _connection.PacketReceived = OnPacket;
        _connection.Closed = reason =>
        {
            _closed.TrySetResult(reason);
            Signal();
        };
    }

    internal IFrameCodec Codec { get; }

    /// <summary>Exceptions thrown by the server's handlers for this connection (a test may assert it is empty).</summary>
    public List<Exception> Errors { get; } = new();

    /// <summary>Free state a scripted server keeps per connection.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, object> State { get; } = new(StringComparer.Ordinal);

    /// <summary>Every packet received, in order.</summary>
    public IReadOnlyList<ReceivedPacket> Received
    {
        get
        {
            lock (_sync) return _received.ToArray();
        }
    }

    /// <summary>Whether the connection has closed.</summary>
    public bool IsClosed => _closed.Task.IsCompleted;

    internal void Start() => _connection.Start();

    private void OnPacket(byte[] packet)
    {
        if (packet.Length == 0) return;
        var received = new ReceivedPacket((packet[0] - 32) & 0xFF, packet.AsSpan(1).ToArray());
        lock (_sync) _received.Add(received);
        _server.Dispatch(this, received);
        Signal();
    }

    private void Signal()
    {
        TaskCompletionSource changed;
        lock (_sync)
        {
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }

    /// <summary>Waits for the next packet with an id that no earlier wait returned.</summary>
    /// <param name="id">The packet id.</param>
    /// <param name="where">An extra condition.</param>
    /// <param name="timeout">How long to wait; the server's default when <c>null</c>.</param>
    /// <returns>The packet.</returns>
    /// <exception cref="TimeoutException">It did not come in time.</exception>
    public Task<ReceivedPacket> WaitForPacketAsync(ClientPacketId id, Func<ReceivedPacket, bool>? where = null, TimeSpan? timeout = null) =>
        WaitForPacketAsync((int)id, where, timeout);

    /// <summary>Waits for the next packet with an id that no earlier wait returned.</summary>
    /// <param name="id">The packet id.</param>
    /// <param name="where">An extra condition.</param>
    /// <param name="timeout">How long to wait; the server's default when <c>null</c>.</param>
    /// <returns>The packet.</returns>
    /// <exception cref="TimeoutException">It did not come in time.</exception>
    public async Task<ReceivedPacket> WaitForPacketAsync(int id, Func<ReceivedPacket, bool>? where = null, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? _server.DefaultTimeout);
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                for (int index = 0; index < _received.Count; index++)
                {
                    if (_taken.Contains(index) || _received[index].Id != id || (where is not null && !where(_received[index]))) continue;
                    _taken.Add(index);
                    return _received[index];
                }

                changed = _changed.Task;
            }

            TimeSpan left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException($"Packet {id} did not arrive in time.");
            await Task.WhenAny(changed, Task.Delay(left)).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until the client closes the connection.</summary>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>Whether it closed in time.</returns>
    public async Task<bool> WaitForCloseAsync(TimeSpan? timeout = null)
    {
        Task finished = await Task.WhenAny(_closed.Task, Task.Delay(timeout ?? _server.DefaultTimeout)).ConfigureAwait(false);
        return finished == _closed.Task;
    }

    /// <summary>Sends packets, each in its own frame, in order.</summary>
    /// <param name="packets">Id and payload of each packet.</param>
    /// <returns>Whether they were written.</returns>
    public async Task<bool> SendAsync(params (int Id, byte[] Payload)[] packets)
    {
        var outcome = await _connection.SendAsync(packets.Select(packet => Frames.Packet(packet.Id, packet.Payload)).ToArray()).ConfigureAwait(false);
        return outcome.Success;
    }

    /// <summary>Sends one packet.</summary>
    /// <param name="id">The id.</param>
    /// <param name="payload">The payload.</param>
    /// <returns>Whether it was written.</returns>
    public Task<bool> SendAsync(ServerPacketId id, byte[] payload) => SendAsync(((int)id, payload));

    /// <summary>Sends one packet.</summary>
    /// <param name="id">The id.</param>
    /// <param name="payload">The payload.</param>
    /// <returns>Whether it was written.</returns>
    public Task<bool> SendAsync(ServerPacketId id, PacketWriter payload) => SendAsync(((int)id, payload.ToArray()));

    /// <summary>Sends one packet whose payload is text.</summary>
    /// <param name="id">The id.</param>
    /// <param name="text">The payload.</param>
    /// <returns>Whether it was written.</returns>
    public Task<bool> SendTextAsync(ServerPacketId id, string text) => SendAsync(((int)id, RcText.GetBytes(text)));

    /// <summary>Sends several packets in one frame (as a server that batches).</summary>
    /// <param name="packets">Id and payload of each packet.</param>
    /// <returns>Whether they were written.</returns>
    public async Task<bool> SendInOneFrameAsync(params (int Id, byte[] Payload)[] packets)
    {
        byte[] plaintext = packets.SelectMany(packet => Frames.Packet(packet.Id, packet.Payload)).ToArray();
        var outcome = await _connection.SendAsync(new[] { plaintext }).ConfigureAwait(false);
        return outcome.Success;
    }

    /// <summary>Sends any bytes as the plaintext of one frame (for malformed input).</summary>
    /// <param name="plaintext">The plaintext.</param>
    /// <returns>Whether it was written.</returns>
    public async Task<bool> SendPlaintextFrameAsync(byte[] plaintext)
    {
        var outcome = await _connection.SendAsync(new[] { plaintext }).ConfigureAwait(false);
        return outcome.Success;
    }

    /// <summary>Sends a packet wrapped as raw data: PLO_RAWDATA with its length, then the packet (newline included) in the same frame.</summary>
    /// <param name="id">The inner packet's id.</param>
    /// <param name="payload">The inner packet's payload (it may contain newlines).</param>
    /// <returns>Whether it was written.</returns>
    public async Task<bool> SendRawDataAsync(ServerPacketId id, byte[] payload)
    {
        byte[] inner = Frames.Packet((int)id, payload);
        byte[] header = new byte[3];
        GEncoding.EncodeGInt3(inner.Length, header);
        byte[] plaintext = Frames.Packet((int)ServerPacketId.RawData, header).Concat(inner).ToArray();
        var outcome = await _connection.SendAsync(new[] { plaintext }).ConfigureAwait(false);
        return outcome.Success;
    }

    /// <summary>Closes the connection.</summary>
    public void Close() => _connection.Close("Closed by the test server");
}
