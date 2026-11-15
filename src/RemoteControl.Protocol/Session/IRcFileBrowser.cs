namespace RemoteControl.Protocol.Session;

/// <summary>
/// One consumer's view of the server's file browser. A session has two: <see cref="IRcSession.FileBrowser"/> for the windows and
/// <see cref="IRcSession.SyncFileBrowser"/> for the folder synchroniser. Each view has its own folder list, file list and events;
/// a reply goes to the view whose request it answers, so the windows never see the synchroniser's listings and downloads, and the
/// other way round. Paths are <c>folder/name</c>; the server works on bare names in its current folder, so an operation on another
/// folder first changes the server's folder.
/// </summary>
public interface IRcFileBrowser
{
    /// <summary>Whether this is the synchroniser's view.</summary>
    bool IsSync { get; }

    /// <summary>The folders this account may browse (from the last folder list this view asked for).</summary>
    IReadOnlyList<FileBrowserFolder> Folders { get; }

    /// <summary>The files of <see cref="CurrentFolder"/> (from the last listing this view asked for).</summary>
    IReadOnlyList<FileBrowserEntry> Files { get; }

    /// <summary>The folder of the last listing, as the server named it (e.g. <c>levels/</c>).</summary>
    string CurrentFolder { get; }

    /// <summary>The folder list arrived (RClib <c>on_filebrowser_folders</c> / <c>on_sync_filebrowser_folders</c>).</summary>
    event EventHandler<ValueEventArgs<IReadOnlyList<FileBrowserFolder>>>? FoldersReceived;

    /// <summary>A folder listing arrived (RClib <c>on_filebrowser_files</c> / <c>on_sync_filebrowser_files</c>).</summary>
    event EventHandler<FileBrowserFilesEventArgs>? FilesReceived;

    /// <summary>A file this view downloaded arrived (RClib <c>on_file_received</c> / <c>on_sync_file_received</c>).</summary>
    event EventHandler<FileReceivedEventArgs>? FileReceived;

    /// <summary>A download or upload of this view failed (the server refused it, the time ran out, or the connection ended).</summary>
    event EventHandler<FileTransferFailedEventArgs>? TransferFailed;

    /// <summary>A download or upload of this view made progress.</summary>
    event EventHandler<FileTransferProgressEventArgs>? TransferProgressChanged;

    /// <summary>Opens the file browser: the server answers with the folder list and the files of its current folder (RClib <c>rc_filebrowser_start</c>).</summary>
    /// <returns>Whether the request was sent.</returns>
    Task<bool> StartAsync();

    /// <summary>Changes the server's folder; the server answers with its files (RClib <c>rc_filebrowser_cd</c>).</summary>
    /// <param name="folder">The folder, as in the folder list (e.g. <c>levels/</c>).</param>
    /// <returns>Whether the request was sent.</returns>
    Task<bool> ChangeFolderAsync(string folder);

    /// <summary>Downloads a file (RClib <c>rc_filebrowser_download</c>). A file the listing shows as empty completes at once.</summary>
    /// <param name="path"><c>folder/name</c>, or a bare name in the current folder.</param>
    /// <param name="cancellationToken">Cancels the wait (the server may still send the file; it is then ignored).</param>
    /// <returns>The bytes, or <c>null</c> when the download failed, timed out or the session ended.</returns>
    Task<byte[]?> DownloadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a file into the folder of <paramref name="path"/> (RClib <c>rc_upload_file</c>, which used only the base name and so wrote
    /// into whatever folder the server was in). Files over 32000 bytes go as a large-file transfer in pieces.
    /// </summary>
    /// <param name="path"><c>folder/name</c>, or a bare name for the current folder.</param>
    /// <param name="content">The bytes.</param>
    /// <param name="cancellationToken">Cancels before the upload is queued.</param>
    /// <returns>Whether every piece was written (the server's answer arrives later as a file-browser message).</returns>
    Task<bool> UploadAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file (RClib <c>rc_filebrowser_delete</c>).</summary>
    /// <param name="path"><c>folder/name</c>.</param>
    /// <returns>Whether the request was sent.</returns>
    Task<bool> DeleteAsync(string path);

    /// <summary>Renames a file within its folder (RClib <c>rc_filebrowser_rename</c>).</summary>
    /// <param name="path"><c>folder/name</c>.</param>
    /// <param name="newName">The new bare name.</param>
    /// <returns>Whether the request was sent.</returns>
    Task<bool> RenameAsync(string path, string newName);

    /// <summary>Moves a file to another folder, then lists that folder (RClib <c>rc_filebrowser_move</c>).</summary>
    /// <param name="path"><c>folder/name</c>.</param>
    /// <param name="destinationFolder">The destination folder.</param>
    /// <returns>Whether the requests were sent.</returns>
    Task<bool> MoveAsync(string path, string destinationFolder);

    /// <summary>The progress of a transfer of this view (RClib <c>rc_filebrowser_transfer_progress</c>).</summary>
    /// <param name="path">The path as given to <see cref="DownloadAsync"/> or <see cref="UploadAsync"/>.</param>
    /// <param name="progress">The progress.</param>
    /// <returns>Whether such a transfer is running.</returns>
    bool TryGetTransferProgress(string path, out TransferProgress progress);
}
