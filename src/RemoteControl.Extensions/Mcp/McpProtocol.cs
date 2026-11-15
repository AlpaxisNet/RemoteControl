using System.Collections.Immutable;
using System.Text;

namespace RemoteControl.Extensions;

/// <summary>
/// The text helpers of the MCP server (<c>TMcpProtocol.cpp</c>): JSON field lookups on raw request text, bounded line ranges and
/// searches of an editor buffer, the guarded replace, and the score that picks a running program.
/// </summary>
public static class McpProtocol
{
    /// <summary>The most lines a line range or a search returns (and the most matches a search counts).</summary>
    public const int MaximumLines = 200;

    /// <summary>The most bytes (UTF-8) of a line range or search result, and of the expected and replacement texts of a guarded replace.</summary>
    public const int MaximumOutput = 65536;

    /// <summary>The views <c>rc_open_window</c> accepts (<c>TRemoteFrame::mcpOpenView</c>).</summary>
    public static ImmutableArray<string> Views { get; } = ImmutableArray.Create("player_list", "file_browser", "classes", "weapons", "npcs", "server_options", "server_flags", "folder_config", "toalls", "accounts", "options");

    /// <summary>
    /// Finds the string value of the first <c>"key":</c> anywhere in <paramref name="json"/> (at any depth, as <c>mcpJsonStringField</c>)
    /// and decodes it with full escape validation (<c>\uXXXX</c> with surrogate pairs; a lone surrogate, a bad escape or a raw control
    /// character fails).
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The decoded value.</param>
    /// <returns><c>true</c> when the key was found and its value is a valid string.</returns>
    public static bool TryGetStringField(string json, string key, out string value)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(key);
        value = string.Empty;
        if (!ValuePosition(json, key, out int position)) return false;
        if (!ReadString(json, ref position, out string result)) return false;
        value = result;
        return true;
    }

    /// <summary>The boolean value of the first <c>"key":</c> anywhere in <paramref name="json"/> (<c>mcpJsonBoolField</c>).</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="key">The key.</param>
    /// <param name="fallback">The value when the key is missing or not <c>true</c>/<c>false</c>.</param>
    /// <returns>The value.</returns>
    public static bool GetBoolField(string json, string key, bool fallback)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!ValuePosition(json, key, out int position)) return fallback;
        if (string.CompareOrdinal(json, position, "true", 0, 4) == 0) return true;
        if (string.CompareOrdinal(json, position, "false", 0, 5) == 0) return false;
        return fallback;
    }

    /// <summary>The integer value (its leading digits) of the first <c>"key":</c> anywhere in <paramref name="json"/> (<c>mcpJsonIntField</c>).</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="key">The key.</param>
    /// <param name="fallback">The value when the key is missing or its value does not start with a digit (after an optional minus).</param>
    /// <returns>The value, limited to the range of <see cref="int"/>.</returns>
    public static int GetIntField(string json, string key, int fallback)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!ValuePosition(json, key, out int position)) return fallback;
        bool negative = position < json.Length && json[position] == '-';
        if (negative) position++;
        if (position >= json.Length || !char.IsAsciiDigit(json[position])) return fallback;
        long result = 0;
        while (position < json.Length && char.IsAsciiDigit(json[position]))
        {
            result = Math.Min(result * 10 + (json[position++] - '0'), (long)int.MaxValue + 1);
        }
        result = negative ? -result : result;
        return (int)Math.Clamp(result, int.MinValue, int.MaxValue);
    }

    /// <summary>
    /// Lines <paramref name="startLine"/> to <paramref name="endLine"/> (1-based, at most <see cref="MaximumLines"/> and
    /// <see cref="MaximumOutput"/> bytes) as <c>N: line\n</c> (<c>mcpEditorLineRange</c>).
    /// </summary>
    /// <param name="text">The buffer.</param>
    /// <param name="startLine">The first line (at least 1).</param>
    /// <param name="endLine">The last line.</param>
    /// <returns>The numbered lines, or empty when the start is past the end.</returns>
    public static string EditorLineRange(string text, int startLine, int endLine)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] source = Lines(text);
        startLine = Math.Max(1, startLine);
        endLine = Math.Max(startLine, endLine);
        endLine = (int)Math.Min(endLine, (long)startLine + MaximumLines - 1);
        if (startLine > source.Length) return string.Empty;
        endLine = Math.Min(endLine, source.Length);
        var result = new BoundedText();
        for (int line = startLine; line <= endLine; line++) result.AppendLine(line, source[line - 1]);
        return result.ToString();
    }

    /// <summary>
    /// The lines containing <paramref name="query"/> with <paramref name="contextLines"/> lines around them (0 to 20), numbered as
    /// <c>N: line\n</c>, separate groups divided by <c>--\n</c>; at most <see cref="MaximumLines"/> matches and lines and
    /// <see cref="MaximumOutput"/> bytes (<c>mcpEditorSearch</c>). Without <paramref name="caseSensitive"/> case is ignored (ordinal).
    /// </summary>
    /// <param name="text">The buffer.</param>
    /// <param name="query">The text to find; empty finds nothing.</param>
    /// <param name="caseSensitive">Whether case matters.</param>
    /// <param name="contextLines">Lines before and after each match.</param>
    /// <returns>The numbered lines, or empty.</returns>
    public static string EditorSearch(string text, string query, bool caseSensitive, int contextLines)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0) return string.Empty;
        string[] source = Lines(text);
        StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        contextLines = Math.Clamp(contextLines, 0, 20);
        var included = new bool[source.Length];
        int matches = 0;
        for (int index = 0; index < source.Length && matches < MaximumLines; index++)
        {
            if (source[index].IndexOf(query, comparison) < 0) continue;
            matches++;
            int first = Math.Max(0, index - contextLines);
            int last = Math.Min(source.Length - 1, index + contextLines);
            for (int line = first; line <= last; line++) included[line] = true;
        }
        var result = new BoundedText();
        int emitted = 0;
        int previous = -2;
        for (int index = 0; index < included.Length && emitted < MaximumLines && !result.Full; index++)
        {
            if (!included[index]) continue;
            if (previous >= 0 && index != previous + 1) result.AppendRaw("--\n");
            result.AppendLine(index + 1, source[index]);
            previous = index;
            emitted++;
        }
        return result.ToString();
    }

    /// <summary>
    /// Replaces the one occurrence of <paramref name="expected"/> in <paramref name="text"/> (<c>mcpGuardedReplace</c>): the expected text
    /// must be non-empty, both texts at most <see cref="MaximumOutput"/> bytes, and the expected text must occur exactly once.
    /// </summary>
    /// <param name="text">The buffer.</param>
    /// <param name="expected">The text to replace.</param>
    /// <param name="replacement">The new text.</param>
    /// <param name="result">The new buffer.</param>
    /// <param name="error">Why not.</param>
    /// <returns><c>true</c> when replaced.</returns>
    public static bool TryGuardedReplace(string text, string expected, string replacement, out string result, out string error)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        result = string.Empty;
        error = string.Empty;
        if (expected.Length == 0) { error = "Expected text must not be empty"; return false; }
        if (Encoding.UTF8.GetByteCount(expected) > MaximumOutput || Encoding.UTF8.GetByteCount(replacement) > MaximumOutput) { error = "Partial edit is limited to 65536 bytes"; return false; }
        int found = text.IndexOf(expected, StringComparison.Ordinal);
        if (found < 0) { error = "Expected text was not found in the current buffer"; return false; }
        if (text.IndexOf(expected, found + expected.Length, StringComparison.Ordinal) >= 0) { error = "Expected text is not unique in the current buffer"; return false; }
        result = string.Concat(text.AsSpan(0, found), replacement, text.AsSpan(found + expected.Length));
        return true;
    }

    /// <summary>
    /// The score used to pick a running program when several run (<c>mcpInstanceScore</c>): open script editors weigh most
    /// (10000 plus their number up to 100), then an active window (1000), then a connected server (100).
    /// </summary>
    /// <param name="active">Whether one of its windows is active.</param>
    /// <param name="connected">Whether it is connected to a server.</param>
    /// <param name="editorCount">How many script editors are open.</param>
    /// <returns>The score; higher wins.</returns>
    public static int InstanceScore(bool active, bool connected, int editorCount) =>
        (editorCount > 0 ? 10000 + Math.Min(editorCount, 100) : 0) + (active ? 1000 : 0) + (connected ? 100 : 0);

    /// <summary>The script type of an editor name for the saved approvals: <c>Weapon</c>, <c>Class</c>, <c>NPC</c> or empty.</summary>
    /// <param name="editorName">The editor name (window title), such as <c>Weapon: bow</c>.</param>
    /// <returns>The type.</returns>
    public static string ScriptTypeOf(string editorName)
    {
        ArgumentNullException.ThrowIfNull(editorName);
        if (editorName.StartsWith("Weapon/GUI Script:", StringComparison.Ordinal) || editorName.StartsWith("Weapon:", StringComparison.Ordinal)) return "Weapon";
        if (editorName.StartsWith("Class:", StringComparison.Ordinal)) return "Class";
        if (editorName.StartsWith("NPC:", StringComparison.Ordinal) || editorName.StartsWith("Npcscript:", StringComparison.Ordinal)) return "NPC";
        return string.Empty;
    }

    private static string[] Lines(string text)
    {
        string[] lines = text.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].EndsWith('\r')) lines[index] = lines[index][..^1];
        }
        return lines;
    }

    private static bool ValuePosition(string json, string key, out int value)
    {
        value = 0;
        int position = 0;
        while (position < json.Length)
        {
            if (json[position] != '"') { position++; continue; }
            if (!ReadString(json, ref position, out string candidate)) return false;
            int separator = position;
            while (separator < json.Length && char.IsWhiteSpace(json[separator])) separator++;
            if (separator >= json.Length || json[separator] != ':') continue;
            value = separator + 1;
            while (value < json.Length && char.IsWhiteSpace(json[value])) value++;
            if (string.Equals(candidate, key, StringComparison.Ordinal)) return true;
            position = value;
        }
        return false;
    }

    private static bool ReadString(string json, ref int position, out string result)
    {
        result = string.Empty;
        if (position >= json.Length || json[position++] != '"') return false;
        var text = new StringBuilder();
        while (position < json.Length)
        {
            char value = json[position++];
            if (value == '"') { result = text.ToString(); return true; }
            if (value < 0x20) return false;
            if (value != '\\') { text.Append(value); continue; }
            if (position >= json.Length) return false;
            char escape = json[position++];
            switch (escape)
            {
                case '"': case '\\': case '/': text.Append(escape); break;
                case 'b': text.Append('\b'); break;
                case 'f': text.Append('\f'); break;
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case 'u':
                    if (!ReadHex(json, ref position, out int codepoint)) return false;
                    if (codepoint is >= 0xd800 and <= 0xdbff)
                    {
                        if (position + 2 > json.Length || json[position++] != '\\' || json[position++] != 'u') return false;
                        if (!ReadHex(json, ref position, out int low) || low is < 0xdc00 or > 0xdfff) return false;
                        text.Append((char)codepoint).Append((char)low);
                    }
                    else if (codepoint is >= 0xdc00 and <= 0xdfff) return false;
                    else text.Append((char)codepoint);
                    break;
                default:
                    return false;
            }
        }
        return false;
    }

    private static bool ReadHex(string json, ref int position, out int value)
    {
        value = 0;
        if (position + 4 > json.Length) return false;
        for (int index = 0; index < 4; index++)
        {
            int digit = HexValue(json[position++]);
            if (digit < 0) return false;
            value = value * 16 + digit;
        }
        return true;
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };

    /// <summary>Text with a bound in UTF-8 bytes, cut at a character boundary (the C++ could cut inside a UTF-8 sequence).</summary>
    private sealed class BoundedText
    {
        private readonly StringBuilder _text = new();
        private int _bytes;

        public bool Full => _bytes >= MaximumOutput;

        public void AppendLine(int number, string line) => AppendRaw(number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " + line + "\n");

        public void AppendRaw(string value)
        {
            if (Full) return;
            int room = MaximumOutput - _bytes;
            int count = Encoding.UTF8.GetByteCount(value);
            if (count <= room)
            {
                _text.Append(value);
                _bytes += count;
                return;
            }
            int used = 0;
            int index = 0;
            while (index < value.Length)
            {
                int width = char.IsHighSurrogate(value[index]) && index + 1 < value.Length ? 2 : 1;
                int size = Encoding.UTF8.GetByteCount(value.AsSpan(index, width));
                if (used + size > room) break;
                used += size;
                index += width;
            }
            _text.Append(value, 0, index);
            _bytes = MaximumOutput;
        }

        public override string ToString() => _text.ToString();
    }
}
