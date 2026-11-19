using RemoteControl.Core;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Sync;

/// <summary>How a <see cref="ServerBackup"/> runs.</summary>
public sealed record ServerBackupOptions
{
    /// <summary>How long to wait for the folder list after the file browser was opened (a server sends none to an account without folder rights).</summary>
    public TimeSpan FolderListTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the listing of one folder (a server does not answer a folder it refuses).</summary>
    public TimeSpan ListingTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Files larger than this are left out, with a log line.</summary>
    public long MaxFileSize { get; init; } = 256L * 1024 * 1024;

    /// <summary>The most folders visited, a guard against a server that lists folders without end.</summary>
    public int MaxFolders { get; init; } = 10_000;

    /// <summary>The clock that names the folder of earlier copies (local time).</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
}

/// <summary>Where a <see cref="ServerBackup"/> stands: what it does now and what it has counted so far.</summary>
public sealed record ServerBackupProgress
{
    /// <summary>What happens now, for a status line ("Listing levels/", "Downloading levels/start.nw").</summary>
    public string Phase { get; init; } = string.Empty;

    /// <summary>The folders listed so far (answered or not).</summary>
    public int FoldersDone { get; init; }

    /// <summary>The folders known so far (it grows when a listing names subfolders).</summary>
    public int FoldersTotal { get; init; }

    /// <summary>Files written: new ones and ones that changed on the server.</summary>
    public int Saved { get; init; }

    /// <summary>Files that were already there with the server's size and time (or the same bytes), and were left as they were.</summary>
    public int UpToDate { get; init; }

    /// <summary>Files left out on purpose: no read right, larger than <see cref="ServerBackupOptions.MaxFileSize"/>.</summary>
    public int Skipped { get; init; }

    /// <summary>Files that could not be downloaded or written, or whose server name was refused.</summary>
    public int Failed { get; init; }

    /// <summary>The bytes written.</summary>
    public long BytesSaved { get; init; }
}

/// <summary>How a <see cref="ServerBackup"/> ended.</summary>
public enum ServerBackupOutcome
{
    /// <summary>Every folder was visited (single files may still have failed, see <see cref="ServerBackupProgress.Failed"/>).</summary>
    Completed,

    /// <summary>It was stopped.</summary>
    Cancelled,

    /// <summary>The connection ended.</summary>
    Disconnected,

    /// <summary>It could not start: no connection, no folder list, or the folder could not be made.</summary>
    Failed,
}

/// <summary>The end of a <see cref="ServerBackup"/>.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Message">One sentence for the user.</param>
/// <param name="Totals">The counts at the end.</param>
/// <param name="FailedPaths">The server paths that failed, in order.</param>
public sealed record ServerBackupResult(ServerBackupOutcome Outcome, string Message, ServerBackupProgress Totals, IReadOnlyList<string> FailedPaths);

/// <summary>
/// Downloads every file an account may read on the server into a local folder, in the server's folder structure
/// (<c>levels/start.nw</c> becomes <c>&lt;folder&gt;/levels/start.nw</c>), through the session's own backup view of the file browser
/// (<see cref="IRcSession.BackupFileBrowser"/>), so the file browser window and the synchroniser are not disturbed.
/// </summary>
/// <remarks>
/// <para>The folders are those of the account's folder rules (the server lets nothing else be browsed, the root folder never); a rule
/// with a wildcard in its folder part is left out, as changing to it would make a folder named <c>*</c> on the server. A listing that
/// names subfolders adds them. One request at a time: list a folder, then download its files one by one.</para>
/// <para>A file that is already there with the server's size and time is left alone, so running the backup again only fetches what
/// changed. A file that changed is replaced; the earlier local copy is never lost but moved to
/// <c>&lt;folder&gt;/.earlier-copies/&lt;date time&gt;/&lt;server path&gt;</c>. Files the account may not read (rights without
/// <c>r</c>) are left out, since the server answers their download with nothing. Empty files are written without asking the server
/// (it answers them with "file send failed"). Nothing is ever deleted locally. A server's file name never leaves the folder (rule 7).</para>
/// <para>Run it on the thread that receives the session's events (the UI thread in the program); its events are raised on that thread.
/// Files are read and written on worker threads.</para>
/// </remarks>
/// <example>
/// <code>
/// var backup = new ServerBackup(context.Session, "/home/me/Backups/Alpaxis");
/// backup.LogLine += (_, e) =&gt; log.Append(e.Text);
/// ServerBackupResult result = await backup.RunAsync(stop.Token);
/// </code>
/// </example>
public sealed class ServerBackup
{
    /// <summary>The folder inside the backup that keeps the earlier copies of files that changed.</summary>
    public const string EarlierCopiesFolder = ".earlier-copies";

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly IRcSession _session;
    private readonly IRcFileBrowser _browser;
    private readonly ServerBackupOptions _options;
    private readonly List<string> _failedPaths = new();
    private TaskCompletionSource<IReadOnlyList<FileBrowserFolder>>? _foldersWaiter;
    private (string Key, TaskCompletionSource<IReadOnlyList<FileBrowserEntry>> Completion)? _listingWaiter;
    private CancellationTokenSource? _download;
    private string? _downloadName;
    private string? _downloadRefusal;
    private CancellationTokenSource? _run;
    private string? _disconnectReason;
    private string? _earlierCopies;
    private ServerBackupProgress _progress = new();
    private int _started;

    /// <summary>Creates a backup of the files of a session; nothing happens until <see cref="RunAsync"/>.</summary>
    /// <param name="session">The connection (its <see cref="IRcSession.BackupFileBrowser"/> is used).</param>
    /// <param name="destination">The local folder; made when missing. A relative path is taken from the current folder.</param>
    /// <param name="options">The time limits; the defaults when null.</param>
    public ServerBackup(IRcSession session, string destination, ServerBackupOptions? options = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        _browser = session.BackupFileBrowser;
        Destination = Path.GetFullPath(destination);
        _options = options ?? new ServerBackupOptions();
    }

    /// <summary>The local folder, as a full path.</summary>
    public string Destination { get; }

    /// <summary>What it has done so far.</summary>
    public ServerBackupProgress Progress => _progress;

    /// <summary>A line for the log.</summary>
    public event EventHandler<TextEventArgs>? LogLine;

    /// <summary><see cref="Progress"/> changed.</summary>
    public event EventHandler<ValueEventArgs<ServerBackupProgress>>? ProgressChanged;

    /// <summary>Runs the backup to its end; can be called once. Never throws for what the server or the disk does.</summary>
    /// <param name="cancellationToken">Stops it (the file being downloaded is given up).</param>
    /// <returns>How it ended.</returns>
    /// <exception cref="InvalidOperationException">It was run before.</exception>
    public async Task<ServerBackupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("A server backup runs once.");
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _run = run;
        EventHandler<ValueEventArgs<IReadOnlyList<FileBrowserFolder>>> folders = (_, e) => _foldersWaiter?.TrySetResult(e.Value);
        EventHandler<FileBrowserFilesEventArgs> files = (_, e) => OnFilesReceived(e);
        EventHandler<TextEventArgs> message = (_, e) => OnMessage(e.Text);
        EventHandler<DisconnectedEventArgs> disconnected = (_, e) => OnDisconnected(e.Reason);
        _browser.FoldersReceived += folders;
        _browser.FilesReceived += files;
        _session.FileBrowserMessageReceived += message;
        _session.Disconnected += disconnected;
        try
        {
            return await RunCoreAsync(run.Token);
        }
        catch (OperationCanceledException)
        {
            return _disconnectReason is { } reason
                ? End(ServerBackupOutcome.Disconnected, "The backup stopped: the connection ended (" + reason + ").")
                : End(ServerBackupOutcome.Cancelled, "The backup was stopped.");
        }
        catch (Exception exception)
        {
            // Rule 4: nothing escapes; the backup ends with the reason.
            return End(ServerBackupOutcome.Failed, "The backup failed: " + exception.Message);
        }
        finally
        {
            _browser.FoldersReceived -= folders;
            _browser.FilesReceived -= files;
            _session.FileBrowserMessageReceived -= message;
            _session.Disconnected -= disconnected;
            _foldersWaiter = null;
            _listingWaiter = null;
            _run = null;
        }
    }

    /// <summary>The folders of the folder rules a backup visits, in order: each rule's folder part (as the server spells it, ending in <c>/</c>), without root, wildcard or unsafe folders.</summary>
    /// <param name="rules">The folder rules.</param>
    /// <returns>The distinct folders, sorted.</returns>
    public static IReadOnlyList<string> FoldersOf(IEnumerable<FileBrowserFolder> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (FileBrowserFolder rule in rules)
        {
            string folder = (rule.Pattern ?? string.Empty).Trim().Replace('\\', '/');
            int slash = folder.LastIndexOf('/');
            if (slash <= 0) continue;
            folder = folder[..(slash + 1)];
            if (IsVisitable(folder)) result.Add(folder);
        }

        return result.ToArray();
    }

    private static bool IsVisitable(string folder) =>
        folder.Length > 1 && folder.EndsWith('/') && folder[0] != '/' && folder.IndexOfAny(['*', '?', ':', '\n', '\r']) < 0 && !folder.Contains("//", StringComparison.Ordinal)
        && folder.TrimEnd('/').Split('/').All(segment => segment is not "." and not "..");

    private async Task<ServerBackupResult> RunCoreAsync(CancellationToken token)
    {
        if (!_session.IsConnected) return End(ServerBackupOutcome.Failed, "Not connected.");
        string? problem = await Task.Run(() => MakeFolder(Destination), CancellationToken.None);
        if (problem is not null) return End(ServerBackupOutcome.Failed, "The folder " + Destination + " could not be made: " + problem);
        Log("Backing up the files of " + _session.Account + " into " + Destination);

        Report(_progress with { Phase = "Asking for the folders" });
        IReadOnlyList<FileBrowserFolder>? rules = await RequestFoldersAsync(token);
        if (rules is null)
            return End(ServerBackupOutcome.Failed, _session.IsConnected
                ? "The server sent no folder list: this account may not browse any folder."
                : "Not connected.");
        IReadOnlyList<string> roots = FoldersOf(rules);
        if (roots.Count == 0) return End(ServerBackupOutcome.Completed, "The folder rules of this account name no folder that can be backed up.");

        var queue = new Queue<string>(roots);
        var seen = new HashSet<string>(roots.Select(Key), StringComparer.Ordinal);
        Log("Folders to back up: " + string.Join(", ", roots));
        Report(_progress with { FoldersTotal = queue.Count });
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string folder = queue.Dequeue();
            Report(_progress with { Phase = "Listing " + folder });
            IReadOnlyList<FileBrowserEntry>? listing = await ListAsync(folder, token);
            token.ThrowIfCancellationRequested();
            if (listing is null)
            {
                Log("No listing of " + folder + " arrived; it is left out.");
                Report(_progress with { FoldersDone = _progress.FoldersDone + 1 });
                continue;
            }

            List<(FileBrowserEntry Entry, string Remote)> entries = Entries(folder, listing, out List<string> subfolders);
            foreach (string subfolder in subfolders)
            {
                if (seen.Count >= _options.MaxFolders || !IsVisitable(subfolder) || !seen.Add(Key(subfolder))) continue;
                queue.Enqueue(subfolder);
            }

            Log($"Listed {folder}: {entries.Count} {(entries.Count == 1 ? "file" : "files")}" + (subfolders.Count == 0 ? string.Empty : $", {subfolders.Count} folders"));
            Report(_progress with { FoldersTotal = _progress.FoldersDone + 1 + queue.Count });
            int unreadable = 0;
            foreach ((FileBrowserEntry entry, string remote) in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Rights.Length != 0 && !entry.Rights.Contains('r', StringComparison.OrdinalIgnoreCase))
                {
                    unreadable++;
                    Report(_progress with { Skipped = _progress.Skipped + 1 });
                    continue;
                }

                await BackUpFileAsync(entry, remote, token);
            }

            if (unreadable > 0) Log($"{unreadable} {(unreadable == 1 ? "file" : "files")} in {folder} may not be read with this account's rights; left out.");
            Report(_progress with { FoldersDone = _progress.FoldersDone + 1 });
        }

        ServerBackupProgress done = _progress;
        string summary = $"Backup finished: {done.Saved} saved, {done.UpToDate} already up to date"
            + (done.Skipped > 0 ? $", {done.Skipped} left out" : string.Empty)
            + (done.Failed > 0 ? $", {done.Failed} failed" : string.Empty) + ".";
        return End(ServerBackupOutcome.Completed, summary);
    }

    /// <summary>The files of a listing as server paths (once each, the readable entry first) and the subfolders it names.</summary>
    private static List<(FileBrowserEntry Entry, string Remote)> Entries(string folder, IReadOnlyList<FileBrowserEntry> listing, out List<string> subfolders)
    {
        var files = new Dictionary<string, (FileBrowserEntry Entry, string Remote)>(StringComparer.Ordinal);
        subfolders = new List<string>();
        foreach (FileBrowserEntry entry in listing)
        {
            string name = (entry.Name ?? string.Empty).Trim().Replace('\\', '/');
            if (name.Length == 0) continue;
            string remote = name.StartsWith(folder, StringComparison.Ordinal) ? name : folder + name.TrimStart('/');
            bool archive = entry.IsDirectory && entry.Size > 0 && name.TrimEnd('/').EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            if (entry.IsDirectory && !archive)
            {
                if (!subfolders.Contains(remote, StringComparer.Ordinal)) subfolders.Add(remote);
                continue;
            }

            // A browsable archive (a ".zip/" folder entry) is backed up as the file it is.
            remote = remote.TrimEnd('/');
            if (files.TryGetValue(remote, out var known) && known.Entry.Rights.Contains('r', StringComparison.OrdinalIgnoreCase)) continue;
            files[remote] = (entry, remote);
        }

        return files.Values.OrderBy(pair => pair.Remote, StringComparer.Ordinal).ToList();
    }

    private async Task BackUpFileAsync(FileBrowserEntry entry, string remote, CancellationToken token)
    {
        if (entry.Size > _options.MaxFileSize)
        {
            Log($"Left out {remote}: larger than {_options.MaxFileSize / (1024 * 1024)} MiB.");
            Report(_progress with { Skipped = _progress.Skipped + 1 });
            return;
        }

        if (!SafePath.TryCombine(Destination, remote, out string? target, out string? refusal))
        {
            Fail(remote, "Refused the server's file name " + remote + ": " + refusal);
            return;
        }

        long modified = entry.Modified;
        bool current = await Task.Run(() => IsUpToDate(target, entry.Size, modified), CancellationToken.None);
        if (current)
        {
            Report(_progress with { UpToDate = _progress.UpToDate + 1 });
            return;
        }

        byte[]? content;
        if (entry.Size == 0) content = Array.Empty<byte>();
        else
        {
            Report(_progress with { Phase = "Downloading " + remote });
            content = await DownloadAsync(remote, token);
            token.ThrowIfCancellationRequested();
            if (content is null)
            {
                string reason = _downloadRefusal ?? (_session.IsConnected ? "the server sent nothing" : "not connected");
                Fail(remote, "Could not download " + remote + " (" + reason + ").");
                if (!_session.IsConnected) throw new OperationCanceledException();
                return;
            }
        }

        _earlierCopies ??= Path.Combine(Destination, EarlierCopiesFolder, _options.Now().ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture));
        string earlier = _earlierCopies;
        (bool written, string? aside, string? error) = await Task.Run(() => Write(target, content, modified, earlier, remote), CancellationToken.None);
        if (error is not null)
        {
            Fail(remote, "Could not save " + remote + ": " + error);
            return;
        }

        if (!written)
        {
            Report(_progress with { UpToDate = _progress.UpToDate + 1 });
            return;
        }

        if (aside is not null) Log("Saved " + remote + " (it changed; the earlier copy is in " + aside + ")");
        else Log("Saved " + remote);
        Report(_progress with { Saved = _progress.Saved + 1, BytesSaved = _progress.BytesSaved + content.LongLength });
    }

    private async Task<byte[]?> DownloadAsync(string remote, CancellationToken token)
    {
        using var download = CancellationTokenSource.CreateLinkedTokenSource(token);
        _download = download;
        _downloadName = remote[(remote.LastIndexOf('/') + 1)..];
        _downloadRefusal = null;
        try
        {
            return await _browser.DownloadAsync(remote, download.Token);
        }
        finally
        {
            _download = null;
            _downloadName = null;
        }
    }

    private async Task<IReadOnlyList<FileBrowserFolder>?> RequestFoldersAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<FileBrowserFolder>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _foldersWaiter = completion;
        try
        {
            if (!await _browser.StartAsync()) return null;
            return await completion.Task.WaitAsync(_options.FolderListTimeout, token);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(_foldersWaiter, completion)) _foldersWaiter = null;
        }
    }

    private async Task<IReadOnlyList<FileBrowserEntry>?> ListAsync(string folder, CancellationToken token)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<FileBrowserEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _listingWaiter = (Key(folder), completion);
        try
        {
            if (!await _browser.ChangeFolderAsync(folder)) return null;
            return await completion.Task.WaitAsync(_options.ListingTimeout, token);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            if (_listingWaiter is { } waiter && ReferenceEquals(waiter.Completion, completion)) _listingWaiter = null;
        }
    }

    private void OnFilesReceived(FileBrowserFilesEventArgs e)
    {
        if (_listingWaiter is { } waiter && Key(e.Folder) == waiter.Key) waiter.Completion.TrySetResult(e.Files);
    }

    /// <summary>A server that will not send a file says so in a message ("Insufficient rights to download/view x"): the wait ends at once.</summary>
    private void OnMessage(string? text)
    {
        if (text is null || _download is not { } download || _downloadName is not { } name) return;
        if (!text.StartsWith("Insufficient rights", StringComparison.OrdinalIgnoreCase) || !text.Contains(name, StringComparison.Ordinal)) return;
        _downloadRefusal = text.Trim();
        download.Cancel();
    }

    private void OnDisconnected(string reason)
    {
        _disconnectReason = string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason;
        try
        {
            _run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run has ended.
        }
    }

    private static string Key(string? folder) => SyncNames.LowerAscii((folder ?? string.Empty).Replace('\\', '/').Trim('/'));

    private static string? MakeFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return exception.Message;
        }
    }

    /// <summary>Whether the local file has the server's size and time (within the two seconds some file systems round to).</summary>
    private static bool IsUpToDate(string path, long size, long modified)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != size || modified <= 0) return false;
            double seconds = (info.LastWriteTimeUtc - UnixEpoch).TotalSeconds;
            return Math.Abs(seconds - modified) < 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Writes a file whole with the server's time; a different file that is there is moved into the earlier copies first.</summary>
    /// <returns>Whether bytes were written (false: the same bytes were there), where the earlier copy went, or the error.</returns>
    private static (bool Written, string? Aside, string? Error) Write(string path, byte[] content, long modified, string earlierCopies, string remote)
    {
        try
        {
            string? aside = null;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length == content.LongLength && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                {
                    SetTime(path, modified);
                    return (false, null, null);
                }

                aside = FreeName(SafePath.Combine(earlierCopies, remote));
                Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
                File.Move(path, aside, overwrite: false);
            }

            AtomicFile.WriteAllBytes(path, content);
            SetTime(path, modified);
            return (true, aside, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (false, null, exception.Message);
        }
    }

    private static string FreeName(string path)
    {
        string candidate = path;
        for (int counter = 2; File.Exists(candidate) || Directory.Exists(candidate); counter++)
        {
            if (counter > 1000) throw new IOException("No free name for the earlier copy of " + path + ".");
            candidate = path + "." + counter;
        }

        return candidate;
    }

    private static void SetTime(string path, long modified)
    {
        if (modified <= 0) return;
        try
        {
            File.SetLastWriteTimeUtc(path, UnixEpoch.AddSeconds(Math.Min(modified, 253402300799)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The time is a courtesy (it only makes the next backup skip the file).
        }
    }

    private void Fail(string remote, string line)
    {
        _failedPaths.Add(remote);
        Log(line);
        Report(_progress with { Failed = _progress.Failed + 1 });
    }

    private ServerBackupResult End(ServerBackupOutcome outcome, string message)
    {
        Log(message);
        Report(_progress with { Phase = message });
        return new ServerBackupResult(outcome, message, _progress, _failedPaths.ToArray());
    }

    private void Log(string line) => LogLine?.Invoke(this, new TextEventArgs(line));

    private void Report(ServerBackupProgress progress)
    {
        _progress = progress;
        ProgressChanged?.Invoke(this, new ValueEventArgs<ServerBackupProgress>(progress));
    }
}
