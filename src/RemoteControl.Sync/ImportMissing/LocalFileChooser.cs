using RemoteControl.Protocol.Session;

namespace RemoteControl.Sync.ImportMissing;

/// <summary>The local file chosen for a missing file, and the other files of that name.</summary>
/// <param name="Path">The chosen file's full path.</param>
/// <param name="Alternatives">The other files of the name, in the order of preference.</param>
public sealed record LocalChoice(string Path, IReadOnlyList<string> Alternatives);

/// <summary>Chooses among several local files of one name (the Import Missing specification, section 5).</summary>
public static class LocalFileChooser
{
    /// <summary>The folder name that marks a file as the right one for a kind, or <c>null</c> when the kind has none.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The folder name.</returns>
    public static string? KindFolder(MissingFileKind kind) => kind switch
    {
        MissingFileKind.Head => "heads",
        MissingFileKind.Body => "bodies",
        MissingFileKind.Sword => "swords",
        MissingFileKind.Shield => "shields",
        MissingFileKind.Hat => "hats",
        MissingFileKind.Gani => "ganis",
        MissingFileKind.Sound => "sounds",
        MissingFileKind.Image => "images",
        MissingFileKind.Level => "levels",
        _ => null,
    };

    /// <summary>
    /// The one whose path has a folder named like the kind (any case) wins, then the shortest path, then the path's ordinal order. Only
    /// the folders below <paramref name="root"/> count, so a root that is itself called <c>images</c> does not make every file an image.
    /// </summary>
    /// <param name="kind">What the missing file is.</param>
    /// <param name="root">The folder that was searched.</param>
    /// <param name="paths">The full paths of the files of the name.</param>
    /// <returns>The choice, or <c>null</c> when there is no file.</returns>
    public static LocalChoice? Choose(MissingFileKind kind, string root, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        string? marker = KindFolder(kind);
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool HasKindFolder(string path)
        {
            if (marker is null) return false;
            string relative = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
            string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return segments.Take(segments.Length - 1).Any(segment => string.Equals(segment, marker, StringComparison.OrdinalIgnoreCase));
        }

        List<string> ordered = paths
            .OrderByDescending(HasKindFolder)
            .ThenBy(path => path.Length)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();
        return new LocalChoice(ordered[0], ordered.Skip(1).ToList());
    }
}
