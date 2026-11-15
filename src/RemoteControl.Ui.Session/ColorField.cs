using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Session;

/// <summary>
/// A colour option of the options window (the C++ <c>GtkColorButton</c>): a swatch with the value; clicking it opens a chooser with the
/// hexadecimal value, red, green and blue (and alpha for the background tint) and a few swatches of the theme palettes. The value is
/// written as the C++ wrote it: <c>#rrggbb</c>, or <c>#rrggbbaa</c> with <see cref="AllowAlpha"/>.
/// </summary>
internal sealed class ColorField : UserControl
{
    private static readonly string[] s_swatches =
    [
        "#000000", "#1e1e1e", "#1e1e2e", "#282a36", "#2e3440", "#002b36", "#ffffff", "#d4d4d4",
        "#cdd6f4", "#f8f8f2", "#cba6f7", "#00c000", "#00ff00", "#50fa7b", "#88c0d0", "#61afef",
        "#7aa2f7", "#ffcc66", "#fabd2f", "#b58900", "#a6e22e", "#f48771", "#ff5555", "#008000",
    ];

    private readonly Border _swatch = new() { Width = 28, Height = 16, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly Button _button;
    private readonly TextBox _hex = new() { Name = "ColorHex", Width = 110 };
    private readonly Slider[] _sliders = new Slider[4];
    private Color _color = Colors.Black;
    private bool _updating;
    private bool _allowAlpha;

    /// <summary>Creates the field.</summary>
    public ColorField()
    {
        _swatch.BorderBrush = Brushes.Gray;
        _button = new Button
        {
            Name = "ColorButton",
            Padding = new Thickness(6, 3),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _swatch, _label } },
        };
        _button.Flyout = new Flyout { Content = BuildChooser(), Placement = PlacementMode.BottomEdgeAlignedLeft };
        Content = _button;
        Show();
    }

    /// <summary>Raised when the user chose a colour.</summary>
    public event EventHandler? ColorChanged;

    /// <summary>Whether the colour has an alpha value.</summary>
    public bool AllowAlpha
    {
        get => _allowAlpha;
        set
        {
            _allowAlpha = value;
            _sliders[3].IsVisible = value;
            if (!value) _color = Color.FromRgb(_color.R, _color.G, _color.B);
            Show();
        }
    }

    /// <summary>The colour as the options file holds it.</summary>
    public string Value
    {
        get => Format(_color, AllowAlpha);
        set
        {
            Color color = ColorParser.Parse(value, Colors.Black);
            _color = _allowAlpha ? color : Color.FromRgb(color.R, color.G, color.B);
            Show();
        }
    }

    /// <summary>Sets the colour as the user would (raises <see cref="ColorChanged"/>).</summary>
    /// <param name="value">The colour text.</param>
    public void Choose(string value)
    {
        if (!ColorParser.TryParse(value, out Color color)) return;
        if (!AllowAlpha) color = Color.FromRgb(color.R, color.G, color.B);
        if (color == _color) return;
        _color = color;
        Show();
        UiSafe.Run(() => ColorChanged?.Invoke(this, EventArgs.Empty), "ColorField.ColorChanged");
    }

    /// <summary>The C++ text of a colour.</summary>
    /// <param name="color">The colour.</param>
    /// <param name="alpha">Whether to write the alpha value.</param>
    /// <returns>The text.</returns>
    public static string Format(Color color, bool alpha) =>
        "#" + color.R.ToString("x2", CultureInfo.InvariantCulture) + color.G.ToString("x2", CultureInfo.InvariantCulture) + color.B.ToString("x2", CultureInfo.InvariantCulture)
        + (alpha ? color.A.ToString("x2", CultureInfo.InvariantCulture) : string.Empty);

    private Control BuildChooser()
    {
        var panel = new StackPanel { Spacing = 6, Width = 230 };
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) Choose(_hex.Text ?? string.Empty);
        };
        _hex.LostFocus += (_, _) => Choose(_hex.Text ?? string.Empty);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = "Hex:", VerticalAlignment = VerticalAlignment.Center }, _hex } });
        string[] names = ["Red", "Green", "Blue", "Alpha"];
        for (int index = 0; index < 4; index++)
        {
            var slider = new Slider { Minimum = 0, Maximum = 255, Name = "Color" + names[index], IsVisible = index < 3 };
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty && !_updating) FromSliders();
            };
            _sliders[index] = slider;
            panel.Children.Add(slider);
        }
        var swatches = new WrapPanel();
        foreach (string swatch in s_swatches)
        {
            var button = new Button { Width = 24, Height = 20, Margin = new Thickness(1), Padding = new Thickness(0), Background = new SolidColorBrush(Color.Parse(swatch)) };
            ToolTip.SetTip(button, swatch);
            string value = swatch;
            button.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Choose(AllowAlpha ? value + _color.A.ToString("x2", CultureInfo.InvariantCulture) : value), "ColorField.Swatch");
            swatches.Children.Add(button);
        }
        panel.Children.Add(swatches);
        return panel;
    }

    private void FromSliders()
    {
        byte alpha = AllowAlpha ? (byte)_sliders[3].Value : (byte)255;
        Choose(Format(Color.FromArgb(alpha, (byte)_sliders[0].Value, (byte)_sliders[1].Value, (byte)_sliders[2].Value), AllowAlpha));
    }

    private void Show()
    {
        _updating = true;
        try
        {
            string text = Format(_color, AllowAlpha);
            _swatch.Background = new SolidColorBrush(_color);
            _label.Text = text;
            _hex.Text = text;
            _sliders[0].Value = _color.R;
            _sliders[1].Value = _color.G;
            _sliders[2].Value = _color.B;
            _sliders[3].Value = _color.A;
        }
        finally
        {
            _updating = false;
        }
    }
}
