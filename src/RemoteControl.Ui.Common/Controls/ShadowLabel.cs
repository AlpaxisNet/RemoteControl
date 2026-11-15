using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RemoteControl.Ui.Common;

/// <summary>
/// Text with a one-pixel outline, as the header labels of the RC window (server, players, NPC server): the C++ drew eight copies
/// in <c>colorlabelback</c> around the text in <c>colorlabel</c> (<c>TRemoteFrame.cpp:569-609</c>), font "Sans Bold 12" (16 pixels).
/// By default the colours follow the theme resources <see cref="ThemeKeys.LabelBrush"/> and <see cref="ThemeKeys.LabelBackBrush"/>.
/// </summary>
/// <example>
/// <code>
/// var server = new ShadowLabel { Text = options.LabelServers + " " + serverName };
/// Canvas.SetLeft(server, 10); Canvas.SetTop(server, 90);
/// </code>
/// In XAML: <c>&lt;common:ShadowLabel Text="Players: 12" TextAlignment="Center" Width="500" /&gt;</c>
/// </example>
public sealed class ShadowLabel : Control
{
    /// <summary>The text.</summary>
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<ShadowLabel, string?>(nameof(Text));

    /// <summary>The text colour (<c>colorlabel</c>).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<ShadowLabel, IBrush?>(nameof(Foreground));

    /// <summary>The outline colour (<c>colorlabelback</c>).</summary>
    public static readonly StyledProperty<IBrush?> OutlineProperty = AvaloniaProperty.Register<ShadowLabel, IBrush?>(nameof(Outline));

    /// <summary>The font family.</summary>
    public static readonly StyledProperty<FontFamily> FontFamilyProperty = AvaloniaProperty.Register<ShadowLabel, FontFamily>(nameof(FontFamily), FontFamily.Default);

    /// <summary>The font size in pixels (16, as "Sans Bold 12").</summary>
    public static readonly StyledProperty<double> FontSizeProperty = AvaloniaProperty.Register<ShadowLabel, double>(nameof(FontSize), 16);

    /// <summary>The font weight (bold).</summary>
    public static readonly StyledProperty<FontWeight> FontWeightProperty = AvaloniaProperty.Register<ShadowLabel, FontWeight>(nameof(FontWeight), FontWeight.Bold);

    /// <summary>Where the text is placed horizontally within the control's width.</summary>
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty = AvaloniaProperty.Register<ShadowLabel, TextAlignment>(nameof(TextAlignment), TextAlignment.Left);

    private static readonly Point[] Offsets = [new(-1, -1), new(0, -1), new(1, -1), new(-1, 0), new(1, 0), new(-1, 1), new(0, 1), new(1, 1)];
    private FormattedText? _text;
    private FormattedText? _outline;

    static ShadowLabel()
    {
        AffectsRender<ShadowLabel>(ForegroundProperty, OutlineProperty, TextAlignmentProperty);
        AffectsMeasure<ShadowLabel>(TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty);
    }

    /// <summary>Creates a label whose colours follow the theme's label resources until set.</summary>
    public ShadowLabel()
    {
        this[!ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(ThemeKeys.LabelBrush);
        this[!OutlineProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(ThemeKeys.LabelBackBrush);
    }

    /// <summary>The text.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The text colour.</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The outline colour.</summary>
    public IBrush? Outline
    {
        get => GetValue(OutlineProperty);
        set => SetValue(OutlineProperty, value);
    }

    /// <summary>The font family.</summary>
    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>The font size in pixels.</summary>
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>The font weight.</summary>
    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    /// <summary>Where the text is placed horizontally.</summary>
    public TextAlignment TextAlignment
    {
        get => GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == FontFamilyProperty || change.Property == FontSizeProperty || change.Property == FontWeightProperty
            || change.Property == ForegroundProperty || change.Property == OutlineProperty || change.Property == FlowDirectionProperty)
        {
            _text = null;
            _outline = null;
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        FormattedText text = Format(Foreground);
        return new Size(text.WidthIncludingTrailingWhitespace + 2, text.Height + 2);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Text)) return;
        _text ??= Format(Foreground ?? Brushes.White);
        _outline ??= Format(Outline ?? Brushes.Black);
        double free = Math.Max(0, Bounds.Width - 2 - _text.WidthIncludingTrailingWhitespace);
        double x = 1 + TextAlignment switch
        {
            TextAlignment.Center => free / 2,
            TextAlignment.Right or TextAlignment.End => free,
            _ => 0,
        };
        var origin = new Point(x, 1);
        foreach (Point offset in Offsets) context.DrawText(_outline, origin + offset);
        context.DrawText(_text, origin);
    }

    private FormattedText Format(IBrush? brush) =>
        new(Text ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection, new Typeface(FontFamily, FontStyle.Normal, FontWeight), Math.Max(1, FontSize), brush);
}
