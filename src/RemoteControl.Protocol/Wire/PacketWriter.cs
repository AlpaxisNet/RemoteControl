namespace RemoteControl.Protocol.Wire;

/// <summary>
/// Builds the payload of one packet (the bytes after the packet id; the id and the closing newline are added when the packet is sent).
/// A text longer than its length prefix can carry is refused with <see cref="PacketTooLongException"/>, never truncated.
/// </summary>
public sealed class PacketWriter
{
    private byte[] _buffer;
    private int _length;

    /// <summary>Creates an empty writer.</summary>
    /// <param name="capacity">The initial capacity in bytes.</param>
    public PacketWriter(int capacity = 64) => _buffer = new byte[Math.Max(capacity, 16)];

    /// <summary>The number of bytes written.</summary>
    public int Length => _length;

    /// <summary>The bytes written so far.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    /// <summary>Copies the bytes written so far.</summary>
    /// <returns>A new array.</returns>
    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    /// <summary>Writes one raw byte.</summary>
    /// <param name="value">The byte.</param>
    /// <returns>This writer.</returns>
    public PacketWriter WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
        return this;
    }

    /// <summary>Writes raw bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>This writer.</returns>
    public PacketWriter WriteBytes(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
        return this;
    }

    /// <summary>Writes a GByte.</summary>
    /// <param name="value">The value.</param>
    /// <returns>This writer.</returns>
    public PacketWriter WriteGByte(int value) => WriteByte(GEncoding.EncodeGByte(value));

    /// <summary>Writes a GShort.</summary>
    /// <param name="value">The value.</param>
    /// <returns>This writer.</returns>
    public PacketWriter WriteGShort(int value)
    {
        Ensure(2);
        GEncoding.EncodeGShort(value, _buffer.AsSpan(_length));
        _length += 2;
        return this;
    }

    /// <summary>Writes a GInt3.</summary>
    /// <param name="value">The value.</param>
    /// <returns>This writer.</returns>
    public PacketWriter WriteGInt3(int value)
    {
        Ensure(3);
        GEncoding.EncodeGInt3(value, _buffer.AsSpan(_length));
        _length += 3;
        return this;
    }

    /// <summary>Writes a GInt4.</summary>
    /// <param name="value">The value.</param>
    /// <returns>This writer.</returns>
    public PacketWriter WriteGInt4(int value)
    {
        Ensure(4);
        GEncoding.EncodeGInt4(value, _buffer.AsSpan(_length));
        _length += 4;
        return this;
    }

    /// <summary>Writes a GInt5.</summary>
    /// <param name="value">The value.</param>
    /// <returns>This writer.</returns>
    public PacketWriter WriteGInt5(long value)
    {
        Ensure(5);
        GEncoding.EncodeGInt5(value, _buffer.AsSpan(_length));
        _length += 5;
        return this;
    }

    /// <summary>Writes text without a length (it runs to the end of the packet or to the next field the reader knows).</summary>
    /// <param name="text">The text.</param>
    /// <returns>This writer.</returns>
    public PacketWriter WriteText(string? text) => WriteBytes(RcText.GetBytes(text));

    /// <summary>Writes a GByte length (at most 223) and the text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>This writer.</returns>
    /// <exception cref="PacketTooLongException">The text is longer than 223 bytes.</exception>
    public PacketWriter WriteGString(string? text)
    {
        byte[] bytes = RcText.GetBytes(text);
        if (bytes.Length > GEncoding.MaxGStringLength)
            throw new PacketTooLongException($"A text of {bytes.Length} bytes is longer than the 223 bytes its length prefix can carry.");
        WriteGByte(bytes.Length);
        return WriteBytes(bytes);
    }

    /// <summary>Writes a GShort length (at most 0x6FFF) and the text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>This writer.</returns>
    /// <exception cref="PacketTooLongException">The text is longer than 0x6FFF bytes.</exception>
    public PacketWriter WriteGShortString(string? text)
    {
        byte[] bytes = RcText.GetBytes(text);
        if (bytes.Length > GEncoding.MaxGShort)
            throw new PacketTooLongException($"A text of {bytes.Length} bytes is longer than the {GEncoding.MaxGShort} bytes its length prefix can carry.");
        WriteGShort(bytes.Length);
        return WriteBytes(bytes);
    }

    private void Ensure(int extra)
    {
        if (_length + extra <= _buffer.Length) return;
        int size = Math.Max(_buffer.Length * 2, _length + extra);
        Array.Resize(ref _buffer, size);
    }
}

/// <summary>A value is too long for the field or the frame that should carry it; nothing was sent.</summary>
public sealed class PacketTooLongException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was too long.</param>
    public PacketTooLongException(string message) : base(message)
    {
    }
}
