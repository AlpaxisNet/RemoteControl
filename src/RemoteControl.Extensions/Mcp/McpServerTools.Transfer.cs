using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Extensions;

internal static partial class McpServerTools
{
    /// <summary>The index of an exported folder (<c>rc_export</c>, <c>rc_import</c>).</summary>
    internal const string IndexFileName = "rc-export.json";

    private sealed record ScriptItem(ScriptKind Kind, string Name, int Id, string NewName, int ToId);

    private sealed record ItemResult(string Item, string Outcome, string Message, string Backup = "");

    // ---- Copy between servers ----------------------------------------------------------------------------------------------------

    public static async Task<string> CopyAsync(McpToolContext c)
    {
        McpSessionTarget to = c.Target;
        McpSessionTarget from = await c.FindSessionAsync(c.Args.RequiredString("from")).ConfigureAwait(true);
        bool overwrite = c.Args.Bool("overwrite", true);
        List<ScriptItem> scripts = ScriptItems(c.Args);
        IReadOnlyList<string> filePatterns = c.Args.Strings("files");
        if (scripts.Count == 0 && filePatterns.Count == 0) throw new McpToolException("Give scripts (or weapons/classes) and/or files to copy.");
        if (ReferenceEquals(from.Session, to.Session) && scripts.Any(item => item.NewName.Length == 0 && item.ToId == 0) && filePatterns.Count == 0 && !c.Args.Has("folder"))
            throw new McpToolException("The source and the destination are the same session: give newName (or toId) for the scripts, or a folder for the files.");
        c.RequireRc(from);
        c.RequireRc(to);
        if (scripts.Count > 0)
        {
            c.RequireNc(from);
            c.RequireNc(to);
        }

        // Resolve what to copy before asking.
        var resolved = new List<(ScriptItem Item, string Name, int Id)>();
        var results = new List<ItemResult>();
        foreach (ScriptItem item in scripts)
        {
            try
            {
                (string name, int id) = await ResolveScriptAsync(c, from, item.Kind, item.Name, item.Id).ConfigureAwait(true);
                resolved.Add((item, name, id));
            }
            catch (McpToolException exception)
            {
                results.Add(new ItemResult(Describe(item.Kind, item.Name, item.Id), "failed", exception.Message));
            }
        }
        string? destinationFolder = c.Args.TryString("folder", out string folderText) && folderText.Trim().Length > 0 ? FolderOf(folderText) : null;
        List<string> files = await ExpandFilesAsync(c, from, filePatterns, results).ConfigureAwait(true);
        if (resolved.Count == 0 && files.Count == 0) return CopyResult(c, results, Array.Empty<McpJournalEntry>(), "Nothing could be copied.");

        string summary = string.Join(", ", resolved.Select(item => Describe(item.Item.Kind, item.Name, item.Id)).Concat(files).Take(20)) + (resolved.Count + files.Count > 20 ? ", …" : string.Empty);
        await c.ApproveAsync("Copy " + resolved.Count + " script(s) and " + files.Count + " file(s) from " + from.ServerName + " to " + to.ServerName + (overwrite ? string.Empty : " (keeping what exists)") + ": " + summary).ConfigureAwait(true);
        long since = to.Journal.Sequence;

        foreach ((ScriptItem item, string name, int id) in resolved)
        {
            string label = Describe(item.Kind, name, id);
            try
            {
                ScriptReceivedEventArgs source = await FetchScriptAsync(c, from, item.Kind, name, id).ConfigureAwait(true);
                if (item.Kind == ScriptKind.Class && Normalize(source.Script).Trim().Length == 0)
                {
                    results.Add(new ItemResult(label, "skipped", "The class is empty on " + from.ServerName + ", and an empty class would delete the one on " + to.ServerName + "."));
                    continue;
                }
                string targetName = item.NewName.Length > 0 ? item.NewName : name;
                int targetId = 0;
                if (item.Kind == ScriptKind.Npc)
                {
                    RcNpc? npc = item.ToId > 0 ? to.Session.Npcs.FirstOrDefault(entry => entry.Id == item.ToId)
                        : to.Session.Npcs.FirstOrDefault(entry => string.Equals(entry.Name, targetName, StringComparison.OrdinalIgnoreCase));
                    if (npc is null)
                    {
                        results.Add(new ItemResult(label, "failed", "No NPC " + (item.ToId > 0 ? "with the id " + item.ToId : "named " + targetName) + " on " + to.ServerName + "; create it with rc_npc_action first (or give toId)."));
                        continue;
                    }
                    (targetName, targetId) = (npc.Name, npc.Id);
                }
                bool exists = await ExistsAsync(c, to, item.Kind, targetName, targetId).ConfigureAwait(true);
                if (exists && !overwrite)
                {
                    results.Add(new ItemResult(label, "skipped", "It exists on " + to.ServerName + "."));
                    continue;
                }
                if (exists && item.Kind != ScriptKind.Npc) (targetName, _) = ResolveScript(to, item.Kind, targetName, 0);
                ScriptReceivedEventArgs? current = exists ? await c.ReadForBackupAsync(() => FetchScriptAsync(c, to, item.Kind, targetName, targetId), "script of " + Describe(item.Kind, targetName, targetId) + " on " + to.ServerName).ConfigureAwait(true) : null;
                (bool created, McpBackupEntry? backup) = await SendScriptAsync(c, to, item.Kind, targetName, targetId, Normalize(source.Script), source.Image, current, "rc_copy").ConfigureAwait(true);
                results.Add(new ItemResult(label, created ? "created" : "replaced", "as " + Describe(item.Kind, targetName, targetId), backup?.Id ?? string.Empty));
            }
            catch (McpToolException exception)
            {
                results.Add(new ItemResult(label, "failed", exception.Message));
            }
        }

        var listings = new Dictionary<string, IReadOnlyList<FileBrowserEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in files)
        {
            try
            {
                (string folder, string name) = SplitServerPath(path);
                string targetFolder = destinationFolder ?? folder;
                byte[] content = await DownloadAsync(c, from, path).ConfigureAwait(true);
                // Without the destination's listing it is not known what the copy would replace: the file fails, nothing is cached.
                if (!listings.TryGetValue(targetFolder, out IReadOnlyList<FileBrowserEntry>? listing))
                {
                    await FoldersAsync(c, to, refresh: false).ConfigureAwait(true);
                    listing = await ListFolderAsync(c, to, targetFolder).ConfigureAwait(true);
                    listings[targetFolder] = listing;
                }
                FileBrowserEntry? existing = listing.FirstOrDefault(entry => !entry.IsDirectory && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null && !overwrite)
                {
                    results.Add(new ItemResult(path, "skipped", "It exists on " + to.ServerName + "."));
                    continue;
                }
                byte[]? previous = existing is null ? null : await c.ReadForBackupAsync(() => DownloadAsync(c, to, targetFolder + existing.Name), targetFolder + existing.Name + " on " + to.ServerName).ConfigureAwait(true);
                if (previous is not null && previous.AsSpan().SequenceEqual(content))
                {
                    results.Add(new ItemResult(path, "unchanged", "The destination has the same bytes."));
                    continue;
                }
                (UploadAnswer answer, bool replaced, McpBackupEntry? backup) = await PutFileAsync(c, to, targetFolder, existing?.Name ?? name, content, previous, lookedUp: true, "rc_copy").ConfigureAwait(true);
                // The listing kept here no longer holds this file's size; the next file of the folder asks again.
                listings.Remove(targetFolder);
                results.Add(answer.Answered && !answer.Accepted
                    ? new ItemResult(path, "failed", answer.Message)
                    : new ItemResult(path, replaced || existing is not null ? "replaced" : "created", (answer.Answered ? string.Empty : "not confirmed by the server; ") + (existing is not null && previous is null ? "the old file could not be read and has no backup; " : string.Empty) + "as " + targetFolder + (existing?.Name ?? name), backup?.Id ?? string.Empty));
            }
            catch (McpToolException exception)
            {
                results.Add(new ItemResult(path, "failed", exception.Message));
            }
        }
        IReadOnlyList<McpJournalEntry> events = await c.CollectAsync(to, since, 1500, AnswerKinds).ConfigureAwait(true);
        return CopyResult(c, results, events, "Copied from " + from.ServerName + " to " + to.ServerName + ".");
    }

    private static string CopyResult(McpToolContext c, List<ItemResult> results, IReadOnlyList<McpJournalEntry> events, string message) => JsonText.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteBoolean("ok", results.All(result => result.Outcome != "failed"));
        McpJson.Text(writer, "message", message);
        WriteResults(writer, results);
        McpJson.Entries(writer, "events", events);
        writer.WriteEndObject();
    });

    private static void WriteResults(Utf8JsonWriter writer, IReadOnlyList<ItemResult> results)
    {
        writer.WriteStartObject("counts");
        foreach (IGrouping<string, ItemResult> group in results.GroupBy(result => result.Outcome)) writer.WriteNumber(group.Key, group.Count());
        writer.WriteEndObject();
        writer.WriteStartArray("items");
        foreach (ItemResult result in results)
        {
            writer.WriteStartObject();
            McpJson.Text(writer, "item", result.Item);
            writer.WriteString("outcome", result.Outcome);
            if (result.Message.Length > 0) McpJson.Text(writer, "message", result.Message);
            if (result.Backup.Length > 0) McpJson.Text(writer, "backup", result.Backup);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    /// <summary>The scripts of a call: <c>scripts</c> (objects with kind, name or id, newName, toId) and the shorthands <c>weapons</c> and <c>classes</c> (names).</summary>
    private static List<ScriptItem> ScriptItems(McpArguments args)
    {
        var items = new List<ScriptItem>();
        foreach (McpArguments item in args.Objects("scripts"))
            items.Add(new ScriptItem(ParseKind(item.RequiredString("kind")), item.String("name"), item.Int("id", 0), item.String("newName"), item.Int("toId", 0)));
        items.AddRange(args.Strings("weapons").Where(name => name.Length > 0).Select(name => new ScriptItem(ScriptKind.Weapon, name, 0, string.Empty, 0)));
        items.AddRange(args.Strings("classes").Where(name => name.Length > 0).Select(name => new ScriptItem(ScriptKind.Class, name, 0, string.Empty, 0)));
        return items;
    }

    /// <summary>The file paths of patterns: <c>folder/name</c> as it is, <c>folder/*.nw</c> (a wildcard in the name) as the matching files of the folder.</summary>
    private static async Task<List<string>> ExpandFilesAsync(McpToolContext c, McpSessionTarget source, IReadOnlyList<string> patterns, List<ItemResult> results)
    {
        var files = new List<string>();
        foreach (string pattern in patterns.Where(pattern => pattern.Trim().Length > 0))
        {
            try
            {
                string trimmed = pattern.Trim().Replace('\\', '/').TrimStart('/');
                bool wholeFolder = trimmed.EndsWith('/');
                (string folder, string name) = wholeFolder ? (FolderOf(trimmed), "*") : SplitServerPath(trimmed);
                if (name.IndexOfAny(new[] { '*', '?' }) < 0)
                {
                    files.Add(folder + name);
                    continue;
                }
                await FoldersAsync(c, source, refresh: false).ConfigureAwait(true);
                Func<string, bool> filter = NameFilter(name);
                int before = files.Count;
                files.AddRange((await ListFolderAsync(c, source, folder).ConfigureAwait(true)).Where(entry => !entry.IsDirectory && filter(entry.Name)).Select(entry => folder + entry.Name));
                if (files.Count == before) results.Add(new ItemResult(pattern, "skipped", "No file of " + source.ServerName + " matches."));
            }
            catch (McpToolException exception)
            {
                results.Add(new ItemResult(pattern, "failed", exception.Message));
            }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- Local folders -------------------------------------------------------------------------------------------------------------

    private sealed class ExportIndex
    {
        public string Server { get; set; } = string.Empty;

        public List<IndexEntry> Entries { get; } = new();
    }

    /// <summary>
    /// An entry of the index; <paramref name="Origin"/> is the server it was exported from (NPC ids are per server). <paramref name="Found"/>
    /// marks a file the index does not name (its name is only what the file is called); it is not written to the index.
    /// </summary>
    private sealed record IndexEntry(string Kind, string Name, int Id, string Image, string Path, string File, string Sha256, string Origin = "", bool Found = false);

    public static async Task<string> ExportAsync(McpToolContext c)
    {
        McpLocalFiles local = c.Services.LocalFiles(c.Options);
        string folder = local.Resolve(c.Args.TryString("localFolder", out string given) && given.Trim().Length > 0 ? given : FriendlyFileName(c.Server));
        IReadOnlyList<string> filePatterns = c.Args.Strings("files");
        bool scripts = c.Args.Bool("scripts", filePatterns.Count == 0);
        var results = new List<ItemResult>();
        var targets = new List<(ScriptKind Kind, string Name, int Id)>();
        if (scripts)
        {
            c.RequireNc();
            IReadOnlyList<string> kindNames = c.Args.Strings("kinds");
            HashSet<ScriptKind> kinds = kindNames.Count == 0 ? new HashSet<ScriptKind> { ScriptKind.Weapon, ScriptKind.Class, ScriptKind.Npc } : kindNames.Select(ParseKind).ToHashSet();
            if (kinds.Contains(ScriptKind.Weapon))
            {
                // The session's weapon list is the one of the NC login; weapons added since would be left out.
                try { await RefreshWeaponsAsync(c, c.Target).ConfigureAwait(true); }
                catch (McpToolException exception) { results.Add(new ItemResult("weapon list", "failed", exception.Message + " The weapons of the last list were exported.")); }
            }
            Func<string, bool> names = NameFilter(c.Args.String("names"));
            if (kinds.Contains(ScriptKind.Weapon)) targets.AddRange(c.Session.Weapons.Where(item => names(item.Name)).Select(item => (ScriptKind.Weapon, item.Name, 0)));
            if (kinds.Contains(ScriptKind.Class)) targets.AddRange(c.Session.Classes.Where(item => names(item.Name)).Select(item => (ScriptKind.Class, item.Name, 0)));
            if (kinds.Contains(ScriptKind.Npc)) targets.AddRange(c.Session.Npcs.Where(item => names(item.Name)).Select(item => (ScriptKind.Npc, item.Name, item.Id)));
        }
        else
        {
            c.RequireRc();
        }
        List<string> files = await ExpandFilesAsync(c, c.Target, filePatterns, results).ConfigureAwait(true);
        await c.ApproveAsync("Write " + targets.Count + " script(s) and " + files.Count + " file(s) of " + c.Server + " into the local folder " + folder).ConfigureAwait(true);

        ExportIndex index = await ReadIndexAsync(local, folder).ConfigureAwait(true);
        if (index.Server.Length > 0 && !string.Equals(index.Server, c.Server, StringComparison.OrdinalIgnoreCase))
        {
            // What the folder holds from another server is new to this one: rc_import into this server sends it (an NPC by its name, as
            // the ids of two servers have nothing to do with each other).
            for (int position = 0; position < index.Entries.Count; position++)
            {
                IndexEntry old = index.Entries[position];
                index.Entries[position] = old with { Sha256 = string.Empty, Origin = old.Origin.Length > 0 ? old.Origin : index.Server };
            }
        }
        index.Server = c.Server;
        Dictionary<(ScriptKind, string, int), Task<string>> fetched = await FetchManyAsync(c, c.Target, targets, cached: false).ConfigureAwait(true);
        foreach ((ScriptKind kind, string name, int id) in targets)
        {
            Task<string> task = fetched[(kind, name, id)];
            string label = Describe(kind, name, id);
            if (!task.IsCompletedSuccessfully)
            {
                results.Add(new ItemResult(label, "failed", task.Exception?.InnerException?.Message ?? "cancelled"));
                continue;
            }
            string relative = ScriptFolder(kind) + "/" + (kind == ScriptKind.Npc ? id + "_" : string.Empty) + FriendlyFileName(name.Length > 0 ? name : id.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ".gs2";
            byte[] bytes = Encoding.UTF8.GetBytes(task.Result);
            string image = kind == ScriptKind.Weapon ? c.Session.Weapons.FirstOrDefault(item => item.Name == name)?.Image ?? string.Empty : string.Empty;
            try
            {
                await WriteLocalAsync(local, folder, relative, bytes).ConfigureAwait(true);
                // An entry of the same script under another file (an NPC whose id now has another name) goes, with its file:
                // rc_import would otherwise send the old file as a new script of that id.
                foreach (IndexEntry old in index.Entries.Where(entry => SameScript(entry, kind, name, id, c.Server) && !string.Equals(entry.File, relative, McpLocalFiles.PathComparison)).ToArray())
                    await DeleteLocalAsync(local, folder, old.File).ConfigureAwait(true);
                index.Entries.RemoveAll(entry => SameScript(entry, kind, name, id, c.Server) || string.Equals(entry.File, relative, McpLocalFiles.PathComparison));
                index.Entries.Add(new IndexEntry(KindName(kind), name, id, image, string.Empty, relative, Sha(bytes), c.Server));
                results.Add(new ItemResult(label, "exported", relative));
            }
            catch (McpToolException exception)
            {
                results.Add(new ItemResult(label, "failed", exception.Message));
            }
        }
        foreach (string path in files)
        {
            try
            {
                byte[] bytes = await DownloadAsync(c, c.Target, path).ConfigureAwait(true);
                string relative = "files/" + path;
                await WriteLocalAsync(local, folder, relative, bytes).ConfigureAwait(true);
                index.Entries.RemoveAll(entry => entry.Kind == "file" && string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
                index.Entries.Add(new IndexEntry("file", string.Empty, 0, string.Empty, path, relative, Sha(bytes), c.Server));
                results.Add(new ItemResult(path, "exported", relative));
            }
            catch (McpToolException exception)
            {
                results.Add(new ItemResult(path, "failed", exception.Message));
            }
        }
        await WriteIndexAsync(local, folder, index).ConfigureAwait(true);
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", results.All(result => result.Outcome != "failed"));
            McpJson.Text(writer, "folder", folder);
            McpJson.Text(writer, "index", Path.Combine(folder, IndexFileName));
            writer.WriteString("hint", "Edit the files with your own tools, then rc_import sends back what changed.");
            WriteResults(writer, results);
            writer.WriteEndObject();
        });

        // An NPC id is the same NPC only on the same server: an entry of another one (its origin) is another NPC.
        static bool SameScript(IndexEntry entry, ScriptKind kind, string name, int id, string server) =>
            entry.Kind == KindName(kind) && (kind == ScriptKind.Npc
                ? entry.Id == id && (entry.Origin.Length == 0 || string.Equals(entry.Origin, server, StringComparison.OrdinalIgnoreCase))
                : string.Equals(entry.Name, name, StringComparison.Ordinal));
    }

    public static async Task<string> ImportAsync(McpToolContext c)
    {
        McpLocalFiles local = c.Services.LocalFiles(c.Options);
        string folder = local.Resolve(c.Args.RequiredString("localFolder"));
        if (!Directory.Exists(folder)) throw new McpToolException("The local folder " + folder + " does not exist.");
        ExportIndex index = await ReadIndexAsync(local, folder).ConfigureAwait(true);
        bool sameServer = string.Equals(index.Server, c.Server, StringComparison.OrdinalIgnoreCase);
        bool changedOnly = c.Args.Bool("changedOnly", sameServer);
        bool dryRun = c.Args.Bool("dryRun", false);
        IReadOnlyList<string> kindNames = c.Args.Strings("kinds");
        HashSet<string> kinds = kindNames.Count == 0 ? new HashSet<string> { "weapon", "class", "npc", "file" } : kindNames.Select(kind => kind.ToLowerInvariant() is "file" or "files" ? "file" : KindName(ParseKind(kind))).ToHashSet();
        c.RequireRc();

        // What is there: the index's entries and the files of the folders the index does not name. Every file is read through the
        // link check of the local roots: a link inside the folder that leads out of them is refused.
        var candidates = new List<(IndexEntry Entry, byte[] Bytes, string Change)>();
        var results = new List<ItemResult>();
        var known = new HashSet<string>(index.Entries.Select(entry => entry.File), McpLocalFiles.PathComparer);
        var entries = new List<IndexEntry>(index.Entries);
        try
        {
            // An unindexed NPC file of an id the index names is the NPC's script renamed when the indexed file of that id is gone (of the
            // server that entry came from; an empty origin: more than one), and an old copy while it is there.
            var present = index.Entries.Where(entry => entry.Kind == "npc").ToLookup(entry => IndexedFileExists(local, folder, entry.File));
            var npcIds = new HashSet<int>(present[true].Select(entry => entry.Id));
            Dictionary<int, string> goneOrigins = present[false]
                .GroupBy(entry => entry.Id)
                .ToDictionary(group => group.Key, group =>
                {
                    string[] origins = group.Select(entry => entry.Origin.Length > 0 ? entry.Origin : index.Server).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    return origins.Length == 1 ? origins[0] : string.Empty;
                });
            (List<IndexEntry> found, List<ItemResult> skipped) = await Task.Run(() => ScanNewFiles(folder, known, npcIds, goneOrigins)).ConfigureAwait(true);
            entries.AddRange(found);
            if (kinds.Contains("npc")) results.AddRange(skipped);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new McpToolException("The local folder " + folder + " could not be read: " + exception.Message);
        }
        foreach (IndexEntry entry in entries)
        {
            if (!kinds.Contains(entry.Kind)) continue;
            byte[] bytes;
            try
            {
                string full = local.ResolveUnder(folder, entry.File);
                if (!File.Exists(full))
                {
                    results.Add(new ItemResult(entry.File, "skipped", "The local file is gone; nothing is deleted on the server."));
                    continue;
                }
                bytes = await local.ReadAsync(full, c.Token).ConfigureAwait(true);
            }
            catch (McpToolException exception)
            {
                results.Add(new ItemResult(entry.File, "failed", exception.Message));
                continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                results.Add(new ItemResult(entry.File, "failed", "The local file could not be read: " + exception.Message));
                continue;
            }
            string change = entry.Sha256.Length == 0 ? "new" : Sha(bytes) == entry.Sha256 ? "unchanged" : "changed";
            if (changedOnly && change == "unchanged") continue;
            candidates.Add((entry, bytes, change));
        }
        bool scripts = candidates.Any(item => item.Entry.Kind != "file");
        if (scripts) c.RequireNc();
        if (dryRun || candidates.Count == 0)
        {
            foreach ((IndexEntry entry, byte[] bytes, string change) in candidates) results.Add(new ItemResult(Label(entry), "would-send", change + ", " + bytes.Length + " bytes"));
            return ImportResult(folder, results, Array.Empty<McpJournalEntry>(), candidates.Count == 0 ? "Nothing to send" + (changedOnly ? " (no file changed since the export)." : ".") : "Dry run: nothing was sent.");
        }
        await c.ApproveAsync("Send " + candidates.Count + " item(s) from the local folder " + folder + " to " + c.Server + ":\n" + string.Join("\n", candidates.Select(item => Label(item.Entry) + " (" + item.Change + ")"))).ConfigureAwait(true);
        long since = c.Target.Journal.Sequence;
        var listings = new Dictionary<string, IReadOnlyList<FileBrowserEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach ((IndexEntry entry, byte[] bytes, string change) in candidates)
        {
            string label = Label(entry);
            try
            {
                if (entry.Kind == "file")
                {
                    (string serverFolder, string name) = SplitServerPath(entry.Path);
                    // Without the folder's listing it is not known what the upload would replace: the file fails, nothing is cached.
                    if (!listings.TryGetValue(serverFolder, out IReadOnlyList<FileBrowserEntry>? listing))
                    {
                        await FoldersAsync(c, c.Target, refresh: false).ConfigureAwait(true);
                        listing = await ListFolderAsync(c, c.Target, serverFolder).ConfigureAwait(true);
                        listings[serverFolder] = listing;
                    }
                    FileBrowserEntry? existing = listing.FirstOrDefault(item => !item.IsDirectory && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                    byte[]? previous = existing is null ? null : await c.ReadForBackupAsync(() => DownloadAsync(c, c.Target, serverFolder + existing.Name), serverFolder + existing.Name).ConfigureAwait(true);
                    if (previous is not null && previous.AsSpan().SequenceEqual(bytes))
                    {
                        results.Add(new ItemResult(label, "unchanged", "The server has the same bytes."));
                    }
                    else
                    {
                        (UploadAnswer answer, bool replaced, McpBackupEntry? backup) = await PutFileAsync(c, c.Target, serverFolder, existing?.Name ?? name, bytes, previous, lookedUp: true, "rc_import").ConfigureAwait(true);
                        listings.Remove(serverFolder);
                        if (answer.Answered && !answer.Accepted)
                        {
                            results.Add(new ItemResult(label, "failed", answer.Message));
                            continue;
                        }
                        results.Add(new ItemResult(label, replaced || existing is not null ? "replaced" : "created",
                            string.Join("; ", new[] { answer.Answered ? string.Empty : "not confirmed by the server", existing is not null && previous is null ? "the old file could not be read and has no backup" : string.Empty }.Where(part => part.Length > 0)),
                            backup?.Id ?? string.Empty));
                    }
                }
                else
                {
                    ScriptKind kind = ParseKind(entry.Kind);
                    string script = Normalize(Encoding.UTF8.GetString(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsSpan(3) : bytes));
                    if (kind == ScriptKind.Class && script.Trim().Length == 0)
                    {
                        results.Add(new ItemResult(label, "skipped", "An empty class would be deleted by the server; use rc_delete_script."));
                        continue;
                    }
                    string name = entry.Name;
                    int id = entry.Id;
                    if (kind == ScriptKind.Npc)
                    {
                        // NPC ids belong to their server: an NPC exported from another one is found by its name only, never by an id that
                        // may be another NPC here; one of this server must still have the name it had (an id can be given to a new NPC).
                        string origin = entry.Origin.Length > 0 ? entry.Origin : index.Server;
                        bool foreign = origin.Length > 0 && !string.Equals(origin, c.Server, StringComparison.OrdinalIgnoreCase);
                        RcNpc? byId = foreign ? null : c.Session.Npcs.FirstOrDefault(npc => npc.Id == id);
                        // (The name of an exported entry is the NPC's; that of a file found in the folder is only what its file is called.)
                        if (byId is not null && !entry.Found && entry.Origin.Length > 0 && name.Length > 0 && byId.Name.Length > 0 && !string.Equals(byId.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(new ItemResult(label, "failed", "NPC " + id + " is named " + byId.Name + " on " + c.Server + " now, not " + name + "; nothing was sent."));
                            continue;
                        }
                        RcNpc? found = byId ?? (name.Length > 0 ? c.Session.Npcs.FirstOrDefault(npc => string.Equals(npc.Name, name, StringComparison.OrdinalIgnoreCase)) : null);
                        if (found is null)
                        {
                            results.Add(new ItemResult(label, "failed", (foreign ? "No NPC named " + name : "No NPC with the id " + id + (name.Length > 0 ? " or the name " + name : string.Empty)) + " on " + c.Server + "; create it with rc_npc_action first."));
                            continue;
                        }
                        id = found.Id;
                    }
                    bool exists = await ExistsAsync(c, c.Target, kind, name, id).ConfigureAwait(true);
                    if (exists && kind != ScriptKind.Npc) (name, _) = ResolveScript(c.Target, kind, name, 0);
                    ScriptReceivedEventArgs? current = exists ? await c.ReadForBackupAsync(() => FetchScriptAsync(c, c.Target, kind, name, id), "script of " + Describe(kind, name, id)).ConfigureAwait(true) : null;
                    if (current is not null && Normalize(current.Script) == script)
                    {
                        results.Add(new ItemResult(label, "unchanged", "The server has the same text."));
                    }
                    else
                    {
                        // A .gs2 file carries no image: a weapon that exists keeps the server's (changes made there since the export stay);
                        // the exported image goes to a new weapon, or to another server.
                        string image = current is not null && (sameServer || entry.Image.Length == 0) ? current.Image : entry.Image;
                        (bool created, McpBackupEntry? backup) = await SendScriptAsync(c, c.Target, kind, name, id, script, image, current, "rc_import").ConfigureAwait(true);
                        results.Add(new ItemResult(label, created ? "created" : "replaced", string.Empty, backup?.Id ?? string.Empty));
                    }
                }
                index.Entries.RemoveAll(item => string.Equals(item.File, entry.File, McpLocalFiles.PathComparison));
                index.Entries.Add(entry with { Sha256 = Sha(bytes) });
            }
            catch (McpToolException exception)
            {
                results.Add(new ItemResult(label, "failed", exception.Message));
            }
        }
        if (sameServer || index.Server.Length == 0)
        {
            index.Server = c.Server;
            await WriteIndexAsync(local, folder, index).ConfigureAwait(true);
        }
        IReadOnlyList<McpJournalEntry> events = await c.CollectAsync(c.Target, since, 1500, AnswerKinds).ConfigureAwait(true);
        return ImportResult(folder, results, events, "Sent to " + c.Server + ".");

        static string Label(IndexEntry entry) => entry.Kind == "file" ? entry.Path : entry.Kind == "npc" ? "npc " + entry.Id + (entry.Name.Length > 0 ? " (" + entry.Name + ")" : string.Empty) : entry.Kind + " " + entry.Name;
    }

    private static string ImportResult(string folder, List<ItemResult> results, IReadOnlyList<McpJournalEntry> events, string message) => JsonText.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteBoolean("ok", results.All(result => result.Outcome != "failed"));
        McpJson.Text(writer, "message", message);
        McpJson.Text(writer, "folder", folder);
        WriteResults(writer, results);
        McpJson.Entries(writer, "events", events);
        writer.WriteEndObject();
    });

    private static string ScriptFolder(ScriptKind kind) => kind switch { ScriptKind.Weapon => "weapons", ScriptKind.Class => "classes", _ => "npcs" };

    private static bool IndexedFileExists(McpLocalFiles local, string folder, string relative)
    {
        try
        {
            return File.Exists(local.ResolveUnder(folder, relative));
        }
        catch (McpToolException)
        {
            return false;
        }
    }

    /// <summary>How the export folder is walked: unreadable folders are skipped, and links are not followed (a link is not "in" the folder).</summary>
    private static readonly EnumerationOptions LocalWalk = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>
    /// The files under <c>weapons/</c>, <c>classes/</c>, <c>npcs/</c> and <c>files/</c> that the index does not name: new scripts and
    /// files. An NPC file of an id whose indexed file is gone is the renamed script of that NPC, of the server in
    /// <paramref name="goneOrigins"/>; one whose id the index names under a file that is there is an old copy (the NPC was renamed).
    /// Those old copies, NPC files without the <c>id_</c> of their name, and NPC files of an id that two servers' gone entries name are
    /// skipped (the second list).
    /// </summary>
    private static (List<IndexEntry> Found, List<ItemResult> Skipped) ScanNewFiles(string folder, HashSet<string> known, HashSet<int> indexedNpcIds, IReadOnlyDictionary<int, string> goneOrigins)
    {
        var entries = new List<IndexEntry>();
        var skipped = new List<ItemResult>();
        foreach (ScriptKind kind in new[] { ScriptKind.Weapon, ScriptKind.Class, ScriptKind.Npc })
        {
            string directory = Path.Combine(folder, ScriptFolder(kind));
            if (!Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null) continue;
            foreach (string file in Directory.EnumerateFiles(directory, "*.gs2", LocalWalk))
            {
                string relative = ScriptFolder(kind) + "/" + Path.GetFileName(file);
                if (known.Contains(relative)) continue;
                string stem = UnescapeFileName(Path.GetFileNameWithoutExtension(file));
                int id = 0;
                string origin = string.Empty;
                if (kind == ScriptKind.Npc)
                {
                    int underscore = stem.IndexOf('_');
                    if (underscore <= 0 || !int.TryParse(stem[..underscore], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id) || id <= 0)
                    {
                        skipped.Add(new ItemResult(relative, "skipped", "An NPC file is named <id>_<name>.gs2 (as rc_export writes it); this one has no id, so it is not known which NPC it is."));
                        continue;
                    }
                    if (goneOrigins.TryGetValue(id, out string? gone))
                    {
                        if (gone.Length == 0)
                        {
                            skipped.Add(new ItemResult(relative, "skipped", "The index names NPC " + id + " of more than one server, and their files are gone; it is not known which this one is."));
                            continue;
                        }
                        origin = gone;
                    }
                    else if (indexedNpcIds.Contains(id))
                    {
                        skipped.Add(new ItemResult(relative, "skipped", "The index names NPC " + id + " under another file; this one is an old copy (the NPC was renamed)."));
                        continue;
                    }
                    stem = stem[(underscore + 1)..];
                }
                entries.Add(new IndexEntry(KindName(kind), stem, id, string.Empty, string.Empty, relative, string.Empty, origin, Found: true));
            }
        }
        string files = Path.Combine(folder, "files");
        if (Directory.Exists(files) && new DirectoryInfo(files).LinkTarget is null)
        {
            var walk = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = true };
            foreach (string file in Directory.EnumerateFiles(files, "*", walk))
            {
                string path = Path.GetRelativePath(files, file).Replace('\\', '/');
                string relative = "files/" + path;
                if (known.Contains(relative) || !path.Contains('/')) continue;
                entries.Add(new IndexEntry("file", string.Empty, 0, string.Empty, path, relative, string.Empty, Found: true));
            }
        }
        return (entries, skipped);
    }

    /// <summary>The index of an export folder (an empty one when there is none). The folder is edited with local tools, so anything unexpected in it is a tool error, never a crash.</summary>
    private static async Task<ExportIndex> ReadIndexAsync(McpLocalFiles local, string folder)
    {
        var index = new ExportIndex();
        string path = local.ResolveUnder(folder, IndexFileName);
        if (!File.Exists(path)) return index;
        try
        {
            byte[] bytes = await local.ReadAsync(path, CancellationToken.None).ConfigureAwait(true);
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            using JsonDocument document = JsonDocument.Parse(await reader.ReadToEndAsync().ConfigureAwait(true));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !McpArguments.IsWellFormed(root)) throw new McpToolException(path + " is not a valid export index: it is no JSON object of valid text.");
            index.Server = Text(root, "server");
            if (root.TryGetProperty("entries", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    int id = item.TryGetProperty("id", out JsonElement idValue) && idValue.ValueKind == JsonValueKind.Number && idValue.TryGetInt32(out int number) ? number : 0;
                    var entry = new IndexEntry(Text(item, "kind"), Text(item, "name"), id, Text(item, "image"), Text(item, "path"), Text(item, "file"), Text(item, "sha256"), Text(item, "origin"));
                    if (entry.Kind is "weapon" or "class" or "npc" or "file" && entry.File.Length > 0) index.Entries.Add(entry);
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new McpToolException(path + " is not a valid export index: " + exception.Message);
        }
        return index;

        static string Text(JsonElement element, string key) =>
            element.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? McpArguments.Text(value) ?? string.Empty : string.Empty;
    }

    private static async Task WriteIndexAsync(McpLocalFiles local, string folder, ExportIndex index)
    {
        string json = JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", 1);
            McpJson.Text(writer, "server", index.Server);
            writer.WriteString("written", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteStartArray("entries");
            foreach (IndexEntry entry in index.Entries.OrderBy(entry => entry.File, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", entry.Kind);
                if (entry.Name.Length > 0) McpJson.Text(writer, "name", entry.Name);
                if (entry.Id != 0) writer.WriteNumber("id", entry.Id);
                if (entry.Image.Length > 0) McpJson.Text(writer, "image", entry.Image);
                if (entry.Path.Length > 0) McpJson.Text(writer, "path", entry.Path);
                McpJson.Text(writer, "file", entry.File);
                writer.WriteString("sha256", entry.Sha256);
                if (entry.Origin.Length > 0) McpJson.Text(writer, "origin", entry.Origin);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        await WriteLocalAsync(local, folder, IndexFileName, Encoding.UTF8.GetBytes(json)).ConfigureAwait(true);
    }

    /// <summary>Writes a file of an export folder, whole, after the link check of the local roots.</summary>
    private static async Task WriteLocalAsync(McpLocalFiles local, string folder, string relative, byte[] bytes)
    {
        string path = local.ResolveUnder(folder, relative);
        try
        {
            await Task.Run(() => AtomicFile.WriteAllBytes(path, bytes)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new McpToolException("The local file " + path + " could not be written: " + exception.Message);
        }
    }

    /// <summary>Removes a file of an export folder that an index entry no longer names (best effort: a file that cannot go stays).</summary>
    private static async Task DeleteLocalAsync(McpLocalFiles local, string folder, string relative)
    {
        string path = local.ResolveUnder(folder, relative);
        try
        {
            await Task.Run(() => File.Delete(path)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // It stays; rc_import leaves it out, as the index names its NPC under another file (see ScanNewFiles).
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A file name for a script or server name: letters, digits, <c>_ - . space</c> kept, every other byte as <c>%XX</c> (UTF-8).</summary>
    internal static string FriendlyFileName(string name)
    {
        var result = new StringBuilder();
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        for (int index = 0; index < bytes.Length; index++)
        {
            byte value = bytes[index];
            bool keep = value is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or (byte)'_' or (byte)'-'
                || value is (byte)'.' or (byte)' ' && index > 0 && index < bytes.Length - 1;
            if (keep) result.Append((char)value);
            else result.Append('%').Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        string text = result.ToString();
        return SafePath.IsSafeSegment(text, out _) ? text : "%" + ((int)text[0]).ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + text[1..];
    }

    internal static string UnescapeFileName(string name)
    {
        var bytes = new List<byte>();
        for (int index = 0; index < name.Length; index++)
        {
            if (name[index] == '%' && index + 2 < name.Length && byte.TryParse(name.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte value))
            {
                bytes.Add(value);
                index += 2;
                continue;
            }
            bytes.AddRange(Encoding.UTF8.GetBytes(name[index].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    // ---- Backups -----------------------------------------------------------------------------------------------------------------

    public static async Task<string> BackupsAsync(McpToolContext c)
    {
        // The server scope covers the backups too: only those of the server it names are listed and read.
        string scope = c.Options.McpServerScope;
        if (c.Args.TryString("id", out string id) && id.Length > 0)
        {
            (McpBackupEntry entry, byte[] content) = await c.Services.Backups.ReadAsync(id).ConfigureAwait(true);
            if (scope.Length > 0 && !string.Equals(entry.Server, scope, StringComparison.OrdinalIgnoreCase)) throw new McpScopeException();
            bool binary = McpFileText.IsBinary(content) || content.Length > MaximumInlineBytes;
            string text = string.Empty;
            string encoding = binary ? "base64" : McpFileText.Decode(content, "auto", out text);
            return JsonText.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("backup");
                McpBackupStore.WriteEntry(writer, entry);
                writer.WriteString("encoding", encoding);
                if (binary) writer.WriteString("base64", content.Length > MaximumInlineBytes ? string.Empty : Convert.ToBase64String(content));
                else McpJson.Text(writer, "text", text);
                writer.WriteEndObject();
            });
        }
        string ofServer = c.Args.String("ofServer");
        if (scope.Length > 0)
        {
            if (ofServer.Length > 0 && !string.Equals(ofServer, scope, StringComparison.OrdinalIgnoreCase)) throw new McpScopeException();
            ofServer = scope;
        }
        IReadOnlyList<McpBackupEntry> entries = (await c.Services.Backups.ListAsync(ofServer, c.Args.String("query"), Math.Clamp(c.Args.Int("limit", 50), 1, 1000)).ConfigureAwait(true))
            .Where(entry => scope.Length == 0 || string.Equals(entry.Server, scope, StringComparison.OrdinalIgnoreCase)).ToArray();
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            McpJson.Text(writer, "folder", c.Services.Backups.Root);
            writer.WriteStartArray("backups");
            foreach (McpBackupEntry entry in entries) McpBackupStore.WriteEntry(writer, entry);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static async Task<string> RestoreBackupAsync(McpToolContext c)
    {
        (McpBackupEntry entry, byte[] content) = await c.Services.Backups.ReadAsync(c.Args.RequiredString("id")).ConfigureAwait(true);
        // The server scope covers the backups: one of another server is neither shown (rc_backups) nor put back anywhere.
        if (c.Options.McpServerScope.Length > 0 && !string.Equals(entry.Server, c.Options.McpServerScope, StringComparison.OrdinalIgnoreCase)) throw new McpScopeException();
        // A restore is the change the backup's kind stands for, so it needs that change's permission: player texts and server settings
        // are administration (rc_write_player_info, rc_write_server_text), NPC flags a game change (rc_npc_action).
        McpToolGroup needed = entry.Kind switch
        {
            "account" or "rights" or "attributes" or "comments" or "profile" or "serveroptions" or "serverflags" or "folderconfig" => McpToolGroup.Admin,
            "npcflags" => McpToolGroup.Server,
            _ => McpToolGroup.Write,
        };
        if (!McpTools.Allowed(needed, c.Options))
            throw new McpToolException("Restoring a " + entry.Kind + " backup needs " + (needed == McpToolGroup.Admin ? "administrative actions (mcpadmin)" : "server and game mutations (mcpserver)") + ".");
        McpSessionTarget target = await c.FindSessionAsync(c.Args.TryString("server", out string server) && server.Length > 0 ? server : entry.Server).ConfigureAwait(true);
        c.RequireRc(target);
        string text = Encoding.UTF8.GetString(content);
        long since = target.Journal.Sequence;
        string what = entry.Kind + " " + (entry.NpcId != 0 ? entry.NpcId + " " : string.Empty) + entry.Name;
        McpBackupEntry? backup = null;
        switch (entry.Kind)
        {
            case "weapon" or "class" or "npc":
            {
                c.RequireNc(target);
                ScriptKind kind = ParseKind(entry.Kind);
                if (kind == ScriptKind.Class && Normalize(text).Trim().Length == 0) throw new McpToolException("The backup holds an empty class, and the server deletes a class whose script is empty; use rc_delete_script.");
                bool exists = await ExistsAsync(c, target, kind, entry.Name, entry.NpcId).ConfigureAwait(true);
                if (kind == ScriptKind.Npc && !exists) throw new McpToolException("NPC " + entry.NpcId + " is not on " + target.ServerName + "; create it first with rc_npc_action.");
                string name = exists && kind != ScriptKind.Npc ? ResolveScript(target, kind, entry.Name, 0).Name : entry.Name;
                await c.ApproveScriptAsync(McpScriptAction.Write, ApprovalType(kind), (kind == ScriptKind.Npc ? entry.NpcId + " " : string.Empty) + name + " on " + target.ServerName + " (restore of " + entry.Id + (exists ? ", replacing the script there" : ", a new " + KindName(kind)) + ")").ConfigureAwait(true);
                // The question may have been open for a while: a script added or deleted since would be written over without its backup.
                if (c.AsksApproval && await ExistsAsync(c, target, kind, name, entry.NpcId).ConfigureAwait(true) != exists)
                    throw new McpToolException(Describe(kind, name, entry.NpcId) + " was " + (exists ? "deleted" : "added") + " on " + target.ServerName + " while the question was open; nothing was restored. Look at it again and repeat the restore.");
                ScriptReceivedEventArgs? current = exists ? await c.ReadForBackupAsync(() => FetchScriptAsync(c, target, kind, name, entry.NpcId), "script of " + Describe(kind, name, entry.NpcId)).ConfigureAwait(true) : null;
                backup = (await SendScriptAsync(c, target, kind, name, entry.NpcId, Normalize(text), entry.Image, current, "rc_restore_backup").ConfigureAwait(true)).Backup;
                break;
            }
            case "file":
            {
                (string folder, string name) = SplitServerPath(entry.Name);
                await c.ApproveAsync("Restore " + entry.Name + " (" + content.Length + " bytes) on " + target.ServerName + " from the backup " + entry.Id).ConfigureAwait(true);
                (UploadAnswer answer, _, McpBackupEntry? saved) = await PutFileAsync(c, target, folder, name, content, null, lookedUp: false, "rc_restore_backup").ConfigureAwait(true);
                if (answer.Answered && !answer.Accepted) throw new McpToolException("The server refused " + entry.Name + ": " + answer.Message);
                backup = saved;
                break;
            }
            case "serveroptions" or "serverflags" or "folderconfig":
            {
                string kind = entry.Kind switch { "serveroptions" => "options", "serverflags" => "flags", _ => "folderconfig" };
                await c.ApproveAsync("Restore the server " + kind + " of " + target.ServerName + " from the backup " + entry.Id).ConfigureAwait(true);
                string? current = await c.ReadForBackupAsync(() => FetchServerTextAsync(c, target, kind), "server " + kind).ConfigureAwait(true);
                if (current is not null) backup = await c.BackupAsync(target.ServerName, entry.Kind, kind, 0, string.Empty, Encoding.UTF8.GetBytes(current), "rc_restore_backup").ConfigureAwait(true);
                await UploadServerTextAsync(target.Session, kind, Normalize(text)).ConfigureAwait(true);
                break;
            }
            case "npcflags":
            {
                c.RequireNc(target);
                if (!target.Session.Npcs.Any(npc => npc.Id == entry.NpcId)) throw new McpToolException("NPC " + entry.NpcId + " is not on " + target.ServerName + ".");
                await c.ApproveAsync("Restore the flags of NPC " + entry.NpcId + " on " + target.ServerName + " from the backup " + entry.Id + ":\n" + McpToolContext.Whole(Normalize(text))).ConfigureAwait(true);
                IRcSession session = target.Session;
                NpcTextEventArgs? current = await c.ReadForBackupAsync(() => c.RequestAsync<NpcTextEventArgs>(session, h => session.NpcFlagsReceived += h, h => session.NpcFlagsReceived -= h, e => e.NpcId == entry.NpcId,
                    () => session.RequestNpcFlagsAsync(entry.NpcId), "the flags of NPC " + entry.NpcId), "flags of NPC " + entry.NpcId).ConfigureAwait(true);
                if (current is not null) backup = await c.BackupAsync(target.ServerName, "npcflags", entry.Name, entry.NpcId, string.Empty, Encoding.UTF8.GetBytes(current.Text), "rc_restore_backup").ConfigureAwait(true);
                McpToolContext.Sent(await target.Session.SetNpcFlagsAsync(entry.NpcId, Normalize(text)).ConfigureAwait(true), target.Session, "The flags");
                break;
            }
            case "account" or "rights" or "attributes" or "comments" or "profile":
            {
                await c.ApproveAsync("Restore the " + entry.Kind + " of " + entry.Name + " on " + target.ServerName + " from the backup " + entry.Id + ":\n" + McpToolContext.Whole(text)).ConfigureAwait(true);
                backup = await BackupPlayerPartAsync(c, target, entry.Name, entry.Kind, "rc_restore_backup").ConfigureAwait(true);
                await WritePlayerPartAsync(c, target, entry.Name, entry.Kind, text, create: false).ConfigureAwait(true);
                break;
            }
            case "npcattributes":
                throw new McpToolException("NPC attributes are kept to make the NPC again: read them (rc_backups with this id) and create it with rc_npc_action, then restore its script and flags.");
            default:
                throw new McpToolException("A backup of kind " + entry.Kind + " cannot be restored.");
        }
        return Written(c, await c.CollectAsync(target, since, 1500, AnswerKinds).ConfigureAwait(true), backup, "Restored " + what + " on " + target.ServerName + " from " + entry.Id + ".");
    }
}
