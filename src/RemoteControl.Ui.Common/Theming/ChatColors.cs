using Avalonia.Media;
using Avalonia.Media.Immutable;
using RemoteControl.Core;

namespace RemoteControl.Ui.Common;

/// <summary>
/// The per-option colours of the chat views, the chat entry and the header labels, read from <see cref="RcOptions"/>. A value that
/// does not parse falls back to the palette (text, editor, accent) or to the C++ default (alert <c>#f48771</c>, link <c>#4ec9b0</c>).
/// With the option <c>synccolors</c> on, choosing a theme writes these options from the theme (<see cref="ThemeManager.ChooseTheme"/>).
/// </summary>
/// <param name="Chat">Chat text (<c>colorchat</c>).</param>
/// <param name="ChatBack">Chat background (<c>colorchatback</c>).</param>
/// <param name="ChatBold">Bold prefixes, time stamps and links (<c>colorchatbold</c>).</param>
/// <param name="Alert"><c>#ALERT</c> lines (<c>coloralert</c>).</param>
/// <param name="Link">The option <c>colorlink</c> (unused by the C++ chat, which coloured links with <paramref name="ChatBold"/>).</param>
/// <param name="Label">Header labels (<c>colorlabel</c>).</param>
/// <param name="LabelBack">Header label outline (<c>colorlabelback</c>).</param>
/// <param name="Edit">Chat entry text (<c>coloredit</c>).</param>
/// <param name="EditBack">Chat entry background (<c>coloreditback</c>).</param>
public sealed record ChatColors(Color Chat, Color ChatBack, Color ChatBold, Color Alert, Color Link, Color Label, Color LabelBack, Color Edit, Color EditBack)
{
    /// <summary>Reads the colours of <paramref name="options"/>.</summary>
    /// <param name="options">The options.</param>
    /// <param name="palette">The palette used for values that do not parse.</param>
    /// <returns>The colours.</returns>
    public static ChatColors From(RcOptions options, ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(palette);
        return new ChatColors(
            ColorParser.Parse(options.ColorChat, palette.Text),
            ColorParser.Parse(options.ColorChatBack, palette.Editor),
            ColorParser.Parse(options.ColorChatBold, palette.Accent),
            ColorParser.Parse(options.ColorAlert, Color.FromRgb(0xf4, 0x87, 0x71)),
            ColorParser.Parse(options.ColorLink, Color.FromRgb(0x4e, 0xc9, 0xb0)),
            ColorParser.Parse(options.ColorLabel, palette.Accent),
            ColorParser.Parse(options.ColorLabelBack, palette.Editor),
            ColorParser.Parse(options.ColorEdit, palette.Text),
            ColorParser.Parse(options.ColorEditBack, palette.Editor));
    }

    /// <summary>The chat text as a brush.</summary>
    public IBrush ChatBrush => new ImmutableSolidColorBrush(Chat);

    /// <summary>The chat background as a brush.</summary>
    public IBrush ChatBackBrush => new ImmutableSolidColorBrush(ChatBack);

    /// <summary>The bold chat colour as a brush.</summary>
    public IBrush ChatBoldBrush => new ImmutableSolidColorBrush(ChatBold);

    /// <summary>The alert colour as a brush.</summary>
    public IBrush AlertBrush => new ImmutableSolidColorBrush(Alert);

    /// <summary>The label colour as a brush.</summary>
    public IBrush LabelBrush => new ImmutableSolidColorBrush(Label);

    /// <summary>The label outline as a brush.</summary>
    public IBrush LabelBackBrush => new ImmutableSolidColorBrush(LabelBack);

    /// <summary>The chat entry text as a brush.</summary>
    public IBrush EditBrush => new ImmutableSolidColorBrush(Edit);

    /// <summary>The chat entry background as a brush.</summary>
    public IBrush EditBackBrush => new ImmutableSolidColorBrush(EditBack);
}

/// <summary>Turns the font options (GTK/Pango family names, sizes in points) into Avalonia fonts.</summary>
public static class ThemeFonts
{
    /// <summary>Monospaced families tried in order for <c>Monospace</c>.</summary>
    public const string MonospaceFamilies = "Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, Noto Sans Mono, Menlo, monospace";

    /// <summary>
    /// The font family of an option value: <c>Sans</c> (the C++ default) and <c>sans-serif</c> give the application's default
    /// font, <c>Monospace</c> a list of common monospaced fonts, <c>Serif</c> a list of serif fonts; any other value is used as it is
    /// (Avalonia falls back to the default font when it is not installed).
    /// </summary>
    /// <param name="family">The option value, for example <c>chatfontfamily</c>.</param>
    /// <returns>The family.</returns>
    public static FontFamily FromOption(string? family)
    {
        string value = (family ?? string.Empty).Trim();
        try
        {
            return value.ToLowerInvariant() switch
            {
                "" or "sans" or "sans-serif" or "sans serif" => FontFamily.Default,
                "monospace" or "mono" => new FontFamily(MonospaceFamilies),
                "serif" => new FontFamily("DejaVu Serif, Times New Roman, Liberation Serif, serif"),
                _ => new FontFamily(value),
            };
        }
        catch (Exception)
        {
            return FontFamily.Default;
        }
    }

    /// <summary>Converts a size in points (as Pango read the options) to device-independent pixels (96 per inch).</summary>
    /// <param name="points">The size in points.</param>
    /// <returns>The size in pixels, at least 1.</returns>
    public static double PointsToPixels(int points) => Math.Max(1, points) * 96.0 / 72.0;
}
