using System.Globalization;
using Avalonia.Media;

namespace RemoteControl.Ui.Common;

/// <summary>
/// Reads the colours the options file holds. Accepted: <c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c>, <c>#RRGGBBAA</c> (CSS order: the alpha
/// is last, unlike Avalonia's own <c>#AARRGGBB</c>), <c>rgb(r, g, b)</c>, <c>rgba(r, g, b, a)</c> (alpha 0 to 1), <c>transparent</c> and the
/// CSS colour names (<c>red</c>, <c>cornflowerblue</c>...), case ignored, surrounding spaces ignored. Never throws.
/// </summary>
/// <example>
/// <code>
/// Color tint = ColorParser.Parse(options.BackgroundTint, Colors.Transparent);
/// if (ColorParser.TryParse("#00C000", out Color bold)) { ... }
/// </code>
/// </example>
public static class ColorParser
{
    /// <summary>Reads a colour.</summary>
    /// <param name="text">The text, for example <c>#1e1e2e</c>.</param>
    /// <param name="color">The colour when it was read, otherwise transparent black.</param>
    /// <returns><c>true</c> when the text is a colour.</returns>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.Trim();
            if (value[0] == '#') return TryParseHex(value.AsSpan(1), out color);
            if (value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) return TryParseFunction(value, out color);
            if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            {
                color = Color.FromArgb(0, 0, 0, 0);
                return true;
            }
            foreach (char character in value)
                if (!char.IsAsciiLetter(character)) return false;
            return Color.TryParse(value, out color);
        }
        catch (Exception)
        {
            color = default;
            return false;
        }
    }

    /// <summary>Reads a colour or gives <paramref name="fallback"/>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="fallback">The colour used when the text is not a colour.</param>
    /// <returns>The colour.</returns>
    public static Color Parse(string? text, Color fallback) => TryParse(text, out Color color) ? color : fallback;

    /// <summary>Writes a colour as the options file holds it: <c>#rrggbb</c>, or <c>#rrggbbaa</c> when it is not opaque.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The text in lower case.</returns>
    public static string Format(Color color) => color.A == 255
        ? string.Create(CultureInfo.InvariantCulture, $"#{color.R:x2}{color.G:x2}{color.B:x2}")
        : string.Create(CultureInfo.InvariantCulture, $"#{color.R:x2}{color.G:x2}{color.B:x2}{color.A:x2}");

    private static bool TryParseHex(ReadOnlySpan<char> hex, out Color color)
    {
        color = default;
        if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value)) return false;
        switch (hex.Length)
        {
            case 3:
                color = Color.FromArgb(255, Expand(value >> 8), Expand(value >> 4), Expand(value));
                return true;
            case 4:
                color = Color.FromArgb(Expand(value), Expand(value >> 12), Expand(value >> 8), Expand(value >> 4));
                return true;
            case 6:
                color = Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
                return true;
            case 8:
                color = Color.FromArgb((byte)value, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8));
                return true;
            default:
                return false;
        }
    }

    private static byte Expand(uint nibble)
    {
        uint digit = nibble & 0xF;
        return (byte)(digit << 4 | digit);
    }

    private static bool TryParseFunction(string value, out Color color)
    {
        color = default;
        int open = value.IndexOf('(');
        int close = value.LastIndexOf(')');
        if (open < 0 || close < open || close != value.Length - 1) return false;
        string name = value[..open].Trim().ToLowerInvariant();
        string[] parts = value[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
        if (name == "rgb" && parts.Length != 3) return false;
        if (name == "rgba" && parts.Length != 4) return false;
        if (name is not ("rgb" or "rgba")) return false;
        var channels = new byte[3];
        for (int index = 0; index < 3; index++)
        {
            string part = parts[index];
            bool percent = part.EndsWith('%');
            if (!double.TryParse(percent ? part[..^1] : part, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) return false;
            if (percent) number = number * 255.0 / 100.0;
            channels[index] = (byte)Math.Clamp(Math.Round(number), 0, 255);
        }
        byte alpha = 255;
        if (parts.Length == 4)
        {
            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double a) || !double.IsFinite(a)) return false;
            alpha = (byte)Math.Clamp(Math.Round(a * 255.0), 0, 255);
        }
        color = Color.FromArgb(alpha, channels[0], channels[1], channels[2]);
        return true;
    }
}
