using System.Collections.Concurrent;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Protocol.Testing;

/// <summary>
/// A file-browser view of a <see cref="FakeRcSession"/>. Calls are recorded in the session's <see cref="FakeRcSession.Calls"/> as
/// <c>"FileBrowser.&lt;method&gt;"</c>, <c>"SyncFileBrowser.&lt;method&gt;"</c> or <c>"BackupFileBrowser.&lt;method&gt;"</c>. A download is
/// answered from <see cref="RemoteFiles"/> (raising <see cref="FileReceived"/>, or <see cref="TransferFailed"/> when the file is missing);
/// an upload stores into it. <see cref="OnCall"/> lets a test answer a request the moment it is made, as a server would.
/// </summary>
public sealed class FakeFileBrowser : IRcFileBrowser
{
    private readonly FakeRcSession _session;
    private readonly string _prefix;

    internal FakeFileBrowser(FakeRcSession session, bool isSync, string prefix)
    {
        _session = session;
        IsSync = isSync;
        _prefix = prefix;
    }

    /// <summary>Called with every request of this view after it was recorded (on the caller's thread), to answer it.</summary>
    public Action<RecordedCall>? OnCall { get; set; }

    /// <inheritdoc />
    public bool IsSync { get; }

    /// <summary>
    /// When set, the fake plays the game server's answer to every upload (message 67 of the session's
    /// <see cref="FakeRcSession.FileBrowserMessageReceived"/>, raised before the upload call returns): the account needs a folder right with
    /// <c>w</c> that covers the folder and the name, a file over <see cref="FakeRcSession.MaxUploadFileSize"/> is refused, and otherwise
    /// <c>Uploaded file NAME</c> (<c>Uploaded large file NAME</c> over 32,000 bytes) is answered. A refused upload is not stored.
    /// </summary>
    public bool ServerAnswersUploads { get; set; }

    /// <summary>
    /// With <see cref="ServerAnswersUploads"/>: lets a test replace the answer of one upload (the path as passed, the content); return the
    /// message text, or <c>null</c> for no answer at all (a server that never says anything), or <c>"\0"</c> for the default answer.
    /// </summary>
    public Func<string, byte[], string?>? UploadAnswer { get; set; }

    /// <summary>The folders (settable list behind <see cref="Folders"/>).</summary>
    public List<FileBrowserFolder> FolderList { get; } = new();

    /// <summary>The files (settable list behind <see cref="Files"/>).</summary>
    public List<FileBrowserEntry> FileList { get; } = new();

    /// <summary>The files a download returns and an upload writes, by path as passed.</summary>
    public ConcurrentDictionary<string, byte[]> RemoteFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, answers downloads instead of <see cref="RemoteFiles"/> (return <c>null</c> for a failure).</summary>
    public Func<string, Task<byte[]?>>? DownloadHandler { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<FileBrowserFolder> Folders => FolderList;

    /// <inheritdoc />
    public IReadOnlyList<FileBrowserEntry> Files => FileList;

    /// <inheritdoc />
    public string CurrentFolder { get; set; } = string.Empty;

    /// <summary>Progress values <see cref="TryGetTransferProgress"/> reports, by path.</summary>
    public Dictionary<string, TransferProgress> Progress { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public event EventHandler<ValueEventArgs<IReadOnlyList<FileBrowserFolder>>>? FoldersReceived;

    /// <inheritdoc />
    public event EventHandler<FileBrowserFilesEventArgs>? FilesReceived;

    /// <inheritdoc />
    public event EventHandler<FileReceivedEventArgs>? FileReceived;

    /// <inheritdoc />
    public event EventHandler<FileTransferFailedEventArgs>? TransferFailed;

    /// <inheritdoc />
    public event EventHandler<FileTransferProgressEventArgs>? TransferProgressChanged;

    private Task<bool> Send(string method, params object?[] arguments)
    {
        Record(method, arguments);
        return Task.FromResult(_session.SendResult && !_session.IsClosed);
    }

    private void Record(string method, object?[] arguments)
    {
        _session.Record(_prefix + method, arguments);
        OnCall?.Invoke(new RecordedCall(_prefix + method, arguments));
    }

    /// <inheritdoc />
    public Task<bool> StartAsync() => Send(nameof(StartAsync));

    /// <inheritdoc />
    public Task<bool> ChangeFolderAsync(string folder) => Send(nameof(ChangeFolderAsync), folder);

    /// <inheritdoc />
    public async Task<byte[]?> DownloadAsync(string path, CancellationToken cancellationToken = default)
    {
        Record(nameof(DownloadAsync), new object?[] { path });
        if (_session.IsClosed) return null;
        byte[]? content;
        try
        {
            // As the real session: a cancelled download gives null and raises nothing.
            content = DownloadHandler is not null ? await DownloadHandler(path).WaitAsync(cancellationToken).ConfigureAwait(false) : RemoteFiles.TryGetValue(path, out byte[]? stored) ? stored : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (content is null) RaiseTransferFailed(path, "No such file.");
        else RaiseFileReceived(path, content);
        return content;
    }

    /// <inheritdoc />
    public Task<bool> UploadAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        byte[] bytes = content.ToArray();
        Task<bool> result = Send(nameof(UploadAsync), path, bytes);
        if (!result.Result) return result;
        string? answer = ServerAnswersUploads ? AnswerFor(path, bytes) : null;
        bool refused = answer is not null && answer.StartsWith("Could not upload", StringComparison.Ordinal);
        if (!refused) RemoteFiles[path] = bytes;
        if (answer is not null) _session.RaiseFileBrowserMessage(answer);
        return result;
    }

    private string? AnswerFor(string path, byte[] bytes)
    {
        string? custom = UploadAnswer?.Invoke(path, bytes);
        if (UploadAnswer is not null && custom != "\0") return custom;
        int slash = path.LastIndexOf('/');
        string folder = slash < 0 ? CurrentFolder : path[..(slash + 1)];
        string name = path[(slash + 1)..];
        string full = folder + name;
        if (!FolderList.Any(rule => rule.AllowsWrite && rule.Covers(folder, name))) return $"Could not upload {full}: not in your folder rights";
        if (bytes.Length > _session.MaxUploadFileSize) return $"Could not upload {full}: larger than 20 MiB";
        return (bytes.Length > 32000 ? "Uploaded large file " : "Uploaded file ") + name;
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string path)
    {
        Task<bool> result = Send(nameof(DeleteAsync), path);
        if (result.Result) RemoteFiles.TryRemove(path, out _);
        return result;
    }

    /// <inheritdoc />
    public Task<bool> RenameAsync(string path, string newName) => Send(nameof(RenameAsync), path, newName);

    /// <inheritdoc />
    public Task<bool> MoveAsync(string path, string destinationFolder) => Send(nameof(MoveAsync), path, destinationFolder);

    /// <inheritdoc />
    public bool TryGetTransferProgress(string path, out TransferProgress progress) => Progress.TryGetValue(path, out progress);

    /// <summary>Replaces <see cref="FolderList"/> and raises <see cref="FoldersReceived"/>.</summary>
    /// <param name="folders">The folders.</param>
    public void RaiseFoldersReceived(IEnumerable<FileBrowserFolder> folders)
    {
        FolderList.Clear();
        FolderList.AddRange(folders);
        _session.Fire(FoldersReceived, new ValueEventArgs<IReadOnlyList<FileBrowserFolder>>(FolderList.ToArray()));
    }

    /// <summary>Sets <see cref="CurrentFolder"/> and <see cref="FileList"/> and raises <see cref="FilesReceived"/>.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="files">The files.</param>
    public void RaiseFilesReceived(string folder, IEnumerable<FileBrowserEntry> files)
    {
        CurrentFolder = folder;
        FileList.Clear();
        FileList.AddRange(files);
        _session.Fire(FilesReceived, new FileBrowserFilesEventArgs(folder, FileList.ToArray()));
    }

    /// <summary>Raises <see cref="FileReceived"/>.</summary>
    /// <param name="path">The path.</param>
    /// <param name="content">The bytes.</param>
    public void RaiseFileReceived(string path, byte[] content) => _session.Fire(FileReceived, new FileReceivedEventArgs(path, content));

    /// <summary>Raises <see cref="TransferFailed"/>.</summary>
    /// <param name="path">The path.</param>
    /// <param name="reason">Why.</param>
    public void RaiseTransferFailed(string path, string reason) => _session.Fire(TransferFailed, new FileTransferFailedEventArgs(path, reason));

    /// <summary>Raises <see cref="TransferProgressChanged"/>.</summary>
    public void RaiseTransferProgress(string path, long transferred, long total, bool upload = false) =>
        _session.Fire(TransferProgressChanged, new FileTransferProgressEventArgs(path, transferred, total, upload));
}
