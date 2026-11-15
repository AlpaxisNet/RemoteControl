namespace RemoteControl.Sync;

/// <summary>
/// The "Exclude files" setting (<c>isFileExcluded</c>, <c>TSyncManager.cpp:1187-1198</c>): patterns separated by <c>;</c>, <c>,</c> or line
/// breaks, matched case-insensitively (ASCII) against the whole remote path and against the file name. <c>*</c> matches any run of
/// characters and <c>?</c> one character (GLib's <c>g_pattern_match_simple</c>); a pattern starting with <c>.</c> means <c>*.ext</c>.
/// </summary>
public sealed class ExclusionList
{
    private readonly string[] _patterns;

    /// <summary>Parses the setting.</summary>
    /// <param name="setting">The setting text.</param>
    public ExclusionList(string? setting)
    {
        _patterns = SplitPatterns(setting).Select(pattern =>
        {
            string lower = SyncNames.LowerAscii(pattern);
            return lower.StartsWith('.') ? "*" + lower : lower;
        }).ToArray();
    }

    /// <summary>The patterns as matched (lower case, <c>.ext</c> expanded).</summary>
    public IReadOnlyList<string> Patterns => _patterns;

    /// <summary><c>splitPatterns</c>: the patterns, each trimmed of spaces and tabs, without empty ones.</summary>
    /// <param name="value">The setting.</param>
    /// <returns>The patterns.</returns>
    public static IReadOnlyList<string> SplitPatterns(string? value)
    {
        var patterns = new List<string>();
        foreach (string part in (value ?? string.Empty).Split(';', ',', '\n', '\r'))
        {
            string trimmed = part.Trim(' ', '\t');
            if (trimmed.Length != 0) patterns.Add(trimmed);
        }

        return patterns;
    }

    /// <summary>Whether a remote file is excluded.</summary>
    /// <param name="remotePath">The remote path.</param>
    /// <returns><c>true</c> when a pattern matches its path or name.</returns>
    public bool IsExcluded(string remotePath)
    {
        if (_patterns.Length == 0) return false;
        string normalized = SyncNames.LowerAscii(SyncNames.NormalizeRemotePath(remotePath));
        string name = SyncNames.NameOf(normalized);
        foreach (string pattern in _patterns)
        {
            if (Matches(pattern, normalized) || Matches(pattern, name)) return true;
        }

        return false;
    }

    /// <summary><c>g_pattern_match_simple</c>: <c>*</c> and <c>?</c> wildcards, no escapes, compared ordinally.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="text">The text.</param>
    /// <returns>Whether the whole text matches.</returns>
    public static bool Matches(string pattern, string text)
    {
        int p = 0;
        int t = 0;
        int starPattern = -1;
        int starText = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPattern = p++;
                starText = t;
            }
            else if (starPattern >= 0)
            {
                p = starPattern + 1;
                t = ++starText;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
