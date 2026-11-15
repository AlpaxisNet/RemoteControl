using System.Collections.Concurrent;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Protocol.Testing;

/// <summary>
/// A file-browser view of a <see cref="FakeRcSession"/>. Calls are recorded in the session's <see cref="FakeRcSession.Calls"/> as
/// <c>"FileBrowser.&lt;method&gt;"</c> or <c>"SyncFileBrowser.&lt;method&gt;"</c>. A download is answered from <see cref="RemoteFiles"/>
/// (raising <see cref="FileReceived"/>, or <see cref="TransferFailed"/> when the file is missing); an upload stores into it.
/// </summary>
public sealed class FakeFileBrowser : IRcFileBrowser
{
    private readonly FakeRcSession _session;
    private readonly string _prefix;

    internal FakeFileBrowser(FakeRcSession session, bool isSync)
    {
        _session = session;
        IsSync = isSync;
        _prefix = isSync ? "SyncFileBrowser." : "FileBrowser.";
    }

    /// <inheritdoc />
    public bool IsSync { get; }

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
        _session.Record(_prefix + method, arguments);
        return Task.FromResult(_session.SendResult && !_session.IsClosed);
    }

    /// <inheritdoc />
    public Task<bool> StartAsync() => Send(nameof(StartAsync));

    /// <inheritdoc />
    public Task<bool> ChangeFolderAsync(string folder) => Send(nameof(ChangeFolderAsync), folder);

    /// <inheritdoc />
    public async Task<byte[]?> DownloadAsync(string path, CancellationToken cancellationToken = default)
    {
        _session.Record(_prefix + nameof(DownloadAsync), new object?[] { path });
        if (_session.IsClosed) return null;
        byte[]? content = DownloadHandler is not null ? await DownloadHandler(path).ConfigureAwait(false) : RemoteFiles.TryGetValue(path, out byte[]? stored) ? stored : null;
        if (content is null) RaiseTransferFailed(path, "No such file.");
        else RaiseFileReceived(path, content);
        return content;
    }

    /// <inheritdoc />
    public Task<bool> UploadAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        byte[] bytes = content.ToArray();
        Task<bool> result = Send(nameof(UploadAsync), path, bytes);
        if (result.Result) RemoteFiles[path] = bytes;
        return result;
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
