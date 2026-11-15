namespace RemoteControl.Extensions;

/// <summary>
/// An extension's log: the newest lines up to a number of characters, with the same result as <see cref="ExtensionProtocol.AppendLog"/>
/// but without copying the whole text for every line (an extension that writes quickly would otherwise cost a copy of 64 KiB per line).
/// </summary>
internal sealed class BoundedLog
{
    private readonly Queue<string> _lines = new();
    private readonly int _limit;
    private long _length;

    /// <summary>Creates an empty log.</summary>
    /// <param name="limit">The most characters kept.</param>
    public BoundedLog(int limit) => _limit = Math.Max(0, limit);

    /// <summary>Whether the log has no text.</summary>
    public bool IsEmpty => _length == 0;

    /// <summary>Adds a line; an empty line adds nothing.</summary>
    /// <param name="line">The line.</param>
    public void Add(string line)
    {
        if (line.Length == 0) return;
        if (_lines.Count > 0) _length++;
        _lines.Enqueue(line);
        _length += line.Length;
        while (_lines.Count > 1 && _length - (_lines.Peek().Length + 1) >= _limit) _length -= _lines.Dequeue().Length + 1;
    }

    /// <summary>The log text, lines separated by <c>\n</c>, at most the limit (its oldest part cut off).</summary>
    public string Text
    {
        get
        {
            string text = string.Join('\n', _lines);
            if (text.Length <= _limit) return text;
            int start = text.Length - _limit;
            if (char.IsLowSurrogate(text[start])) start++;
            return text[start..];
        }
    }

    /// <summary>The lines of <see cref="Text"/>.</summary>
    public IReadOnlyList<string> Lines => IsEmpty ? Array.Empty<string>() : Text.Split('\n');
}
