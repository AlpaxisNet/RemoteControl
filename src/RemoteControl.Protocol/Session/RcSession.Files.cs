using System.IO.Compression;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Session;

public sealed partial class RcSession
{
    /// <summary>Files up to this size go in one raw block; larger files go as a large-file transfer in pieces of this size.</summary>
    internal const int UploadChunkSize = 32000;

    private static readonly TimeSpan ListingRequestLifetime = TimeSpan.FromMinutes(1);

    private readonly Queue<RcFileBrowser> _startOwners = new();
    private readonly List<ListingRequest> _listingRequests = new();
    private readonly List<Download> _downloads = new();
    private string _serverFolder = string.Empty;

    /// <summary>Who asked for a folder listing: a view, or nobody (a change of folder the session made before a file operation).</summary>
    private sealed record ListingRequest(RcFileBrowser? Owner, string? FolderKey, DateTime Time);

    private sealed class Download(RcFileBrowser owner, string path, string name)
    {
        public RcFileBrowser Owner { get; } = owner;

        public string Path { get; } = path;

        public string Name { get; } = name;

        public TaskCompletionSource<byte[]?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MemoryStream? Buffer { get; set; }

        public long Total { get; set; }

        public bool Large { get; set; }

        public CancellationTokenSource? Timer { get; set; }

        public CancellationTokenRegistration Cancellation { get; set; }
    }

    /// <summary>RClib <c>fileBrowserFolderKey</c>: forward slashes, no slash at either end, lower case.</summary>
    internal static string FolderKey(string? folder) => (folder ?? string.Empty).Replace('\\', '/').Trim('/').ToLowerInvariant();

    /// <summary>Splits <c>folder/name</c>; the folder keeps its trailing slash, as the server's folder keys do.</summary>
    internal static (string Folder, string Name) SplitPath(string? path)
    {
        string normalized = (path ?? string.Empty).Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? (string.Empty, normalized) : (normalized[..(slash + 1)], normalized[(slash + 1)..]);
    }

    /// <summary>A folder of a path: relative, without <c>.</c> or <c>..</c> segments, drive letters or line breaks.</summary>
    private static bool IsSafeFolder(string folder) =>
        !folder.StartsWith('/') && folder.IndexOfAny(new[] { ':', '\n', '\r' }) < 0
        && folder.Split('/', StringSplitOptions.RemoveEmptyEntries).All(segment => segment is not "." and not "..");

    private static bool IsBareName(string name) =>
        name.Length > 0 && name is not "." and not ".." && name.IndexOfAny(new[] { '/', '\\', ':', '\n', '\r' }) < 0 && RcText.ByteCount(name) <= GEncoding.MaxGStringLength;

    private void PruneListingRequests()
    {
        DateTime now = DateTime.UtcNow;
        _listingRequests.RemoveAll(request => now - request.Time > ListingRequestLifetime);
    }

    /// <summary>Changes the server's folder first when an operation needs another one. On the context.</summary>
    private Task<bool> EnsureFolderNow(string folder)
    {
        if (folder.Length == 0 || FolderKey(folder) == FolderKey(_serverFolder)) return Task.FromResult(true);
        PruneListingRequests();
        _listingRequests.Add(new ListingRequest(null, FolderKey(folder), DateTime.UtcNow));
        _serverFolder = folder;
        return SendRcNow(ClientPacketId.FileBrowserCd, writer => writer.WriteText(folder));
    }

    internal Task<bool> FileBrowserStart(RcFileBrowser view) => Run(() =>
    {
        view.ClearListing();
        PruneListingRequests();
        _startOwners.Enqueue(view);
        _listingRequests.Add(new ListingRequest(view, null, DateTime.UtcNow));
        return SendRcNow(ClientPacketId.FileBrowserStart);
    }, false);

    internal Task<bool> FileBrowserChangeFolder(RcFileBrowser view, string folder) => Run(() =>
    {
        string target = OneLine(folder).Trim();
        PruneListingRequests();
        _listingRequests.Add(new ListingRequest(view, FolderKey(target), DateTime.UtcNow));
        _serverFolder = target;
        return SendRcNow(ClientPacketId.FileBrowserCd, writer => writer.WriteText(target));
    }, false);

    internal Task<bool> FileBrowserDelete(RcFileBrowser view, string path) => Run(async () =>
    {
        (string folder, string name) = SplitPath(path);
        if (!IsBareName(name) || !IsSafeFolder(folder)) return Refused($"\"{path}\" is not a file name the server accepts.");
        Task<bool> cd = EnsureFolderNow(folder);
        Task<bool> delete = SendRcNow(ClientPacketId.FileBrowserDelete, writer => writer.WriteText(name));
        return await cd.ConfigureAwait(false) & await delete.ConfigureAwait(false);
    }, false);

    internal Task<bool> FileBrowserRename(RcFileBrowser view, string path, string newName) => Run(async () =>
    {
        (string folder, string name) = SplitPath(path);
        string target = SplitPath(newName).Name;
        if (!IsBareName(name) || !IsBareName(target) || !IsSafeFolder(folder)) return Refused($"\"{path}\" or \"{newName}\" is not a file name the server accepts.");
        Task<bool> cd = EnsureFolderNow(folder);
        Task<bool> rename = SendRcNow(ClientPacketId.FileBrowserRename, writer => writer.WriteGString(name).WriteGString(target));
        return await cd.ConfigureAwait(false) & await rename.ConfigureAwait(false);
    }, false);

    internal Task<bool> FileBrowserMove(RcFileBrowser view, string path, string destinationFolder) => Run(async () =>
    {
        (string folder, string name) = SplitPath(path);
        string destination = OneLine(destinationFolder).Trim();
        if (destination.Length == 0 || !IsBareName(name) || !IsSafeFolder(folder) || !IsSafeFolder(destination)) return Refused($"Cannot move \"{path}\" to \"{destinationFolder}\".");
        if (!destination.EndsWith('/')) destination += "/";
        Task<bool> cd = EnsureFolderNow(folder);
        Task<bool> move = SendRcNow(ClientPacketId.FileBrowserMove, writer => writer.WriteGString(destination).WriteText(name));
        _listingRequests.Add(new ListingRequest(view, FolderKey(destination), DateTime.UtcNow));
        _serverFolder = destination;
        Task<bool> refresh = SendRcNow(ClientPacketId.FileBrowserCd, writer => writer.WriteText(destination));
        return await cd.ConfigureAwait(false) & await move.ConfigureAwait(false) & await refresh.ConfigureAwait(false);
    }, false);

    private bool Refused(string message)
    {
        Refuse(message);
        return false;
    }

    // ---- Downloads ---------------------------------------------------------------------------------------------------------

    internal Task<byte[]?> FileBrowserDownload(RcFileBrowser view, string path, CancellationToken cancellationToken) => Run<byte[]?>(async () =>
    {
        (string folder, string name) = SplitPath(path);
        if (!IsBareName(name) || !IsSafeFolder(folder))
        {
            Refuse($"\"{path}\" is not a file name the server accepts.");
            return null;
        }

        // RClib: a file the listing shows as empty is delivered at once (the server answers an empty file with "send failed"). Not for
        // the MCP view: its listings may be minutes old, so it always asks, and its caller reads "send failed" with a fresh listing.
        bool sameFolder = folder.Length == 0 || FolderKey(folder) == FolderKey(view.CurrentFolder);
        if (!ReferenceEquals(view, _mcp) && sameFolder && view.Files.Any(entry => !entry.IsDirectory && entry.Size == 0 && entry.Name == name))
        {
            view.RaiseFileReceived(path, Array.Empty<byte>());
            return Array.Empty<byte>();
        }

        var download = new Download(view, path, name);
        _downloads.Add(download);
        Task<bool> cd = EnsureFolderNow(folder);
        Task<bool> request = SendRcNow(ClientPacketId.FileBrowserDown, writer => writer.WriteText(name));
        StartTimer(download);
        if (cancellationToken.CanBeCanceled)
            download.Cancellation = cancellationToken.Register(() => Post(() => FailDownload(download, "The download was cancelled.", raise: false)));
        if (!await cd.ConfigureAwait(false) | !await request.ConfigureAwait(false))
        {
            await OnContextAsync(() => FailDownload(download, "The request could not be sent.", raise: true)).ConfigureAwait(false);
        }

        return await download.Completion.Task.ConfigureAwait(false);
    }, null);

    private void StartTimer(Download download)
    {
        var timer = new CancellationTokenSource();
        download.Timer = timer;
        timer.Token.Register(() => Post(() => FailDownload(download, "The server sent nothing for too long.", raise: true)));
        timer.CancelAfter(_options.DownloadInactivityTimeout);
    }

    private void Restart(Download download)
    {
        try
        {
            if (download.Timer is { IsCancellationRequested: false } timer) timer.CancelAfter(_options.DownloadInactivityTimeout);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Finish(Download download)
    {
        _downloads.Remove(download);
        download.Cancellation.Dispose();
        CancellationTokenSource? timer = download.Timer;
        download.Timer = null;
        timer?.Dispose();
        download.Buffer?.Dispose();
    }

    private void FailDownload(Download download, string reason, bool raise)
    {
        if (!_downloads.Contains(download)) return;
        Finish(download);
        // The event first: whoever awaited the download and then listens for the next one must not get this one's.
        if (raise) download.Owner.RaiseTransferFailed(download.Path, reason);
        download.Completion.TrySetResult(null);
    }

    private void CompleteDownload(Download download, byte[] content)
    {
        if (!_downloads.Contains(download)) return;
        Finish(download);
        // The event first: whoever awaited the download and then listens for the next one must not get this one's.
        download.Owner.RaiseFileReceived(download.Path, content);
        download.Completion.TrySetResult(content);
    }

    private void AbortTransfers()
    {
        foreach (Download download in _downloads.ToArray())
        {
            Finish(download);
            download.Completion.TrySetResult(null);
        }
    }

    internal bool TryGetDownloadProgress(RcFileBrowser view, string path, out TransferProgress progress)
    {
        Download? download = _downloads.FirstOrDefault(item => item.Owner == view && item.Path == path);
        progress = download is null ? default : new TransferProgress(download.Buffer?.Length ?? 0, download.Total);
        return download is not null;
    }

    private Download? FindDownload(string name, bool large) =>
        _downloads.FirstOrDefault(item => item.Large == large && item.Name == name)
        ?? _downloads.FirstOrDefault(item => item.Large == large && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    private void FileSendFailed(string name)
    {
        Download? download = FindDownload(name, large: false) ?? FindDownload(name, large: true);
        if (download is not null) FailDownload(download, $"The server could not send {name}.", raise: true);
    }

    private void FileUpToDate(string name)
    {
        Download? download = FindDownload(name, large: true);
        if (download is not null) CompleteDownload(download, download.Buffer?.ToArray() ?? Array.Empty<byte>());
    }

    private void LargeFileStart(string name)
    {
        Download? download = FindDownload(name, large: false);
        if (download is null)
        {
            if (!IsBareName(name)) return;
            // Nobody asked: keep it for the windows' view, as RClib did.
            download = new Download(_browser, name, name);
            _downloads.Add(download);
            StartTimer(download);
        }

        download.Large = true;
        download.Buffer = new MemoryStream();
        Restart(download);
        Raise(FileBrowserMessageReceived, new TextEventArgs("Bigfile transfer started: " + name));
    }

    private void LargeFileSize(long size)
    {
        Download? download = _downloads.LastOrDefault(item => item.Large && item.Total == 0);
        if (download is null) return;
        download.Total = Math.Max(size, 0);
        Restart(download);
        download.Owner.ReportProgress(download.Path, download.Buffer?.Length ?? 0, download.Total, upload: false);
    }

    private void LargeFileEnd(string name)
    {
        Download? download = FindDownload(name, large: true);
        if (download is null) return;
        Raise(FileBrowserMessageReceived, new TextEventArgs("Bigfile transfer ended: " + name));
        CompleteDownload(download, download.Buffer?.ToArray() ?? Array.Empty<byte>());
    }

    private void HandleFile(PacketReader reader)
    {
        if (!reader.TryReadGInt5(out _) || !reader.TryReadGString(out string name)) return;
        byte[] content = reader.ReadRemainingBytes();
        Download? large = FindDownload(name, large: true);
        if (large is not null)
        {
            if (large.Buffer!.Length + content.Length > FrameCompression.MaxDecompressedSize * 8L)
            {
                FailDownload(large, "The file is too large.", raise: true);
                return;
            }

            large.Buffer.Write(content);
            Restart(large);
            large.Owner.ReportProgress(large.Path, large.Buffer.Length, large.Total, upload: false);
            return;
        }

        Download? small = FindDownload(name, large: false);
        if (small is not null)
        {
            small.Owner.ReportProgress(small.Path, content.Length, content.Length, upload: false);
            CompleteDownload(small, content);
            return;
        }

        // Nobody asked: hand it to the windows' view, as RClib did, unless the name is not a bare file name (a server's names are untrusted).
        if (IsBareName(name)) _browser.RaiseFileReceived(name, content);
    }

    // ---- Uploads -----------------------------------------------------------------------------------------------------------

    internal Task<bool> FileBrowserUpload(RcFileBrowser view, string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return FalseTask;
        byte[] bytes = content.ToArray();
        return Run(async () =>
        {
            (string folder, string name) = SplitPath(path);
            if (!IsBareName(name) || !IsSafeFolder(folder)) return Refused($"\"{path}\" is not a file name the server accepts.");
            byte[] nameBytes = RcText.GetBytes(name);
            var packets = new List<byte[]>();
            if (bytes.Length <= UploadChunkSize)
            {
                AddRawUpload(packets, nameBytes, bytes, 0, bytes.Length);
            }
            else
            {
                packets.Add(Frames.Packet((int)ClientPacketId.LargeFileStart, nameBytes));
                for (int offset = 0; offset < bytes.Length; offset += UploadChunkSize)
                    AddRawUpload(packets, nameBytes, bytes, offset, Math.Min(UploadChunkSize, bytes.Length - offset));
                packets.Add(Frames.Packet((int)ClientPacketId.LargeFileEnd, nameBytes));
            }

            Task<bool> cd = EnsureFolderNow(folder);
            int frameCount = packets.Count;
            long total = bytes.Length;
            view.ReportProgress(path, 0, total, upload: true);
            // Every frame of the upload is queued as one item, so nothing (another folder change in particular) can come between them.
            Task<bool> upload = SendRcNow(packets, written =>
            {
                long done = frameCount <= 1 ? total : Math.Min(total, (long)(written / 2) * UploadChunkSize);
                if (written == frameCount) done = total;
                Post(() => view.ReportProgress(path, done, total, upload: true));
            });
            bool success = await cd.ConfigureAwait(false) & await upload.ConfigureAwait(false);
            await OnContextAsync(() =>
            {
                view.EndUpload(path);
                if (!success) view.RaiseTransferFailed(path, _lastError ?? "The upload could not be sent.");
            }).ConfigureAwait(false);
            return success;
        }, false);
    }

    /// <summary>PLI_RAWDATA with the block's length, then the block: <c>[UP id][GByte name length][name][content][\n]</c>.</summary>
    private static void AddRawUpload(List<byte[]> packets, byte[] name, byte[] content, int offset, int count)
    {
        byte[] inner = new byte[3 + name.Length + count];
        inner[0] = GEncoding.EncodeGByte((int)ClientPacketId.FileBrowserUp);
        inner[1] = GEncoding.EncodeGByte(name.Length);
        name.CopyTo(inner, 2);
        Array.Copy(content, offset, inner, 2 + name.Length, count);
        inner[^1] = (byte)'\n';
        byte[] header = new byte[3];
        GEncoding.EncodeGInt3(inner.Length, header);
        packets.Add(Frames.Packet((int)ClientPacketId.RawData, header));
        packets.Add(inner);
    }

    // ---- Listings ----------------------------------------------------------------------------------------------------------

    private void HandleDirList(string text)
    {
        var folders = new List<FileBrowserFolder>();
        foreach (string raw in SplitParts(text))
        {
            string line = Text.TextHelpers.Trim(raw);
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0) continue;
            string pattern = Text.TextHelpers.Trim(line[(space + 1)..]);
            if (pattern.Length > 0) folders.Add(new FileBrowserFolder(line[..space], pattern));
        }

        RcFileBrowser owner = _startOwners.Count > 0 ? _startOwners.Dequeue() : _browser;
        owner.SetFolders(folders);
    }

    private ListingRequest? TakeListingRequest(string? folderKey)
    {
        PruneListingRequests();
        ListingRequest? request = folderKey is null
            ? _listingRequests.FirstOrDefault()
            : _listingRequests.FirstOrDefault(item => item.FolderKey == folderKey) ?? _listingRequests.FirstOrDefault(item => item.FolderKey is null);
        if (request is not null) _listingRequests.Remove(request);
        return request;
    }

    private void HandleDirectory(byte[] payload)
    {
        if (payload.Length == 0 || (payload.Length == 1 && payload[0] == ' '))
        {
            ListingRequest? empty = TakeListingRequest(null);
            if (empty is null) _browser.SetFiles(string.Empty, new List<FileBrowserEntry>());
            else empty.Owner?.SetFiles(string.Empty, new List<FileBrowserEntry>());
            return;
        }

        payload = UnwrapListing(payload);
        if (payload.Length > 0 && payload[0] == GEncoding.EncodeGByte((int)ServerPacketId.File))
        {
            HandleFile(new PacketReader(payload, 1, payload.Length - 1));
            return;
        }

        if (payload.Length > 0 && payload[0] == GEncoding.EncodeGByte((int)ServerPacketId.LargeFileStart))
        {
            LargeFileStart(FirstLine(RcText.GetString(payload.AsSpan(1))));
            return;
        }

        var reader = new PacketReader(payload);
        if (!reader.TryReadGString(out string folder)) return;
        var files = new List<FileBrowserEntry>();
        while (reader.Remaining >= 2)
        {
            reader.TrySkip(2);
            if (!reader.TryReadGString(out string name) || !reader.TryReadGString(out string rights)) break;
            if (!reader.TryReadGInt5(out long size) || !reader.TryReadGInt5(out long modified)) break;
            files.Add(new FileBrowserEntry(name, rights, size, modified));
        }

        _serverFolder = folder;
        ListingRequest? request = TakeListingRequest(FolderKey(folder));
        if (request is null) _browser.SetFiles(folder, files);
        else request.Owner?.SetFiles(folder, files);
    }

    /// <summary>
    /// RClib's guesses at a compressed listing: a bzip2 stream (also when its leading <c>B</c> is missing), a gzip stream, or a raw-data
    /// wrapper. RClib's "format byte" descrambling (D-17) is speculative and not reproduced.
    /// </summary>
    private static byte[] UnwrapListing(byte[] payload)
    {
        if (payload.Length >= 2 && payload[0] == 'Z' && payload[1] == 'h'
            && FrameCompression.TryBzip2Decompress(new byte[] { (byte)'B' }.Concat(payload).ToArray(), out byte[] fromBzip))
            return fromBzip;
        if (payload.Length >= 3 && payload[0] == 'B' && payload[1] == 'Z' && payload[2] == 'h' && FrameCompression.TryBzip2Decompress(payload, out byte[] bzip))
            return bzip;
        if (payload.Length >= 2 && payload[0] == 0x1F && payload[1] == 0x8B)
        {
            try
            {
                using var input = new MemoryStream(payload, writable: false);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                return output.ToArray();
            }
            catch (InvalidDataException)
            {
                return payload;
            }
        }

        if (payload.Length > 5 && payload[0] == GEncoding.EncodeGByte((int)ServerPacketId.RawData) && payload[4] == '\n')
            return payload.AsSpan(5).ToArray();
        return payload;
    }
}
