using RemoteControl.Protocol.Session;

namespace RemoteControl.Ui.Files;

/// <summary>Server paths of the file browser: folders end in <c>/</c>, files are <c>folder/name</c>.</summary>
internal static class RemotePaths
{
    /// <summary>
    /// The folder as the browser sends it (<c>navigateTo</c>, <c>TFileBrowserTree.cpp:868-907</c>): <c>\</c> becomes <c>/</c>, leading
    /// <c>/</c> are removed, <c>//</c> collapse, <c>.</c> is the root (empty), and a folder ends in <c>/</c>.
    /// </summary>
    /// <param name="folder">The folder as typed or received.</param>
    /// <returns>The normalised folder, or empty for the root.</returns>
    public static string NormalizeFolder(string? folder)
    {
        string value = (folder ?? string.Empty).Trim().Replace('\\', '/');
        while (value.Contains("//", StringComparison.Ordinal)) value = value.Replace("//", "/", StringComparison.Ordinal);
        value = value.TrimStart('/');
        if (value == "." || value == "./") value = string.Empty;
        if (value.Length != 0 && !value.EndsWith('/')) value += "/";
        return value;
    }

    /// <summary>The parent of a folder (<c>onAddressUp</c>): <c>levels/sub/</c> gives <c>levels/</c>, <c>levels/</c> gives the root.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The parent folder.</returns>
    public static string Parent(string? folder)
    {
        string value = NormalizeFolder(folder).TrimEnd('/');
        int slash = value.LastIndexOf('/');
        return slash < 0 ? string.Empty : value[..(slash + 1)];
    }

    /// <summary>The last part of a path (<c>g_path_get_basename</c>).</summary>
    /// <param name="path">The path.</param>
    /// <returns>The name.</returns>
    public static string NameOf(string? path)
    {
        string value = (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        int slash = value.LastIndexOf('/');
        return slash < 0 ? value : value[(slash + 1)..];
    }

    /// <summary>The folder part of a file path, ending in <c>/</c>, or empty.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The folder.</returns>
    public static string FolderOf(string? path)
    {
        string value = (path ?? string.Empty).Replace('\\', '/');
        int slash = value.LastIndexOf('/');
        return slash < 0 ? string.Empty : NormalizeFolder(value[..(slash + 1)]);
    }

    /// <summary>A file in a folder.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="name">The name.</param>
    /// <returns><c>folder/name</c>.</returns>
    public static string Combine(string? folder, string? name) => NormalizeFolder(folder) + (name ?? string.Empty).Replace('\\', '/').TrimStart('/');

    /// <summary>Whether two paths name the same file (<c>pathMatches</c>: equal, or one ends with <c>/</c> and the other).</summary>
    /// <param name="expected">One path.</param>
    /// <param name="received">The other.</param>
    /// <returns>Whether they match.</returns>
    public static bool Matches(string? expected, string? received)
    {
        string a = expected ?? string.Empty;
        string b = received ?? string.Empty;
        if (a.Length == 0 || b.Length == 0) return false;
        return a == b || b.EndsWith("/" + a, StringComparison.Ordinal) || a.EndsWith("/" + b, StringComparison.Ordinal);
    }

    /// <summary>
    /// The folder of a folder rule (<c>refreshFolders</c>): the pattern cut at the first <c>*</c>, without trailing <c>/</c>
    /// (<c>levels/*.nw</c> gives <c>levels</c>); empty when nothing is left.
    /// </summary>
    /// <param name="pattern">The rule's pattern.</param>
    /// <returns>The folder without trailing <c>/</c>.</returns>
    public static string FolderOfRule(string? pattern)
    {
        string value = (pattern ?? string.Empty).Replace('\\', '/');
        int wildcard = value.IndexOf('*');
        if (wildcard >= 0) value = value[..wildcard];
        return value.TrimEnd('/').TrimStart('/');
    }

    /// <summary>The distinct folders of the folder rules, in rule order.</summary>
    /// <param name="folders">The rules.</param>
    /// <returns>The folders without trailing <c>/</c>.</returns>
    public static IReadOnlyList<string> FoldersOfRules(IEnumerable<FileBrowserFolder> folders)
    {
        var result = new List<string>();
        foreach (FileBrowserFolder folder in folders)
        {
            string path = FolderOfRule(folder.Pattern);
            if (path.Length != 0 && !result.Contains(path, StringComparer.Ordinal)) result.Add(path);
        }
        return result;
    }

    /// <summary>The direct subfolders of a folder among the rule folders (the modern view's folder items).</summary>
    /// <param name="ruleFolders">The rule folders without trailing <c>/</c>.</param>
    /// <param name="folder">The current folder.</param>
    /// <returns>The names of the subfolders.</returns>
    public static IReadOnlyList<string> SubfoldersOf(IEnumerable<string> ruleFolders, string? folder)
    {
        string prefix = NormalizeFolder(folder);
        var result = new List<string>();
        foreach (string path in ruleFolders)
        {
            if (prefix.Length != 0 && !path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string remainder = path[prefix.Length..];
            int slash = remainder.IndexOf('/');
            if (slash >= 0) remainder = remainder[..slash];
            if (remainder.Length == 0) continue;
            if (!result.Contains(remainder, StringComparer.Ordinal)) result.Add(remainder);
        }
        return result;
    }
}
