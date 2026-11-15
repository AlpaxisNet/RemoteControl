using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using RemoteControl.Scripting;

namespace RemoteControl.Ui.Scripts;

/// <summary>The underlines of the script analysis (C++ diagnostics tags): red and amber squiggles for errors and warnings, a blue line for information.</summary>
internal sealed class DiagnosticsRenderer : IBackgroundRenderer
{
    private static readonly IPen ErrorPen = new Pen(new SolidColorBrush(Color.Parse("#f44747")), 1.2);
    private static readonly IPen WarningPen = new Pen(new SolidColorBrush(Color.Parse("#e5a50a")), 1.2);
    private static readonly IPen InfoPen = new Pen(new SolidColorBrush(Color.Parse("#4aa3df")), 1);

    /// <summary>The diagnostics shown, sorted by start.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; set; } = Array.Empty<Diagnostic>();

    /// <inheritdoc />
    public KnownLayer Layer => KnownLayer.Selection;

    /// <inheritdoc />
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Diagnostics.Count == 0 || textView.Document is null || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
        int first = textView.VisualLines[0].FirstDocumentLine.Offset;
        int last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        int length = textView.Document.TextLength;
        foreach (Diagnostic diagnostic in Diagnostics)
        {
            if (diagnostic.End < first || diagnostic.Start > last) continue;
            int start = Math.Clamp(diagnostic.Start, 0, length);
            int end = Math.Clamp(Math.Max(diagnostic.End, diagnostic.Start + 1), 0, length);
            if (end <= start) continue;
            IPen pen = diagnostic.Severity switch { DiagnosticSeverity.Error => ErrorPen, DiagnosticSeverity.Warning => WarningPen, _ => InfoPen };
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = start, EndOffset = end }, false))
            {
                double y = rect.Bottom - 1;
                if (diagnostic.Severity == DiagnosticSeverity.Info)
                {
                    drawingContext.DrawLine(pen, new Point(rect.Left, y), new Point(rect.Right, y));
                    continue;
                }
                var geometry = new StreamGeometry();
                using (StreamGeometryContext context = geometry.Open())
                {
                    context.BeginFigure(new Point(rect.Left, y), false);
                    bool up = true;
                    for (double x = rect.Left + 2; x <= rect.Right + 0.01; x += 2)
                    {
                        context.LineTo(new Point(x, up ? y - 2 : y));
                        up = !up;
                    }
                    context.EndFigure(false);
                }
                drawingContext.DrawGeometry(null, pen, geometry);
            }
        }
    }
}

/// <summary>The tint of the lines that differ from the text the server confirmed (C++ <c>updateChangedLines</c>, rgba 1.0 0.78 0.18 0.14).</summary>
internal sealed class ChangedLinesRenderer : IBackgroundRenderer
{
    private static readonly IBrush Tint = new SolidColorBrush(Color.FromArgb(36, 255, 199, 46));

    /// <summary>The changed lines, or null.</summary>
    public ChangedLines? Lines { get; set; }

    /// <inheritdoc />
    public KnownLayer Layer => KnownLayer.Background;

    /// <inheritdoc />
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Lines is not { } lines || !textView.VisualLinesValid) return;
        foreach (VisualLine line in textView.VisualLines)
        {
            int number = line.FirstDocumentLine.LineNumber - 1;
            if (number < lines.FirstLine || number >= lines.EndLine) continue;
            double top = line.VisualTop - textView.VerticalOffset;
            drawingContext.FillRectangle(Tint, new Rect(0, top, textView.Bounds.Width, line.Height));
        }
    }
}

/// <summary>Highlights the bracket at the caret and its partner (C++ <c>showbrackets</c>), skipping strings and comments on the way.</summary>
internal sealed class BracketRenderer : IBackgroundRenderer
{
    private const int ScanLimit = 200_000;

    /// <summary>The two offsets to highlight, or null.</summary>
    public (int Open, int Close)? Pair { get; private set; }

    /// <summary>The brush.</summary>
    public IBrush Brush { get; set; } = new SolidColorBrush(Color.FromArgb(70, 128, 160, 255));

    /// <inheritdoc />
    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>Finds the pair at a caret offset.</summary>
    /// <param name="document">The text.</param>
    /// <param name="caret">The caret.</param>
    /// <returns>Whether the pair changed.</returns>
    public bool Update(TextDocument document, int caret)
    {
        (int, int)? next = Find(document, caret);
        if (next == Pair) return false;
        Pair = next;
        return true;
    }

    /// <summary>Clears the pair.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Clear()
    {
        if (Pair is null) return false;
        Pair = null;
        return true;
    }

    /// <inheritdoc />
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Pair is not { } pair || textView.Document is null) return;
        foreach (int offset in new[] { pair.Open, pair.Close })
        {
            if (offset < 0 || offset >= textView.Document.TextLength) continue;
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = offset, EndOffset = offset + 1 }, false))
                drawingContext.FillRectangle(Brush, rect);
        }
    }

    internal static (int, int)? Find(TextDocument document, int caret)
    {
        int length = document.TextLength;
        foreach (int at in new[] { caret - 1, caret })
        {
            if (at < 0 || at >= length) continue;
            char c = document.GetCharAt(at);
            int match = c switch
            {
                '(' or '[' or '{' => Scan(document, at, Closing(c), c, 1),
                ')' or ']' or '}' => Scan(document, at, Opening(c), c, -1),
                _ => -2,
            };
            if (match == -2) continue;
            if (match < 0) return null;
            return at < match ? (at, match) : (match, at);
        }
        return null;
    }

    private static char Closing(char c) => c == '(' ? ')' : c == '[' ? ']' : '}';

    private static char Opening(char c) => c == ')' ? '(' : c == ']' ? '[' : '{';

    private static int Scan(TextDocument document, int from, char target, char self, int direction)
    {
        int depth = 0;
        int length = document.TextLength;
        char quote = '\0';
        for (int index = from + direction, steps = 0; index >= 0 && index < length && steps < ScanLimit; index += direction, steps++)
        {
            char c = document.GetCharAt(index);
            if (quote != '\0')
            {
                if (c == quote && (index == 0 || document.GetCharAt(index - 1) != '\\')) quote = '\0';
                else if (c == '\n') quote = '\0';
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }
            if (c == self) depth++;
            else if (c == target)
            {
                if (depth == 0) return index;
                depth--;
            }
        }
        return -1;
    }
}

/// <summary>Draws the extra carets and selections of multi-caret editing.</summary>
internal sealed class MultiCaretRenderer : IBackgroundRenderer
{
    private readonly MultiCaret _carets;

    public MultiCaretRenderer(MultiCaret carets) => _carets = carets;

    /// <summary>The selection brush.</summary>
    public IBrush SelectionBrush { get; set; } = new SolidColorBrush(Color.FromArgb(110, 80, 130, 220));

    /// <summary>The caret pen.</summary>
    public IPen CaretPen { get; set; } = new Pen(Brushes.White, 1.5);

    /// <inheritdoc />
    public KnownLayer Layer => KnownLayer.Caret;

    /// <inheritdoc />
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_carets.Count == 0 || textView.Document is null) return;
        foreach ((int anchor, int caret) in _carets.Ranges)
        {
            int start = Math.Min(anchor, caret);
            int end = Math.Max(anchor, caret);
            if (end > start)
            {
                foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = start, EndOffset = end }, false))
                    drawingContext.FillRectangle(SelectionBrush, rect);
            }
            var location = textView.Document.GetLocation(Math.Clamp(caret, 0, textView.Document.TextLength));
            var position = new AvaloniaEdit.TextViewPosition(location);
            Point top = textView.GetVisualPosition(position, VisualYPosition.LineTop) - textView.ScrollOffset;
            Point bottom = textView.GetVisualPosition(position, VisualYPosition.LineBottom) - textView.ScrollOffset;
            drawingContext.DrawLine(CaretPen, new Point(Math.Round(top.X) + 0.5, top.Y), new Point(Math.Round(bottom.X) + 0.5, bottom.Y));
        }
    }
}
