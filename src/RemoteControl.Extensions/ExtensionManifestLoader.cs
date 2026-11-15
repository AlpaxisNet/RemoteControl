using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using RemoteControl.Core;

namespace RemoteControl.Extensions;

/// <summary>
/// Reads extension manifests and finds the packages of the extensions folders (<c>TExtensions.cpp:299-376</c>, <c>:520-527</c>).
/// </summary>
/// <remarks>
/// Differences from the C++:
/// <list type="bullet">
/// <item>A JSON manifest is parsed as JSON (comments and trailing commas allowed) and only its top-level keys count; the C++ searched
/// the text for the first <c>"key"</c> anywhere, so a nested <c>"id"</c> could win. A manifest that is not valid JSON is listed with an
/// error instead of being read in pieces.</item>
/// <item>The entry is resolved with <see cref="SafePath"/>: an absolute entry, one with <c>..</c> or a drive letter is an error.</item>
/// <item>An id containing white space is an error: the state file separates its fields by spaces and could not read it back.</item>
/// <item>The package folder keeps its path; the C++ resolved symbolic links, so removing a linked package deleted the link target (D-24).</item>
/// <item>A UTF-8 byte order mark at the start of an INI manifest is ignored (the C++ made it part of the first key).</item>
/// </list>
/// </remarks>
public static class ExtensionManifestLoader
{
    /// <summary>The manifest names looked for in a package folder, in order; the first that exists is read.</summary>
    public static ImmutableArray<string> ManifestFileNames { get; } = ImmutableArray.Create("extension.json", "extension.ini", "manifest.ini");

    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 };

    /// <summary>The largest manifest read; a bigger file is an error.</summary>
    public const int MaximumManifestSize = 1024 * 1024;

    /// <summary>Reads one manifest file. Never throws for a bad file: the result then has an <see cref="ExtensionManifest.Error"/>.</summary>
    /// <param name="path">The manifest file (<c>extension.json</c>, <c>extension.ini</c> or <c>manifest.ini</c>).</param>
    /// <param name="root">The extensions folder the package is in, kept in <see cref="ExtensionManifest.Root"/>.</param>
    /// <returns>The manifest; when invalid, its id and name fall back to the folder name.</returns>
    public static ExtensionManifest Load(string path, string? root = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? fullPath;
        string rootPath = root is null ? Path.GetDirectoryName(directory) ?? string.Empty : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        bool json = string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase);
        var manifest = new ExtensionManifest { Directory = directory, Root = rootPath, ManifestFile = fullPath, Format = json ? ExtensionManifestFormat.Json : ExtensionManifestFormat.Ini };
        string text;
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > MaximumManifestSize) return Invalid(manifest, "Manifest is larger than 1 MiB");
            text = File.ReadAllText(fullPath, new UTF8Encoding(false, false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid(manifest, "Manifest could not be opened");
        }
        manifest = json ? ParseJson(manifest, text) : ParseIni(manifest, text);
        return Validate(manifest);
    }

    /// <summary>Finds the packages directly under one extensions folder, sorted by id (ordinal), as <c>scanExtensionManifests</c>.</summary>
    /// <param name="root">The extensions folder; a missing or unreadable folder gives an empty list.</param>
    /// <returns>One manifest per package folder that has a manifest file.</returns>
    public static IReadOnlyList<ExtensionManifest> Scan(string root)
    {
        var result = new List<ExtensionManifest>();
        if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) return result;
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        IEnumerable<string> folders;
        try
        {
            folders = System.IO.Directory.EnumerateDirectories(fullRoot).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return result;
        }
        foreach (string folder in folders)
        {
            foreach (string name in ManifestFileNames)
            {
                string manifestPath = Path.Combine(folder, name);
                if (!File.Exists(manifestPath)) continue;
                result.Add(Load(manifestPath, fullRoot));
                break;
            }
        }
        result.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return result;
    }

    /// <summary>
    /// Scans several extensions folders. A package of a later folder replaces the one with the same id of an earlier folder in
    /// place; a new id is appended. With the program folder first and the user folder last, a user package overrides an installed one.
    /// </summary>
    /// <param name="roots">The extensions folders, lowest priority first.</param>
    /// <returns>The merged list.</returns>
    public static IReadOnlyList<ExtensionManifest> ScanAll(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var result = new List<ExtensionManifest>();
        foreach (string root in roots)
        {
            foreach (ExtensionManifest manifest in Scan(root))
            {
                int existing = result.FindIndex(value => string.Equals(value.Id, manifest.Id, StringComparison.Ordinal));
                if (existing < 0) result.Add(manifest);
                else result[existing] = manifest;
            }
        }
        return result;
    }

    private static ExtensionManifest ParseJson(ExtensionManifest manifest, string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, DocumentOptions);
        }
        catch (JsonException exception)
        {
            return Invalid(manifest, "Manifest is not valid JSON: " + exception.Message);
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalid(manifest, "Manifest is not a JSON object");
            ImmutableArray<string> capabilities = StringArray(root, "capabilities");
            if (capabilities.IsEmpty) capabilities = StringArray(root, "requestedCapabilities");
            ImmutableArray<string> ui = StringArray(root, "ui");
            if (ui.IsEmpty) ui = StringArray(root, "uiContributions");
            string mode = StringValue(root, "mode");
            if (mode.Length == 0) mode = StringValue(root, "entryMode");
            bool autoDiscover = (root.TryGetProperty("autoDiscover", out JsonElement auto) && auto.ValueKind == JsonValueKind.True) || mode is "auto" or "discover";
            int api = 1;
            if (root.TryGetProperty("api", out JsonElement apiValue) && apiValue.ValueKind == JsonValueKind.Number)
            {
                if (apiValue.TryGetInt32(out int whole)) api = whole;
                else if (apiValue.TryGetDouble(out double number) && number is > int.MinValue and < int.MaxValue) api = (int)number;
            }
            var actions = ImmutableArray.CreateBuilder<ExtensionWindowAction>();
            if (root.TryGetProperty("windowActions", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string kind = StringValue(item, "kind");
                    string id = StringValue(item, "id");
                    string label = StringValue(item, "label");
                    string placement = StringValue(item, "placement");
                    if (kind.Length == 0) kind = ExtensionWindowAction.ScriptEditorKind;
                    if (label.Length == 0) label = id;
                    if (id.Length == 0) continue;
                    actions.Add(new ExtensionWindowAction(kind, id, label, placement == "bottom" ? ExtensionActionPlacement.Bottom : ExtensionActionPlacement.Top, StringValue(item, "icon")));
                }
            }
            return manifest with
            {
                Id = StringValue(root, "id"),
                Name = StringValue(root, "name"),
                Version = StringValue(root, "version"),
                Publisher = StringValue(root, "publisher"),
                Entry = StringValue(root, "entry"),
                Runtime = StringValue(root, "runtime"),
                RequestedCapabilities = capabilities,
                UiContributions = ui,
                Themes = StringArray(root, "themes"),
                Commands = StringArray(root, "commands"),
                ReadOnlyViews = StringArray(root, "readOnlyViews"),
                WindowActions = actions.ToImmutable(),
                AutoDiscover = autoDiscover,
                Api = api,
            };
        }
    }

    private static ExtensionManifest ParseIni(ExtensionManifest manifest, string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string rawLine in text.Split('\n'))
        {
            string line = Trim(rawLine);
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            int separator = line.IndexOf('=');
            if (separator < 0) continue;
            values[Trim(line[..separator])] = Trim(line[(separator + 1)..]);
        }
        string Get(string key) => values.TryGetValue(key, out string? value) ? value : string.Empty;
        ImmutableArray<string> capabilities = SplitList(Get("capabilities"));
        if (capabilities.IsEmpty) capabilities = SplitList(Get("requestedCapabilities"));
        ImmutableArray<string> ui = SplitList(Get("ui"));
        if (ui.IsEmpty) ui = SplitList(Get("uiContributions"));
        string mode = Get("mode").Length == 0 ? Get("entryMode") : Get("mode");
        string autoDiscover = Get("autoDiscover");
        var actions = ImmutableArray.CreateBuilder<ExtensionWindowAction>();
        foreach (string token in SplitList(Get("windowActions")))
        {
            int first = token.IndexOf(':');
            int second = first < 0 ? -1 : token.IndexOf(':', first + 1);
            string kind = first < 0 ? ExtensionWindowAction.ScriptEditorKind : Trim(token[..first]);
            string id = first < 0 ? Trim(token) : Trim(second < 0 ? token[(first + 1)..] : token[(first + 1)..second]);
            string label = second < 0 ? id : Trim(token[(second + 1)..]);
            if (id.Length == 0) continue;
            actions.Add(new ExtensionWindowAction(kind, id, label, ExtensionActionPlacement.Top, string.Empty));
        }
        return manifest with
        {
            Id = Get("id"),
            Name = Get("name"),
            Version = Get("version"),
            Publisher = Get("publisher"),
            Entry = Get("entry"),
            Runtime = Get("runtime"),
            RequestedCapabilities = capabilities,
            UiContributions = ui,
            Themes = SplitList(Get("themes")),
            Commands = SplitList(Get("commands")),
            ReadOnlyViews = SplitList(Get("readOnlyViews")),
            WindowActions = actions.ToImmutable(),
            AutoDiscover = mode is "auto" or "discover" || autoDiscover is "1" or "true",
        };
    }

    private static ExtensionManifest Validate(ExtensionManifest manifest)
    {
        if (!manifest.IsValid) return Invalid(manifest, manifest.Error);
        bool json = manifest.Format == ExtensionManifestFormat.Json;
        bool missing = manifest.Id.Length == 0 || manifest.Name.Length == 0 || manifest.Version.Length == 0
            || (json && manifest.Runtime.Length == 0) || (!manifest.AutoDiscover && manifest.Entry.Length == 0);
        if (missing)
        {
            string message = json
                ? manifest.AutoDiscover ? "Manifest requires id, name, version, and runtime" : "Manifest requires id, name, version, runtime, and entry"
                : manifest.AutoDiscover ? "Manifest requires id, name, and version" : "Manifest requires id, name, version, and entry";
            return Invalid(manifest, message);
        }
        if (manifest.Id.Any(char.IsWhiteSpace)) return Invalid(manifest, "Manifest id must not contain white space");
        if (manifest.Entry.Length > 0)
        {
            if (!SafePath.TryCombine(manifest.Directory, manifest.Entry, out string? entryPath, out string? reason)) return Invalid(manifest, "Entry is not inside the package: " + reason);
            manifest = manifest with { EntryPath = entryPath };
        }
        return manifest;
    }

    private static ExtensionManifest Invalid(ExtensionManifest manifest, string error)
    {
        string folder = Path.GetFileName(manifest.Directory);
        string id = manifest.Id.Length == 0 ? folder : manifest.Id;
        return manifest with { Error = error, Id = id, Name = manifest.Name.Length == 0 ? id : manifest.Name, EntryPath = string.Empty };
    }

    private static string StringValue(JsonElement element, string key) =>
        element.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static ImmutableArray<string> StringArray(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Array) return ImmutableArray<string>.Empty;
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) builder.Add(text);
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<string> SplitList(string value)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (string item in value.Split(','))
        {
            string trimmed = Trim(item);
            if (trimmed.Length > 0) builder.Add(trimmed);
        }
        return builder.ToImmutable();
    }

    /// <summary>Trims the characters the C++ trimmed (space, tab, carriage return, line feed).</summary>
    internal static string Trim(string value) => value.Trim(' ', '\t', '\r', '\n');
}
