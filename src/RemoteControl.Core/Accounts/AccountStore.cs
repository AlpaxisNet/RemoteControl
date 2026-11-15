using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RemoteControl.Core;

/// <summary>One saved account as the user interface sees it. The password is not part of it: ask the store with <see cref="AccountStore.GetPassword"/>.</summary>
/// <param name="Id">A non-zero id that stays the same across saves (two accounts may share a name).</param>
/// <param name="Name">The account name.</param>
/// <param name="HasPassword">Whether a password is stored.</param>
/// <param name="ListServer">The list-server association (see <see cref="ListServerAssociation"/>), or empty.</param>
/// <param name="DirectMode">Connect directly to <see cref="ListServer"/> as a game server instead of through a list server.</param>
public sealed record SavedAccount(ulong Id, string Name, bool HasPassword, string ListServer, bool DirectMode);

/// <summary>How <see cref="AccountStore.Load"/> went.</summary>
public enum AccountLoadStatus
{
    /// <summary>The accounts were read.</summary>
    Loaded,

    /// <summary>The accounts were read from an older format (or needed merging) and were saved again in format 3.</summary>
    Migrated,

    /// <summary>There is no <c>accounts.dat</c> yet.</summary>
    NoAccounts,

    /// <summary><c>accounts.key</c> is not 32 bytes or cannot be read. Both files are kept; saving is refused until the user chooses <see cref="AccountStore.StartFresh"/> or restores the key.</summary>
    KeyInvalid,

    /// <summary><c>accounts.dat</c> exists but cannot be read, decrypted or parsed (or its key is missing). Both files are kept; saving is refused until the user decides.</summary>
    DataUnreadable,
}

/// <summary>What a change of <see cref="AccountStore"/> did.</summary>
public enum AccountSaveStatus
{
    /// <summary>The change was made and written.</summary>
    Saved,

    /// <summary>Nothing was changed: saving is refused because the stored files could not be read (<see cref="AccountStore.CanSave"/> is false).</summary>
    Refused,

    /// <summary>The change was made in memory but writing failed (<see cref="AccountStore.LastError"/>); the previous files are intact.</summary>
    Failed,

    /// <summary>Nothing was changed: the name was empty or the index out of range.</summary>
    Ignored,
}

/// <summary>The outcome of <see cref="AccountStore.Load"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Message">A sentence for the user when something went wrong, otherwise <c>null</c>.</param>
public sealed record AccountLoadResult(AccountLoadStatus Status, string? Message = null)
{
    /// <summary>Whether the user must decide what to do (the stored accounts could not be read).</summary>
    public bool NeedsAttention => Status is AccountLoadStatus.KeyInvalid or AccountLoadStatus.DataUnreadable;
}

/// <summary>The outcome of <see cref="AccountStore.StartFresh"/>.</summary>
/// <param name="Success">Whether the old files were moved aside and the store is empty and writable.</param>
/// <param name="MovedFiles">Where the old files are now.</param>
/// <param name="Error">Why it failed, otherwise <c>null</c>.</param>
public sealed record AccountStartFreshResult(bool Success, IReadOnlyList<string> MovedFiles, string? Error = null);

/// <summary>
/// The saved accounts (<c>accounts.key</c>, <c>accounts.dat</c>, <c>selected-account</c>), read in every format the C++ wrote
/// and written in format 3 (see <c>TRCAccounts.cpp</c>, <c>TEncryption.cpp</c>). <c>accounts.dat</c> is an AES-256-GCM container
/// (magic <c>GSRCACC1</c>) under the 32 random bytes of <c>accounts.key</c>; each password inside is a second container (magic
/// <c>PWG1</c>) under the same key. Passwords stay encrypted in memory and are decrypted only by <see cref="GetPassword"/>.
/// </summary>
/// <remarks>
/// Defect D-12 is fixed: the C++ replaced a key file of the wrong size with a new key and then saved an empty list over an
/// <c>accounts.dat</c> it could not decrypt, losing every account. Here such files are never replaced: <see cref="Load"/> reports
/// <see cref="AccountLoadStatus.KeyInvalid"/> or <see cref="AccountLoadStatus.DataUnreadable"/>, every change returns
/// <see cref="AccountSaveStatus.Refused"/>, and only <see cref="StartFresh"/> (after the user agreed) moves the old files aside.
/// A missing key is created only when there is no <c>accounts.dat</c>, written to a temporary file and moved into place without
/// replacing a key another instance created at the same moment. On Unix the folder is made user-only (0700) and the files 0600.
/// The store is safe to call from any thread; <see cref="Changed"/> is raised on the calling thread.
/// </remarks>
public sealed class AccountStore
{
    private const int MaximumDataSize = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, false);

    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private List<AccountFileFormat.Entry> _entries = new();
    private byte[]? _key;
    private bool _blocked;
    private int _activeIndex = -1;
    private string _activeName = string.Empty;
    private ulong _nextId = 1;
    private string? _lastError;
    private AccountLoadResult _lastLoad = new(AccountLoadStatus.NoAccounts);

    /// <summary>Creates a store for the files in <paramref name="directory"/>; call <see cref="Load"/> before use.</summary>
    /// <param name="directory">The folder of <c>accounts.key</c> and <c>accounts.dat</c> (the user folder; a temporary folder in tests).</param>
    /// <param name="clock">The clock used to name moved-aside files; the local time by default.</param>
    public AccountStore(string directory, Func<DateTime>? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = Path.GetFullPath(directory);
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>Creates a store for the user folder of <paramref name="paths"/>.</summary>
    /// <param name="paths">Where the files are.</param>
    public AccountStore(AppPaths paths) : this((paths ?? throw new ArgumentNullException(nameof(paths))).UserDirectory)
    {
    }

    /// <summary>Raised after the accounts or the active account changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The folder of the files.</summary>
    public string Directory { get; }

    /// <summary>The key file.</summary>
    public string KeyFile => Path.Combine(Directory, "accounts.key");

    /// <summary>The encrypted accounts.</summary>
    public string DataFile => Path.Combine(Directory, "accounts.dat");

    /// <summary>The active-index fallback file.</summary>
    public string SelectedFile => Path.Combine(Directory, "selected-account");

    /// <summary>The result of the last <see cref="Load"/>.</summary>
    public AccountLoadResult LastLoad
    {
        get { lock (_gate) return _lastLoad; }
    }

    /// <summary>Whether changes can be saved (false after <see cref="AccountLoadStatus.KeyInvalid"/> or <see cref="AccountLoadStatus.DataUnreadable"/>).</summary>
    public bool CanSave
    {
        get { lock (_gate) return !_blocked; }
    }

    /// <summary>The message of the last failed write, or <c>null</c>.</summary>
    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>The saved accounts, in order.</summary>
    public IReadOnlyList<SavedAccount> Accounts
    {
        get { lock (_gate) return _entries.Select(ToPublic).ToArray(); }
    }

    /// <summary>The account names, in order.</summary>
    public IReadOnlyList<string> Names
    {
        get { lock (_gate) return _entries.Select(entry => entry.Name).ToArray(); }
    }

    /// <summary>The index of the active account (the one last saved or used), or -1.</summary>
    public int ActiveIndex
    {
        get { lock (_gate) return _activeIndex; }
    }

    /// <summary>The id of the active account, or 0.</summary>
    public ulong ActiveId
    {
        get { lock (_gate) return _activeIndex >= 0 ? _entries[_activeIndex].Id : 0; }
    }

    /// <summary>The active account, or <c>null</c>.</summary>
    public SavedAccount? Active
    {
        get { lock (_gate) return _activeIndex >= 0 ? ToPublic(_entries[_activeIndex]) : null; }
    }

    /// <summary>The name of the active account, or empty.</summary>
    public string ActiveName
    {
        get { lock (_gate) return _activeName; }
    }

    /// <summary>The index of the account with <paramref name="id"/>, or -1.</summary>
    /// <param name="id">An account id.</param>
    /// <returns>The index.</returns>
    public int IndexOf(ulong id)
    {
        lock (_gate) return _entries.FindIndex(entry => entry.Id == id);
    }

    /// <summary>The index of the first account named <paramref name="name"/> (ignoring case), or -1.</summary>
    /// <param name="name">An account name.</param>
    /// <returns>The index.</returns>
    public int IndexOfName(string name)
    {
        lock (_gate) return FindName(name);
    }

    /// <summary>The id of the account at <paramref name="index"/>, or 0.</summary>
    /// <param name="index">An index.</param>
    /// <returns>The id.</returns>
    public ulong IdAt(int index)
    {
        lock (_gate) return index >= 0 && index < _entries.Count ? _entries[index].Id : 0;
    }

    /// <summary>Decrypts the password of the account at <paramref name="index"/>. Keep the result only as long as needed and never log it.</summary>
    /// <param name="index">An index.</param>
    /// <returns>The password, or empty when there is none.</returns>
    public string GetPassword(int index)
    {
        lock (_gate) return index >= 0 && index < _entries.Count ? Decode(_entries[index].EncodedPassword) : string.Empty;
    }

    /// <summary>Decrypts the password of the first account named <paramref name="name"/> (ignoring case).</summary>
    /// <param name="name">An account name.</param>
    /// <returns>The password, or empty.</returns>
    public string PasswordFor(string name)
    {
        lock (_gate)
        {
            int index = FindName(name);
            return index >= 0 ? Decode(_entries[index].EncodedPassword) : string.Empty;
        }
    }

    /// <summary>The list-server association of the first account named <paramref name="name"/>, or empty.</summary>
    /// <param name="name">An account name.</param>
    /// <returns>The association.</returns>
    public string ListServerFor(string name)
    {
        lock (_gate)
        {
            int index = FindName(name);
            return index >= 0 ? _entries[index].ListServer : string.Empty;
        }
    }

    /// <summary>
    /// Reads the files. A missing <c>accounts.dat</c> gives an empty, writable store; any file that exists but cannot be used is
    /// kept and makes the store refuse changes. Accounts in an older format are saved again in format 3.
    /// </summary>
    /// <returns>What happened; show <see cref="AccountLoadResult.Message"/> and offer <see cref="StartFresh"/> when <see cref="AccountLoadResult.NeedsAttention"/>.</returns>
    public AccountLoadResult Load()
    {
        AccountLoadResult result;
        bool changed;
        lock (_gate)
        {
            result = LoadLocked();
            _lastLoad = result;
            changed = true;
        }
        if (changed) RaiseChanged();
        return result;
    }

    /// <summary>
    /// The user's decision after a failed load: moves <c>accounts.key</c>, <c>accounts.dat</c> and <c>selected-account</c> aside to
    /// <c>&lt;name&gt;.old-&lt;yyyyMMdd-HHmmss&gt;</c> and starts with no accounts. Nothing is deleted.
    /// </summary>
    /// <returns>Where the files went, or why they could not be moved (then the store stays as it was).</returns>
    public AccountStartFreshResult StartFresh()
    {
        AccountStartFreshResult result;
        lock (_gate)
        {
            var moved = new List<string>();
            DateTime now = _clock();
            try
            {
                foreach (string path in new[] { DataFile, KeyFile, SelectedFile })
                {
                    string? target = AtomicFile.MoveAside(path, "old", now);
                    if (target is not null) moved.Add(target);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new AccountStartFreshResult(false, moved, $"The old account files could not be moved aside: {exception.Message}");
            }
            ClearKey();
            _entries = new List<AccountFileFormat.Entry>();
            _activeIndex = -1;
            _activeName = string.Empty;
            _nextId = 1;
            _blocked = false;
            _lastError = null;
            _lastLoad = new AccountLoadResult(AccountLoadStatus.NoAccounts);
            result = new AccountStartFreshResult(true, moved);
        }
        RaiseChanged();
        return result;
    }

    /// <summary>
    /// Saves the login of an account by name (<c>RCAccounts::save</c>): the first account with that name (ignoring case) is
    /// updated or a new one is added; it becomes the active account.
    /// </summary>
    /// <param name="name">The account name.</param>
    /// <param name="password">The password; not stored when <paramref name="dontSavePassword"/>.</param>
    /// <param name="dontSavePassword">Store an empty password.</param>
    /// <param name="listServer">The list-server association, or empty.</param>
    /// <param name="directMode">Connect directly.</param>
    /// <returns>What happened.</returns>
    public AccountSaveStatus Save(string name, string password, bool dontSavePassword, string listServer = "", bool directMode = false)
    {
        if (string.IsNullOrEmpty(name)) return AccountSaveStatus.Ignored;
        return Mutate(() =>
        {
            int index = FindName(name);
            if (index < 0)
            {
                _entries.Add(new AccountFileFormat.Entry { Id = _nextId++ });
                index = _entries.Count - 1;
            }
            Assign(index, name, dontSavePassword ? null : password, listServer, directMode);
            return true;
        });
    }

    /// <summary>Saves the login of the account at <paramref name="index"/> (<c>RCAccounts::saveAt</c>); it becomes the active account.</summary>
    /// <param name="index">An index.</param>
    /// <param name="password">The password; not stored when <paramref name="dontSavePassword"/>.</param>
    /// <param name="dontSavePassword">Store an empty password.</param>
    /// <param name="listServer">The list-server association, or empty.</param>
    /// <param name="directMode">Connect directly.</param>
    /// <returns>What happened.</returns>
    public AccountSaveStatus SaveAt(int index, string password, bool dontSavePassword, string listServer = "", bool directMode = false) =>
        Mutate(() =>
        {
            if (index < 0 || index >= _entries.Count) return false;
            Assign(index, _entries[index].Name, dontSavePassword ? null : password, listServer, directMode);
            return true;
        });

    /// <summary>
    /// Edits or adds an account (<c>RCAccounts::update</c>): the first account named <paramref name="previousName"/> is changed, or a
    /// new one is added when <paramref name="previousName"/> is empty or not found; it becomes the active account.
    /// </summary>
    /// <param name="previousName">The name before the edit, or empty to add.</param>
    /// <param name="name">The new name.</param>
    /// <param name="password">The password; not stored when <paramref name="dontSavePassword"/>.</param>
    /// <param name="dontSavePassword">Store an empty password.</param>
    /// <param name="listServer">The list-server association, or empty (the C++ took a list and kept only the first).</param>
    /// <param name="directMode">Connect directly.</param>
    /// <returns>What happened.</returns>
    public AccountSaveStatus Update(string previousName, string name, string password, bool dontSavePassword, string listServer = "", bool directMode = false)
    {
        if (string.IsNullOrEmpty(name)) return AccountSaveStatus.Ignored;
        return Mutate(() =>
        {
            int index = string.IsNullOrEmpty(previousName) ? -1 : FindName(previousName);
            if (index < 0)
            {
                _entries.Add(new AccountFileFormat.Entry { Id = _nextId++ });
                index = _entries.Count - 1;
            }
            Assign(index, name, dontSavePassword ? null : password, listServer, directMode);
            return true;
        });
    }

    /// <summary>Edits the account at <paramref name="index"/> (<c>RCAccounts::updateAt</c>); it becomes the active account.</summary>
    /// <param name="index">An index.</param>
    /// <param name="name">The new name.</param>
    /// <param name="password">The password; not stored when <paramref name="dontSavePassword"/>.</param>
    /// <param name="dontSavePassword">Store an empty password.</param>
    /// <param name="listServer">The list-server association, or empty.</param>
    /// <param name="directMode">Connect directly.</param>
    /// <returns>What happened.</returns>
    public AccountSaveStatus UpdateAt(int index, string name, string password, bool dontSavePassword, string listServer = "", bool directMode = false)
    {
        if (string.IsNullOrEmpty(name)) return AccountSaveStatus.Ignored;
        return Mutate(() =>
        {
            if (index < 0 || index >= _entries.Count) return false;
            Assign(index, name, dontSavePassword ? null : password, listServer, directMode);
            return true;
        });
    }

    /// <summary>
    /// Sets the list server of an account by name, keeping its password and mode (<c>RCAccounts::associate</c>); adds the account
    /// when it does not exist. It becomes the active account.
    /// </summary>
    /// <param name="name">The account name.</param>
    /// <param name="listServer">The list-server association.</param>
    /// <returns>What happened.</returns>
    public AccountSaveStatus Associate(string name, string listServer)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(listServer)) return AccountSaveStatus.Ignored;
        return Mutate(() =>
        {
            int index = FindName(name);
            if (index < 0)
            {
                _entries.Add(new AccountFileFormat.Entry { Id = _nextId++ });
                index = _entries.Count - 1;
            }
            AccountFileFormat.Entry entry = _entries[index];
            entry.Name = name;
            entry.ListServer = listServer;
            Activate(index);
            return true;
        });
    }

    /// <summary>Removes every account named <paramref name="name"/> (ignoring case).</summary>
    /// <param name="name">An account name.</param>
    /// <returns>What happened.</returns>
    public AccountSaveStatus Remove(string name) =>
        Mutate(() =>
        {
            ulong activeId = _activeIndex >= 0 ? _entries[_activeIndex].Id : 0;
            bool activeRemoved = string.Equals(_activeName, name, StringComparison.OrdinalIgnoreCase);
            _entries.RemoveAll(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
            if (activeRemoved || _entries.FindIndex(entry => entry.Id == activeId) < 0) ActivateFirst();
            else Activate(_entries.FindIndex(entry => entry.Id == activeId));
            return true;
        });

    /// <summary>Removes the account at <paramref name="index"/>.</summary>
    /// <param name="index">An index.</param>
    /// <returns>What happened.</returns>
    public AccountSaveStatus RemoveAt(int index) =>
        Mutate(() =>
        {
            if (index < 0 || index >= _entries.Count) return false;
            bool active = _activeIndex == index;
            _entries.RemoveAt(index);
            if (active) ActivateFirst();
            else if (_activeIndex > index) _activeIndex--;
            return true;
        });

    private AccountLoadResult LoadLocked()
    {
        ClearKey();
        _entries = new List<AccountFileFormat.Entry>();
        _activeIndex = -1;
        _activeName = string.Empty;
        _nextId = 1;
        _blocked = false;
        _lastError = null;

        bool keyExists = File.Exists(KeyFile);
        bool dataExists = File.Exists(DataFile);
        if (keyExists)
        {
            try
            {
                var info = new FileInfo(KeyFile);
                if (info.Length != AccountCrypto.KeySize)
                {
                    _blocked = true;
                    return new AccountLoadResult(AccountLoadStatus.KeyInvalid, $"The account key {KeyFile} is {info.Length} bytes instead of {AccountCrypto.KeySize}. The saved accounts cannot be read; both files are kept. Restore the key or start with no saved accounts (the old files are kept aside).");
                }
                _key = File.ReadAllBytes(KeyFile);
                if (_key.Length != AccountCrypto.KeySize)
                {
                    ClearKey();
                    _blocked = true;
                    return new AccountLoadResult(AccountLoadStatus.KeyInvalid, $"The account key {KeyFile} changed size while it was read. Both files are kept.");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ClearKey();
                _blocked = true;
                return new AccountLoadResult(AccountLoadStatus.KeyInvalid, $"The account key {KeyFile} cannot be read ({exception.Message}). Both files are kept.");
            }
        }

        if (!dataExists) return new AccountLoadResult(AccountLoadStatus.NoAccounts);
        if (_key is null)
        {
            _blocked = true;
            return new AccountLoadResult(AccountLoadStatus.DataUnreadable, $"The saved accounts {DataFile} exist but their key {KeyFile} is missing. The file is kept; restore the key or start with no saved accounts.");
        }

        byte[] encrypted;
        try
        {
            var info = new FileInfo(DataFile);
            if (info.Length > MaximumDataSize)
            {
                _blocked = true;
                return new AccountLoadResult(AccountLoadStatus.DataUnreadable, $"The saved accounts {DataFile} are larger than such a file can be. Both files are kept.");
            }
            encrypted = File.ReadAllBytes(DataFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _blocked = true;
            return new AccountLoadResult(AccountLoadStatus.DataUnreadable, $"The saved accounts {DataFile} cannot be read ({exception.Message}). Both files are kept.");
        }

        byte[]? plaintext = AccountCrypto.Decrypt(encrypted, _key, AccountCrypto.FileMagic);
        AccountFileFormat.Parsed? parsed = null;
        try
        {
            if (plaintext is not null) parsed = AccountFileFormat.Deserialize(plaintext, _key);
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
        if (parsed is null)
        {
            _blocked = true;
            return new AccountLoadResult(AccountLoadStatus.DataUnreadable, plaintext is null
                ? $"The saved accounts {DataFile} do not decrypt with {KeyFile} (the key does not belong to them, or the file is damaged). Both files are kept."
                : $"The saved accounts {DataFile} are damaged. Both files are kept.");
        }

        _entries = parsed.Entries;
        foreach (AccountFileFormat.Entry entry in _entries) _nextId = Math.Max(_nextId, entry.Id == ulong.MaxValue ? entry.Id : entry.Id + 1);
        bool migrated = parsed.Migrated;
        _activeIndex = parsed.SelectedId == 0 ? -1 : _entries.FindIndex(entry => entry.Id == parsed.SelectedId);
        if (_activeIndex < 0 && TryReadSelectedIndex(out int stored) && stored < _entries.Count)
        {
            _activeIndex = stored;
            migrated = true;
        }
        if (_activeIndex < 0 && parsed.SelectedName.Length != 0) _activeIndex = FindName(parsed.SelectedName);
        if (_activeIndex < 0 && _entries.Count > 0) _activeIndex = 0;
        _activeName = _activeIndex >= 0 ? _entries[_activeIndex].Name : string.Empty;

        if (!migrated) return new AccountLoadResult(AccountLoadStatus.Loaded);
        Persist();
        return new AccountLoadResult(AccountLoadStatus.Migrated, _lastError);
    }

    private bool TryReadSelectedIndex(out int index)
    {
        index = -1;
        try
        {
            if (!File.Exists(SelectedFile)) return false;
            string text = File.ReadAllText(SelectedFile).Trim();
            int end = 0;
            while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
            return end > 0 && int.TryParse(text.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out index);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private AccountSaveStatus Mutate(Func<bool> change)
    {
        AccountSaveStatus status;
        lock (_gate)
        {
            if (_blocked) return AccountSaveStatus.Refused;
            if (!EnsureKey()) return AccountSaveStatus.Failed;
            if (!change()) return AccountSaveStatus.Ignored;
            status = Persist() ? AccountSaveStatus.Saved : AccountSaveStatus.Failed;
        }
        RaiseChanged();
        return status;
    }

    /// <summary>Makes sure a key exists in memory and on disk, creating one only when there is none (never replacing a file).</summary>
    private bool EnsureKey()
    {
        try
        {
            PrepareDirectory();
            if (_key is not null)
            {
                if (File.Exists(KeyFile))
                {
                    byte[] onDisk = File.ReadAllBytes(KeyFile);
                    bool same = CryptographicOperations.FixedTimeEquals(onDisk, _key);
                    CryptographicOperations.ZeroMemory(onDisk);
                    if (same) return true;
                    _lastError = $"The account key {KeyFile} was changed by another program while this one was running; nothing was saved.";
                    return false;
                }
                if (AtomicFile.TryCreateNew(KeyFile, _key, userOnly: true)) return true;
                _lastError = $"The account key {KeyFile} was created by another program; nothing was saved.";
                return false;
            }
            if (File.Exists(DataFile))
            {
                _lastError = $"The saved accounts {DataFile} appeared while this program was running; nothing was saved. Restart to read them.";
                return false;
            }
            byte[] key = RandomNumberGenerator.GetBytes(AccountCrypto.KeySize);
            if (!AtomicFile.TryCreateNew(KeyFile, key, userOnly: true))
            {
                CryptographicOperations.ZeroMemory(key);
                byte[] existing = File.ReadAllBytes(KeyFile);
                if (existing.Length != AccountCrypto.KeySize)
                {
                    CryptographicOperations.ZeroMemory(existing);
                    _lastError = $"The account key {KeyFile} created by another program is not {AccountCrypto.KeySize} bytes; nothing was saved.";
                    return false;
                }
                key = existing;
            }
            _key = key;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _lastError = $"The account key could not be prepared: {exception.Message}";
            return false;
        }
    }

    private bool Persist()
    {
        if (_key is null) return false;
        byte[] plaintext = AccountFileFormat.Serialize(_activeIndex >= 0 ? _entries[_activeIndex].Id : 0, _entries);
        try
        {
            byte[] encrypted = AccountCrypto.Encrypt(plaintext, _key, AccountCrypto.FileMagic);
            PrepareDirectory();
            AtomicFile.WriteAllBytes(DataFile, encrypted, userOnly: true);
            AtomicFile.WriteAllText(SelectedFile, _activeIndex >= 0 ? _activeIndex.ToString(CultureInfo.InvariantCulture) + "\n" : string.Empty, userOnly: true);
            _lastError = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _lastError = $"The accounts could not be saved to {DataFile}: {exception.Message}";
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void PrepareDirectory()
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Default.Warn($"Could not make {Directory} private.", exception);
        }
    }

    private void Assign(int index, string name, string? password, string listServer, bool directMode)
    {
        AccountFileFormat.Entry entry = _entries[index];
        entry.Name = name;
        entry.EncodedPassword = Encode(password);
        entry.ListServer = listServer ?? string.Empty;
        entry.DirectMode = directMode;
        Activate(index);
    }

    private void Activate(int index)
    {
        _activeIndex = index;
        _activeName = index >= 0 ? _entries[index].Name : string.Empty;
    }

    private void ActivateFirst() => Activate(_entries.Count > 0 ? 0 : -1);

    private int FindName(string name) =>
        string.IsNullOrEmpty(name) ? -1 : _entries.FindIndex(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    private byte[] Encode(string? password)
    {
        if (string.IsNullOrEmpty(password) || _key is null) return Array.Empty<byte>();
        byte[] bytes = Utf8.GetBytes(password);
        try
        {
            return AccountFileFormat.EncodePassword(bytes, _key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private string Decode(byte[] encoded)
    {
        if (encoded.Length == 0 || _key is null) return string.Empty;
        byte[]? bytes = AccountCrypto.Decrypt(encoded, _key, AccountCrypto.PasswordMagic);
        if (bytes is null) return string.Empty;
        try
        {
            return Utf8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void ClearKey()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    private static SavedAccount ToPublic(AccountFileFormat.Entry entry) => new(entry.Id, entry.Name, entry.EncodedPassword.Length != 0, entry.ListServer, entry.DirectMode);

    private void RaiseChanged()
    {
        EventHandler? handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                AppLog.Default.Error("An accounts change handler failed.", exception);
            }
        }
    }
}
