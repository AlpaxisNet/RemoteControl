namespace RemoteControl.Protocol.Wire;

/// <summary>A frame that is compressed and known to fit, but not yet encrypted.</summary>
/// <param name="Type">The compression type, or 0 for a frame with no type byte.</param>
/// <param name="Data">The compressed data.</param>
internal readonly record struct PreparedFrame(byte Type, byte[] Data);

/// <summary>
/// Turns the plaintext of one frame (packets ending in newlines) into a frame and back, for one connection. Sending is split in two:
/// <see cref="Prepare"/> compresses and checks the size without touching the cipher, <see cref="Seal"/> encrypts and cannot fail. The
/// connection's single writer prepares every frame of a send before it seals the first, so a refused frame never leaves the cipher
/// out of step with the server.
/// </summary>
internal interface IFrameCodec
{
    /// <summary>Compresses one frame and checks that it fits.</summary>
    /// <param name="plaintext">The packets.</param>
    /// <param name="plain">Send it as a bare zlib stream with no type byte and no encryption (the login frame).</param>
    /// <exception cref="PacketTooLongException">The frame would not fit in what the server can read.</exception>
    PreparedFrame Prepare(byte[] plaintext, bool plain);

    /// <summary>Encrypts a prepared frame and adds the length prefix. Called in send order.</summary>
    byte[] Seal(PreparedFrame frame);

    /// <summary>Decodes the body of a received frame (the bytes after the length). Called by the connection's single reader.</summary>
    bool TryDecode(byte[] body, out byte[] plaintext);
}

/// <summary>Frame helpers shared by the codecs.</summary>
internal static class Frames
{
    /// <summary>
    /// The largest frame body a client sends. The length prefix is 16 bits, but the Alpaxis game server reads it as a signed short,
    /// so bodies of 32768 bytes or more would be misread.
    /// </summary>
    public const int MaxClientBody = 0x7FFF;

    /// <summary>
    /// The 16-bit length that announces an extended header: a frame whose body is 65,535 bytes or more is written as
    /// <c>FF FF</c> and a 4-byte big-endian length, and the body (which starts with the compression type byte) follows as usual.
    /// </summary>
    public const int ExtendedMarker = 0xFFFF;

    /// <summary>The largest frame body a server may send; a longer announced length is a protocol error and ends the connection.</summary>
    public const int MaxFrameBody = 8 * 1024 * 1024;

    /// <summary>
    /// The most plaintext a zlib frame may carry: the Alpaxis server inflates a zlib frame into a fixed 200 KiB buffer and silently
    /// drops the rest.
    /// </summary>
    public const int MaxZlibPlaintext = 204800;

    /// <summary>
    /// Prefixes a body with its length: big-endian 16 bits, or for a body of 65,535 bytes or more the extended header
    /// (<see cref="ExtendedMarker"/> and a 4-byte big-endian length).
    /// </summary>
    public static byte[] WithLength(ReadOnlySpan<byte> body, int maxBody = MaxClientBody)
    {
        maxBody = Math.Min(maxBody, MaxFrameBody);
        if (body.Length > maxBody)
            throw new PacketTooLongException($"A frame of {body.Length} bytes is larger than the {maxBody} bytes the server can read.");
        bool extended = body.Length >= ExtendedMarker;
        int headerSize = extended ? 6 : 2;
        byte[] frame = new byte[body.Length + headerSize];
        if (extended)
        {
            frame[0] = 0xFF;
            frame[1] = 0xFF;
            frame[2] = (byte)(body.Length >> 24);
            frame[3] = (byte)(body.Length >> 16);
            frame[4] = (byte)(body.Length >> 8);
            frame[5] = (byte)body.Length;
        }
        else
        {
            frame[0] = (byte)(body.Length >> 8);
            frame[1] = (byte)body.Length;
        }

        body.CopyTo(frame.AsSpan(headerSize));
        return frame;
    }

    /// <summary>
    /// Reads the length that follows an extended marker: four bytes, big-endian, unsigned.
    /// </summary>
    /// <param name="extension">The four bytes after the <c>FF FF</c> marker.</param>
    /// <returns>The announced body length, as a 64-bit number so that a value over two billion is not read as negative.</returns>
    public static long ReadExtendedLength(ReadOnlySpan<byte> extension) =>
        ((long)extension[0] << 24) | ((long)extension[1] << 16) | ((long)extension[2] << 8) | extension[3];

    /// <summary>Builds the plaintext of one packet: the id as a GByte, the payload and a newline.</summary>
    public static byte[] Packet(int id, ReadOnlySpan<byte> payload)
    {
        byte[] packet = new byte[payload.Length + 2];
        packet[0] = GEncoding.EncodeGByte(id);
        payload.CopyTo(packet.AsSpan(1));
        packet[^1] = (byte)'\n';
        return packet;
    }

    /// <summary>Prepares a bare zlib frame (no type byte, no encryption).</summary>
    public static PreparedFrame PreparePlainZlib(byte[] plaintext, int maxBody = MaxClientBody)
    {
        if (plaintext.Length > MaxZlibPlaintext)
            throw new PacketTooLongException($"A packet of {plaintext.Length} bytes is larger than the server can inflate; it was not sent.");
        byte[] data = FrameCompression.ZlibCompress(plaintext);
        if (data.Length > maxBody)
            throw new PacketTooLongException($"A frame of {data.Length} compressed bytes is larger than the {maxBody} bytes the server can read.");
        return new PreparedFrame(0, data);
    }

    /// <summary>Prepares a Gen5 frame body.</summary>
    public static PreparedFrame PrepareGen5(byte[] plaintext, int maxBody = MaxClientBody)
    {
        var choice = FrameCompression.ChooseForGameFrame(plaintext, maxBody - 1)
                     ?? throw new PacketTooLongException(
                         $"A packet of {plaintext.Length} bytes does not fit in one frame, even compressed; it was not sent.");
        if (choice.Type == FrameCompression.Zlib && plaintext.Length > MaxZlibPlaintext)
            throw new PacketTooLongException($"A packet of {plaintext.Length} bytes is larger than the server can inflate; it was not sent.");
        return new PreparedFrame(choice.Type, choice.Body);
    }

    /// <summary>Seals a frame: a plain frame gets only its length, a Gen5 frame its type byte and encryption.</summary>
    public static byte[] Seal(PreparedFrame frame, Gen5Cipher? cipher, int maxBody = MaxClientBody)
    {
        if (frame.Type == 0 || cipher is null) return WithLength(frame.Data, maxBody);
        byte[] body = new byte[frame.Data.Length + 1];
        body[0] = frame.Type;
        frame.Data.CopyTo(body, 1);
        cipher.Apply(body.AsSpan(1), frame.Type);
        return WithLength(body, maxBody);
    }

    /// <summary>Decodes a Gen5 frame body: type byte, decrypt, decompress. Unknown types are taken as uncompressed, as RClib does.</summary>
    public static bool TryDecodeGen5(byte[] body, Gen5Cipher cipher, out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();
        if (body.Length < 1) return false;
        byte type = body[0];
        byte[] data = body.AsSpan(1).ToArray();
        cipher.Apply(data, type);
        switch (type)
        {
            case FrameCompression.Zlib:
                return FrameCompression.TryZlibDecompress(data, out plaintext);
            case FrameCompression.Bzip2:
                return FrameCompression.TryBzip2Decompress(data, out plaintext);
            default:
                plaintext = data;
                return true;
        }
    }
}

/// <summary>
/// The client side of a game-server (RC) connection. The first frame (the login) is a zlib stream with no type byte and no
/// encryption; every later frame in both directions is Gen5 with the key the login announced.
/// </summary>
internal sealed class GameClientCodec : IFrameCodec
{
    private readonly Gen5Cipher _out;
    private readonly Gen5Cipher _in;

    public GameClientCodec(byte key)
    {
        _out = new Gen5Cipher(key);
        _in = new Gen5Cipher(key);
    }

    public PreparedFrame Prepare(byte[] plaintext, bool plain) => plain ? Frames.PreparePlainZlib(plaintext) : Frames.PrepareGen5(plaintext);

    public byte[] Seal(PreparedFrame frame) => Frames.Seal(frame, _out);

    public bool TryDecode(byte[] body, out byte[] plaintext)
    {
        if (!Frames.TryDecodeGen5(body, _in, out plaintext)) return false;
        // RClib: a decompressed frame that is itself a bzip2 stream is decompressed again.
        if (plaintext.Length > 3 && plaintext[0] == 'B' && plaintext[1] == 'Z' && plaintext[2] == 'h'
            && FrameCompression.TryBzip2Decompress(plaintext, out byte[] inner))
            plaintext = inner;
        return true;
    }
}

/// <summary>The NC connection: every frame, in both directions, is one complete zlib stream; there is no encryption.</summary>
internal sealed class NcCodec : IFrameCodec
{
    public PreparedFrame Prepare(byte[] plaintext, bool plain) => Frames.PreparePlainZlib(plaintext);

    public byte[] Seal(PreparedFrame frame) => Frames.Seal(frame, null);

    public bool TryDecode(byte[] body, out byte[] plaintext) => FrameCompression.TryZlibDecompress(body, out plaintext);
}

/// <summary>
/// The client side of a list-server connection: the first frame (the edition) is zlib with no type byte; later frames are Gen5 with
/// key 4, zlib when the payload is over 40 bytes (as RClib). The iterators persist from frame to frame (the hub resets them only at
/// the edition packet); RClib's scrambler restarted them for every frame, which gives the same bytes for the single frame it read.
/// </summary>
internal sealed class ListServerClientCodec : IFrameCodec
{
    public const byte Key = 4;

    private readonly Gen5Cipher _out = new(Key);
    private readonly Gen5Cipher _in = new(Key);

    public PreparedFrame Prepare(byte[] plaintext, bool plain)
    {
        if (plain) return Frames.PreparePlainZlib(plaintext);
        PreparedFrame frame = plaintext.Length > 40
            ? new PreparedFrame(FrameCompression.Zlib, FrameCompression.ZlibCompress(plaintext))
            : new PreparedFrame(FrameCompression.None, (byte[])plaintext.Clone());
        if (frame.Data.Length + 1 > Frames.MaxClientBody) throw new PacketTooLongException("The list-server packet is too large.");
        return frame;
    }

    public byte[] Seal(PreparedFrame frame) => Frames.Seal(frame, _out);

    public bool TryDecode(byte[] body, out byte[] plaintext) => Frames.TryDecodeGen5(body, _in, out plaintext);
}
