namespace RemoteControl.Protocol.Wire;

/// <summary>
/// The "generation 5" scrambler of the game and list servers, for one direction. An iterator starts at 0x4A80B38 and, for every
/// dword processed, becomes <c>iterator * 0x8088405 + key</c>; the dword is XORed with it (low byte first). Only a prefix of each
/// frame is processed: 12 dwords for an uncompressed frame (type 0x02), 4 dwords for a compressed one (0x04, 0x06). The iterator
/// persists from one frame to the next. Encrypting and decrypting are the same operation.
/// </summary>
internal sealed class Gen5Cipher
{
    /// <summary>The iterator's start value.</summary>
    public const uint StartIterator = 0x4A80B38;

    private const uint Multiplier = 0x8088405;

    private uint _iterator = StartIterator;

    /// <summary>Creates a cipher for one direction.</summary>
    /// <param name="key">The key (the login's key byte minus 32).</param>
    public Gen5Cipher(byte key) => Key = key;

    /// <summary>The key.</summary>
    public byte Key { get; }

    /// <summary>The current iterator, for tests.</summary>
    public uint Iterator => _iterator;

    /// <summary>Processes the body of one frame in place.</summary>
    /// <param name="body">The bytes after the compression-type byte.</param>
    /// <param name="compressionType">The frame's compression type.</param>
    public void Apply(Span<byte> body, byte compressionType)
    {
        int limit = compressionType == FrameCompression.None ? 12 : 4;
        for (int i = 0; i < body.Length; i++)
        {
            if (i % 4 == 0)
            {
                if (limit <= 0) break;
                limit--;
                _iterator = unchecked(_iterator * Multiplier + Key);
            }

            body[i] ^= (byte)(_iterator >> (8 * (i % 4)));
        }
    }
}
