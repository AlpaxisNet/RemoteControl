using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RemoteControl.Core;

/// <summary>
/// The plaintext inside <c>accounts.dat</c> (<c>TRCAccounts.cpp:113-185</c>). Integers are little-endian, strings are a
/// <c>u32</c> length and bytes (UTF-8).
/// <code>
/// format 3: str "GScriptRCAccounts3", u64 selectedId, u32 count, count × (u64 id, str name, str password, u32 n, n × str server, u32 direct)
/// format 2: str "GScriptRCAccounts2", u64 selectedId, u32 count, count × (u64 id, str name, str password, u32 n, n × str server)
/// format 1: str selectedName,                     u32 count, count × (str name, str password, u32 n, n × str server)
/// </code>
/// A password is empty, a <c>PWG1</c> container, or (legacy) DES-ECB of <c>u32 length || bytes || padding</c>.
/// </summary>
internal static class AccountFileFormat
{
    public const string Format3 = "GScriptRCAccounts3";
    public const string Format2 = "GScriptRCAccounts2";
    public const int MaximumAccounts = 1000;
    public const int MaximumServers = 1000;
    public const string DirectPrefix = "direct://";

    private static readonly UTF8Encoding Utf8 = new(false, false);

    /// <summary>One account as stored in memory: the password stays encrypted (a <c>PWG1</c> container or empty).</summary>
    internal sealed class Entry
    {
        public ulong Id;
        public string Name = string.Empty;
        public byte[] EncodedPassword = Array.Empty<byte>();
        public string ListServer = string.Empty;
        public bool DirectMode;
    }

    /// <summary>The result of reading the plaintext.</summary>
    internal sealed class Parsed
    {
        public string SelectedName = string.Empty;
        public ulong SelectedId;
        public List<Entry> Entries = new();
        public bool Migrated;
        public int Format;
    }

    public static byte[] Serialize(ulong selectedId, IEnumerable<Entry> entries)
    {
        using var stream = new MemoryStream();
        WriteString(stream, Utf8.GetBytes(Format3));
        WriteUInt64(stream, selectedId);
        List<Entry> list = entries.ToList();
        WriteUInt32(stream, (uint)list.Count);
        foreach (Entry entry in list)
        {
            WriteUInt64(stream, entry.Id);
            WriteString(stream, Utf8.GetBytes(entry.Name));
            WriteString(stream, entry.EncodedPassword);
            if (entry.ListServer.Length == 0) WriteUInt32(stream, 0);
            else
            {
                WriteUInt32(stream, 1);
                WriteString(stream, Utf8.GetBytes(entry.ListServer));
            }
            WriteUInt32(stream, entry.DirectMode ? 1U : 0U);
        }
        byte[] result = stream.ToArray();
        CryptographicOperations.ZeroMemory(stream.GetBuffer());
        return result;
    }

    /// <summary>
    /// Reads the plaintext the way the C++ did: older formats get ids 1, 2, …; a <c>direct://</c> list server sets direct mode;
    /// only the first list server is kept; entries equal to an earlier one (name ignoring case, password, list server, mode)
    /// are merged; entries without a name are dropped; the input must be consumed exactly. Legacy passwords are re-encrypted
    /// as <c>PWG1</c>. Any of these changes sets <see cref="Parsed.Migrated"/>.
    /// </summary>
    /// <returns>The accounts, or <c>null</c> when the plaintext is malformed or a password does not decrypt.</returns>
    public static Parsed? Deserialize(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key)
    {
        var reader = new Reader(input);
        var parsed = new Parsed();
        var plaintexts = new List<byte[]>();
        byte[]? pending = null;
        try
        {
            if (!reader.String(out byte[] headerBytes)) return null;
            string header = Utf8.GetString(headerBytes);
            bool hasIds = header is Format3 or Format2;
            bool hasDirect = header == Format3;
            parsed.Format = hasDirect ? 3 : hasIds ? 2 : 1;
            if (hasIds)
            {
                if (!reader.UInt64(out parsed.SelectedId)) return null;
                if (!hasDirect) parsed.Migrated = true;
            }
            else
            {
                parsed.SelectedName = header;
                parsed.Migrated = true;
            }
            if (!reader.UInt32(out uint count) || count > MaximumAccounts) return null;
            ulong nextId = 1;
            for (uint accountIndex = 0; accountIndex < count; accountIndex++)
            {
                var entry = new Entry();
                if (hasIds)
                {
                    if (!reader.UInt64(out entry.Id) || entry.Id == 0) return null;
                    nextId = Math.Max(nextId, entry.Id == ulong.MaxValue ? entry.Id : entry.Id + 1);
                }
                else entry.Id = nextId++;
                if (!reader.String(out byte[] nameBytes) || !reader.String(out byte[] encodedPassword)) return null;
                entry.Name = Utf8.GetString(nameBytes);
                byte[]? password = DecodePassword(encodedPassword, key, out bool legacy);
                if (password is null) return null;
                pending = password;
                if (legacy) parsed.Migrated = true;
                if (!reader.UInt32(out uint serverCount) || serverCount > MaximumServers) return null;
                var servers = new List<string>();
                for (uint serverIndex = 0; serverIndex < serverCount; serverIndex++)
                {
                    if (!reader.String(out byte[] serverBytes)) return null;
                    string server = Utf8.GetString(serverBytes);
                    if (server.Length != 0 && !servers.Any(existing => string.Equals(existing, server, StringComparison.OrdinalIgnoreCase))) servers.Add(server);
                }
                if (hasDirect)
                {
                    if (!reader.UInt32(out uint direct)) return null;
                    entry.DirectMode = direct != 0;
                }
                if (servers.Count > 0 && servers[0].StartsWith(DirectPrefix, StringComparison.Ordinal))
                {
                    entry.DirectMode = true;
                    servers[0] = servers[0][DirectPrefix.Length..];
                    parsed.Migrated = true;
                }
                if (servers.Count > 1) parsed.Migrated = true;
                entry.ListServer = servers.Count > 0 ? servers[0] : string.Empty;
                if (entry.Name.Length == 0)
                {
                    CryptographicOperations.ZeroMemory(password);
                    pending = null;
                    continue;
                }
                int duplicate = FindDuplicate(parsed.Entries, plaintexts, entry, password);
                if (duplicate >= 0)
                {
                    if (parsed.SelectedId == entry.Id) parsed.SelectedId = parsed.Entries[duplicate].Id;
                    parsed.Migrated = true;
                    CryptographicOperations.ZeroMemory(password);
                    pending = null;
                    continue;
                }
                entry.EncodedPassword = legacy || !AccountCrypto.HasMagic(encodedPassword, AccountCrypto.PasswordMagic) ? EncodePassword(password, key) : encodedPassword;
                parsed.Entries.Add(entry);
                plaintexts.Add(password);
                pending = null;
            }
            if (!reader.AtEnd) return null;
            return parsed;
        }
        finally
        {
            foreach (byte[] plaintext in plaintexts) CryptographicOperations.ZeroMemory(plaintext);
            if (pending is not null) CryptographicOperations.ZeroMemory(pending);
        }
    }

    /// <summary>Encrypts a password as a <c>PWG1</c> container; an empty password stays empty.</summary>
    public static byte[] EncodePassword(ReadOnlySpan<byte> password, ReadOnlySpan<byte> key) =>
        password.IsEmpty ? Array.Empty<byte>() : AccountCrypto.Encrypt(password, key, AccountCrypto.PasswordMagic);

    /// <summary>Decrypts a stored password: empty, <c>PWG1</c>, or legacy DES. <c>null</c> when it does not decrypt.</summary>
    public static byte[]? DecodePassword(ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> key, out bool legacy)
    {
        legacy = false;
        if (encoded.IsEmpty) return Array.Empty<byte>();
        if (AccountCrypto.HasMagic(encoded, AccountCrypto.PasswordMagic)) return AccountCrypto.Decrypt(encoded, key, AccountCrypto.PasswordMagic);
        legacy = true;
        return DecodeLegacyPassword(encoded, key);
    }

    /// <summary>Decrypts a legacy DES password: <c>u32 length || bytes || padding</c> in whole 8-byte blocks.</summary>
    public static byte[]? DecodeLegacyPassword(ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> key)
    {
        if (encoded.Length < 8 || encoded.Length % 8 != 0) return null;
        byte[] plaintext = LegacyDes.DecryptEcb(key[..8], encoded);
        try
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(plaintext);
            if (length > (uint)(plaintext.Length - 4)) return null;
            return plaintext.AsSpan(4, (int)length).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Builds a legacy DES password (zero padding), as old versions stored it. Used by tests.</summary>
    public static byte[] EncodeLegacyPassword(ReadOnlySpan<byte> password, ReadOnlySpan<byte> key)
    {
        int length = 4 + password.Length;
        byte[] plaintext = new byte[(length + 7) / 8 * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(plaintext, (uint)password.Length);
        password.CopyTo(plaintext.AsSpan(4));
        byte[] encrypted = LegacyDes.EncryptEcb(key[..8], plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        return encrypted;
    }

    private static int FindDuplicate(List<Entry> entries, List<byte[]> plaintexts, Entry candidate, byte[] password)
    {
        for (int index = 0; index < entries.Count; index++)
        {
            Entry current = entries[index];
            if (string.Equals(current.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)
                && current.ListServer == candidate.ListServer
                && current.DirectMode == candidate.DirectMode
                && CryptographicOperations.FixedTimeEquals(plaintexts[index], password)) return index;
        }
        return -1;
    }

    public static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    public static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    public static void WriteString(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(value);
    }

    /// <summary>A bounds-checked little-endian reader.</summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _input;
        private int _offset;

        public Reader(ReadOnlySpan<byte> input)
        {
            _input = input;
            _offset = 0;
        }

        public readonly bool AtEnd => _offset == _input.Length;

        public bool UInt32(out uint value)
        {
            value = 0;
            if (_input.Length - _offset < 4) return false;
            value = BinaryPrimitives.ReadUInt32LittleEndian(_input.Slice(_offset, 4));
            _offset += 4;
            return true;
        }

        public bool UInt64(out ulong value)
        {
            value = 0;
            if (_input.Length - _offset < 8) return false;
            value = BinaryPrimitives.ReadUInt64LittleEndian(_input.Slice(_offset, 8));
            _offset += 8;
            return true;
        }

        public bool String(out byte[] value)
        {
            value = Array.Empty<byte>();
            if (!UInt32(out uint length)) return false;
            if (length > (uint)(_input.Length - _offset)) return false;
            value = _input.Slice(_offset, (int)length).ToArray();
            _offset += (int)length;
            return true;
        }
    }
}
