using System.Text;

namespace RemoteControl.Core;

/// <summary>How <see cref="OptionsStore.Load"/> went.</summary>
public enum OptionsLoadStatus
{
    /// <summary>The options file of the user folder was read.</summary>
    Loaded,

    /// <summary>There was no options file in the user folder; the one beside the executable was read and copied to the user folder.</summary>
    CopiedFromProgramFolder,

    /// <summary>No options file existed; the defaults were written.</summary>
    CreatedDefaults,

    /// <summary>The options file could not be read; it was moved aside (<see cref="OptionsLoadResult.MovedAsidePath"/>) and the defaults are used. Tell the user.</summary>
    UnreadableMovedAside,

    /// <summary>The options file could not be read and could not be moved aside; the defaults are used and saving is refused so the file is kept. Tell the user.</summary>
    UnreadableKept,
}

/// <summary>What <see cref="OptionsStore.Update"/> and <see cref="OptionsStore.Save"/> did.</summary>
public enum OptionsSaveStatus
{
    /// <summary>The file was written.</summary>
    Saved,

    /// <summary>Nothing changed, so nothing was written.</summary>
    Unchanged,

    /// <summary>Writing failed (<see cref="OptionsStore.LastError"/>); the values are changed in memory and the previous file is intact.</summary>
    Failed,

    /// <summary>Saving is refused because an unreadable options file is being kept (<see cref="OptionsLoadStatus.UnreadableKept"/>).</summary>
    Blocked,
}

/// <summary>The outcome of <see cref="OptionsStore.Load"/> or <see cref="OptionsStore.Reload"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Message">A sentence for the user when something went wrong, otherwise <c>null</c>.</param>
/// <param name="MovedAsidePath">Where an unreadable file was moved, otherwise <c>null</c>.</param>
public sealed record OptionsLoadResult(OptionsLoadStatus Status, string? Message = null, string? MovedAsidePath = null)
{
    /// <summary>Whether the user should be told (the file was unreadable).</summary>
    public bool NeedsAttention => Status is OptionsLoadStatus.UnreadableMovedAside or OptionsLoadStatus.UnreadableKept;
}

/// <summary>Says which options changed, who changed them and the snapshots before and after.</summary>
public sealed class OptionsChangedEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    /// <param name="source">The object that made the change (a window), or <c>null</c>.</param>
    /// <param name="keys">The changed keys.</param>
    /// <param name="previous">The snapshot before.</param>
    /// <param name="current">The snapshot after.</param>
    public OptionsChangedEventArgs(object? source, IReadOnlyList<string> keys, RcOptions previous, RcOptions current)
    {
        Source = source;
        Keys = keys;
        KeySet = new HashSet<string>(keys, StringComparer.Ordinal);
        Previous = previous;
        Current = current;
    }

    /// <summary>The object passed as <c>source</c> to <see cref="OptionsStore.Update"/>, so that a window can ignore its own changes.</summary>
    public object? Source { get; }

    /// <summary>The changed keys (see <see cref="OptionKeys"/>), in file order.</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>The changed keys as a set.</summary>
    public IReadOnlySet<string> KeySet { get; }

    /// <summary>The options before the change.</summary>
    public RcOptions Previous { get; }

    /// <summary>The options after the change.</summary>
    public RcOptions Current { get; }

    /// <summary>Whether any of <paramref name="keys"/> changed.</summary>
    /// <param name="keys">Keys from <see cref="OptionKeys"/>.</param>
    /// <returns><c>true</c> when at least one of them changed.</returns>
    public bool Contains(params string[] keys) => keys.Any(KeySet.Contains);
}

/// <summary>
/// The one options object of the program (defect D12: in the C++ every window held a copy and saved it whole, so windows
/// overwrote each other's changes). Every window reads <see cref="Current"/> and changes values through <see cref="Update"/>,
/// which applies the change to the latest snapshot, writes the file atomically and raises <see cref="Changed"/> with the keys
/// that changed. Keys the program does not know are kept and written back.
/// </summary>
/// <remarks>
/// The store is safe to call from any thread; <see cref="Changed"/> is raised on the thread that made the change, after the
/// file is written. In the program all changes come from the UI thread.
/// <para>Differences from the C++: the file is replaced atomically instead of truncated and rewritten (D-13); a value with a line
/// break is refused instead of corrupting the file; unknown keys are kept instead of dropped; an unreadable file (I/O error,
/// larger than 1 MiB, or containing NUL bytes as after a crash during a write) is moved aside instead of overwritten.</para>
/// </remarks>
public sealed class OptionsStore
{
    private readonly object _gate = new();
    private readonly AppPaths _paths;
    private readonly Func<DateTime> _clock;
    private RcOptions _current = RcOptions.Defaults;
    private IReadOnlyList<KeyValuePair<string, string>> _unknown = Array.Empty<KeyValuePair<string, string>>();
    private bool _blocked;
    private string? _lastError;

    /// <summary>Creates a store for the options file of <paramref name="paths"/>; call <see cref="Load"/> before use.</summary>
    /// <param name="paths">Where the files are.</param>
    /// <param name="clock">The clock used to name moved-aside files (tests); the local time by default.</param>
    public OptionsStore(AppPaths paths, Func<DateTime>? clock = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>Raised after options changed (by <see cref="Update"/>, <see cref="SetValue"/> or <see cref="Reload"/>).</summary>
    public event EventHandler<OptionsChangedEventArgs>? Changed;

    /// <summary>The current options. A snapshot: it never changes; read this property again to see later changes.</summary>
    public RcOptions Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>The entries of the file whose keys this version does not know, kept and written back after the known keys.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> UnknownEntries
    {
        get { lock (_gate) return _unknown; }
    }

    /// <summary>Whether saving is possible (false while an unreadable file is kept, see <see cref="OptionsLoadStatus.UnreadableKept"/>).</summary>
    public bool CanSave
    {
        get { lock (_gate) return !_blocked; }
    }

    /// <summary>The message of the last failed write, or <c>null</c>.</summary>
    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>
    /// Reads the options. The user folder's file wins; when there is none, the file beside the executable (the bundled
    /// defaults, or the C++ Windows location) is read and saved to the user folder; when neither exists, the defaults are
    /// written. A background picture that does not exist is reset to <c>rc_background.png</c> and saved, as in the C++.
    /// Does not raise <see cref="Changed"/>.
    /// </summary>
    /// <returns>What happened; show <see cref="OptionsLoadResult.Message"/> when <see cref="OptionsLoadResult.NeedsAttention"/>.</returns>
    public OptionsLoadResult Load()
    {
        lock (_gate)
        {
            _blocked = false;
            _lastError = null;
            OptionsLoadResult result;
            RcOptions options = RcOptions.Defaults;
            List<KeyValuePair<string, string>> unknown = new();
            bool save;

            ReadOutcome user = TryRead(_paths.OptionsFile, out string? userText, out string? userError);
            if (user == ReadOutcome.Read)
            {
                options = OptionsFileFormat.Apply(RcOptions.Defaults, OptionsFileFormat.Parse(userText!), out unknown);
                result = new OptionsLoadResult(OptionsLoadStatus.Loaded);
                save = false;
            }
            else
            {
                if (user == ReadOutcome.Unreadable)
                {
                    string? aside = null;
                    try
                    {
                        aside = AtomicFile.MoveAside(_paths.OptionsFile, "unreadable", _clock());
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        _blocked = true;
                    }
                    result = _blocked
                        ? new OptionsLoadResult(OptionsLoadStatus.UnreadableKept, $"The options file {_paths.OptionsFile} could not be read ({userError}) and could not be moved aside. The defaults are used and nothing is saved until the file is fixed or removed.")
                        : new OptionsLoadResult(OptionsLoadStatus.UnreadableMovedAside, $"The options file could not be read ({userError}). It was kept as {aside} and the defaults are used.", aside);
                }
                else result = new OptionsLoadResult(OptionsLoadStatus.CreatedDefaults);

                if (TryRead(_paths.BundledOptionsFile, out string? bundledText, out _) == ReadOutcome.Read)
                {
                    options = OptionsFileFormat.Apply(RcOptions.Defaults, OptionsFileFormat.Parse(bundledText!), out unknown);
                    if (result.Status == OptionsLoadStatus.CreatedDefaults) result = new OptionsLoadResult(OptionsLoadStatus.CopiedFromProgramFolder);
                }
                save = true;
            }

            string background = _paths.BackgroundFile(options);
            if (options.Background.Length == 0 || !File.Exists(background))
            {
                options = options with { Background = RcOptions.Defaults.Background };
                save = true;
            }

            _current = options;
            _unknown = unknown;
            if (save) SaveLocked();
            return result;
        }
    }

    /// <summary>
    /// Reads the file again (the C++ re-read the MCP approvals when the options window opened) and raises <see cref="Changed"/>
    /// for the keys that differ. An unreadable or missing file changes nothing.
    /// </summary>
    /// <param name="source">Passed on as <see cref="OptionsChangedEventArgs.Source"/>.</param>
    /// <returns><c>true</c> when the file was read.</returns>
    public bool Reload(object? source = null)
    {
        OptionsChangedEventArgs? args;
        lock (_gate)
        {
            if (TryRead(_paths.OptionsFile, out string? text, out _) != ReadOutcome.Read) return false;
            RcOptions previous = _current;
            RcOptions next = OptionsFileFormat.Apply(RcOptions.Defaults, OptionsFileFormat.Parse(text!), out List<KeyValuePair<string, string>> unknown);
            if (next.Background.Length == 0) next = next with { Background = RcOptions.Defaults.Background };
            _current = next;
            _unknown = unknown;
            IReadOnlyList<string> keys = RcOptions.ChangedKeys(previous, next);
            args = keys.Count == 0 ? null : new OptionsChangedEventArgs(source, keys, previous, next);
        }
        if (args is not null) RaiseChanged(args);
        return true;
    }

    /// <summary>
    /// Changes options: <paramref name="change"/> receives the latest snapshot and returns the new one
    /// (<c>o =&gt; o with { Theme = "nord" }</c>). The file is written when something changed, then <see cref="Changed"/> is raised.
    /// </summary>
    /// <param name="change">Builds the new snapshot from the current one; it must not have side effects (it runs under the store's lock).</param>
    /// <param name="source">The window or object making the change, passed on in <see cref="OptionsChangedEventArgs.Source"/>.</param>
    /// <returns>Whether the file was written.</returns>
    /// <exception cref="ArgumentException">A value contains a line break or NUL; nothing is changed.</exception>
    public OptionsSaveStatus Update(Func<RcOptions, RcOptions> change, object? source = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        OptionsChangedEventArgs args;
        OptionsSaveStatus status;
        lock (_gate)
        {
            RcOptions previous = _current;
            RcOptions next = change(previous) ?? throw new ArgumentException("The change returned no options.", nameof(change));
            foreach (KeyValuePair<string, string> entry in next.ToEntries())
                if (!OptionsFileFormat.IsWritable(entry.Value)) throw new ArgumentException($"The value of '{entry.Key}' contains a line break, which the options file cannot hold.", nameof(change));
            IReadOnlyList<string> keys = RcOptions.ChangedKeys(previous, next);
            if (keys.Count == 0) return OptionsSaveStatus.Unchanged;
            _current = next;
            status = SaveLocked();
            args = new OptionsChangedEventArgs(source, keys, previous, next);
        }
        RaiseChanged(args);
        return status;
    }

    /// <summary>Sets one key from its file text (as the file would), for code that works with keys, such as MCP tools.</summary>
    /// <param name="key">One of <see cref="RcOptions.Keys"/>.</param>
    /// <param name="value">The value text.</param>
    /// <param name="source">Passed on as <see cref="OptionsChangedEventArgs.Source"/>.</param>
    /// <returns>Whether the file was written.</returns>
    /// <exception cref="ArgumentException">The key is unknown or the value contains a line break.</exception>
    public OptionsSaveStatus SetValue(string key, string value, object? source = null)
    {
        if (!RcOptions.IsKnownKey(key)) throw new ArgumentException($"Unknown option key '{key}'.", nameof(key));
        if (value is not null && !OptionsFileFormat.IsWritable(value)) throw new ArgumentException($"The value of '{key}' contains a line break, which the options file cannot hold.", nameof(value));
        return Update(options => options.WithValue(key, value ?? string.Empty), source);
    }

    /// <summary>Writes the current options now (normally <see cref="Update"/> does this).</summary>
    /// <returns>Whether the file was written.</returns>
    public OptionsSaveStatus Save()
    {
        lock (_gate) return SaveLocked();
    }

    /// <summary>Calls every handler; one that throws is logged and does not keep the others from running.</summary>
    private void RaiseChanged(OptionsChangedEventArgs args)
    {
        EventHandler<OptionsChangedEventArgs>? handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler<OptionsChangedEventArgs> handler in handlers.GetInvocationList().Cast<EventHandler<OptionsChangedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                AppLog.Default.Error("An options change handler failed.", exception);
            }
        }
    }

    private OptionsSaveStatus SaveLocked()
    {
        if (_blocked) return OptionsSaveStatus.Blocked;
        try
        {
            AtomicFile.WriteAllText(_paths.OptionsFile, OptionsFileFormat.Serialize(_current, _unknown));
            _lastError = null;
            return OptionsSaveStatus.Saved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _lastError = $"The options could not be saved to {_paths.OptionsFile}: {exception.Message}";
            return OptionsSaveStatus.Failed;
        }
    }

    private enum ReadOutcome
    {
        Missing,
        Read,
        Unreadable,
    }

    private static ReadOutcome TryRead(string path, out string? text, out string? error)
    {
        text = null;
        error = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return ReadOutcome.Missing;
            if (info.Length == 0) return ReadOutcome.Missing;
            if (info.Length > OptionsFileFormat.MaximumFileSize) { error = $"it is {info.Length} bytes, larger than an options file can be"; return ReadOutcome.Unreadable; }
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > OptionsFileFormat.MaximumFileSize) { error = "it is larger than an options file can be"; return ReadOutcome.Unreadable; }
            if (Array.IndexOf(bytes, (byte)0) >= 0) { error = "it contains NUL bytes, as a file damaged during a write does"; return ReadOutcome.Unreadable; }
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            text = new UTF8Encoding(false, false).GetString(bytes, start, bytes.Length - start);
            return ReadOutcome.Read;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return ReadOutcome.Unreadable;
        }
    }
}
