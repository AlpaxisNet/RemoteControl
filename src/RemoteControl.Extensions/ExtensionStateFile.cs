using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using RemoteControl.Core;

namespace RemoteControl.Extensions;

/// <summary>What is saved per extension: whether it is enabled, whether its output goes to a tab, and the approved capabilities.</summary>
/// <param name="Id">The extension id.</param>
/// <param name="Enabled">Whether the extension is launched.</param>
/// <param name="OutputToTab">Whether its output is shown in a tab.</param>
/// <param name="ApprovedCapabilities">The capabilities the user approved, sorted (ordinal).</param>
public sealed record ExtensionSavedState(string Id, bool Enabled, bool OutputToTab, ImmutableSortedSet<string> ApprovedCapabilities);

/// <summary>The content of the state file: the entries that were read and the lines that could not be read (kept as they are).</summary>
/// <param name="Entries">The entries by id.</param>
/// <param name="UnreadableLines">Lines that did not parse; they are written back unchanged so nothing is lost.</param>
public sealed record ExtensionStateContent(IReadOnlyDictionary<string, ExtensionSavedState> Entries, IReadOnlyList<string> UnreadableLines)
{
    /// <summary>An empty state.</summary>
    public static ExtensionStateContent Empty { get; } = new(new Dictionary<string, ExtensionSavedState>(), Array.Empty<string>());
}

/// <summary>
/// The per-user extension state file <c>extensions.state</c> in the user folder (the C++ <c>&lt;user config&gt;/GScriptRC/extensions.state</c>,
/// the same file). One line per extension: <c>&lt;id&gt; &lt;enabled 0|1&gt; &lt;outputToTab 0|1&gt; &lt;capability&gt; &lt;capability&gt; ...</c>.
/// </summary>
/// <remarks>
/// Differences from the C++: the file is written atomically (<see cref="AtomicFile"/>); the entries of packages that are not installed
/// at the moment, and lines that do not parse, are kept instead of dropped; an id with white space is never written.
/// </remarks>
public static class ExtensionStateFile
{
    /// <summary>The file name in the user folder.</summary>
    public const string FileName = "extensions.state";

    /// <summary>The state file of <paramref name="paths"/>.</summary>
    /// <param name="paths">The program's folders.</param>
    /// <returns>The full path.</returns>
    public static string PathFor(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.UserDirectory, FileName);
    }

    /// <summary>Parses the text of a state file as the C++ read it (<c>id value output</c>, then the rest of the line as capabilities separated by spaces or commas).</summary>
    /// <param name="text">The file text.</param>
    /// <returns>The entries; a later line for the same id wins, as in the C++.</returns>
    public static ExtensionStateContent Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new Dictionary<string, ExtensionSavedState>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || !int.TryParse(tokens[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int enabled))
            {
                unreadable.Add(line);
                continue;
            }
            int output = 0;
            int capabilitiesFrom = 2;
            if (tokens.Length > 2)
            {
                if (int.TryParse(tokens[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed)) { output = parsed; capabilitiesFrom = 3; }
                else { unreadable.Add(line); continue; }
            }
            var capabilities = ImmutableSortedSet.CreateRange(StringComparer.Ordinal,
                tokens.Skip(capabilitiesFrom).SelectMany(token => token.Split(',', StringSplitOptions.RemoveEmptyEntries)));
            entries[tokens[0]] = new ExtensionSavedState(tokens[0], enabled != 0, output != 0, capabilities);
        }
        return new ExtensionStateContent(entries, unreadable);
    }

    /// <summary>Formats entries in the C++ format, in the order given, followed by the kept unreadable lines.</summary>
    /// <param name="entries">The entries to write; one whose id is empty or contains white space is skipped.</param>
    /// <param name="unreadableLines">Lines to keep as they are.</param>
    /// <returns>The file text.</returns>
    public static string Format(IEnumerable<ExtensionSavedState> entries, IEnumerable<string>? unreadableLines = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var text = new StringBuilder();
        foreach (ExtensionSavedState entry in entries)
        {
            if (entry.Id.Length == 0 || entry.Id.Any(char.IsWhiteSpace)) continue;
            text.Append(entry.Id).Append(' ').Append(entry.Enabled ? '1' : '0').Append(' ').Append(entry.OutputToTab ? '1' : '0');
            foreach (string capability in entry.ApprovedCapabilities)
            {
                if (capability.Length == 0 || capability.Any(character => char.IsWhiteSpace(character) || character == ',')) continue;
                text.Append(' ').Append(capability);
            }
            text.Append('\n');
        }
        foreach (string line in unreadableLines ?? Array.Empty<string>()) text.Append(line).Append('\n');
        return text.ToString();
    }

    /// <summary>Reads the state file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The content; a missing file is empty.</returns>
    /// <exception cref="IOException">The file exists but cannot be read; the caller must then not overwrite it.</exception>
    /// <exception cref="UnauthorizedAccessException">The file exists but cannot be read.</exception>
    public static ExtensionStateContent Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Directory.Exists(path)) throw new IOException($"'{path}' is a folder.");
        if (!File.Exists(path)) return ExtensionStateContent.Empty;
        return Parse(File.ReadAllText(path, new UTF8Encoding(false, false)));
    }

    /// <summary>Writes the state file atomically.</summary>
    /// <param name="path">The file; its folder is created when missing.</param>
    /// <param name="entries">The entries.</param>
    /// <param name="unreadableLines">Lines to keep as they are.</param>
    /// <exception cref="IOException">The file could not be written; the previous content is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    public static void Write(string path, IEnumerable<ExtensionSavedState> entries, IEnumerable<string>? unreadableLines = null) =>
        AtomicFile.WriteAllText(path, Format(entries, unreadableLines));
}
