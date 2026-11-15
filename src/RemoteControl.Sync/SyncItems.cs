using RemoteControl.Protocol.Session;

namespace RemoteControl.Sync;

/// <summary>The size and modification time of a local file when it was last hashed.</summary>
internal readonly record struct FileStamp(long Length, long LastWriteTicks);

/// <summary>One tracked item. Lives on the engine's context only.</summary>
internal abstract class TrackedItem
{
    protected TrackedItem(string path) => Path = path;

    public abstract string Key { get; }

    public abstract SyncItemKind Kind { get; }

    public abstract string Name { get; }

    /// <summary>The workspace path (<c>/</c> separators).</summary>
    public string Path { get; set; }

    /// <summary>The hash of the local content as last synchronised (both sides agreed then); <c>null</c> when unknown.</summary>
    public string? SyncedLocalHash { get; set; }

    /// <summary>The <c>modified</c> value the manifest had when loaded (what the C++ set as the file's time after a download).</summary>
    public long LoadedModified { get; set; }

    /// <summary>The hash of the local file at the last scan.</summary>
    public string? LocalHash { get; set; }

    /// <summary>The stamp of the local file at the last scan.</summary>
    public FileStamp? Stamp { get; set; }

    public SyncItemState State { get; set; } = SyncItemState.InSync;

    public string? Detail { get; set; }

    /// <summary>The workspace path of the server copy of a conflict.</summary>
    public string? ConflictCopy { get; set; }

    /// <summary>The server version of a conflict.</summary>
    public byte[]? ConflictContent { get; set; }

    /// <summary>The hash of the server version of a conflict (the server script for scripts).</summary>
    public string? ConflictServerHash { get; set; }

    /// <summary>The server hash expected back after an upload, and until when.</summary>
    public string? PendingUploadHash { get; set; }

    public DateTime PendingUploadUntil { get; set; }

    /// <summary>When the local file was first seen missing.</summary>
    public DateTime? MissingSince { get; set; }

    public SyncItemStatus Status() => new(Key, Kind, Name, Path, State, Detail, ConflictCopy);
}

internal sealed class ScriptItem : TrackedItem
{
    public ScriptItem(ScriptKind scriptKind, string name, int id, string path)
        : base(path)
    {
        ScriptKind = scriptKind;
        ScriptName = name;
        Id = id;
    }

    public ScriptKind ScriptKind { get; }

    public string ScriptName { get; set; }

    public int Id { get; }

    public string Image { get; set; } = string.Empty;

    public string NpcType { get; set; } = string.Empty;

    public string Level { get; set; } = string.Empty;

    /// <summary>The server's modification time (<c>modified</c>).</summary>
    public long Modified { get; set; }

    /// <summary>The hash of the server's script as last synchronised.</summary>
    public string? SyncedServerHash { get; set; }

    public override string Key => SyncNames.ScriptKey(ScriptKind, ScriptName, Id);

    public override SyncItemKind Kind => ScriptKind switch
    {
        ScriptKind.Class => SyncItemKind.Class,
        ScriptKind.Npc => SyncItemKind.Npc,
        _ => SyncItemKind.Weapon,
    };

    public override string Name => ScriptName;
}

internal sealed class FileItem : TrackedItem
{
    public FileItem(string remotePath, string path)
        : base(path) => RemotePath = remotePath;

    public string RemotePath { get; }

    /// <summary>The server size and time as last synchronised.</summary>
    public long Size { get; set; }

    public long Modified { get; set; }

    /// <summary>The size and time of the last listing that queued a download.</summary>
    public long ListedSize { get; set; }

    public long ListedModified { get; set; }

    /// <summary>The listing values of a conflict (not downloaded again while they stay).</summary>
    public (long Size, long Modified)? ConflictListing { get; set; }

    public override string Key => "file:" + RemotePath;

    public override SyncItemKind Kind => SyncItemKind.File;

    public override string Name => RemotePath;
}

internal sealed class ConfigItem : TrackedItem
{
    public ConfigItem(SyncConfigKind configKind)
        : base(SyncConfigKinds.PathOf(configKind)) => ConfigKind = configKind;

    public SyncConfigKind ConfigKind { get; }

    public override string Key => "config:" + SyncConfigKinds.TypeName(ConfigKind);

    public override SyncItemKind Kind => SyncItemKind.Configuration;

    public override string Name => SyncConfigKinds.TypeName(ConfigKind);
}
