using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;

namespace RemoteControl.Ui.Scripts;

/// <summary>
/// The minimap beside the editor (C++ <c>GtkSourceMap</c>, 120 pixels wide): each line is a thin bar as long as its text, the part of
/// the script in view is marked by a box of clamp(round(mapHeight * page / extent), 12, 48) pixels, and clicking or dragging scrolls
/// there. Only the lines that fit are drawn, so a very long script costs no more than a short one.
/// </summary>
internal sealed class MinimapView : Control
{
    private const double LineHeight = 2;
    private const double CharacterWidth = 1;
    private TextEditor? _editor;
    private bool _dragging;

    public MinimapView()
    {
        Width = 120;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>The colour of the bars.</summary>
    public IBrush LineBrush { get; set; } = new SolidColorBrush(Color.FromArgb(110, 200, 200, 200));

    /// <summary>The colour of the view box.</summary>
    public IBrush ViewportBrush { get; set; } = new SolidColorBrush(Color.FromArgb(50, 128, 160, 255));

    /// <summary>The background.</summary>
    public IBrush? Background { get; set; }

    /// <summary>Whether the view box is shown (the script does not fit the editor).</summary>
    public bool MarkerVisible { get; private set; }

    /// <summary>The view box's height in the last drawing.</summary>
    public double MarkerHeight { get; private set; }

    /// <summary>The editor shown.</summary>
    public TextEditor? Editor
    {
        get => _editor;
        set
        {
            if (_editor is not null) _editor.TextArea.TextView.ScrollOffsetChanged -= OnChanged;
            _editor = value;
            if (_editor is not null) _editor.TextArea.TextView.ScrollOffsetChanged += OnChanged;
            InvalidateVisual();
        }
    }

    /// <summary>Redraws after a text change.</summary>
    public void Refresh() => InvalidateVisual();

    /// <summary>The view box's height for a map, page and extent (C++ formula).</summary>
    internal static double MarkerHeightFor(double mapHeight, double page, double extent) =>
        extent <= 0 ? 0 : Math.Clamp(Math.Round(mapHeight * page / extent), 12, 48);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        if (Background is not null) context.FillRectangle(Background, bounds);
        TextEditor? editor = _editor;
        TextDocument? document = editor?.Document;
        if (editor is null || document is null || bounds.Height <= 0) return;
        double extent = editor.ExtentHeight;
        double page = editor.ViewportHeight;
        double progress = extent > page ? Math.Clamp(editor.VerticalOffset / (extent - page), 0, 1) : 0;
        int lineCount = document.LineCount;
        double total = lineCount * LineHeight;
        double mapTop = total > bounds.Height ? progress * (total - bounds.Height) : 0;
        int firstLine = Math.Max(1, (int)(mapTop / LineHeight) + 1);
        int visible = (int)(bounds.Height / LineHeight) + 2;
        for (int number = firstLine; number <= lineCount && number < firstLine + visible; number++)
        {
            DocumentLine line = document.GetLineByNumber(number);
            int indent = 0;
            int length = Math.Min(line.Length, 400);
            while (indent < length && char.IsWhiteSpace(document.GetCharAt(line.Offset + indent))) indent++;
            double width = Math.Min(bounds.Width - 4, (length - indent) * CharacterWidth);
            if (width <= 0) continue;
            double y = (number - 1) * LineHeight - mapTop;
            context.FillRectangle(LineBrush, new Rect(2 + Math.Min(indent * CharacterWidth, 60), y, width, LineHeight - 0.5));
        }
        MarkerVisible = extent > page + 0.5;
        if (!MarkerVisible) return;
        MarkerHeight = MarkerHeightFor(bounds.Height, page, extent);
        double top = progress * (bounds.Height - MarkerHeight);
        context.FillRectangle(ViewportBrush, new Rect(0, top, bounds.Width, MarkerHeight));
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        ScrollTo(e.GetPosition(this).Y);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) ScrollTo(e.GetPosition(this).Y);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    /// <summary>Scrolls the editor so that a map position is in view.</summary>
    /// <param name="y">The position on the map.</param>
    internal void ScrollTo(double y)
    {
        TextEditor? editor = _editor;
        if (editor is null || Bounds.Height <= 0) return;
        double extent = editor.ExtentHeight;
        double page = editor.ViewportHeight;
        if (extent <= page) return;
        double fraction = Math.Clamp(y / Bounds.Height, 0, 1);
        editor.ScrollToVerticalOffset(fraction * (extent - page));
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();
}
