using RemoteControl.Protocol.Session;

namespace RemoteControl.Sync.ImportMissing;

/// <summary>
/// Uploads ticked rows through the session's backup file-browser view, one after the other: the change of folder and the upload are
/// one request of the session (it changes the server's folder first when it is another), then the server's message 67 for that file is
/// awaited (<c>Uploaded file NAME</c>, <c>Uploaded large file NAME</c> or <c>Could not upload PATH: REASON</c>). The server's folder is used
/// only as the folder to upload into and only when it is a plain relative path (rule 7 of AGENTS.md); a local file is read only under the
/// folder the admin chose. Methods run on the session's context and continue on it.
/// </summary>
internal sealed class ImportUploader
{
    private const string Uploaded = "Uploaded file ";
    private const string UploadedLarge = "Uploaded large file ";
    private const string CouldNot = "Could not upload ";
    private const string FolderRefused = "Folder ";
    private const string NotInRights = " is not in your folder rights";

    private readonly IRcSession _session;
    private readonly MissingFilesImporterOptions _options;

    public ImportUploader(IRcSession session, MissingFilesImporterOptions options)
    {
        _session = session;
        _options = options;
    }

    /// <summary>How many uploads in a row may go without any answer before the run is ended.</summary>
    internal const int MaxSilentUploads = 3;

    public async Task<ImportRun> UploadAsync(IReadOnlyList<ImportRow> rows, string localRoot, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        var results = new List<ImportResult>();
        string root = Path.GetFullPath(localRoot);
        int silent = 0;
        for (int index = 0; index < rows.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested) return new ImportRun(results, Cancelled: true);
            ImportRow row = rows[index];
            ImportResult result = await UploadGuardedAsync(row, root, index, rows.Count, progress, cancellationToken).ConfigureAwait(true);
            results.Add(result);
            progress?.Report(new ImportProgress(index, rows.Count, row, row.LocalSize, row.LocalSize, result));
            if (result.Outcome == ImportOutcome.Cancelled) return new ImportRun(results, Cancelled: true);
            silent = result.Outcome == ImportOutcome.NoAnswer ? silent + 1 : 0;
            if (silent >= MaxSilentUploads && index + 1 < rows.Count)
                return new ImportRun(results, Cancelled: false, $"The run was stopped: the server did not answer {MaxSilentUploads} uploads in a row. It may have stopped answering; the files after these were not sent.");
        }

        return new ImportRun(results, Cancelled: false);
    }

    /// <summary>One row; whatever goes wrong with it is its result and never ends the run (only cancelling does).</summary>
    private async Task<ImportResult> UploadGuardedAsync(ImportRow row, string root, int index, int count, IProgress<ImportProgress>? progress, CancellationToken token)
    {
        try
        {
            return await UploadOneAsync(row, root, index, count, progress, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new ImportResult(row, ImportOutcome.Cancelled, "cancelled");
        }
        catch (Exception exception)
        {
            return Failed(row, "this file could not be uploaded: " + exception.Message);
        }
    }

    private async Task<ImportResult> UploadOneAsync(ImportRow row, string root, int index, int count, IProgress<ImportProgress>? progress, CancellationToken token)
    {
        string name = row.Missing.Name;
        string folder = row.Missing.Folder;
        if (!ImportPlanner.IsPlainName(name) || !ImportPlanner.IsPlainFolder(folder)) return Failed(row, "the server's name or folder is not a plain name and relative folder");
        if (!folder.EndsWith('/')) folder += "/";
        if (row.LocalPath is null) return Failed(row, "there is no local file");
        string? refusal = CheckUnderRoot(row.LocalPath, root);
        if (refusal is not null) return Failed(row, refusal);

        // The limit the server named, else the program's own cap; the size is checked before the file is read and the read itself stops
        // one byte over it, so that a file that grew since the plan is never held in memory whole.
        long limit = _session.MaxUploadFileSize > 0 ? Math.Min(_session.MaxUploadFileSize, MaximumBytes) : MaximumBytes;
        byte[]? content;
        try
        {
            long length = new FileInfo(row.LocalPath).Length;
            if (length == 0) return Failed(row, "the file is empty");
            if (length > limit) return Failed(row, TooLargeText(limit));
            content = await ReadBoundedAsync(row.LocalPath, limit, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return new ImportResult(row, ImportOutcome.Cancelled, "cancelled");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Failed(row, "the file cannot be read: " + exception.Message);
        }

        if (content is null) return Failed(row, TooLargeText(limit));
        if (content.Length == 0) return Failed(row, "the file is empty");

        IRcFileBrowser browser = _session.BackupFileBrowser;
        string path = folder + name;
        var answer = new TaskCompletionSource<ImportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool sending = false;
        void OnMessage(object? sender, TextEventArgs e)
        {
            // What the server said before this row's upload was handed to the session is about something else (an earlier row, a late answer).
            if (!sending) return;
            ImportResult? result = ReadAnswer(row, folder, e.Text);
            if (result is not null) answer.TrySetResult(result);
        }

        void OnProgress(object? sender, FileTransferProgressEventArgs e)
        {
            if (e.Upload && e.Path == path) progress?.Report(new ImportProgress(index, count, row, e.Transferred, e.Total, null));
        }

        _session.FileBrowserMessageReceived += OnMessage;
        browser.TransferProgressChanged += OnProgress;
        try
        {
            progress?.Report(new ImportProgress(index, count, row, 0, content.Length, null));
            sending = true;
            bool sent = await browser.UploadAsync(path, content, token).ConfigureAwait(true);
            if (!sent) return token.IsCancellationRequested ? new ImportResult(row, ImportOutcome.Cancelled, "cancelled") : Failed(row, "the upload could not be sent");
            try
            {
                ImportResult? result = await MissingFilesImporter.WaitAsync(answer.Task, _options.UploadAnswerTimeout, token).ConfigureAwait(true);
                return result ?? new ImportResult(row, ImportOutcome.NoAnswer, "the server did not answer");
            }
            catch (OperationCanceledException)
            {
                return new ImportResult(row, ImportOutcome.Cancelled, "cancelled while waiting for the server; the file may have arrived");
            }
        }
        finally
        {
            _session.FileBrowserMessageReceived -= OnMessage;
            browser.TransferProgressChanged -= OnProgress;
        }
    }

    /// <summary>The largest file this program sends whatever the server allows (the same cap as the file browser's transfers).</summary>
    internal const long MaximumBytes = 256L * 1024 * 1024;

    private static string TooLargeText(long limit) => limit == MaximumBytes ? "larger than 256 MiB, the most this program sends" : "larger than the server's limit";

    /// <summary>Reads a file, but stops at once with <c>null</c> when it is longer than <paramref name="limit"/> (it grew since its size was read).</summary>
    private static async Task<byte[]?> ReadBoundedAsync(string path, long limit, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
        if (stream.Length > limit) return null;
        var buffer = new MemoryStream((int)Math.Min(stream.Length, limit));
        var chunk = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, token).ConfigureAwait(true);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
    }

    private static ImportResult Failed(ImportRow row, string message) => new(row, ImportOutcome.Failed, message);

    /// <summary>
    /// The server's message 67 about this row's file, or <c>null</c> when it is about something else: <c>Uploaded file NAME</c> and
    /// <c>Uploaded large file NAME</c> (the server names no folder there), <c>Could not upload PATH: REASON</c> when PATH is this row's folder and
    /// name (the bare name only when the server knew no folder), and the refusal of the change of folder, <c>Folder X is not in your folder
    /// rights</c>, for this row's folder, which the server sends before it answers the upload itself with <c>no folder open</c>.
    /// </summary>
    private static ImportResult? ReadAnswer(ImportRow row, string folder, string text)
    {
        string name = row.Missing.Name;
        string line = text.Trim();
        if (line.StartsWith(UploadedLarge, StringComparison.Ordinal) && string.Equals(line[UploadedLarge.Length..], name, StringComparison.OrdinalIgnoreCase))
            return new ImportResult(row, ImportOutcome.Uploaded, "uploaded");
        if (line.StartsWith(Uploaded, StringComparison.Ordinal) && string.Equals(line[Uploaded.Length..], name, StringComparison.OrdinalIgnoreCase))
            return new ImportResult(row, ImportOutcome.Uploaded, "uploaded");
        if (line.StartsWith(FolderRefused, StringComparison.Ordinal) && line.EndsWith(NotInRights, StringComparison.Ordinal))
        {
            string refused = line[FolderRefused.Length..^NotInRights.Length];
            return string.Equals(refused.Trim('/'), folder.Trim('/'), StringComparison.OrdinalIgnoreCase) ? new ImportResult(row, ImportOutcome.Refused, line) : null;
        }

        if (!line.StartsWith(CouldNot, StringComparison.Ordinal)) return null;
        string rest = line[CouldNot.Length..];
        int separator = rest.IndexOf(": ", StringComparison.Ordinal);
        if (separator < 0) return null;
        string path = rest[..separator];
        string expected = row.Missing.Folder.Length == 0 ? name : folder + name;
        if (!string.Equals(path, expected, StringComparison.OrdinalIgnoreCase)) return null;
        return new ImportResult(row, ImportOutcome.Refused, rest[(separator + 2)..]);
    }

    /// <summary>A reason to refuse reading the file, or <c>null</c> when it lies under the chosen folder (a link must point there too).</summary>
    private static string? CheckUnderRoot(string localPath, string root)
    {
        string full = Path.GetFullPath(localPath);
        if (!IsUnder(root, full)) return "the file is not under the folder you chose";
        try
        {
            string? target = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is null) return null;
            string? realRoot = new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            return IsUnder(root, target) || (realRoot is not null && IsUnder(realRoot, target)) ? null : "the file is a link that leads out of the folder you chose";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "the file cannot be checked: " + exception.Message;
        }
    }

    private static bool IsUnder(string root, string full)
    {
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(prefix, comparison);
    }
}
