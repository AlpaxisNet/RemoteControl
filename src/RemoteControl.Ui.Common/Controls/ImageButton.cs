using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering;

namespace RemoteControl.Ui.Common;

/// <summary>
/// A picture button, as the 15 header buttons of the RC window (<c>createGraphicalButton</c> and <c>onGraphicalButton</c>,
/// <c>TRemoteFrame.cpp:1993-2011, 1236-1258</c>): it shows <see cref="Source"/>, shows <see cref="PressedSource"/> while the left
/// button is held, and raises <see cref="Click"/> when the button is released over it (releasing elsewhere does nothing). Its size is
/// the picture's size unless set. The tool tip is the usual <c>ToolTip.Tip</c>.
/// </summary>
/// <example>
/// <code>
/// var options = ImageButton.FromAssets(services.Assets, "rc_options_normal.png", "rc_options_normal.png");
/// ToolTip.SetTip(options, "Options");
/// options.Click += UiSafe.Handler&lt;RoutedEventArgs&gt;((_, _) =&gt; OpenOptions(), "Header.Options");
/// Canvas.SetLeft(options, 394); Canvas.SetTop(options, 15);
/// </code>
/// In XAML: <c>&lt;common:ImageButton Source="{Binding Normal}" PressedSource="{Binding Pressed}" ToolTip.Tip="Options" /&gt;</c>
/// </example>
public sealed class ImageButton : Control, ICustomHitTest
{
    /// <summary>The picture shown normally.</summary>
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<ImageButton, IImage?>(nameof(Source));

    /// <summary>The picture shown while pressed (the normal one when <c>null</c>).</summary>
    public static readonly StyledProperty<IImage?> PressedSourceProperty = AvaloniaProperty.Register<ImageButton, IImage?>(nameof(PressedSource));

    /// <summary>Whether the left button is held on the control.</summary>
    public static readonly DirectProperty<ImageButton, bool> IsPressedProperty = AvaloniaProperty.RegisterDirect<ImageButton, bool>(nameof(IsPressed), button => button.IsPressed);

    /// <summary>Raised when the left button is released over the control after being pressed on it.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ClickEvent = RoutedEvent.Register<ImageButton, RoutedEventArgs>(nameof(Click), RoutingStrategies.Bubble);

    private bool _isPressed;

    static ImageButton()
    {
        AffectsRender<ImageButton>(SourceProperty, PressedSourceProperty, IsPressedProperty);
        AffectsMeasure<ImageButton>(SourceProperty);
        CursorProperty.OverrideDefaultValue<ImageButton>(new Cursor(StandardCursorType.Hand));
    }

    /// <summary>Raised when the button is clicked (released over it).</summary>
    public event EventHandler<RoutedEventArgs>? Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
    }

    /// <summary>The picture shown normally.</summary>
    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>The picture shown while pressed.</summary>
    public IImage? PressedSource
    {
        get => GetValue(PressedSourceProperty);
        set => SetValue(PressedSourceProperty, value);
    }

    /// <summary>Whether the left button is held on the control.</summary>
    public bool IsPressed
    {
        get => _isPressed;
        private set
        {
            if (SetAndRaise(IsPressedProperty, ref _isPressed, value)) PseudoClasses.Set(":pressed", value);
        }
    }

    /// <summary>
    /// A button with the pictures of the options: <paramref name="normal"/> and the pressed picture chosen by
    /// <see cref="AssetStore.PressedImageName"/>. A missing picture shows the store's placeholder.
    /// </summary>
    /// <param name="assets">The pictures.</param>
    /// <param name="normal">The option <c>icon&lt;name&gt;</c>.</param>
    /// <param name="pressed">The option <c>icon&lt;name&gt;pressed</c>.</param>
    /// <returns>The button.</returns>
    public static ImageButton FromAssets(AssetStore assets, string normal, string pressed)
    {
        ArgumentNullException.ThrowIfNull(assets);
        return new ImageButton { Source = assets.Get(normal), PressedSource = assets.Get(assets.PressedImageName(normal, pressed)) };
    }

    /// <summary>Raises <see cref="Click"/> as if the button had been clicked.</summary>
    public void PerformClick() => RaiseEvent(new RoutedEventArgs(ClickEvent, this));

    /// <summary>The whole rectangle of the button reacts to the pointer, also where the picture is transparent.</summary>
    /// <param name="point">A point in the button's coordinates.</param>
    /// <returns>Whether the point is inside the button.</returns>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        IImage? image = IsPressed ? PressedSource ?? Source : Source;
        if (image is null) return;
        Size size = image.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        context.DrawImage(image, new Rect(size), new Rect(Bounds.Size));
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => Source?.Size ?? default;

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        IsPressed = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsPressed || e.InitialPressMouseButton != MouseButton.Left) return;
        IsPressed = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        Point point = e.GetPosition(this);
        if (new Rect(Bounds.Size).Contains(point)) RaiseEvent(new RoutedEventArgs(ClickEvent, this));
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        IsPressed = false;
    }
}
