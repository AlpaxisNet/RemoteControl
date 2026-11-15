using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace RemoteControl.Ui.Scripts;

/// <summary>
/// The extra carets and selections of multi-caret editing (C++ <c>EditorMultiSelectionState</c>): Ctrl+D adds the next occurrence of
/// the selected word, Ctrl+Alt+Up/Down a caret on the line above or below, Alt+click a caret at the pointer. Typing, Backspace, Delete,
/// Enter and Tab are applied at every caret as one undo step, from the last offset to the first. AvaloniaEdit has one caret only,
/// so the extra ones are anchors in the document drawn by <see cref="MultiCaretRenderer"/>.
/// </summary>
internal sealed class MultiCaret
{
    private readonly TextArea _area;
    private readonly List<(TextAnchor Anchor, TextAnchor Caret)> _extra = [];

    public MultiCaret(TextArea area) => _area = area;

    /// <summary>Raised when the carets changed (redraw).</summary>
    public event EventHandler? Changed;

    /// <summary>How many extra carets there are.</summary>
    public int Count => _extra.Count;

    /// <summary>The extra selections as (anchor, caret) offsets.</summary>
    public IEnumerable<(int Anchor, int Caret)> Ranges => _extra.Where(item => !item.Anchor.IsDeleted && !item.Caret.IsDeleted).Select(item => (item.Anchor.Offset, item.Caret.Offset));

    private TextDocument Document => _area.Document;

    /// <summary>Removes every extra caret.</summary>
    public void Clear()
    {
        if (_extra.Count == 0) return;
        _extra.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a selection (or a caret when both are equal), unless it is the main one or already there.</summary>
    /// <param name="anchor">Where it starts.</param>
    /// <param name="caret">Where its caret is.</param>
    public void Add(int anchor, int caret)
    {
        int length = Document.TextLength;
        anchor = Math.Clamp(anchor, 0, length);
        caret = Math.Clamp(caret, 0, length);
        (int mainAnchor, int mainCaret) = MainRange();
        if (mainAnchor == anchor && mainCaret == caret) return;
        if (Ranges.Any(range => range.Anchor == anchor && range.Caret == caret)) return;
        TextAnchor a = Document.CreateAnchor(anchor);
        a.MovementType = AnchorMovementType.BeforeInsertion;
        TextAnchor c = Document.CreateAnchor(caret);
        c.MovementType = AnchorMovementType.AfterInsertion;
        _extra.Add((a, c));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the caret of the last extra selection (Alt+Shift+click).</summary>
    /// <param name="caret">The new caret offset.</param>
    public void MoveLast(int caret)
    {
        if (_extra.Count == 0) return;
        (TextAnchor anchor, _) = _extra[^1];
        TextAnchor c = Document.CreateAnchor(Math.Clamp(caret, 0, Document.TextLength));
        c.MovementType = AnchorMovementType.AfterInsertion;
        _extra[^1] = (anchor, c);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a caret on the line above or below the outermost caret, at the same column (clamped).</summary>
    /// <param name="direction">1 for down, -1 for up.</param>
    /// <param name="extend">With Shift: the new selection starts at the main (or last) anchor.</param>
    public void AddAdjacent(int direction, bool extend)
    {
        int source = _area.Caret.Offset;
        int sourceLine = Document.GetLineByOffset(source).LineNumber;
        foreach ((_, int caret) in Ranges)
        {
            int line = Document.GetLineByOffset(caret).LineNumber;
            if ((direction > 0 && line > sourceLine) || (direction < 0 && line < sourceLine))
            {
                source = caret;
                sourceLine = line;
            }
        }
        DocumentLine current = Document.GetLineByNumber(sourceLine);
        int column = source - current.Offset;
        int targetNumber = sourceLine + direction;
        if (targetNumber < 1 || targetNumber > Document.LineCount) return;
        DocumentLine target = Document.GetLineByNumber(targetNumber);
        int offset = target.Offset + Math.Min(column, target.Length);
        int anchor = offset;
        if (extend) anchor = _extra.Count > 0 ? _extra[^1].Anchor.Offset : MainRange().Anchor;
        Add(anchor, offset);
    }

    /// <summary>Ctrl+D: selects the word at the caret when nothing is selected, otherwise adds the next occurrence (wrapping).</summary>
    public void SelectNextOccurrence()
    {
        string text = Document.Text;
        int selectionStart = _area.Selection.IsEmpty ? _area.Caret.Offset : _area.Selection.SurroundingSegment.Offset;
        int selectionEnd = _area.Selection.IsEmpty ? _area.Caret.Offset : _area.Selection.SurroundingSegment.EndOffset;
        if (_area.Selection.IsEmpty)
        {
            int start = selectionStart;
            int end = selectionEnd;
            while (start > 0 && IsWord(text[start - 1])) start--;
            while (end < text.Length && IsWord(text[end])) end++;
            if (start == end) return;
            _area.Selection = Selection.Create(_area, start, end);
            _area.Caret.Offset = end;
            return;
        }
        string selected = text.Substring(selectionStart, selectionEnd - selectionStart);
        if (selected.Length == 0) return;
        int search = selectionEnd;
        foreach ((_, int caret) in Ranges) search = Math.Max(search, caret);
        int found = search <= text.Length ? text.IndexOf(selected, search, StringComparison.Ordinal) : -1;
        if (found < 0) found = text.IndexOf(selected, 0, StringComparison.Ordinal);
        if (found < 0) return;
        Add(found, found + selected.Length);
    }

    /// <summary>Replaces every selection (the main one too) with a text, or erases one character at each caret.</summary>
    /// <param name="insert">The text to insert.</param>
    /// <param name="erase">-1 for Backspace, 1 for Delete, 0 for none.</param>
    /// <returns>Whether there were extra carets to edit.</returns>
    public bool Replace(string insert, int erase)
    {
        if (_extra.Count == 0) return false;
        (int mainAnchor, int mainCaret) = MainRange();
        TextAnchor mainAnchorMark = Document.CreateAnchor(mainAnchor);
        TextAnchor mainCaretMark = Document.CreateAnchor(mainCaret);
        mainCaretMark.MovementType = AnchorMovementType.AfterInsertion;
        var operations = new List<(TextAnchor Anchor, TextAnchor Caret)> { (mainAnchorMark, mainCaretMark) };
        operations.AddRange(_extra);
        operations.Sort((left, right) => Math.Max(right.Anchor.Offset, right.Caret.Offset).CompareTo(Math.Max(left.Anchor.Offset, left.Caret.Offset)));
        var results = new List<(TextAnchor, TextAnchor)>();
        using (Document.RunUpdate())
        {
            foreach ((TextAnchor anchor, TextAnchor caret) in operations)
            {
                int start = Math.Min(anchor.Offset, caret.Offset);
                int end = Math.Max(anchor.Offset, caret.Offset);
                if (start == end)
                {
                    if (erase < 0 && start > 0) start--;
                    else if (erase > 0 && end < Document.TextLength) end++;
                }
                Document.Replace(start, end - start, insert);
                int position = start + insert.Length;
                TextAnchor a = Document.CreateAnchor(position);
                TextAnchor c = Document.CreateAnchor(position);
                c.MovementType = AnchorMovementType.AfterInsertion;
                if (!ReferenceEquals(anchor, mainAnchorMark)) results.Add((a, c));
                else
                {
                    mainAnchorMark = a;
                    mainCaretMark = c;
                }
            }
        }
        _extra.Clear();
        _extra.AddRange(results);
        _area.ClearSelection();
        _area.Caret.Offset = mainCaretMark.Offset;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private (int Anchor, int Caret) MainRange()
    {
        if (_area.Selection.IsEmpty) return (_area.Caret.Offset, _area.Caret.Offset);
        int start = _area.Selection.SurroundingSegment.Offset;
        int end = _area.Selection.SurroundingSegment.EndOffset;
        return _area.Caret.Offset == start ? (end, start) : (start, end);
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
