using System.Text;

namespace RemoteControl.Protocol.Text;

/// <summary>The profile editor text: <c>"Label: value"</c> lines for the ten profile fields (RClib <c>rc_set_player_profile</c>).</summary>
public static class ProfileText
{
    /// <summary>The labels of fields 1 to 10, in order.</summary>
    public static IReadOnlyList<string> Labels { get; } = new[]
    {
        "Real Name", "Age", "Sex", "Country", "Messenger", "E-Mail", "Homepage", "Fav. Hangout", "Favorite Quote", "Online Time",
    };

    private static readonly Dictionary<string, int> LabelIndex = new(StringComparer.Ordinal)
    {
        ["Real Name"] = 1, ["Age"] = 2, ["Sex"] = 3, ["Country"] = 4, ["Messenger"] = 5, ["E-Mail"] = 6, ["Email"] = 6, ["Homepage"] = 7,
        ["Fav. Hangout"] = 8, ["Favourite Quote"] = 9, ["Favorite Quote"] = 9, ["Online Time"] = 10,
    };

    /// <summary>Formats profile fields (as received after the account) for the editor.</summary>
    /// <param name="fields">Field 1 first; missing fields are empty.</param>
    /// <returns>One <c>"Label: value"</c> line per field.</returns>
    public static string Format(IReadOnlyList<string> fields)
    {
        var text = new StringBuilder();
        for (int index = 0; index < Labels.Count; index++)
            text.Append(Labels[index]).Append(": ").Append(index < fields.Count ? fields[index] : string.Empty).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Parses the editor text into fields 1 to 10 (index 0 of the result is field 1). Lines with a known label fill that field; lines
    /// without a colon fill fields 0, 1, 2 ... in order, as RClib did (field 0 is never sent); blank lines and a "Profile for" line are skipped.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>Ten fields.</returns>
    public static IReadOnlyList<string> Parse(string? text)
    {
        string[] fields = new string[11];
        Array.Fill(fields, string.Empty);
        int plainIndex = 0;
        foreach (string line in TextHelpers.Lines(text))
        {
            if (TextHelpers.Trim(line).Length == 0 || line.StartsWith("Profile for ", StringComparison.Ordinal)) continue;
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                string label = TextHelpers.Trim(line[..colon]);
                if (LabelIndex.TryGetValue(label, out int index)) fields[index] = TextHelpers.Trim(line[(colon + 1)..]);
            }
            else if (plainIndex < 11)
            {
                fields[plainIndex++] = line;
            }
        }

        return fields.Skip(1).ToArray();
    }
}

/// <summary>Helpers for ban data: the legacy <c>"banned=0|1\n"</c> details and the <c>key=value</c> lines of the lister's ban texts.</summary>
public static class BanText
{
    /// <summary>The details RClib built from PLO_RC_PLAYERBANGET: <c>"banned=1\n"</c> or <c>"banned=0\n"</c>, then the reason.</summary>
    /// <param name="banned">Whether the account is banned.</param>
    /// <param name="reason">The reason text.</param>
    /// <returns>The details.</returns>
    public static string FormatLegacyDetails(bool banned, string reason) => (banned ? "banned=1\n" : "banned=0\n") + reason;

    /// <summary>Reads <c>key=value</c> lines (keys and values trimmed of spaces and quotes); lines without <c>=</c> are skipped; a later key wins.</summary>
    /// <param name="details">The details.</param>
    /// <returns>The values.</returns>
    public static IReadOnlyDictionary<string, string> ParseDetails(string? details)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in TextHelpers.Lines(details))
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0) continue;
            values[TextHelpers.Trim(line[..equals])] = TextHelpers.Trim(line[(equals + 1)..]);
        }

        return values;
    }

    /// <summary>
    /// The fields of a lister <c>setban</c> text: <c>GraalEngine, lister, setban, target, world=…, banned=0|1, bantype=…,
    /// [releasetime=…,] reason=…</c>. A line break in a value becomes a space (one field cannot hold one).
    /// </summary>
    /// <returns>The fields; the session sends them as comma text.</returns>
    internal static IReadOnlyList<string> SetBanFields(string target, string world, bool banned, string? banType, string? releaseTime, string? reason)
    {
        static string Flat(string? value) => (value ?? string.Empty).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        var fields = new List<string> { "GraalEngine", "lister", "setban", Flat(target), "world=" + Flat(world), "banned=" + (banned ? "1" : "0"), "bantype=" + Flat(banType) };
        if (!string.IsNullOrEmpty(releaseTime)) fields.Add("releasetime=" + Flat(releaseTime));
        fields.Add("reason=" + Flat(reason));
        return fields;
    }
}
