using System.Text;

namespace RemoteControl.Protocol.Text;

/// <summary>The names of the staff rights, by bit (RClib <c>rc_get_rights_names</c>). Bit 13 has no name.</summary>
public static class RightsNames
{
    /// <summary>The 20 names; index i is bit i; index 13 is empty.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        "Warp to XY", "Warp to Player", "Warp player", "Update Level",
        "Disconnect", "View Attributes", "Set Attributes", "Set Own Attributes",
        "Reset Attributes", "Admin Message", "Change Rights", "Ban Players",
        "Comments", "", "Staff Accounts", "Server Flags",
        "Server Options", "Folder Configuration", "Folder Rights", "NPC Control",
    };

    /// <summary>The names joined with newlines, as RClib returned them.</summary>
    /// <returns>The text.</returns>
    public static string AsText() => string.Join('\n', All);
}

/// <summary>The names of the 20 player colours, by index (RClib <c>rc_get_color_names</c>).</summary>
public static class ColorNames
{
    /// <summary>The names; index 0 is White.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        "White", "Yellow", "Orange", "Pink", "Red", "Darkred",
        "Lightgreen", "Green", "Darkgreen", "Lightblue", "Blue", "Darkblue",
        "Brown", "Cynober", "Purple", "Darkpurple", "Lightgray", "Gray",
        "Black", "Transparent",
    };

    /// <summary>The names joined with newlines, as RClib returned them.</summary>
    /// <returns>The text.</returns>
    public static string AsText() => string.Join('\n', All);

    /// <summary>The name of a colour index, or <c>""</c> outside 0 to 19.</summary>
    /// <param name="index">The index.</param>
    /// <returns>The name.</returns>
    public static string NameOf(int index) => index >= 0 && index < All.Count ? All[index] : string.Empty;

    /// <summary>A colour from text: a number, or a name in any case; 0 when it is neither.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The index.</returns>
    public static int IndexOf(string? text)
    {
        string trimmed = TextHelpers.Trim(text);
        if (trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit)) return TextHelpers.ParseInt(trimmed);
        for (int index = 0; index < All.Count; index++)
        {
            if (string.Equals(All[index], trimmed, StringComparison.OrdinalIgnoreCase)) return index;
        }

        return 0;
    }
}

/// <summary>A player's staff rights.</summary>
/// <param name="Rights">The rights bits.</param>
/// <param name="IpRange">The IP range the rights are valid from.</param>
/// <param name="Folders">The folder rights, one per line (<c>"rw levels/*.nw"</c>).</param>
public sealed record PlayerRights(int Rights, string IpRange, string Folders)
{
    /// <summary>Whether a right is set.</summary>
    /// <param name="bit">The bit (the index in <see cref="RightsNames.All"/>).</param>
    /// <returns>Whether it is set.</returns>
    public bool Has(int bit) => bit is >= 0 and < 32 && (Rights & (1 << bit)) != 0;
}

/// <summary>The rights editor text (RClib <c>rc_format_player_rights_text</c> and <c>rc_parse_player_rights_text</c>).</summary>
public static class PlayerRightsText
{
    /// <summary>Formats rights for the editor.</summary>
    /// <param name="rights">The rights.</param>
    /// <returns>
    /// <c>"IP Range: &lt;ip&gt;\n\n# Rights (set to true/false):\n"</c>, a <c>"Name: true|false"</c> line per named bit, then
    /// <c>"\nFolder Access:\n&lt;folders&gt;\n"</c>.
    /// </returns>
    public static string Format(PlayerRights rights)
    {
        var text = new StringBuilder();
        text.Append("IP Range: ").Append(rights.IpRange).Append("\n\n");
        text.Append("# Rights (set to true/false):\n");
        for (int bit = 0; bit < RightsNames.All.Count; bit++)
        {
            if (RightsNames.All[bit].Length == 0) continue;
            text.Append(RightsNames.All[bit]).Append(": ").Append(rights.Has(bit) ? "true" : "false").Append('\n');
        }

        text.Append("\nFolder Access:\n").Append(rights.Folders).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Parses the editor text. Bits without a name (13, and 20 and above) cannot be written in the text; they are taken from
    /// <paramref name="unnamedBitsFrom"/> so that editing does not clear them (RClib cleared them).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="unnamedBitsFrom">The rights the text was made from, or 0.</param>
    /// <returns>The rights; folders are the non-empty lines after <c>Folder Access:</c>, trimmed, joined with newlines.</returns>
    public static PlayerRights Parse(string? text, int unnamedBitsFrom = 0)
    {
        string ipRange = string.Empty;
        var folders = new List<string>();
        int rights = 0;
        bool inFolders = false;
        foreach (string line in TextHelpers.Lines(text))
        {
            if (line.StartsWith("IP Range:", StringComparison.Ordinal))
            {
                ipRange = TextHelpers.Trim(line["IP Range:".Length..]);
                inFolders = false;
                continue;
            }

            if (line.StartsWith("Folder Access:", StringComparison.Ordinal))
            {
                string first = TextHelpers.Trim(line["Folder Access:".Length..]);
                if (first.Length > 0) folders.Add(first);
                inFolders = true;
                continue;
            }

            if (line.StartsWith('#'))
            {
                inFolders = false;
                continue;
            }

            if (inFolders)
            {
                string folder = TextHelpers.Trim(line);
                if (folder.Length > 0) folders.Add(folder);
                continue;
            }

            for (int bit = 0; bit < RightsNames.All.Count; bit++)
            {
                string name = RightsNames.All[bit];
                if (name.Length == 0) continue;
                string prefix = name + ":";
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (TextHelpers.IsTruthy(line[prefix.Length..])) rights |= 1 << bit;
                break;
            }
        }

        int namedMask = 0;
        for (int bit = 0; bit < RightsNames.All.Count; bit++)
            if (RightsNames.All[bit].Length > 0) namedMask |= 1 << bit;
        rights |= unnamedBitsFrom & ~namedMask;
        return new PlayerRights(rights, ipRange, string.Join('\n', folders));
    }
}
