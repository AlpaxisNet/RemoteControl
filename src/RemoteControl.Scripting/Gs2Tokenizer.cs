namespace RemoteControl.Scripting;

internal enum Gs2TokenKind { Identifier, Keyword, Number, String, Message, Operator, End }

/// <summary>One token; <see cref="Text"/> is set for identifiers, keywords and operators (empty for literals).</summary>
internal readonly record struct Gs2Token(Gs2TokenKind Kind, int Start, int End, string Text);

/// <summary>Collects diagnostics with ranges that stay inside the text and never split a surrogate pair.</summary>
internal sealed class DiagnosticSink
{
    private readonly string text;

    public DiagnosticSink(string text) { this.text = text; }

    public List<Diagnostic> Items { get; } = new();

    public void Add(DiagnosticSeverity severity, int start, int end, string message)
    {
        int length = text.Length;
        if (start > length) start = length;
        if (start < 0) start = 0;
        if (start == length && length > 0) start = TextHelpers.PreviousCodePointStart(text, length);
        if (start > 0 && start < length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) --start;
        if (end < start + 1) end = start + 1;
        if (end > length) end = length;
        if (end > 0 && end < length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) ++end;
        Items.Add(new Diagnostic(severity, start, end - start, message));
    }
}

internal static class TextHelpers
{
    /// <summary>The C locale <c>isspace</c>: space, tab, line feed, vertical tab, form feed, carriage return.</summary>
    public static bool IsCSpace(char character) => character == ' ' || (character >= '\t' && character <= '\r');

    public static bool IsAsciiLetter(char character) => (character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z');

    public static bool IsDigit(char character) => character >= '0' && character <= '9';

    public static bool IsHexDigit(char character) => IsDigit(character) || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F');

    /// <summary>Index of the code point that ends at <paramref name="end"/> (one or two code units back).</summary>
    public static int PreviousCodePointStart(string text, int end)
    {
        if (end <= 0) return 0;
        if (end >= 2 && char.IsLowSurrogate(text[end - 1]) && char.IsHighSurrogate(text[end - 2])) return end - 2;
        return end - 1;
    }

    /// <summary>Unicode letter, digit, underscore or dollar at <paramref name="index"/> (surrogate pairs handled).</summary>
    public static bool IsWordCharacterAt(string text, int index, out int width)
    {
        width = 1;
        if (index < 0 || index >= text.Length) return false;
        char character = text[index];
        if (character == '_' || character == '$') return true;
        if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            width = 2;
            return char.IsLetterOrDigit(text, index);
        }
        return char.IsLetterOrDigit(character);
    }

    /// <summary>Whether the code point ending at <paramref name="end"/> is a word character; <paramref name="start"/> receives its start.</summary>
    public static bool IsWordCharacterBefore(string text, int end, out int start)
    {
        start = PreviousCodePointStart(text, end);
        if (end <= 0) return false;
        return IsWordCharacterAt(text, start, out _);
    }

    /// <summary>Start of the line containing <paramref name="offset"/> (after the previous '\n' or '\r').</summary>
    public static int LineStart(string text, int offset)
    {
        int index = Math.Clamp(offset, 0, text.Length);
        while (index > 0 && text[index - 1] != '\n' && text[index - 1] != '\r') --index;
        return index;
    }

    /// <summary>End of the line containing <paramref name="offset"/> (at the next '\n' or '\r').</summary>
    public static int LineEnd(string text, int offset)
    {
        int index = Math.Clamp(offset, 0, text.Length);
        while (index < text.Length && text[index] != '\n' && text[index] != '\r') ++index;
        return index;
    }
}

/// <summary>The tokenizer of the C++ <c>tokenizeGS2</c>.</summary>
internal static class Gs2Tokenizer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "const", "enum", "public", "function", "if", "elseif", "else", "for", "while", "do", "switch", "case", "default", "with", "new",
        "return", "break", "continue", "goto", "int", "float", "in", "true", "false", "null", "NULL", "xor",
    };

    private static readonly string[] Operators =
    {
        "<<=", ">>=", ":=", "=<", "=>", "<>", "+=", "-=", "*=", "/=", "^=", "%=", "@=", "|=", "&=", "++", "--", "<<", ">>", "&&", "||", "==", "!=", "<=", ">=",
    };

    private const string SingleOperators = "=<>+-*/%^@!~&|?#:;,.(){}[]";
    private const string MessageSingles = "angcmWw1235678NDLFfpbEStKkGPsvITUeiRQ";
    private static readonly string[] SingleOperatorTexts = BuildSingleOperatorTexts();

    private static string[] BuildSingleOperatorTexts()
    {
        var texts = new string[SingleOperators.Length];
        for (int index = 0; index < SingleOperators.Length; ++index) texts[index] = SingleOperators[index].ToString();
        return texts;
    }

    public static bool IsIdentifierStart(char character) => TextHelpers.IsAsciiLetter(character) || character == '_' || character == '$' || character >= 0x80;

    public static bool IsIdentifierPart(char character) => IsIdentifierStart(character) || TextHelpers.IsDigit(character);

    public static List<Gs2Token> Tokenize(string source, DiagnosticSink diagnostics)
    {
        var tokens = new List<Gs2Token>(Math.Min(source.Length / 2 + 1, 1 << 20));
        int size = source.Length;
        int index = 0;
        int unterminatedDouble = -1;
        int unterminatedSingle = -1;
        int[]? parenthesisMatches = null;
        while (index < size)
        {
            char character = source[index];
            char next = index + 1 < size ? source[index + 1] : '\0';
            if (TextHelpers.IsCSpace(character)) { ++index; continue; }
            if (character == '/' && next == '/')
            {
                int end = source.IndexOf('\n', index);
                index = end < 0 ? size : end;
                continue;
            }
            if (character == '/' && next == '*')
            {
                int end = index + 2 <= size ? source.IndexOf("*/", index + 2, StringComparison.Ordinal) : -1;
                index = end < 0 ? size : end + 2;
                continue;
            }
            if (IsIdentifierStart(character))
            {
                int start = index;
                while (index < size && IsIdentifierPart(source[index])) ++index;
                while (index + 2 < size && source[index] == ':' && source[index + 1] == ':' && IsIdentifierStart(source[index + 2]))
                {
                    index += 2;
                    while (index < size && IsIdentifierPart(source[index])) ++index;
                }
                string word = source.Substring(start, index - start);
                if (word is "NL" or "SPC" or "TAB") tokens.Add(new Gs2Token(Gs2TokenKind.Operator, start, index, "@"));
                else if (Keywords.TryGetValue(word, out string? keyword)) tokens.Add(new Gs2Token(Gs2TokenKind.Keyword, start, index, keyword));
                else tokens.Add(new Gs2Token(Gs2TokenKind.Identifier, start, index, word));
                continue;
            }
            if (TextHelpers.IsDigit(character) || (character == '.' && TextHelpers.IsDigit(next)))
            {
                int start = index;
                if (character == '0' && (next == 'x' || next == 'X') && index + 2 < size && TextHelpers.IsHexDigit(source[index + 2]))
                {
                    index += 2;
                    while (index < size && TextHelpers.IsHexDigit(source[index])) ++index;
                }
                else
                {
                    while (index < size && TextHelpers.IsDigit(source[index])) ++index;
                    if (index < size && source[index] == '.')
                    {
                        ++index;
                        while (index < size && TextHelpers.IsDigit(source[index])) ++index;
                    }
                }
                tokens.Add(new Gs2Token(Gs2TokenKind.Number, start, index, string.Empty));
                continue;
            }
            if (character == '"' || character == '\'')
            {
                int start = index++;
                bool closed = false;
                // A quote after a scan of the same quote that ran to the end unterminated was consumed there as an escaped
                // character, so a scan from it sees the same characters with the same escape pairing: it is unterminated too.
                // Remembering that keeps many unterminated strings linear instead of quadratic.
                int knownUnterminated = character == '"' ? unterminatedDouble : unterminatedSingle;
                if (knownUnterminated < 0 || start <= knownUnterminated)
                {
                    for (; index < size; ++index)
                    {
                        if (source[index] == '\\') { ++index; continue; }
                        if (source[index] == character) { ++index; closed = true; break; }
                    }
                }
                if (!closed)
                {
                    if (character == '"') unterminatedDouble = start; else unterminatedSingle = start;
                    int lineBreak = source.IndexOf('\n', start);
                    index = lineBreak < 0 ? size : lineBreak;
                    diagnostics.Add(DiagnosticSeverity.Error, start, start + 1, "Unterminated string");
                }
                tokens.Add(new Gs2Token(Gs2TokenKind.String, start, Math.Min(index, size), string.Empty));
                continue;
            }
            if (character == '#')
            {
                int length = MessageCodeLength(source, index, ref parenthesisMatches);
                if (length > 0)
                {
                    tokens.Add(new Gs2Token(Gs2TokenKind.Message, index, index + length, string.Empty));
                    index += length;
                    continue;
                }
            }
            bool matched = false;
            foreach (string candidate in Operators)
            {
                if (string.CompareOrdinal(source, index, candidate, 0, candidate.Length) == 0 && index + candidate.Length <= size)
                {
                    tokens.Add(new Gs2Token(Gs2TokenKind.Operator, index, index + candidate.Length, candidate));
                    index += candidate.Length;
                    matched = true;
                    break;
                }
            }
            if (matched) continue;
            int single = SingleOperators.IndexOf(character);
            if (single >= 0)
            {
                tokens.Add(new Gs2Token(Gs2TokenKind.Operator, index, index + 1, SingleOperatorTexts[single]));
                ++index;
                continue;
            }
            diagnostics.Add(DiagnosticSeverity.Error, index, index + 1, UnexpectedCharacterMessage(character));
            ++index;
        }
        tokens.Add(new Gs2Token(Gs2TokenKind.End, size, size, string.Empty));
        return tokens;
    }

    private static readonly string?[] UnexpectedCharacterMessages = new string?[128];

    private static string UnexpectedCharacterMessage(char character)
    {
        if (character >= UnexpectedCharacterMessages.Length) return "Unexpected character '" + Printable(character) + "'";
        return UnexpectedCharacterMessages[character] ??= "Unexpected character '" + Printable(character) + "'";
    }

    /// <summary>The character as the message shows it; control characters (which the C++ printed raw) become <c>U+XXXX</c>.</summary>
    private static string Printable(char character) => character < 0x20 || character == 0x7f ? "U+" + ((int)character).ToString("X4") : character.ToString();

    /// <summary>Length of a GS1 message code such as <c>#c</c>, <c>#P12</c> or <c>#s(name)</c>, or 0.</summary>
    private static int MessageCodeLength(string source, int start, ref int[]? parenthesisMatches)
    {
        int size = source.Length;
        int index = start + 1;
        if (index >= size) return 0;
        char first = source[index];
        if (first == 'U' && index + 1 < size && source[index + 1] == '2') index += 2;
        else if (first == 'C' && index + 1 < size && source[index + 1] >= '0' && source[index + 1] <= '7') index += 2;
        else if (first == 'P' && index + 1 < size && source[index + 1] >= '1' && source[index + 1] <= '9')
        {
            index += 2;
            while (index < size && TextHelpers.IsDigit(source[index])) ++index;
        }
        else if (MessageSingles.Contains(first)) ++index;
        else return 0;
        if (index < size && source[index] == '(')
        {
            parenthesisMatches ??= MatchParenthesesPerLine(source);
            int close = parenthesisMatches[index];
            if (close >= 0) index = close + 1;
        }
        return index - start;
    }

    /// <summary>
    /// For every '(' the index of the ')' that closes it on the same line ('\n' and '\r' end a line), else -1. This is what the
    /// C++ found by scanning forward from each '(' (which was quadratic on long lines).
    /// </summary>
    private static int[] MatchParenthesesPerLine(string source)
    {
        var matches = new int[source.Length];
        var open = new Stack<int>();
        for (int index = 0; index < source.Length; ++index)
        {
            matches[index] = -1;
            char character = source[index];
            if (character == '\n' || character == '\r') open.Clear();
            else if (character == '(') open.Push(index);
            else if (character == ')' && open.Count > 0) matches[open.Pop()] = index;
        }
        return matches;
    }
}
