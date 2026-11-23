using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using RemoteControl.Protocol.Net;
using RemoteControl.Protocol.Testing;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Tests;

public sealed class FrameHeaderTests
{
    [Fact]
    public void A_body_below_65535_bytes_has_a_two_byte_header()
    {
        byte[] frame = Frames.WithLength(new byte[65534], Frames.MaxFrameBody);
        Assert.Equal(65534 + 2, frame.Length);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, frame[..2]);
    }

    [Fact]
    public void A_body_of_65535_bytes_has_the_extended_header()
    {
        byte[] frame = Frames.WithLength(new byte[65535], Frames.MaxFrameBody);
        Assert.Equal(65535 + 6, frame.Length);
        Assert.Equal(Convert.FromHexString("FFFF0000FFFF"), frame[..6]);
    }

    [Fact]
    public void The_length_of_a_real_server_frame_is_written_as_the_server_does()
    {
        // From the Alpaxis game server: FF FF 00 01 BE 15 announces a body of 114,197 bytes.
        byte[] frame = Frames.WithLength(new byte[114197], Frames.MaxFrameBody);
        Assert.Equal(Convert.FromHexString("FFFF0001BE15"), frame[..6]);
        Assert.Equal(114197L, Frames.ReadExtendedLength(frame.AsSpan(2, 4)));
    }

    [Fact]
    public void An_extended_length_is_unsigned()
    {
        Assert.Equal(4294967295L, Frames.ReadExtendedLength(Convert.FromHexString("FFFFFFFF")));
        Assert.Equal(0L, Frames.ReadExtendedLength(Convert.FromHexString("00000000")));
    }

    [Fact]
    public void A_client_still_refuses_a_body_over_32767_bytes()
    {
        Assert.Throws<PacketTooLongException>(() => Frames.WithLength(new byte[40000]));
    }
}

public sealed class FramedConnectionExtendedFrameTests
{
    private const byte Key = 0x56;

    private static async Task<(TcpClient Client, TcpClient Server)> ConnectPairAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        Task connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        TcpClient server = await listener.AcceptTcpClientAsync().Within();
        await connect.Within();
        return (client, server);
    }

    private static byte[] ServerFrame(byte[] plaintext, ServerGen5Codec codec) => codec.Seal(codec.Prepare(plaintext, false));

    private static ServerGen5Codec ServerCodec()
    {
        var codec = new ServerGen5Codec();
        codec.SetKey(Key);
        return codec;
    }

    /// <summary>Bytes that neither compress nor contain a newline, so that one packet of them needs a frame over 64 KiB.</summary>
    private static byte[] Incompressible(int length)
    {
        var random = new Random(11);
        byte[] bytes = new byte[length];
        for (int index = 0; index < length; index++) bytes[index] = (byte)random.Next(0x20, 0x100);
        return bytes;
    }

    private static (FramedConnection Reader, ConcurrentQueue<byte[]> Packets, Task<string> Closed) StartReader(TcpClient socket)
    {
        var reader = new FramedConnection(socket, new GameClientCodec(Key));
        var packets = new ConcurrentQueue<byte[]>();
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.PacketReceived = packet => packets.Enqueue(packet);
        reader.Closed = reason => closed.TrySetResult(reason);
        reader.Start();
        return (reader, packets, closed.Task);
    }

    private static async Task<byte[]> NextPacketAsync(ConcurrentQueue<byte[]> packets)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (packets.TryDequeue(out byte[]? packet)) return packet;
            await Task.Delay(10);
        }

        throw new TimeoutException("No packet arrived.");
    }

    [Fact]
    public async Task A_frame_over_64_KiB_with_the_extended_header_is_read_whole()
    {
        (TcpClient clientSocket, TcpClient serverSocket) = await ConnectPairAsync();
        using TcpClient serverGuard = serverSocket;
        (FramedConnection reader, ConcurrentQueue<byte[]> packets, _) = StartReader(clientSocket);
        using FramedConnection _reader = reader;

        byte[] payload = Incompressible(130_000);
        byte[] frame = ServerFrame(Frames.Packet(5, payload), ServerCodec());
        Assert.Equal(new byte[] { 0xFF, 0xFF }, frame[..2]);
        Assert.Equal((long)(frame.Length - 6), Frames.ReadExtendedLength(frame.AsSpan(2, 4)));
        await serverSocket.GetStream().WriteAsync(frame).AsTask().Within();

        byte[] packet = await NextPacketAsync(packets);
        Assert.Equal(GEncoding.EncodeGByte(5), packet[0]);
        Assert.Equal(payload, packet[1..]);
    }

    [Fact]
    public async Task The_frames_after_an_extended_frame_are_read_in_step()
    {
        (TcpClient clientSocket, TcpClient serverSocket) = await ConnectPairAsync();
        using TcpClient serverGuard = serverSocket;
        (FramedConnection reader, ConcurrentQueue<byte[]> packets, Task<string> closed) = StartReader(clientSocket);
        using FramedConnection _reader = reader;

        ServerGen5Codec codec = ServerCodec();
        byte[] big = Incompressible(130_000);
        byte[] first = ServerFrame(Frames.Packet(5, big), codec);
        byte[] second = ServerFrame(Frames.Packet(6, "after"u8), codec);
        byte[] third = ServerFrame(Frames.Packet(7, Incompressible(90_000)), codec);
        await serverSocket.GetStream().WriteAsync(first).AsTask().Within();
        await serverSocket.GetStream().WriteAsync(second).AsTask().Within();
        await serverSocket.GetStream().WriteAsync(third).AsTask().Within();

        Assert.Equal(GEncoding.EncodeGByte(5), (await NextPacketAsync(packets))[0]);
        byte[] small = await NextPacketAsync(packets);
        Assert.Equal(GEncoding.EncodeGByte(6), small[0]);
        Assert.Equal("after"u8.ToArray(), small[1..]);
        Assert.Equal(GEncoding.EncodeGByte(7), (await NextPacketAsync(packets))[0]);
        Assert.False(closed.IsCompleted);
    }

    [Theory]
    [InlineData("FFFF00000000", "a length of 0 bytes")]
    [InlineData("FFFF00800001", "a length of 8388609 bytes")]
    [InlineData("FFFFFFFFFFFF", "a length of 4294967295 bytes")]
    public async Task An_extended_header_with_an_impossible_length_ends_the_connection(string header, string expected)
    {
        (TcpClient clientSocket, TcpClient serverSocket) = await ConnectPairAsync();
        using TcpClient serverGuard = serverSocket;
        (FramedConnection reader, ConcurrentQueue<byte[]> packets, Task<string> closed) = StartReader(clientSocket);
        using FramedConnection _reader = reader;

        await serverSocket.GetStream().WriteAsync(Convert.FromHexString(header)).AsTask().Within();

        string reason = await closed.Within();
        Assert.StartsWith("Protocol error", reason);
        Assert.Contains(expected, reason);
        Assert.Empty(packets);
    }

    [Fact]
    public async Task A_frame_that_cannot_be_decoded_ends_the_connection_instead_of_being_skipped()
    {
        (TcpClient clientSocket, TcpClient serverSocket) = await ConnectPairAsync();
        using TcpClient serverGuard = serverSocket;
        (FramedConnection reader, ConcurrentQueue<byte[]> packets, Task<string> closed) = StartReader(clientSocket);
        using FramedConnection _reader = reader;

        // A well-formed length, then a body that claims to be zlib and is not: the next bytes on the stream must not be trusted.
        byte[] body = new byte[20];
        body[0] = FrameCompression.Zlib;
        new Random(3).NextBytes(body.AsSpan(1));
        byte[] frame = [0x00, 0x14, .. body];
        ServerGen5Codec codec = ServerCodec();
        byte[] good = ServerFrame(Frames.Packet(5, "later"u8), codec);
        await serverSocket.GetStream().WriteAsync(frame.Concat(good).ToArray()).AsTask().Within();

        string reason = await closed.Within();
        Assert.Equal("Protocol error: a frame of 20 bytes could not be decoded", reason);
        Assert.Empty(packets);
    }

    [Fact]
    public async Task A_frame_written_by_the_real_write_loop_with_the_extended_header_is_read_whole()
    {
        (TcpClient clientSocket, TcpClient serverSocket) = await ConnectPairAsync();
        (FramedConnection reader, ConcurrentQueue<byte[]> packets, Task<string> closed) = StartReader(clientSocket);
        using FramedConnection readerGuard = reader;
        using var writer = new FramedConnection(serverSocket, ServerCodec(), (int)ClientPacketId.RawData);
        writer.Start();

        byte[] big = Incompressible(130_000);
        SendOutcome outcome = await writer.SendAsync([Frames.Packet(5, big), Frames.Packet(6, "after"u8)]).Within();
        Assert.True(outcome.Success, outcome.Error);

        byte[] first = await NextPacketAsync(packets);
        Assert.Equal(GEncoding.EncodeGByte(5), first[0]);
        Assert.Equal(big, first[1..]);
        byte[] second = await NextPacketAsync(packets);
        Assert.Equal("after"u8.ToArray(), second[1..]);
        Assert.False(closed.IsCompleted);
    }

    [Fact]
    public async Task An_empty_two_byte_frame_is_still_ignored()
    {
        (TcpClient clientSocket, TcpClient serverSocket) = await ConnectPairAsync();
        using TcpClient serverGuard = serverSocket;
        (FramedConnection reader, ConcurrentQueue<byte[]> packets, Task<string> closed) = StartReader(clientSocket);
        using FramedConnection readerGuard = reader;

        byte[] good = ServerFrame(Frames.Packet(5, "ok"u8), ServerCodec());
        await serverSocket.GetStream().WriteAsync(new byte[] { 0x00, 0x00 }.Concat(good).ToArray()).AsTask().Within();

        byte[] packet = await NextPacketAsync(packets);
        Assert.Equal("ok"u8.ToArray(), packet[1..]);
        Assert.False(closed.IsCompleted);
    }

    [Fact]
    public async Task A_small_frame_keeps_the_two_byte_header()
    {
        (TcpClient clientSocket, TcpClient serverSocket) = await ConnectPairAsync();
        using TcpClient serverGuard = serverSocket;
        (FramedConnection reader, ConcurrentQueue<byte[]> packets, _) = StartReader(clientSocket);
        using FramedConnection _reader = reader;

        byte[] frame = ServerFrame(Frames.Packet(5, "hello"u8), ServerCodec());
        Assert.NotEqual(0xFF, frame[0]);
        await serverSocket.GetStream().WriteAsync(frame).AsTask().Within();

        byte[] packet = await NextPacketAsync(packets);
        Assert.Equal("hello"u8.ToArray(), packet[1..]);
    }
}
