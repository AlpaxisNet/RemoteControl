using System.Globalization;

namespace RemoteControl.Protocol.Text;

/// <summary>Small text helpers that reproduce RClib's C library behaviour (<c>trimText</c>, <c>atoi</c>, <c>atof</c>, <c>ostream</c> numbers).</summary>
internal static class TextHelpers
{
    private static readonly char[] TrimCharacters = { ' ', '\t', '\r', '\n', '"' };

    /// <summary>RClib <c>trimText</c>: trims spaces, tabs, line breaks and double quotes.</summary>
    public static string Trim(string? value) => (value ?? string.Empty).Trim(TrimCharacters);

    /// <summary>Splits text into lines the way <c>std::getline</c> does, dropping one trailing carriage return per line.</summary>
    public static IEnumerable<string> Lines(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            string line = end < 0 ? text[start..] : text[start..end];
            if (line.EndsWith('\r')) line = line[..^1];
            yield return line;
            if (end < 0) break;
            start = end + 1;
        }
    }

    /// <summary>RClib <c>truthyText</c>: <c>true</c>, <c>1</c> or <c>yes</c>, in any case, after trimming.</summary>
    public static bool IsTruthy(string? value)
    {
        string lower = Trim(value).ToLowerInvariant();
        return lower is "true" or "1" or "yes";
    }

    /// <summary><c>atoi</c>: the leading integer, or 0.</summary>
    public static int ParseInt(string? text) => ListServer.ListServerClient.ParseInt(text ?? string.Empty);

    /// <summary><c>atof</c>: the leading decimal number, or 0.</summary>
    public static double ParseDouble(string? text)
    {
        string value = (text ?? string.Empty).TrimStart();
        int end = 0;
        if (end < value.Length && (value[end] == '-' || value[end] == '+')) end++;
        while (end < value.Length && (char.IsAsciiDigit(value[end]) || value[end] == '.')) end++;
        if (end < value.Length && (value[end] == 'e' || value[end] == 'E'))
        {
            int exponent = end + 1;
            if (exponent < value.Length && (value[exponent] == '-' || value[exponent] == '+')) exponent++;
            if (exponent < value.Length && char.IsAsciiDigit(value[exponent]))
            {
                end = exponent;
                while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
            }
        }

        for (int length = end; length > 0; length--)
        {
            if (double.TryParse(value.AsSpan(0, length), NumberStyles.Float, CultureInfo.InvariantCulture, out double result)) return result;
        }

        return 0;
    }

    /// <summary>A number as C++ <c>ostream &lt;&lt; double</c> prints it (six significant digits, no trailing zeros).</summary>
    public static string FormatNumber(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        if (value == 0) return "0";
        string text = value.ToString("G6", CultureInfo.InvariantCulture);
        return text.Replace("E", "e", StringComparison.Ordinal);
    }

    /// <summary>An integer as text.</summary>
    public static string FormatInt(long value) => value.ToString(CultureInfo.InvariantCulture);
}
