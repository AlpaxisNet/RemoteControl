using System.Text.RegularExpressions;

namespace RemoteControl.Scripting;

/// <summary>A range of a text in UTF-16 code units.</summary>
/// <param name="Start">First character.</param>
/// <param name="Length">Number of characters.</param>
public readonly record struct TextRange(int Start, int Length)
{
    /// <summary>Index just after the range.</summary>
    public int End => Start + Length;
}

/// <summary>How the Find and Replace dialog searches.</summary>
public sealed record FindOptions
{
    /// <summary>Plain text, case-insensitive, wrapping: the dialog's defaults.</summary>
    public static FindOptions Default { get; } = new();

    /// <summary>"Case sensitive" check box; off compares with simple Unicode case folding.</summary>
    public bool CaseSensitive { get; init; }

    /// <summary>
    /// "Full words only" check box: a match is rejected when the text next to it is a letter, digit or underscore on a side where
    /// the query itself starts or ends with such a character (so "x(" still matches inside "max(").
    /// </summary>
    public bool WholeWord { get; init; }

    /// <summary>Treat the query as a .NET regular expression. The C++ had no regex search; this is an addition, off by default.</summary>
    public bool UseRegex { get; init; }

    /// <summary>When nothing is found before the end (or start), search once more from the other end, as the C++ always did.</summary>
    public bool Wrap { get; init; } = true;

    /// <summary>Longest time one regex match attempt may take before the search gives up.</summary>
    public TimeSpan RegexTimeout { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>The result of a replace step.</summary>
/// <param name="Text">The text after the step (the same instance when nothing was replaced).</param>
/// <param name="Replaced">The inserted replacement, or null when nothing was replaced.</param>
/// <param name="NextMatch">The match to select afterwards, or null.</param>
/// <param name="Caret">Where the caret goes when <paramref name="NextMatch"/> is null.</param>
public sealed record ReplaceResult(string Text, TextRange? Replaced, TextRange? NextMatch, int Caret);

/// <summary>The result of Replace All.</summary>
/// <param name="Text">The text after replacing.</param>
/// <param name="Count">How many matches were replaced.</param>
/// <param name="Caret">Where the caret goes: after the last replacement (or unchanged when nothing was replaced).</param>
public sealed record ReplaceAllResult(string Text, int Count, int Caret);

/// <summary>
/// Find and replace over a <see cref="string"/>, independent of any control (port of <c>TEditorFind.h</c>). Searching
/// forwards starts at the selection end (or caret), backwards at the selection start; both wrap once. A query that is empty or
/// a regular expression that does not compile or times out finds nothing.
/// </summary>
public static class TextSearch
{
    /// <summary>Finds the next match at or after <paramref name="from"/>, wrapping to the start if allowed.</summary>
    /// <param name="text">The text.</param>
    /// <param name="query">The query.</param>
    /// <param name="from">The selection end, or the caret when nothing is selected.</param>
    /// <param name="options">The search options.</param>
    /// <returns>The match or null.</returns>
    public static TextRange? FindNext(string text, string query, int from, FindOptions? options = null)
    {
        options ??= FindOptions.Default;
        if (string.IsNullOrEmpty(query) || text == null) return null;
        Matcher? matcher = Matcher.Create(query, options);
        if (matcher == null) return null;
        from = Math.Clamp(from, 0, text.Length);
        TextRange? found = matcher.Forward(text, from, text.Length);
        if (found == null && options.Wrap) found = matcher.Forward(text, 0, text.Length);
        return found;
    }

    /// <summary>Finds the previous match that ends at or before <paramref name="from"/>, wrapping to the end if allowed.</summary>
    /// <param name="text">The text.</param>
    /// <param name="query">The query.</param>
    /// <param name="from">The selection start, or the caret when nothing is selected.</param>
    /// <param name="options">The search options.</param>
    /// <returns>The match or null.</returns>
    public static TextRange? FindPrevious(string text, string query, int from, FindOptions? options = null)
    {
        options ??= FindOptions.Default;
        if (string.IsNullOrEmpty(query) || text == null) return null;
        Matcher? matcher = Matcher.Create(query, options);
        if (matcher == null) return null;
        from = Math.Clamp(from, 0, text.Length);
        TextRange? found = matcher.Backward(text, from);
        if (found == null && options.Wrap) found = matcher.Backward(text, text.Length);
        return found;
    }

    /// <summary>Every match from the start, non-overlapping, in order.</summary>
    /// <param name="text">The text.</param>
    /// <param name="query">The query.</param>
    /// <param name="options">The search options (wrapping does not apply).</param>
    /// <returns>The matches.</returns>
    public static IReadOnlyList<TextRange> FindAll(string text, string query, FindOptions? options = null)
    {
        options ??= FindOptions.Default;
        var result = new List<TextRange>();
        if (string.IsNullOrEmpty(query) || text == null) return result;
        Matcher? matcher = Matcher.Create(query, options);
        if (matcher == null) return result;
        int position = 0;
        while (position <= text.Length)
        {
            TextRange? found = matcher.Forward(text, position, text.Length);
            if (found == null) break;
            result.Add(found.Value);
            position = found.Value.Length == 0 ? found.Value.End + 1 : found.Value.End;
        }
        return result;
    }

    /// <summary>
    /// The Replace button: when the selection is not a match, the next match is selected first; a selected match is replaced and
    /// the match after it is found.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="query">The query.</param>
    /// <param name="replacement">The replacement (in regex mode <c>$1</c> and the like are expanded).</param>
    /// <param name="selection">The current selection; a zero-length range is the caret.</param>
    /// <param name="options">The search options.</param>
    /// <returns>The new text and what to select.</returns>
    public static ReplaceResult Replace(string text, string query, string? replacement, TextRange selection, FindOptions? options = null)
    {
        options ??= FindOptions.Default;
        replacement ??= string.Empty;
        int caret = Math.Clamp(selection.End, 0, text?.Length ?? 0);
        if (text == null || string.IsNullOrEmpty(query)) return new ReplaceResult(text ?? string.Empty, null, null, caret);
        Matcher? matcher = Matcher.Create(query, options);
        if (matcher == null) return new ReplaceResult(text, null, null, caret);
        TextRange current = Clamp(selection, text.Length);
        if (!matcher.SelectionMatches(text, current))
        {
            TextRange? found = FindNext(text, query, current.End, options);
            if (found == null) return new ReplaceResult(text, null, null, caret);
            current = found.Value;
            if (!matcher.SelectionMatches(text, current)) return new ReplaceResult(text, null, current, current.End);
        }
        string inserted = matcher.Expand(text, current, replacement);
        string updated = string.Concat(text.AsSpan(0, current.Start), inserted, text.AsSpan(current.End));
        var replaced = new TextRange(current.Start, inserted.Length);
        TextRange? next = FindNext(updated, query, replaced.End, options);
        return new ReplaceResult(updated, replaced, next, replaced.End);
    }

    /// <summary>Replace All: every match from the start; searching continues after each inserted replacement.</summary>
    /// <param name="text">The text.</param>
    /// <param name="query">The query.</param>
    /// <param name="replacement">The replacement.</param>
    /// <param name="caret">The caret, returned unchanged when nothing matches.</param>
    /// <param name="options">The search options.</param>
    /// <returns>The new text, the count and the caret position after the last replacement.</returns>
    public static ReplaceAllResult ReplaceAll(string text, string query, string? replacement, int caret = 0, FindOptions? options = null)
    {
        options ??= FindOptions.Default;
        replacement ??= string.Empty;
        if (text == null || string.IsNullOrEmpty(query)) return new ReplaceAllResult(text ?? string.Empty, 0, caret);
        Matcher? matcher = Matcher.Create(query, options);
        if (matcher == null) return new ReplaceAllResult(text, 0, caret);
        var builder = new System.Text.StringBuilder(text.Length);
        int copied = 0;
        int count = 0;
        int position = 0;
        int newCaret = caret;
        while (position <= text.Length)
        {
            TextRange? found = matcher.Forward(text, position, text.Length);
            if (found == null) break;
            builder.Append(text, copied, found.Value.Start - copied);
            string inserted = matcher.Expand(text, found.Value, replacement);
            builder.Append(inserted);
            copied = found.Value.End;
            newCaret = builder.Length;
            ++count;
            position = found.Value.Length == 0 ? found.Value.End + 1 : found.Value.End;
            if (found.Value.Length == 0 && found.Value.End < text.Length)
            {
                builder.Append(text[found.Value.End]);
                copied = found.Value.End + 1;
            }
        }
        if (count == 0) return new ReplaceAllResult(text, 0, caret);
        builder.Append(text, copied, text.Length - copied);
        return new ReplaceAllResult(builder.ToString(), count, newCaret);
    }

    private static TextRange Clamp(TextRange range, int length)
    {
        int start = Math.Clamp(range.Start, 0, length);
        int end = Math.Clamp(range.End, start, length);
        return new TextRange(start, end - start);
    }

    /// <summary>The query compiled for one search.</summary>
    private sealed class Matcher
    {
        private readonly string query;
        private readonly FindOptions options;
        private readonly Regex? regex;
        private readonly Regex? reverseRegex;

        private Matcher(string query, FindOptions options, Regex? regex, Regex? reverseRegex)
        {
            this.query = query;
            this.options = options;
            this.regex = regex;
            this.reverseRegex = reverseRegex;
        }

        public static Matcher? Create(string query, FindOptions options)
        {
            if (!options.UseRegex) return new Matcher(query, options, null, null);
            RegexOptions flags = RegexOptions.CultureInvariant | RegexOptions.Multiline | (options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            TimeSpan timeout = options.RegexTimeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(250) : options.RegexTimeout;
            try
            {
                return new Matcher(query, options, new Regex(query, flags, timeout), new Regex(query, flags | RegexOptions.RightToLeft, timeout));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private StringComparison Comparison => options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        /// <summary>First acceptable match starting in [from, limit].</summary>
        public TextRange? Forward(string text, int from, int limit)
        {
            int start = from;
            while (start <= limit)
            {
                TextRange? candidate = RawForward(text, start);
                if (candidate == null) return null;
                TextRange match = candidate.Value;
                if (!options.WholeWord || WordBoundary(text, match)) return match;
                start = match.Length == 0 ? match.End + 1 : match.End;
            }
            return null;
        }

        /// <summary>Last acceptable match ending at or before <paramref name="from"/>.</summary>
        public TextRange? Backward(string text, int from)
        {
            int end = from;
            while (end >= 0)
            {
                TextRange? candidate = RawBackward(text, end);
                if (candidate == null) return null;
                TextRange match = candidate.Value;
                if (!options.WholeWord || WordBoundary(text, match)) return match;
                end = match.Length == 0 ? match.Start - 1 : match.Start;
            }
            return null;
        }

        private TextRange? RawForward(string text, int start)
        {
            if (start > text.Length) return null;
            if (regex == null)
            {
                int found = text.IndexOf(query, start, Comparison);
                return found < 0 ? null : new TextRange(found, query.Length);
            }
            try
            {
                Match match = regex.Match(text, start);
                return match.Success ? new TextRange(match.Index, match.Length) : null;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        private TextRange? RawBackward(string text, int end)
        {
            if (end < 0) return null;
            if (regex == null)
            {
                if (end < query.Length) return null;
                int found = text.LastIndexOf(query, end - 1, end, Comparison);
                return found < 0 ? null : new TextRange(found, query.Length);
            }
            try
            {
                Match match = reverseRegex!.Match(text, 0, end);
                return match.Success ? new TextRange(match.Index, match.Length) : null;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        /// <summary>Whether the selected text is exactly a match (the C++ compared case-folded text and ignored "full words").</summary>
        public bool SelectionMatches(string text, TextRange selection)
        {
            if (selection.Length == 0) return false;
            if (regex == null) return selection.Length == query.Length && string.Compare(text, selection.Start, query, 0, query.Length, Comparison) == 0;
            try
            {
                Match match = regex.Match(text, selection.Start, selection.Length);
                return match.Success && match.Index == selection.Start && match.Length == selection.Length;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        public string Expand(string text, TextRange range, string replacement)
        {
            if (regex == null) return replacement;
            try
            {
                Match match = regex.Match(text, range.Start);
                if (match.Success && match.Index == range.Start && match.Length == range.Length) return match.Result(replacement);
            }
            catch (RegexMatchTimeoutException)
            {
            }
            return replacement;
        }

        private bool WordBoundary(string text, TextRange match)
        {
            string matched = regex == null ? query : text.Substring(match.Start, match.Length);
            if (matched.Length == 0) return true;
            bool wordFirst = TextHelpers.IsWordCharacterAt(matched, 0, out _) && matched[0] != '$';
            bool wordLast = TextHelpers.IsWordCharacterBefore(matched, matched.Length, out int lastStart) && matched[lastStart] != '$';
            bool beforeWord = TextHelpers.IsWordCharacterBefore(text, match.Start, out int beforeStart) && text[beforeStart] != '$';
            bool afterWord = TextHelpers.IsWordCharacterAt(text, match.End, out _) && text[match.End] != '$';
            return (!wordFirst || !beforeWord) && (!wordLast || !afterWord);
        }
    }
}

/// <summary>Line arithmetic for the Go to line dialog and the "Line: N" footer.</summary>
public static class GoToLine
{
    /// <summary>Number of lines; '\n', "\r\n" and a lone '\r' each end a line, so an empty text has one line.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The line count, at least 1.</returns>
    public static int LineCount(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        int count = 1;
        for (int index = 0; index < text.Length; ++index)
        {
            if (text[index] == '\n') ++count;
            else if (text[index] == '\r' && (index + 1 >= text.Length || text[index + 1] != '\n')) ++count;
        }
        return count;
    }

    /// <summary>The 1-based line number of <paramref name="offset"/> (what the dialog pre-fills and the footer shows).</summary>
    /// <param name="text">The text.</param>
    /// <param name="offset">A caret offset; clamped to the text.</param>
    /// <returns>The line number.</returns>
    public static int LineNumberAt(string? text, int offset)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        int end = Math.Clamp(offset, 0, text.Length);
        int line = 1;
        for (int index = 0; index < end; ++index)
        {
            if (text[index] == '\n') ++line;
            else if (text[index] == '\r' && (index + 1 >= text.Length || text[index + 1] != '\n')) ++line;
        }
        return line;
    }

    /// <summary>
    /// Reads the dialog entry like the C++ <c>strtol</c>: leading whitespace, an optional sign and digits; anything else counts as 0.
    /// The result is clamped to [1, <paramref name="lineCount"/>].
    /// </summary>
    /// <param name="input">The entry text.</param>
    /// <param name="lineCount">Number of lines of the text.</param>
    /// <returns>The 1-based line to go to.</returns>
    public static int Parse(string? input, int lineCount)
    {
        long requested = 0;
        string value = input ?? string.Empty;
        int index = 0;
        while (index < value.Length && TextHelpers.IsCSpace(value[index])) ++index;
        bool negative = false;
        if (index < value.Length && (value[index] == '+' || value[index] == '-')) { negative = value[index] == '-'; ++index; }
        while (index < value.Length && TextHelpers.IsDigit(value[index]))
        {
            requested = Math.Min(int.MaxValue, requested * 10 + (value[index] - '0'));
            ++index;
        }
        if (negative) requested = -requested;
        return (int)Math.Clamp(requested, 1, Math.Max(1, lineCount));
    }

    /// <summary>Offset of the first character of a 1-based line (clamped), where the dialog puts the caret.</summary>
    /// <param name="text">The text.</param>
    /// <param name="line">The 1-based line.</param>
    /// <returns>The offset.</returns>
    public static int LineStartOffset(string? text, int line)
    {
        if (string.IsNullOrEmpty(text) || line <= 1) return 0;
        int current = 1;
        for (int index = 0; index < text.Length; ++index)
        {
            bool lineBreak = text[index] == '\n' || (text[index] == '\r' && (index + 1 >= text.Length || text[index + 1] != '\n'));
            if (!lineBreak) continue;
            if (++current == line) return index + 1;
        }
        return LineStartOffset(text, current);
    }
}
