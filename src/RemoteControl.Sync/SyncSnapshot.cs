using System.Collections.Immutable;
using RemoteControl.Core;

namespace RemoteControl.Sync;

/// <summary>Whether the engine runs (the "State:" line of the status).</summary>
public enum SyncRunState
{
    /// <summary>No session is attached (never connected, disconnected, or replaced).</summary>
    Detached,

    /// <summary>Attached and stopped.</summary>
    Stopped,

    /// <summary>Running, but the scripts and the configuration wait for the NC connection.</summary>
    WaitingForNcServer,

    /// <summary>Running continuously (Start).</summary>
    Running,

    /// <summary>One pass (Sync now, Pull, Push) that stops when done.</summary>
    OneShot,
}

/// <summary>What a tracked item is.</summary>
public enum SyncItemKind
{
    /// <summary>A weapon.</summary>
    Weapon,

    /// <summary>A class.</summary>
    Class,

    /// <summary>A database NPC.</summary>
    Npc,

    /// <summary>A server file.</summary>
    File,

    /// <summary>A server text (options, flags, folder configuration).</summary>
    Configuration,
}

/// <summary>The state of one tracked item.</summary>
public enum SyncItemState
{
    /// <summary>Local and server agree.</summary>
    InSync,

    /// <summary>Changed locally; not uploaded because uploads are off (download only) or the NC is not connected.</summary>
    LocallyModified,

    /// <summary>Changed locally and on the server; the local file is kept and the server copy is in <see cref="SyncItemStatus.ConflictCopy"/>.</summary>
    Conflict,

    /// <summary>The local file is missing and will be deleted on the server once <see cref="SyncEngine.ConfirmPendingDeletions"/> is called.</summary>
    PendingDeletion,

    /// <summary>The local file is missing; the server copy is kept.</summary>
    MissingLocally,

    /// <summary>The last transfer failed (<see cref="SyncItemStatus.Detail"/> says why).</summary>
    Failed,
}

/// <summary>How a conflict is resolved.</summary>
public enum ConflictResolution
{
    /// <summary>Keep the local file: it counts as a local edit of the server's current version (uploaded when uploads are on).</summary>
    KeepLocal,

    /// <summary>Use the server copy: the local file is moved into the conflict folder (as <c>.local</c>) and replaced.</summary>
    UseServer,
}

/// <summary>The status of one item the window may list.</summary>
/// <param name="Key">The item key (<c>weapon:Name</c>, <c>class:Name</c>, <c>npc:12</c>, <c>file:levels/a.nw</c>, <c>config:options</c>).</param>
/// <param name="Kind">The kind.</param>
/// <param name="Name">The server name (script name, NPC name, remote path or configuration type).</param>
/// <param name="RelativePath">The workspace path.</param>
/// <param name="State">The state.</param>
/// <param name="Detail">A sentence for the user, or <c>null</c>.</param>
/// <param name="ConflictCopy">The workspace path of the server copy of a conflict, or <c>null</c>.</param>
public sealed record SyncItemStatus(string Key, SyncItemKind Kind, string Name, string RelativePath, SyncItemState State, string? Detail, string? ConflictCopy);

/// <summary>
/// Everything the sync window shows, as one immutable value (<see cref="SyncEngine.Snapshot"/>, <see cref="SyncEngine.StateChanged"/>). Values
/// follow <c>statusText</c> (<c>TSyncManager.cpp:1314-1336</c>).
/// </summary>
public sealed record SyncSnapshot
{
    /// <summary>Increases with every snapshot raised.</summary>
    public long Sequence { get; init; }

    /// <summary>The server the engine is set up for.</summary>
    public string ServerName { get; init; } = string.Empty;

    /// <summary>Whether it runs.</summary>
    public SyncRunState State { get; init; } = SyncRunState.Detached;

    /// <summary>The current phase ("Listing levels", "Watching for changes" ...).</summary>
    public string Phase { get; init; } = "Stopped";

    /// <summary>The item being transferred, or <c>null</c>.</summary>
    public string? CurrentItem { get; init; }

    /// <summary>The percentage of <see cref="CurrentItem"/>, when known.</summary>
    public int? CurrentItemPercent { get; init; }

    /// <summary>The time left for <see cref="CurrentItem"/>, when known.</summary>
    public TimeSpan? CurrentItemEta { get; init; }

    /// <summary>The overall percentage of the current pass (0 when stopped, 100 when nothing is left).</summary>
    public int Percent { get; init; }

    /// <summary>The overall time left: elapsed × remaining ÷ done.</summary>
    public TimeSpan? Eta { get; init; }

    /// <summary>Work items done in the current pass.</summary>
    public int WorkDone { get; init; }

    /// <summary>Work items of the current pass.</summary>
    public int WorkTotal { get; init; }

    /// <summary>Folders waiting to be listed.</summary>
    public int QueuedFolders { get; init; }

    /// <summary>Files waiting to be downloaded.</summary>
    public int QueuedDownloads { get; init; }

    /// <summary>Tracked scripts.</summary>
    public int TrackedScripts { get; init; }

    /// <summary>Tracked files.</summary>
    public int TrackedFiles { get; init; }

    /// <summary>Tracked configuration texts.</summary>
    public int TrackedConfiguration { get; init; }

    /// <summary>Changes downloaded since the engine was created.</summary>
    public int Downloaded { get; init; }

    /// <summary>Changes uploaded (and deletions made) since the engine was created.</summary>
    public int Uploaded { get; init; }

    /// <summary>Items in conflict.</summary>
    public int Conflicts { get; init; }

    /// <summary>Whether a pass started since the window last showed it (the main window's title shows the percentage meanwhile).</summary>
    public bool ProgressUnseen { get; init; }

    /// <summary>Whether a file pass is active (<c>isFileSyncActive</c>).</summary>
    public bool FileSyncActive { get; init; }

    /// <summary>The settings.</summary>
    public SyncSettings Settings { get; init; } = SyncSettings.Default;

    /// <summary>Whether an SSH key is stored.</summary>
    public bool HasGitKey { get; init; }

    /// <summary>Whether a token is stored.</summary>
    public bool HasGitToken { get; init; }

    /// <summary>The last log lines, oldest first (<c>HH:MM:SS  text</c>).</summary>
    public ImmutableArray<string> Log { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>The items that need attention (conflicts, local edits not uploaded, pending deletions, failures), at most 1000.</summary>
    public ImmutableArray<SyncItemStatus> Items { get; init; } = ImmutableArray<SyncItemStatus>.Empty;

    /// <summary>The status text of the Setup page, as the C++ formatted it.</summary>
    public string StatusText
    {
        get
        {
            var text = new System.Text.StringBuilder();
            text.Append("State: ").Append(State switch
            {
                SyncRunState.Detached or SyncRunState.Stopped => "Stopped",
                SyncRunState.WaitingForNcServer => "Waiting for NPC-Server",
                _ => "Running",
            });
            text.Append("\nCurrent: ").Append(Phase);
            if (CurrentItemPercent is int itemPercent)
            {
                text.Append(" (").Append(itemPercent).Append('%');
                if (CurrentItemEta is TimeSpan itemEta) text.Append(", ETA ").Append(FormatEta(itemEta));
                text.Append(')');
            }

            if (WorkTotal > 0)
            {
                text.Append("\nOverall: ").Append(Percent).Append('%');
                if (Eta is TimeSpan eta) text.Append(" (ETA ").Append(FormatEta(eta)).Append(')');
            }

            text.Append("\nQueue: ").Append(QueuedFolders).Append(" folders, ").Append(QueuedDownloads).Append(" downloads");
            text.Append("\nTracked: ").Append(TrackedScripts).Append(" scripts, ").Append(TrackedFiles).Append(" files, ").Append(TrackedConfiguration).Append(" configuration files");
            text.Append("\nTransfers: ").Append(Downloaded).Append(" downloaded, ").Append(Uploaded).Append(" uploaded");
            if (Conflicts > 0) text.Append(", ").Append(Conflicts).Append(" conflicts");
            string direction = Settings.Direction switch
            {
                SyncDirection.UploadOnly => "upload only",
                SyncDirection.TwoWay => "two-way",
                _ => "download only",
            };
            text.Append("\nModes: scripts ").Append(Settings.Scripts ? "on" : "off").Append(", files ").Append(Settings.Files ? "on" : "off")
                .Append(", configuration ").Append(Settings.ServerConfig ? "on" : "off").Append(", direction ").Append(direction)
                .Append(", remote deletes ").Append(Settings.RemoteDeletesEffective ? "on" : "off").Append(", auto-start ").Append(Settings.AutoStart ? "on" : "off");
            if (!Log.IsEmpty) text.Append("\nLast: ").Append(Log[^1]);
            return text.ToString();
        }
    }

    /// <summary><c>formatEta</c>: <c>42s</c>, <c>3m 5s</c>, <c>2h 10m</c>.</summary>
    /// <param name="value">The time.</param>
    /// <returns>The text.</returns>
    public static string FormatEta(TimeSpan value)
    {
        long seconds = (long)Math.Max(0, value.TotalSeconds);
        if (seconds < 60) return seconds + "s";
        if (seconds < 3600) return (seconds / 60) + "m " + (seconds % 60) + "s";
        return (seconds / 3600) + "h " + ((seconds % 3600) / 60) + "m";
    }
}

/// <summary>A snapshot event.</summary>
public sealed class SyncSnapshotEventArgs : EventArgs
{
    /// <summary>Creates the event.</summary>
    /// <param name="snapshot">The snapshot.</param>
    public SyncSnapshotEventArgs(SyncSnapshot snapshot) => Snapshot = snapshot;

    /// <summary>The snapshot.</summary>
    public SyncSnapshot Snapshot { get; }
}

/// <summary>A log line event.</summary>
public sealed class SyncLogEventArgs : EventArgs
{
    /// <summary>Creates the event.</summary>
    /// <param name="line">The line (<c>HH:MM:SS  text</c>).</param>
    public SyncLogEventArgs(string line) => Line = line;

    /// <summary>The line.</summary>
    public string Line { get; }
}

/// <summary>An item changed its state.</summary>
public sealed class SyncItemEventArgs : EventArgs
{
    /// <summary>Creates the event.</summary>
    /// <param name="status">The new status.</param>
    public SyncItemEventArgs(SyncItemStatus status) => Status = status;

    /// <summary>The new status.</summary>
    public SyncItemStatus Status { get; }
}

/// <summary>
/// The server's options, flags or folder configuration changed (seen by the engine's configuration pull). The engine only updates its own
/// file in <c>config/</c>; an open editor decides itself whether to offer the new text (defect D-12).
/// </summary>
public sealed class ServerConfigurationChangedEventArgs : EventArgs
{
    /// <summary>Creates the event.</summary>
    /// <param name="kind">Which text.</param>
    /// <param name="content">The server's text.</param>
    public ServerConfigurationChangedEventArgs(SyncConfigKind kind, string content)
    {
        Kind = kind;
        Content = content;
    }

    /// <summary>Which text.</summary>
    public SyncConfigKind Kind { get; }

    /// <summary>The server's text.</summary>
    public string Content { get; }
}

/// <summary>The outcome of a git command started from the engine.</summary>
public sealed class SyncGitEventArgs : EventArgs
{
    /// <summary>Creates the event.</summary>
    /// <param name="operation">What ran ("Commit", "Push" ...).</param>
    /// <param name="result">The result.</param>
    public SyncGitEventArgs(string operation, GitResult result)
    {
        Operation = operation;
        Result = result;
    }

    /// <summary>What ran.</summary>
    public string Operation { get; }

    /// <summary>The result.</summary>
    public GitResult Result { get; }
}
