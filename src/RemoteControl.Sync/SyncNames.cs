using System.Globalization;
using System.Text;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Sync;

/// <summary>
/// The names the C++ sync manager gave files and settings (<c>TSyncManager.cpp:24-150</c>). Names work on the server's single bytes
/// (Windows-1252, as RClib passed them on), so a workspace the C++ created keeps its file names.
/// </summary>
public static class SyncNames
{
    /// <summary>The folder of weapon documents.</summary>
    public const string WeaponsFolder = "weapons";

    /// <summary>The folder of class scripts.</summary>
    public const string ClassesFolder = "classes";

    /// <summary>The folder of NPC documents.</summary>
    public const string NpcsFolder = "npcs";

    /// <summary>The folder of synchronised server files.</summary>
    public const string FilesFolder = "files";

    /// <summary>The folder of the server options, flags and folder configuration.</summary>
    public const string ConfigFolder = "config";

    /// <summary>The engine's own folder (manifest, conflict copies, temporary git helpers).</summary>
    public const string MetadataFolder = ".graal-sync";

    /// <summary>The folders a workspace has, in the order the C++ created them.</summary>
    public static IReadOnlyList<string> WorkspaceFolders { get; } = [MetadataFolder, WeaponsFolder, ClassesFolder, NpcsFolder, FilesFolder, ConfigFolder];

    /// <summary>
    /// The Graal file-name encoding (<c>encodeGraalFilename</c>): ASCII letters and digits are kept, every other byte becomes <c>%</c>
    /// and its decimal value in three digits (a space is <c>%032</c>). The result never contains a separator, a dot or a reserved name.
    /// </summary>
    /// <param name="value">The name.</param>
    /// <returns>The encoded name.</returns>
    public static string EncodeGraalFileName(string? value)
    {
        var result = new StringBuilder();
        foreach (byte character in RcText.GetBytes(value))
        {
            if (IsAsciiAlphanumeric(character)) result.Append((char)character);
            else result.Append('%').Append(character.ToString("D3", CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    /// <summary>
    /// The reverse of <see cref="EncodeGraalFileName"/> (<c>decodeGraalFilename</c>): <c>%</c> and three digits become that byte (modulo 256,
    /// as the C++ char arithmetic did); anything else is kept. Like the C++, a sequence at the very end of the text is not decoded.
    /// </summary>
    /// <param name="value">The encoded name.</param>
    /// <returns>The name.</returns>
    public static string DecodeGraalFileName(string? value)
    {
        string text = value ?? string.Empty;
        byte[] bytes = RcText.GetBytes(text);
        var decoded = new List<byte>(bytes.Length);
        for (int index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == '%' && index + 3 < bytes.Length && IsDigit(bytes[index + 1]) && IsDigit(bytes[index + 2]) && IsDigit(bytes[index + 3]))
            {
                decoded.Add(unchecked((byte)(((bytes[index + 1] - '0') * 100) + ((bytes[index + 2] - '0') * 10) + (bytes[index + 3] - '0'))));
                index += 3;
            }
            else
            {
                decoded.Add(bytes[index]);
            }
        }

        return RcText.GetString(decoded.ToArray());
    }

    /// <summary>
    /// <c>safeName</c>: keeps ASCII letters, digits, <c>-</c>, <c>_</c> and <c>.</c>, replaces every other byte with <c>_</c>, drops trailing
    /// dots; <c>script</c> when nothing is left. Used for class file names and the settings file name.
    /// </summary>
    /// <param name="value">The name.</param>
    /// <returns>The file-name part.</returns>
    public static string SafeName(string? value)
    {
        var result = new StringBuilder();
        foreach (byte character in RcText.GetBytes(value))
            result.Append(IsAsciiAlphanumeric(character) || character is (byte)'-' or (byte)'_' or (byte)'.' ? (char)character : '_');
        while (result.Length > 0 && result[^1] is '.' or ' ') result.Length--;
        return result.Length == 0 ? "script" : result.ToString();
    }

    /// <summary>
    /// <c>stableSuffix</c>: the 64-bit FNV-1a hash of the bytes, truncated to its low 32 bits, as eight lower-case hex digits. Names the
    /// settings file of a server and the manifest group of a file. The C++ started from 1469598103934665603 (the standard offset basis
    /// without its last digit); that value is kept so existing settings files are found.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>Eight hex digits.</returns>
    public static string StableSuffix(string? value)
    {
        ulong hash = 1469598103934665603UL;
        foreach (byte character in RcText.GetBytes(value))
        {
            hash ^= character;
            hash = unchecked(hash * 1099511628211UL);
        }

        return ((uint)hash).ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <c>normalizeRemotePath</c>: trims white space, turns <c>\</c> into <c>/</c>, drops slashes at both ends and <c>.</c> segments, resolves
    /// <c>a/../b</c>; returns an empty text when a <c>..</c> would leave the root or nothing is left.
    /// </summary>
    /// <param name="path">A server path.</param>
    /// <returns>The normalised path, or empty.</returns>
    public static string NormalizeRemotePath(string? path)
    {
        string text = (path ?? string.Empty).Trim(' ', '\t', '\r', '\n').Replace('\\', '/');
        var segments = new List<string>();
        foreach (string segment in text.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return string.Empty;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    /// <summary>The folder part of a normalised remote path (empty for a top-level name).</summary>
    /// <param name="remotePath">A normalised path.</param>
    /// <returns>The parent folder.</returns>
    public static string ParentOf(string remotePath)
    {
        int slash = remotePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : remotePath[..slash];
    }

    /// <summary>The last segment of a path.</summary>
    /// <param name="path">A path with <c>/</c> separators.</param>
    /// <returns>The name.</returns>
    public static string NameOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>
    /// <c>isSafeRelativePath</c> for a path read from the manifest, made stricter: relative, no <c>..</c>, every segment acceptable to
    /// <see cref="SafePath.IsSafeSegment"/>, inside one of the folders of a workspace.
    /// </summary>
    /// <param name="relativePath">The path, with <c>/</c> separators.</param>
    /// <returns>Whether it may be used.</returns>
    public static bool IsSafeWorkspacePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath[0] is '/' or '\\') return false;
        if (relativePath.Length >= 2 && relativePath[1] == ':') return false;
        string[] segments = relativePath.Split('/');
        if (segments.Length < 2) return false;
        if (segments[0] is not (WeaponsFolder or ClassesFolder or NpcsFolder or FilesFolder or ConfigFolder)) return false;
        foreach (string segment in segments)
        {
            if (!SafePath.IsSafeSegment(segment, out _)) return false;
        }

        return true;
    }

    /// <summary>
    /// <c>splitFolders</c>: the folders of the "Remote file folders" setting, separated by <c>;</c>, <c>,</c> or line breaks, normalised,
    /// without empty ones and duplicates, in their order.
    /// </summary>
    /// <param name="value">The setting.</param>
    /// <returns>The folders.</returns>
    public static IReadOnlyList<string> SplitFolders(string? value)
    {
        var folders = new List<string>();
        foreach (string part in (value ?? string.Empty).Split(';', ',', '\n', '\r'))
        {
            string folder = NormalizeRemotePath(part);
            if (folder.Length != 0 && !folders.Contains(folder, StringComparer.Ordinal)) folders.Add(folder);
        }

        return folders;
    }

    /// <summary>Joins folders as the C++ wrote the setting: <c>"a; b"</c>.</summary>
    /// <param name="folders">The folders.</param>
    /// <returns>The setting text.</returns>
    public static string JoinFolders(IEnumerable<string> folders) => string.Join("; ", folders);

    /// <summary>The workspace folder of a script kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><c>weapons</c>, <c>classes</c> or <c>npcs</c>.</returns>
    public static string FolderOf(ScriptKind kind) => kind switch
    {
        ScriptKind.Class => ClassesFolder,
        ScriptKind.Npc => NpcsFolder,
        _ => WeaponsFolder,
    };

    /// <summary>The manifest type of a script kind: <c>weapon</c>, <c>class</c> or <c>npc</c>.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The type word.</returns>
    public static string TypeName(ScriptKind kind) => kind switch
    {
        ScriptKind.Class => "class",
        ScriptKind.Npc => "npc",
        _ => "weapon",
    };

    /// <summary>The key of a script in the manifest and the engine (<c>keyFor</c>): <c>weapon:&lt;name&gt;</c>, <c>class:&lt;name&gt;</c>, <c>npc:&lt;id&gt;</c>.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="name">The name.</param>
    /// <param name="id">The NPC id.</param>
    /// <returns>The key.</returns>
    public static string ScriptKey(ScriptKind kind, string name, int id) =>
        kind == ScriptKind.Npc ? "npc:" + id.ToString(CultureInfo.InvariantCulture) : TypeName(kind) + ":" + name;

    /// <summary>
    /// The workspace path of a script as the C++ named it (<c>addOrUpdateEntry</c>): <c>weapons/weapon&lt;enc(name)&gt;.txt</c>,
    /// <c>classes/&lt;safeName(name)&gt;.txt</c>, <c>npcs/npc&lt;enc(name)&gt;.txt</c>.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="name">The name.</param>
    /// <returns>The relative path with <c>/</c>.</returns>
    public static string ScriptPath(ScriptKind kind, string name) => kind switch
    {
        ScriptKind.Class => ClassesFolder + "/" + SafeName(name) + ".txt",
        ScriptKind.Npc => NpcsFolder + "/npc" + EncodeGraalFileName(name) + ".txt",
        _ => WeaponsFolder + "/weapon" + EncodeGraalFileName(name) + ".txt",
    };

    /// <summary>
    /// The path used instead of <see cref="ScriptPath"/> when that one is refused (a class named <c>con</c>) or already used by another
    /// script (two classes <c>a/b</c> and <c>a_b</c>, two NPCs with one name): the stem gets <c>-&lt;id or stableSuffix(name)&gt;</c>.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="name">The name.</param>
    /// <param name="id">The NPC id.</param>
    /// <returns>The relative path with <c>/</c>.</returns>
    public static string AlternateScriptPath(ScriptKind kind, string name, int id)
    {
        string path = ScriptPath(kind, name);
        string suffix = kind == ScriptKind.Npc ? id.ToString(CultureInfo.InvariantCulture) : StableSuffix(name);
        return path[..^4] + "-" + suffix + ".txt";
    }

    /// <summary><c>lowerAscii</c>: lower-cases ASCII letters only.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The lower-case text.</returns>
    public static string LowerAscii(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (char character in value) result.Append(character is >= 'A' and <= 'Z' ? (char)(character + 32) : character);
        return result.ToString();
    }

    private static bool IsAsciiAlphanumeric(byte value) => value is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';
}
