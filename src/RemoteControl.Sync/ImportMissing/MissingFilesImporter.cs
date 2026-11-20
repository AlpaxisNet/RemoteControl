using RemoteControl.Protocol.Session;

namespace RemoteControl.Sync.ImportMissing;

/// <summary>The server's answer to the request for the missing files, or why there was none.</summary>
/// <param name="Files">The files (empty when there is an <paramref name="Error"/> or nothing is missing).</param>
/// <param name="Error">The server's sentence when it refused or the search failed, or the program's when the request could not be sent; otherwise <c>null</c>.</param>
/// <param name="TimedOut">Whether the server gave no answer of any kind (not even <c>searching</c>) in time: it does not know the request (an older game server).</param>
/// <param name="ConnectionLost">Whether the connection ended (or was replaced by a new one) before the result came; the server's answer, if any, is lost with it.</param>
public sealed record MissingFilesAnswer(IReadOnlyList<MissingFile> Files, string? Error, bool TimedOut, bool ConnectionLost = false);

/// <summary>
/// "Import Missing": asks the server for the files its NPCs, weapons and scripts name and it lacks, searches a local folder for them and
/// uploads what the admin ticks. A window drives it in this order, all on the session's context (the program's UI thread), which every
/// method awaits back onto:
/// <list type="number">
/// <item><see cref="RequestAsync"/> then <see cref="LoadRightsAsync"/>;</item>
/// <item><see cref="LocalFileIndex.BuildAsync"/> for the folder the admin chose (it runs on a background task and reports progress);</item>
/// <item><see cref="ImportPlanner.Plan"/> with the server's limit (<see cref="MaxUploadSize"/>) and show <see cref="ImportRow"/> rows, each with its
/// <see cref="ImportRow.Reason"/> or ticked; <see cref="ImportPlanner.WithLocalFile"/> when the admin picks an alternative;</item>
/// <item>after the admin confirmed the count and the total size (<see cref="ImportPlan.Summarize"/>), <see cref="UploadAsync"/> with the
/// ticked rows, an <see cref="IProgress{T}"/> of <see cref="ImportProgress"/> and a cancellation token.</item>
/// </list>
/// The uploads go through the session's backup file-browser view: it has no window, and the synchroniser and the file browser window
/// keep their own folder, listing and transfer events. Do not start an upload while a server backup runs: both share that view.
/// </summary>
public sealed class MissingFilesImporter
{
    private readonly IRcSession _session;
    private readonly MissingFilesImporterOptions _options;
    private readonly CancellationToken _connectionEnded;

    /// <summary>Creates the importer.</summary>
    /// <param name="session">The connection.</param>
    /// <param name="options">The waits; the defaults when <c>null</c>.</param>
    /// <param name="connectionEnded">Cancelled by the caller when the session was replaced by another connection (the old one then raises
    /// nothing any more): a request that waits ends with <see cref="MissingFilesAnswer.ConnectionLost"/>.</param>
    public MissingFilesImporter(IRcSession session, MissingFilesImporterOptions? options = null, CancellationToken connectionEnded = default)
    {
        _session = session;
        _options = options ?? new MissingFilesImporterOptions();
        _connectionEnded = connectionEnded;
    }

    /// <summary>The upload limit the server advertises, in bytes.</summary>
    /// <remarks>0 means the server named none (it has not sent packet 103): no limit is checked then.</remarks>
    public long MaxUploadSize => _session.MaxUploadFileSize;

    /// <summary>
    /// Asks the server for the missing files and waits for the whole answer. The wait for "a server that knows the request" is
    /// <see cref="MissingFilesImporterOptions.RequestTimeout"/> and ends with the server's first answer of any kind (<c>searching</c>, a
    /// part, an error); after that the request waits for the result however long the search takes, until the admin cancels
    /// (<paramref name="cancellationToken"/>), the connection ends or is replaced, or the server answers. One request at a time for a
    /// session: a call while one is outstanding does not send again and gets that request's result.
    /// </summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <param name="onProgress">Told, on the session's context, of the server's <c>searching</c> and of every part before the last (also of
    /// what already came when a request was outstanding).</param>
    /// <returns>The answer; <see cref="MissingFilesAnswer.TimedOut"/> when there was no answer at all, <see cref="MissingFilesAnswer.ConnectionLost"/>
    /// when the connection ended.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task<MissingFilesAnswer> RequestAsync(CancellationToken cancellationToken = default, Action<MissingFilesProgressEventArgs>? onProgress = null)
    {
        PendingRequest request = PendingRequest.GetOrStart(_session, _options);
        return await request.WaitAsync(onProgress, _connectionEnded, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Asks the server for the account's folder rights through the upload view and waits for the list.</summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>The rights; empty when the account has none or the server did not answer.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task<IReadOnlyList<FileBrowserFolder>> LoadRightsAsync(CancellationToken cancellationToken = default)
    {
        IRcFileBrowser browser = _session.BackupFileBrowser;
        var completion = new TaskCompletionSource<IReadOnlyList<FileBrowserFolder>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFolders(object? sender, ValueEventArgs<IReadOnlyList<FileBrowserFolder>> e) => completion.TrySetResult(e.Value);
        browser.FoldersReceived += OnFolders;
        try
        {
            if (!await browser.StartAsync().ConfigureAwait(true)) return Array.Empty<FileBrowserFolder>();
            return await WaitAsync(completion.Task, _options.RightsTimeout, cancellationToken).ConfigureAwait(true) ?? Array.Empty<FileBrowserFolder>();
        }
        finally
        {
            browser.FoldersReceived -= OnFolders;
        }
    }

    /// <summary>Uploads the rows one after the other; see <see cref="ImportUploader"/>.</summary>
    /// <param name="rows">The rows the admin ticked.</param>
    /// <param name="localRoot">The folder the admin chose; no file outside it is read.</param>
    /// <param name="progress">Told when a row starts, while it is sent and when it is done.</param>
    /// <param name="cancellationToken">Stops the run between files (and the wait for a server answer).</param>
    /// <returns>The results.</returns>
    public Task<ImportRun> UploadAsync(IReadOnlyList<ImportRow> rows, string localRoot, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default) =>
        new ImportUploader(_session, _options).UploadAsync(rows, localRoot, progress, cancellationToken);

    /// <summary>Waits for a task for at most <paramref name="timeout"/>; <c>null</c> when the time ran out.</summary>
    internal static async Task<T?> WaitAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken) where T : class
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task delay = Task.Delay(timeout, timer.Token);
        Task finished = await Task.WhenAny(task, delay).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (finished != task) return null;
        timer.Cancel();
        return await task.ConfigureAwait(true);
    }
}
