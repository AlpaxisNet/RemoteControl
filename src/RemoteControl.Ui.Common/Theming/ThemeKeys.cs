namespace RemoteControl.Ui.Common;

/// <summary>
/// The application resource keys <see cref="ThemeManager"/> keeps up to date. Use them with <c>DynamicResource</c> so that a control
/// follows theme and option changes without code: brushes (<c>...Brush</c>, <see cref="Avalonia.Media.IBrush"/>), colours
/// (<c>...Color</c>, <see cref="Avalonia.Media.Color"/>), fonts (<see cref="Avalonia.Media.FontFamily"/>) and sizes (<see cref="double"/>,
/// in device-independent pixels).
/// </summary>
/// <example>
/// <code>
/// &lt;Border Background="{DynamicResource RcSurfaceBrush}" BorderBrush="{DynamicResource RcBorderBrush}"&gt;
///   &lt;TextBlock Foreground="{DynamicResource RcChatBoldBrush}" FontFamily="{DynamicResource RcChatFontFamily}" /&gt;
/// &lt;/Border&gt;
/// </code>
/// In code: <c>control[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(ThemeKeys.AccentBrush);</c> or
/// <c>control.Bind(TextBlock.ForegroundProperty, control.GetResourceObservable(ThemeKeys.AccentBrush));</c>
/// </example>
public static class ThemeKeys
{
    /// <summary>The window background of the palette.</summary>
    public const string BackgroundBrush = "RcBackgroundBrush";

    /// <summary>The surface (buttons, tabs, menus) of the palette.</summary>
    public const string SurfaceBrush = "RcSurfaceBrush";

    /// <summary>The editor colour (text boxes, lists) of the palette.</summary>
    public const string EditorBrush = "RcEditorBrush";

    /// <summary>The text colour of the palette.</summary>
    public const string TextBrush = "RcTextBrush";

    /// <summary>The accent of the palette.</summary>
    public const string AccentBrush = "RcAccentBrush";

    /// <summary>Text drawn on the accent.</summary>
    public const string OnAccentBrush = "RcOnAccentBrush";

    /// <summary>The border colour of the palette.</summary>
    public const string BorderBrush = "RcBorderBrush";

    /// <summary>Text of disabled controls.</summary>
    public const string DisabledTextBrush = "RcDisabledTextBrush";

    /// <summary>The window background as a colour.</summary>
    public const string BackgroundColor = "RcBackgroundColor";

    /// <summary>The surface as a colour.</summary>
    public const string SurfaceColor = "RcSurfaceColor";

    /// <summary>The editor colour as a colour.</summary>
    public const string EditorColor = "RcEditorColor";

    /// <summary>The text colour as a colour.</summary>
    public const string TextColor = "RcTextColor";

    /// <summary>The accent as a colour.</summary>
    public const string AccentColor = "RcAccentColor";

    /// <summary>The border colour as a colour.</summary>
    public const string BorderColor = "RcBorderColor";

    /// <summary>The chat text (option <c>colorchat</c>).</summary>
    public const string ChatBrush = "RcChatBrush";

    /// <summary>The chat background (option <c>colorchatback</c>).</summary>
    public const string ChatBackBrush = "RcChatBackBrush";

    /// <summary>The bold chat prefix, time stamps and links (option <c>colorchatbold</c>).</summary>
    public const string ChatBoldBrush = "RcChatBoldBrush";

    /// <summary>The <c>#ALERT</c> lines (option <c>coloralert</c>).</summary>
    public const string AlertBrush = "RcAlertBrush";

    /// <summary>The option <c>colorlink</c> (kept; the C++ coloured links with <c>colorchatbold</c>, and so does <see cref="ChatView"/>).</summary>
    public const string LinkBrush = "RcLinkBrush";

    /// <summary>The header labels (option <c>colorlabel</c>).</summary>
    public const string LabelBrush = "RcLabelBrush";

    /// <summary>The outline of the header labels (option <c>colorlabelback</c>).</summary>
    public const string LabelBackBrush = "RcLabelBackBrush";

    /// <summary>The chat entry text (option <c>coloredit</c>).</summary>
    public const string EditBrush = "RcEditBrush";

    /// <summary>The chat entry background (option <c>coloreditback</c>).</summary>
    public const string EditBackBrush = "RcEditBackBrush";

    /// <summary>The chat font (option <c>chatfontfamily</c>).</summary>
    public const string ChatFontFamily = "RcChatFontFamily";

    /// <summary>The chat font size in pixels (option <c>chatfontsize</c>, which is in points).</summary>
    public const string ChatFontSize = "RcChatFontSize";

    /// <summary>The script font (option <c>scriptfontfamily</c>).</summary>
    public const string ScriptFontFamily = "RcScriptFontFamily";

    /// <summary>The script font size in pixels (option <c>scriptfontsize</c>, which is in points).</summary>
    public const string ScriptFontSize = "RcScriptFontSize";

    /// <summary>The corner radius of controls (4, or 0 without the option <c>roundedcorners</c>), a <see cref="Avalonia.CornerRadius"/>.</summary>
    public const string ControlCornerRadius = "RcControlCornerRadius";

    /// <summary>The corner radius of popups and panels (8, or 0 without the option <c>roundedcorners</c>), a <see cref="Avalonia.CornerRadius"/>.</summary>
    public const string OverlayCornerRadius = "RcOverlayCornerRadius";
}
