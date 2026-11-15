using System.Globalization;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Sync;

/// <summary>A server text the engine keeps in <c>config/</c>.</summary>
public enum SyncConfigKind
{
    /// <summary>The server options (<c>options</c>, <c>config/serveroptions.txt</c>).</summary>
    ServerOptions,

    /// <summary>The server flags (<c>flags</c>, <c>config/serverflags.txt</c>).</summary>
    ServerFlags,

    /// <summary>The folder configuration (<c>folder_config</c>, <c>config/folderconfig.txt</c>).</summary>
    FolderConfig,
}

/// <summary>Names of the <see cref="SyncConfigKind"/> values in the manifest and the workspace.</summary>
public static class SyncConfigKinds
{
    /// <summary>Every kind.</summary>
    public static IReadOnlyList<SyncConfigKind> All { get; } = [SyncConfigKind.ServerOptions, SyncConfigKind.ServerFlags, SyncConfigKind.FolderConfig];

    /// <summary>The type word the C++ used (<c>options</c>, <c>flags</c>, <c>folder_config</c>).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The word.</returns>
    public static string TypeName(SyncConfigKind kind) => kind switch
    {
        SyncConfigKind.ServerFlags => "flags",
        SyncConfigKind.FolderConfig => "folder_config",
        _ => "options",
    };

    /// <summary>The workspace path (<c>config/serveroptions.txt</c> ...).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The relative path.</returns>
    public static string PathOf(SyncConfigKind kind) => SyncNames.ConfigFolder + "/" + kind switch
    {
        SyncConfigKind.ServerFlags => "serverflags.txt",
        SyncConfigKind.FolderConfig => "folderconfig.txt",
        _ => "serveroptions.txt",
    };

    /// <summary>The kind of a type word.</summary>
    /// <param name="typeName">The word.</param>
    /// <param name="kind">The kind.</param>
    /// <returns>Whether the word is known.</returns>
    public static bool TryParse(string? typeName, out SyncConfigKind kind)
    {
        foreach (SyncConfigKind candidate in All)
        {
            if (TypeName(candidate) == typeName)
            {
                kind = candidate;
                return true;
            }
        }

        kind = SyncConfigKind.ServerOptions;
        return false;
    }
}

/// <summary>A script entry of the manifest (<c>[weapon:&lt;name&gt;]</c>, <c>[class:&lt;name&gt;]</c>, <c>[npc:&lt;id&gt;]</c>).</summary>
/// <param name="Kind">The kind (<c>type</c>).</param>
/// <param name="Name">The name (<c>name</c>).</param>
/// <param name="Image">The image (<c>image</c>).</param>
/// <param name="NpcType">The NPC type (<c>npcType</c>).</param>
/// <param name="Level">The level (<c>level</c>).</param>
/// <param name="Id">The NPC id (<c>id</c>).</param>
/// <param name="Modified">The server's modification time in Unix seconds, or 0 (<c>modified</c>).</param>
/// <param name="Path">The workspace path (<c>path</c>).</param>
/// <param name="LocalHash">The hash of the document as last synchronised (<c>localHash</c>, new; <c>null</c> in a C++ manifest).</param>
/// <param name="ServerHash">The hash of the server's script as last synchronised (<c>serverHash</c>, new).</param>
public sealed record ManifestScript(ScriptKind Kind, string Name, string Image, string NpcType, string Level, int Id, long Modified, string Path, string? LocalHash, string? ServerHash)
{
    /// <summary>The key (<see cref="SyncNames.ScriptKey"/>).</summary>
    public string Key => SyncNames.ScriptKey(Kind, Name, Id);
}

/// <summary>A file entry of the manifest (<c>[file:&lt;stableSuffix(remote path)&gt;]</c>).</summary>
/// <param name="RemotePath">The server path (<c>name</c>).</param>
/// <param name="Path">The workspace path, <c>files/&lt;remote path&gt;</c> (<c>path</c>).</param>
/// <param name="Size">The server size as last synchronised (<c>size</c>).</param>
/// <param name="Modified">The server modification time as last synchronised (<c>modified</c>).</param>
/// <param name="LocalHash">The hash of the content as last synchronised (<c>localHash</c>, new).</param>
public sealed record ManifestFile(string RemotePath, string Path, long Size, long Modified, string? LocalHash);

/// <summary>A configuration entry of the manifest (<c>[config:&lt;type&gt;]</c>).</summary>
/// <param name="Kind">The kind (<c>name</c>).</param>
/// <param name="Path">The workspace path (<c>path</c>).</param>
/// <param name="LocalHash">The hash as last synchronised (<c>localHash</c>; the C++ <c>std::hash</c> values are dropped, D-14).</param>
public sealed record ManifestConfig(SyncConfigKind Kind, string Path, string? LocalHash);

/// <summary>
/// <c>&lt;workspace&gt;/.graal-sync/manifest.conf</c> (<c>loadManifest</c>/<c>saveManifest</c>, <c>TSyncManager.cpp:359-492</c>): a GLib key file the
/// C++ and this program both read. This program adds <c>localHash</c> (and <c>serverHash</c>) to script and file entries, so an edit made
/// while the program was closed is recognised (defect D-10); the C++ ignores those keys. Entries with an unsafe path are skipped.
/// </summary>
public sealed class SyncManifest
{
    /// <summary>The manifest path of a workspace.</summary>
    /// <param name="workspace">The workspace.</param>
    /// <returns>The path.</returns>
    public static string PathFor(string workspace) => Path.Combine(workspace, SyncNames.MetadataFolder, "manifest.conf");

    /// <summary>The scripts.</summary>
    public List<ManifestScript> Scripts { get; } = new();

    /// <summary>The files.</summary>
    public List<ManifestFile> Files { get; } = new();

    /// <summary>The configuration texts.</summary>
    public List<ManifestConfig> Configs { get; } = new();

    /// <summary>Entries that were skipped while parsing, with the reason.</summary>
    public List<string> Skipped { get; } = new();

    /// <summary>Parses a manifest.</summary>
    /// <param name="text">The content.</param>
    /// <param name="manifest">The manifest.</param>
    /// <param name="error">Why it could not be parsed.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string text, out SyncManifest manifest, out string? error)
    {
        manifest = new SyncManifest();
        if (!KeyFile.TryParse(text, out KeyFile file, out error)) return false;
        foreach (string group in file.Groups)
        {
            string? type = file.GetString(group, "type");
            string? name = file.GetString(group, "name");
            string? path = file.GetString(group, "path");
            if (type is null || name is null || path is null || !SyncNames.IsSafeWorkspacePath(path))
            {
                manifest.Skipped.Add($"[{group}]: missing type, name or a safe path");
                continue;
            }

            string? localHash = Trusted(file.GetString(group, "localHash"));
            if (type == "config")
            {
                if (!SyncConfigKinds.TryParse(name, out SyncConfigKind kind) || path != SyncConfigKinds.PathOf(kind))
                {
                    manifest.Skipped.Add($"[{group}]: unknown configuration {name}");
                    continue;
                }

                manifest.Configs.Add(new ManifestConfig(kind, path, localHash));
                continue;
            }

            if (type == "file")
            {
                string remote = SyncNames.NormalizeRemotePath(name);
                if (remote.Length == 0 || path != SyncNames.FilesFolder + "/" + remote)
                {
                    manifest.Skipped.Add($"[{group}]: file {name} does not match its path");
                    continue;
                }

                manifest.Files.Add(new ManifestFile(remote, path, file.GetInteger(group, "size"), file.GetInteger(group, "modified"), localHash));
                continue;
            }

            ScriptKind scriptKind = type switch
            {
                "npc" => ScriptKind.Npc,
                "class" => ScriptKind.Class,
                _ => ScriptKind.Weapon,
            };
            if (!path.StartsWith(SyncNames.FolderOf(scriptKind) + "/", StringComparison.Ordinal))
            {
                manifest.Skipped.Add($"[{group}]: {type} {name} is not in {SyncNames.FolderOf(scriptKind)}/");
                continue;
            }

            long id = file.GetInteger(group, "id");
            manifest.Scripts.Add(new ManifestScript(scriptKind, name, file.GetString(group, "image") ?? string.Empty, file.GetString(group, "npcType") ?? string.Empty,
                file.GetString(group, "level") ?? string.Empty, id is >= int.MinValue and <= int.MaxValue ? (int)id : 0, file.GetInteger(group, "modified"), path,
                localHash, Trusted(file.GetString(group, "serverHash"))));
        }

        return true;
    }

    /// <summary>The manifest text; entries are sorted so that git diffs stay small.</summary>
    /// <returns>The content.</returns>
    public string Serialize()
    {
        var file = new KeyFile();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (ManifestScript script in Scripts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            string group = script.Key;
            if (!KeyFile.IsValidGroupName(group)) group = SyncNames.TypeName(script.Kind) + ":" + SyncNames.EncodeGraalFileName(script.Name);
            group = Unique(group, used);
            file.SetString(group, "type", SyncNames.TypeName(script.Kind));
            file.SetString(group, "name", script.Name);
            file.SetString(group, "image", script.Image);
            file.SetString(group, "npcType", script.NpcType);
            file.SetString(group, "level", script.Level);
            file.SetInteger(group, "id", script.Id);
            file.SetInteger(group, "modified", script.Modified);
            file.SetString(group, "path", script.Path);
            if (script.LocalHash is not null) file.SetString(group, "localHash", script.LocalHash);
            if (script.ServerHash is not null) file.SetString(group, "serverHash", script.ServerHash);
        }

        foreach (ManifestFile entry in Files.OrderBy(item => item.RemotePath, StringComparer.Ordinal))
        {
            string group = Unique("file:" + SyncNames.StableSuffix(entry.RemotePath), used);
            file.SetString(group, "type", "file");
            file.SetString(group, "name", entry.RemotePath);
            file.SetString(group, "path", entry.Path);
            file.SetInteger(group, "size", entry.Size);
            file.SetInteger(group, "modified", entry.Modified);
            if (entry.LocalHash is not null) file.SetString(group, "localHash", entry.LocalHash);
        }

        foreach (ManifestConfig entry in Configs.OrderBy(item => item.Kind))
        {
            string group = Unique("config:" + SyncConfigKinds.TypeName(entry.Kind), used);
            file.SetString(group, "type", "config");
            file.SetString(group, "name", SyncConfigKinds.TypeName(entry.Kind));
            file.SetString(group, "path", entry.Path);
            if (entry.LocalHash is not null) file.SetString(group, "localHash", entry.LocalHash);
        }

        return file.Serialize();
    }

    /// <summary>
    /// Reads a manifest file: a missing file gives an empty manifest; a file that does not parse is moved aside and an empty manifest is
    /// used (nothing is then considered synchronised, so nothing local is overwritten).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="warning">What went wrong, or <c>null</c>.</param>
    /// <param name="mayOverwrite">Whether saving may replace the file (false when it exists but could not be read nor moved aside).</param>
    /// <returns>The manifest.</returns>
    public static SyncManifest Load(string path, out string? warning, out bool mayOverwrite)
    {
        warning = null;
        mayOverwrite = true;
        string text;
        try
        {
            if (!File.Exists(path)) return new SyncManifest();
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warning = $"The sync manifest could not be read ({exception.Message}); it is not changed.";
            mayOverwrite = false;
            return new SyncManifest();
        }

        if (TryParse(text, out SyncManifest manifest, out string? error)) return manifest;
        try
        {
            string? aside = AtomicFile.MoveAside(path, "unreadable", DateTime.Now);
            warning = $"The sync manifest could not be read ({error}); it was kept as {aside}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warning = $"The sync manifest could not be read ({error}) nor moved aside; it is not changed.";
            mayOverwrite = false;
        }

        return new SyncManifest();
    }

    private static string? Trusted(string? value) => SyncHash.IsTrusted(value) ? value : null;

    private static string Unique(string group, HashSet<string> used)
    {
        string candidate = group;
        for (int counter = 2; !used.Add(candidate); counter++) candidate = group + "-" + counter.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }
}
