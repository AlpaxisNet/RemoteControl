using System.Text;

namespace RemoteControl.Protocol.Wire;

/// <summary>
/// The "comma text" of the Graal protocol (RClib <c>gtokenize</c> and <c>gtokenizeReverse</c>): lines joined with commas, a line quoted
/// when it needs it. Both directions keep RClib's quirks byte for byte, because servers store and return this text:
/// <list type="bullet">
/// <item>every carriage return is removed before tokenizing;</item>
/// <item>a text that is empty or ends with a newline gets an empty last field, so <c>"a\n"</c> becomes <c>"a,"</c>;</item>
/// <item>un-tokenizing drops a trailing empty field, so <c>Untokenize(Tokenize("a\n"))</c> is <c>"a"</c> (one trailing newline is lost);</item>
/// <item>an unterminated quote swallows the rest of the text.</item>
/// </list>
/// One deliberate difference: a line that contains a double quote or a backslash is always quoted (RClib quoted it only for the other
/// reasons), because both RClib's reader and the Alpaxis server's reader corrupt such a line when it is not.
/// </summary>
public static class CommaText
{
    /// <summary>Tokenizes lines into comma text.</summary>
    /// <param name="text">The text; lines are separated by <c>\n</c>.</param>
    /// <returns>The comma text.</returns>
    public static string Tokenize(string? text)
    {
        string cleaned = (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal);
        var lines = new List<string>();
        int start = 0;
        while (start < cleaned.Length)
        {
            int end = cleaned.IndexOf('\n', start);
            if (end < 0)
            {
                lines.Add(cleaned[start..]);
                break;
            }

            lines.Add(cleaned[start..end]);
            start = end + 1;
        }

        if (cleaned.Length == 0 || cleaned[^1] == '\n') lines.Add(string.Empty);

        var output = new StringBuilder(cleaned.Length + lines.Count * 3);
        for (int index = 0; index < lines.Count; index++)
        {
            if (index > 0) output.Append(',');
            string line = lines[index];
            if (line.Length == 0) continue;
            if (NeedsQuotes(line))
            {
                output.Append('"');
                foreach (char c in line)
                {
                    if (c == '\\') output.Append("\\\\");
                    else if (c == '"') output.Append("\"\"");
                    else output.Append(c);
                }

                output.Append('"');
            }
            else
            {
                output.Append(line);
            }
        }

        return output.ToString();
    }

    /// <summary>Tokenizes a list of lines (joined with <c>\n</c> first).</summary>
    /// <param name="lines">The lines.</param>
    /// <returns>The comma text.</returns>
    public static string Tokenize(IEnumerable<string> lines) => Tokenize(string.Join('\n', lines));

    /// <summary>Turns comma text back into lines joined with <c>\n</c>.</summary>
    /// <param name="content">The comma text.</param>
    /// <returns>The text.</returns>
    public static string Untokenize(string? content) => string.Join('\n', Split(content));

    /// <summary>Turns comma text into its fields.</summary>
    /// <param name="content">The comma text.</param>
    /// <returns>The fields, without a trailing empty field.</returns>
    public static IReadOnlyList<string> Split(string? content)
    {
        var output = new List<string>();
        if (string.IsNullOrEmpty(content)) return output;
        int length = content.Length;
        bool insideQuotes = false;
        bool lineQuoted = false;
        int position = 0;
        int i = 0;
        while (i < length)
        {
            char c = content[i];
            if (c == '"')
            {
                if (insideQuotes && i + 1 < length && content[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                lineQuoted = true;
                insideQuotes = !insideQuotes;
            }

            if (!insideQuotes && (c == ',' || i + 1 == length))
            {
                int lineStart = position + (lineQuoted ? 1 : 0);
                int lineLength = i - position - (lineQuoted ? 2 : 0) + (i + 1 == length && c != ',' ? 1 : 0);
                string line;
                if (lineStart >= length) line = string.Empty;
                else if (lineLength < 0 || lineStart + lineLength > length) line = content[lineStart..];
                else line = content.Substring(lineStart, lineLength);
                if (lineQuoted)
                {
                    line = line.Replace("\"\"", "\"", StringComparison.Ordinal)
                        .Replace("\\\\", "\\", StringComparison.Ordinal)
                        .Replace("\n", string.Empty, StringComparison.Ordinal)
                        .Replace("\r", string.Empty, StringComparison.Ordinal);
                }

                output.Add(line);
                position = i + 1;
                lineQuoted = false;
            }

            i++;
        }

        return output;
    }

    private static bool NeedsQuotes(string line)
    {
        if (line[0] == '"') return true;
        bool allSpaces = true;
        foreach (char c in line)
        {
            // RClib compares a signed char, so every byte of 0x80 or more needs quotes too. Unlike RClib, a line with a quote or a
            // backslash anywhere is quoted as well: unquoted, RClib's own reader mangled the quote, and the Alpaxis reader drops a
            // single backslash (so "echo(\"a\\n\");" lost its backslash on the server).
            if (c < 33 || c > 126 || c == ',' || c == '/' || c == '"' || c == '\\') return true;
            if (c != ' ') allSpaces = false;
        }

        return allSpaces;
    }
}
