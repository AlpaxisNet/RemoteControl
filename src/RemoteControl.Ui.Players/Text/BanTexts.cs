using System.Globalization;
using System.Text;

namespace RemoteControl.Ui.Players;

/// <summary>A ban type of the server's <c>bantypes</c> list.</summary>
/// <param name="Name">The name sent back in <c>setban</c>.</param>
/// <param name="Seconds">How long it bans, in seconds (0 when unknown).</param>
internal sealed record BanType(string Name, long Seconds)
{
    /// <summary>What the combo box shows: <c>name (duration)</c>.</summary>
    public string Label => Name + " (" + BanTexts.Duration(Seconds) + ")";

    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>One ban of the lister's ban details (<c>account=..,world=..,bantype=..,releasetime=..,reason=..</c>).</summary>
/// <param name="Target">The account, or <c>pc:&lt;id&gt;</c> for a computer.</param>
/// <param name="World">The world, <c>all</c> for a global ban.</param>
/// <param name="BanType">The ban type.</param>
/// <param name="ReleaseTime">The release time as sent (Unix seconds on Alpaxis).</param>
/// <param name="Reason">The reason.</param>
internal sealed record BanRecord(string Target, string World, string BanType, string ReleaseTime, string Reason)
{
    /// <summary>The tab of the Edit Access window: 0 local, 1 global, 2 computer, 3 global computer.</summary>
    public int Scope => (Target.StartsWith("pc:", StringComparison.Ordinal) ? 2 : 0) + (World == "all" ? 1 : 0);
}

/// <summary>The texts of the Edit Access window and the ban history (<c>TLocalBanWindow.cpp</c>, <c>handleBanListData</c>).</summary>
internal static class BanTexts
{
    /// <summary>A duration as the C++ <c>banTimeText</c> wrote it: "-", minutes, hours, days, or "unlimited" from ten years.</summary>
    /// <param name="seconds">The seconds.</param>
    /// <returns>The text.</returns>
    public static string Duration(long seconds)
    {
        if (seconds <= 0) return "-";
        if (seconds >= 315360000) return "unlimited";
        long days = seconds / 86400;
        if (days > 0) return days + (days == 1 ? " day" : " days");
        long hours = seconds / 3600;
        if (hours > 0) return hours + (hours == 1 ? " hour" : " hours");
        long minutes = seconds / 60;
        return minutes + (minutes == 1 ? " min" : " mins");
    }

    /// <summary>Reads the ban types: one per line, <c>name,seconds</c> (the name trimmed of spaces and quotes).</summary>
    /// <param name="content">The <c>bantypes</c> content.</param>
    /// <returns>The types.</returns>
    public static IReadOnlyList<BanType> ParseTypes(string? content)
    {
        var types = new List<BanType>();
        foreach (string raw in (content ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            int comma = line.LastIndexOf(',');
            string name = TrimName(comma < 0 ? line : line[..comma]);
            long seconds = comma < 0 ? 0 : LeadingNumber(line[(comma + 1)..]);
            if (name.Length > 0) types.Add(new BanType(name, seconds));
        }
        return types;
    }

    /// <summary>Reads the lister ban details: one ban per line, fields separated by commas, <c>key=value</c> each.</summary>
    /// <param name="details">The details.</param>
    /// <returns>The bans that name an account and a world.</returns>
    public static IReadOnlyList<BanRecord> ParseBans(string? details)
    {
        var bans = new List<BanRecord>();
        foreach (string raw in (details ?? string.Empty).Split('\n'))
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in raw.TrimEnd('\r').Split(','))
            {
                int equals = part.IndexOf('=');
                if (equals > 0) fields[part[..equals]] = part[(equals + 1)..];
            }
            if (!fields.TryGetValue("account", out string? target) || !fields.TryGetValue("world", out string? world)) continue;
            bans.Add(new BanRecord(target, world, TrimName(fields.GetValueOrDefault("bantype")), fields.GetValueOrDefault("releasetime") ?? string.Empty,
                fields.GetValueOrDefault("reason") ?? string.Empty));
        }
        return bans;
    }

    /// <summary>Whether the legacy details (<c>banned=1|true</c> line) say the account is banned.</summary>
    /// <param name="details">The details.</param>
    /// <returns>Whether it is banned.</returns>
    public static bool ParseLegacyBanned(string? details)
    {
        bool banned = false;
        foreach (string raw in (details ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int equals = line.IndexOf('=');
            if (equals < 0 || line[..equals] != "banned") continue;
            string value = line[(equals + 1)..];
            banned = value is "1" or "true";
        }
        return banned;
    }

    /// <summary>
    /// The "Ban time left" of a ban: the time until its release time when that is a Unix time in the future (the C++ showed the
    /// ban type's length instead, defect D19), otherwise the length of the chosen ban type.
    /// </summary>
    /// <param name="releaseTime">The release time as sent, or empty.</param>
    /// <param name="typeSeconds">The chosen type's length, or 0.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The text after "Ban time left: ".</returns>
    public static string TimeLeft(string? releaseTime, long typeSeconds, DateTimeOffset now)
    {
        if (long.TryParse(releaseTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out long release) && release > 0)
        {
            long left = release - now.ToUnixTimeSeconds();
            if (left > 0) return Duration(left);
        }
        return Duration(typeSeconds);
    }

    /// <summary>Whether a line of a ban history or staff activity is an entry's header (<c>YYYY-MM-DD HH:MM:SS ...:</c>).</summary>
    /// <param name="line">The line.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsHistoryHeader(string line)
    {
        if (line.Length < 20 || !line.EndsWith(':')) return false;
        const string pattern = "dddd-dd-dd dd:dd:dd";
        for (int index = 0; index < pattern.Length; index++)
        {
            char expected = pattern[index];
            if (expected == 'd' ? !char.IsAsciiDigit(line[index]) : line[index] != expected) return false;
        }
        return true;
    }

    /// <summary>
    /// The text of a ban history or staff activity window: empty lines dropped, a blank line before each entry header after the
    /// first, <c>(none)</c> when there is nothing.
    /// </summary>
    /// <param name="content">The content as sent.</param>
    /// <returns>The text shown.</returns>
    public static string HistoryText(string? content)
    {
        if (string.IsNullOrEmpty(content)) return "(none)";
        var text = new StringBuilder(content.Length + 64);
        bool hasEntry = false;
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            bool header = IsHistoryHeader(line);
            if (header && hasEntry) text.Append('\n');
            if (header) hasEntry = true;
            text.Append(line).Append('\n');
        }
        return text.Length == 0 ? "(none)" : text.ToString();
    }

    private static string TrimName(string? value) => (value ?? string.Empty).Trim(' ', '\t', '\r', '"');

    private static long LeadingNumber(string text)
    {
        string value = text.TrimStart();
        int end = 0;
        if (end < value.Length && (value[end] == '-' || value[end] == '+')) end++;
        while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
        return long.TryParse(value.AsSpan(0, end), NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) ? number : 0;
    }
}
