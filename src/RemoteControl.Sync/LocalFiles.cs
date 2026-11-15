using RemoteControl.Core;

namespace RemoteControl.Sync;

/// <summary>What applying a server version did.</summary>
internal enum ApplyOutcome
{
    /// <summary>The local file already had the server's content.</summary>
    Unchanged,

    /// <summary>The local file was (re)written with the server's content.</summary>
    Written,

    /// <summary>The local file was edited and the server did not change since the last sync: the local edit stays.</summary>
    LocalModified,

    /// <summary>Both changed: the local file stays, the server version went to the conflict copy.</summary>
    Conflict,

    /// <summary>The local file is missing and counts as a deletion (remote deletes are on): nothing was written.</summary>
    DeletedLocally,

    /// <summary>A file operation failed.</summary>
    Failed,
}

/// <summary>The input of <see cref="LocalFiles.ApplyServerVersion"/>.</summary>
/// <param name="FullPath">The local file.</param>
/// <param name="Server">The server version (the whole document for scripts).</param>
/// <param name="ServerHash">The hash of <paramref name="Server"/>.</param>
/// <param name="Baseline">The hash of the local content as last synchronised, or <c>null</c>.</param>
/// <param name="ServerUnchanged">Whether the server did not change since the last sync.</param>
/// <param name="LegacyModified">Without a baseline: the time the C++ gave the file after its download (0 = unknown).</param>
/// <param name="MissingIsDeletion">Whether a missing local file counts as a deletion.</param>
/// <param name="ConflictFullPath">Where a conflict's server version goes.</param>
/// <param name="Modified">The server time to give a written file (0 = none).</param>
internal sealed record ApplyInput(string FullPath, byte[] Server, string ServerHash, string? Baseline, bool ServerUnchanged, long LegacyModified, bool MissingIsDeletion,
    string ConflictFullPath, long Modified);

/// <summary>The outcome of <see cref="LocalFiles.ApplyServerVersion"/>.</summary>
internal sealed record ApplyResult(ApplyOutcome Outcome, string? LocalHash, FileStamp? Stamp, string? Error);

/// <summary>One file to look at in a scan.</summary>
internal sealed record ScanRequest(string Key, string FullPath, string FolderPath, FileStamp? Stamp, string? KnownHash);

/// <summary>What a scan found.</summary>
internal sealed record ScanResult(string Key, bool Exists, bool FolderExists, FileStamp? Stamp, string? Hash, string? Error);

/// <summary>The file work of the engine; every method runs on a background task and never touches the engine's state.</summary>
internal static class LocalFiles
{
    /// <summary>
    /// Brings a server version into the workspace without losing a local edit: a missing file or an unchanged one (its hash equals the
    /// baseline; without a baseline, its time equals the C++ download time) is replaced; an edited one stays, and when the server changed too
    /// the server version is written to the conflict copy.
    /// </summary>
    public static ApplyResult ApplyServerVersion(ApplyInput input)
    {
        try
        {
            var info = new FileInfo(input.FullPath);
            if (!info.Exists)
            {
                if (input.MissingIsDeletion) return new ApplyResult(ApplyOutcome.DeletedLocally, null, null, null);
                return new ApplyResult(ApplyOutcome.Written, input.ServerHash, Write(input.FullPath, input.Server, input.Modified), null);
            }

            string local = SyncHash.OfFile(input.FullPath);
            if (local == input.ServerHash)
            {
                SetTime(input.FullPath, input.Modified);
                return new ApplyResult(ApplyOutcome.Unchanged, local, StampOf(input.FullPath), null);
            }

            bool localUnchanged = input.Baseline is not null
                ? local == input.Baseline
                : input.LegacyModified > 0 && new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds() == input.LegacyModified;
            if (localUnchanged) return new ApplyResult(ApplyOutcome.Written, input.ServerHash, Write(input.FullPath, input.Server, input.Modified), null);
            if (input.ServerUnchanged && input.Baseline is not null) return new ApplyResult(ApplyOutcome.LocalModified, local, StampOf(input.FullPath), null);
            AtomicFile.WriteAllBytes(input.ConflictFullPath, input.Server);
            return new ApplyResult(ApplyOutcome.Conflict, local, StampOf(input.FullPath), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ApplyResult(ApplyOutcome.Failed, null, null, exception.Message);
        }
    }

    /// <summary>Looks at files: a file whose stamp did not change keeps its known hash; others are hashed (larger than the limit: an error).</summary>
    public static ScanResult[] Scan(IReadOnlyList<ScanRequest> requests, long maxFileSize, CancellationToken cancellationToken)
    {
        var results = new ScanResult[requests.Count];
        for (int index = 0; index < requests.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanRequest request = requests[index];
            try
            {
                var info = new FileInfo(request.FullPath);
                if (!info.Exists)
                {
                    results[index] = new ScanResult(request.Key, false, Directory.Exists(request.FolderPath), null, null, null);
                    continue;
                }

                var stamp = new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
                if (request.Stamp == stamp && request.KnownHash is not null)
                {
                    results[index] = new ScanResult(request.Key, true, true, stamp, request.KnownHash, null);
                    continue;
                }

                if (info.Length > maxFileSize)
                {
                    results[index] = new ScanResult(request.Key, true, true, stamp, null, $"The file is larger than {maxFileSize / (1024 * 1024)} MiB.");
                    continue;
                }

                results[index] = new ScanResult(request.Key, true, true, stamp, SyncHash.OfFile(request.FullPath), null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                results[index] = new ScanResult(request.Key, true, true, null, null, exception.Message);
            }
        }

        return results;
    }

    /// <summary>Reads a file for an upload; <c>null</c> when it is missing, too large or unreadable.</summary>
    public static byte[]? ReadForUpload(string path, long maxFileSize, out string? error)
    {
        error = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = "The file is missing.";
                return null;
            }

            if (info.Length > maxFileSize)
            {
                error = $"The file is larger than {maxFileSize / (1024 * 1024)} MiB.";
                return null;
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = exception.Message;
            return null;
        }
    }

    /// <summary>Writes a file atomically and gives it the server time.</summary>
    public static FileStamp? Write(string path, byte[] content, long modified)
    {
        AtomicFile.WriteAllBytes(path, content);
        SetTime(path, modified);
        return StampOf(path);
    }

    /// <summary><c>applyModifiedTime</c>: gives a file the server's time (when known and different).</summary>
    public static void SetTime(string path, long modified)
    {
        if (modified <= 0) return;
        DateTime time = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(modified, 0, 253402300799)).UtcDateTime;
        if (File.GetLastWriteTimeUtc(path) != time) File.SetLastWriteTimeUtc(path, time);
    }

    /// <summary>The stamp of a file, or <c>null</c>.</summary>
    public static FileStamp? StampOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : null;
    }

    /// <summary>Creates the workspace folders (<c>prepareWorkspace</c>).</summary>
    public static string? PrepareWorkspace(string workspace)
    {
        try
        {
            foreach (string folder in SyncNames.WorkspaceFolders) Directory.CreateDirectory(Path.Combine(workspace, folder));
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return exception.Message;
        }
    }
}
