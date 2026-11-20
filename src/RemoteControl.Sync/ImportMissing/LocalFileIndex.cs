using System.IO.Enumeration;

namespace RemoteControl.Sync.ImportMissing;

/// <summary>How far a <see cref="LocalFileIndex"/> scan is.</summary>
/// <param name="Folders">Folders read so far.</param>
/// <param name="Files">Files seen so far.</param>
/// <param name="Skipped">Folders that could not be read so far.</param>
public readonly record struct LocalScanProgress(int Folders, long Files, int Skipped);

/// <summary>
/// An index of the files under a folder by file name (without regard to case), for finding the local copies of the files a server is
/// missing. It holds only names and folder paths, never content: a folder of several hundred thousand files needs a few tens of
/// megabytes. A link to a folder is not followed, and a folder that cannot be read is skipped and counted.
/// </summary>
public sealed class LocalFileIndex
{
    private readonly struct Entry(string name, int folder)
    {
        public string Name { get; } = name;

        public int Folder { get; } = folder;
    }

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly Entry[] _entries;
    private readonly string[] _folders;

    private LocalFileIndex(string root, Entry[] entries, string[] folders, long filesSeen, int foldersRead, int foldersSkipped)
    {
        Root = root;
        _entries = entries;
        _folders = folders;
        FilesSeen = filesSeen;
        FoldersRead = foldersRead;
        FoldersSkipped = foldersSkipped;
    }

    /// <summary>The folder that was searched, as a full path.</summary>
    public string Root { get; }

    /// <summary>How many files the search saw (also those the index does not hold when it was limited to wanted names).</summary>
    public long FilesSeen { get; }

    /// <summary>How many files the index holds.</summary>
    public int Count => _entries.Length;

    /// <summary>How many folders were read.</summary>
    public int FoldersRead { get; }

    /// <summary>How many folders could not be read and were skipped.</summary>
    public int FoldersSkipped { get; }

    /// <summary>
    /// Searches <paramref name="root"/> recursively on a background task. Cancelling throws <see cref="OperationCanceledException"/>; a
    /// root that is not a folder gives an empty index with one skipped folder.
    /// </summary>
    /// <param name="root">The folder to search.</param>
    /// <param name="wanted">When given, only files of these names (without regard to case) are held, which keeps the index small.</param>
    /// <param name="progress">Told now and then how far the search is, from the background task (a <see cref="Progress{T}"/> posts to its context).</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The index.</returns>
    public static Task<LocalFileIndex> BuildAsync(string root, IReadOnlySet<string>? wanted = null, IProgress<LocalScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Build(root, wanted, progress, cancellationToken), cancellationToken);

    private static LocalFileIndex Build(string root, IReadOnlySet<string>? wanted, IProgress<LocalScanProgress>? progress, CancellationToken token)
    {
        string full = Path.GetFullPath(root);
        var entries = new List<Entry>();
        var folders = new List<string>();
        var pending = new Stack<string>();
        long seen = 0;
        int read = 0, skipped = 0;
        HashSet<string>? wantedSet = wanted is null ? null : new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        pending.Push(full);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string folder = pending.Pop();
            int folderIndex = -1;
            try
            {
                var found = new FileSystemEnumerable<(string Name, bool IsDirectory, bool IsLink)>(
                    folder, static (ref FileSystemEntry entry) => (entry.FileName.ToString(), entry.IsDirectory, (entry.Attributes & FileAttributes.ReparsePoint) != 0), Options);
                foreach ((string name, bool isDirectory, bool isLink) in found)
                {
                    if (isDirectory)
                    {
                        if (!isLink) pending.Push(Path.Combine(folder, name));
                        continue;
                    }

                    seen++;
                    if (wantedSet is not null && !wantedSet.Contains(name)) continue;
                    if (folderIndex < 0)
                    {
                        folderIndex = folders.Count;
                        folders.Add(folder);
                    }

                    entries.Add(new Entry(name, folderIndex));
                    if ((seen & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
                }

                read++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                skipped++;
            }

            progress?.Report(new LocalScanProgress(read, seen, skipped));
        }

        Entry[] sorted = entries.ToArray();
        Array.Sort(sorted, static (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return new LocalFileIndex(full, sorted, folders.ToArray(), seen, read, skipped);
    }

    /// <summary>The full paths of the files of this name (without regard to case), in no particular order.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>The paths; empty when there is none.</returns>
    public IReadOnlyList<string> Find(string name)
    {
        int low = 0, high = _entries.Length - 1, hit = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            int order = string.Compare(_entries[middle].Name, name, StringComparison.OrdinalIgnoreCase);
            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                if (order == 0) hit = middle;
                high = middle - 1;
            }
        }

        if (hit < 0) return Array.Empty<string>();
        var paths = new List<string>();
        for (int index = hit; index < _entries.Length && string.Equals(_entries[index].Name, name, StringComparison.OrdinalIgnoreCase); index++)
            paths.Add(Path.Combine(_folders[_entries[index].Folder], _entries[index].Name));
        return paths;
    }
}
