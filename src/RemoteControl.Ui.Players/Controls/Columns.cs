using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>The widths of the columns of a list, shared by its header and its rows.</summary>
internal sealed class ColumnLayout
{
    private readonly double[] _widths;

    /// <summary>Creates a layout.</summary>
    /// <param name="titles">The header texts.</param>
    /// <param name="widths">The starting widths.</param>
    /// <param name="rightAligned">Which columns are right aligned.</param>
    public ColumnLayout(string[] titles, double[] widths, bool[]? rightAligned = null)
    {
        Titles = titles;
        _widths = (double[])widths.Clone();
        RightAligned = rightAligned ?? new bool[titles.Length];
    }

    /// <summary>The header texts.</summary>
    public IReadOnlyList<string> Titles { get; }

    /// <summary>Which columns are right aligned.</summary>
    public IReadOnlyList<bool> RightAligned { get; }

    /// <summary>How many columns.</summary>
    public int Count => _widths.Length;

    /// <summary>Raised when a width changed.</summary>
    public event EventHandler? Changed;

    /// <summary>A column's width.</summary>
    /// <param name="index">The column.</param>
    /// <returns>The width.</returns>
    public double Width(int index) => index >= 0 && index < _widths.Length ? _widths[index] : 0;

    /// <summary>Changes a column's width (at least 16).</summary>
    /// <param name="index">The column.</param>
    /// <param name="width">The width.</param>
    public void SetWidth(int index, double width)
    {
        if (index < 0 || index >= _widths.Length) return;
        double value = Math.Max(16, width);
        if (Math.Abs(_widths[index] - value) < 0.1) return;
        _widths[index] = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The sum of the widths.</summary>
    public double Total => _widths.Sum();
}

/// <summary>
/// A row of cells laid out by the inherited <see cref="LayoutProperty"/>: child i gets column i's width (the last one takes the rest
/// of the row). Set the layout on the list; every row inside inherits it.
/// </summary>
internal sealed class ColumnsPanel : Panel
{
    /// <summary>The layout, inherited down the tree.</summary>
    public static readonly AttachedProperty<ColumnLayout?> LayoutProperty =
        AvaloniaProperty.RegisterAttached<ColumnsPanel, Control, ColumnLayout?>("Layout", inherits: true);

    private ColumnLayout? _hooked;

    /// <summary>Gets the layout of a control.</summary>
    public static ColumnLayout? GetLayout(Control control) => control.GetValue(LayoutProperty);

    /// <summary>Sets the layout of a control (and what it contains).</summary>
    public static void SetLayout(Control control, ColumnLayout? value) => control.SetValue(LayoutProperty, value);

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayoutProperty) Hook(change.GetNewValue<ColumnLayout?>());
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hook(GetLayout(this));
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Hook(null);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        ColumnLayout? layout = GetLayout(this);
        double total = 0, height = 0;
        for (int index = 0; index < Children.Count; index++)
        {
            double width = layout?.Width(index) ?? 100;
            if (layout is not null && index == Children.Count - 1 && !double.IsInfinity(availableSize.Width)) width = Math.Max(width, availableSize.Width - total);
            Children[index].Measure(new Size(width, availableSize.Height));
            total += width;
            height = Math.Max(height, Children[index].DesiredSize.Height);
        }
        return new Size(total, height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        ColumnLayout? layout = GetLayout(this);
        double x = 0;
        for (int index = 0; index < Children.Count; index++)
        {
            double width = layout?.Width(index) ?? 100;
            if (index == Children.Count - 1) width = Math.Max(width, finalSize.Width - x);
            Children[index].Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }

    private void Hook(ColumnLayout? layout)
    {
        if (ReferenceEquals(layout, _hooked)) return;
        if (_hooked is not null) _hooked.Changed -= OnLayoutChanged;
        _hooked = layout;
        if (_hooked is not null) _hooked.Changed += OnLayoutChanged;
        InvalidateMeasure();
    }

    private void OnLayoutChanged(object? sender, EventArgs e) => InvalidateMeasure();
}

/// <summary>The header of a list with columns: a button per column (click sorts) and a grip at its right edge (drag resizes).</summary>
internal sealed class ColumnHeaderBar : UserControl
{
    private readonly ColumnLayout _layout;
    private readonly List<Button> _buttons = new();

    /// <summary>Creates the header.</summary>
    /// <param name="layout">The columns.</param>
    /// <param name="namePrefix">The prefix of the buttons' names (<c>prefix</c> + column index), for tests.</param>
    public ColumnHeaderBar(ColumnLayout layout, string namePrefix)
    {
        _layout = layout;
        var panel = new ColumnsPanel();
        ColumnsPanel.SetLayout(panel, layout);
        for (int index = 0; index < layout.Count; index++)
        {
            int column = index;
            var button = new Button
            {
                Name = namePrefix + index,
                Content = new TextBlock { Text = layout.Titles[index], TextTrimming = TextTrimming.CharacterEllipsis },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = layout.RightAligned[index] ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Padding = new Thickness(4, 2),
                MinHeight = 0,
                CornerRadius = new CornerRadius(0),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Background = Brushes.Transparent,
                FontWeight = FontWeight.SemiBold,
            };
            button[!BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(ThemeKeys.BorderBrush);
            button.Click += On.Click(() => SortRequested?.Invoke(this, column), "ColumnHeaderBar.Sort");
            _buttons.Add(button);
            var thumb = new Thumb
            {
                Width = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
            };
            thumb.DragDelta += UiSafe.Handler<VectorEventArgs>((_, e) => _layout.SetWidth(column, _layout.Width(column) + e.Vector.X), "ColumnHeaderBar.Resize");
            var cell = new Panel();
            cell.Children.Add(button);
            cell.Children.Add(thumb);
            panel.Children.Add(cell);
        }
        Content = panel;
    }

    /// <summary>Raised with the column whose header was clicked.</summary>
    public event EventHandler<int>? SortRequested;

    /// <summary>The header buttons.</summary>
    public IReadOnlyList<Button> Buttons => _buttons;

    /// <summary>Shows the sort direction on a column's header (an arrow after its text).</summary>
    /// <param name="column">The sorted column, or -1.</param>
    /// <param name="descending">Whether it is sorted descending.</param>
    public void ShowSort(int column, bool descending)
    {
        for (int index = 0; index < _buttons.Count; index++)
        {
            string text = _layout.Titles[index];
            if (index == column && text.Length > 0) text += descending ? " ▾" : " ▴";
            ((TextBlock)_buttons[index].Content!).Text = text;
        }
    }
}
