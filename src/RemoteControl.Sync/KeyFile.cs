using System.Globalization;
using System.Text;

namespace RemoteControl.Sync;

/// <summary>
/// The subset of GLib's <c>GKeyFile</c> the C++ used for the sync settings and the manifest: <c>[group]</c> lines, <c>key=value</c> lines,
/// <c>#</c> comments, GLib's escapes (<c>\s \n \t \r \\</c>) and booleans <c>true</c>/<c>false</c>. Groups and keys keep their order.
/// </summary>
internal sealed class KeyFile
{
    private readonly List<Group> _groups = new();
    private readonly Dictionary<string, Group> _byName = new(StringComparer.Ordinal);

    private sealed class Group(string name)
    {
        public string Name { get; } = name;

        public List<string> Keys { get; } = new();

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The group names, in order.</summary>
    public IEnumerable<string> Groups => _groups.Select(group => group.Name);

    /// <summary>Parses a key file the way GLib does; a line that is neither a group, a key, a comment nor blank fails the whole file.</summary>
    /// <param name="text">The file content.</param>
    /// <param name="file">The parsed file.</param>
    /// <param name="error">Why it failed.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string text, out KeyFile file, out string? error)
    {
        file = new KeyFile();
        error = null;
        Group? current = null;
        int lineNumber = 0;
        foreach (string rawLine in text.Split('\n'))
        {
            lineNumber++;
            string line = rawLine.TrimEnd('\r').TrimStart(' ', '\t', '\f', '\v');
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                int close = line.IndexOf(']', 1);
                if (close < 0 || line[(close + 1)..].Trim().Length != 0)
                {
                    error = $"Line {lineNumber} is not a valid group.";
                    return false;
                }

                current = file.GetOrAdd(line[1..close]);
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0 || current is null)
            {
                error = current is null ? $"Line {lineNumber} comes before the first group." : $"Line {lineNumber} is not a key=value line.";
                return false;
            }

            string key = line[..equals].TrimEnd(' ', '\t');
            string value = line[(equals + 1)..].TrimStart(' ', '\t');
            if (key.Length == 0)
            {
                error = $"Line {lineNumber} has no key.";
                return false;
            }

            if (!current.Values.ContainsKey(key)) current.Keys.Add(key);
            current.Values[key] = value;
        }

        return true;
    }

    /// <summary>Whether a group has a key.</summary>
    public bool HasKey(string group, string key) => _byName.TryGetValue(group, out Group? found) && found.Values.ContainsKey(key);

    /// <summary>A string value, unescaped; <c>null</c> when missing.</summary>
    public string? GetString(string group, string key) =>
        _byName.TryGetValue(group, out Group? found) && found.Values.TryGetValue(key, out string? raw) ? Unescape(raw) : null;

    /// <summary>A boolean (<c>true</c>/<c>1</c>, <c>false</c>/<c>0</c>); <paramref name="fallback"/> when missing or invalid.</summary>
    public bool GetBoolean(string group, string key, bool fallback = false)
    {
        string? value = GetString(group, key)?.TrimEnd();
        return value switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => fallback,
        };
    }

    /// <summary>An integer; <paramref name="fallback"/> when missing or invalid (GLib returned 0).</summary>
    public long GetInteger(string group, string key, long fallback = 0)
    {
        string? value = GetString(group, key)?.Trim();
        return value is not null && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed) ? parsed : fallback;
    }

    /// <summary>Sets a string, escaped as <c>g_key_file_set_string</c> does.</summary>
    public void SetString(string group, string key, string? value)
    {
        Group found = GetOrAdd(group);
        if (!found.Values.ContainsKey(key)) found.Keys.Add(key);
        found.Values[key] = Escape(value ?? string.Empty);
    }

    /// <summary>Sets a boolean.</summary>
    public void SetBoolean(string group, string key, bool value) => SetString(group, key, value ? "true" : "false");

    /// <summary>Sets an integer.</summary>
    public void SetInteger(string group, string key, long value) => SetString(group, key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>The text as <c>g_key_file_to_data</c> wrote it: groups separated by a blank line.</summary>
    public string Serialize()
    {
        var text = new StringBuilder();
        for (int index = 0; index < _groups.Count; index++)
        {
            if (index != 0) text.Append('\n');
            Group group = _groups[index];
            text.Append('[').Append(group.Name).Append("]\n");
            foreach (string key in group.Keys) text.Append(key).Append('=').Append(group.Values[key]).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Whether a text can be a group name (GLib refuses <c>[</c>, <c>]</c> and control characters).</summary>
    public static bool IsValidGroupName(string name) => name.Length != 0 && name.All(character => character >= ' ' && character != '[' && character != ']');

    /// <summary>GLib's escaping of a string value.</summary>
    public static string Escape(string value)
    {
        var result = new StringBuilder(value.Length + 8);
        bool leading = true;
        foreach (char character in value)
        {
            switch (character)
            {
                case ' ' when leading:
                    result.Append("\\s");
                    continue;
                case '\t' when leading:
                    result.Append("\\t");
                    continue;
                case '\n':
                    result.Append("\\n");
                    break;
                case '\r':
                    result.Append("\\r");
                    break;
                case '\\':
                    result.Append("\\\\");
                    break;
                default:
                    result.Append(character);
                    break;
            }

            leading = false;
        }

        return result.ToString();
    }

    /// <summary>GLib's unescaping; an unknown escape keeps its character.</summary>
    public static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var result = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character != '\\' || index + 1 >= value.Length)
            {
                result.Append(character);
                continue;
            }

            char next = value[++index];
            result.Append(next switch
            {
                's' => ' ',
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                _ => next,
            });
        }

        return result.ToString();
    }

    private Group GetOrAdd(string name)
    {
        if (_byName.TryGetValue(name, out Group? found)) return found;
        var group = new Group(name);
        _groups.Add(group);
        _byName[name] = group;
        return group;
    }
}
