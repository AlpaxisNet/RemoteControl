using System.Text;

namespace RemoteControl.Protocol.Wire;

/// <summary>
/// The NC's line lists as an RC sends them: a class script (<c>PLI_NC_CLASSADD</c>, after the class name), NPC flags
/// (<c>PLI_NC_NPCFLAGSSET</c>, after the NPC id) and the level list (<c>PLI_NC_LEVELLISTSET</c>). The Alpaxis game server reads what
/// follows as one line per GString when those bytes are an exact chain of GStrings (<see cref="IsExactGStringChain"/>), and as comma
/// text, with the original Graal rules, otherwise. So a list whose lines all fit in a GString (at most 223 bytes) can go as GStrings, as
/// servers that have not changed read it, and a list with a longer line goes as comma text that is never an exact chain
/// (<see cref="CommaText"/>): the server tells the two apart without a guess and without a flag.
/// </summary>
public static class LineListText
{
    /// <summary>
    /// Whether <paramref name="bytes"/> read as an exact chain of GStrings: a length byte (its value minus 32), then that many bytes, again
    /// and again, ending exactly at the end. A negative length, or one longer than what is left, ends the test with "no". No bytes at all
    /// are an exact chain (of no strings). This is the test of the RC's own reader (<c>ReadExactGStrings</c>), which the server uses too.
    /// </summary>
    /// <param name="bytes">The bytes after the packet's fixed fields.</param>
    /// <returns>Whether they are an exact chain.</returns>
    public static bool IsExactGStringChain(ReadOnlySpan<byte> bytes)
    {
        int position = 0;
        while (position < bytes.Length)
        {
            int length = GEncoding.DecodeGByte(bytes[position]);
            if (length < 0 || length > bytes.Length - position - 1) return false;
            position += 1 + length;
        }

        return true;
    }

    /// <summary>
    /// The lines as comma text that is not an exact GString chain. Carriage returns are dropped and the text is split on <c>\n</c> (a text
    /// that ends with a line break ends with an empty line). A line is written bare only when it is not empty, has no character below 33
    /// or above 126 and none of <c>, / " \</c>; any other line is put in double quotes, with every backslash and double quote doubled,
    /// and an empty line is <c>""</c>. While the result would still read as an exact chain, the bare lines are quoted too, the last first,
    /// as the game server does with what it sends. Quoting a line never changes what the server reads.
    /// </summary>
    /// <param name="text">The lines, separated by <c>\n</c>.</param>
    /// <returns>The comma text.</returns>
    /// <exception cref="PacketTooLongException">Even with every line quoted the bytes are an exact chain: rare, and nothing else can be done
    /// without changing the text, so it is refused (a blank line more or less at the end changes the bytes).</exception>
    public static string CommaText(string? text)
    {
        string[] lines = (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        bool[] quoted = lines.Select(NeedsQuotes).ToArray();
        int next = lines.Length - 1;
        while (true)
        {
            string result = Join(lines, quoted);
            if (!IsExactGStringChain(RcText.GetBytes(result))) return result;
            while (next >= 0 && quoted[next]) next--;
            if (next < 0)
                throw new PacketTooLongException("The text cannot be sent in a form the server reads without doubt: with every line in quotes it still reads as one length-prefixed string per line. Add or remove a blank line at the end and save again.");
            quoted[next] = true;
        }
    }

    /// <summary>Whether a line must be quoted in comma text: it is empty, or has a character below 33 or above 126, or one of <c>, / " \</c>.</summary>
    /// <param name="line">The line.</param>
    /// <returns>Whether it is quoted.</returns>
    public static bool NeedsQuotes(string line)
    {
        if (string.IsNullOrEmpty(line)) return true;
        foreach (char c in line)
            if (c < 33 || c > 126 || c is ',' or '/' or '"' or '\\') return true;
        return false;
    }

    private static string Join(string[] lines, bool[] quoted)
    {
        var output = new StringBuilder(lines.Sum(line => line.Length + 3));
        for (int index = 0; index < lines.Length; index++)
        {
            if (index > 0) output.Append(',');
            if (!quoted[index])
            {
                output.Append(lines[index]);
                continue;
            }

            output.Append('"');
            foreach (char c in lines[index])
            {
                if (c == '\\') output.Append("\\\\");
                else if (c == '"') output.Append("\"\"");
                else output.Append(c);
            }

            output.Append('"');
        }

        return output.ToString();
    }
}
