namespace RemoteControl.Sync;

/// <summary>How a <see cref="SyncEngine"/> works. The defaults are the C++ values; tests shorten the times.</summary>
public sealed record SyncEngineOptions
{
    /// <summary>The user folder: the settings go to <c>sync/</c> in it, the credentials use its <c>accounts.key</c>.</summary>
    public required string UserDirectory { get; init; }

    /// <summary>
    /// The context the engine's state belongs to and its events are raised on (the UI thread; it must be the session's context).
    /// When <c>null</c>, <see cref="SynchronizationContext.Current"/> is used, or a private single-threaded context when there is none.
    /// </summary>
    public SynchronizationContext? Context { get; init; }

    /// <summary>The time between full pulls while running (<c>pullInterval</c>, 15 minutes).</summary>
    public TimeSpan PullInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The tick of the engine (the C++ timer, 1 second).</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The time between full rescans of the workspace while running; between them only what the file watcher reports is read.</summary>
    public TimeSpan RescanInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long the folder list may take to arrive.</summary>
    public TimeSpan FileRootsTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a folder listing may take (the C++ waited forever).</summary>
    public TimeSpan FolderListTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the listing of <c>weapons/</c>, <c>scripts/</c>, <c>npcs/</c> may take (3 seconds).</summary>
    public TimeSpan MetadataFolderTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a pass waits for requested scripts and configuration texts once nothing arrives any more.</summary>
    public TimeSpan ArrivalTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The base time a download may take; one second per 128 KiB is added, at most 270 seconds (30 seconds).</summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Attempts per download (3).</summary>
    public int DownloadAttempts { get; init; } = 3;

    /// <summary>How long a server copy that differs from an upload just made is taken for an old echo and ignored (10 seconds).</summary>
    public TimeSpan UploadEchoWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a tracked file must stay missing before it counts as deleted (an editor's save-by-rename is not a deletion).</summary>
    public TimeSpan DeletionDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>More local deletions than this at once are held until <see cref="SyncEngine.ConfirmPendingDeletions"/>.</summary>
    public int MaxAutomaticDeletions { get; init; } = 10;

    /// <summary>The cap of queued folders (5000).</summary>
    public int MaxQueuedFolders { get; init; } = 5000;

    /// <summary>The cap of queued downloads (10000).</summary>
    public int MaxQueuedDownloads { get; init; } = 10000;

    /// <summary>The largest file the engine downloads, hashes for upload or uploads.</summary>
    public long MaxFileSize { get; init; } = 64L * 1024 * 1024;

    /// <summary>The number of log lines kept (300).</summary>
    public int LogCapacity { get; init; } = 300;

    /// <summary>How long the manifest may stay unsaved after a change.</summary>
    public TimeSpan ManifestSaveDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The git program; found like the C++ when <c>null</c>.</summary>
    public string? GitExecutable { get; init; }

    /// <summary>How long one git command may run.</summary>
    public TimeSpan GitTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Extra environment variables for git.</summary>
    public IReadOnlyDictionary<string, string> GitEnvironment { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether a file-system watcher reports local edits between rescans.</summary>
    public bool WatchFileSystem { get; init; } = true;
}
