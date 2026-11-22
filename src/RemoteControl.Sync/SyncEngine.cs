using System.Collections.Immutable;
using System.Globalization;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Sync;

/// <summary>
/// The synchronisation of one server with a workspace folder (<c>TSyncManager</c> without its window): scripts through the NC calls, files
/// through <see cref="IRcSession.SyncFileBrowser"/>, the server options, flags and folder configuration, and the Git backups.
/// </summary>
/// <remarks>
/// <para>All state belongs to one context (the session's, normally the UI thread): commands may be called from any thread and are carried
/// over to it; every event is raised on it. File reads, hashing, directory walks and git run on background tasks with cancellation.</para>
/// <para>Usage: create one per server window, <see cref="AttachAsync"/> the session when it connected, show <see cref="Snapshot"/> and update it
/// on <see cref="StateChanged"/>; call <see cref="Start"/>, <see cref="Stop"/>, <see cref="SyncNow"/>, <see cref="UpdateSettings(SyncSettings)"/> from the
/// buttons. When the session disconnects the engine detaches itself and pauses; attach the next session to continue.</para>
/// <para>Differences from the C++: the last synchronised hash of every item is kept in the manifest, so an edit made while the program was
/// closed is uploaded or reported, never overwritten (D-10); an item changed on both sides is a conflict whose server copy is kept in
/// <c>.graal-sync/conflicts/</c>; scripts are taken from <see cref="IRcSession.ScriptReceived"/> instead of copying every cache every second, and
/// the workspace is read only where the file watcher saw a change, plus a full rescan every <see cref="SyncEngineOptions.RescanInterval"/>
/// (D-09); the pull does not ask for every NPC's flags (D-13); git runs in the background with a time limit (D-08) and a failing
/// "pull before uploads" is reported once a minute in the log (D-16); a disconnect stops the engine and drops the session (D-03).</para>
/// </remarks>
public sealed partial class SyncEngine : IDisposable, IAsyncDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly SyncEngineOptions _options;
    private readonly SynchronizationContext _context;
    private readonly PumpSynchronizationContext? _ownContext;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _browserGate = new(1, 1);
    private readonly LinkedList<string> _log = new();
    private readonly List<TaskCompletionSource> _idleWaiters = new();

    // ---- Attached session ------------------------------------------------------------------------------------------------------
    private IRcSession? _session;
    private Subscription? _subscription;
    private int _generation;
    private string _serverName = string.Empty;
    private bool _serverLoaded;
    private GitCredentialStore? _credentials;
    private bool _hasGitKey;
    private bool _hasGitToken;

    // ---- Settings and manifest ---------------------------------------------------------------------------------------------------
    private SyncSettings _settings = SyncSettings.Default;
    private ExclusionList _exclusions = new(null);
    private string? _settingsPath;
    private bool _settingsWritable = true;
    private Task _settingsSaveChain = Task.CompletedTask;
    private readonly Dictionary<string, ScriptItem> _scripts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileItem> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<SyncConfigKind, ConfigItem> _configs = new();
    private readonly Dictionary<string, TrackedItem> _byPath = new(PathComparer);
    private readonly Dictionary<string, long> _scriptModifiedTimes = new(StringComparer.Ordinal);
    private bool _manifestDirty;
    private bool _manifestWritable = true;
    private DateTime _manifestDirtySince;
    private Task _manifestSaveChain = Task.CompletedTask;

    // ---- Run state ---------------------------------------------------------------------------------------------------------------
    private RunContext? _run;
    private bool _enabled;
    private bool _oneShot;
    private bool _wantRunning;
    private bool _pullRequested;
    private bool _pullForced;
    private bool _pushRequested;
    private bool _pushForced;
    private bool _downloadsForced;
    private bool _fullRescan;
    private bool _waitingForNc;
    private DateTime _nextPull;
    private DateTime _nextRescan;
    private readonly HashSet<string> _dirtyPaths = new(PathComparer);
    /// <summary>The files the watcher reported, with the time of their last event: they become dirty once they settled (<see cref="SyncEngineOptions.WatchSettleDelay"/>).</summary>
    private readonly Dictionary<string, DateTime> _settlingPaths = new(PathComparer);
    private readonly HashSet<string> _missingWatch = new(StringComparer.Ordinal);
    private readonly HashSet<string> _confirmedDeletions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptReceivedEventArgs> _scriptArrivals = new(StringComparer.Ordinal);
    private readonly Queue<string> _scriptArrivalOrder = new();
    private readonly Dictionary<SyncConfigKind, string> _configArrivals = new();
    private readonly HashSet<string> _expectedScripts = new(StringComparer.Ordinal);
    private readonly HashSet<SyncConfigKind> _expectedConfig = new();
    private DateTime _lastArrival;
    private TaskCompletionSource? _wake;
    private FileSystemWatcher? _watcher;
    private IReadOnlyList<string>? _passRoots;

    /// <summary>The folders (by <c>FolderKey</c>) the server said it does not open for this account, during the pass that runs.</summary>
    private readonly HashSet<string> _refusedFolders = new(StringComparer.Ordinal);
    private DateTime _gitPullRetryAt;
    private DateTime _gitPulledAt;
    private bool _autoCommitRunning;
    private bool _autoCommitAgain;

    // ---- Progress ----------------------------------------------------------------------------------------------------------------
    private string _phase = "Stopped";
    private int _workDone;
    private int _workTotal;
    private DateTime _passStarted;
    private int _downloaded;
    private int _uploaded;
    private int _downloadsThisPass;
    private bool _progressUnseen;
    private bool _fileSyncActive;
    private int _queuedFolders;
    private int _queuedDownloads;
    private string? _currentItem;
    private DateTime _currentStarted;
    private TransferProgress? _currentProgress;
    private readonly Dictionary<string, SyncItemStatus> _problems = new(StringComparer.Ordinal);
    private long _sequence;
    private SyncSnapshot _snapshot = new();
    private bool _snapshotDirty;
    private bool _snapshotPosted;
    private ImmutableArray<string> _logArray = ImmutableArray<string>.Empty;
    private bool _disposed;

    /// <summary>Creates an engine. It does nothing until a session is attached.</summary>
    /// <param name="options">The options.</param>
    public SyncEngine(SyncEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.UserDirectory);
        _options = options;
        SynchronizationContext? context = options.Context ?? SynchronizationContext.Current;
        if (context is null)
        {
            _ownContext = new PumpSynchronizationContext("Sync engine");
            context = _ownContext;
        }

        _context = context;
    }

    // ---- Public state and events ---------------------------------------------------------------------------------------------------

    /// <summary>The state to show. On the engine's context it is current; elsewhere it is the last snapshot raised.</summary>
    public SyncSnapshot Snapshot => OnContext ? BuildSnapshot(_sequence) : Volatile.Read(ref _snapshot);

    /// <summary>The settings (the last ones set on the context).</summary>
    public SyncSettings Settings => _settings;

    /// <summary>The server the engine is set up for.</summary>
    public string ServerName => _serverName;

    /// <summary>The state changed (raised at most once per posted callback, in order, on the context).</summary>
    public event EventHandler<SyncSnapshotEventArgs>? StateChanged;

    /// <summary>A log line was added.</summary>
    public event EventHandler<SyncLogEventArgs>? LogAdded;

    /// <summary>An item changed its state (conflict, local edit kept, failure, resolved ...).</summary>
    public event EventHandler<SyncItemEventArgs>? ItemStatusChanged;

    /// <summary>The configuration pull saw a server text that differs from the one last synchronised.</summary>
    public event EventHandler<ServerConfigurationChangedEventArgs>? ServerConfigurationChanged;

    /// <summary>A git command started by the engine (a button or the automatic commit) finished.</summary>
    public event EventHandler<SyncGitEventArgs>? GitCompleted;

    /// <summary>
    /// Whether the engine asked for this server text and waits for it: an editor that receives the session's matching event meanwhile may
    /// treat it as the synchroniser's pull rather than an answer to its own request. Call it on the engine's context.
    /// </summary>
    /// <param name="kind">The text.</param>
    /// <returns>Whether a request of the engine is outstanding.</returns>
    public bool ExpectsConfiguration(SyncConfigKind kind) => _expectedConfig.Contains(kind);

    // ---- Session ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Attaches a connected session. A session already attached is detached first. When the server differs from the last one, its settings
    /// and the manifest of its workspace are read (in the background). The engine starts when it ran before the last disconnect or
    /// "start automatically" is on.
    /// </summary>
    /// <param name="session">The session (its events must arrive on the engine's context).</param>
    /// <param name="serverName">The server name (names the settings file).</param>
    /// <returns>Completes when attached.</returns>
    public Task AttachAsync(IRcSession session, string serverName)
    {
        ArgumentNullException.ThrowIfNull(session);
        return InvokeAsync(async () =>
        {
            if (_disposed) return;
            DetachCore("replaced by a new session");
            int generation = ++_generation;
            string name = serverName ?? string.Empty;
            if (!_serverLoaded || name != _serverName)
            {
                await LoadServerAsync(name, generation);
                if (generation != _generation || _disposed) return;
            }

            if (!session.IsConnected)
            {
                Log("The session is not connected; synchronization waits for the next connection.");
                Notify();
                return;
            }

            _session = session;
            _subscription = new Subscription(this, session);
            _phase = "Stopped";
            if (_wantRunning || _settings.AutoStart) StartCore();
            Notify();
        });
    }

    /// <summary>Detaches the session: running work stops, the manifest is saved, and the engine keeps no reference to the session.</summary>
    public void Detach() => Dispatch(() => DetachCore("detached"));

    // ---- Commands -----------------------------------------------------------------------------------------------------------------

    /// <summary>"Start": synchronises continuously (a full pull now and every <see cref="SyncEngineOptions.PullInterval"/>).</summary>
    public void Start() => Dispatch(StartCore);

    /// <summary>"Stop".</summary>
    public void Stop() => Dispatch(() => StopCore(log: true));

    /// <summary>"Sync now": one pass in the configured direction; when not running, the engine stops again afterwards.</summary>
    public void SyncNow() => Dispatch(() =>
    {
        if (!CanRun()) return;
        if (!_enabled && !_oneShot)
        {
            _oneShot = true;
            Log("One-shot synchronization started");
        }

        _pullRequested = true;
        _pushRequested = true;
        _fullRescan = true;
        EnsureLoop();
    });

    /// <summary>"Pull": downloads server changes now, also when the direction is upload only (a local edit is still never overwritten).</summary>
    public void Pull() => Dispatch(() =>
    {
        if (!CanRun()) return;
        if (!_enabled) _oneShot = true;
        _pullRequested = true;
        _pullForced = true;
        EnsureLoop();
    });

    /// <summary>"Push": uploads local edits now, also when the direction is download only (conflicts are not uploaded).</summary>
    public void Push() => Dispatch(() =>
    {
        if (!CanRun()) return;
        if (!_enabled) _oneShot = true;
        _pushRequested = true;
        _pushForced = true;
        _fullRescan = true;
        EnsureLoop();
    });

    /// <summary>
    /// Changes the settings and saves them. As in the C++: changing the direction restarts the progress and pulls; turning file sync or the
    /// configuration on while running pulls them; a new workspace stops the engine and reads that workspace's manifest.
    /// </summary>
    /// <param name="settings">The new settings.</param>
    public void UpdateSettings(SyncSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Dispatch(() => ApplySettings(settings.Normalized()));
    }

    /// <summary>Changes the settings with a function of the current ones.</summary>
    /// <param name="change">The change.</param>
    public void UpdateSettings(Func<SyncSettings, SyncSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Dispatch(() => ApplySettings(change(_settings).Normalized()));
    }

    /// <summary>The window showed the progress (clears <see cref="SyncSnapshot.ProgressUnseen"/>).</summary>
    public void MarkProgressSeen() => Dispatch(() =>
    {
        _progressUnseen = false;
        Notify();
    });

    /// <summary>Deletes on the server the items held as <see cref="SyncItemState.PendingDeletion"/> (their local files are still missing).</summary>
    public void ConfirmPendingDeletions() => Dispatch(() =>
    {
        foreach (TrackedItem item in AllItems().Where(item => item.State == SyncItemState.PendingDeletion).ToArray())
        {
            _confirmedDeletions.Add(item.Key);
            _dirtyPaths.Add(item.Path);
        }

        if (_confirmedDeletions.Count == 0) return;
        if (!_enabled) _oneShot = true;
        _pushRequested = true;
        EnsureLoop();
        Wake();
    });

    /// <summary>Keeps the server copies of the items held for deletion; the local files count as missing (they come back with a download).</summary>
    public void DiscardPendingDeletions() => Dispatch(() =>
    {
        foreach (TrackedItem item in AllItems().Where(item => item.State == SyncItemState.PendingDeletion).ToArray())
            SetState(item, SyncItemState.MissingLocally, "Deleted locally; the server copy is kept.");
        Notify();
    });

    /// <summary>Resolves a conflict.</summary>
    /// <param name="key">The item key (<see cref="SyncItemStatus.Key"/>).</param>
    /// <param name="resolution">Which side wins.</param>
    /// <returns>Completes when done.</returns>
    public Task ResolveConflictAsync(string key, ConflictResolution resolution) => InvokeAsync(() => ResolveConflictCoreAsync(key, resolution));

    /// <summary>The status of every tracked item.</summary>
    /// <returns>The statuses.</returns>
    public Task<IReadOnlyList<SyncItemStatus>> GetItemStatusesAsync() =>
        InvokeAsync(() => Task.FromResult<IReadOnlyList<SyncItemStatus>>(AllItems().Select(item => item.Status()).ToArray()));

    /// <summary>Completes when the engine has nothing left to do right now (or is not running).</summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Completes when idle.</returns>
    public Task WhenIdleAsync(CancellationToken cancellationToken = default) => InvokeAsync(() =>
    {
        if (_run is null || IsIdle()) return Task.CompletedTask;
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _idleWaiters.Add(waiter);
        return waiter.Task.WaitAsync(cancellationToken);
    });

    // ---- Disposal -----------------------------------------------------------------------------------------------------------------

    /// <summary>Stops, detaches and saves; the engine cannot be used afterwards.</summary>
    /// <returns>Completes when the files are written.</returns>
    public async ValueTask DisposeAsync()
    {
        await InvokeAsync(() =>
        {
            if (_disposed) return Task.CompletedTask;
            _disposed = true;
            DetachCore("closed");
            _enabled = _oneShot = _wantRunning = false;
            _lifetime.Cancel();
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        try
        {
            await _manifestSaveChain.ConfigureAwait(false);
            await _settingsSaveChain.ConfigureAwait(false);
        }
        finally
        {
            _ownContext?.Dispose();
        }
    }

    /// <summary>Stops and detaches; files are still written in the background.</summary>
    public void Dispose()
    {
        if (_ownContext is not null)
        {
            _ = DisposeAsync().AsTask();
            return;
        }

        Dispatch(() =>
        {
            if (_disposed) return;
            _disposed = true;
            DetachCore("closed");
            _enabled = _oneShot = _wantRunning = false;
            _lifetime.Cancel();
        });
    }

    // ---- Test helpers ---------------------------------------------------------------------------------------------------------------

    /// <summary>Saves the manifest now and completes when the pending writes are done.</summary>
    internal Task FlushAsync() => InvokeAsync(async () =>
    {
        SaveManifestNow();
        await _manifestSaveChain;
        await _settingsSaveChain;
    });

    /// <summary>Runs an action on the engine's context (tests raise the fake session's events there).</summary>
    internal Task RunOnContextAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return Task.CompletedTask;
    });

    // ---- Context helpers ------------------------------------------------------------------------------------------------------------

    private bool OnContext => SynchronizationContext.Current == _context;

    private void Dispatch(Action action)
    {
        if (OnContext)
        {
            Guard(action);
            return;
        }

        _context.Post(static state => ((Action)state!)(), (Action)(() => Guard(action)));
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            ReportInternalError(exception);
        }
    }

    private void ReportInternalError(Exception exception)
    {
        AppLog.Default.Error("The sync engine failed.", exception);
        try
        {
            Log("Internal error: " + exception.Message);
        }
        catch (Exception logging)
        {
            AppLog.Default.Error("The sync engine could not log an error.", logging);
        }
    }

    private Task InvokeAsync(Func<Task> work) => InvokeAsync(async () =>
    {
        await work();
        return true;
    });

    private Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (OnContext) _ = RunAndComplete(work, completion);
        else _context.Post(_ => _ = RunAndComplete(work, completion), null);
        return completion.Task;
    }

    private async Task RunAndComplete<T>(Func<Task<T>> work, TaskCompletionSource<T> completion)
    {
        try
        {
            completion.TrySetResult(await work());
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            ReportInternalError(exception);
            completion.TrySetException(exception);
        }
    }

    private void Wake() => _wake?.TrySetResult();

    // ---- Attach, detach, load -------------------------------------------------------------------------------------------------------

    private void DetachCore(string reason)
    {
        if (_session is null) return;
        bool wasActive = _enabled || _oneShot;
        _subscription?.Dispose();
        _subscription = null;
        _session = null;
        _generation++;
        CancelRun();
        StopWatcher();
        _enabled = false;
        _oneShot = false;
        ClearPassState();
        SaveManifestNow();
        _phase = wasActive ? "Paused: " + reason : "Stopped";
        if (wasActive) Log("Synchronization paused (" + reason + ")");
        Notify();
    }

    private async Task LoadServerAsync(string serverName, int generation)
    {
        StopCore(log: false);
        SaveManifestNow();
        ResetServerState();
        _serverName = serverName;
        _serverLoaded = true;
        _settingsPath = SyncSettingsFile.PathFor(_options.UserDirectory, serverName);
        _credentials = new GitCredentialStore(_options.UserDirectory, serverName);
        string settingsPath = _settingsPath;
        GitCredentialStore credentials = _credentials;
        (SyncSettingsLoadResult settings, bool hasKey, bool hasToken) = await Task.Run(() =>
            (SyncSettingsFile.Load(settingsPath), credentials.HasKey, credentials.HasToken));
        if (generation != _generation) return;
        _settings = settings.Settings;
        _settingsWritable = settings.MayOverwrite;
        _exclusions = new ExclusionList(_settings.ExcludeFiles);
        _hasGitKey = hasKey;
        _hasGitToken = hasToken;
        if (settings.Warning is not null)
        {
            Log(settings.Warning);
            AppLog.Default.Warn(settings.Warning);
        }

        await LoadManifestAsync(generation);
    }

    private async Task LoadManifestAsync(int generation)
    {
        string? workspace = WorkspacePath();
        if (workspace is null) return;
        string path = SyncManifest.PathFor(workspace);
        (SyncManifest manifest, string? warning, bool mayOverwrite) = await Task.Run(() =>
        {
            SyncManifest loaded = SyncManifest.Load(path, out string? text, out bool may);
            return (loaded, text, may);
        });
        if (generation != _generation) return;
        _manifestWritable = mayOverwrite;
        if (warning is not null)
        {
            Log(warning);
            AppLog.Default.Warn(warning);
        }

        foreach (string skipped in manifest.Skipped.Take(10)) Log("Ignored manifest entry " + skipped);
        foreach (ManifestScript entry in manifest.Scripts)
        {
            if (_scripts.ContainsKey(entry.Key) || _byPath.ContainsKey(entry.Path)) continue;
            var item = new ScriptItem(entry.Kind, entry.Name, entry.Id, entry.Path)
            {
                Image = entry.Image,
                NpcType = entry.NpcType,
                Level = entry.Level,
                Modified = entry.Modified,
                LoadedModified = entry.Modified,
                SyncedLocalHash = entry.LocalHash,
                SyncedServerHash = entry.ServerHash,
            };
            AddItem(item);
        }

        foreach (ManifestFile entry in manifest.Files)
        {
            if (_files.ContainsKey(entry.RemotePath) || _byPath.ContainsKey(entry.Path)) continue;
            AddItem(new FileItem(entry.RemotePath, entry.Path)
            {
                Size = entry.Size,
                Modified = entry.Modified,
                LoadedModified = entry.Modified,
                SyncedLocalHash = entry.LocalHash,
            });
        }

        foreach (ManifestConfig entry in manifest.Configs)
        {
            if (_configs.ContainsKey(entry.Kind)) continue;
            AddItem(new ConfigItem(entry.Kind) { SyncedLocalHash = entry.LocalHash });
        }

        Notify();
    }

    private void ResetServerState()
    {
        _scripts.Clear();
        _files.Clear();
        _configs.Clear();
        _byPath.Clear();
        _problems.Clear();
        _scriptModifiedTimes.Clear();
        _manifestDirty = false;
        _manifestWritable = true;
        _settings = SyncSettings.Default;
        _exclusions = new ExclusionList(null);
        _wantRunning = false;
        _downloaded = 0;
        _uploaded = 0;
        ResetProgress();
    }

    // ---- Settings ---------------------------------------------------------------------------------------------------------------------

    private void ApplySettings(SyncSettings next)
    {
        SyncSettings old = _settings;
        if (next == old) return;
        _settings = next;
        _exclusions = new ExclusionList(next.ExcludeFiles);
        SaveSettingsSoon();
        if (!PathComparer.Equals(old.Workspace, next.Workspace))
        {
            StopCore(log: true);
            SaveManifestToWorkspace(old.Workspace);
            ClearItems();
            int generation = ++_generation;
            _ = InvokeAsync(() => LoadManifestAsync(generation));
            Notify();
            return;
        }

        bool active = _enabled || _oneShot;
        if (old.Direction != next.Direction)
        {
            ResetProgress();
            if (_enabled && next.DownloadsAllowed) _pullRequested = true;
            if (next.UploadsAllowed) _fullRescan = true;
        }

        if (active && ((!old.Files && next.Files) || (!old.ServerConfig && next.ServerConfig) || (!old.Scripts && next.Scripts))) _pullRequested = true;
        if (old.ExcludeFiles != next.ExcludeFiles || old.AllowRemoteDeletes != next.AllowRemoteDeletes) _fullRescan = true;
        Wake();
        Notify();
    }

    private void ChangeSettingsInternal(Func<SyncSettings, SyncSettings> change)
    {
        SyncSettings next = change(_settings).Normalized();
        if (next == _settings) return;
        _settings = next;
        SaveSettingsSoon();
        Notify();
    }

    private void SaveSettingsSoon()
    {
        if (!_settingsWritable || _settingsPath is null) return;
        string path = _settingsPath;
        string text = SyncSettingsFile.Serialize(_settings);
        _settingsSaveChain = _settingsSaveChain.ContinueWith(_ =>
        {
            try
            {
                AtomicFile.WriteAllText(path, text);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Default.Warn("The sync settings could not be saved.", exception);
                Dispatch(() => Log("The sync settings could not be saved: " + exception.Message));
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private string? WorkspacePath()
    {
        string workspace = _settings.Workspace;
        if (string.IsNullOrWhiteSpace(workspace)) return null;
        try
        {
            return Path.GetFullPath(Path.IsPathRooted(workspace) ? workspace : Path.Combine(_options.UserDirectory, workspace));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // ---- Manifest ---------------------------------------------------------------------------------------------------------------------

    private void MarkManifestDirty()
    {
        if (!_manifestDirty) _manifestDirtySince = DateTime.UtcNow;
        _manifestDirty = true;
    }

    private void SaveManifestIfDue()
    {
        if (_manifestDirty && DateTime.UtcNow - _manifestDirtySince >= _options.ManifestSaveDelay) SaveManifestNow();
    }

    private void SaveManifestNow() => SaveManifestToWorkspace(_settings.Workspace);

    private void SaveManifestToWorkspace(string workspaceSetting)
    {
        if (!_manifestDirty) return;
        _manifestDirty = false;
        if (!_manifestWritable || string.IsNullOrWhiteSpace(workspaceSetting)) return;
        string workspace = Path.IsPathRooted(workspaceSetting) ? workspaceSetting : Path.Combine(_options.UserDirectory, workspaceSetting);
        string path = SyncManifest.PathFor(workspace);
        string text = BuildManifest().Serialize();
        _manifestSaveChain = _manifestSaveChain.ContinueWith(_ =>
        {
            try
            {
                AtomicFile.WriteAllText(path, text);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                AppLog.Default.Warn("The sync manifest could not be saved.", exception);
                Dispatch(() => Log("The sync manifest could not be saved: " + exception.Message));
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>The manifest of the current items.</summary>
    internal SyncManifest BuildManifest()
    {
        var manifest = new SyncManifest();
        foreach (ScriptItem item in _scripts.Values)
            manifest.Scripts.Add(new ManifestScript(item.ScriptKind, item.ScriptName, item.Image, item.NpcType, item.Level, item.Id, item.Modified, item.Path, item.SyncedLocalHash, item.SyncedServerHash));
        foreach (FileItem item in _files.Values) manifest.Files.Add(new ManifestFile(item.RemotePath, item.Path, item.Size, item.Modified, item.SyncedLocalHash));
        foreach (ConfigItem item in _configs.Values) manifest.Configs.Add(new ManifestConfig(item.ConfigKind, item.Path, item.SyncedLocalHash));
        return manifest;
    }

    // ---- Items -------------------------------------------------------------------------------------------------------------------------

    private IEnumerable<TrackedItem> AllItems() => _scripts.Values.Cast<TrackedItem>().Concat(_files.Values).Concat(_configs.Values);

    private void AddItem(TrackedItem item)
    {
        switch (item)
        {
            case ScriptItem script:
                _scripts[script.Key] = script;
                break;
            case FileItem file:
                _files[file.RemotePath] = file;
                break;
            case ConfigItem config:
                _configs[config.ConfigKind] = config;
                break;
        }

        _byPath[item.Path] = item;
    }

    private void RemoveItem(TrackedItem item)
    {
        switch (item)
        {
            case ScriptItem script:
                _scripts.Remove(script.Key);
                break;
            case FileItem file:
                _files.Remove(file.RemotePath);
                break;
            case ConfigItem config:
                _configs.Remove(config.ConfigKind);
                break;
        }

        if (_byPath.TryGetValue(item.Path, out TrackedItem? current) && ReferenceEquals(current, item)) _byPath.Remove(item.Path);
        _problems.Remove(item.Key);
        _missingWatch.Remove(item.Key);
        _confirmedDeletions.Remove(item.Key);
        MarkManifestDirty();
    }

    private TrackedItem? FindItem(string key)
    {
        if (_scripts.TryGetValue(key, out ScriptItem? script)) return script;
        if (key.StartsWith("file:", StringComparison.Ordinal) && _files.TryGetValue(key[5..], out FileItem? file)) return file;
        return _configs.Values.FirstOrDefault(config => config.Key == key);
    }

    private void ClearItems()
    {
        _scripts.Clear();
        _files.Clear();
        _configs.Clear();
        _byPath.Clear();
        _problems.Clear();
        _missingWatch.Clear();
        _confirmedDeletions.Clear();
        _manifestDirty = false;
        _manifestWritable = true;
    }

    private string FullPath(TrackedItem item) => FullPath(item.Path);

    private string FullPath(string relativePath)
    {
        string workspace = WorkspacePath() ?? throw new InvalidOperationException("No workspace.");
        return Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private string ConflictPath(TrackedItem item) => SyncNames.MetadataFolder + "/conflicts/" + item.Path;

    private void SetState(TrackedItem item, SyncItemState state, string? detail = null, string? conflictCopy = null)
    {
        if (item.State == state && item.Detail == detail && item.ConflictCopy == conflictCopy) return;
        item.State = state;
        item.Detail = detail;
        item.ConflictCopy = conflictCopy;
        if (state == SyncItemState.InSync) _problems.Remove(item.Key);
        else _problems[item.Key] = item.Status();
        try
        {
            ItemStatusChanged?.Invoke(this, new SyncItemEventArgs(item.Status()));
        }
        catch (Exception exception)
        {
            AppLog.Default.Error("A sync item handler failed.", exception);
        }

        Notify();
    }

    // ---- Log and snapshot ------------------------------------------------------------------------------------------------------------

    private void Log(string text)
    {
        string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text;
        _log.AddLast(line);
        while (_log.Count > Math.Max(1, _options.LogCapacity)) _log.RemoveFirst();
        _logArray = _log.ToImmutableArray();
        try
        {
            LogAdded?.Invoke(this, new SyncLogEventArgs(line));
        }
        catch (Exception exception)
        {
            AppLog.Default.Error("A sync log handler failed.", exception);
        }

        Notify();
    }

    private void SetPhase(string phase)
    {
        if (_phase == phase) return;
        _phase = phase;
        Notify();
    }

    private void Notify()
    {
        _snapshotDirty = true;
        if (_snapshotPosted) return;
        _snapshotPosted = true;
        _context.Post(_ => Guard(RaiseSnapshot), null);
    }

    private void RaiseSnapshot()
    {
        _snapshotPosted = false;
        if (!_snapshotDirty) return;
        _snapshotDirty = false;
        SyncSnapshot snapshot = BuildSnapshot(++_sequence);
        Volatile.Write(ref _snapshot, snapshot);
        try
        {
            StateChanged?.Invoke(this, new SyncSnapshotEventArgs(snapshot));
        }
        catch (Exception exception)
        {
            AppLog.Default.Error("A sync state handler failed.", exception);
        }
    }

    private SyncSnapshot BuildSnapshot(long sequence)
    {
        SyncRunState state = _session is null ? SyncRunState.Detached
            : !_enabled && !_oneShot ? SyncRunState.Stopped
            : _waitingForNc && !_session.IsNcAuthenticated ? SyncRunState.WaitingForNcServer
            : _enabled ? SyncRunState.Running : SyncRunState.OneShot;
        bool active = _enabled || _oneShot;
        int percent = !active ? 0 : _workTotal == 0 ? 100 : (int)Math.Min(100, (long)_workDone * 100 / _workTotal);
        TimeSpan? eta = null;
        if (active && _workDone > 0 && _workDone < _workTotal)
        {
            double elapsed = Math.Max(1, (DateTime.UtcNow - _passStarted).TotalSeconds);
            eta = TimeSpan.FromSeconds(Math.Floor(elapsed) * (_workTotal - _workDone) / _workDone);
        }

        int? itemPercent = null;
        TimeSpan? itemEta = null;
        if (_currentItem is not null && _currentProgress is { Total: > 0 } progress)
        {
            itemPercent = (int)Math.Clamp(progress.Transferred * 100 / progress.Total, 0, 100);
            if (progress.Transferred > 0)
            {
                double elapsed = Math.Max(1, (DateTime.UtcNow - _currentStarted).TotalSeconds);
                itemEta = TimeSpan.FromSeconds(Math.Floor(elapsed) * (progress.Total - progress.Transferred) / progress.Transferred);
            }
        }

        return new SyncSnapshot
        {
            Sequence = sequence,
            ServerName = _serverName,
            State = state,
            Phase = _phase,
            CurrentItem = _currentItem,
            CurrentItemPercent = itemPercent,
            CurrentItemEta = itemEta,
            Percent = percent,
            Eta = eta,
            WorkDone = _workDone,
            WorkTotal = _workTotal,
            QueuedFolders = _queuedFolders,
            QueuedDownloads = _queuedDownloads,
            TrackedScripts = _scripts.Count,
            TrackedFiles = _files.Count,
            TrackedConfiguration = _configs.Count,
            Downloaded = _downloaded,
            Uploaded = _uploaded,
            Conflicts = _problems.Values.Count(status => status.State == SyncItemState.Conflict),
            ProgressUnseen = _progressUnseen,
            FileSyncActive = _fileSyncActive || (_settings.Files && active),
            Settings = _settings,
            HasGitKey = _hasGitKey,
            HasGitToken = _hasGitToken,
            Log = _logArray,
            Items = _problems.Values.OrderBy(status => status.Key, StringComparer.Ordinal).Take(1000).ToImmutableArray(),
        };
    }

    // ---- Session events -----------------------------------------------------------------------------------------------------------------

    /// <summary>The handlers of one attached session; disposing removes them, and late events of that session are ignored.</summary>
    private sealed class Subscription : IDisposable
    {
        private readonly SyncEngine _engine;
        private readonly IRcSession _session;
        private readonly IRcFileBrowser _browser;
        private bool _disposed;

        public Subscription(SyncEngine engine, IRcSession session)
        {
            _engine = engine;
            _session = session;
            _browser = session.SyncFileBrowser;
            session.Disconnected += OnDisconnected;
            session.NcConnected += OnNcConnected;
            session.ScriptReceived += OnScriptReceived;
            session.ServerOptionsReceived += OnServerOptions;
            session.ServerFlagsReceived += OnServerFlags;
            session.FolderConfigReceived += OnFolderConfig;
            session.FileBrowserMessageReceived += OnFileBrowserMessage;
            _browser.FoldersReceived += OnFoldersReceived;
            _browser.FilesReceived += OnFilesReceived;
            _browser.TransferProgressChanged += OnTransferProgress;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.Disconnected -= OnDisconnected;
            _session.NcConnected -= OnNcConnected;
            _session.ScriptReceived -= OnScriptReceived;
            _session.ServerOptionsReceived -= OnServerOptions;
            _session.ServerFlagsReceived -= OnServerFlags;
            _session.FolderConfigReceived -= OnFolderConfig;
            _session.FileBrowserMessageReceived -= OnFileBrowserMessage;
            _browser.FoldersReceived -= OnFoldersReceived;
            _browser.FilesReceived -= OnFilesReceived;
            _browser.TransferProgressChanged -= OnTransferProgress;
        }

        private void On(Action action) => _engine.Dispatch(() =>
        {
            if (!_disposed && ReferenceEquals(_engine._session, _session)) action();
        });

        private void OnDisconnected(object? sender, DisconnectedEventArgs e) => On(() => _engine.DetachCore("disconnected: " + e.Reason));

        private void OnNcConnected(object? sender, EventArgs e) => On(_engine.HandleNcConnected);

        private void OnScriptReceived(object? sender, ScriptReceivedEventArgs e) => On(() => _engine.HandleScriptReceived(e));

        private void OnServerOptions(object? sender, TextEventArgs e) => On(() => _engine.HandleConfigReceived(SyncConfigKind.ServerOptions, e.Text));

        private void OnServerFlags(object? sender, TextEventArgs e) => On(() => _engine.HandleConfigReceived(SyncConfigKind.ServerFlags, e.Text));

        private void OnFolderConfig(object? sender, TextEventArgs e) => On(() => _engine.HandleConfigReceived(SyncConfigKind.FolderConfig, e.Text));

        private void OnFileBrowserMessage(object? sender, TextEventArgs e) => On(() => _engine.HandleFileBrowserMessage(e.Text));

        private void OnFoldersReceived(object? sender, ValueEventArgs<IReadOnlyList<FileBrowserFolder>> e)
        {
            FileBrowserFolder[] folders = e.Value.ToArray();
            On(() => _engine.HandleFoldersReceived(folders));
        }

        private void OnFilesReceived(object? sender, FileBrowserFilesEventArgs e)
        {
            FileBrowserEntry[] files = e.Files.ToArray();
            On(() => _engine.HandleFilesReceived(e.Folder, files));
        }

        private void OnTransferProgress(object? sender, FileTransferProgressEventArgs e) => On(() => _engine.HandleTransferProgress(e));
    }
}
