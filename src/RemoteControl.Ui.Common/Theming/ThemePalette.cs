using Avalonia.Media;

namespace RemoteControl.Ui.Common;

/// <summary>
/// The six colours of a user-interface theme, as the C++ CSS used them (<c>TMain.cpp:536-541</c>): the window background, the
/// surface of buttons, tabs, menus and header bars, the editor colour of text boxes, text views and lists, the text colour,
/// the accent (caret, focused border, checked tab) and the border colour.
/// </summary>
/// <param name="Id">The theme id, for example <c>nord</c>.</param>
/// <param name="DisplayName">The name the options window shows, for example <c>Nord</c>.</param>
/// <param name="Background">The window background.</param>
/// <param name="Surface">Buttons, tabs, menus, tool tips and selected rows.</param>
/// <param name="Editor">Text boxes, text views and lists.</param>
/// <param name="Text">The text colour.</param>
/// <param name="Accent">Caret, focused borders, checked boxes and the selected tab.</param>
/// <param name="Border">Borders and separators.</param>
/// <param name="IsDark">Whether the theme is dark (the option <c>darkmode</c>).</param>
public sealed record ThemePalette(string Id, string DisplayName, Color Background, Color Surface, Color Editor, Color Text, Color Accent, Color Border, bool IsDark)
{
    /// <summary>Text on a disabled control (<c>#828282</c> in the C++ on dark themes).</summary>
    public Color DisabledText => IsDark ? Color.FromRgb(0x82, 0x82, 0x82) : Color.FromRgb(0x8c, 0x8c, 0x8c);

    /// <summary>Text on a selected row (white in the C++ dark themes, the text colour in the light one).</summary>
    public Color SelectedText => IsDark ? Colors.White : Text;

    /// <summary>Text drawn on the accent colour (the editor colour, as the C++ completion rows did).</summary>
    public Color OnAccent => Editor;

    /// <summary>The colour of a row under the pointer: the editor colour mixed a tenth towards the text.</summary>
    public Color Hover => Mix(Editor, Text, 0.10);

    /// <summary>A colour between <paramref name="from"/> and <paramref name="to"/>.</summary>
    /// <param name="from">The first colour.</param>
    /// <param name="to">The second colour.</param>
    /// <param name="amount">0 gives <paramref name="from"/>, 1 gives <paramref name="to"/>.</param>
    /// <returns>The mixed, opaque colour.</returns>
    public static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        static byte Channel(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromRgb(Channel(from.R, to.R, amount), Channel(from.G, to.G, amount), Channel(from.B, to.B, amount));
    }

    /// <summary><paramref name="color"/> with another alpha.</summary>
    /// <param name="color">The colour.</param>
    /// <param name="alpha">The alpha, 0 to 1.</param>
    /// <returns>The colour with that alpha.</returns>
    public static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);
}
