using System.Security.Cryptography;
using System.Text;
using RemoteControl.Core;

namespace RemoteControl.Sync;

/// <summary>
/// The Git credentials of one server's sync (<c>storeGitKey</c>/<c>storeGitToken</c>, <c>TSyncManager.cpp:1857-1911</c>): an SSH private key
/// in <c>&lt;settings&gt;.credentials</c> and an HTTPS token in <c>&lt;settings&gt;.token</c>, each an AES-256-GCM container
/// <c>"GSRCGIT1" || nonce(12) || tag(16) || ciphertext</c> under the 32-byte <c>accounts.key</c> of the saved accounts, mode 0600 on Unix.
/// Files the C++ wrote are read as they are. Secrets are never logged; buffers holding them are cleared after use.
/// </summary>
public sealed class GitCredentialStore
{
    /// <summary>The magic of a credential container.</summary>
    public static ReadOnlySpan<byte> Magic => "GSRCGIT1"u8;

    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaximumFileSize = 16 * 1024 * 1024;

    /// <summary>Creates the store of one server.</summary>
    /// <param name="userDirectory">The user folder (holds <c>accounts.key</c> and <c>sync/</c>).</param>
    /// <param name="serverName">The server name.</param>
    public GitCredentialStore(string userDirectory, string serverName)
    {
        ArgumentException.ThrowIfNullOrEmpty(userDirectory);
        string settings = SyncSettingsFile.PathFor(userDirectory, serverName ?? string.Empty);
        KeyPath = Path.ChangeExtension(settings, ".credentials");
        TokenPath = Path.ChangeExtension(settings, ".token");
        AccountsKeyPath = Path.Combine(userDirectory, "accounts.key");
    }

    /// <summary>The encrypted SSH key file.</summary>
    public string KeyPath { get; }

    /// <summary>The encrypted token file.</summary>
    public string TokenPath { get; }

    /// <summary>The key file shared with the saved accounts.</summary>
    public string AccountsKeyPath { get; }

    /// <summary>Whether an SSH key is stored.</summary>
    public bool HasKey => File.Exists(KeyPath);

    /// <summary>Whether a token is stored.</summary>
    public bool HasToken => File.Exists(TokenPath);

    /// <summary>Encrypts and stores an SSH private key (the text must contain <c>PRIVATE KEY</c>).</summary>
    /// <param name="keyText">The key file's content.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><c>null</c> when stored, otherwise why not.</returns>
    public Task<string?> StoreKeyAsync(string keyText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(keyText) || !keyText.Contains("PRIVATE KEY", StringComparison.Ordinal))
            return Task.FromResult<string?>("The selected file is not a supported private key.");
        return Task.Run(() => Store(KeyPath, keyText), cancellationToken);
    }

    /// <summary>Encrypts and stores an access token.</summary>
    /// <param name="token">The token.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><c>null</c> when stored, otherwise why not.</returns>
    public Task<string?> StoreTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token)) return Task.FromResult<string?>("Enter a token before saving.");
        return Task.Run(() => Store(TokenPath, token), cancellationToken);
    }

    /// <summary>The stored SSH key, or <c>null</c>.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The key text.</returns>
    public Task<string?> LoadKeyAsync(CancellationToken cancellationToken = default) => Task.Run(() => Load(KeyPath), cancellationToken);

    /// <summary>The stored token, or <c>null</c>.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The token.</returns>
    public Task<string?> LoadTokenAsync(CancellationToken cancellationToken = default) => Task.Run(() => Load(TokenPath), cancellationToken);

    /// <summary>Removes the stored SSH key.</summary>
    /// <returns>Whether nothing is stored any more.</returns>
    public bool ClearKey() => Delete(KeyPath);

    /// <summary>Removes the stored token.</summary>
    /// <returns>Whether nothing is stored any more.</returns>
    public bool ClearToken() => Delete(TokenPath);

    /// <summary>Encrypts bytes into a container (also used by tests to build what the C++ wrote).</summary>
    /// <param name="plaintext">The secret.</param>
    /// <param name="key">The 32-byte key.</param>
    /// <returns>The container.</returns>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key)
    {
        byte[] output = new byte[Magic.Length + NonceSize + TagSize + plaintext.Length];
        Magic.CopyTo(output);
        Span<byte> nonce = output.AsSpan(Magic.Length, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(Magic.Length + NonceSize + TagSize), output.AsSpan(Magic.Length + NonceSize, TagSize));
        return output;
    }

    /// <summary>Decrypts a container; <c>null</c> when the magic, the length or the tag is wrong.</summary>
    /// <param name="container">The container.</param>
    /// <param name="key">The 32-byte key.</param>
    /// <returns>The secret.</returns>
    public static byte[]? Decrypt(ReadOnlySpan<byte> container, ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize || container.Length < Magic.Length + NonceSize + TagSize || !container[..Magic.Length].SequenceEqual(Magic)) return null;
        byte[] plaintext = new byte[container.Length - Magic.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(container.Slice(Magic.Length, NonceSize), container[(Magic.Length + NonceSize + TagSize)..], container.Slice(Magic.Length + NonceSize, TagSize), plaintext);
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return null;
        }
    }

    private string? Store(string path, string secret)
    {
        byte[]? key = null;
        byte[] plaintext = Encoding.UTF8.GetBytes(secret);
        try
        {
            key = LoadAccountsKey(create: true, out string? error);
            if (key is null) return error;
            byte[] container = Encrypt(plaintext, key);
            AtomicFile.WriteAllBytes(path, container, userOnly: true);
            CryptographicOperations.ZeroMemory(container);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return "The credential could not be stored: " + exception.Message;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private string? Load(string path)
    {
        byte[]? key = null;
        byte[]? container = null;
        byte[]? plaintext = null;
        try
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaximumFileSize) return null;
            container = File.ReadAllBytes(path);
            key = LoadAccountsKey(create: false, out _);
            if (key is null) return null;
            plaintext = Decrypt(container, key);
            return plaintext is null ? null : Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Default.Warn($"Stored Git credential {Path.GetFileName(path)} could not be read: {exception.Message}");
            return null;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (container is not null) CryptographicOperations.ZeroMemory(container);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// <c>accounts.key</c>: 32 bytes. When it is missing and <paramref name="create"/> is set, a new key is created (as the C++ did); a key file
    /// of another length is never replaced, because the saved accounts depend on it.
    /// </summary>
    private byte[]? LoadAccountsKey(bool create, out string? error)
    {
        error = null;
        if (File.Exists(AccountsKeyPath))
        {
            byte[] existing = File.ReadAllBytes(AccountsKeyPath);
            if (existing.Length == KeySize) return existing;
            CryptographicOperations.ZeroMemory(existing);
            error = "accounts.key is not a valid key; it is left unchanged and the credential was not stored.";
            return null;
        }

        if (!create)
        {
            error = "There is no accounts.key.";
            return null;
        }

        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        if (AtomicFile.TryCreateNew(AccountsKeyPath, key, userOnly: true)) return key;
        CryptographicOperations.ZeroMemory(key);
        return LoadAccountsKey(create: false, out error);
    }

    private static bool Delete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Default.Warn($"Stored Git credential {Path.GetFileName(path)} could not be removed: {exception.Message}");
            return false;
        }
    }
}
