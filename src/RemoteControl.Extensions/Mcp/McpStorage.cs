using System.Globalization;
using System.Text.Json;
using RemoteControl.Core;

namespace RemoteControl.Extensions;

/// <summary>A copy of what an MCP tool overwrote or deleted on a server.</summary>
/// <param name="Id">What names it for <c>rc_backups</c> and <c>rc_restore_backup</c>: <c>server/stamp-kind-name</c>, path-safe.</param>
/// <param name="Server">The server it came from.</param>
/// <param name="Kind">What it is: <c>weapon</c>, <c>class</c>, <c>npc</c>, <c>npcflags</c>, <c>file</c>, <c>serveroptions</c>, <c>serverflags</c>, <c>folderconfig</c>, or a player text (<c>account</c>, <c>rights</c>, <c>attributes</c>, <c>comments</c>, <c>profile</c>).</param>
/// <param name="Name">The script name, the file path, or the account.</param>
/// <param name="NpcId">The NPC id for <c>npc</c> and <c>npcflags</c>, otherwise 0.</param>
/// <param name="Image">The weapon image, otherwise empty.</param>
/// <param name="Time">When it was taken.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Reason">The tool that replaced it.</param>
internal sealed record McpBackupEntry(string Id, string Server, string Kind, string Name, int NpcId, string Image, DateTimeOffset Time, long Size, string Reason);

/// <summary>
/// Keeps the previous version of everything an MCP tool overwrites or deletes on a server, so that an agent's change can be looked at
/// and undone: <c>&lt;root&gt;/&lt;server&gt;/&lt;stamp&gt;-&lt;kind&gt;-&lt;name&gt;.data</c> with a <c>.json</c> beside it that says what it is.
/// Files are written whole (AGENTS.md rule 6) on a background task; a failure is logged and reported, never thrown.
/// </summary>
internal sealed class McpBackupStore
{
    private const int MaximumNameLength = 80;
    private readonly AppLog _log;
    private readonly Func<DateTimeOffset> _clock;
    private int _counter;

    public McpBackupStore(string root, AppLog log, Func<DateTimeOffset>? clock = null)
    {
        Root = Path.GetFullPath(root);
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Root { get; }

    /// <summary>Saves a previous version; returns its entry, or <c>null</c> when it could not be written (logged).</summary>
    public async Task<McpBackupEntry?> SaveAsync(string server, string kind, string name, int npcId, string image, byte[] content, string reason)
    {
        DateTimeOffset time = _clock();
        int counter = Interlocked.Increment(ref _counter) % 1000;
        string escapedName = AppPaths.EscapeFileName(name);
        if (escapedName.Length > MaximumNameLength) escapedName = escapedName[..MaximumNameLength];
        string id = AppPaths.EscapeFileName(server.Length == 0 ? "server" : server) + "/" + time.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
            + counter.ToString("D3", CultureInfo.InvariantCulture) + "-" + AppPaths.EscapeFileName(kind) + "-" + escapedName;
        var entry = new McpBackupEntry(id, server, kind, name, npcId, image, time, content.LongLength, reason);
        if (!SafePath.TryCombine(Root, id + ".data", out string? data, out _) || !SafePath.TryCombine(Root, id + ".json", out string? meta, out _)) return null;
        string json = JsonText.Write(writer => WriteEntry(writer, entry));
        try
        {
            await Task.Run(() =>
            {
                AtomicFile.WriteAllBytes(data, content, userOnly: true);
                AtomicFile.WriteAllText(meta, json, userOnly: true);
            }).ConfigureAwait(true);
            return entry;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warn("An MCP backup could not be written to " + data, exception);
            return null;
        }
    }

    /// <summary>The backups, newest first.</summary>
    public async Task<IReadOnlyList<McpBackupEntry>> ListAsync(string server, string query, int limit)
    {
        return await Task.Run(() =>
        {
            var entries = new List<McpBackupEntry>();
            if (!Directory.Exists(Root)) return (IReadOnlyList<McpBackupEntry>)entries;
            IEnumerable<string> folders = server.Length > 0
                ? new[] { Path.Combine(Root, AppPaths.EscapeFileName(server)) }.Where(Directory.Exists)
                : Directory.EnumerateDirectories(Root);
            foreach (string folder in folders)
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
                {
                    McpBackupEntry? entry = ReadEntry(file);
                    if (entry is null) continue;
                    if (query.Length > 0 && entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 && entry.Kind.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    entries.Add(entry);
                }
            }
            return entries.OrderByDescending(entry => entry.Id[(entry.Id.IndexOf('/') + 1)..], StringComparer.Ordinal).Take(Math.Max(1, limit)).ToArray();
        }).ConfigureAwait(true);
    }

    /// <summary>A backup and its bytes.</summary>
    /// <exception cref="McpToolException">There is no backup of that id.</exception>
    public async Task<(McpBackupEntry Entry, byte[] Content)> ReadAsync(string id)
    {
        if (!SafePath.TryCombine(Root, id + ".json", out string? meta, out _) || !SafePath.TryCombine(Root, id + ".data", out string? data, out _))
            throw new McpToolException("No MCP backup has the id \"" + id + "\".");
        return await Task.Run(() =>
        {
            McpBackupEntry? entry = File.Exists(meta) && File.Exists(data) ? ReadEntry(meta) : null;
            if (entry is null) throw new McpToolException("No MCP backup has the id \"" + id + "\".");
            return (entry, File.ReadAllBytes(data));
        }).ConfigureAwait(true);
    }

    private McpBackupEntry? ReadEntry(string file)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
            string id = relative.EndsWith(".json", StringComparison.Ordinal) ? relative[..^5] : relative;
            return new McpBackupEntry(id, Text(root, "server"), Text(root, "kind"), Text(root, "name"), root.TryGetProperty("npcId", out JsonElement npc) && npc.TryGetInt32(out int value) ? value : 0,
                Text(root, "image"), root.TryGetProperty("time", out JsonElement time) && time.TryGetInt64(out long seconds) ? DateTimeOffset.FromUnixTimeMilliseconds(seconds) : DateTimeOffset.MinValue,
                root.TryGetProperty("size", out JsonElement size) && size.TryGetInt64(out long bytes) ? bytes : 0, Text(root, "reason"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentOutOfRangeException or FormatException)
        {
            // A file of the backup folder that is not one of ours (or was changed by hand) is left out; it never breaks the list.
            return null;
        }

        static string Text(JsonElement root, string key) => root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? McpArguments.Text(value) ?? string.Empty : string.Empty;
    }

    internal static void WriteEntry(Utf8JsonWriter writer, McpBackupEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("id", JsonText.Clean(entry.Id));
        writer.WriteString("server", JsonText.Clean(entry.Server));
        writer.WriteString("kind", entry.Kind);
        writer.WriteString("name", JsonText.Clean(entry.Name));
        if (entry.NpcId != 0) writer.WriteNumber("npcId", entry.NpcId);
        if (entry.Image.Length > 0) writer.WriteString("image", JsonText.Clean(entry.Image));
        writer.WriteNumber("time", entry.Time.ToUnixTimeMilliseconds());
        writer.WriteNumber("size", entry.Size);
        writer.WriteString("reason", entry.Reason);
        writer.WriteEndObject();
    }
}

/// <summary>
/// The local folders MCP tools may read and write (the option <c>mcpfileroots</c>: folders separated by <c>;</c> or line breaks). A
/// relative root is taken inside the MCP workspace folder, so the default <c>.</c> is the workspace itself. A local path given to a tool
/// is either absolute and inside a root, or relative to the first root; <c>..</c> and links that lead out of every root are refused.
/// </summary>
internal sealed class McpLocalFiles
{
    public McpLocalFiles(string workspace, string rootsOption)
    {
        Workspace = Path.GetFullPath(workspace);
        var roots = new List<string>();
        foreach (string raw in (rootsOption ?? string.Empty).Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(Workspace, raw)));
            if (!roots.Contains(root, PathComparer)) roots.Add(root);
        }
        Roots = roots;
    }

    public string Workspace { get; }

    public IReadOnlyList<string> Roots { get; }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The full path of a local path an MCP client gave.</summary>
    /// <exception cref="McpToolException">No roots are configured, or the path is outside every root.</exception>
    public string Resolve(string path)
    {
        if (Roots.Count == 0) throw new McpToolException("No local folders are allowed: set mcpfileroots in the RC options.");
        if (string.IsNullOrWhiteSpace(path)) return EnsureRoot(Roots[0]);
        string full;
        if (Path.IsPathRooted(path))
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        else
        {
            if (!SafePath.TryCombine(Roots[0], path, out string? combined, out string? reason)) throw new McpToolException("The local path is refused: " + reason);
            full = combined;
        }
        string? root = Roots.FirstOrDefault(candidate => IsInside(candidate, full));
        if (root is null) throw new McpToolException("The local path \"" + path + "\" is outside the folders MCP may use (" + string.Join("; ", Roots) + ").");
        CheckLinks(root, full);
        if (string.Equals(root, Roots[0], PathComparison)) EnsureRoot(root);
        return full;
    }

    /// <summary>
    /// A file inside a local folder (the result of <see cref="Resolve"/>): the relative path may not leave the folder (<c>..</c>, an absolute
    /// path), and no existing part of it may be a link that leads out of the root the folder is in.
    /// </summary>
    /// <exception cref="McpToolException">The path is refused.</exception>
    public string ResolveUnder(string folder, string relative)
    {
        if (!SafePath.TryCombine(folder, relative, out string? full, out string? reason)) throw new McpToolException("The local path " + relative + " is refused: " + reason);
        string? root = Roots.FirstOrDefault(candidate => IsInside(candidate, full));
        if (root is null) throw new McpToolException("The local path \"" + full + "\" is outside the folders MCP may use.");
        CheckLinks(root, full);
        return full;
    }

    private string EnsureRoot(string root)
    {
        if (string.Equals(root, Workspace, PathComparison) || root.StartsWith(Workspace + Path.DirectorySeparatorChar, PathComparison))
        {
            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new McpToolException("The MCP workspace folder could not be made: " + exception.Message);
            }
        }
        return root;
    }

    private static bool IsInside(string root, string full) => string.Equals(root, full, PathComparison) || full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>
    /// Refuses a path that really leads out of its root: both are resolved as the system resolves them (every link on the way, also links
    /// inside a link's own target, and <c>..</c> after a link), and the real path must lie inside the real root. A path that does not exist
    /// yet counts from its last existing part.
    /// </summary>
    /// <exception cref="McpToolException">The path leads out of the root, or its links cannot be followed (a loop).</exception>
    private static void CheckLinks(string root, string full)
    {
        string realRoot;
        string real;
        try
        {
            realRoot = RealPath(root);
            real = RealPath(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new McpToolException("The local path \"" + full + "\" goes through a link that cannot be followed: " + exception.Message);
        }
        if (!IsInside(realRoot, real))
            throw new McpToolException("The local path \"" + full + "\" goes through a link that leads out of " + root + " (to " + real + ").");
    }

    /// <summary>The real path of <paramref name="path"/>: each part from the top is looked at on disk, a link is replaced by its own target (resolved again, part by part), <c>..</c> goes to the real parent.</summary>
    /// <exception cref="IOException">There are too many links (a loop).</exception>
    internal static string RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full) ?? string.Empty;
        var pending = new LinkedList<string>(full[current.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries));
        int links = 0;
        while (pending.First is { } first)
        {
            string segment = first.Value;
            pending.RemoveFirst();
            if (segment == ".") continue;
            if (segment == "..")
            {
                current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)) ?? current;
                continue;
            }
            string next = Path.Combine(current, segment);
            string? target = new FileInfo(next).LinkTarget;
            if (target is null)
            {
                if (!File.Exists(next) && !Directory.Exists(next))
                {
                    // The rest does not exist yet, so it holds no link; a ".." in it is taken as text (as a later write would take it).
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(new[] { next }.Concat(pending).ToArray())));
                }
                current = next;
                continue;
            }
            if (++links > 40) throw new IOException("Too many links (a loop?) at " + next);
            if (Path.IsPathRooted(target)) current = Path.GetPathRoot(target) ?? current;
            string[] parts = target[(Path.IsPathRooted(target) ? (Path.GetPathRoot(target) ?? string.Empty).Length : 0)..]
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = parts.Length - 1; index >= 0; index--) pending.AddFirst(parts[index]);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
}
