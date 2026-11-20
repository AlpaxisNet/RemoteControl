using System.Text;
using System.Text.Json;
using RemoteControl.Scripting;

namespace RemoteControl.Extensions;

/// <summary>A document of the GS2 reference.</summary>
/// <param name="Id">Its name for the tools: the path inside the bundled reference (<c>docs/GraalScript_Reference.md</c>), or the folder's name and the path for another folder (<c>GScript.GS2Taco/ghidra/TPlayer/TPlayer.txt</c>).</param>
/// <param name="FullPath">The file.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Verified">Whether people verified it (<c>docs/GraalScript_Reference.md</c>).</param>
/// <param name="Bundled">Whether it is part of the reference beside the program.</param>
/// <param name="Rank">Its order in searches: the verified guide first, then the core reference, the basics, the reference documents, the lists, other folders.</param>
/// <param name="Description">What it is, from the reference's own README, or empty.</param>
internal sealed record McpReferenceDocument(string Id, string FullPath, long Size, bool Verified, bool Bundled, int Rank, string Description);

/// <summary>
/// The GS2 language reference for MCP clients: the copy of the GScript.GS2Taco documentation beside the program and the folders of the
/// option <c>mcpreferencefolders</c> (a full checkout with its decompiled engine code and example servers, say). Files are read on a
/// background task; nothing here touches the user interface.
/// </summary>
internal sealed class McpReference
{
    /// <summary>The human-verified guide, which comes first in every search.</summary>
    public const string VerifiedId = "docs/GraalScript_Reference.md";

    /// <summary>The largest file searched or read.</summary>
    public const long MaximumFileBytes = 8 * 1024 * 1024;

    private static readonly string[] TextExtensions = { ".md", ".txt", ".gs2", ".gs", ".nw", ".json", ".xml", ".ini", ".cfg", ".html", ".htm" };

    /// <summary>Makes the reference of the bundled folder and the folders of <c>mcpreferencefolders</c>.</summary>
    /// <param name="bundled">The reference beside the program.</param>
    /// <param name="extraFolders">The option: folders separated by <c>;</c>; <c>~</c> is the home folder, a relative folder is taken inside <paramref name="relativeTo"/>.</param>
    /// <param name="relativeTo">Where relative folders are (the MCP workspace), or <c>null</c> to refuse them.</param>
    public McpReference(string bundled, string extraFolders, string? relativeTo = null)
    {
        Bundled = Path.GetFullPath(bundled);
        var roots = new List<string>();
        var refused = new List<(string Entry, string Reason)>();
        foreach (string raw in (extraFolders ?? string.Empty).Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string entry = raw;
            if (entry == "~" || entry.StartsWith("~/", StringComparison.Ordinal) || entry.StartsWith("~\\", StringComparison.Ordinal))
                entry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), entry.Length > 2 ? entry[2..] : string.Empty);
            if (!Path.IsPathRooted(entry))
            {
                if (relativeTo is null)
                {
                    refused.Add((raw, "not an absolute folder"));
                    continue;
                }
                entry = Path.Combine(relativeTo, entry);
            }
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry));
            if (!roots.Contains(root)) roots.Add(root);
        }
        ExtraRoots = roots;
        Refused = refused;
    }

    public string Bundled { get; }

    public IReadOnlyList<string> ExtraRoots { get; }

    /// <summary>Entries of the option that are not used, and why.</summary>
    public IReadOnlyList<(string Entry, string Reason)> Refused { get; }

    /// <summary>
    /// The prefix of every extra folder that exists: its name, numbered when another folder (or a top-level folder of the bundled reference,
    /// such as <c>docs</c>) has it already, so that no id of one folder can be the id of another document.
    /// </summary>
    public IReadOnlyList<(string Root, string Name)> RootNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(Bundled))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(Bundled)) names.Add(Path.GetFileName(entry));
        }
        var result = new List<(string, string)>();
        foreach (string root in ExtraRoots)
        {
            if (!Directory.Exists(root)) continue;
            string name = Path.GetFileName(root);
            if (name.Length == 0) name = "folder";
            string candidate = name;
            for (int number = 2; !names.Add(candidate); number++) candidate = name + number;
            result.Add((root, candidate));
        }
        return result;
    }

    /// <summary>Every document, in search order (call off the UI thread: it lists folders).</summary>
    public IReadOnlyList<McpReferenceDocument> Documents()
    {
        var documents = new List<McpReferenceDocument>();
        Dictionary<string, string> descriptions = ReadDescriptions(Path.Combine(Bundled, "README.md"));
        var bundled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Files(Bundled))
        {
            string id = Path.GetRelativePath(Bundled, file).Replace('\\', '/');
            bundled.Add(id);
            documents.Add(new McpReferenceDocument(id, file, new FileInfo(file).Length, string.Equals(id, VerifiedId, StringComparison.OrdinalIgnoreCase), true, RankOf(id), descriptions.GetValueOrDefault(id, string.Empty)));
        }
        foreach ((string root, string name) in RootNames())
        {
            foreach (string file in Files(root))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                // A checkout of GS2Taco has the bundled documents too (in another version, or with other line endings): the bundled copy,
                // the one people verified, is the one that counts, so it is listed once.
                if (bundled.Contains(relative)) continue;
                documents.Add(new McpReferenceDocument(name + "/" + relative, file, new FileInfo(file).Length, false, false, 9, string.Empty));
            }
        }
        return documents.OrderBy(document => document.Rank).ThenBy(document => document.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>A document by id (without regard to case), or by a file name only one document has.</summary>
    public static McpReferenceDocument? Find(IReadOnlyList<McpReferenceDocument> documents, string id)
    {
        string wanted = (id ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        McpReferenceDocument? exact = documents.FirstOrDefault(document => string.Equals(document.Id, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        McpReferenceDocument[] byName = documents.Where(document => string.Equals(Path.GetFileName(document.Id), wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        return byName.Length == 1 ? byName[0] : null;
    }

    /// <summary>The text of a document (UTF-8, or Windows-1252 when it is not valid UTF-8), or <c>null</c> for a binary or too large file.</summary>
    public static string? Read(McpReferenceDocument document)
    {
        if (document.Size > MaximumFileBytes) return null;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(document.FullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (McpFileText.IsBinary(bytes)) return null;
        McpFileText.Decode(bytes, "auto", out string text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>The headings of a Markdown text: line number (from 1), level and text.</summary>
    public static IReadOnlyList<(int Line, int Level, string Text)> Outline(string text)
    {
        var headings = new List<(int, int, string)>();
        string[] lines = text.Split('\n');
        bool fence = false;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) fence = !fence;
            if (fence || !line.StartsWith('#')) continue;
            int level = line.TakeWhile(character => character == '#').Count();
            if (level > 6 || line.Length <= level || line[level] != ' ') continue;
            headings.Add((index + 1, level, line[level..].Trim()));
        }
        return headings;
    }

    /// <summary>The section that starts at the first heading containing <paramref name="section"/> and ends before the next heading of its level or higher.</summary>
    public static (int Start, int End, string Heading)? Section(string text, string section)
    {
        IReadOnlyList<(int Line, int Level, string Text)> headings = Outline(text);
        string wanted = section.Trim().TrimStart('#').Trim();
        int index = -1;
        for (int candidate = 0; candidate < headings.Count && index < 0; candidate++) if (string.Equals(headings[candidate].Text, wanted, StringComparison.OrdinalIgnoreCase)) index = candidate;
        for (int candidate = 0; candidate < headings.Count && index < 0; candidate++) if (headings[candidate].Text.Contains(wanted, StringComparison.OrdinalIgnoreCase)) index = candidate;
        if (index < 0) return null;
        (int line, int level, string heading) = headings[index];
        int end = text.Count(character => character == '\n') + 1;
        for (int next = index + 1; next < headings.Count; next++)
        {
            if (headings[next].Level > level) continue;
            end = headings[next].Line - 1;
            break;
        }
        return (line, end, heading);
    }

    private static int RankOf(string id)
    {
        if (string.Equals(id, VerifiedId, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(id, "CORE.md", StringComparison.OrdinalIgnoreCase)) return 1;
        if (id.StartsWith("basics/", StringComparison.OrdinalIgnoreCase)) return 2;
        if (id.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) && id.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return 3;
        if (id.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) && id.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return 4;
        if (id.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)) return 5;
        return 6;
    }

    private static IEnumerable<string> Files(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(root, "*", options)
            .Where(file => TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) && !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.')));
    }

    /// <summary>The descriptions of the README's tables: <c>| [name](path) | description |</c>.</summary>
    private static Dictionary<string, string> ReadDescriptions(string readme)
    {
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(readme)) return descriptions;
        foreach (string line in File.ReadLines(readme))
        {
            if (!line.StartsWith("| [", StringComparison.Ordinal)) continue;
            int open = line.IndexOf("](", StringComparison.Ordinal);
            int close = open < 0 ? -1 : line.IndexOf(')', open);
            int cell = close < 0 ? -1 : line.IndexOf('|', close);
            if (cell < 0) continue;
            string path = line[(open + 2)..close].Trim();
            string description = line[(cell + 1)..].Trim().TrimEnd('|').Trim();
            if (path.Length > 0 && !path.EndsWith('/')) descriptions.TryAdd(path, description);
        }
        return descriptions;
    }
}

/// <summary>
/// The script API of the option <c>autocompletesource</c> (by default <c>https://api.gscript.dev/</c>, the definitions the script editor
/// completes with), loaded with the editor's loader and cache file and kept for an hour.
/// </summary>
internal sealed class McpScriptApi
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private readonly string? _cacheFile;
    private readonly ApiDefinitionLoader _loader;
    private Task<ApiLoadResult>? _loading;
    private string _source = string.Empty;
    private DateTimeOffset _loaded;

    public McpScriptApi(string? cacheFile, HttpMessageHandler? handler = null)
    {
        _cacheFile = cacheFile;
        _loader = new ApiDefinitionLoader(handler);
    }

    /// <summary>The source an option names: empty is the default source.</summary>
    public static string SourceOf(string option) => string.IsNullOrWhiteSpace(option) ? ApiDefinitionLoader.DefaultSource : option.Trim();

    /// <summary>
    /// The definitions. They are loaded again when the source changed, after an hour, or when <paramref name="refresh"/>; a load that came
    /// only from the cache, or failed, is tried again after a minute. A load in progress is shared. Used on one context at a time.
    /// </summary>
    public Task<ApiLoadResult> GetAsync(string source, bool refresh, CancellationToken cancellationToken)
    {
        if (_loading is not null && _source == source)
        {
            if (!_loading.IsCompleted) return _loading;
            TimeSpan age = DateTimeOffset.UtcNow - _loaded;
            bool good = _loading.IsCompletedSuccessfully && _loading.Result.Origin == ApiDefinitionOrigin.Source;
            if (!refresh && (good ? age < Lifetime : age < RetryAfter)) return _loading;
        }
        _source = source;
        _loaded = DateTimeOffset.UtcNow;
        _loading = _loader.LoadAsync(source, _cacheFile, cancellationToken);
        return _loading;
    }
}

internal static partial class McpServerTools
{
    // ---- The GS2 reference ---------------------------------------------------------------------------------------------------------

    public static async Task<string> Gs2DocsAsync(McpToolContext c)
    {
        McpReference reference = c.Services.Reference(c.Options);
        (IReadOnlyList<McpReferenceDocument> documents, IReadOnlyList<(string Root, string Name)> names) = await Task.Run(() => (reference.Documents(), reference.RootNames()), c.Token).ConfigureAwait(true);
        if (!c.Args.TryString("document", out string id) || id.Trim().Length == 0)
        {
            Func<string, bool> filter = NameFilter(c.Args.String("filter"));
            bool filtered = c.Args.String("filter").Length > 0;
            McpReferenceDocument[] shown = documents.Where(document => (filtered || document.Bundled) && filter(document.Id)).Take(500).ToArray();
            return JsonText.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("start", "Read " + McpReference.VerifiedId + " (verified by people; its outline first) and CORE.md before writing GS2. rc_gs2_search finds a topic in every document; rc_script_help looks up a function's signature.");
                writer.WriteStartArray("documents");
                foreach (McpReferenceDocument document in shown) WriteDocument(writer, document);
                writer.WriteEndArray();
                writer.WriteStartArray("otherFolders");
                foreach ((string root, string prefix) in names)
                {
                    writer.WriteStartObject();
                    McpJson.Text(writer, "folder", root);
                    McpJson.Text(writer, "prefix", prefix + "/");
                    writer.WriteNumber("documents", documents.Count(document => !document.Bundled && document.Id.StartsWith(prefix + "/", StringComparison.Ordinal)));
                    writer.WriteBoolean("exists", true);
                    writer.WriteEndObject();
                }
                foreach (string root in reference.ExtraRoots.Where(root => !Directory.Exists(root)))
                {
                    writer.WriteStartObject();
                    McpJson.Text(writer, "folder", root);
                    writer.WriteBoolean("exists", false);
                    writer.WriteEndObject();
                }
                foreach ((string entry, string reason) in reference.Refused)
                {
                    writer.WriteStartObject();
                    McpJson.Text(writer, "folder", entry);
                    writer.WriteBoolean("exists", false);
                    McpJson.Text(writer, "reason", reason);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                if (!filtered && reference.ExtraRoots.Count > 0) writer.WriteString("hint", "Give filter (such as GScript.GS2Taco/ghidra/*) to list the documents of the other folders.");
                writer.WriteEndObject();
            });
        }

        McpReferenceDocument found = McpReference.Find(documents, id) ?? throw new McpToolException("No reference document is named \"" + id + "\" (rc_gs2_docs lists them).");
        string text = await Task.Run(() => McpReference.Read(found), c.Token).ConfigureAwait(true)
            ?? throw new McpToolException(found.Id + " is not a text file of at most " + McpReference.MaximumFileBytes + " bytes.");
        int lines = text.Length == 0 ? 0 : text.Count(character => character == '\n') + 1;
        const int Whole = 100_000;
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            WriteDocumentHeader(writer, found);
            writer.WriteNumber("lines", lines);
            if (c.Args.TryString("section", out string section) && section.Trim().Length > 0)
            {
                (int Start, int End, string Heading)? range = McpReference.Section(text, section) ?? throw new McpToolException("No heading of " + found.Id + " contains \"" + section + "\" (outline: true lists them).");
                string body = string.Join('\n', text.Split('\n')[(range.Value.Start - 1)..Math.Min(lines, range.Value.End)]);
                bool cut = body.Length > Whole;
                McpJson.Text(writer, "section", range.Value.Heading);
                writer.WriteNumber("start", range.Value.Start);
                writer.WriteNumber("end", range.Value.End);
                McpJson.Text(writer, "text", cut ? body[..Whole] : body);
                if (cut) writer.WriteString("truncated", "The section is longer; read on with start/end from line " + (range.Value.Start + body[..Whole].Count(character => character == '\n')) + ".");
            }
            else if (c.Args.Has("start") || c.Args.Has("end"))
            {
                int start = Math.Max(1, c.Args.Int("start", 1));
                int last = (int)Math.Min(lines, start + 1999L);
                int end = start > last ? start : Math.Clamp(c.Args.Int("end", start + 399), start, last);
                writer.WriteNumber("start", start);
                writer.WriteNumber("end", end);
                McpJson.Text(writer, "text", start > lines ? string.Empty : string.Join('\n', text.Split('\n')[(start - 1)..end]));
            }
            else if (c.Args.Bool("outline", false) || text.Length > Whole)
            {
                if (!c.Args.Bool("outline", false)) writer.WriteString("hint", "The document is long: here is its outline; read a part with section (a heading) or start/end.");
                writer.WriteStartArray("outline");
                foreach ((int line, int level, string heading) in McpReference.Outline(text))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("line", line);
                    writer.WriteNumber("level", level);
                    McpJson.Text(writer, "heading", heading);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            else
            {
                McpJson.Text(writer, "text", text);
            }
            writer.WriteEndObject();
        });
    }

    public static async Task<string> Gs2SearchAsync(McpToolContext c)
    {
        Func<string, bool> matches = Matcher(c.Args);
        McpReference reference = c.Services.Reference(c.Options);
        Func<string, bool> filter = NameFilter(c.Args.String("documents"));
        int budget = Math.Clamp(c.Args.Int("maxMatches", 60), 1, 1000);
        int perDocument = Math.Clamp(c.Args.Int("maxPerDocument", 15), 1, 200);
        int context = Math.Clamp(c.Args.Int("contextLines", 1), 0, 20);
        bool everywhere = c.Args.Bool("includeOtherFolders", true);
        CancellationToken token = c.Token;
        var results = await Task.Run(() =>
        {
            var found = new List<(McpReferenceDocument Document, string Text, string[] Sections, int Hits)>();
            int searched = 0;
            int left = budget;
            foreach (McpReferenceDocument document in reference.Documents())
            {
                if (left <= 0 || token.IsCancellationRequested) break;
                if (!document.Bundled && !everywhere || !filter(document.Id)) continue;
                string? text = McpReference.Read(document);
                if (text is null) continue;
                searched++;
                int allowed = Math.Min(perDocument, left);
                int before = allowed;
                string block = SearchText(text, matches, context, ref allowed);
                if (block.Length == 0) continue;
                int hits = before - allowed;
                left -= hits;
                found.Add((document, block, document.Id.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? SectionsOf(text, block) : Array.Empty<string>(), hits));
            }
            return (found, searched, left);
        }, token).ConfigureAwait(true);
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("searched", results.searched);
            writer.WriteBoolean("truncated", results.left <= 0);
            writer.WriteStartArray("results");
            foreach ((McpReferenceDocument document, string text, string[] sections, int hits) in results.found)
            {
                writer.WriteStartObject();
                WriteDocumentHeader(writer, document);
                writer.WriteNumber("hits", hits);
                if (sections.Length > 0) McpJson.Strings(writer, "sections", sections);
                McpJson.Text(writer, "matches", text);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>The headings under which the numbered lines of a search block are.</summary>
    private static string[] SectionsOf(string text, string block)
    {
        IReadOnlyList<(int Line, int Level, string Text)> outline = McpReference.Outline(text);
        var sections = new List<string>();
        foreach (string line in block.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || !int.TryParse(line.AsSpan(0, colon), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)) continue;
            (int Line, int Level, string Text) heading = outline.LastOrDefault(item => item.Line <= number);
            if (heading.Text is not null && !sections.Contains(heading.Text)) sections.Add(heading.Text);
        }
        return sections.ToArray();
    }

    private static void WriteDocument(Utf8JsonWriter writer, McpReferenceDocument document)
    {
        writer.WriteStartObject();
        WriteDocumentHeader(writer, document);
        writer.WriteNumber("size", document.Size);
        if (document.Description.Length > 0) McpJson.Text(writer, "description", document.Description);
        writer.WriteEndObject();
    }

    private static void WriteDocumentHeader(Utf8JsonWriter writer, McpReferenceDocument document)
    {
        McpJson.Text(writer, "document", document.Id);
        if (document.Verified) writer.WriteBoolean("verified", true);
        if (!document.Bundled) writer.WriteBoolean("local", true);
    }

    // ---- The script API ---------------------------------------------------------------------------------------------------------------

    public static async Task<string> ScriptHelpAsync(McpToolContext c)
    {
        if (c.Args.Bool("fromServer", false)) return await ServerScriptHelpAsync(c, (c.Args.String("name").Trim() is { Length: > 0 } wanted ? wanted : c.Args.String("query")).Trim()).ConfigureAwait(true);
        string source = McpScriptApi.SourceOf(c.Options.AutocompleteSource);
        ApiLoadResult loaded = await c.Services.ScriptApi.GetAsync(source, c.Args.Bool("refresh", false), CancellationToken.None).WaitAsync(c.Token).ConfigureAwait(true);
        if (loaded.Origin == ApiDefinitionOrigin.None) throw new McpToolException("The script API could not be loaded from " + source + (loaded.SourceError is null ? "." : ": " + loaded.SourceError + "."));
        ApiDefinitionSet set = loaded.Definitions;
        string name = c.Args.String("name").Trim();
        if (name.Length > 0)
        {
            ApiDefinition? definition = set.Resolve(name, null);
            if (definition is not null)
            {
                return JsonText.Write(writer =>
                {
                    writer.WriteStartObject();
                    WriteApiOrigin(writer, source, loaded);
                    writer.WritePropertyName("definition");
                    WriteDefinition(writer, definition, full: true);
                    writer.WriteEndObject();
                });
            }
        }
        string query = c.Args.String("query").Trim();
        if (query.Length == 0) query = name;
        if (query.Length == 0) throw new McpToolException("Give name (a function, variable or event) or query.");
        Func<string, bool> filter = NameFilter(query);
        bool descriptions = c.Args.Bool("inDescriptions", false);
        string scope = c.Args.String("scope").Trim().ToLowerInvariant();
        string type = c.Args.String("type").Trim().ToLowerInvariant();
        int limit = Math.Clamp(c.Args.Int("limit", 40), 1, 300);
        ApiDefinition[] hits = set.Definitions
            .Where(definition => scope.Length == 0 || string.Equals(definition.Scope, scope, StringComparison.OrdinalIgnoreCase))
            .Where(definition => type.Length == 0 || string.Equals(definition.Type, type, StringComparison.OrdinalIgnoreCase))
            .Where(definition => filter(definition.Name) || descriptions && definition.Description.Contains(query.Trim('*'), StringComparison.OrdinalIgnoreCase))
            .OrderBy(definition => !definition.Name.StartsWith(query.Trim('*'), StringComparison.OrdinalIgnoreCase)).ThenBy(definition => definition.Name.Length).ThenBy(definition => definition.SearchName, StringComparer.Ordinal)
            .ToArray();
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            WriteApiOrigin(writer, source, loaded);
            if (name.Length > 0) writer.WriteString("note", "No definition is named \"" + name + "\"; these names are like it.");
            writer.WriteNumber("count", hits.Length);
            writer.WriteStartArray("definitions");
            foreach (ApiDefinition definition in hits.Take(limit)) WriteDefinition(writer, definition, full: false);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static void WriteApiOrigin(Utf8JsonWriter writer, string source, ApiLoadResult loaded)
    {
        McpJson.Text(writer, "source", source);
        writer.WriteString("origin", loaded.Origin == ApiDefinitionOrigin.Source ? "source" : "cache");
        if (loaded.SourceError is not null) McpJson.Text(writer, "sourceError", loaded.SourceError);
    }

    private static void WriteDefinition(Utf8JsonWriter writer, ApiDefinition definition, bool full)
    {
        writer.WriteStartObject();
        McpJson.Text(writer, "name", definition.Name);
        McpJson.Text(writer, "signature", definition.Signature);
        if (definition.Type.Length > 0) McpJson.Text(writer, "type", definition.Type);
        if (definition.Scope.Length > 0) McpJson.Text(writer, "scope", definition.Scope);
        if (definition.Returns.Length > 0) McpJson.Text(writer, "returns", definition.Returns);
        if (full)
        {
            McpJson.Strings(writer, "parameters", definition.Parameters);
            McpJson.Text(writer, "description", definition.Description);
            if (definition.Example.Length > 0) McpJson.Text(writer, "example", definition.Example);
        }
        else
        {
            string summary = definition.Description.Split('\n').FirstOrDefault(line => line.Trim().Length > 0)?.Trim() ?? string.Empty;
            if (summary.Length > 0) McpJson.Text(writer, "summary", summary.Length > 200 ? summary[..200] + "…" : summary);
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// The diagnostics options of a check, as the editor makes them: with the script API's functions when it loads (unknown functions and
    /// wrong argument counts are then reported); without it, the syntax only (no "unknown function" for names the analyser does not know).
    /// </summary>
    internal static async Task<(DiagnosticsOptions Options, string Api)> CheckOptionsAsync(McpToolContext c)
    {
        DiagnosticsOptions syntaxOnly = DiagnosticsOptions.ForDefinitions(null, completionEnabled: true);
        if (!c.Args.Bool("api", true)) return (syntaxOnly, "not used");
        string source = McpScriptApi.SourceOf(c.Options.AutocompleteSource);
        try
        {
            ApiLoadResult loaded = await c.Services.ScriptApi.GetAsync(source, false, CancellationToken.None).WaitAsync(c.Token).ConfigureAwait(true);
            if (loaded.Origin == ApiDefinitionOrigin.None) return (syntaxOnly, "not loaded" + (loaded.SourceError is null ? string.Empty : ": " + loaded.SourceError));
            return (DiagnosticsOptions.ForDefinitions(loaded.Definitions, completionEnabled: true), source + (loaded.Origin == ApiDefinitionOrigin.Cache ? " (cache)" : string.Empty));
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            return (syntaxOnly, "not loaded: " + exception.Message);
        }
    }
}
