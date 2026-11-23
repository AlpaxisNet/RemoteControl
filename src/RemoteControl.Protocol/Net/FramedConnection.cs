using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Net;

/// <summary>The outcome of one send: whether the bytes were written, and why not.</summary>
/// <param name="Success">Whether every frame was written.</param>
/// <param name="Error">Why not.</param>
internal readonly record struct SendOutcome(bool Success, string? Error)
{
    public static readonly SendOutcome Ok = new(true, null);
}

/// <summary>
/// One TCP connection that speaks frames: a reader task that decodes frames and splits them into packets, and a single writer task
/// that encodes and writes the frames in the order they were queued (so two callers can never interleave the bytes or the cipher
/// state of two frames). A send of several frames (a raw-data header and its block) is written without anything in between.
/// </summary>
internal sealed class FramedConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly IFrameCodec _codec;
    private readonly PacketSplitter _splitter;
    private readonly Channel<WriteItem> _writes = Channel.CreateUnbounded<WriteItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private int _closed;

    public FramedConnection(TcpClient client, IFrameCodec codec, int rawDataId = (int)ServerPacketId.RawData)
    {
        _splitter = new PacketSplitter(rawDataId);
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        _codec = codec;
        _splitter.Dropped = reason => Log?.Invoke(reason);
    }

    /// <summary>Called on the reader task for every complete packet.</summary>
    public Action<byte[]>? PacketReceived { get; set; }

    /// <summary>Called once, on whichever task notices it first, when the connection ends.</summary>
    public Action<string>? Closed { get; set; }

    /// <summary>Called with a line for the log.</summary>
    public Action<string>? Log { get; set; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Connects with a timeout; never blocks the calling thread.</summary>
    public static async Task<TcpClient> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
                ? new[] { literal }
                : (await Dns.GetHostAddressesAsync(host, linked.Token).ConfigureAwait(false))
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork).ToArray();
            if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
            await client.ConnectAsync(addresses, port, linked.Token).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Connecting to {host}:{port} timed out after {timeout.TotalSeconds:0.#} s.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Starts the reader and the writer.</summary>
    public void Start()
    {
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    /// <summary>Queues frames; each element of <paramref name="plaintexts"/> becomes one frame.</summary>
    /// <param name="plaintexts">The frames' plaintexts.</param>
    /// <param name="plainFirst">Send the first frame as a bare zlib stream (the login).</param>
    /// <param name="progress">Called on the writer task after each frame with the frames written so far.</param>
    public Task<SendOutcome> SendAsync(IReadOnlyList<byte[]> plaintexts, bool plainFirst = false, Action<int>? progress = null)
    {
        var item = new WriteItem(plaintexts, plainFirst, progress);
        if (IsClosed || !_writes.Writer.TryWrite(item)) return Task.FromResult(new SendOutcome(false, "The connection is closed."));
        return item.Completion.Task;
    }

    /// <summary>Closes the connection; <see cref="Closed"/> is called with the reason if it was not called before.</summary>
    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _writes.Writer.TryComplete();
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _client.Client.Shutdown(SocketShutdown.Both);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
        }

        _client.Dispose();
        while (_writes.Reader.TryRead(out WriteItem? pending)) pending.Completion.TrySetResult(new SendOutcome(false, "The connection is closed."));
        try
        {
            Closed?.Invoke(reason);
        }
        catch (Exception exception)
        {
            Log?.Invoke($"Close handler failed: {exception.Message}");
        }
    }

    public void Dispose()
    {
        Closed = null;
        PacketReceived = null;
        Close("Disposed");
    }

    private async Task ReadLoopAsync()
    {
        string reason = "Connection closed";
        byte[] header = new byte[2];
        byte[] extension = new byte[4];
        try
        {
            while (!IsClosed)
            {
                await _stream.ReadExactlyAsync(header, _stop.Token).ConfigureAwait(false);
                int length = (header[0] << 8) | header[1];
                if (length == Frames.ExtendedMarker)
                {
                    // A frame of 65,535 bytes or more: the marker is followed by a 4-byte length. Reading only two bytes here took the
                    // marker for a length of 65,535 and the rest of the frame for the next frames.
                    await _stream.ReadExactlyAsync(extension, _stop.Token).ConfigureAwait(false);
                    long announced = Frames.ReadExtendedLength(extension);
                    if (announced == 0 || announced > Frames.MaxFrameBody)
                    {
                        reason = $"Protocol error: a frame announced a length of {announced} bytes";
                        break;
                    }

                    length = (int)announced;
                }
                else if (length == 0)
                {
                    // Tolerated, unlike a zero after the extended marker: the log of an earlier session shows empty frames only right
                    // after a frame that could not be decoded, and nothing shows a server that sends them on purpose.
                    Log?.Invoke("An empty frame was ignored.");
                    continue;
                }

                byte[] body = new byte[length];
                await _stream.ReadExactlyAsync(body, _stop.Token).ConfigureAwait(false);
                if (!_codec.TryDecode(body, out byte[] plaintext))
                {
                    // Not skipped: the bytes after a frame that cannot be read cannot be trusted either, and decoded as packets they
                    // show windows and players that do not exist. The reason is kept short because it is shown to the user.
                    Log?.Invoke($"A frame of {length} bytes (type 0x{body[0]:X2}) could not be decoded; the connection is closed.");
                    reason = $"Protocol error: a frame of {length} bytes could not be decoded";
                    break;
                }

                foreach (byte[] packet in _splitter.Push(plaintext))
                {
                    try
                    {
                        PacketReceived?.Invoke(packet);
                    }
                    catch (Exception exception)
                    {
                        Log?.Invoke($"A packet handler failed: {exception.Message}");
                    }
                }
            }
        }
        catch (EndOfStreamException)
        {
            reason = "Connection closed";
        }
        catch (OperationCanceledException)
        {
            reason = "Connection closed";
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            reason = exception.InnerException is SocketException socket ? $"Connection lost: {socket.Message}" : $"Connection lost: {exception.Message}";
        }
        catch (Exception exception)
        {
            reason = $"Connection failed: {exception.Message}";
        }

        Close(reason);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (WriteItem item in _writes.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                var prepared = new List<PreparedFrame>(item.Plaintexts.Count);
                try
                {
                    for (int index = 0; index < item.Plaintexts.Count; index++)
                        prepared.Add(_codec.Prepare(item.Plaintexts[index], item.PlainFirst && index == 0));
                }
                catch (PacketTooLongException exception)
                {
                    item.Completion.TrySetResult(new SendOutcome(false, exception.Message));
                    continue;
                }

                try
                {
                    for (int index = 0; index < prepared.Count; index++)
                    {
                        byte[] frame = _codec.Seal(prepared[index]);
                        await _stream.WriteAsync(frame, _stop.Token).ConfigureAwait(false);
                        item.Progress?.Invoke(index + 1);
                    }

                    item.Completion.TrySetResult(SendOutcome.Ok);
                }
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    item.Completion.TrySetResult(new SendOutcome(false, "The connection is closed."));
                    Close($"Connection lost: {exception.Message}");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Close($"Connection failed: {exception.Message}");
        }
    }

    private sealed class WriteItem(IReadOnlyList<byte[]> plaintexts, bool plainFirst, Action<int>? progress)
    {
        public IReadOnlyList<byte[]> Plaintexts { get; } = plaintexts;

        public bool PlainFirst { get; } = plainFirst;

        public Action<int>? Progress { get; } = progress;

        public TaskCompletionSource<SendOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
