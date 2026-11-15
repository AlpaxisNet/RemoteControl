using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RemoteControl.Core.Tests;

public sealed class AccountStoreTests
{
    private static AccountStore Open(string directory)
    {
        var store = new AccountStore(directory);
        store.Load();
        return store;
    }

    // Ported from tests/RCAccountsTests.cpp, first block.
    [Fact]
    public void Save_associate_update_and_reload_like_the_cpp_test()
    {
        using var folder = new TempFolder();
        {
            AccountStore accounts = Open(folder.Path);
            Assert.Equal(AccountSaveStatus.Saved, accounts.Save("Deny", "portable-secret", false, "login-one.example:14922"));
            accounts.Associate("Deny", "login-two.example:14922");
            accounts.Update("Deny", "Deny", "portable-secret", false, "login-one.example:14922");
            accounts.Save("Test", "second-secret", false, "login-two.example:14922");
            Assert.Equal(2, accounts.Accounts.Count);
        }
        byte[] firstCiphertext = File.ReadAllBytes(Path.Combine(folder.Path, "accounts.dat"));
        {
            AccountStore accounts = Open(folder.Path);
            Assert.Equal(AccountLoadStatus.Loaded, accounts.LastLoad.Status);
            Assert.Equal("Test", accounts.ActiveName);
            Assert.Equal("portable-secret", accounts.PasswordFor("Deny"));
            Assert.Equal("login-one.example:14922", accounts.ListServerFor("Deny"));
            string bytes = Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(folder.Path, "accounts.dat")));
            Assert.DoesNotContain("portable-secret", bytes);
            Assert.DoesNotContain("second-secret", bytes);
            Assert.DoesNotContain("Deny", bytes);
            accounts.Update("Test", "Test", "second-secret", false, "login-two.example:14922");
            byte[] secondCiphertext = File.ReadAllBytes(Path.Combine(folder.Path, "accounts.dat"));
            Assert.NotEqual(firstCiphertext, secondCiphertext);
            Assert.Equal(firstCiphertext.Length, secondCiphertext.Length);
            accounts.Save("Deny", "must-not-persist", true, "login-one.example:14922");
        }
        {
            AccountStore accounts = Open(folder.Path);
            Assert.Equal("Deny", accounts.ActiveName);
            Assert.Empty(accounts.PasswordFor("Deny"));
            Assert.False(accounts.Accounts[accounts.IndexOfName("Deny")].HasPassword);
            Assert.Equal("login-one.example:14922", accounts.ListServerFor("Deny"));
            accounts.Remove("Test");
            Assert.Single(accounts.Accounts);
        }
        Assert.Single(Open(folder.Path).Accounts);
    }

    // Ported from tests/RCAccountsTests.cpp, tamper block, with defect D-12 fixed.
    [Fact]
    public void Tampered_data_is_reported_kept_and_never_overwritten()
    {
        using var folder = new TempFolder();
        Open(folder.Path).Save("Tamper", "authenticated-secret", false, "Retail — listserver.graalonline.com:14922");
        string data = Path.Combine(folder.Path, "accounts.dat");
        string key = Path.Combine(folder.Path, "accounts.key");
        byte[] tampered = File.ReadAllBytes(data);
        tampered[^1] ^= 0x5a;
        File.WriteAllBytes(data, tampered);
        byte[] keyBytes = File.ReadAllBytes(key);

        AccountStore accounts = Open(folder.Path);

        Assert.Equal(AccountLoadStatus.DataUnreadable, accounts.LastLoad.Status);
        Assert.True(accounts.LastLoad.NeedsAttention);
        Assert.False(accounts.CanSave);
        Assert.Empty(accounts.Accounts);
        Assert.Empty(accounts.PasswordFor("Tamper"));
        Assert.Equal(AccountSaveStatus.Refused, accounts.Save("New", "pw", false));
        Assert.Equal(AccountSaveStatus.Refused, accounts.Associate("Tamper", "x:1"));
        Assert.Equal(AccountSaveStatus.Refused, accounts.Remove("Tamper"));
        Assert.Empty(accounts.Accounts);
        Assert.Equal(tampered, File.ReadAllBytes(data));
        Assert.Equal(keyBytes, File.ReadAllBytes(key));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void A_key_of_the_wrong_size_is_never_replaced(int size)
    {
        using var folder = new TempFolder();
        Open(folder.Path).Save("Keep", "secret", false);
        string data = Path.Combine(folder.Path, "accounts.dat");
        string key = Path.Combine(folder.Path, "accounts.key");
        byte[] dataBytes = File.ReadAllBytes(data);
        byte[] badKey = File.ReadAllBytes(key).Concat(new byte[8]).Take(size).ToArray();
        File.WriteAllBytes(key, badKey);

        AccountStore accounts = Open(folder.Path);

        Assert.Equal(AccountLoadStatus.KeyInvalid, accounts.LastLoad.Status);
        Assert.False(accounts.CanSave);
        Assert.Equal(AccountSaveStatus.Refused, accounts.Save("Other", "pw", false));
        Assert.Equal(badKey, File.ReadAllBytes(key));
        Assert.Equal(dataBytes, File.ReadAllBytes(data));
    }

    [Fact]
    public void A_wrong_size_key_without_data_still_blocks_until_the_user_decides()
    {
        using var folder = new TempFolder();
        File.WriteAllBytes(Path.Combine(folder.Path, "accounts.key"), new byte[10]);
        AccountStore accounts = Open(folder.Path);
        Assert.Equal(AccountLoadStatus.KeyInvalid, accounts.LastLoad.Status);
        Assert.Equal(AccountSaveStatus.Refused, accounts.Save("A", "b", false));
        Assert.Equal(10, new FileInfo(Path.Combine(folder.Path, "accounts.key")).Length);
    }

    [Fact]
    public void Data_without_its_key_is_kept_and_no_key_is_created()
    {
        using var folder = new TempFolder();
        Open(folder.Path).Save("Keep", "secret", false);
        File.Move(Path.Combine(folder.Path, "accounts.key"), Path.Combine(folder.Path, "elsewhere.key"));
        AccountStore accounts = Open(folder.Path);
        Assert.Equal(AccountLoadStatus.DataUnreadable, accounts.LastLoad.Status);
        Assert.Equal(AccountSaveStatus.Refused, accounts.Save("Other", "pw", false));
        Assert.False(File.Exists(Path.Combine(folder.Path, "accounts.key")));
    }

    [Fact]
    public void Start_fresh_moves_the_old_files_aside_and_allows_saving()
    {
        using var folder = new TempFolder();
        Open(folder.Path).Save("Keep", "secret", false);
        string data = Path.Combine(folder.Path, "accounts.dat");
        byte[] original = File.ReadAllBytes(data);
        File.WriteAllBytes(Path.Combine(folder.Path, "accounts.key"), new byte[5]);
        var accounts = new AccountStore(folder.Path, () => new DateTime(2026, 2, 3, 4, 5, 6));
        accounts.Load();
        Assert.False(accounts.CanSave);

        AccountStartFreshResult fresh = accounts.StartFresh();

        Assert.True(fresh.Success);
        Assert.Equal(3, fresh.MovedFiles.Count);
        Assert.Equal(original, File.ReadAllBytes(data + ".old-20260203-040506"));
        Assert.Equal(5, new FileInfo(Path.Combine(folder.Path, "accounts.key.old-20260203-040506")).Length);
        Assert.True(accounts.CanSave);
        Assert.Equal(AccountSaveStatus.Saved, accounts.Save("New", "pw", false));
        Assert.Equal("pw", Open(folder.Path).PasswordFor("New"));
    }

    // Ported from tests/RCAccountsTests.cpp, duplicate block.
    [Fact]
    public void Same_names_keep_distinct_ids_and_identical_entries_merge_on_load()
    {
        using var folder = new TempFolder();
        {
            AccountStore accounts = Open(folder.Path);
            accounts.Save("Twin", "first", false, "retail.example:14922");
            accounts.Update("", "Twin", "second", false, "moreno.example:14922");
            Assert.Equal(2, accounts.Accounts.Count);
            Assert.NotEqual(0UL, accounts.IdAt(0));
            Assert.NotEqual(0UL, accounts.IdAt(1));
            Assert.NotEqual(accounts.IdAt(0), accounts.IdAt(1));
            accounts.UpdateAt(1, "Twin", "second", false, "moreno.example:14922");
        }
        {
            AccountStore accounts = Open(folder.Path);
            ulong retailId = accounts.IdAt(0);
            ulong morenoId = accounts.IdAt(1);
            Assert.Equal(1, accounts.ActiveIndex);
            Assert.Equal(morenoId, accounts.ActiveId);
            Assert.Equal(0, accounts.IndexOf(retailId));
            Assert.Equal(1, accounts.IndexOf(morenoId));
            Assert.Equal("first", accounts.GetPassword(0));
            Assert.Equal("second", accounts.GetPassword(1));
            Assert.Equal("moreno.example:14922", accounts.Accounts[1].ListServer);
            accounts.UpdateAt(1, "Twin", "first", false, "retail.example:14922");
        }
        {
            AccountStore accounts = Open(folder.Path);
            Assert.Equal(AccountLoadStatus.Migrated, accounts.LastLoad.Status);
            Assert.Single(accounts.Accounts);
            Assert.NotEqual(0UL, accounts.IdAt(0));
            Assert.Equal(accounts.IdAt(0), accounts.ActiveId);
            Assert.Equal("first", accounts.GetPassword(0));
            Assert.Equal("retail.example:14922", accounts.Accounts[0].ListServer);
        }
        Assert.Equal(AccountLoadStatus.Loaded, Open(folder.Path).LastLoad.Status);
    }

    [Fact]
    public void Remove_at_keeps_the_active_account()
    {
        using var folder = new TempFolder();
        AccountStore accounts = Open(folder.Path);
        accounts.Save("A", "a", false);
        accounts.Save("B", "b", false);
        accounts.Save("C", "c", false);
        accounts.RemoveAt(0);
        Assert.Equal("C", accounts.ActiveName);
        Assert.Equal(1, accounts.ActiveIndex);
        accounts.RemoveAt(1);
        Assert.Equal("B", accounts.ActiveName);
        Assert.Equal(AccountSaveStatus.Ignored, accounts.RemoveAt(5));
        Assert.Equal(AccountSaveStatus.Ignored, accounts.Save("", "x", false));
    }

    [Fact]
    public void Files_are_private_on_unix_and_selected_account_is_written()
    {
        using var folder = new TempFolder();
        AccountStore accounts = Open(folder.Path);
        int changes = 0;
        accounts.Changed += (_, _) => changes++;
        accounts.Save("A", "a", false);
        accounts.Save("B", "b", false);
        Assert.Equal(2, changes);
        Assert.Equal("1\n", File.ReadAllText(Path.Combine(folder.Path, "selected-account")));
        Assert.Equal(32, new FileInfo(Path.Combine(folder.Path, "accounts.key")).Length);
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder.Path, "accounts.key")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder.Path, "accounts.dat")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder.Path));
    }

    [Fact]
    public void Legacy_des_matches_the_standard_vectors()
    {
        Assert.Equal(Convert.FromHexString("85E813540F0AB405"), LegacyDes.EncryptEcb(Convert.FromHexString("133457799BBCDFF1"), Convert.FromHexString("0123456789ABCDEF")));
        Assert.Equal(Convert.FromHexString("0000000000000000"), LegacyDes.EncryptEcb(Convert.FromHexString("0E329232EA6D0D73"), Convert.FromHexString("8787878787878787")));
        Assert.Equal(Convert.FromHexString("0123456789ABCDEF"), LegacyDes.DecryptEcb(Convert.FromHexString("133457799BBCDFF1"), Convert.FromHexString("85E813540F0AB405")));
    }

    [Fact]
    public void Format_1_with_des_passwords_is_read_and_migrated()
    {
        using var folder = new TempFolder();
        byte[] key = WriteKey(folder.Path);
        var plain = new Plain();
        plain.Str("Bob");
        plain.U32(3);
        plain.Str("Alice").Bytes(AccountFileFormat.EncodeLegacyPassword("alice-pass"u8, key)).U32(1).Str("direct://game.example:14900");
        plain.Str("Bob").Str("").U32(2).Str("Retail — listserver.graalonline.com:14922").Str("other.example:1");
        plain.Str("").Str("").U32(0);
        WriteData(folder.Path, key, plain.ToArray());

        AccountStore accounts = Open(folder.Path);

        Assert.Equal(AccountLoadStatus.Migrated, accounts.LastLoad.Status);
        Assert.Equal(2, accounts.Accounts.Count);
        Assert.Equal([1UL, 2UL], accounts.Accounts.Select(account => account.Id).ToArray());
        Assert.Equal("Bob", accounts.ActiveName);
        Assert.Equal("alice-pass", accounts.PasswordFor("alice"));
        Assert.True(accounts.Accounts[0].DirectMode);
        Assert.Equal("game.example:14900", accounts.Accounts[0].ListServer);
        Assert.Equal("Retail — listserver.graalonline.com:14922", accounts.Accounts[1].ListServer);
        Assert.False(accounts.Accounts[1].HasPassword);
        Assert.Equal(3, ReadFormat(folder.Path, key));
        AccountStore again = Open(folder.Path);
        Assert.Equal(AccountLoadStatus.Loaded, again.LastLoad.Status);
        Assert.Equal("alice-pass", again.GetPassword(0));
        Assert.Equal("Bob", again.ActiveName);
    }

    [Fact]
    public void Format_2_with_ids_is_read_and_migrated()
    {
        using var folder = new TempFolder();
        byte[] key = WriteKey(folder.Path);
        var plain = new Plain();
        plain.Str("GScriptRCAccounts2").U64(9).U32(2);
        plain.U64(7).Str("First").Bytes(AccountFileFormat.EncodePassword("one"u8, key)).U32(1).Str("a.example:14922");
        plain.U64(9).Str("Second").Bytes(AccountFileFormat.EncodeLegacyPassword("two-is-a-longer-password"u8, key)).U32(0);
        WriteData(folder.Path, key, plain.ToArray());

        AccountStore accounts = Open(folder.Path);

        Assert.Equal(AccountLoadStatus.Migrated, accounts.LastLoad.Status);
        Assert.Equal([7UL, 9UL], accounts.Accounts.Select(account => account.Id).ToArray());
        Assert.Equal(9UL, accounts.ActiveId);
        Assert.Equal("one", accounts.GetPassword(0));
        Assert.Equal("two-is-a-longer-password", accounts.GetPassword(1));
        Assert.False(accounts.Accounts[0].DirectMode);
        Assert.Equal(3, ReadFormat(folder.Path, key));
        accounts.Save("Third", "three", false);
        Assert.Equal(10UL, accounts.ActiveId);
    }

    [Fact]
    public void Format_3_is_read_as_it_is()
    {
        using var folder = new TempFolder();
        byte[] key = WriteKey(folder.Path);
        var plain = new Plain();
        plain.Str("GScriptRCAccounts3").U64(4).U32(1);
        plain.U64(4).Str("Direct").Bytes(AccountFileFormat.EncodePassword("pw"u8, key)).U32(1).Str("game.example:14900").U32(1);
        WriteData(folder.Path, key, plain.ToArray());
        byte[] before = File.ReadAllBytes(Path.Combine(folder.Path, "accounts.dat"));

        AccountStore accounts = Open(folder.Path);

        Assert.Equal(AccountLoadStatus.Loaded, accounts.LastLoad.Status);
        Assert.Equal(new SavedAccount(4, "Direct", true, "game.example:14900", true), accounts.Active);
        Assert.Equal("pw", accounts.GetPassword(0));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(folder.Path, "accounts.dat")));
    }

    [Fact]
    public void Selected_account_file_is_used_when_the_id_does_not_resolve()
    {
        using var folder = new TempFolder();
        byte[] key = WriteKey(folder.Path);
        var plain = new Plain();
        plain.Str("GScriptRCAccounts3").U64(0).U32(2);
        plain.U64(1).Str("A").Str("").U32(0).U32(0);
        plain.U64(2).Str("B").Str("").U32(0).U32(0);
        WriteData(folder.Path, key, plain.ToArray());
        File.WriteAllText(Path.Combine(folder.Path, "selected-account"), "1\n");
        AccountStore accounts = Open(folder.Path);
        Assert.Equal("B", accounts.ActiveName);
        Assert.Equal(AccountLoadStatus.Migrated, accounts.LastLoad.Status);
    }

    [Theory]
    [InlineData("trailing")]
    [InlineData("zero-id")]
    [InlineData("too-many")]
    [InlineData("short-string")]
    [InlineData("bad-password")]
    public void Malformed_plaintext_is_unreadable_and_kept(string damage)
    {
        using var folder = new TempFolder();
        byte[] key = WriteKey(folder.Path);
        var plain = new Plain();
        plain.Str("GScriptRCAccounts3").U64(1);
        switch (damage)
        {
            case "too-many": plain.U32(1001); break;
            case "zero-id": plain.U32(1).U64(0).Str("A").Str("").U32(0).U32(0); break;
            case "short-string": plain.U32(1).U64(1).U32(500).Str("x"); break;
            case "bad-password": plain.U32(1).U64(1).Str("A").Str("PWG1-not-a-container").U32(0).U32(0); break;
            default: plain.U32(1).U64(1).Str("A").Str("").U32(0).U32(0).U32(99); break;
        }
        WriteData(folder.Path, key, plain.ToArray());
        byte[] before = File.ReadAllBytes(Path.Combine(folder.Path, "accounts.dat"));
        AccountStore accounts = Open(folder.Path);
        Assert.Equal(AccountLoadStatus.DataUnreadable, accounts.LastLoad.Status);
        Assert.Equal(AccountSaveStatus.Refused, accounts.Save("X", "y", false));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(folder.Path, "accounts.dat")));
    }

    [Fact]
    public void Garbage_data_file_is_unreadable()
    {
        using var folder = new TempFolder();
        WriteKey(folder.Path);
        File.WriteAllBytes(Path.Combine(folder.Path, "accounts.dat"), "not encrypted at all"u8.ToArray());
        Assert.Equal(AccountLoadStatus.DataUnreadable, Open(folder.Path).LastLoad.Status);
    }

    [Fact]
    public void Empty_folder_creates_the_key_on_first_save_only()
    {
        using var folder = new TempFolder();
        AccountStore accounts = Open(folder.Path);
        Assert.Equal(AccountLoadStatus.NoAccounts, accounts.LastLoad.Status);
        Assert.False(File.Exists(Path.Combine(folder.Path, "accounts.key")));
        accounts.Save("A", "b", false);
        Assert.True(File.Exists(Path.Combine(folder.Path, "accounts.key")));
    }

    private static byte[] WriteKey(string directory)
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(Path.Combine(directory, "accounts.key"), key);
        return key;
    }

    private static void WriteData(string directory, byte[] key, byte[] plaintext) =>
        File.WriteAllBytes(Path.Combine(directory, "accounts.dat"), AccountCrypto.Encrypt(plaintext, key, "GSRCACC1"u8));

    private static int ReadFormat(string directory, byte[] key)
    {
        byte[] plaintext = AccountCrypto.Decrypt(File.ReadAllBytes(Path.Combine(directory, "accounts.dat")), key, "GSRCACC1"u8)!;
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(plaintext);
        string header = Encoding.UTF8.GetString(plaintext, 4, length);
        return header == "GScriptRCAccounts3" ? 3 : header == "GScriptRCAccounts2" ? 2 : 1;
    }

    /// <summary>Builds an account plaintext by hand, little-endian as the C++ wrote it.</summary>
    private sealed class Plain
    {
        private readonly MemoryStream _stream = new();

        public Plain U32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            _stream.Write(buffer);
            return this;
        }

        public Plain U64(ulong value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            _stream.Write(buffer);
            return this;
        }

        public Plain Str(string value) => Bytes(Encoding.UTF8.GetBytes(value));

        public Plain Bytes(byte[] value)
        {
            U32((uint)value.Length);
            _stream.Write(value);
            return this;
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
