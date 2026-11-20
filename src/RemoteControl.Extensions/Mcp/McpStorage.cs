using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
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
/// A root lexically inside another root counts only while it really lies inside that one too (a link there is no root of its own).
/// What this cannot see: a link put in place between the check of a path and a write to it (reads go through <see cref="ReadAsync"/>,
/// which checks the open file), and a hard link to a file outside the roots, which is a file of the folder like any other.
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

    /// <summary>How the system compares file names: without case on Windows and macOS, with case elsewhere.</summary>
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The comparison of <see cref="PathComparer"/>.</summary>
    internal static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
        string? root = RootOf(full);
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
        string? root = RootOf(full);
        if (root is null) throw new McpToolException("The local path \"" + full + "\" is outside the folders MCP may use.");
        CheckLinks(root, full);
        return full;
    }

    /// <summary>
    /// Reads a local file (a path of <see cref="Resolve"/> or <see cref="ResolveUnder"/>) through one open handle, after checking where
    /// that handle really is: a link put in place of the file after its path was checked is refused. Where the system cannot tell the path
    /// of an open file (macOS) the check of the path stands alone.
    /// </summary>
    /// <exception cref="McpToolException">The file is outside the roots, or what was opened is.</exception>
    /// <exception cref="IOException">It could not be read.</exception>
    public async Task<byte[]> ReadAsync(string full, CancellationToken token)
    {
        string root = RootOf(full) ?? throw new McpToolException("The local path \"" + full + "\" is outside the folders MCP may use.");
        CheckLinks(root, full);
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        string? opened = OpenedPath(stream.SafeFileHandle);
        if (opened is not null)
        {
            string realRoot;
            try
            {
                realRoot = RealPath(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new McpToolException("The local folder " + root + " goes through a link that cannot be followed: " + exception.Message);
            }
            if (!IsInside(realRoot, opened)) throw new McpToolException("The local file \"" + full + "\" leads out of " + root + " (to " + opened + "); it was not read.");
        }
        using var content = new MemoryStream();
        await stream.CopyToAsync(content, token).ConfigureAwait(true);
        return content.ToArray();
    }

    /// <summary>
    /// The root a full path is in: the first that holds it and is trusted. A root inside another root (lexically) is trusted only while its
    /// real path lies inside that one's real path; otherwise the path is judged by the outer root, whose link check refuses it.
    /// </summary>
    private string? RootOf(string full)
    {
        foreach (string candidate in Roots)
        {
            if (!IsInside(candidate, full)) continue;
            if (Roots.Any(outer => !string.Equals(outer, candidate, PathComparison) && IsInside(outer, candidate) && !RealInside(outer, candidate))) continue;
            return candidate;
        }
        return null;
    }

    /// <summary>Whether the real path of <paramref name="inner"/> lies inside the real path of <paramref name="outer"/> (false when either cannot be followed).</summary>
    private static bool RealInside(string outer, string inner)
    {
        try
        {
            return IsInside(RealPath(outer), RealPath(inner));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The real path of an open file, or <c>null</c> where the system does not tell it.</summary>
    private static string? OpenedPath(SafeFileHandle handle)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                bool added = false;
                handle.DangerousAddRef(ref added);
                try
                {
                    string? target = new FileInfo("/proc/self/fd/" + handle.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture)).LinkTarget;
                    return target is not null && Path.IsPathRooted(target) ? target : null;
                }
                finally
                {
                    if (added) handle.DangerousRelease();
                }
            }
            if (OperatingSystem.IsWindows())
            {
                var buffer = new char[1024];
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
                if (length >= buffer.Length)
                {
                    buffer = new char[length + 1];
                    length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
                }
                if (length == 0 || length >= buffer.Length) return null;
                return WithoutDevicePrefix(new string(buffer, 0, (int)length));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return null;
        }
        return null;
    }

    /// <summary>A Windows path without its <c>\\?\</c> prefix: <c>\\?\C:\a</c> is <c>C:\a</c>, <c>\\?\UNC\host\share</c> is <c>\\host\share</c>.</summary>
    internal static string WithoutDevicePrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

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

    /// <summary>Whether <paramref name="full"/> is <paramref name="root"/> or inside it (a root that ends with a separator, <c>/</c> or <c>E:\</c>, holds every path that starts with it).</summary>
    internal static bool IsInside(string root, string full) => string.Equals(root, full, PathComparison)
        || full.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison);

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
            string[] parts;
            if (OperatingSystem.IsWindows())
            {
                // Windows takes a link's target as text: a relative one from the link's folder with its ".." collapsed, a root-relative
                // one (\foo) on the drive of the link; the result is walked again from its root.
                string resolved = WindowsLinkTarget(current, WithoutDevicePrefix(target));
                current = Path.GetPathRoot(resolved) ?? current;
                parts = resolved[current.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            }
            else
            {
                if (Path.IsPathRooted(target)) current = Path.GetPathRoot(target) ?? current;
                parts = target[(Path.IsPathRooted(target) ? (Path.GetPathRoot(target) ?? string.Empty).Length : 0)..]
                    .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            }
            for (int index = parts.Length - 1; index >= 0; index--) pending.AddFirst(parts[index]);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    /// <summary>
    /// The full path a Windows link in <paramref name="folder"/> leads to: a fully qualified target as it is, a root-relative one
    /// (<c>\foo</c>) on the folder's drive, a relative one from the folder; <c>..</c> collapsed as text, as Windows does.
    /// </summary>
    internal static string WindowsLinkTarget(string folder, string target)
    {
        string combined = Path.IsPathFullyQualified(target) ? target
            : Path.IsPathRooted(target) && target.Length > 0 && target[0] is '\\' or '/' ? Path.Combine(Path.GetPathRoot(folder) ?? string.Empty, target.TrimStart('\\', '/'))
            : Path.Combine(folder, target);
        return Path.GetFullPath(combined);
    }
}
