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

    private readonly IRcSession _session;
    private readonly MissingFilesImporterOptions _options;

    public ImportUploader(IRcSession session, MissingFilesImporterOptions options)
    {
        _session = session;
        _options = options;
    }

    public async Task<ImportRun> UploadAsync(IReadOnlyList<ImportRow> rows, string localRoot, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        var results = new List<ImportResult>();
        string root = Path.GetFullPath(localRoot);
        for (int index = 0; index < rows.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested) return new ImportRun(results, Cancelled: true);
            ImportRow row = rows[index];
            ImportResult result = await UploadOneAsync(row, root, index, rows.Count, progress, cancellationToken).ConfigureAwait(true);
            results.Add(result);
            progress?.Report(new ImportProgress(index, rows.Count, row, row.LocalSize, row.LocalSize, result));
            if (result.Outcome == ImportOutcome.Cancelled) return new ImportRun(results, Cancelled: true);
        }

        return new ImportRun(results, Cancelled: false);
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

        byte[] content;
        try
        {
            content = await File.ReadAllBytesAsync(row.LocalPath, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return new ImportResult(row, ImportOutcome.Cancelled, "cancelled");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Failed(row, "the file cannot be read: " + exception.Message);
        }

        if (content.Length == 0) return Failed(row, "the file is empty");
        if (content.Length > _session.MaxUploadFileSize) return Failed(row, "larger than the server's limit");

        IRcFileBrowser browser = _session.BackupFileBrowser;
        string path = folder + name;
        var answer = new TaskCompletionSource<ImportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnMessage(object? sender, TextEventArgs e)
        {
            ImportResult? result = ReadAnswer(row, e.Text);
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

    private static ImportResult Failed(ImportRow row, string message) => new(row, ImportOutcome.Failed, message);

    /// <summary>The server's message 67 about this row's file, or <c>null</c> when it is about something else.</summary>
    private static ImportResult? ReadAnswer(ImportRow row, string text)
    {
        string name = row.Missing.Name;
        string line = text.Trim();
        if (line.StartsWith(UploadedLarge, StringComparison.Ordinal) && string.Equals(line[UploadedLarge.Length..], name, StringComparison.OrdinalIgnoreCase))
            return new ImportResult(row, ImportOutcome.Uploaded, "uploaded");
        if (line.StartsWith(Uploaded, StringComparison.Ordinal) && string.Equals(line[Uploaded.Length..], name, StringComparison.OrdinalIgnoreCase))
            return new ImportResult(row, ImportOutcome.Uploaded, "uploaded");
        if (!line.StartsWith(CouldNot, StringComparison.Ordinal)) return null;
        string rest = line[CouldNot.Length..];
        int separator = rest.IndexOf(": ", StringComparison.Ordinal);
        if (separator < 0) return null;
        string path = rest[..separator];
        if (!string.Equals(path, name, StringComparison.OrdinalIgnoreCase) && !path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)) return null;
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
