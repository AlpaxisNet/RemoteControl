using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace RemoteControl.Ui.Files;

/// <summary>
/// A read-only, word-wrapped log that keeps at most <see cref="MaximumLines"/> lines and <see cref="MaximumCharacters"/> characters,
/// dropping the oldest, and follows the newest line (<c>appendLog</c>, <c>TFileBrowserTree.cpp:2400-2416</c>). Built on AvaloniaEdit,
/// which lays out only the visible lines. An optional colouring picks a brush per line. Used on the UI thread.
/// </summary>
internal sealed class LogView : UserControl
{
    private readonly TextEditor _editor;
    private readonly List<string> _lines = new();
    private int _characters;

    /// <summary>Creates an empty log.</summary>
    public LogView()
    {
        _editor = new TextEditor
        {
            Name = "LogEditor",
            IsReadOnly = true,
            WordWrap = true,
            ShowLineNumbers = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Avalonia.Thickness(4, 2, 10, 2),
            FontFamily = FontFamily.Default,
        };
        _editor.Bind(TextEditor.BackgroundProperty, _editor.GetResourceObservable("RcEditorBrush"));
        _editor.Bind(TextEditor.ForegroundProperty, _editor.GetResourceObservable("RcTextBrush"));
        _editor.TextArea.Caret.CaretBrush = Brushes.Transparent;
        Content = _editor;
    }

    /// <summary>The most lines kept (5000, the C++ limit).</summary>
    public int MaximumLines { get; set; } = 5000;

    /// <summary>The most characters kept (2 MiB).</summary>
    public int MaximumCharacters { get; set; } = 2 * 1024 * 1024;

    /// <summary>The lines shown, oldest first.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>All text.</summary>
    public string Text => _editor.Document.Text;

    /// <summary>The editor (for tests and the colouring).</summary>
    public TextEditor Editor => _editor;

    /// <summary>Picks the brush of a line, or null for the normal colour.</summary>
    public Func<string, IBrush?>? LineBrush
    {
        get => _colorizer?.Pick;
        set
        {
            if (_colorizer is not null) _editor.TextArea.TextView.LineTransformers.Remove(_colorizer);
            _colorizer = value is null ? null : new LineColorizer(value);
            if (_colorizer is not null) _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        }
    }

    private LineColorizer? _colorizer;

    /// <summary>Adds a line (a text with line breaks adds several) and scrolls to it.</summary>
    /// <param name="text">The text.</param>
    public void Append(string? text)
    {
        foreach (string line in (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            TextDocument document = _editor.Document;
            if (_lines.Count > 0) document.Insert(document.TextLength, "\n");
            document.Insert(document.TextLength, line);
            _lines.Add(line);
            _characters += line.Length + 1;
        }

        Trim();
        _editor.ScrollToEnd();
    }

    /// <summary>Removes every line.</summary>
    public void Clear()
    {
        _lines.Clear();
        _characters = 0;
        _editor.Document.Text = string.Empty;
    }

    private void Trim()
    {
        int drop = 0;
        int characters = _characters;
        while (_lines.Count - drop > 1 && (_lines.Count - drop > MaximumLines || characters > MaximumCharacters))
        {
            characters -= _lines[drop].Length + 1;
            drop++;
        }

        if (drop == 0) return;
        TextDocument document = _editor.Document;
        DocumentLine last = document.GetLineByNumber(drop);
        document.Remove(0, last.EndOffset + last.DelimiterLength);
        _lines.RemoveRange(0, drop);
        _characters = characters;
    }

    private sealed class LineColorizer(Func<string, IBrush?> pick) : DocumentColorizingTransformer
    {
        public Func<string, IBrush?> Pick { get; } = pick;

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0) return;
            string text = CurrentContext.Document.GetText(line);
            IBrush? brush = Pick(text);
            if (brush is null) return;
            ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}
