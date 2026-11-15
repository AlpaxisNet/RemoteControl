using System.Security.Cryptography;

namespace RemoteControl.Core;

/// <summary>
/// The AES-256-GCM container of <c>TEncryption.cpp</c>: <c>magic || nonce(12) || tag(16) || ciphertext</c>, no associated data,
/// a random nonce per encryption.
/// </summary>
internal static class AccountCrypto
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>The magic of <c>accounts.dat</c>.</summary>
    public static ReadOnlySpan<byte> FileMagic => "GSRCACC1"u8;

    /// <summary>The magic of one encrypted password inside the account list.</summary>
    public static ReadOnlySpan<byte> PasswordMagic => "PWG1"u8;

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> magic)
    {
        byte[] output = new byte[magic.Length + NonceSize + TagSize + plaintext.Length];
        magic.CopyTo(output);
        Span<byte> nonce = output.AsSpan(magic.Length, NonceSize);
        Span<byte> tag = output.AsSpan(magic.Length + NonceSize, TagSize);
        Span<byte> ciphertext = output.AsSpan(magic.Length + NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return output;
    }

    /// <summary>Decrypts a container; <c>null</c> when the magic is wrong, it is too short or the tag does not verify.</summary>
    public static byte[]? Decrypt(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key, ReadOnlySpan<byte> magic)
    {
        if (input.Length < magic.Length + NonceSize + TagSize || !input[..magic.Length].SequenceEqual(magic)) return null;
        ReadOnlySpan<byte> nonce = input.Slice(magic.Length, NonceSize);
        ReadOnlySpan<byte> tag = input.Slice(magic.Length + NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = input[(magic.Length + NonceSize + TagSize)..];
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return null;
        }
    }

    public static bool HasMagic(ReadOnlySpan<byte> input, ReadOnlySpan<byte> magic) => input.Length >= magic.Length && input[..magic.Length].SequenceEqual(magic);
}

/// <summary>
/// Single DES in ECB mode, used only to read the legacy passwords (<c>TRCAccounts.cpp:81-101</c>): key = the first 8 bytes of
/// <c>accounts.key</c> with the parity bits ignored (<c>DES_set_key_unchecked</c>). Implemented here rather than with
/// <see cref="DES"/> because OpenSSL 3 keeps single DES in its legacy provider, which is not always installed on Linux.
/// </summary>
internal static class LegacyDes
{
    private static readonly byte[] InitialPermutation =
    [
        58, 50, 42, 34, 26, 18, 10, 2, 60, 52, 44, 36, 28, 20, 12, 4,
        62, 54, 46, 38, 30, 22, 14, 6, 64, 56, 48, 40, 32, 24, 16, 8,
        57, 49, 41, 33, 25, 17, 9, 1, 59, 51, 43, 35, 27, 19, 11, 3,
        61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7,
    ];

    private static readonly byte[] FinalPermutation = Invert(InitialPermutation);

    private static readonly byte[] Expansion =
    [
        32, 1, 2, 3, 4, 5, 4, 5, 6, 7, 8, 9, 8, 9, 10, 11, 12, 13, 12, 13, 14, 15, 16, 17,
        16, 17, 18, 19, 20, 21, 20, 21, 22, 23, 24, 25, 24, 25, 26, 27, 28, 29, 28, 29, 30, 31, 32, 1,
    ];

    private static readonly byte[] RoundPermutation =
    [
        16, 7, 20, 21, 29, 12, 28, 17, 1, 15, 23, 26, 5, 18, 31, 10,
        2, 8, 24, 14, 32, 27, 3, 9, 19, 13, 30, 6, 22, 11, 4, 25,
    ];

    private static readonly byte[] KeyPermutation1 =
    [
        57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18,
        10, 2, 59, 51, 43, 35, 27, 19, 11, 3, 60, 52, 44, 36,
        63, 55, 47, 39, 31, 23, 15, 7, 62, 54, 46, 38, 30, 22,
        14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 28, 20, 12, 4,
    ];

    private static readonly byte[] KeyPermutation2 =
    [
        14, 17, 11, 24, 1, 5, 3, 28, 15, 6, 21, 10, 23, 19, 12, 4, 26, 8, 16, 7, 27, 20, 13, 2,
        41, 52, 31, 37, 47, 55, 30, 40, 51, 45, 33, 48, 44, 49, 39, 56, 34, 53, 46, 42, 50, 36, 29, 32,
    ];

    private static readonly byte[] Shifts = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];

    private static readonly byte[] SBoxes =
    [
        14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7,
        0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
        4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0,
        15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13,

        15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10,
        3, 13, 4, 7, 15, 2, 8, 14, 12, 0, 1, 10, 6, 9, 11, 5,
        0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15,
        13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9,

        10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8,
        13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
        13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7,
        1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12,

        7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15,
        13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
        10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4,
        3, 15, 0, 6, 10, 1, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14,

        2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9,
        14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
        4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14,
        11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3,

        12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11,
        10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
        9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6,
        4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13,

        4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1,
        13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
        1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2,
        6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12,

        13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7,
        1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
        7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8,
        2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11,
    ];

    /// <summary>Decrypts whole 8-byte blocks.</summary>
    public static byte[] DecryptEcb(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data) => Transform(key, data, decrypt: true);

    /// <summary>Encrypts whole 8-byte blocks (used by tests to build legacy files).</summary>
    public static byte[] EncryptEcb(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data) => Transform(key, data, decrypt: false);

    private static byte[] Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, bool decrypt)
    {
        if (key.Length < 8) throw new ArgumentException("A DES key has 8 bytes.", nameof(key));
        if (data.Length % 8 != 0) throw new ArgumentException("DES works on whole 8-byte blocks.", nameof(data));
        ulong[] subkeys = Subkeys(key[..8]);
        byte[] output = new byte[data.Length];
        for (int offset = 0; offset < data.Length; offset += 8)
        {
            ulong block = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(offset, 8), Block(block, subkeys, decrypt));
        }
        Array.Clear(subkeys);
        return output;
    }

    private static ulong Permute(ulong input, byte[] table, int inputBits)
    {
        ulong result = 0;
        foreach (byte position in table) result = (result << 1) | ((input >> (inputBits - position)) & 1UL);
        return result;
    }

    private static ulong[] Subkeys(ReadOnlySpan<byte> key)
    {
        ulong permuted = Permute(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(key), KeyPermutation1, 64);
        uint c = (uint)(permuted >> 28) & 0x0FFFFFFF;
        uint d = (uint)permuted & 0x0FFFFFFF;
        var subkeys = new ulong[16];
        for (int round = 0; round < 16; round++)
        {
            int shift = Shifts[round];
            c = ((c << shift) | (c >> (28 - shift))) & 0x0FFFFFFF;
            d = ((d << shift) | (d >> (28 - shift))) & 0x0FFFFFFF;
            subkeys[round] = Permute(((ulong)c << 28) | d, KeyPermutation2, 56);
        }
        return subkeys;
    }

    private static ulong Block(ulong block, ulong[] subkeys, bool decrypt)
    {
        ulong permuted = Permute(block, InitialPermutation, 64);
        uint left = (uint)(permuted >> 32);
        uint right = (uint)permuted;
        for (int round = 0; round < 16; round++)
        {
            uint next = left ^ Feistel(right, subkeys[decrypt ? 15 - round : round]);
            left = right;
            right = next;
        }
        return Permute(((ulong)right << 32) | left, FinalPermutation, 64);
    }

    private static uint Feistel(uint right, ulong subkey)
    {
        ulong expanded = Permute(right, Expansion, 32) ^ subkey;
        uint substituted = 0;
        for (int box = 0; box < 8; box++)
        {
            int six = (int)((expanded >> (42 - 6 * box)) & 0x3F);
            int row = ((six & 0x20) >> 4) | (six & 1);
            int column = (six >> 1) & 0x0F;
            substituted = (substituted << 4) | SBoxes[box * 64 + row * 16 + column];
        }
        return (uint)Permute(substituted, RoundPermutation, 32);
    }

    private static byte[] Invert(byte[] permutation)
    {
        var inverse = new byte[permutation.Length];
        for (int index = 0; index < permutation.Length; index++) inverse[permutation[index] - 1] = (byte)(index + 1);
        return inverse;
    }
}
