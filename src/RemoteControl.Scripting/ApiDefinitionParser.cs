using System.Text.Json;

namespace RemoteControl.Scripting;

/// <summary>
/// Reads the script API JSON (C++ <c>completionDefinitions</c>). Two shapes are accepted: an array of definition objects, or an
/// object mapping a name to a definition object (the key is the name when the definition has none). Definition keys:
/// <c>name</c>, <c>type</c>, <c>returns</c>, <c>scope</c>, <c>description</c>, <c>example</c>, and the parameters under
/// <c>params</c>, <c>parameters</c>, <c>args</c> or <c>arguments</c> (the last one present wins), given as an array of strings,
/// an array of objects (their <c>name</c>, else <c>label</c>, else <c>type</c>), or one signature string split at top-level
/// commas. Keys are case-sensitive. Definitions without a name are dropped; duplicates (case-insensitive) keep the first.
/// </summary>
/// <remarks>
/// Differences from the C++ hand-written reader: the JSON must be well-formed (trailing commas and comments are allowed), and an
/// entry with a wrong value type (a definition that is not an object, a <c>name</c> that is not a string) is skipped instead of
/// discarding the whole file.
/// </remarks>
public static class ApiDefinitionParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    /// <summary>Parses API JSON text.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The definitions sorted by lower-case name without duplicates; empty when the text is not valid JSON of an accepted shape.</returns>
    public static IReadOnlyList<ApiDefinition> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ApiDefinition>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, DocumentOptions);
            return FromRoot(document.RootElement);
        }
        catch (JsonException)
        {
            return Array.Empty<ApiDefinition>();
        }
    }

    /// <summary>Parses API JSON bytes (UTF-8, an optional byte order mark is skipped).</summary>
    /// <param name="utf8">The bytes.</param>
    /// <returns>The definitions, as <see cref="Parse(string)"/>.</returns>
    public static IReadOnlyList<ApiDefinition> Parse(ReadOnlyMemory<byte> utf8)
    {
        ReadOnlyMemory<byte> bytes = utf8;
        if (bytes.Length >= 3 && bytes.Span[0] == 0xEF && bytes.Span[1] == 0xBB && bytes.Span[2] == 0xBF) bytes = bytes[3..];
        if (bytes.IsEmpty) return Array.Empty<ApiDefinition>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, DocumentOptions);
            return FromRoot(document.RootElement);
        }
        catch (JsonException)
        {
            return Array.Empty<ApiDefinition>();
        }
        catch (ArgumentException)
        {
            return Array.Empty<ApiDefinition>();
        }
    }

    private static IReadOnlyList<ApiDefinition> FromRoot(JsonElement root)
    {
        var definitions = new List<ApiDefinition>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in root.EnumerateArray())
            {
                ApiDefinition? definition = ReadDefinition(element, null);
                if (definition != null) definitions.Add(definition);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                ApiDefinition? definition = ReadDefinition(property.Value, property.Name);
                if (definition != null) definitions.Add(definition);
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return definitions.Where(definition => seen.Add(definition.SearchName)).OrderBy(definition => definition.SearchName, StringComparer.Ordinal).ToArray();
    }

    private static ApiDefinition? ReadDefinition(JsonElement element, string? key)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        string name = string.Empty, type = string.Empty, returns = string.Empty, scope = string.Empty, description = string.Empty, example = string.Empty;
        List<string> parameters = new();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "params":
                case "parameters":
                case "args":
                case "arguments":
                    ReadParameters(property.Value, ref parameters);
                    break;
                case "name": name = StringValue(property.Value, name); break;
                case "type": type = StringValue(property.Value, type); break;
                case "returns": returns = StringValue(property.Value, returns); break;
                case "scope": scope = StringValue(property.Value, scope); break;
                case "description": description = StringValue(property.Value, description); break;
                case "example": example = StringValue(property.Value, example); break;
            }
        }
        if (name.Length == 0) name = key ?? string.Empty;
        return name.Length == 0 ? null : new ApiDefinition(name, type, parameters, returns, scope, description, example);
    }

    private static string StringValue(JsonElement value, string current) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : current;

    private static void ReadParameters(JsonElement value, ref List<string> parameters)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var values = new List<string>();
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string text = item.GetString() ?? string.Empty;
                    if (text.Length > 0) values.Add(text);
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    string name = string.Empty, label = string.Empty, type = string.Empty;
                    foreach (JsonProperty property in item.EnumerateObject())
                    {
                        if (property.Name == "name") name = StringValue(property.Value, name);
                        else if (property.Name == "label") label = StringValue(property.Value, label);
                        else if (property.Name == "type") type = StringValue(property.Value, type);
                    }
                    if (name.Length > 0) values.Add(name);
                    else if (label.Length > 0) values.Add(label);
                    else if (type.Length > 0) values.Add(type);
                }
            }
            parameters = values;
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            parameters = SplitSignature(value.GetString() ?? string.Empty);
        }
    }

    /// <summary>Splits a parameter signature such as <c>a, b = f(1, 2), "x,y"</c> at commas outside brackets and quotes; parts are trimmed, empty ones dropped. As in the C++, a last part inside an unclosed bracket or quote is dropped.</summary>
    /// <param name="signature">The signature text.</param>
    /// <returns>The parameters.</returns>
    public static List<string> SplitSignature(string signature)
    {
        var values = new List<string>();
        int start = 0;
        int nested = 0;
        char quote = '\0';
        bool escaped = false;
        for (int index = 0; index <= signature.Length; ++index)
        {
            char character = index < signature.Length ? signature[index] : ',';
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == quote) quote = '\0';
                continue;
            }
            if (character == '"' || character == '\'') { quote = character; continue; }
            if (character is '(' or '[' or '{') ++nested;
            else if (character is ')' or ']' or '}') nested = Math.Max(0, nested - 1);
            else if (character == ',' && nested == 0)
            {
                string part = signature.Substring(start, index - start).Trim(' ', '\t', '\r', '\n');
                if (part.Length > 0) values.Add(part);
                start = index + 1;
            }
        }
        return values;
    }
}
