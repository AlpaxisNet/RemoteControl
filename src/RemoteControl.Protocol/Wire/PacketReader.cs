namespace RemoteControl.Protocol.Wire;

/// <summary>
/// Reads the payload of one received packet with every length checked. No method throws: a <c>TryRead</c> method that fails returns
/// <c>false</c> and leaves the position where it was, so a packet that is too short or has a negative length is simply rejected.
/// </summary>
public sealed class PacketReader
{
    private readonly byte[] _data;
    private readonly int _end;

    /// <summary>Creates a reader over a payload.</summary>
    /// <param name="data">The bytes.</param>
    public PacketReader(byte[] data) : this(data, 0, data.Length)
    {
    }

    /// <summary>Creates a reader over part of a buffer.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="offset">The first byte to read.</param>
    /// <param name="count">The number of bytes.</param>
    public PacketReader(byte[] data, int offset, int count)
    {
        _data = data;
        Position = Math.Clamp(offset, 0, data.Length);
        _end = Math.Clamp(offset + Math.Max(count, 0), Position, data.Length);
    }

    /// <summary>The index of the next byte.</summary>
    public int Position { get; private set; }

    /// <summary>The number of bytes left.</summary>
    public int Remaining => _end - Position;

    /// <summary>Whether every byte has been read.</summary>
    public bool AtEnd => Position >= _end;

    /// <summary>Looks at the next byte without reading it.</summary>
    /// <param name="value">The byte.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryPeekByte(out byte value)
    {
        value = AtEnd ? (byte)0 : _data[Position];
        return !AtEnd;
    }

    /// <summary>Reads one raw byte.</summary>
    /// <param name="value">The byte.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryReadByte(out byte value)
    {
        if (!TryPeekByte(out value)) return false;
        Position++;
        return true;
    }

    /// <summary>Reads a GByte (may be negative).</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryReadGByte(out int value)
    {
        value = 0;
        if (!TryReadByte(out byte raw)) return false;
        value = GEncoding.DecodeGByte(raw);
        return true;
    }

    /// <summary>Reads a GShort.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether there were two bytes.</returns>
    public bool TryReadGShort(out int value)
    {
        value = 0;
        if (Remaining < 2) return false;
        value = GEncoding.DecodeGShort(_data.AsSpan(Position, 2));
        Position += 2;
        return true;
    }

    /// <summary>Reads a GInt3.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether there were three bytes.</returns>
    public bool TryReadGInt3(out int value)
    {
        value = 0;
        if (Remaining < 3) return false;
        value = GEncoding.DecodeGInt3(_data.AsSpan(Position, 3));
        Position += 3;
        return true;
    }

    /// <summary>Reads a GInt4.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether there were four bytes.</returns>
    public bool TryReadGInt4(out int value)
    {
        value = 0;
        if (Remaining < 4) return false;
        value = GEncoding.DecodeGInt4(_data.AsSpan(Position, 4));
        Position += 4;
        return true;
    }

    /// <summary>Reads a GInt5.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether there were five bytes.</returns>
    public bool TryReadGInt5(out long value)
    {
        value = 0;
        if (Remaining < 5) return false;
        value = GEncoding.DecodeGInt5(_data.AsSpan(Position, 5));
        Position += 5;
        return true;
    }

    /// <summary>Reads raw bytes.</summary>
    /// <param name="count">How many.</param>
    /// <param name="bytes">The bytes.</param>
    /// <returns>Whether there were enough and <paramref name="count"/> was not negative.</returns>
    public bool TryReadBytes(int count, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (count < 0 || count > Remaining) return false;
        bytes = _data.AsSpan(Position, count).ToArray();
        Position += count;
        return true;
    }

    /// <summary>Reads text of a known byte length.</summary>
    /// <param name="count">The length.</param>
    /// <param name="text">The text.</param>
    /// <returns>Whether there were enough bytes and the length was not negative.</returns>
    public bool TryReadText(int count, out string text)
    {
        text = string.Empty;
        if (count < 0 || count > Remaining) return false;
        text = RcText.GetString(_data.AsSpan(Position, count));
        Position += count;
        return true;
    }

    /// <summary>Reads a GByte length and that many bytes of text. Fails without moving when the length is negative or too long.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether it could be read.</returns>
    public bool TryReadGString(out string text)
    {
        text = string.Empty;
        int start = Position;
        if (!TryReadGByte(out int length) || !TryReadText(length, out text))
        {
            Position = start;
            return false;
        }

        return true;
    }

    /// <summary>Reads a GByte length and that many raw bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>Whether they could be read.</returns>
    public bool TryReadGStringBytes(out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        int start = Position;
        if (!TryReadGByte(out int length) || !TryReadBytes(length, out bytes))
        {
            Position = start;
            return false;
        }

        return true;
    }

    /// <summary>Reads a GString the way RClib's <c>readLengthString</c> does: <c>""</c> when it does not fit (the length byte is still consumed).</summary>
    /// <returns>The text or <c>""</c>.</returns>
    public string ReadGStringOrEmpty()
    {
        if (!TryReadGByte(out int length)) return string.Empty;
        return TryReadText(length, out string text) ? text : string.Empty;
    }

    /// <summary>Reads the rest of the payload as text.</summary>
    /// <returns>The text.</returns>
    public string ReadRemainingText()
    {
        string text = RcText.GetString(_data.AsSpan(Position, Remaining));
        Position = _end;
        return text;
    }

    /// <summary>Reads the rest of the payload as bytes.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ReadRemainingBytes()
    {
        byte[] bytes = _data.AsSpan(Position, Remaining).ToArray();
        Position = _end;
        return bytes;
    }

    /// <summary>Reads the rest of the payload as comma text and un-tokenizes it.</summary>
    /// <returns>The lines joined with <c>\n</c>.</returns>
    public string ReadRemainingCommaText() => CommaText.Untokenize(ReadRemainingText());

    /// <summary>Skips bytes.</summary>
    /// <param name="count">How many.</param>
    /// <returns>Whether there were enough.</returns>
    public bool TrySkip(int count)
    {
        if (count < 0 || count > Remaining) return false;
        Position += count;
        return true;
    }

    /// <summary>Moves the position back to an earlier value, for a parse that has to try another layout.</summary>
    /// <param name="position">A value of <see cref="Position"/> read before.</param>
    public void Rewind(int position) => Position = Math.Clamp(position, 0, _end);
}
