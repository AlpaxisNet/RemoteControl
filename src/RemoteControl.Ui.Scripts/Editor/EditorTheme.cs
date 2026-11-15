using System.Xml;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using RemoteControl.Core;
using RemoteControl.Scripting;

namespace RemoteControl.Ui.Scripts;

/// <summary>
/// The syntax theme of the script editors (C++ <c>applyRemoteControlSourceStyle</c>): the style scheme of the <c>syntaxtheme</c> option,
/// or of the user-interface theme when <c>syncsyntaxtheme</c> is on, and the AvaloniaEdit highlighting definitions coloured with it.
/// Themes and definitions are cached per scheme; used on the UI thread.
/// </summary>
internal static class EditorTheme
{
    private static readonly Dictionary<string, SyntaxTheme> s_themes = new(StringComparer.Ordinal);
    private static readonly Dictionary<(HighlightingLanguage, string), IHighlightingDefinition> s_definitions = new();

    /// <summary>The theme name the options choose for the editors.</summary>
    /// <param name="options">The options.</param>
    /// <returns>The name.</returns>
    public static string ThemeNameFor(RcOptions options) => options.SyncSyntaxTheme ? options.Theme : options.SyntaxTheme;

    /// <summary>The syntax theme for the options (the classic one when the scheme file is missing or broken).</summary>
    /// <param name="options">The options.</param>
    /// <param name="languageSpecsDirectory">The folder with the scheme files.</param>
    /// <returns>The theme.</returns>
    public static SyntaxTheme For(RcOptions options, string languageSpecsDirectory)
    {
        string name = ThemeNameFor(options);
        string key = SyntaxThemeLoader.SchemeIdForTheme(name) + "|" + languageSpecsDirectory;
        if (s_themes.TryGetValue(key, out SyntaxTheme? cached)) return cached;
        SyntaxThemeLoadResult result = SyntaxThemeLoader.LoadForTheme(name, languageSpecsDirectory);
        SyntaxTheme theme = result.Theme ?? SyntaxThemeLoader.Classic;
        if (result.Theme is null && result.Error is not null) Ui.Common.UiSafe.Log.Warn("Syntax theme: " + result.Error);
        s_themes[key] = theme;
        return theme;
    }

    /// <summary>The highlighting of a language coloured with a theme, or null for plain text.</summary>
    /// <param name="language">The language.</param>
    /// <param name="theme">The theme.</param>
    /// <returns>The definition, or null.</returns>
    public static IHighlightingDefinition? Definition(HighlightingLanguage language, SyntaxTheme theme)
    {
        if (language == HighlightingLanguage.None) return null;
        if (s_definitions.TryGetValue((language, theme.Id), out IHighlightingDefinition? cached)) return cached;
        using Stream? stream = HighlightingResources.Open(language);
        if (stream is null) return null;
        XshdSyntaxDefinition xshd;
        using (XmlReader reader = XmlReader.Create(stream)) xshd = HighlightingLoader.LoadXshd(reader);
        foreach (XshdColor color in xshd.Elements.OfType<XshdColor>())
        {
            if (color.Name is null || !theme.Colors.TryGetValue(color.Name, out SyntaxStyle? style)) continue;
            color.Foreground = new SimpleHighlightingBrush(ToColor(style.Foreground));
            color.Background = style.Background is { } background ? new SimpleHighlightingBrush(ToColor(background)) : null;
            color.FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal;
            color.FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal;
            color.Underline = style.Underline;
        }
        IHighlightingDefinition definition = HighlightingLoader.Load(xshd, HighlightingManager.Instance);
        s_definitions[(language, theme.Id)] = definition;
        return definition;
    }

    /// <summary>Converts a theme colour.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The Avalonia colour.</returns>
    public static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    /// <summary>A brush of a theme colour, or a fallback.</summary>
    /// <param name="color">The colour or null.</param>
    /// <param name="fallback">The colour to use without one.</param>
    /// <returns>The brush.</returns>
    public static IBrush Brush(ThemeColor? color, Color fallback) => new SolidColorBrush(color is { } value ? ToColor(value) : fallback);
}
