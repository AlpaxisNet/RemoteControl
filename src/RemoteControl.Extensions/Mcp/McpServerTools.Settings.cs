using System.Text;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Extensions;

internal static partial class McpServerTools
{
    /// <summary>The server texts: <c>options</c>, <c>flags</c> and <c>folderconfig</c>.</summary>
    internal static readonly string[] ServerTextKinds = { "options", "flags", "folderconfig" };

    private static string ParseServerTextKind(string kind) => (kind ?? string.Empty).Trim().ToLowerInvariant().Replace("_", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal) switch
    {
        "options" or "serveroptions" => "options",
        "flags" or "serverflags" => "flags",
        "folderconfig" or "folders" or "folderconfiguration" => "folderconfig",
        _ => throw new McpToolException("kind must be options, flags or folderconfig."),
    };

    private static string BackupKind(string kind) => kind switch { "options" => "serveroptions", "flags" => "serverflags", _ => "folderconfig" };

    /// <summary>Asks the server for one of its texts and waits for it.</summary>
    internal static async Task<string> FetchServerTextAsync(McpToolContext c, McpSessionTarget target, string kind)
    {
        IRcSession session = target.Session;
        TextEventArgs answer = kind switch
        {
            "options" => await c.RequestAsync<TextEventArgs>(session, h => session.ServerOptionsReceived += h, h => session.ServerOptionsReceived -= h, _ => true, session.RequestServerOptionsAsync, "the server options").ConfigureAwait(true),
            "flags" => await c.RequestAsync<TextEventArgs>(session, h => session.ServerFlagsReceived += h, h => session.ServerFlagsReceived -= h, _ => true, session.RequestServerFlagsAsync, "the server flags").ConfigureAwait(true),
            _ => await c.RequestAsync<TextEventArgs>(session, h => session.FolderConfigReceived += h, h => session.FolderConfigReceived -= h, _ => true, session.RequestFolderConfigAsync, "the folder configuration").ConfigureAwait(true),
        };
        return Normalize(answer.Text);
    }

    internal static async Task UploadServerTextAsync(IRcSession session, string kind, string text)
    {
        bool sent = kind switch
        {
            "options" => await session.UploadServerOptionsAsync(text).ConfigureAwait(true),
            "flags" => await session.UploadServerFlagsAsync(text).ConfigureAwait(true),
            _ => await session.UploadFolderConfigAsync(text).ConfigureAwait(true),
        };
        McpToolContext.Sent(sent, session, "The server " + kind);
    }

    public static async Task<string> ReadServerTextAsync(McpToolContext c)
    {
        c.RequireRc();
        string kind = ParseServerTextKind(c.Args.RequiredString("kind"));
        string text = await FetchServerTextAsync(c, c.Target, kind).ConfigureAwait(true);
        string? found = null;
        if (c.Args.Has("query"))
        {
            Func<string, bool> matches = Matcher(c.Args);
            int contextLines = c.Args.Int("contextLines", 0);
            found = await Task.Run(() =>
            {
                int budget = McpProtocol.MaximumLines * 10;
                return SearchText(text, matches, contextLines, ref budget);
            }, c.Token).ConfigureAwait(true);
        }
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteNumber("lines", LineCount(text));
            if (found is not null) McpJson.Text(writer, "matches", found);
            else McpJson.Text(writer, "text", text);
            writer.WriteEndObject();
        });
    }

    public static async Task<string> WriteServerTextAsync(McpToolContext c)
    {
        c.RequireRc();
        string kind = ParseServerTextKind(c.Args.RequiredString("kind"));
        if (kind == "folderconfig" && (c.Args.Has("set") || c.Args.Has("remove"))) throw new McpToolException("The folder configuration has no keys: use text or edits.");
        // Edits and keys need the current text; a whole new text needs it only for the backup, which force may go without.
        string? current = c.Args.Has("text")
            ? await c.ReadForBackupAsync(() => FetchServerTextAsync(c, c.Target, kind), "server " + kind).ConfigureAwait(true)
            : await FetchServerTextAsync(c, c.Target, kind).ConfigureAwait(true);
        string updated = MergeSettings(current ?? string.Empty, c.Args, ignoreCase: kind == "options", out List<string> notes);
        if (updated == current) return Status("The server " + kind + " already are this; nothing was sent.");
        string change = notes.Count > 0 ? string.Join("; ", notes) : current is null ? LineCount(updated) + " lines (the current text could not be read)" : DiffSummary(current, updated);
        await c.ApproveAsync("Change the server " + kind + " of " + c.Server + ": " + (notes.Count > 0 ? change : change + "\n\nThe new text:\n" + McpToolContext.Whole(updated))).ConfigureAwait(true);
        if (c.Options.McpApprove && current is not null && await FetchServerTextAsync(c, c.Target, kind).ConfigureAwait(true) != current)
            throw new McpToolException("The server " + kind + " changed while the question was open; nothing was sent. Read them again and repeat the change.");
        McpBackupEntry? backup = current is null ? null : await c.BackupAsync(c.Server, BackupKind(kind), kind, 0, string.Empty, Encoding.UTF8.GetBytes(current), "rc_write_server_text").ConfigureAwait(true);
        long since = c.Target.Journal.Sequence;
        await UploadServerTextAsync(c.Session, kind, updated).ConfigureAwait(true);
        return Written(c, await c.CollectAsync(c.Target, since, 1000, AnswerKinds).ConfigureAwait(true), backup, "The server " + kind + " were sent.", writer =>
        {
            McpJson.Strings(writer, "changes", notes.Count > 0 ? notes : new List<string> { change });
            writer.WriteNumber("lines", LineCount(updated));
        });
    }

    /// <summary>
    /// The new text of a <c>key=value</c> text: <c>text</c> replaces it all; otherwise <c>edits</c> (guarded replacements), then <c>set</c>
    /// (an object of keys and values: the line of each key is replaced, keeping its spacing, or added at the end) and <c>remove</c> (keys
    /// whose lines go) are applied to the current text.
    /// </summary>
    internal static string MergeSettings(string current, McpArguments args, bool ignoreCase, out List<string> notes)
    {
        notes = new List<string>();
        if (args.TryString("text", out string whole))
        {
            if (args.Has("set") || args.Has("remove") || args.Has("edits") || args.Has("expected")) throw new McpToolException("Give text alone, or edits/set/remove without text.");
            return Normalize(whole);
        }
        IReadOnlyList<McpArguments> edits = EditsOf(args);
        IReadOnlyList<KeyValuePair<string, string>> set = args.StringMap("set");
        IReadOnlyList<string> remove = args.Strings("remove");
        if (edits.Count == 0 && set.Count == 0 && remove.Count == 0) throw new McpToolException("Give text (all of it), edits, set or remove.");
        string text = edits.Count > 0 ? ApplyEdits(current, edits) : current;
        if (edits.Count > 0) notes.Add(edits.Count + " edit(s)");
        bool trailing = text.EndsWith('\n');
        List<string> lines = text.Length == 0 ? new List<string>() : text.Split('\n').ToList();
        if (trailing) lines.RemoveAt(lines.Count - 1);
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string separator = lines.Count(line => line.Contains(" = ", StringComparison.Ordinal)) > lines.Count / 2 && lines.Count > 0 ? " = " : "=";
        foreach ((string key, string value) in set)
        {
            if (key.Trim().Length == 0 || key.Contains('=') || key.Contains('\n') || value.Contains('\n')) throw new McpToolException("set: \"" + key + "\" is not a key, or its value has a line break.");
            int index = lines.FindIndex(line => string.Equals(KeyOf(line), key.Trim(), comparison));
            if (index < 0)
            {
                lines.Add(key.Trim() + separator + value);
                notes.Add("added " + key.Trim());
                continue;
            }
            string line = lines[index];
            int equals = line.IndexOf('=');
            string head = line[..equals];
            string spacing = line.Length > equals + 1 && line[equals + 1] == ' ' ? " " : string.Empty;
            string updated = head + "=" + spacing + value;
            if (updated != line) notes.Add("set " + key.Trim());
            lines[index] = updated;
            for (int duplicate = lines.Count - 1; duplicate > index; duplicate--)
                if (string.Equals(KeyOf(lines[duplicate]), key.Trim(), comparison)) lines.RemoveAt(duplicate);
        }
        foreach (string key in remove)
        {
            int removed = lines.RemoveAll(line => string.Equals(KeyOf(line), key.Trim(), comparison));
            notes.Add(removed > 0 ? "removed " + key.Trim() : key.Trim() + " was not there");
        }
        return string.Join('\n', lines) + (trailing || lines.Count > 0 && current.EndsWith('\n') ? "\n" : string.Empty);
    }

    private static string? KeyOf(string line)
    {
        int equals = line.IndexOf('=');
        if (equals <= 0) return null;
        string key = line[..equals].Trim();
        return key.Length == 0 || key.StartsWith('#') || key.StartsWith("//", StringComparison.Ordinal) ? null : key;
    }
}
