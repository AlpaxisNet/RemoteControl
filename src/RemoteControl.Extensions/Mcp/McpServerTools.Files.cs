using System.Text.Json;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Extensions;

internal static partial class McpServerTools
{
    /// <summary>The most bytes a file read returns inline (as text or Base64); larger files go through <c>rc_export</c>.</summary>
    internal const int MaximumInlineBytes = 8 * 1024 * 1024;

    /// <summary>The largest file a delete or a move backs up; a larger one is deleted or replaced only with <c>force: true</c>.</summary>
    internal const long MaximumBackupBytes = 64L * 1024 * 1024;

    private const string Uploaded = "Uploaded file ";
    private const string UploadedLarge = "Uploaded large file ";
    private const string CouldNotUpload = "Could not upload ";
    private const string FolderRefused = "Folder ";
    private const string NotInRights = " is not in your folder rights";

    // ---- Paths -----------------------------------------------------------------------------------------------------------------

    /// <summary>A server folder as the file browser names it: forward slashes, no leading slash, a trailing slash.</summary>
    internal static string FolderOf(string folder)
    {
        string normalized = (folder ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        if (normalized.Length == 0) throw new McpToolException("Give a folder, such as levels/ (rc_list_files without a folder lists them).");
        if (normalized.Split('/').Any(segment => segment is "." or "..") || normalized.Contains(':')) throw new McpToolException("\"" + folder + "\" is not a folder the server accepts.");
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    /// <summary>Splits <c>folder/name</c>; both parts must be there.</summary>
    internal static (string Folder, string Name) SplitServerPath(string path)
    {
        string normalized = (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        int slash = normalized.LastIndexOf('/');
        if (slash <= 0 || slash == normalized.Length - 1) throw new McpToolException("\"" + path + "\" is not a file path: give folder/name, such as levels/start.nw.");
        string name = normalized[(slash + 1)..];
        if (name is "." or ".." || name.Contains(':')) throw new McpToolException("\"" + path + "\" is not a file name the server accepts.");
        return (FolderOf(normalized[..slash]), name);
    }

    // ---- Listings ----------------------------------------------------------------------------------------------------------------

    /// <summary>The folder rights of the account, from the MCP view (asked again when <paramref name="refresh"/>).</summary>
    internal static async Task<IReadOnlyList<FileBrowserFolder>> FoldersAsync(McpToolContext c, McpSessionTarget target, bool refresh)
    {
        IRcFileBrowser browser = target.Session.McpFileBrowser;
        if (!refresh && browser.Folders.Count > 0) return browser.Folders;
        ValueEventArgs<IReadOnlyList<FileBrowserFolder>> folders = await c.RequestAsync<ValueEventArgs<IReadOnlyList<FileBrowserFolder>>>(target.Session,
            h => browser.FoldersReceived += h, h => browser.FoldersReceived -= h, _ => true, browser.StartAsync, "the folder list").ConfigureAwait(true);
        return folders.Value;
    }

    /// <summary>A folder's files, from the MCP view.</summary>
    internal static async Task<IReadOnlyList<FileBrowserEntry>> ListFolderAsync(McpToolContext c, McpSessionTarget target, string folder)
    {
        IRcFileBrowser browser = target.Session.McpFileBrowser;
        string key = folder.Trim('/').ToLowerInvariant();
        FileBrowserFilesEventArgs files = await c.RequestAsync<FileBrowserFilesEventArgs>(target.Session, h => browser.FilesReceived += h, h => browser.FilesReceived -= h,
            e => e.Folder.Replace('\\', '/').Trim('/').ToLowerInvariant() == key, () => browser.ChangeFolderAsync(folder), "the listing of " + folder).ConfigureAwait(true);
        return files.Files;
    }

    public static async Task<string> ListFilesAsync(McpToolContext c)
    {
        c.RequireRc();
        if (!c.Args.TryString("folder", out string given) || given.Trim().Length == 0)
        {
            IReadOnlyList<FileBrowserFolder> folders = await FoldersAsync(c, c.Target, refresh: true).ConfigureAwait(true);
            return JsonText.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("folders");
                foreach (FileBrowserFolder folder in folders)
                {
                    writer.WriteStartObject();
                    McpJson.Text(writer, "folder", folder.Folder);
                    McpJson.Text(writer, "pattern", folder.Pattern);
                    McpJson.Text(writer, "rights", folder.Rights);
                    writer.WriteBoolean("writable", folder.AllowsWrite);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        }
        string name = FolderOf(given);
        await FoldersAsync(c, c.Target, refresh: false).ConfigureAwait(true);
        IReadOnlyList<FileBrowserEntry> entries = await ListFolderAsync(c, c.Target, name).ConfigureAwait(true);
        Func<string, bool> filter = NameFilter(c.Args.String("query"));
        FileBrowserEntry[] shown = entries.Where(entry => filter(entry.Name)).OrderBy(entry => !entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            McpJson.Text(writer, "folder", name);
            writer.WriteNumber("count", shown.Length);
            writer.WriteStartArray("files");
            foreach (FileBrowserEntry entry in shown)
            {
                writer.WriteStartObject();
                McpJson.Text(writer, "name", entry.Name);
                McpJson.Text(writer, "path", name + entry.Name);
                writer.WriteNumber("size", entry.Size);
                writer.WriteString("modified", entry.ModifiedTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
                McpJson.Text(writer, "rights", entry.Rights);
                if (entry.IsDirectory) writer.WriteBoolean("directory", true);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    // ---- Transfers ---------------------------------------------------------------------------------------------------------------

    /// <summary>Downloads a file through the MCP view of <paramref name="target"/>.</summary>
    /// <exception cref="McpToolException">The server refused it or sent nothing.</exception>
    internal static async Task<byte[]> DownloadAsync(McpToolContext c, McpSessionTarget target, string path)
    {
        IRcFileBrowser browser = target.Session.McpFileBrowser;
        // The session hands out a file its view lists as empty without asking the server. A listing is only a moment: the file may have
        // been written since (by the Files window, other staff, a rename onto it), so such a listing is asked for again first.
        int slash = path.LastIndexOf('/');
        if (slash > 0)
        {
            string folder = path[..(slash + 1)];
            string name = path[(slash + 1)..];
            if (browser.CurrentFolder.Trim('/').Equals(folder.Trim('/'), StringComparison.OrdinalIgnoreCase)
                && browser.Files.Any(entry => entry.Size == 0 && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
                await ListFolderAsync(c, target, folder).ConfigureAwait(true);
        }
        string? reason = null;
        EventHandler<FileTransferFailedEventArgs> failed = (_, e) =>
        {
            if (e.Path == path) reason = e.Reason;
        };
        browser.TransferFailed += failed;
        try
        {
            byte[]? content = await browser.DownloadAsync(path, c.Token).ConfigureAwait(true);
            c.Token.ThrowIfCancellationRequested();
            return content ?? throw new McpToolException("The download of " + path + " from " + target.ServerName + " failed" + (reason is null ? McpToolContext.Because(target.Session) : ": " + reason) + ".");
        }
        finally
        {
            browser.TransferFailed -= failed;
        }
    }

    /// <summary>The server's answer to an upload: whether it took the file, and what it said (empty when it said nothing in time).</summary>
    internal sealed record UploadAnswer(bool Accepted, bool Answered, string Message);

    /// <summary>Uploads a file through the MCP view and waits for the server's message about it.</summary>
    internal static async Task<UploadAnswer> UploadAsync(McpToolContext c, McpSessionTarget target, string folder, string name, byte[] content)
    {
        IRcSession session = target.Session;
        if (session.MaxUploadFileSize > 0 && content.LongLength > session.MaxUploadFileSize)
            throw new McpToolException(folder + name + " has " + content.LongLength + " bytes, more than the " + session.MaxUploadFileSize + " bytes " + target.ServerName + " takes.");
        var answer = new TaskCompletionSource<UploadAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool sending = false;
        EventHandler<TextEventArgs> onMessage = (_, e) =>
        {
            if (!sending) return;
            UploadAnswer? read = ReadUploadAnswer(folder, name, e.Text);
            if (read is not null) answer.TrySetResult(read);
        };
        session.FileBrowserMessageReceived += onMessage;
        try
        {
            sending = true;
            if (!await session.McpFileBrowser.UploadAsync(folder + name, content, c.Token).ConfigureAwait(true))
                throw new McpToolException("The upload of " + folder + name + " to " + target.ServerName + " could not be sent" + McpToolContext.Because(session) + ".");
            UploadAnswer result;
            try
            {
                result = await c.WaitAsync(answer.Task, "the upload of " + folder + name).ConfigureAwait(true);
            }
            catch (McpToolException) when (!c.Token.IsCancellationRequested)
            {
                result = new UploadAnswer(true, false, string.Empty);
            }
            return result;
        }
        finally
        {
            session.FileBrowserMessageReceived -= onMessage;
        }
    }

    /// <summary>
    /// The server's message about an upload of <paramref name="name"/> into <paramref name="folder"/>, or <c>null</c> when it is about something
    /// else (as the Import Missing upload reads it): <c>Uploaded file NAME</c>, <c>Uploaded large file NAME</c>, <c>Could not upload PATH: REASON</c>,
    /// and <c>Folder X is not in your folder rights</c> for this folder.
    /// </summary>
    internal static UploadAnswer? ReadUploadAnswer(string folder, string name, string text)
    {
        string line = (text ?? string.Empty).Trim();
        if (line.StartsWith(UploadedLarge, StringComparison.Ordinal) && string.Equals(line[UploadedLarge.Length..], name, StringComparison.OrdinalIgnoreCase)) return new UploadAnswer(true, true, line);
        if (line.StartsWith(Uploaded, StringComparison.Ordinal) && string.Equals(line[Uploaded.Length..], name, StringComparison.OrdinalIgnoreCase)) return new UploadAnswer(true, true, line);
        if (line.StartsWith(FolderRefused, StringComparison.Ordinal) && line.EndsWith(NotInRights, StringComparison.Ordinal))
        {
            string refused = line[FolderRefused.Length..^NotInRights.Length];
            return string.Equals(refused.Trim('/'), folder.Trim('/'), StringComparison.OrdinalIgnoreCase) ? new UploadAnswer(false, true, line) : null;
        }
        if (!line.StartsWith(CouldNotUpload, StringComparison.Ordinal)) return null;
        string rest = line[CouldNotUpload.Length..];
        int separator = rest.IndexOf(": ", StringComparison.Ordinal);
        if (separator < 0) return null;
        string path = rest[..separator];
        return string.Equals(path, folder + name, StringComparison.OrdinalIgnoreCase) || string.Equals(path, name, StringComparison.OrdinalIgnoreCase) ? new UploadAnswer(false, true, line) : null;
    }

    /// <summary>The entry of a file in its folder's listing, or <c>null</c> when the folder has none of that name.</summary>
    internal static async Task<FileBrowserEntry?> FindFileAsync(McpToolContext c, McpSessionTarget target, string folder, string name)
    {
        await FoldersAsync(c, target, refresh: false).ConfigureAwait(true);
        IReadOnlyList<FileBrowserEntry> entries = await ListFolderAsync(c, target, folder).ConfigureAwait(true);
        return entries.FirstOrDefault(entry => !entry.IsDirectory && entry.Name == name) ?? entries.FirstOrDefault(entry => !entry.IsDirectory && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Uploads a file after backing up the one it replaces; returns the answer, whether it replaced one, and the backup. Without
    /// <paramref name="lookedUp"/> the destination folder is listed first to find the file it replaces.
    /// </summary>
    internal static async Task<(UploadAnswer Answer, bool Replaced, McpBackupEntry? Backup)> PutFileAsync(McpToolContext c, McpSessionTarget target, string folder, string name, byte[] content, byte[]? previous, bool lookedUp, string reason)
    {
        FileBrowserEntry? existing = previous is null && !lookedUp ? await FindFileAsync(c, target, folder, name).ConfigureAwait(true) : null;
        if (previous is null && existing is not null) previous = existing.Size == 0 ? Array.Empty<byte>() : await DownloadAsync(c, target, folder + existing.Name).ConfigureAwait(true);
        McpBackupEntry? backup = previous is null ? null : await c.BackupAsync(target.ServerName, "file", folder + name, 0, string.Empty, previous, reason).ConfigureAwait(true);
        UploadAnswer answer = await UploadAsync(c, target, folder, name, content).ConfigureAwait(true);
        return (answer, previous is not null, backup);
    }

    // ---- Tools -------------------------------------------------------------------------------------------------------------------

    public static async Task<string> ReadFileAsync(McpToolContext c)
    {
        c.RequireRc();
        (string folder, string name) = SplitServerPath(c.Args.RequiredString("path"));
        string encoding = McpFileText.Normalize(c.Args.String("encoding"));
        byte[] content;
        try
        {
            content = await DownloadAsync(c, c.Target, folder + name).ConfigureAwait(true);
        }
        catch (McpToolException)
        {
            // The server answers a download of an empty file with "send failed": an empty file of that name is read as empty.
            FileBrowserEntry? entry = await FindFileAsync(c, c.Target, folder, name).ConfigureAwait(true);
            if (entry is not { Size: 0 }) throw;
            content = Array.Empty<byte>();
        }
        bool range = c.Args.Has("start") || c.Args.Has("end");
        bool search = c.Args.Has("query");
        if (content.Length > MaximumInlineBytes && (encoding == "base64" || !range && !search))
            throw new McpToolException(folder + name + " has " + content.Length + " bytes; read it in parts with start/end, search it with query, or use rc_export to put it in a local folder.");
        if (encoding == "base64")
        {
            return JsonText.Write(writer =>
            {
                writer.WriteStartObject();
                McpJson.Text(writer, "path", folder + name);
                writer.WriteNumber("size", content.Length);
                writer.WriteString("encoding", "base64");
                writer.WriteString("base64", Convert.ToBase64String(content));
                writer.WriteEndObject();
            });
        }
        if (McpFileText.IsBinary(content) && encoding == "auto") throw new McpToolException(folder + name + " is a binary file (" + content.Length + " bytes); read it with encoding base64.");
        // Decoding, counting and searching a large file take time: they run off the UI thread (AGENTS.md rule 1).
        Func<string, bool>? matches = search ? Matcher(c.Args) : null;
        int contextLines = c.Args.Int("contextLines", 0);
        int start = c.Args.Int("start", 1);
        int end = c.Args.Int("end", start + McpProtocol.MaximumLines - 1);
        (string used, int lines, string body) = await Task.Run(() =>
        {
            string decoded = McpFileText.Decode(content, encoding, out string text);
            int budget = McpProtocol.MaximumLines;
            string part = matches is not null ? SearchText(text, matches, contextLines, ref budget) : range ? McpProtocol.EditorLineRange(text, start, end) : text;
            return (decoded, LineCount(text), part);
        }, c.Token).ConfigureAwait(true);
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            McpJson.Text(writer, "path", folder + name);
            writer.WriteNumber("size", content.Length);
            writer.WriteString("encoding", used);
            writer.WriteNumber("lines", lines);
            McpJson.Text(writer, search ? "matches" : range ? "numbered" : "text", body);
            writer.WriteEndObject();
        });
    }

    public static async Task<string> SearchFilesAsync(McpToolContext c)
    {
        c.RequireRc();
        string folder = FolderOf(c.Args.RequiredString("folder"));
        Func<string, bool> matches = Matcher(c.Args);
        Func<string, bool> names = NameFilter(c.Args.String("pattern"));
        int maxFiles = Math.Clamp(c.Args.Int("maxFiles", 100), 1, 1000);
        long maxSize = Math.Clamp(c.Args.Int("maxFileBytes", 2 * 1024 * 1024), 1, MaximumInlineBytes);
        await FoldersAsync(c, c.Target, refresh: false).ConfigureAwait(true);
        FileBrowserEntry[] files = (await ListFolderAsync(c, c.Target, folder).ConfigureAwait(true))
            .Where(entry => !entry.IsDirectory && entry.Size > 0 && names(entry.Name)).OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        FileBrowserEntry[] searched = files.Where(entry => entry.Size <= maxSize).Take(maxFiles).ToArray();
        var downloads = new Dictionary<string, Task<byte[]>>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(4);
        foreach (FileBrowserEntry entry in searched) downloads[entry.Name] = Limited(entry.Name);
        try
        {
            await Task.WhenAll(downloads.Values).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is McpToolException or OperationCanceledException)
        {
            // Reported per file.
        }
        int maximum = Math.Clamp(c.Args.Int("maxMatches", 500), 1, 5000);
        int context = c.Args.Int("contextLines", 0);
        // The downloads came here; decoding and searching them run off the UI thread (AGENTS.md rule 1).
        var contents = searched.Where(entry => downloads[entry.Name].IsCompletedSuccessfully).Select(entry => (entry.Name, Bytes: downloads[entry.Name].Result)).ToArray();
        CancellationToken token = c.Token;
        (List<(string Name, string Found)> hits, int left) = await Task.Run(() =>
        {
            var found = new List<(string, string)>();
            int budget = maximum;
            foreach ((string fileName, byte[] bytes) in contents)
            {
                if (budget <= 0 || token.IsCancellationRequested) break;
                if (McpFileText.IsBinary(bytes)) continue;
                McpFileText.Decode(bytes, "auto", out string text);
                string lines = SearchText(text, matches, context, ref budget);
                if (lines.Length > 0) found.Add((fileName, lines));
            }
            return (found, budget);
        }, token).ConfigureAwait(true);
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            McpJson.Text(writer, "folder", folder);
            writer.WriteNumber("searched", searched.Length);
            writer.WriteNumber("skipped", files.Length - searched.Length);
            writer.WriteStartArray("results");
            foreach ((string fileName, string found) in hits)
            {
                writer.WriteStartObject();
                McpJson.Text(writer, "path", folder + fileName);
                McpJson.Text(writer, "matches", found);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteBoolean("truncated", left <= 0);
            WriteFailures(writer, searched.Where(entry => !downloads[entry.Name].IsCompletedSuccessfully).Select(entry => (folder + entry.Name, downloads[entry.Name].Exception?.InnerException?.Message ?? "cancelled")));
            writer.WriteEndObject();
        });

        async Task<byte[]> Limited(string name)
        {
            await gate.WaitAsync(c.Token).ConfigureAwait(true);
            try
            {
                return await DownloadAsync(c, c.Target, folder + name).ConfigureAwait(true);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public static async Task<string> WriteFileAsync(McpToolContext c)
    {
        c.RequireRc();
        (string folder, string name) = SplitServerPath(c.Args.RequiredString("path"));
        string encoding = McpFileText.Normalize(c.Args.String("encoding"));
        IReadOnlyList<McpArguments> edits = EditsOf(c.Args);
        bool hasText = c.Args.TryString("text", out string text);
        bool hasBase64 = c.Args.TryString("base64", out string base64);
        if ((hasText ? 1 : 0) + (hasBase64 ? 1 : 0) + (edits.Count > 0 ? 1 : 0) != 1) throw new McpToolException("Give exactly one of text, base64 or edits.");
        await FoldersAsync(c, c.Target, refresh: false).ConfigureAwait(true);
        FileBrowserEntry? existing = await FindFileAsync(c, c.Target, folder, name).ConfigureAwait(true);
        if (existing is null && !c.Args.Bool("create", true)) throw new McpToolException(folder + name + " does not exist and create is false.");
        if (existing is not null) name = existing.Name;
        // Edits need the old text; a whole new content needs it only for the backup, which force may go without.
        byte[]? previous = existing is null ? null : existing.Size == 0 ? Array.Empty<byte>()
            : edits.Count > 0 ? await DownloadAsync(c, c.Target, folder + name).ConfigureAwait(true)
            : await c.ReadForBackupAsync(() => DownloadAsync(c, c.Target, folder + name), folder + name).ConfigureAwait(true);
        bool unread = existing is not null && previous is null;
        byte[] content;
        string used = encoding;
        var notes = new List<string>();
        if (hasBase64)
        {
            try { content = Convert.FromBase64String(base64); }
            catch (FormatException) { throw new McpToolException("base64 is not valid Base64."); }
            used = "base64";
        }
        else if (edits.Count > 0)
        {
            if (previous is null) throw new McpToolException(folder + name + " does not exist, so there is nothing to edit.");
            if (McpFileText.IsBinary(previous)) throw new McpToolException(folder + name + " is a binary file; write it with base64.");
            bool chosen = encoding is not ("auto" or "base64");
            used = McpFileText.Decode(previous, chosen ? encoding : "auto", out string current);
            bool crlf = current.Contains("\r\n", StringComparison.Ordinal);
            string edited = ApplyEdits(current.Replace("\r\n", "\n", StringComparison.Ordinal), edits);
            // A file of plain ASCII has no encoding of its own yet: its new text goes in the servers' encoding when it fits.
            if (!chosen && McpFileText.IsAscii(previous)) used = "auto";
            // A chosen UTF-8 form is written as chosen (with or without its mark), as a whole text would be.
            if (chosen && encoding is "utf8" or "utf8-bom") used = encoding;
            (content, used) = EncodeKeeping(crlf ? edited.Replace("\n", "\r\n", StringComparison.Ordinal) : edited, used, chosen, notes);
            if (used == "auto") used = McpFileText.Decode(content, "auto", out _);
        }
        else
        {
            if (encoding == "base64") throw new McpToolException("encoding base64 goes with the base64 argument, not text.");
            bool chosen = encoding != "auto";
            if (!chosen && previous is not null && !McpFileText.IsBinary(previous) && !McpFileText.IsAscii(previous)) encoding = McpFileText.Decode(previous, "auto", out _);
            (content, used) = EncodeKeeping(text, encoding, chosen, notes);
            if (used == "auto") used = McpFileText.Decode(content, "auto", out _);
        }
        if (previous is not null && previous.AsSpan().SequenceEqual(content)) return Status(folder + name + " already has this content; nothing was sent.");
        await c.ApproveAsync("Upload " + folder + name + " to " + c.Server + " (" + content.Length + " bytes" + (previous is null ? ", a new file" : ", replacing " + previous.Length + " bytes") + ")").ConfigureAwait(true);
        if (c.Options.McpApprove && !unread)
        {
            // The question may have been open for a while: the upload must replace the file it was about (and that is backed up).
            FileBrowserEntry? now = await FindFileAsync(c, c.Target, folder, name).ConfigureAwait(true);
            byte[]? again = now is null ? null : now.Size == 0 ? Array.Empty<byte>() : await DownloadAsync(c, c.Target, folder + now.Name).ConfigureAwait(true);
            if ((again is null) != (previous is null) || again is not null && !again.AsSpan().SequenceEqual(previous))
                throw new McpToolException(folder + name + " changed on " + c.Server + " while the question was open; nothing was sent. Read it again and repeat the change.");
        }
        long since = c.Target.Journal.Sequence;
        (UploadAnswer answer, bool replaced, McpBackupEntry? backup) = await PutFileAsync(c, c.Target, folder, name, content, previous, lookedUp: true, "rc_write_file").ConfigureAwait(true);
        replaced |= unread;
        if (answer.Answered && !answer.Accepted) throw new McpToolException("The server refused " + folder + name + ": " + answer.Message);
        IReadOnlyList<McpJournalEntry> events = c.Target.Journal.Read(since, new[] { McpSessionJournal.Files, McpSessionJournal.Error }, 50);
        return Written(c, events, backup, folder + name + (replaced ? " was replaced" : " was uploaded") + " on " + c.Server + (answer.Answered ? "." : "; the server did not confirm it in time."), writer =>
        {
            writer.WriteNumber("bytes", content.Length);
            writer.WriteString("encoding", used);
            writer.WriteBoolean("created", !replaced);
            writer.WriteBoolean("confirmed", answer.Answered);
            WriteEdits(writer, notes);
            if (folder.StartsWith("levels/", StringComparison.OrdinalIgnoreCase) && (name.EndsWith(".nw", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".graal", StringComparison.OrdinalIgnoreCase)))
                writer.WriteString("hint", "Players see a changed level after rc_update_levels.");
        });
    }

    public static async Task<string> DeleteFileAsync(McpToolContext c)
    {
        c.RequireRc();
        (string folder, string name) = SplitServerPath(c.Args.RequiredString("path"));
        FileBrowserEntry existing = await FindFileAsync(c, c.Target, folder, name).ConfigureAwait(true) ?? throw new McpToolException("There is no file " + folder + name + " on " + c.Server + ".");
        bool force = c.Args.Bool("force", false);
        if (existing.Size > MaximumBackupBytes && !force)
            throw new McpToolException(folder + existing.Name + " has " + existing.Size + " bytes, more than a backup keeps (" + MaximumBackupBytes + "); give force: true to delete it without one.");
        await c.ApproveAsync("Delete " + folder + existing.Name + " (" + existing.Size + " bytes) on " + c.Server + (existing.Size > MaximumBackupBytes ? ", without a backup" : string.Empty)).ConfigureAwait(true);
        McpBackupEntry? backup = null;
        if (existing.Size <= MaximumBackupBytes)
        {
            byte[]? previous = existing.Size == 0 ? Array.Empty<byte>() : await c.ReadForBackupAsync(() => DownloadAsync(c, c.Target, folder + existing.Name), folder + existing.Name).ConfigureAwait(true);
            if (previous is not null) backup = await c.BackupAsync(c.Server, "file", folder + existing.Name, 0, string.Empty, previous, "rc_delete_file").ConfigureAwait(true);
        }
        long since = c.Target.Journal.Sequence;
        McpToolContext.Sent(await c.Session.McpFileBrowser.DeleteAsync(folder + existing.Name).ConfigureAwait(true), c.Session, "The deletion");
        return Written(c, await c.CollectAsync(c.Target, since, 1000, McpSessionJournal.Files, McpSessionJournal.Error).ConfigureAwait(true), backup, folder + existing.Name + " was deleted on " + c.Server + ".");
    }

    public static async Task<string> MoveFileAsync(McpToolContext c)
    {
        c.RequireRc();
        (string folder, string name) = SplitServerPath(c.Args.RequiredString("path"));
        string newName = c.Args.String("newName").Trim();
        string destination = c.Args.TryString("folder", out string given) && given.Trim().Length > 0 ? FolderOf(given) : folder;
        if (newName.Contains('/') || newName.Contains('\\') || newName.Contains(':')) throw new McpToolException("newName is a bare file name; give the folder separately.");
        FileBrowserEntry existing = await FindFileAsync(c, c.Target, folder, name).ConfigureAwait(true) ?? throw new McpToolException("There is no file " + folder + name + " on " + c.Server + ".");
        bool moving = !string.Equals(destination, folder, StringComparison.OrdinalIgnoreCase);
        bool renaming = newName.Length > 0 && newName != existing.Name;
        if (!moving && !renaming) throw new McpToolException("Give newName, folder, or both (different from the file's own).");
        string finalName = renaming ? newName : existing.Name;

        // The server replaces a file of the name it moves or renames to. The file is moved first and renamed in its new folder, so the
        // names it takes are destination+name (after the move) and destination+newName (after the rename); a file of either name, other
        // than this one, is replaced only with overwrite, after a backup. Names are compared exactly: on a server that tells case apart,
        // Start.nw and start.nw are two files.
        var replaced = new List<FileBrowserEntry>();
        foreach (string taken in moving && renaming ? new[] { existing.Name, finalName } : new[] { finalName })
        {
            FileBrowserEntry? other = await FindFileAsync(c, c.Target, destination, taken).ConfigureAwait(true);
            if (other is null || !moving && other.Name == existing.Name) continue;
            if (!replaced.Any(entry => entry.Name == other.Name)) replaced.Add(other);
        }
        bool overwrite = c.Args.Bool("overwrite", false);
        bool force = c.Args.Bool("force", false);
        if (replaced.Count > 0 && !overwrite)
            throw new McpToolException(string.Join(" and ", replaced.Select(entry => destination + entry.Name)) + (replaced.Count > 1 ? " exist" : " exists") + " on " + c.Server + "; give overwrite: true to replace it (it is backed up first).");
        FileBrowserEntry? large = replaced.FirstOrDefault(entry => entry.Size > MaximumBackupBytes);
        if (large is not null && !force)
            throw new McpToolException(destination + large.Name + " has " + large.Size + " bytes, more than a backup keeps; give force: true to replace it without one.");
        await c.ApproveAsync("Move " + folder + existing.Name + " to " + destination + finalName + " on " + c.Server
            + string.Concat(replaced.Select(entry => ", replacing " + destination + entry.Name + " (" + entry.Size + " bytes" + (entry.Size > MaximumBackupBytes ? ", without a backup" : string.Empty) + ")"))).ConfigureAwait(true);
        McpBackupEntry? backup = null;
        foreach (FileBrowserEntry entry in replaced.Where(entry => entry.Size <= MaximumBackupBytes))
        {
            byte[]? old = entry.Size == 0 ? Array.Empty<byte>() : await c.ReadForBackupAsync(() => DownloadAsync(c, c.Target, destination + entry.Name), destination + entry.Name).ConfigureAwait(true);
            if (old is not null) backup = await c.BackupAsync(c.Server, "file", destination + entry.Name, 0, string.Empty, old, "rc_move_file").ConfigureAwait(true) ?? backup;
        }
        long since = c.Target.Journal.Sequence;
        IRcFileBrowser browser = c.Session.McpFileBrowser;
        if (moving) McpToolContext.Sent(await browser.MoveAsync(folder + existing.Name, destination).ConfigureAwait(true), c.Session, "The move");
        if (renaming) McpToolContext.Sent(await browser.RenameAsync(destination + existing.Name, newName).ConfigureAwait(true), c.Session, "The rename");
        return Written(c, await c.CollectAsync(c.Target, since, 1000, McpSessionJournal.Files, McpSessionJournal.Error).ConfigureAwait(true), backup, folder + existing.Name + " was sent to become " + destination + finalName + ".");
    }

    /// <summary>
    /// Encodes text as the file's encoding, but never loses a character: text Windows-1252 cannot hold goes as UTF-8 (with a note), or,
    /// when the caller chose Windows-1252, is refused.
    /// </summary>
    private static (byte[] Content, string Encoding) EncodeKeeping(string text, string encoding, bool chosen, List<string> notes)
    {
        byte[] content = McpFileText.Encode(text, encoding);
        if (encoding != "windows-1252" || Protocol.Wire.RcText.GetString(content) == text) return (content, encoding);
        if (chosen) throw new McpToolException("The text has characters that Windows-1252 cannot hold; write it as utf8.");
        notes.Add("The file was Windows-1252, but the new text has characters it cannot hold, so it was written as UTF-8.");
        return (McpFileText.Encode(text, "utf8"), "utf8");
    }

    internal static void WriteUpload(Utf8JsonWriter writer, UploadAnswer answer)
    {
        writer.WriteBoolean("confirmed", answer.Answered);
        if (answer.Message.Length > 0) McpJson.Text(writer, "answer", answer.Message);
    }
}
