using RemoteControl.Core;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Sync;

public sealed partial class SyncEngine
{
    private TaskCompletionSource<IReadOnlyList<FileBrowserFolder>>? _rootsWaiter;
    private (string Key, TaskCompletionSource<IReadOnlyList<FileBrowserEntry>> Completion)? _listingWaiter;

    /// <summary>
    /// The folders the server lets this account browse, for the "Remote file folders" picker (<c>onChooseFileFolders</c>); <c>null</c> when
    /// no session is attached or the server did not answer.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The root folders.</returns>
    public Task<IReadOnlyList<string>?> RequestFileRootsAsync(CancellationToken cancellationToken = default) => InvokeAsync(async () =>
    {
        IRcSession? session = _session;
        if (session is null) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        return await RequestRootsAsync(session.SyncFileBrowser, linked.Token);
    });

    /// <summary>The subfolders of a server folder, for the picker (browsable archives are left out, as in the C++); <c>null</c> on failure.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The subfolders' paths.</returns>
    public Task<IReadOnlyList<string>?> ListSubfoldersAsync(string folder, CancellationToken cancellationToken = default) => InvokeAsync(async () =>
    {
        IRcSession? session = _session;
        if (session is null) return null;
        string normalized = SyncNames.NormalizeRemotePath(folder);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        IReadOnlyList<FileBrowserEntry>? files = await ListFolderCoreAsync(session.SyncFileBrowser, normalized, _options.FolderListTimeout, linked.Token);
        if (files is null) return null;
        string prefix = normalized.Length == 0 ? string.Empty : normalized + "/";
        var result = new List<string>();
        foreach (FileBrowserEntry entry in files)
        {
            if (!entry.IsDirectory || IsBrowsableArchive(entry)) continue;
            string remote = Resolve(prefix, entry.Name);
            if (remote.Length == 0) continue;
            string remainder = prefix.Length == 0 ? remote : remote[prefix.Length..];
            int slash = remainder.IndexOf('/');
            if (slash >= 0) remainder = remainder[..slash];
            remote = SyncNames.NormalizeRemotePath(prefix + remainder);
            if (remote.Length != 0 && !result.Contains(remote, StringComparer.Ordinal)) result.Add(remote);
        }

        return (IReadOnlyList<string>?)result;
    });

    private void HandleFoldersReceived(FileBrowserFolder[] folders) => _rootsWaiter?.TrySetResult(folders);

    /// <summary>
    /// A server that answers every request (Alpaxis since November 2026) says why it does not open a folder for the account,
    /// "Folder levels/ is not in your folder rights", and then sends an empty listing for it so that nothing waits. The folder that
    /// is being listed is remembered as refused: its empty listing is no empty folder.
    /// </summary>
    private void HandleFileBrowserMessage(string message)
    {
        const string start = "Folder ";
        const string end = " is not in your folder rights";
        if (_listingWaiter is not { } waiter) return;
        string text = message.Trim();
        if (text.Length <= start.Length + end.Length || !text.StartsWith(start, StringComparison.Ordinal) || !text.EndsWith(end, StringComparison.Ordinal)) return;
        if (FolderKey(text[start.Length..^end.Length]) == waiter.Key) _refusedFolders.Add(waiter.Key);
    }

    private void HandleFilesReceived(string folder, FileBrowserEntry[] files)
    {
        if (_listingWaiter is { } waiter && FolderKey(folder) == waiter.Key) waiter.Completion.TrySetResult(files);
    }

    private static string FolderKey(string? folder) => SyncNames.LowerAscii(SyncNames.NormalizeRemotePath(folder));

    private async Task<IReadOnlyList<string>?> GetRootsAsync(RunContext run)
    {
        _passRoots ??= await RequestRootsAsync(run.Browser, run.Token);
        return _passRoots;
    }

    /// <summary>Opens the file browser and turns the folder patterns into root folders (<c>onFileRoots</c>).</summary>
    private async Task<IReadOnlyList<string>?> RequestRootsAsync(IRcFileBrowser browser, CancellationToken cancellationToken)
    {
        await _browserGate.WaitAsync(cancellationToken);
        var completion = new TaskCompletionSource<IReadOnlyList<FileBrowserFolder>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rootsWaiter = completion;
        try
        {
            if (!await browser.StartAsync()) return null;
            IReadOnlyList<FileBrowserFolder> folders;
            try
            {
                folders = await completion.Task.WaitAsync(_options.FileRootsTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return null;
            }

            var roots = new List<string>();
            foreach (FileBrowserFolder folder in folders)
            {
                string pattern = (folder.Pattern ?? string.Empty).Replace('\\', '/');
                int wildcard = pattern.IndexOfAny(['*', '?']);
                if (wildcard >= 0) pattern = pattern[..wildcard];
                pattern = SyncNames.NormalizeRemotePath(pattern);
                if (pattern.Length != 0 && !roots.Contains(pattern, StringComparer.Ordinal)) roots.Add(pattern);
            }

            return roots;
        }
        finally
        {
            if (ReferenceEquals(_rootsWaiter, completion)) _rootsWaiter = null;
            _browserGate.Release();
        }
    }

    private Task<IReadOnlyList<FileBrowserEntry>?> ListFolderAsync(RunContext run, string folder, TimeSpan timeout) => ListFolderCoreAsync(run.Browser, folder, timeout, run.Token);

    /// <summary>Lists one folder through the synchroniser's view and waits for its listing; <c>null</c> when not sent or not answered in time.</summary>
    private async Task<IReadOnlyList<FileBrowserEntry>?> ListFolderCoreAsync(IRcFileBrowser browser, string folder, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _browserGate.WaitAsync(cancellationToken);
        var completion = new TaskCompletionSource<IReadOnlyList<FileBrowserEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _listingWaiter = (FolderKey(folder), completion);
        try
        {
            if (!await browser.ChangeFolderAsync(folder.Length == 0 ? string.Empty : folder + "/")) return null;
            try
            {
                return await completion.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
        finally
        {
            if (_listingWaiter is { } waiter && ReferenceEquals(waiter.Completion, completion)) _listingWaiter = null;
            _browserGate.Release();
        }
    }

    private static bool IsBrowsableArchive(FileBrowserEntry entry) =>
        entry.IsDirectory && entry.Size > 0 && SyncNames.LowerAscii(SyncNames.NormalizeRemotePath(entry.Name)).EndsWith(".zip", StringComparison.Ordinal);

    /// <summary>A listing name as a remote path: names without the folder get it prepended (both forms occur).</summary>
    private static string Resolve(string prefix, string name)
    {
        string remote = SyncNames.NormalizeRemotePath(name);
        if (remote.Length == 0) return string.Empty;
        if (prefix.Length != 0 && !remote.StartsWith(prefix, StringComparison.Ordinal)) remote = prefix + remote;
        return SyncNames.NormalizeRemotePath(remote);
    }

    // ---- The file pass ------------------------------------------------------------------------------------------------------------------

    private sealed class FilePass
    {
        public Queue<string> Folders { get; } = new();

        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

        public Queue<string> Downloads { get; } = new();

        public HashSet<string> QueuedDownloads { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Archives { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Probes { get; } = new(StringComparer.Ordinal);

        public void AddFolder(string folder)
        {
            if (Seen.Add(folder)) Folders.Enqueue(folder);
        }
    }

    /// <summary>The file pass (<c>beginFilePull</c>, <c>onFileRoots</c>, <c>onFileList</c>, <c>requestNextFileDownload</c>): one request at a time.</summary>
    private async Task FilePassAsync(RunContext run)
    {
        _fileSyncActive = true;
        try
        {
            IReadOnlyList<string> saved = SyncNames.SplitFolders(_settings.FileFolders);
            string canonical = SyncNames.JoinFolders(saved);
            if (canonical != _settings.FileFolders) ChangeSettingsInternal(settings => settings with { FileFolders = canonical });
            SetPhase("Discovering server file roots");
            Log("Discovering available server file roots");
            IReadOnlyList<string>? roots = await GetRootsAsync(run);
            var pass = new FilePass();
            if (roots is null)
            {
                foreach (string folder in saved) pass.AddFolder(folder);
                SetPhase("Using saved server file roots");
                Log("Unable to refresh server file roots; using saved selection");
            }
            else if (roots.Count == 0)
            {
                SetPhase("No file roots available");
                Log("The server did not advertise any usable file roots");
                return;
            }
            else
            {
                List<string> selected = SelectRoots(saved, roots);
                foreach (string folder in selected)
                {
                    if (SyncNames.LowerAscii(folder).EndsWith(".zip", StringComparison.Ordinal)) pass.Archives.Add(folder);
                    else pass.AddFolder(folder);
                }

                foreach (string archive in pass.Archives)
                {
                    string parent = SyncNames.ParentOf(archive);
                    if (parent.Length == 0 || pass.Seen.Contains(parent)) continue;
                    pass.AddFolder(parent);
                    pass.Probes.Add(parent);
                }

                string text = SyncNames.JoinFolders(selected);
                if (text != _settings.FileFolders) ChangeSettingsInternal(settings => settings with { FileFolders = text });
                Log($"Queued {selected.Count} selected server file roots");
            }

            _workTotal += pass.Folders.Count;
            while (pass.Folders.Count > 0 || pass.Downloads.Count > 0)
            {
                run.Token.ThrowIfCancellationRequested();
                await DrainArrivalsAsync(run);
                PromoteSettledPaths();
                if (_dirtyPaths.Count > 0) await PushPassAsync(run, forced: false, onlyDirty: true);
                _queuedFolders = pass.Folders.Count;
                _queuedDownloads = pass.Downloads.Count;
                Notify();
                if (pass.Downloads.Count > 0)
                {
                    string remote = pass.Downloads.Dequeue();
                    pass.QueuedDownloads.Remove(remote);
                    await DownloadFileAsync(run, remote);
                    continue;
                }

                await ListFileFolderAsync(run, pass.Folders.Dequeue(), pass);
            }

            SetPhase("Watching for changes");
        }
        finally
        {
            _fileSyncActive = false;
            _queuedFolders = 0;
            _queuedDownloads = 0;
            Notify();
        }
    }

    /// <summary>The selected roots: the saved selection (all roots when empty) plus every root inside a selected one, sorted.</summary>
    internal static List<string> SelectRoots(IReadOnlyList<string> saved, IReadOnlyList<string> roots)
    {
        var selected = saved.Count == 0 ? roots.ToList() : saved.ToList();
        if (saved.Count != 0)
        {
            foreach (string folder in roots)
            {
                bool included = saved.Any(root => folder == root || (folder.Length > root.Length && folder.StartsWith(root + "/", StringComparison.Ordinal)));
                if (included && !selected.Contains(folder, StringComparer.Ordinal)) selected.Add(folder);
            }
        }

        selected.Sort(StringComparer.Ordinal);
        return selected;
    }

    private async Task ListFileFolderAsync(RunContext run, string folder, FilePass pass)
    {
        SetPhase("Listing " + folder);
        Log("Listing " + folder);
        IReadOnlyList<FileBrowserEntry>? files = await ListFolderAsync(run, folder, _options.FolderListTimeout);
        _workDone++;
        if (files is null)
        {
            Log("No listing of " + folder + " arrived");
            return;
        }

        string prefix = folder.Length == 0 ? string.Empty : folder + "/";
        bool probeOnly = pass.Probes.Remove(folder);
        var entries = new List<(FileBrowserEntry Entry, string Remote)>();
        foreach (FileBrowserEntry entry in files)
        {
            string remote = Resolve(prefix, entry.Name);
            if (remote.Length != 0) entries.Add((entry, remote));
        }

        var archiveFiles = entries.Where(pair => pass.Archives.Contains(pair.Remote)).Select(pair => pair.Remote).ToHashSet(StringComparer.Ordinal);
        foreach (string archive in pass.Archives.ToArray())
        {
            if (SyncNames.ParentOf(archive) != folder) continue;
            if (!archiveFiles.Contains(archive))
            {
                pass.AddFolder(archive);
                _workTotal++;
            }

            pass.Archives.Remove(archive);
        }

        int folderCount = 0, fileCount = 0, queued = 0;
        var candidates = new List<(FileItem Item, FileBrowserEntry Entry)>();
        foreach ((FileBrowserEntry entry, string remote) in entries)
        {
            if (probeOnly && !archiveFiles.Contains(remote)) continue;
            if (entry.IsDirectory && !IsBrowsableArchive(entry))
            {
                folderCount++;
                if (pass.Folders.Count < _options.MaxQueuedFolders && !pass.Seen.Contains(remote))
                {
                    pass.AddFolder(remote);
                    _workTotal++;
                }

                continue;
            }

            fileCount++;
            if (_exclusions.IsExcluded(remote)) continue;
            if (entry.Size > _options.MaxFileSize)
            {
                Log($"Skipped {remote}: larger than {_options.MaxFileSize / (1024 * 1024)} MiB");
                continue;
            }

            FileItem? item = GetOrCreateFile(remote, out string? refusal);
            if (item is null)
            {
                Log("Refused server file name " + remote + ": " + refusal);
                continue;
            }

            candidates.Add((item, entry));
        }

        string[] paths = candidates.Select(candidate => FullPath(candidate.Item)).ToArray();
        bool[] exists = await Task.Run(() => paths.Select(File.Exists).ToArray(), CancellationToken.None);
        DateTime now = DateTime.UtcNow;
        for (int index = 0; index < candidates.Count; index++)
        {
            (FileItem item, FileBrowserEntry entry) = candidates[index];
            bool changed = item.Size != entry.Size || item.Modified != entry.Modified || !exists[index] || item.SyncedLocalHash is null;
            if (item.PendingUploadUntil > now) continue;
            if (item.State == SyncItemState.Conflict && item.ConflictListing == (entry.Size, entry.Modified)) continue;
            if (!exists[index] && item.SyncedLocalHash is not null && _settings.RemoteDeletesEffective && item.Size == entry.Size && item.Modified == entry.Modified)
            {
                // Deleted locally while remote deletes are on: the push pass decides, nothing is downloaded again.
                _dirtyPaths.Add(item.Path);
                continue;
            }

            if (!changed || pass.Downloads.Count >= _options.MaxQueuedDownloads || !pass.QueuedDownloads.Add(item.RemotePath)) continue;
            item.ListedSize = entry.Size;
            item.ListedModified = entry.Modified;
            pass.Downloads.Enqueue(item.RemotePath);
            queued++;
            _workTotal++;
        }

        MarkManifestDirty();
        if (!probeOnly && folderCount == 0 && fileCount == 0 && _refusedFolders.Contains(FolderKey(folder)))
        {
            // The listing is empty because the server does not open the folder for this account (it said so), not because the folder
            // is empty: the selection is the user's and stays, so that the folder is synchronised again once the right is back.
            Log("The server does not open " + folder + " for this account; it stays selected");
        }
        else if (!probeOnly && folderCount == 0 && fileCount == 0)
        {
            List<string> selected = SyncNames.SplitFolders(_settings.FileFolders).ToList();
            if (selected.Remove(folder))
            {
                ChangeSettingsInternal(settings => settings with { FileFolders = SyncNames.JoinFolders(selected) });
                Log("Ignored empty folder " + folder);
            }
        }

        Log($"Listed {folder}: {folderCount} folders, {fileCount} files, {queued} queued");
    }

    private FileItem? GetOrCreateFile(string remote, out string? refusal)
    {
        refusal = null;
        if (_files.TryGetValue(remote, out FileItem? existing)) return existing;
        string workspace = WorkspacePathOrThrow();
        string relative = SyncNames.FilesFolder + "/" + remote;
        if (!SafePath.TryCombine(Path.Combine(workspace, SyncNames.FilesFolder), remote, out _, out refusal)) return null;
        if (!SyncNames.IsSafeWorkspacePath(relative))
        {
            refusal = "the name is not a safe file name";
            return null;
        }

        if (_byPath.ContainsKey(relative))
        {
            refusal = "another server file uses the same local name";
            return null;
        }

        var item = new FileItem(remote, relative);
        AddItem(item);
        return item;
    }

    private async Task DownloadFileAsync(RunContext run, string remote)
    {
        if (!_files.TryGetValue(remote, out FileItem? item)) return;
        long size = item.ListedSize;
        long modified = item.ListedModified;
        SetPhase("Downloading " + remote);
        Log("Downloading " + remote);
        _currentItem = remote;
        _currentStarted = DateTime.UtcNow;
        _currentProgress = null;
        byte[]? data = null;
        int attempts = Math.Max(1, _options.DownloadAttempts);
        try
        {
            for (int attempt = 1; attempt <= attempts && data is null; attempt++)
            {
                TimeSpan timeout = _options.DownloadTimeout;
                if (size > 0) timeout += TimeSpan.FromSeconds(Math.Min(270, (size / (128 * 1024)) + 1));
                await _browserGate.WaitAsync(run.Token);
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
                    limit.CancelAfter(timeout);
                    data = await run.Browser.DownloadAsync(remote, limit.Token);
                }
                catch (OperationCanceledException) when (!run.Token.IsCancellationRequested)
                {
                    data = null;
                }
                finally
                {
                    _browserGate.Release();
                }

                run.Token.ThrowIfCancellationRequested();
                if (data is null && attempt < attempts) Log($"Retrying download {remote} ({attempt + 1}/{attempts})");
            }
        }
        finally
        {
            _currentItem = null;
            _currentProgress = null;
        }

        _workDone++;
        if (data is null)
        {
            Log("Skipped unresponsive download " + remote);
            SetState(item, SyncItemState.Failed, "The server did not send the file.");
            return;
        }

        if (data.LongLength > _options.MaxFileSize)
        {
            SetState(item, SyncItemState.Failed, "The file is larger than the limit.");
            return;
        }

        string hash = SyncHash.Of(data);
        var input = new ApplyInput(FullPath(item), data, hash, item.SyncedLocalHash, hash == item.SyncedLocalHash, item.SyncedLocalHash is null ? item.LoadedModified : 0,
            item.SyncedLocalHash is not null && _settings.RemoteDeletesEffective, FullPath(ConflictPath(item)), modified);
        ApplyResult result = await Task.Run(() => LocalFiles.ApplyServerVersion(input), CancellationToken.None);
        switch (result.Outcome)
        {
            case ApplyOutcome.Written or ApplyOutcome.Unchanged or ApplyOutcome.LocalModified:
                item.Size = size;
                item.Modified = modified;
                item.ConflictListing = null;
                break;
            case ApplyOutcome.Conflict:
                item.ConflictListing = (size, modified);
                break;
        }

        RecordApply(item, result, data, hash, hash);
        MarkManifestDirty();
    }
}
