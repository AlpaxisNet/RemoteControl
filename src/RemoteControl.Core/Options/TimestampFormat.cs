using System.Globalization;
using System.Text;

namespace RemoteControl.Core;

/// <summary>
/// Formats a time with a <c>strftime</c> pattern (the option <c>timestampformat</c>, default <c>[%I:%M %p]</c>) in the C locale,
/// as the C++ did with <c>std::strftime</c>.
/// </summary>
/// <remarks>
/// Supported: <c>%a %A %b %B %h %c %C %d %D %e %F %H %I %j %k %l %m %M %n %p %P %r %R %S %t %T %u %w %x %X %y %Y %z %Z %%</c>, and the
/// glibc flags <c>-</c> (no padding), <c>_</c> (pad with spaces) and <c>0</c> (pad with zeros) before a numeric directive, as in
/// <c>%-I</c>. An unknown directive is kept as text, as is a lone <c>%</c> at the end.
/// </remarks>
public static class TimestampFormat
{
    private static readonly string[] ShortDays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] LongDays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] ShortMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] LongMonths = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>Formats <paramref name="time"/> with <paramref name="pattern"/>.</summary>
    /// <param name="pattern">The strftime pattern; <c>null</c> or empty gives an empty text.</param>
    /// <param name="time">The time, normally local.</param>
    /// <returns>The formatted text.</returns>
    public static string Format(string? pattern, DateTime time)
    {
        if (string.IsNullOrEmpty(pattern)) return string.Empty;
        var output = new StringBuilder(pattern.Length + 16);
        int index = 0;
        while (index < pattern.Length)
        {
            char character = pattern[index];
            if (character != '%' || index + 1 >= pattern.Length)
            {
                output.Append(character);
                index++;
                continue;
            }
            int start = index;
            index++;
            char flag = '\0';
            if (pattern[index] is '-' or '_' or '0' && index + 1 < pattern.Length)
            {
                flag = pattern[index];
                index++;
            }
            char directive = pattern[index];
            index++;
            if (!Append(output, directive, flag, time)) output.Append(pattern, start, index - start);
        }
        return output.ToString();
    }

    private static bool Append(StringBuilder output, char directive, char flag, DateTime time)
    {
        int hour12 = time.Hour % 12 == 0 ? 12 : time.Hour % 12;
        switch (directive)
        {
            case '%': output.Append('%'); return true;
            case 'n': output.Append('\n'); return true;
            case 't': output.Append('\t'); return true;
            case 'a': output.Append(ShortDays[(int)time.DayOfWeek]); return true;
            case 'A': output.Append(LongDays[(int)time.DayOfWeek]); return true;
            case 'b':
            case 'h': output.Append(ShortMonths[time.Month - 1]); return true;
            case 'B': output.Append(LongMonths[time.Month - 1]); return true;
            case 'p': output.Append(time.Hour < 12 ? "AM" : "PM"); return true;
            case 'P': output.Append(time.Hour < 12 ? "am" : "pm"); return true;
            case 'H': Number(output, time.Hour, 2, '0', flag); return true;
            case 'k': Number(output, time.Hour, 2, ' ', flag); return true;
            case 'I': Number(output, hour12, 2, '0', flag); return true;
            case 'l': Number(output, hour12, 2, ' ', flag); return true;
            case 'M': Number(output, time.Minute, 2, '0', flag); return true;
            case 'S': Number(output, time.Second, 2, '0', flag); return true;
            case 'd': Number(output, time.Day, 2, '0', flag); return true;
            case 'e': Number(output, time.Day, 2, ' ', flag); return true;
            case 'm': Number(output, time.Month, 2, '0', flag); return true;
            case 'j': Number(output, time.DayOfYear, 3, '0', flag); return true;
            case 'y': Number(output, time.Year % 100, 2, '0', flag); return true;
            case 'Y': output.Append(time.Year.ToString(CultureInfo.InvariantCulture)); return true;
            case 'C': Number(output, time.Year / 100, 2, '0', flag); return true;
            case 'u': output.Append(time.DayOfWeek == DayOfWeek.Sunday ? '7' : (char)('0' + (int)time.DayOfWeek)); return true;
            case 'w': output.Append((char)('0' + (int)time.DayOfWeek)); return true;
            case 'D': output.Append(Format("%m/%d/%y", time)); return true;
            case 'x': output.Append(Format("%m/%d/%y", time)); return true;
            case 'F': output.Append(Format("%Y-%m-%d", time)); return true;
            case 'T':
            case 'X': output.Append(Format("%H:%M:%S", time)); return true;
            case 'R': output.Append(Format("%H:%M", time)); return true;
            case 'r': output.Append(Format("%I:%M:%S %p", time)); return true;
            case 'c': output.Append(Format("%a %b %e %H:%M:%S %Y", time)); return true;
            case 'z':
            {
                TimeSpan offset = time.Kind == DateTimeKind.Utc ? TimeSpan.Zero : TimeZoneInfo.Local.GetUtcOffset(time);
                output.Append(offset < TimeSpan.Zero ? '-' : '+');
                offset = offset.Duration();
                output.Append(((int)offset.TotalHours).ToString("D2", CultureInfo.InvariantCulture)).Append(offset.Minutes.ToString("D2", CultureInfo.InvariantCulture));
                return true;
            }
            case 'Z':
                output.Append(time.Kind == DateTimeKind.Utc ? "UTC" : TimeZoneInfo.Local.IsDaylightSavingTime(time) ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName);
                return true;
            default:
                return false;
        }
    }

    private static void Number(StringBuilder output, int value, int width, char pad, char flag)
    {
        string digits = value.ToString(CultureInfo.InvariantCulture);
        if (flag == '-') { output.Append(digits); return; }
        char padding = flag switch { '_' => ' ', '0' => '0', _ => pad };
        for (int count = digits.Length; count < width; count++) output.Append(padding);
        output.Append(digits);
    }
}
