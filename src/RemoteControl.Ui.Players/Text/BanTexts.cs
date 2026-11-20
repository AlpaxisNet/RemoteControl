using System.Globalization;
using System.Text;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Ui.Players;

/// <summary>A ban type of the server's <c>bantypes</c> list.</summary>
/// <param name="Name">The name sent back in <c>setban</c>.</param>
/// <param name="Seconds">How long it bans, in seconds (0 when unknown).</param>
internal sealed record BanType(string Name, long Seconds)
{
    /// <summary>
    /// Whether this is the type of a ban that came from the server but is not in its <c>bantypes</c> list (a plain ban has none, or the
    /// list changed): it is shown and sent back as it is, so that applying the ban does not change its type.
    /// </summary>
    public bool NotListed { get; init; }

    /// <summary>What the combo box shows: <c>name (duration)</c>, or the name of a type that is not listed.</summary>
    public string Label => NotListed ? (Name.Length == 0 ? "(no type)" : Name + " (not in the server's list)") : Name + " (" + BanTexts.Duration(Seconds) + ")";

    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>One ban of the lister's ban details (<c>account=..,world=..,bantype=..,releasetime=..,reason=..</c>).</summary>
/// <param name="Target">The account, or <c>pc:&lt;id&gt;</c> for a computer.</param>
/// <param name="World">The world, <c>all</c> for a global ban.</param>
/// <param name="BanType">The ban type.</param>
/// <param name="ReleaseTime">
/// The release time as sent: the seconds left when the server answered (<see cref="BanTexts.UnlimitedSeconds"/> or more: no end), as
/// Alpaxis and its in-game RC use it. A value from <see cref="BanTexts.FirstUnixTime"/> on is taken for a Unix time, as a guard.
/// </param>
/// <param name="Reason">The reason.</param>
internal sealed record BanRecord(string Target, string World, string BanType, string ReleaseTime, string Reason)
{
    /// <summary>The tab of the Edit Access window: 0 local, 1 global, 2 computer, 3 global computer.</summary>
    public int Scope => (Target.StartsWith("pc:", StringComparison.Ordinal) ? 2 : 0) + (World == "all" ? 1 : 0);
}

/// <summary>The texts of the Edit Access window and the ban history (<c>TLocalBanWindow.cpp</c>, <c>handleBanListData</c>).</summary>
internal static class BanTexts
{
    /// <summary>From this many seconds a ban has no end, as Alpaxis counts it (it sends 315360000 for a ban without an end).</summary>
    public const long UnlimitedSeconds = 100_000_000;

    /// <summary>
    /// The <c>releasetime</c> of a ban that is set without an end. It is sent, not left out: without a <c>releasetime</c> Alpaxis
    /// takes the length of the ban's type and bans nothing when it does not know the type.
    /// </summary>
    public const string NoEnd = "0";

    /// <summary>A release time from this value on (2001 and later) is read as a Unix time rather than seconds left: a guard for other servers.</summary>
    public const long FirstUnixTime = 1_000_000_000;

    /// <summary>
    /// A duration as the C++ <c>banTimeText</c> wrote it: "-", minutes, hours, days, or "unlimited" from <see cref="UnlimitedSeconds"/>
    /// (where Alpaxis bans without an end; the C++ said so from ten years).
    /// </summary>
    /// <param name="seconds">The seconds.</param>
    /// <returns>The text.</returns>
    public static string Duration(long seconds)
    {
        if (seconds <= 0) return "-";
        if (seconds >= UnlimitedSeconds) return "unlimited";
        long days = seconds / 86400;
        if (days > 0) return days + (days == 1 ? " day" : " days");
        long hours = seconds / 3600;
        if (hours > 0) return hours + (hours == 1 ? " hour" : " hours");
        long minutes = seconds / 60;
        return minutes + (minutes == 1 ? " min" : " mins");
    }

    /// <summary>
    /// Reads the ban types: one per line, <c>name,seconds</c> as comma text (Alpaxis quotes a name with a space, <c>"Bug abuse",2592000</c>;
    /// an unquoted name may hold commas, the last field is the length). Names are trimmed of spaces and quotes.
    /// </summary>
    /// <param name="content">The <c>bantypes</c> content.</param>
    /// <returns>The types.</returns>
    public static IReadOnlyList<BanType> ParseTypes(string? content)
    {
        var types = new List<BanType>();
        foreach (string raw in (content ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            IReadOnlyList<string> fields = SplitFields(line);
            if (fields.Count == 0) continue;
            string name = TrimName(fields.Count == 1 ? fields[0] : string.Join(',', fields.Take(fields.Count - 1)));
            long seconds = fields.Count == 1 ? 0 : LeadingNumber(fields[^1]);
            if (name.Length > 0) types.Add(new BanType(name, seconds));
        }
        return types;
    }

    /// <summary>
    /// Reads the lister ban details: one ban per line, each line comma text of <c>key=value</c> fields. Alpaxis quotes a field that holds a
    /// space, a comma, a quote or a backslash (<c>account=bob,world=local,"bantype=Bug abuse","reason=spam, again"</c>); older servers
    /// sent the fields unquoted, which reads the same way.
    /// </summary>
    /// <param name="details">The details.</param>
    /// <returns>The bans that name an account and a world.</returns>
    public static IReadOnlyList<BanRecord> ParseBans(string? details)
    {
        var bans = new List<BanRecord>();
        foreach (string raw in (details ?? string.Empty).Split('\n'))
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in SplitFields(raw.TrimEnd('\r')))
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
    /// The "Ban time left" of a ban (the C++ showed the ban type's length instead, defect D19). The release time is read as Alpaxis
    /// sends it, the seconds left (<see cref="UnlimitedSeconds"/> or more: "unlimited"); a value from <see cref="FirstUnixTime"/> on is
    /// taken for a Unix time and counts down to it. Without a release time (a new ban), or one that has passed, it is the length of the
    /// chosen ban type.
    /// </summary>
    /// <param name="releaseTime">The release time as sent, or empty.</param>
    /// <param name="typeSeconds">The chosen type's length, or 0.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The text after "Ban time left: ".</returns>
    public static string TimeLeft(string? releaseTime, long typeSeconds, DateTimeOffset now)
    {
        if (long.TryParse(releaseTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out long release) && release > 0)
        {
            if (release >= FirstUnixTime)
            {
                long left = release - now.ToUnixTimeSeconds();
                if (left > 0) return Duration(left);
            }
            else return release >= UnlimitedSeconds ? "unlimited" : Duration(release);
        }
        return Duration(typeSeconds);
    }

    /// <summary>
    /// A received release time some seconds later: seconds left count down (they were the seconds left when the server answered);
    /// a ban without an end, a Unix time or anything that is not a positive number stays as it is.
    /// </summary>
    /// <param name="received">The release time as it came.</param>
    /// <param name="elapsedSeconds">The seconds since it came.</param>
    /// <returns>The release time now, and whether the ban has ended meanwhile.</returns>
    public static (string Value, bool Ended) Remaining(string? received, long elapsedSeconds)
    {
        string text = (received ?? string.Empty).Trim();
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value <= 0 || value >= UnlimitedSeconds) return (text, false);
        long left = value - Math.Max(0, elapsedSeconds);
        return left > 0 ? (left.ToString(CultureInfo.InvariantCulture), false) : (string.Empty, true);
    }

    /// <summary>
    /// The release time a ban that is set goes out with (<c>setban</c>'s <c>releasetime</c>, which Alpaxis reads as the ban's length in
    /// seconds from the moment it arrives, and takes a missing one as a ban without an end). For a new ban, or when "Reset ban time" is
    /// checked, the chosen type's length. Otherwise the seconds that are left now (<see cref="Remaining"/>), which keeps the ban's end;
    /// when the ban has ended since it came, the type's length again, and <paramref name="ended"/> says so (the window asks first).
    /// Empty when the type's length is needed and not known.
    /// </summary>
    /// <param name="received">The release time of the ban as it came, or empty for a new ban.</param>
    /// <param name="reset">Whether "Reset ban time" is checked.</param>
    /// <param name="typeSeconds">The chosen type's length, or 0 when unknown.</param>
    /// <param name="elapsedSeconds">The seconds since the ban came.</param>
    /// <param name="ended">Whether the received ban has ended since it came.</param>
    /// <returns>The value to send, or empty.</returns>
    public static string ReleaseToSend(string? received, bool reset, long typeSeconds, long elapsedSeconds, out bool ended)
    {
        ended = false;
        if (!reset && !string.IsNullOrWhiteSpace(received))
        {
            (string value, bool over) = Remaining(received, elapsedSeconds);
            if (!over) return value;
            ended = true;
        }

        return typeSeconds > 0 ? typeSeconds.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    /// <summary>
    /// Whether ban data is the legacy form (the answer to the legacy ban packet, which Alpaxis also sends for <c>/openaccess</c> and
    /// <c>/openban</c>): a first line <c>banned=…</c> and no lister ban line.
    /// </summary>
    /// <param name="details">The details.</param>
    /// <returns>Whether they are legacy.</returns>
    public static bool IsLegacyDetails(string? details)
    {
        string text = details ?? string.Empty;
        string first = text.Split('\n')[0].Trim();
        return first.StartsWith("banned=", StringComparison.Ordinal) && !text.Contains("account=", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a computer id of the ban data names a computer: digits only and not 0. Alpaxis sends <c>-1</c> for an account that is
    /// not online on the server, and a ban of <c>pc:-1</c> would be refused.
    /// </summary>
    /// <param name="computerId">The id as it came.</param>
    /// <returns>Whether it names a computer.</returns>
    public static bool IsComputerId(string? computerId)
    {
        if (string.IsNullOrEmpty(computerId) || !computerId.All(char.IsAsciiDigit)) return false;
        return computerId.TrimStart('0').Length > 0;
    }

    /// <summary>
    /// Whether a line of a ban history or staff activity is an entry's header: <c>YYYY-MM-DD HH:MM:SS ...:</c> (the C++ and the list
    /// server it spoke to) or <c>YYYY-MM-DD HH:MM UTC ...</c> (Alpaxis: <c>2026-11-01 14:30 UTC  bob: banned by Staff (...)</c>).
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsHistoryHeader(string line)
    {
        if (line.Length >= 20 && line.EndsWith(':') && Matches(line, "dddd-dd-dd dd:dd:dd")) return true;
        return line.Length > 20 && Matches(line, "dddd-dd-dd dd:dd UTC ");
    }

    private static bool Matches(string line, string pattern)
    {
        if (line.Length < pattern.Length) return false;
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

    /// <summary>
    /// The fields of one ban line or ban type: comma text as Alpaxis writes it, where a field that starts with a quote runs to its closing
    /// quote (<c>""</c> is a quote, <c>\\</c> a backslash) and any other field runs to the next comma as it is. Unlike
    /// <see cref="CommaText.Split"/> (RClib's reader), a quote inside an unquoted field (<c>reason=said "hi"</c>) is kept, as the
    /// plain comma split of older servers' lines kept it.
    /// </summary>
    internal static IReadOnlyList<string> SplitFields(string line)
    {
        var fields = new List<string>();
        int index = 0;
        while (index <= line.Length)
        {
            if (index < line.Length && line[index] == '"')
            {
                var field = new StringBuilder();
                index++;
                while (index < line.Length)
                {
                    char c = line[index];
                    if (c == '"')
                    {
                        if (index + 1 < line.Length && line[index + 1] == '"')
                        {
                            field.Append('"');
                            index += 2;
                            continue;
                        }

                        index++;
                        break;
                    }

                    if (c == '\\' && index + 1 < line.Length && line[index + 1] == '\\')
                    {
                        field.Append('\\');
                        index += 2;
                        continue;
                    }

                    field.Append(c);
                    index++;
                }

                int after = line.IndexOf(',', index);
                field.Append(after < 0 ? line[index..] : line[index..after]);
                fields.Add(field.ToString());
                if (after < 0) break;
                index = after + 1;
            }
            else
            {
                int comma = line.IndexOf(',', index);
                if (comma < 0)
                {
                    fields.Add(line[index..]);
                    break;
                }

                fields.Add(line[index..comma]);
                index = comma + 1;
            }
        }

        return fields;
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
