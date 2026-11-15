using System.Text;

namespace RemoteControl.Protocol.Wire;

/// <summary>
/// Converts between the program's strings and the single bytes (Windows-1252) the servers speak. Every one of the 256 byte values
/// round-trips: the five bytes Windows-1252 leaves undefined (0x81, 0x8D, 0x8F, 0x90, 0x9D) become the control characters of the same
/// number. A character that has no byte is sent as <c>?</c>.
/// </summary>
public static class RcText
{
    private static readonly char[] ByteToChar = new char[256];
    private static readonly Dictionary<char, byte> CharToByte = new();

    static RcText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding windows1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        for (int value = 0; value < 256; value++)
        {
            char character = (char)value;
            if (value is >= 0x80 and < 0xA0)
            {
                try
                {
                    string decoded = windows1252.GetString(new[] { (byte)value });
                    if (decoded.Length == 1) character = decoded[0];
                }
                catch (DecoderFallbackException)
                {
                    // Undefined in Windows-1252: keep the control character of the same number.
                }
            }

            ByteToChar[value] = character;
            CharToByte[character] = (byte)value;
        }
    }

    /// <summary>Decodes server bytes into a string.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The text.</returns>
    public static string GetString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        return string.Create(bytes.Length, bytes.ToArray(), static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++) span[i] = ByteToChar[source[i]];
        });
    }

    /// <summary>Encodes a string into the bytes the server expects.</summary>
    /// <param name="text">The text; <c>null</c> is treated as empty.</param>
    /// <returns>One byte per character.</returns>
    public static byte[] GetBytes(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<byte>();
        byte[] result = new byte[text.Length];
        for (int i = 0; i < text.Length; i++) result[i] = GetByte(text[i]);
        return result;
    }

    /// <summary>Encodes one character.</summary>
    /// <param name="character">The character.</param>
    /// <returns>Its byte, or <c>?</c> when it has none.</returns>
    public static byte GetByte(char character) => CharToByte.TryGetValue(character, out byte value) ? value : (byte)'?';

    /// <summary>Decodes one byte.</summary>
    /// <param name="value">The byte.</param>
    /// <returns>Its character.</returns>
    public static char GetChar(byte value) => ByteToChar[value];

    /// <summary>The number of bytes a string takes on the wire (always its length: one byte per character).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The byte count.</returns>
    public static int ByteCount(string? text) => text?.Length ?? 0;
}
