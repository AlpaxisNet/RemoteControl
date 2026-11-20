using RemoteControl.Protocol.Session;

namespace RemoteControl.Sync.ImportMissing;

/// <summary>Why a missing file is not ticked for upload.</summary>
public enum ImportProblem
{
    /// <summary>Nothing: the row is ticked by default.</summary>
    None,

    /// <summary>The server's folder configuration has no line for a file of this name.</summary>
    NoServerFolder,

    /// <summary>The server's name or folder is not a plain file name and a plain relative folder (rule 7 of AGENTS.md).</summary>
    UnsafeServerPath,

    /// <summary>No file of this name was found in the folder that was searched.</summary>
    NotFound,

    /// <summary>The local file is empty (the server does not take an empty file through the file browser).</summary>
    Empty,

    /// <summary>The local file is larger than the limit the server advertises.</summary>
    TooLarge,

    /// <summary>No folder right of the account with <c>w</c> covers the file.</summary>
    NoRight,
}

/// <summary>One row of the import list: a missing file of the server, and the local file chosen for it.</summary>
/// <param name="Missing">The server's values.</param>
/// <param name="LocalPath">The chosen local file's full path, or <c>null</c> when none was found.</param>
/// <param name="LocalSize">Its size in bytes (0 when there is none).</param>
/// <param name="Alternatives">The other local files of the name, in the order of preference.</param>
/// <param name="Problem">Why the row is not ticked by default; <see cref="ImportProblem.None"/> when it is.</param>
public sealed record ImportRow(MissingFile Missing, string? LocalPath, long LocalSize, IReadOnlyList<string> Alternatives, ImportProblem Problem)
{
    /// <summary>Whether the row is ticked by default.</summary>
    public bool TickedByDefault => Problem == ImportProblem.None;

    /// <summary>The reason the row is not ticked, in words; empty when it is.</summary>
    public string Reason => Problem switch
    {
        ImportProblem.NoServerFolder => "no folder on the server: its folder configuration has no line for this file",
        ImportProblem.UnsafeServerPath => "the server's name or folder is not a plain name and relative folder",
        ImportProblem.NotFound => "not found in the folder",
        ImportProblem.Empty => "the file is empty",
        ImportProblem.TooLarge => "larger than the server's limit",
        ImportProblem.NoRight => "no folder right with w for it",
        _ => string.Empty,
    };

    /// <summary>The server path the file would go to: the folder and the name.</summary>
    public string ServerPath => (Missing.Folder.EndsWith('/') || Missing.Folder.Length == 0 ? Missing.Folder : Missing.Folder + "/") + Missing.Name;
}

/// <summary>The list the admin confirms: every missing file with its local file, and what the search found.</summary>
/// <param name="Rows">The rows, in the server's order.</param>
/// <param name="FoldersSkipped">How many local folders could not be read.</param>
public sealed record ImportPlan(IReadOnlyList<ImportRow> Rows, int FoldersSkipped)
{
    /// <summary>The rows ticked by default.</summary>
    public IEnumerable<ImportRow> TickedByDefault => Rows.Where(row => row.TickedByDefault);

    /// <summary>What the admin is asked to confirm before anything is uploaded: how many files and how many bytes.</summary>
    /// <param name="rows">The rows that are ticked.</param>
    /// <returns>The count and the total size.</returns>
    public static (int Count, long Bytes) Summarize(IEnumerable<ImportRow> rows)
    {
        int count = 0;
        long bytes = 0;
        foreach (ImportRow row in rows)
        {
            count++;
            bytes += row.LocalSize;
        }

        return (count, bytes);
    }
}

/// <summary>Makes the import list from the server's missing files and a local index (the Import Missing specification, section 5).</summary>
public static class ImportPlanner
{
    /// <summary>Makes the rows.</summary>
    /// <param name="files">The server's missing files.</param>
    /// <param name="index">The index of the local folder.</param>
    /// <param name="rights">The account's folder rights (the folder list of the file browser).</param>
    /// <param name="maxUploadSize">The server's upload limit in bytes; 0 when the server named none (nothing is checked against it).</param>
    /// <returns>The plan.</returns>
    public static ImportPlan Plan(IEnumerable<MissingFile> files, LocalFileIndex index, IReadOnlyList<FileBrowserFolder> rights, long maxUploadSize)
    {
        var rows = new List<ImportRow>();
        foreach (MissingFile file in files)
        {
            LocalChoice? choice = IsPlainName(file.Name) ? LocalFileChooser.Choose(file.Kind, index.Root, index.Find(file.Name)) : null;
            rows.Add(Evaluate(file, choice?.Path, choice?.Alternatives ?? Array.Empty<string>(), rights, maxUploadSize));
        }

        return new ImportPlan(rows, index.FoldersSkipped);
    }

    /// <summary>The row for another of the alternatives (the admin picked it).</summary>
    /// <param name="row">The row.</param>
    /// <param name="path">The alternative's full path.</param>
    /// <param name="rights">The account's folder rights.</param>
    /// <param name="maxUploadSize">The server's upload limit in bytes; 0 when the server named none (nothing is checked against it).</param>
    /// <returns>The new row; the old choice becomes an alternative.</returns>
    public static ImportRow WithLocalFile(ImportRow row, string path, IReadOnlyList<FileBrowserFolder> rights, long maxUploadSize)
    {
        var others = row.Alternatives.Where(item => item != path).ToList();
        if (row.LocalPath is not null && row.LocalPath != path) others.Insert(0, row.LocalPath);
        return Evaluate(row.Missing, path, others, rights, maxUploadSize);
    }

    private static ImportRow Evaluate(MissingFile file, string? path, IReadOnlyList<string> alternatives, IReadOnlyList<FileBrowserFolder> rights, long maxUploadSize)
    {
        long size = 0;
        if (path is not null && !TryGetSize(path, out size)) path = null;
        ImportProblem problem;
        if (!IsPlainName(file.Name) || (file.Folder.Length > 0 && !IsPlainFolder(file.Folder))) problem = ImportProblem.UnsafeServerPath;
        else if (file.Folder.Length == 0) problem = ImportProblem.NoServerFolder;
        else if (path is null) problem = ImportProblem.NotFound;
        else if (size == 0) problem = ImportProblem.Empty;
        else if (maxUploadSize > 0 && size > maxUploadSize) problem = ImportProblem.TooLarge;
        else if (!rights.Any(rule => rule.AllowsWrite && rule.Covers(file.Folder, file.Name))) problem = ImportProblem.NoRight;
        else problem = ImportProblem.None;
        return new ImportRow(file, path, size, alternatives, problem);
    }

    private static bool TryGetSize(string path, out long size)
    {
        try
        {
            var info = new FileInfo(path);
            size = info.Exists ? info.Length : 0;
            return info.Exists;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            size = 0;
            return false;
        }
    }

    /// <summary>A file name the server sent: not empty, not <c>.</c> or <c>..</c>, no separator, drive colon or control character.</summary>
    /// <param name="name">The name.</param>
    /// <returns>Whether it is plain.</returns>
    public static bool IsPlainName(string name) =>
        name.Length > 0 && name is not "." and not ".." && !name.Any(c => c is '/' or '\\' or ':' or '\0' || char.IsControl(c));

    /// <summary>A folder the server sent: relative (no leading slash, no drive), no <c>..</c> or <c>.</c> part, no backslash or control character.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>Whether it is plain.</returns>
    public static bool IsPlainFolder(string folder) =>
        folder.Length > 0 && !folder.StartsWith('/') && !folder.Any(c => c is '\\' or ':' or '\0' || char.IsControl(c))
        && folder.Split('/', StringSplitOptions.RemoveEmptyEntries).All(part => part is not "." and not "..");
}
