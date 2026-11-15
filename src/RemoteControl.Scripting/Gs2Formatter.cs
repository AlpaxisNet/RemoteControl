using System.Text;

namespace RemoteControl.Scripting;

/// <summary>The formatter options (the C++ <c>format*</c> and <c>remove*</c> options).</summary>
public sealed record FormatterOptions
{
    /// <summary>The defaults of the options file: 2 spaces, trim, remove both comment kinds, keep <c>//#CLIENTSIDE</c>.</summary>
    public static FormatterOptions Default { get; } = new();

    /// <summary>Spaces per indent level (<c>formatindentwidth</c>); clamped to 1..16 when used.</summary>
    public int IndentWidth { get; init; } = 2;

    /// <summary>Indent with one tab per level instead of spaces (<c>formatusetabs</c>).</summary>
    public bool UseTabs { get; init; }

    /// <summary>Remove spaces and tabs at the end of non-blank lines (<c>formattrimtrailing</c>).</summary>
    public bool TrimTrailingWhitespace { get; init; } = true;

    /// <summary>Remove <c>//</c> comments (<c>removelinecomments</c>).</summary>
    public bool RemoveLineComments { get; init; } = true;

    /// <summary>Remove <c>/* */</c> comments, keeping their line breaks (<c>removeblockcomments</c>).</summary>
    public bool RemoveBlockComments { get; init; } = true;

    /// <summary>Keep a line that is only <c>//#CLIENTSIDE</c> when line comments are removed (<c>preserveclientside</c>).</summary>
    public bool PreserveClientside { get; init; } = true;
}

/// <summary>
/// The Format button (port of <c>TEditorFormat.h</c>): comment removal, then re-indentation by brace depth. Blank lines are left
/// exactly as they are, other lines lose their leading spaces and tabs and get depth × width spaces (or depth tabs), where a line
/// that starts with closing braces is outdented by their number. Formatting a formatted text changes nothing.
/// </summary>
/// <remarks>
/// One deliberate difference from the C++: a carriage return before a line feed is treated as part of the line break, so CRLF
/// text keeps its line endings, its blank lines stay blank and its trailing whitespace is trimmed (the C++ treated the '\r' as
/// line content, indented "\r"-only lines and could not trim before it).
/// </remarks>
public static class Gs2Formatter
{
    /// <summary>Formats a script.</summary>
    /// <param name="text">The text; null counts as empty.</param>
    /// <param name="options">The options; null means <see cref="FormatterOptions.Default"/>.</param>
    /// <returns>The formatted text.</returns>
    public static string Format(string? text, FormatterOptions? options = null)
    {
        options ??= FormatterOptions.Default;
        return Reindent(RemoveComments(text ?? string.Empty, options), options);
    }

    /// <summary>The comment removal step alone (C++ <c>removeGraalScriptComments</c>).</summary>
    /// <param name="text">The text.</param>
    /// <param name="options">Which comments to remove.</param>
    /// <returns>The text without the selected comments.</returns>
    public static string RemoveComments(string text, FormatterOptions options)
    {
        var result = new StringBuilder(text.Length);
        char quote = '\0';
        bool escaped = false;
        bool inBlockComment = false;
        for (int index = 0; index < text.Length; ++index)
        {
            char character = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';
            if (inBlockComment)
            {
                if (!options.RemoveBlockComments) result.Append(character);
                if (character == '*' && next == '/')
                {
                    if (!options.RemoveBlockComments) result.Append(next);
                    inBlockComment = false;
                    ++index;
                }
                else if (options.RemoveBlockComments && (character == '\n' || (character == '\r' && next == '\n'))) result.Append(character);
                continue;
            }
            if (quote != '\0')
            {
                result.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == quote) quote = '\0';
                continue;
            }
            if (character == '"' || character == '\'' || character == '`') { quote = character; result.Append(character); continue; }
            if (character == '/' && next == '*')
            {
                if (!options.RemoveBlockComments) result.Append("/*");
                inBlockComment = true;
                ++index;
                continue;
            }
            if (character == '/' && next == '/')
            {
                int lineStart = text.LastIndexOf('\n', index) + 1;
                int lineEnd = text.IndexOf('\n', index);
                int commentEnd = lineEnd < 0 ? text.Length : lineEnd;
                if (lineEnd > index + 1 && text[lineEnd - 1] == '\r') --commentEnd;
                bool keep = !options.RemoveLineComments
                    || (options.PreserveClientside && IsOnlyWhitespace(text, lineStart, index) && text.AsSpan(index, commentEnd - index).TrimEnd(" \t\r").SequenceEqual("//#CLIENTSIDE"));
                if (keep) result.Append(text, index, commentEnd - index);
                index = commentEnd - 1;
                continue;
            }
            result.Append(character);
        }
        return result.ToString();
    }

    private static bool IsOnlyWhitespace(string text, int start, int end)
    {
        for (int index = start; index < end; ++index)
        {
            if (text[index] is not (' ' or '\t' or '\r')) return false;
        }
        return true;
    }

    /// <summary>
    /// The re-indentation step alone (C++ <c>formatGraalScriptCode</c>). A pathological text whose indentation would grow the
    /// result beyond 32 Mi characters plus twice its own length (thousands of unclosed braces) is returned unchanged.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">Indent width, tabs and trimming.</param>
    /// <returns>The re-indented text.</returns>
    public static string Reindent(string text, FormatterOptions options)
    {
        int width = Math.Clamp(options.IndentWidth, 1, 16);
        long limit = 32L * 1024 * 1024 + 2L * text.Length;
        var result = new StringBuilder(text.Length + text.Length / 8);
        int depth = 0;
        bool inBlockComment = false;
        int lineStart = 0;
        while (true)
        {
            int newline = text.IndexOf('\n', lineStart);
            int lineEnd = newline < 0 ? text.Length : newline;
            int contentEnd = lineEnd;
            if (newline >= 0 && contentEnd > lineStart && text[contentEnd - 1] == '\r') --contentEnd;
            ReadOnlySpan<char> line = text.AsSpan(lineStart, contentEnd - lineStart);
            int first = 0;
            while (first < line.Length && (line[first] == ' ' || line[first] == '\t')) ++first;
            if (first == line.Length)
            {
                result.Append(line);
            }
            else
            {
                ReadOnlySpan<char> content = line[first..];
                if (options.TrimTrailingWhitespace) content = content.TrimEnd(" \t");
                int lineDepth = Math.Max(0, depth - LeadingClosures(content));
                if (options.UseTabs) result.Append('\t', lineDepth); else result.Append(' ', lineDepth * width);
                result.Append(content);
                depth = Math.Max(0, depth + BraceDelta(content, ref inBlockComment));
            }
            result.Append(text, contentEnd, lineEnd - contentEnd);
            if (result.Length > limit) return text;
            if (newline < 0) break;
            result.Append('\n');
            lineStart = newline + 1;
        }
        return result.ToString();
    }

    private static int LeadingClosures(ReadOnlySpan<char> content)
    {
        int count = 0;
        while (count < content.Length && content[count] == '}') ++count;
        return count;
    }

    private static int BraceDelta(ReadOnlySpan<char> line, ref bool inBlockComment)
    {
        int delta = 0;
        char quote = '\0';
        bool escaped = false;
        for (int index = 0; index < line.Length; ++index)
        {
            char character = line[index];
            char next = index + 1 < line.Length ? line[index + 1] : '\0';
            if (inBlockComment)
            {
                if (character == '*' && next == '/') { inBlockComment = false; ++index; }
                continue;
            }
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == quote) quote = '\0';
                continue;
            }
            if (character == '/' && next == '*') { inBlockComment = true; ++index; continue; }
            if (character == '/' && next == '/') break;
            if (character == '"' || character == '\'') { quote = character; continue; }
            if (character == '{') ++delta;
            else if (character == '}') --delta;
        }
        return delta;
    }
}
