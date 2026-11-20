using System.Globalization;
using System.Text.Json;

namespace RemoteControl.Extensions;

/// <summary>Reads tool arguments from a JSON object; a missing key or a value of another type gives the fallback.</summary>
internal readonly struct McpArguments
{
    private readonly JsonElement _element;

    public McpArguments(JsonElement element) => _element = element;

    /// <summary>The <c>name</c> of <c>tools/call</c> params.</summary>
    public string Name => String("name");

    /// <summary>The <c>arguments</c> object of <c>tools/call</c> params, or the params themselves.</summary>
    public McpArguments ToolArguments =>
        _element.ValueKind == JsonValueKind.Object && _element.TryGetProperty("arguments", out JsonElement value) && value.ValueKind == JsonValueKind.Object ? new McpArguments(value) : this;

    /// <summary>Whether the key is there with a value other than <c>null</c>.</summary>
    public bool Has(string key) => TryGet(key, out JsonElement item) && item.ValueKind != JsonValueKind.Null;

    public bool TryString(string key, out string value)
    {
        value = string.Empty;
        if (!TryGet(key, out JsonElement item) || item.ValueKind != JsonValueKind.String) return false;
        string? text = Text(item);
        if (text is null) return false;
        value = text;
        return true;
    }

    /// <summary>Whether every property name and string of a JSON value can be read as a .NET string (none holds an escaped lone surrogate).</summary>
    internal static bool IsWellFormed(JsonElement element)
    {
        try
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        _ = property.Name;
                        if (!IsWellFormed(property.Value)) return false;
                    }
                    return true;
                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                        if (!IsWellFormed(item)) return false;
                    return true;
                case JsonValueKind.String:
                    _ = element.GetString();
                    return true;
                default:
                    return true;
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The string of a JSON string, or <c>null</c> when it cannot be one (an escaped lone surrogate makes GetString throw).</summary>
    internal static string? Text(JsonElement item)
    {
        try
        {
            return item.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public string String(string key) => TryString(key, out string value) ? value : string.Empty;

    /// <summary>A string that must be given and not empty.</summary>
    /// <exception cref="McpToolException">It is missing or empty.</exception>
    public string RequiredString(string key)
    {
        if (!TryString(key, out string value) || value.Length == 0) throw new McpToolException($"The argument \"{key}\" is required (a non-empty string).");
        return value;
    }

    public bool TryInt(string key, out int value)
    {
        value = 0;
        if (!TryGet(key, out JsonElement item)) return false;
        if (item.ValueKind == JsonValueKind.String) return int.TryParse(Text(item), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        if (item.ValueKind != JsonValueKind.Number) return false;
        if (item.TryGetInt32(out value)) return true;
        if (!item.TryGetDouble(out double number)) return false;
        value = (int)Math.Clamp(Math.Truncate(number), int.MinValue, int.MaxValue);
        return true;
    }

    public int Int(string key, int fallback) => TryInt(key, out int value) ? value : fallback;

    public bool TryDouble(string key, out double value)
    {
        value = 0;
        if (!TryGet(key, out JsonElement item)) return false;
        if (item.ValueKind == JsonValueKind.String) return double.TryParse(Text(item), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        return item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out value) && double.IsFinite(value);
    }

    public bool Bool(string key, bool fallback)
    {
        if (!TryGet(key, out JsonElement item)) return fallback;
        return item.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback };
    }

    /// <summary>A list of strings: an array of strings, or one string; other values are skipped.</summary>
    public IReadOnlyList<string> Strings(string key)
    {
        if (!TryGet(key, out JsonElement item)) return Array.Empty<string>();
        if (item.ValueKind == JsonValueKind.String) return Text(item) is { } one ? new[] { one } : Array.Empty<string>();
        if (item.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var values = new List<string>();
        foreach (JsonElement value in item.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String && Text(value) is { } text) values.Add(text);
        return values;
    }

    /// <summary>A list of integers: an array of numbers, or one number; other values are skipped.</summary>
    public IReadOnlyList<int> Ints(string key)
    {
        if (!TryGet(key, out JsonElement item)) return Array.Empty<int>();
        if (item.ValueKind is JsonValueKind.Number or JsonValueKind.String) return TryInt(key, out int one) ? new[] { one } : Array.Empty<int>();
        if (item.ValueKind != JsonValueKind.Array) return Array.Empty<int>();
        var values = new List<int>();
        foreach (JsonElement value in item.EnumerateArray())
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) values.Add(number);
        return values;
    }

    /// <summary>The objects of an array (a single object counts as one).</summary>
    public IReadOnlyList<McpArguments> Objects(string key)
    {
        if (!TryGet(key, out JsonElement item)) return Array.Empty<McpArguments>();
        if (item.ValueKind == JsonValueKind.Object) return new[] { new McpArguments(item) };
        if (item.ValueKind != JsonValueKind.Array) return Array.Empty<McpArguments>();
        return item.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object).Select(value => new McpArguments(value)).ToArray();
    }

    /// <summary>The string values of an object (<c>{"key": "value"}</c>); numbers and booleans are taken as their text.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> StringMap(string key)
    {
        if (!TryGet(key, out JsonElement item) || item.ValueKind != JsonValueKind.Object) return Array.Empty<KeyValuePair<string, string>>();
        var values = new List<KeyValuePair<string, string>>();
        foreach (JsonProperty property in item.EnumerateObject())
        {
            string? text = property.Value.ValueKind switch
            {
                JsonValueKind.String => Text(property.Value),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
            if (text is not null) values.Add(new KeyValuePair<string, string>(property.Name, text));
        }
        return values;
    }

    private bool TryGet(string key, out JsonElement item)
    {
        item = default;
        return _element.ValueKind == JsonValueKind.Object && _element.TryGetProperty(key, out item);
    }
}

/// <summary>A tool failed with a message for the MCP client (a tool result with <c>isError</c>).</summary>
internal sealed class McpToolException : Exception
{
    public McpToolException(string message) : base(message)
    {
    }
}

/// <summary>The session a tool uses is outside <c>mcpserverscope</c> (the JSON-RPC error -32003).</summary>
internal sealed class McpScopeException : Exception
{
    public McpScopeException() : base("Current server is outside configured MCP server scope")
    {
    }
}

/// <summary>The user denied the approval of a tool (the JSON-RPC error -32002).</summary>
internal sealed class McpDeniedException : Exception
{
    public McpDeniedException() : base("Mutation denied by user")
    {
    }
}
