using System.Globalization;
using System.Text;

namespace RemoteControl.Core;

/// <summary>What a change of <see cref="ListServerProfileStore"/> did.</summary>
public enum ProfileChangeStatus
{
    /// <summary>The change was made and written.</summary>
    Saved,

    /// <summary>The change was made in memory but writing failed (<see cref="ListServerProfileStore.LastError"/>).</summary>
    Failed,

    /// <summary>Refused: the name or host is empty or holds a tab or line break, or the port is not 1 to 65535.</summary>
    Invalid,

    /// <summary>Refused: another profile already has that name.</summary>
    DuplicateName,

    /// <summary>Refused: the built-in profile cannot be edited or removed.</summary>
    BuiltIn,

    /// <summary>Refused: no profile has that name.</summary>
    NotFound,

    /// <summary>Refused: the profiles file could not be read and is kept, so nothing is saved.</summary>
    Refused,
}

/// <summary>How <see cref="ListServerProfileStore.Load"/> went.</summary>
public enum ProfileLoadStatus
{
    /// <summary>The file was read.</summary>
    Loaded,

    /// <summary>There is no file yet; only the built-in profile exists.</summary>
    NoFile,

    /// <summary>The file could not be read and was moved aside (<see cref="ProfileLoadResult.MovedAsidePath"/>).</summary>
    UnreadableMovedAside,

    /// <summary>The file could not be read or moved; it is kept and saving is refused.</summary>
    UnreadableKept,
}

/// <summary>The outcome of <see cref="ListServerProfileStore.Load"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Message">A sentence for the user when something went wrong.</param>
/// <param name="MovedAsidePath">Where an unreadable file was moved.</param>
public sealed record ProfileLoadResult(ProfileLoadStatus Status, string? Message = null, string? MovedAsidePath = null)
{
    /// <summary>Whether the user should be told.</summary>
    public bool NeedsAttention => Status is ProfileLoadStatus.UnreadableMovedAside or ProfileLoadStatus.UnreadableKept;
}

/// <summary>
/// The list-server profiles of <c>listservers.conf</c> (<c>TListServerProfiles.cpp</c>): UTF-8, one <c>name\thost\tport</c> per line;
/// a legacy <c>host:port</c> line gives a profile named after its host; lines with a port outside 1 to 65535 are skipped.
/// </summary>
/// <remarks>
/// Differences from the C++: the built-in profile is <see cref="ListServerProfile.Default"/> (Alpaxis); it is always first, is not
/// written to the file, and cannot be edited or removed. The C++ inserted a "Retail" (<c>listserver.graalonline.com</c>) profile by
/// itself and renamed "Official" to "Retail"; the C# never does, and the profiles in a user's file (including a "Retail" line the C++
/// wrote) stay exactly as they are. A line identical to the built-in profile is not shown twice. The file is replaced atomically,
/// a name or host with a tab or line break is refused, and an unreadable file is moved aside instead of overwritten.
/// </remarks>
public sealed class ListServerProfileStore
{
    private const int MaximumFileSize = 1024 * 1024;

    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private List<ListServerProfile> _userProfiles = new();
    private bool _blocked;
    private string? _lastError;

    /// <summary>Creates a store for <paramref name="path"/>; call <see cref="Load"/> before use.</summary>
    /// <param name="path">The profiles file.</param>
    /// <param name="clock">The clock used to name moved-aside files.</param>
    public ListServerProfileStore(string path, Func<DateTime>? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FilePath = Path.GetFullPath(path);
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>Creates a store for <see cref="AppPaths.ListServersFile"/>.</summary>
    /// <param name="paths">Where the files are.</param>
    public ListServerProfileStore(AppPaths paths) : this((paths ?? throw new ArgumentNullException(nameof(paths))).ListServersFile)
    {
    }

    /// <summary>Raised after the profiles changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The profiles file.</summary>
    public string FilePath { get; }

    /// <summary>Every profile: the built-in one first, then the user's in file order.</summary>
    public IReadOnlyList<ListServerProfile> Profiles
    {
        get
        {
            lock (_gate)
            {
                var all = new List<ListServerProfile>(_userProfiles.Count + 1) { ListServerProfile.Default };
                all.AddRange(_userProfiles);
                return all;
            }
        }
    }

    /// <summary>The user's own profiles, in file order.</summary>
    public IReadOnlyList<ListServerProfile> UserProfiles
    {
        get { lock (_gate) return _userProfiles.ToArray(); }
    }

    /// <summary>Whether changes can be saved.</summary>
    public bool CanSave
    {
        get { lock (_gate) return !_blocked; }
    }

    /// <summary>The message of the last failed write, or <c>null</c>.</summary>
    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>Reads the file.</summary>
    /// <returns>What happened.</returns>
    public ProfileLoadResult Load()
    {
        ProfileLoadResult result;
        lock (_gate)
        {
            _blocked = false;
            _lastError = null;
            _userProfiles = new List<ListServerProfile>();
            string? text = null;
            string? error = null;
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists) result = new ProfileLoadResult(ProfileLoadStatus.NoFile);
                else if (info.Length > MaximumFileSize) { error = "it is larger than a profiles file can be"; result = null!; }
                else
                {
                    byte[] bytes = File.ReadAllBytes(FilePath);
                    if (Array.IndexOf(bytes, (byte)0) >= 0) { error = "it contains NUL bytes, as a file damaged during a write does"; result = null!; }
                    else
                    {
                        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                        text = new UTF8Encoding(false, false).GetString(bytes, start, bytes.Length - start);
                        result = new ProfileLoadResult(ProfileLoadStatus.Loaded);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = exception.Message;
                result = null!;
            }

            if (error is not null)
            {
                try
                {
                    string? aside = AtomicFile.MoveAside(FilePath, "unreadable", _clock());
                    result = new ProfileLoadResult(ProfileLoadStatus.UnreadableMovedAside, $"The list-server profiles could not be read ({error}). The file was kept as {aside}.", aside);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _blocked = true;
                    result = new ProfileLoadResult(ProfileLoadStatus.UnreadableKept, $"The list-server profiles {FilePath} could not be read ({error}) and are kept; changes are not saved.");
                }
            }
            else if (text is not null) _userProfiles = Parse(text);
        }
        RaiseChanged();
        return result;
    }

    /// <summary>The profile named <paramref name="name"/> (ignoring case), or <c>null</c>.</summary>
    /// <param name="name">A profile name.</param>
    /// <returns>The profile.</returns>
    public ListServerProfile? Find(string name) =>
        Profiles.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where an account's association leads; the built-in profile when it matches nothing (see <see cref="ListServerAssociation.Resolve"/>).</summary>
    /// <param name="association">The association stored with the account.</param>
    /// <returns>Where to connect.</returns>
    public ListServerEndpoint Resolve(string? association) => ListServerAssociation.Resolve(association, Profiles);

    /// <summary>Adds a user profile at the end.</summary>
    /// <param name="profile">The new profile.</param>
    /// <returns>What happened.</returns>
    public ProfileChangeStatus Add(ListServerProfile profile) =>
        Mutate(() =>
        {
            ProfileChangeStatus check = Validate(profile, null);
            if (check != ProfileChangeStatus.Saved) return check;
            _userProfiles.Add(profile);
            return ProfileChangeStatus.Saved;
        });

    /// <summary>Replaces the user profile named <paramref name="name"/> (ignoring case).</summary>
    /// <param name="name">The current name.</param>
    /// <param name="profile">The new values.</param>
    /// <returns>What happened.</returns>
    public ProfileChangeStatus Update(string name, ListServerProfile profile) =>
        Mutate(() =>
        {
            if (string.Equals(name, ListServerProfile.DefaultName, StringComparison.OrdinalIgnoreCase)) return ProfileChangeStatus.BuiltIn;
            int index = _userProfiles.FindIndex(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return ProfileChangeStatus.NotFound;
            ProfileChangeStatus check = Validate(profile, index);
            if (check != ProfileChangeStatus.Saved) return check;
            _userProfiles[index] = profile;
            return ProfileChangeStatus.Saved;
        });

    /// <summary>Removes the user profile named <paramref name="name"/> (ignoring case).</summary>
    /// <param name="name">The name.</param>
    /// <returns>What happened.</returns>
    public ProfileChangeStatus Remove(string name) =>
        Mutate(() =>
        {
            if (string.Equals(name, ListServerProfile.DefaultName, StringComparison.OrdinalIgnoreCase)) return ProfileChangeStatus.BuiltIn;
            int index = _userProfiles.FindIndex(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return ProfileChangeStatus.NotFound;
            _userProfiles.RemoveAt(index);
            return ProfileChangeStatus.Saved;
        });

    /// <summary>Moves a user profile to another position among the user profiles (the built-in one stays first).</summary>
    /// <param name="name">The profile name.</param>
    /// <param name="newIndex">The new position among <see cref="UserProfiles"/>.</param>
    /// <returns>What happened.</returns>
    public ProfileChangeStatus Move(string name, int newIndex) =>
        Mutate(() =>
        {
            if (string.Equals(name, ListServerProfile.DefaultName, StringComparison.OrdinalIgnoreCase)) return ProfileChangeStatus.BuiltIn;
            int index = _userProfiles.FindIndex(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return ProfileChangeStatus.NotFound;
            ListServerProfile profile = _userProfiles[index];
            _userProfiles.RemoveAt(index);
            _userProfiles.Insert(Math.Clamp(newIndex, 0, _userProfiles.Count), profile);
            return ProfileChangeStatus.Saved;
        });

    /// <summary>
    /// Reads the profile lines the way the C++ did: <c>name\thost\tport</c>, or legacy <c>host:port</c> (name = host). The port is read
    /// like <c>atoi</c> (leading digits). A line identical to the built-in profile is skipped.
    /// </summary>
    /// <param name="text">The file text.</param>
    /// <returns>The user profiles.</returns>
    public static List<ListServerProfile> Parse(string text)
    {
        var profiles = new List<ListServerProfile>();
        foreach (string rawLine in (text ?? string.Empty).Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            ListServerProfile? profile = null;
            int first = line.IndexOf('\t');
            int second = first < 0 ? -1 : line.IndexOf('\t', first + 1);
            if (first >= 0 && second >= 0)
            {
                int port = Atoi(line[(second + 1)..]);
                if (ListServerProfile.IsValidPort(port)) profile = new ListServerProfile(line[..first], line[(first + 1)..second], port);
            }
            else
            {
                int separator = line.LastIndexOf(':');
                if (separator < 0) continue;
                int port = Atoi(line[(separator + 1)..]);
                string host = line[..separator];
                if (host.Length != 0 && ListServerProfile.IsValidPort(port)) profile = new ListServerProfile(host, host, port);
            }
            if (profile is null || IsBuiltInLine(profile)) continue;
            profiles.Add(profile);
        }
        return profiles;
    }

    /// <summary>The file text of the user profiles, one <c>name\thost\tport</c> per line.</summary>
    /// <param name="profiles">The user profiles.</param>
    /// <returns>The text.</returns>
    public static string Serialize(IEnumerable<ListServerProfile> profiles)
    {
        var text = new StringBuilder();
        foreach (ListServerProfile profile in profiles)
            text.Append(profile.Name).Append('\t').Append(profile.Host).Append('\t').Append(profile.Port.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return text.ToString();
    }

    private static bool IsBuiltInLine(ListServerProfile profile) =>
        string.Equals(profile.Name, ListServerProfile.DefaultName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(profile.Host, ListServerProfile.DefaultHost, StringComparison.OrdinalIgnoreCase)
        && profile.Port == ListServerProfile.DefaultPort;

    private static int Atoi(string text)
    {
        int index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        bool negative = false;
        if (index < text.Length && text[index] is '+' or '-')
        {
            negative = text[index] == '-';
            index++;
        }
        long value = 0;
        while (index < text.Length && char.IsAsciiDigit(text[index]) && value <= 1_000_000)
        {
            value = value * 10 + (text[index] - '0');
            index++;
        }
        return (int)(negative ? -value : value);
    }

    private ProfileChangeStatus Validate(ListServerProfile? profile, int? replacing)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Host) || !ListServerProfile.IsValidPort(profile.Port)) return ProfileChangeStatus.Invalid;
        if ((profile.Name + profile.Host).IndexOfAny(['\t', '\n', '\r', '\0']) >= 0) return ProfileChangeStatus.Invalid;
        if (string.Equals(profile.Name, ListServerProfile.DefaultName, StringComparison.OrdinalIgnoreCase)) return ProfileChangeStatus.DuplicateName;
        for (int index = 0; index < _userProfiles.Count; index++)
            if (index != replacing && string.Equals(_userProfiles[index].Name, profile.Name, StringComparison.OrdinalIgnoreCase)) return ProfileChangeStatus.DuplicateName;
        return ProfileChangeStatus.Saved;
    }

    private ProfileChangeStatus Mutate(Func<ProfileChangeStatus> change)
    {
        ProfileChangeStatus status;
        lock (_gate)
        {
            if (_blocked) return ProfileChangeStatus.Refused;
            List<ListServerProfile> before = _userProfiles.ToList();
            status = change();
            if (status != ProfileChangeStatus.Saved)
            {
                _userProfiles = before;
                return status;
            }
            try
            {
                AtomicFile.WriteAllText(FilePath, Serialize(_userProfiles));
                _lastError = null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _lastError = $"The list-server profiles could not be saved to {FilePath}: {exception.Message}";
                status = ProfileChangeStatus.Failed;
            }
        }
        RaiseChanged();
        return status;
    }

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
                AppLog.Default.Error("A list-server profiles change handler failed.", exception);
            }
        }
    }
}
