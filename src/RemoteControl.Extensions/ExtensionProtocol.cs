using System.Text;
using System.Text.Json;

namespace RemoteControl.Extensions;

/// <summary>
/// The JSON-lines protocol between the program and an extension runtime: one JSON object per line, UTF-8, ended by <c>\n</c>, on the
/// runtime's standard input (program to extension) and standard output (extension to program). Standard error is plain text.
/// </summary>
/// <remarks>
/// <para><b>Program to extension</b> (every message the C++ sent, <c>TExtensions.cpp</c>):</para>
/// <list type="table">
/// <item><term><see cref="StartMessage"/></term><description><c>{"type":"start","protocol":"json-lines"}</c>: sent right after an ordinary runtime
/// was launched (not to the GS2Engine host).</description></item>
/// <item><term><see cref="StopMessage"/></term><description><c>{"type":"stop"}</c>: asks an ordinary runtime to end; its standard input is closed
/// afterwards and it is killed when it has not ended after the stop timeout.</description></item>
/// <item><term><see cref="HostStartRequest"/></term><description><c>{"id":"1","method":"start","params":{"extensionId":..., "entry":&lt;absolute path&gt;}}</c>
/// or with <c>"packageRoot":&lt;absolute folder&gt;</c> for automatic discovery: sent to the GS2Engine host once, after its first
/// <c>{"type":"ready"}</c> line.</description></item>
/// <item><term><see cref="HostStopRequest"/></term><description><c>{"id":"2","method":"stop"}</c>: first of the two messages that end the GS2Engine host.</description></item>
/// <item><term><see cref="HostShutdownRequest"/></term><description><c>{"id":"3","method":"shutdown"}</c>: second of them.</description></item>
/// <item><term><see cref="WindowActionRequest"/></term><description><c>{"id":"window-action-&lt;n&gt;","method":"window/action","params":{"action":&lt;id&gt;,"context":{"windowId","kind","title","scriptType","scriptName","text","selection"}}}</c>:
/// a button of the extension was pressed in a host window. The C++ sent this without the closing quote of <c>selection</c> (D-20); it is valid JSON here.</description></item>
/// </list>
/// <para><b>Extension to program</b>: every standard-output line is offered to <see cref="DisplayLine"/>; the shown text goes to the log and,
/// when enabled, to the output tab. The only message the program acts on is <c>{"type":"ready"}</c> from the GS2Engine host
/// (<see cref="IsReady"/>). Replies with an <c>id</c> are not matched to requests (neither did the C++).</para>
/// </remarks>
public static class ExtensionProtocol
{
    /// <summary>The line the C++ sent to an ordinary runtime right after launching it.</summary>
    public const string StartMessage = "{\"type\":\"start\",\"protocol\":\"json-lines\"}";

    /// <summary>The line asking an ordinary runtime to stop.</summary>
    public const string StopMessage = "{\"type\":\"stop\"}";

    /// <summary>The first of the two lines that stop the GS2Engine host.</summary>
    public const string HostStopRequest = "{\"id\":\"2\",\"method\":\"stop\"}";

    /// <summary>The second of the two lines that stop the GS2Engine host.</summary>
    public const string HostShutdownRequest = "{\"id\":\"3\",\"method\":\"shutdown\"}";

    /// <summary>The <c>type</c> values of protocol-only envelopes that are not shown when they carry no display text.</summary>
    public static IReadOnlyList<string> SilentTypes { get; } = new[] { "ready", "status", "response" };

    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 64 };
    private static long _windowActionCounter;

    /// <summary>Builds the start request for the GS2Engine host.</summary>
    /// <param name="manifest">The package.</param>
    /// <returns>One JSON line without the line break.</returns>
    public static string HostStartRequest(ExtensionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("id", "1");
            writer.WriteString("method", "start");
            writer.WriteStartObject("params");
            writer.WriteString("extensionId", JsonText.Clean(manifest.Id));
            if (!manifest.AutoDiscover && manifest.EntryPath.Length > 0) writer.WriteString("entry", JsonText.Clean(Path.GetFullPath(manifest.EntryPath)));
            else if (manifest.AutoDiscover) writer.WriteString("packageRoot", JsonText.Clean(Path.GetFullPath(manifest.Directory)));
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    /// <summary>Builds a <c>window/action</c> request with a new id <c>window-action-&lt;n&gt;</c>.</summary>
    /// <param name="actionId">The action id of the manifest.</param>
    /// <param name="context">The window the action was pressed in.</param>
    /// <returns>One JSON line without the line break.</returns>
    public static string WindowActionRequest(string actionId, ExtensionWindowContext context)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        ArgumentNullException.ThrowIfNull(context);
        long number = Interlocked.Increment(ref _windowActionCounter);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("id", "window-action-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("method", "window/action");
            writer.WriteStartObject("params");
            writer.WriteString("action", JsonText.Clean(actionId));
            writer.WriteStartObject("context");
            writer.WriteString("windowId", JsonText.Clean(context.WindowId));
            writer.WriteString("kind", JsonText.Clean(context.Kind));
            writer.WriteString("title", JsonText.Clean(context.Title));
            writer.WriteString("scriptType", JsonText.Clean(context.ScriptType));
            writer.WriteString("scriptName", JsonText.Clean(context.ScriptName));
            writer.WriteString("text", JsonText.Clean(context.Text));
            writer.WriteString("selection", JsonText.Clean(context.Selection));
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    /// <summary>Whether a standard-output line is the GS2Engine host's <c>{"type":"ready"}</c>.</summary>
    /// <param name="line">One line.</param>
    /// <returns><c>true</c> for a JSON object whose <c>type</c> is <c>ready</c>.</returns>
    public static bool IsReady(string line) => TryParseObject(line, out JsonDocument? document) && Dispose(document, TopString(document.RootElement, "type") == "ready");

    /// <summary>
    /// The text shown for one standard-output line (<c>extensionDisplayLine</c>, <c>TExtensions.cpp:204-255</c>): the first non-empty
    /// string <c>display</c>, else <c>text</c>, else <c>message</c> (searched in document order at any depth, as the C++ did); nothing
    /// for a <c>ready</c>, <c>status</c> or <c>response</c> envelope without them; any other object as <c>key=value, ...</c> (nested
    /// objects two levels deep in braces); a line that is not a JSON object as it is (trimmed).
    /// </summary>
    /// <param name="line">One line of standard output.</param>
    /// <returns>The text to show, or empty to show nothing.</returns>
    public static string DisplayLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string trimmed = ExtensionManifestLoader.Trim(line);
        if (!TryParseObject(trimmed, out JsonDocument? document)) return trimmed;
        using (document)
        {
            JsonElement root = document.RootElement;
            foreach (string key in new[] { "display", "text", "message" })
            {
                string? found = FindString(root, key, 0);
                if (!string.IsNullOrEmpty(found)) return found;
            }
            if (SilentTypes.Contains(TopString(root, "type"))) return string.Empty;
            string summary = Summarize(root, 0);
            return summary.Length == 0 ? trimmed : summary;
        }
    }

    /// <summary>
    /// Appends a line to a bounded log (<c>appendExtensionLog</c>): lines are separated by <c>\n</c>; when the log is longer than
    /// <paramref name="limit"/> characters its oldest part is cut off.
    /// </summary>
    /// <param name="log">The log text.</param>
    /// <param name="line">The new line; an empty line adds nothing.</param>
    /// <param name="limit">The most characters kept.</param>
    /// <returns>The new log text.</returns>
    public static string AppendLog(string log, string line, int limit = ExtensionManagerOptions.DefaultLogLimit)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(line);
        string result = line.Length == 0 ? log : log.Length == 0 ? line : log + "\n" + line;
        if (limit < 0) limit = 0;
        if (result.Length <= limit) return result;
        int start = result.Length - limit;
        if (start < result.Length && char.IsLowSurrogate(result[start])) start++;
        return result[start..];
    }

    private static bool Dispose(JsonDocument document, bool value)
    {
        document.Dispose();
        return value;
    }

    private static bool TryParseObject(string line, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonDocument? document)
    {
        document = null;
        string trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return false;
        try
        {
            document = JsonDocument.Parse(trimmed, ReadOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        if (document.RootElement.ValueKind == JsonValueKind.Object) return true;
        document.Dispose();
        document = null;
        return false;
    }

    private static string TopString(JsonElement element, string key) =>
        element.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string? FindString(JsonElement element, string key, int depth)
    {
        if (depth > 32) return null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals(key) && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } text) return text;
                string? nested = FindString(property.Value, key, depth + 1);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? nested = FindString(item, key, depth + 1);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static string Summarize(JsonElement element, int depth)
    {
        var text = new StringBuilder();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string value = ValueText(property.Value, depth + 1);
            if (property.Name.Length == 0 || value.Length == 0) continue;
            if (text.Length > 0) text.Append(", ");
            text.Append(property.Name).Append('=').Append(value);
        }
        return text.ToString();
    }

    private static string ValueText(JsonElement value, int depth) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Object when depth <= 2 => "{" + Summarize(value, depth) + "}",
        _ => value.GetRawText().Trim(),
    };

    private static string Write(Action<Utf8JsonWriter> write) => JsonText.Write(write);
}
