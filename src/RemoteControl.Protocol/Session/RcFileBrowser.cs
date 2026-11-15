namespace RemoteControl.Protocol.Session;

/// <summary>One view of the file browser; the session routes the replies of this view's requests to it (see <see cref="IRcFileBrowser"/>).</summary>
internal sealed class RcFileBrowser : IRcFileBrowser
{
    private readonly RcSession _session;
    private List<FileBrowserFolder> _folders = new();
    private List<FileBrowserEntry> _files = new();
    private readonly Dictionary<string, TransferProgress> _uploads = new(StringComparer.Ordinal);

    public RcFileBrowser(RcSession session, bool isSync)
    {
        _session = session;
        IsSync = isSync;
    }

    public bool IsSync { get; }

    public IReadOnlyList<FileBrowserFolder> Folders => _folders.AsReadOnly();

    public IReadOnlyList<FileBrowserEntry> Files => _files.AsReadOnly();

    public string CurrentFolder { get; private set; } = string.Empty;

    public event EventHandler<ValueEventArgs<IReadOnlyList<FileBrowserFolder>>>? FoldersReceived;

    public event EventHandler<FileBrowserFilesEventArgs>? FilesReceived;

    public event EventHandler<FileReceivedEventArgs>? FileReceived;

    public event EventHandler<FileTransferFailedEventArgs>? TransferFailed;

    public event EventHandler<FileTransferProgressEventArgs>? TransferProgressChanged;

    public Task<bool> StartAsync() => _session.FileBrowserStart(this);

    public Task<bool> ChangeFolderAsync(string folder) => _session.FileBrowserChangeFolder(this, folder);

    public Task<byte[]?> DownloadAsync(string path, CancellationToken cancellationToken = default) => _session.FileBrowserDownload(this, path, cancellationToken);

    public Task<bool> UploadAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        _session.FileBrowserUpload(this, path, content, cancellationToken);

    public Task<bool> DeleteAsync(string path) => _session.FileBrowserDelete(this, path);

    public Task<bool> RenameAsync(string path, string newName) => _session.FileBrowserRename(this, path, newName);

    public Task<bool> MoveAsync(string path, string destinationFolder) => _session.FileBrowserMove(this, path, destinationFolder);

    public bool TryGetTransferProgress(string path, out TransferProgress progress)
    {
        if (_session.TryGetDownloadProgress(this, path, out progress)) return true;
        return _uploads.TryGetValue(path, out progress);
    }

    // ---- Called by the session on its context ------------------------------------------------------------------------------

    internal void SetFolders(List<FileBrowserFolder> folders)
    {
        _folders = folders;
        _session.Raise(FoldersReceived, new ValueEventArgs<IReadOnlyList<FileBrowserFolder>>(_folders.AsReadOnly()));
    }

    internal void SetFiles(string folder, List<FileBrowserEntry> files)
    {
        CurrentFolder = folder;
        _files = files;
        _session.Raise(FilesReceived, new FileBrowserFilesEventArgs(folder, _files.AsReadOnly()));
    }

    internal void ClearListing()
    {
        _folders = new List<FileBrowserFolder>();
        _files = new List<FileBrowserEntry>();
    }

    internal void RaiseFileReceived(string path, byte[] content) => _session.Raise(FileReceived, new FileReceivedEventArgs(path, content));

    internal void RaiseTransferFailed(string path, string reason) => _session.Raise(TransferFailed, new FileTransferFailedEventArgs(path, reason));

    internal void ReportProgress(string path, long transferred, long total, bool upload)
    {
        if (upload)
        {
            if (transferred >= total) _uploads.Remove(path);
            else _uploads[path] = new TransferProgress(transferred, total);
        }

        _session.Raise(TransferProgressChanged, new FileTransferProgressEventArgs(path, transferred, total, upload));
    }

    internal void EndUpload(string path) => _uploads.Remove(path);
}
