using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace RemoteControl.Scripting;

/// <summary>An sRGB colour with alpha.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Alpha (255 is opaque).</param>
public readonly record struct ThemeColor(byte R, byte G, byte B, byte A = 255)
{
    private static readonly Dictionary<string, ThemeColor> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = new(0, 0, 0), ["white"] = new(255, 255, 255), ["red"] = new(255, 0, 0), ["green"] = new(0, 128, 0),
        ["blue"] = new(0, 0, 255), ["yellow"] = new(255, 255, 0), ["magenta"] = new(255, 0, 255), ["cyan"] = new(0, 255, 255),
        ["gray"] = new(190, 190, 190), ["grey"] = new(190, 190, 190), ["orange"] = new(255, 165, 0), ["purple"] = new(160, 32, 240),
        ["brown"] = new(165, 42, 42),
    };

    /// <summary>Parses <c>#rgb</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>, <c>#rrrrggggbbbb</c> or a basic colour name.</summary>
    /// <param name="text">The text.</param>
    /// <param name="color">The colour.</param>
    /// <returns>True when it parsed.</returns>
    public static bool TryParse(string? text, out ThemeColor color)
    {
        color = default;
        string value = (text ?? string.Empty).Trim();
        if (Named.TryGetValue(value, out color)) return true;
        if (value.Length < 2 || value[0] != '#') return false;
        ReadOnlySpan<char> hex = value.AsSpan(1);
        foreach (char character in hex)
        {
            if (!char.IsAsciiHexDigit(character)) return false;
        }
        switch (hex.Length)
        {
            case 3:
                color = new ThemeColor(Expand(hex[0]), Expand(hex[1]), Expand(hex[2]));
                return true;
            case 6:
                color = new ThemeColor(Byte(hex[..2]), Byte(hex[2..4]), Byte(hex[4..6]));
                return true;
            case 8:
                color = new ThemeColor(Byte(hex[..2]), Byte(hex[2..4]), Byte(hex[4..6]), Byte(hex[6..8]));
                return true;
            case 12:
                color = new ThemeColor(Byte(hex[..2]), Byte(hex[4..6]), Byte(hex[8..10]));
                return true;
            default:
                return false;
        }
    }

    private static byte Expand(char digit) => (byte)(Convert.ToInt32(digit.ToString(), 16) * 17);

    private static byte Byte(ReadOnlySpan<char> digits) => byte.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary><c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when not opaque.</summary>
    /// <returns>The hexadecimal form.</returns>
    public string ToHex() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    /// <inheritdoc />
    public override string ToString() => ToHex();
}

/// <summary>One style of a style scheme as written (unset attributes are null).</summary>
/// <param name="Foreground">Text colour.</param>
/// <param name="Background">Background colour.</param>
/// <param name="Bold">Bold.</param>
/// <param name="Italic">Italic.</param>
/// <param name="Underline">Underlined.</param>
public sealed record SchemeStyle(ThemeColor? Foreground, ThemeColor? Background, bool? Bold, bool? Italic, bool? Underline);

/// <summary>The resolved look of one highlighting colour.</summary>
/// <param name="Foreground">Text colour; the editor text colour when the scheme does not set one.</param>
/// <param name="Background">Background, or null for none.</param>
/// <param name="Bold">Bold.</param>
/// <param name="Italic">Italic.</param>
/// <param name="Underline">Underlined.</param>
/// <param name="FromScheme">Whether a scheme style was found for the colour (false: plain text look).</param>
public sealed record SyntaxStyle(ThemeColor Foreground, ThemeColor? Background, bool Bold, bool Italic, bool Underline, bool FromScheme);

/// <summary>An editor colour theme read from a GtkSourceView style scheme: the editor colours and one style per highlighting colour.</summary>
public sealed class SyntaxTheme
{
    internal SyntaxTheme(string id, string name, IReadOnlyDictionary<string, SchemeStyle> schemeStyles)
    {
        Id = id;
        Name = name;
        SchemeStyles = schemeStyles;
        SchemeStyle? text = Style("text");
        Foreground = text?.Foreground ?? new ThemeColor(0, 0, 0);
        Background = text?.Background ?? new ThemeColor(255, 255, 255);
        SchemeStyle? selection = Style("selection");
        SelectionForeground = selection?.Foreground;
        SelectionBackground = selection?.Background;
        CurrentLineBackground = Style("current-line")?.Background;
        SchemeStyle? lineNumbers = Style("line-numbers");
        LineNumbersForeground = lineNumbers?.Foreground;
        LineNumbersBackground = lineNumbers?.Background;
        Caret = Style("cursor")?.Foreground;
        BracketMatchBackground = Style("bracket-match")?.Background;
        var colors = new Dictionary<string, SyntaxStyle>(StringComparer.Ordinal);
        foreach (HighlightingColorName color in HighlightingResources.AllColors)
        {
            SchemeStyle? found = null;
            foreach (string candidate in color.SchemeStyles)
            {
                found = Style(candidate);
                if (found != null) break;
            }
            colors[color.Name] = found == null
                ? new SyntaxStyle(Foreground, null, false, false, false, false)
                : new SyntaxStyle(found.Foreground ?? Foreground, found.Background, found.Bold ?? false, found.Italic ?? false, found.Underline ?? false, true);
        }
        Colors = colors;
    }

    /// <summary>The scheme id (for example <c>dracula</c>).</summary>
    public string Id { get; }

    /// <summary>The display name.</summary>
    public string Name { get; }

    /// <summary>Editor text colour.</summary>
    public ThemeColor Foreground { get; }

    /// <summary>Editor background.</summary>
    public ThemeColor Background { get; }

    /// <summary>Selected text colour, or null for the default.</summary>
    public ThemeColor? SelectionForeground { get; }

    /// <summary>Selection background, or null for the default.</summary>
    public ThemeColor? SelectionBackground { get; }

    /// <summary>Background of the caret line, or null for none.</summary>
    public ThemeColor? CurrentLineBackground { get; }

    /// <summary>Line number colour, or null for the default.</summary>
    public ThemeColor? LineNumbersForeground { get; }

    /// <summary>Line number margin background, or null for the default.</summary>
    public ThemeColor? LineNumbersBackground { get; }

    /// <summary>Caret colour, or null for the default.</summary>
    public ThemeColor? Caret { get; }

    /// <summary>Background of a matching bracket, or null for the default.</summary>
    public ThemeColor? BracketMatchBackground { get; }

    /// <summary>The style of every highlighting colour of both xshd files, by xshd colour name.</summary>
    public IReadOnlyDictionary<string, SyntaxStyle> Colors { get; }

    /// <summary>The scheme's styles by GtkSourceView name, with colour references and <c>use-style</c> resolved.</summary>
    public IReadOnlyDictionary<string, SchemeStyle> SchemeStyles { get; }

    private SchemeStyle? Style(string name) => SchemeStyles.TryGetValue(name, out SchemeStyle? style) ? style : null;
}

/// <summary>The result of loading a style scheme.</summary>
/// <param name="Theme">The theme, or null on error.</param>
/// <param name="Error">Why it failed, or null.</param>
/// <param name="Warnings">Problems that were skipped (unknown colours, broken <c>use-style</c> references).</param>
public sealed record SyntaxThemeLoadResult(SyntaxTheme? Theme, string? Error, IReadOnlyList<string> Warnings)
{
    /// <summary>Whether a theme was loaded.</summary>
    public bool Success => Theme != null;
}

/// <summary>Reads GtkSourceView style-scheme XML files and maps theme names to schemes (C++ <c>applyRemoteControlSourceStyle</c>).</summary>
public static class SyntaxThemeLoader
{
    /// <summary>The scheme used when no other applies.</summary>
    public const string DefaultSchemeId = "graalcolors";

    /// <summary>The built-in light scheme used for the "light" theme.</summary>
    public const string ClassicSchemeId = "classic";

    private static readonly string[] SchemeThemes = { "dracula", "material", "ayu-mirage", "nord", "monokai", "one-dark", "tokyo-night", "gruvbox", "solarized", "catppuccin" };

    private static readonly Lazy<SyntaxTheme> ClassicTheme = new(BuildClassic);

    /// <summary>
    /// The built-in "classic" light theme, modelled on GtkSourceView's classic scheme: black on white, blue comments, magenta
    /// literals, brown bold keywords, sea-green bold types, purple preprocessor, dark cyan functions.
    /// </summary>
    public static SyntaxTheme Classic => ClassicTheme.Value;

    /// <summary>The scheme id for a syntax theme name: the named dark schemes map to themselves, "light" to classic, anything else to graalcolors.</summary>
    /// <param name="themeName">The theme option value.</param>
    /// <returns>The scheme id.</returns>
    public static string SchemeIdForTheme(string? themeName)
    {
        string name = themeName ?? string.Empty;
        if (SchemeThemes.Contains(name, StringComparer.Ordinal)) return name;
        return name == "light" ? ClassicSchemeId : DefaultSchemeId;
    }

    /// <summary>The scheme file of an id (<c>&lt;id&gt;.xml</c>), or null for the built-in classic scheme.</summary>
    /// <param name="schemeId">The scheme id.</param>
    /// <returns>The file name or null.</returns>
    public static string? SchemeFileName(string schemeId) => schemeId == ClassicSchemeId ? null : schemeId + ".xml";

    /// <summary>Loads the theme for a theme name from the language-specs folder (the classic theme needs no file).</summary>
    /// <param name="themeName">The theme option value.</param>
    /// <param name="languageSpecsDirectory">The folder with the scheme files.</param>
    /// <returns>The result.</returns>
    public static SyntaxThemeLoadResult LoadForTheme(string? themeName, string languageSpecsDirectory)
    {
        string id = SchemeIdForTheme(themeName);
        string? file = SchemeFileName(id);
        if (file == null) return new SyntaxThemeLoadResult(Classic, null, Array.Empty<string>());
        return Load(Path.Combine(languageSpecsDirectory ?? string.Empty, file));
    }

    /// <summary>Loads a scheme file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The result; a missing or malformed file gives an error result.</returns>
    public static SyntaxThemeLoadResult Load(string path)
    {
        string xml;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Failure("The style scheme " + path + " does not exist");
            if (info.Length > 4 * 1024 * 1024) return Failure("The style scheme " + path + " is too large");
            xml = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return Failure("Cannot read " + path + ": " + exception.Message);
        }
        return Parse(xml, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Parses scheme XML.</summary>
    /// <param name="xml">The XML text.</param>
    /// <param name="fallbackId">The id to use when the root has none.</param>
    /// <returns>The result; malformed XML or a wrong root element gives an error result.</returns>
    public static SyntaxThemeLoadResult Parse(string? xml, string? fallbackId = null)
    {
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersFromEntities = 1024 };
            using var reader = XmlReader.Create(new StringReader(xml ?? string.Empty), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            return Failure("The style scheme is not valid XML: " + exception.Message);
        }
        XElement? root = document.Root;
        if (root == null || root.Name.LocalName != "style-scheme") return Failure("The file is not a style scheme");
        var warnings = new List<string>();
        var colors = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
        foreach (XElement element in root.Elements().Where(element => element.Name.LocalName == "color"))
        {
            string? name = (string?)element.Attribute("name");
            string? value = (string?)element.Attribute("value");
            if (string.IsNullOrEmpty(name)) continue;
            if (ThemeColor.TryParse(value, out ThemeColor color)) colors[name] = color;
            else warnings.Add("Colour '" + name + "' has an unknown value '" + value + "'");
        }
        var raw = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (XElement element in root.Elements().Where(element => element.Name.LocalName == "style"))
        {
            string? name = (string?)element.Attribute("name");
            if (!string.IsNullOrEmpty(name)) raw[name] = element;
        }
        var styles = new Dictionary<string, SchemeStyle>(StringComparer.Ordinal);
        foreach (string name in raw.Keys)
        {
            SchemeStyle? style = ResolveStyle(name, raw, colors, warnings, 0);
            if (style != null) styles[name] = style;
        }
        string id = (string?)root.Attribute("id") ?? fallbackId ?? string.Empty;
        string displayName = (string?)root.Attribute("_name") ?? (string?)root.Attribute("name") ?? id;
        return new SyntaxThemeLoadResult(new SyntaxTheme(id, displayName, styles), null, warnings);
    }

    private static SchemeStyle? ResolveStyle(string name, Dictionary<string, XElement> raw, Dictionary<string, ThemeColor> colors, List<string> warnings, int depth)
    {
        if (!raw.TryGetValue(name, out XElement? element)) return null;
        string? useStyle = (string?)element.Attribute("use-style");
        if (!string.IsNullOrEmpty(useStyle))
        {
            if (depth >= 16 || useStyle == name) { warnings.Add("Style '" + name + "' refers to itself through use-style"); return null; }
            SchemeStyle? target = ResolveStyle(useStyle, raw, colors, warnings, depth + 1);
            if (target == null) warnings.Add("Style '" + name + "' uses the unknown style '" + useStyle + "'");
            return target;
        }
        return new SchemeStyle(
            Color(element, "foreground", name, colors, warnings),
            Color(element, "background", name, colors, warnings),
            Flag(element, "bold"),
            Flag(element, "italic"),
            Flag(element, "underline"));
    }

    private static ThemeColor? Color(XElement element, string attribute, string style, Dictionary<string, ThemeColor> colors, List<string> warnings)
    {
        string? value = (string?)element.Attribute(attribute);
        if (string.IsNullOrEmpty(value)) return null;
        if (!value.StartsWith('#') && colors.TryGetValue(value, out ThemeColor named)) return named;
        if (ThemeColor.TryParse(value, out ThemeColor parsed)) return parsed;
        warnings.Add("Style '" + style + "' has an unknown " + attribute + " '" + value + "'");
        return null;
    }

    private static bool? Flag(XElement element, string attribute)
    {
        string? value = (string?)element.Attribute(attribute);
        if (value == null) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "single" or "double" or "low" or "error" => true,
            _ => false,
        };
    }

    private static SyntaxThemeLoadResult Failure(string error) => new(null, error, Array.Empty<string>());

    private static SyntaxTheme BuildClassic()
    {
        static SchemeStyle Fore(string hex, bool? bold = null) => new(Parse(hex), null, bold, null, null);
        static ThemeColor Parse(string hex) => ThemeColor.TryParse(hex, out ThemeColor color) ? color : default;
        var styles = new Dictionary<string, SchemeStyle>(StringComparer.Ordinal)
        {
            ["text"] = new(Parse("#000000"), Parse("#FFFFFF"), null, null, null),
            ["selection"] = new(Parse("#FFFFFF"), Parse("#4A90D9"), null, null, null),
            ["current-line"] = new(null, Parse("#EEEEEC"), null, null, null),
            ["line-numbers"] = new(Parse("#888A85"), Parse("#F6F6F4"), null, null, null),
            ["cursor"] = Fore("#000000"),
            ["bracket-match"] = new(Parse("#FFFFFF"), Parse("#BEBEBE"), true, null, null),
            ["def:comment"] = Fore("#0000FF"),
            ["def:string"] = Fore("#FF00FF"),
            ["def:constant"] = Fore("#FF00FF"),
            ["def:number"] = Fore("#FF00FF"),
            ["def:decimal"] = Fore("#FF00FF"),
            ["def:floating-point"] = Fore("#FF00FF"),
            ["def:keyword"] = Fore("#A52A2A", true),
            ["def:statement"] = Fore("#A52A2A", true),
            ["def:type"] = Fore("#2E8B57", true),
            ["def:preprocessor"] = Fore("#A020F0"),
            ["def:identifier"] = Fore("#008B8B"),
            ["def:function"] = Fore("#008B8B"),
            ["def:operator"] = Fore("#000000"),
            ["Others"] = Fore("#A52A2A", true),
            ["Others 4"] = Fore("#000000"),
            ["Others 5"] = Fore("#6A5ACD"),
            ["Operators"] = Fore("#000000"),
            ["Object Properties"] = Fore("#008B8B"),
            ["Comment 2"] = Fore("#A020F0", true),
            ["Comment 3"] = Fore("#0000FF"),
        };
        return new SyntaxTheme(ClassicSchemeId, "Classic", styles);
    }
}
