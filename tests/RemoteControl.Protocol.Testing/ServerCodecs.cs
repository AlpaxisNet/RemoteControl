using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Testing;

/// <summary>
/// The server side of a game or list-server connection: the client's first frame is a bare zlib stream; once the handler has read
/// the key from it (<see cref="SetKey"/>), every frame in both directions is Gen5 with that key.
/// </summary>
internal sealed class ServerGen5Codec : IFrameCodec
{
    private Gen5Cipher? _in;
    private Gen5Cipher? _out;

    /// <summary>Installs the key the client announced; called on the reader task while the first packet is handled.</summary>
    public void SetKey(byte key)
    {
        _in = new Gen5Cipher(key);
        _out = new Gen5Cipher(key);
    }

    public bool HasKey => _in is not null;

    public PreparedFrame Prepare(byte[] plaintext, bool plain)
    {
        if (plain || _out is null) return new PreparedFrame(0, FrameCompression.ZlibCompress(plaintext));
        // As the Alpaxis server: bzip2 over 0x2000 bytes, zlib over 55, else uncompressed.
        if (plaintext.Length > 0x2000) return new PreparedFrame(FrameCompression.Bzip2, FrameCompression.Bzip2Compress(plaintext));
        if (plaintext.Length > 55) return new PreparedFrame(FrameCompression.Zlib, FrameCompression.ZlibCompress(plaintext));
        return new PreparedFrame(FrameCompression.None, (byte[])plaintext.Clone());
    }

    public byte[] Seal(PreparedFrame frame) => Frames.Seal(frame, frame.Type == 0 ? null : _out, maxBody: 0xFFFF);

    public bool TryDecode(byte[] body, out byte[] plaintext)
    {
        if (_in is null) return FrameCompression.TryZlibDecompress(body, out plaintext);
        return Frames.TryDecodeGen5(body, _in, out plaintext);
    }
}

/// <summary>The server side of an NC connection: zlib frames, no encryption.</summary>
internal sealed class ServerNcCodec : IFrameCodec
{
    public PreparedFrame Prepare(byte[] plaintext, bool plain) => new(0, FrameCompression.ZlibCompress(plaintext));

    public byte[] Seal(PreparedFrame frame) => Frames.Seal(frame, null, maxBody: 0xFFFF);

    public bool TryDecode(byte[] body, out byte[] plaintext) => FrameCompression.TryZlibDecompress(body, out plaintext);
}
