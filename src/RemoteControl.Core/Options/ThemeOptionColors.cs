using System.Globalization;

namespace RemoteControl.Core;

/// <summary>
/// The chat colours a theme writes into the options when <see cref="RcOptions.SyncColors"/> is on (<c>themeColors</c>,
/// <c>TOptionsWindow.cpp:112-125</c>), and the background tint derived from a theme (<c>tintForTheme</c>). The theme palettes
/// themselves (window colours) belong to the user interface.
/// </summary>
public static class ThemeOptionColors
{
    /// <summary>The seven option colours of one theme.</summary>
    /// <param name="Edit">The chat entry text colour (<c>coloredit</c>).</param>
    /// <param name="EditBack">The chat entry background (<c>coloreditback</c>).</param>
    /// <param name="Chat">The chat text colour (<c>colorchat</c>).</param>
    /// <param name="ChatBold">The bold chat colour (<c>colorchatbold</c>).</param>
    /// <param name="ChatBack">The chat background (<c>colorchatback</c>).</param>
    /// <param name="Label">The label colour (<c>colorlabel</c>).</param>
    /// <param name="LabelBack">The label background (<c>colorlabelback</c>).</param>
    public sealed record Palette(string Edit, string EditBack, string Chat, string ChatBold, string ChatBack, string Label, string LabelBack);

    /// <summary>The theme ids that have a palette of their own; any other id (including <c>dark</c> and <c>system</c>) uses the <c>dark</c> colours.</summary>
    public static IReadOnlyList<string> ThemeIds { get; } = ["dracula", "material", "ayu-mirage", "nord", "monokai", "one-dark", "tokyo-night", "gruvbox", "solarized", "catppuccin", "light"];

    /// <summary>The colours of <paramref name="theme"/>.</summary>
    /// <param name="theme">A theme id.</param>
    /// <returns>The palette.</returns>
    public static Palette For(string? theme) => theme switch
    {
        "dracula" => Same("#f8f8f2", "#282a36", "#50fa7b"),
        "material" => Same("#eeffff", "#263238", "#80cbc4"),
        "ayu-mirage" => Same("#cccac2", "#242936", "#ffcc66"),
        "nord" => Same("#eceff4", "#2e3440", "#88c0d0"),
        "monokai" => Same("#f8f8f2", "#1e1f1c", "#a6e22e"),
        "one-dark" => Same("#abb2bf", "#282c34", "#61afef"),
        "tokyo-night" => Same("#c0caf5", "#1a1b26", "#7aa2f7"),
        "gruvbox" => Same("#ebdbb2", "#282828", "#fabd2f"),
        "solarized" => Same("#839496", "#002b36", "#b58900"),
        "catppuccin" => Same("#cdd6f4", "#1e1e2e", "#cba6f7"),
        "light" => Same("#202020", "#ffffff", "#008000"),
        _ => new Palette("#00ff00", "#1e1e1e", "#d4d4d4", "#00C000", "#1e1e1e", "#00C000", "#1e1e1e"),
    };

    /// <summary>A copy of <paramref name="options"/> with the seven colours of <paramref name="theme"/>.</summary>
    /// <param name="options">The options to change.</param>
    /// <param name="theme">A theme id.</param>
    /// <returns>The changed copy.</returns>
    public static RcOptions Apply(RcOptions options, string? theme)
    {
        ArgumentNullException.ThrowIfNull(options);
        Palette palette = For(theme);
        return options with
        {
            ColorEdit = palette.Edit,
            ColorEditBack = palette.EditBack,
            ColorChat = palette.Chat,
            ColorChatBold = palette.ChatBold,
            ColorChatBack = palette.ChatBack,
            ColorLabel = palette.Label,
            ColorLabelBack = palette.LabelBack,
        };
    }

    /// <summary>The background tint of a theme: its chat background with alpha <c>0x60</c>, as <c>#rrggbbaa</c>.</summary>
    /// <param name="theme">A theme id.</param>
    /// <returns>The tint.</returns>
    public static string TintFor(string? theme) => TintForColor(For(theme).ChatBack);

    /// <summary>A colour <c>#rgb</c> or <c>#rrggbb</c> with alpha <c>0x60</c>; <c>#00000000</c> when it does not parse.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The tint as <c>#rrggbbaa</c> in lower case.</returns>
    public static string TintForColor(string? color)
    {
        if (color is null || color.Length is not (4 or 7) || color[0] != '#') return "#00000000";
        string hex = color.Length == 4 ? string.Concat(color[1], color[1], color[2], color[2], color[3], color[3]) : color[1..];
        if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)) return "#00000000";
        return "#" + hex.ToLowerInvariant() + "60";
    }

    private static Palette Same(string text, string back, string accent) => new(text, back, text, accent, back, accent, back);
}
