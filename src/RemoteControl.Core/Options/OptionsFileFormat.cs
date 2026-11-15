using System.Text;

namespace RemoteControl.Core;

/// <summary>Reads and writes the text of <c>control2config.txt</c> (<c>TRCOptions.cpp:58-278</c>).</summary>
internal static class OptionsFileFormat
{
    /// <summary>The largest options file that is read; anything bigger is not an options file.</summary>
    public const int MaximumFileSize = 1024 * 1024;

    private static readonly char[] CSpace = [' ', '\t', '\n', '\v', '\f', '\r'];

    /// <summary>
    /// Splits the text into <c>key=value</c> entries the way the C++ did: lines without <c>=</c> are skipped, key and value are
    /// trimmed of ASCII whitespace, the first <c>=</c> separates them.
    /// </summary>
    public static List<KeyValuePair<string, string>> Parse(string text)
    {
        var entries = new List<KeyValuePair<string, string>>();
        foreach (string line in text.Split('\n'))
        {
            int separator = line.IndexOf('=');
            if (separator < 0) continue;
            string key = line[..separator].Trim(CSpace);
            string value = line[(separator + 1)..].Trim(CSpace);
            entries.Add(new KeyValuePair<string, string>(key, value));
        }
        return entries;
    }

    /// <summary>Applies parsed entries to the defaults; entries with unknown keys are returned in file order (the last of a repeated key wins).</summary>
    public static RcOptions Apply(RcOptions start, IEnumerable<KeyValuePair<string, string>> entries, out List<KeyValuePair<string, string>> unknown)
    {
        RcOptions options = start;
        var unknownByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        unknown = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, string> entry in entries)
        {
            if (RcOptions.IsKnownKey(entry.Key))
            {
                options = options.WithValue(entry.Key, entry.Value);
                continue;
            }
            if (entry.Key.Length == 0) continue;
            if (unknownByKey.TryGetValue(entry.Key, out int index)) unknown[index] = entry;
            else
            {
                unknownByKey[entry.Key] = unknown.Count;
                unknown.Add(entry);
            }
        }
        return options;
    }

    /// <summary>The file text: every known key in the C++ order, then the unknown entries that were read, one <c>key=value</c> per line.</summary>
    public static string Serialize(RcOptions options, IEnumerable<KeyValuePair<string, string>> unknown)
    {
        var text = new StringBuilder(4096);
        foreach (KeyValuePair<string, string> entry in options.ToEntries()) text.Append(entry.Key).Append('=').Append(entry.Value).Append('\n');
        foreach (KeyValuePair<string, string> entry in unknown)
        {
            if (RcOptions.IsKnownKey(entry.Key) || !IsWritable(entry.Key) || !IsWritable(entry.Value) || entry.Key.Contains('=')) continue;
            text.Append(entry.Key).Append('=').Append(entry.Value).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Whether a value can be written without breaking the line format (no line break, no NUL).</summary>
    public static bool IsWritable(string value) => value.IndexOfAny(['\n', '\r', '\0']) < 0;
}
