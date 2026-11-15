namespace RemoteControl.Protocol.Wire;

/// <summary>
/// The "G" integer encodings of the Graal protocol: each byte carries seven bits (eight for the first byte of a GShort) plus 32, so
/// that packets never contain a newline. The decoders accept any byte and may return negative values, exactly as the servers do.
/// </summary>
public static class GEncoding
{
    /// <summary>The largest value a GShort can carry without its high byte wrapping.</summary>
    public const int MaxGShort = 0x6FFF;

    /// <summary>The largest value a GInt3 can carry.</summary>
    public const int MaxGInt3 = (1 << 21) - 1;

    /// <summary>The longest text a one-byte length prefix can carry (223).</summary>
    public const int MaxGStringLength = 0xDF;

    /// <summary>Encodes a GByte: <c>(value &amp; 0xFF) + 32</c>, wrapping at 256.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The byte.</returns>
    public static byte EncodeGByte(int value) => unchecked((byte)((value & 0xFF) + 32));

    /// <summary>Decodes a GByte: <c>byte - 32</c>, negative for bytes below 32.</summary>
    /// <param name="value">The byte.</param>
    /// <returns>The value.</returns>
    public static int DecodeGByte(byte value) => value - 32;

    /// <summary>Encodes a GShort: <c>((v &gt;&gt; 7) &amp; 0xFF) + 32</c>, <c>(v &amp; 0x7F) + 32</c>.</summary>
    /// <param name="value">The value.</param>
    /// <param name="destination">At least two bytes.</param>
    public static void EncodeGShort(int value, Span<byte> destination)
    {
        destination[0] = unchecked((byte)(((value >> 7) & 0xFF) + 32));
        destination[1] = (byte)((value & 0x7F) + 32);
    }

    /// <summary>Decodes a GShort.</summary>
    /// <param name="source">At least two bytes.</param>
    /// <returns>The value.</returns>
    public static int DecodeGShort(ReadOnlySpan<byte> source) => ((source[0] - 32) << 7) + (source[1] - 32);

    /// <summary>Encodes a GInt3 (three groups of seven bits).</summary>
    /// <param name="value">The value.</param>
    /// <param name="destination">At least three bytes.</param>
    public static void EncodeGInt3(int value, Span<byte> destination)
    {
        destination[0] = (byte)(((value >> 14) & 0x7F) + 32);
        destination[1] = (byte)(((value >> 7) & 0x7F) + 32);
        destination[2] = (byte)((value & 0x7F) + 32);
    }

    /// <summary>Decodes a GInt3.</summary>
    /// <param name="source">At least three bytes.</param>
    /// <returns>The value.</returns>
    public static int DecodeGInt3(ReadOnlySpan<byte> source) => ((source[0] - 32) << 14) + ((source[1] - 32) << 7) + (source[2] - 32);

    /// <summary>Decodes a GInt4 (the world time): each byte is <c>(b - 32) &amp; 0xFF</c>, shifted by 21, 14, 7 and 0.</summary>
    /// <param name="source">At least four bytes.</param>
    /// <returns>The value.</returns>
    public static int DecodeGInt4(ReadOnlySpan<byte> source) =>
        (((source[0] - 32) & 0xFF) << 21) | (((source[1] - 32) & 0xFF) << 14) | (((source[2] - 32) & 0xFF) << 7) | ((source[3] - 32) & 0xFF);

    /// <summary>Encodes a GInt4.</summary>
    /// <param name="value">The value.</param>
    /// <param name="destination">At least four bytes.</param>
    public static void EncodeGInt4(int value, Span<byte> destination)
    {
        destination[0] = (byte)(((value >> 21) & 0x7F) + 32);
        destination[1] = (byte)(((value >> 14) & 0x7F) + 32);
        destination[2] = (byte)(((value >> 7) & 0x7F) + 32);
        destination[3] = (byte)((value & 0x7F) + 32);
    }

    /// <summary>Encodes a GInt5 (five groups of seven bits; the top group carries bits 28 to 31).</summary>
    /// <param name="value">The value.</param>
    /// <param name="destination">At least five bytes.</param>
    public static void EncodeGInt5(long value, Span<byte> destination)
    {
        destination[0] = (byte)(((value >> 28) & 0x7F) + 32);
        destination[1] = (byte)(((value >> 21) & 0x7F) + 32);
        destination[2] = (byte)(((value >> 14) & 0x7F) + 32);
        destination[3] = (byte)(((value >> 7) & 0x7F) + 32);
        destination[4] = (byte)((value & 0x7F) + 32);
    }

    /// <summary>Decodes a GInt5 as a signed 64-bit value (the sum of <c>(b - 32) &lt;&lt; shift</c>).</summary>
    /// <param name="source">At least five bytes.</param>
    /// <returns>The value.</returns>
    public static long DecodeGInt5(ReadOnlySpan<byte> source) =>
        ((long)(source[0] - 32) << 28) + ((long)(source[1] - 32) << 21) + ((source[2] - 32) << 14) + ((source[3] - 32) << 7) + (source[4] - 32);
}
