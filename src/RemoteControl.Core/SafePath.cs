namespace RemoteControl.Core;

/// <summary>
/// Joins a trusted root folder and a relative path that came from a server (a file name, a folder of the file browser) so
/// that the result is always inside the root (AGENTS.md rule 7). Both <c>/</c> and <c>\</c> separate segments; empty and
/// <c>.</c> segments are skipped.
/// </summary>
public static class SafePath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] ForbiddenCharacters = ['<', '>', ':', '"', '|', '?', '*'];

    /// <summary>Joins <paramref name="root"/> and <paramref name="untrustedRelative"/>, refusing anything that could leave the root.</summary>
    /// <param name="root">The trusted folder.</param>
    /// <param name="untrustedRelative">The relative path from the server.</param>
    /// <returns>The full path inside <paramref name="root"/>.</returns>
    /// <exception cref="ArgumentException">The relative path is refused; the message says why.</exception>
    public static string Combine(string root, string untrustedRelative)
    {
        if (TryCombine(root, untrustedRelative, out string? path, out string? reason)) return path;
        throw new ArgumentException(reason, nameof(untrustedRelative));
    }

    /// <summary>Joins <paramref name="root"/> and <paramref name="untrustedRelative"/> without throwing.</summary>
    /// <param name="root">The trusted folder.</param>
    /// <param name="untrustedRelative">The relative path from the server.</param>
    /// <param name="path">The full path inside <paramref name="root"/> when accepted.</param>
    /// <param name="reason">Why the path was refused, otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when the path is accepted.</returns>
    public static bool TryCombine(string root, string? untrustedRelative, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? path, out string? reason)
    {
        path = null;
        reason = null;
        if (string.IsNullOrWhiteSpace(root)) { reason = "No root folder."; return false; }
        if (string.IsNullOrEmpty(untrustedRelative)) { reason = "The path is empty."; return false; }
        if (untrustedRelative[0] is '/' or '\\' || Path.IsPathRooted(untrustedRelative)) { reason = $"'{untrustedRelative}' is an absolute path."; return false; }
        if (untrustedRelative.Length >= 2 && char.IsAsciiLetter(untrustedRelative[0]) && untrustedRelative[1] == ':') { reason = $"'{untrustedRelative}' starts with a drive letter."; return false; }

        var segments = new List<string>();
        foreach (string segment in untrustedRelative.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") { reason = $"'{untrustedRelative}' leaves its folder."; return false; }
            if (!IsSafeSegment(segment, out string? segmentReason)) { reason = $"'{untrustedRelative}': {segmentReason}"; return false; }
            segments.Add(segment);
        }
        if (segments.Count == 0) { reason = $"'{untrustedRelative}' names no file."; return false; }

        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string combined = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(segments.ToArray())));
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!combined.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)) { reason = $"'{untrustedRelative}' leaves its folder."; return false; }
        path = combined;
        return true;
    }

    /// <summary>Whether one file or folder name is acceptable on every platform the program runs on.</summary>
    /// <param name="segment">One name, without separators.</param>
    /// <param name="reason">Why it is refused, otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when it is acceptable.</returns>
    public static bool IsSafeSegment(string segment, out string? reason)
    {
        reason = null;
        if (string.IsNullOrEmpty(segment) || segment == "." || segment == "..") { reason = "empty or relative name"; return false; }
        foreach (char character in segment)
        {
            if (character < ' ' || character == '\u007f') { reason = "control character in the name"; return false; }
            if (character is '/' or '\\') { reason = "separator in the name"; return false; }
        }
        if (segment.IndexOfAny(ForbiddenCharacters) >= 0) { reason = $"'{segment}' contains a character Windows refuses (or a drive or stream separator)"; return false; }
        if (segment.EndsWith('.') || segment.EndsWith(' ')) { reason = $"'{segment}' ends with a dot or a space"; return false; }
        int dot = segment.IndexOf('.');
        string stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        if (ReservedNames.Contains(stem)) { reason = $"'{segment}' is a reserved device name"; return false; }
        return true;
    }
}
