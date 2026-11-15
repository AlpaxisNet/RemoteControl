namespace RemoteControl.Scripting;

/// <summary>One block of popover text.</summary>
/// <param name="Text">The text (plain, not markup).</param>
/// <param name="Bold">Whether the block is bold.</param>
public sealed record HoverBlock(string Text, bool Bold);

/// <summary>What the hover popover shows (C++ <c>hoverMarkup</c>).</summary>
/// <param name="WordStart">Start of the hovered word (where the popover points).</param>
/// <param name="WordLength">Length of the hovered word.</param>
/// <param name="Blocks">In order: the diagnostics at the offset (bold, one per line), the signature (bold) and the definition info.</param>
/// <param name="Definition">The definition shown, or null when only diagnostics are shown.</param>
public sealed record HoverContent(int WordStart, int WordLength, IReadOnlyList<HoverBlock> Blocks, ApiDefinition? Definition)
{
    /// <summary>The blocks as plain text with the C++ separators (a blank line between diagnostics and definition, a line break after the signature).</summary>
    /// <returns>The text.</returns>
    public string ToPlainText()
    {
        var builder = new System.Text.StringBuilder();
        for (int index = 0; index < Blocks.Count; ++index)
        {
            if (index > 0) builder.Append(Blocks[index - 1].Bold && Blocks[index].Bold ? "\n\n" : "\n");
            builder.Append(Blocks[index].Text);
        }
        return builder.ToString();
    }
}

/// <summary>The hover popover's content for a pointer position.</summary>
public static class HoverInfo
{
    /// <summary>
    /// Computes the hover at <paramref name="offset"/>: the hovered word is the run of identifier characters around it on its line
    /// (a non-identifier character is a one-character "word"); diagnostics whose range contains the offset (end inclusive) come
    /// first, then the definition of the word (API by name, else the script's functions).
    /// </summary>
    /// <param name="text">The editor text.</param>
    /// <param name="offset">The character under the pointer.</param>
    /// <param name="diagnostics">The diagnostics of the text (may be null).</param>
    /// <param name="definitions">The API definitions (may be null).</param>
    /// <param name="localFunctions">The script's functions (may be null).</param>
    /// <returns>The content, or null when there is nothing to show (also at a line end).</returns>
    public static HoverContent? At(string? text, int offset, IReadOnlyList<Diagnostic>? diagnostics, ApiDefinitionSet? definitions, IReadOnlyList<ApiDefinition>? localFunctions = null)
    {
        string source = text ?? string.Empty;
        if (offset < 0 || offset >= source.Length) return null;
        if (!WordRange(source, offset, out int start, out int end)) return null;
        var blocks = new List<HoverBlock>();
        if (diagnostics != null)
        {
            var messages = new List<string>();
            foreach (Diagnostic diagnostic in diagnostics)
            {
                if (offset < diagnostic.Start || offset > Math.Max(diagnostic.Start, diagnostic.End)) continue;
                messages.Add(diagnostic.HoverText);
            }
            if (messages.Count > 0) blocks.Add(new HoverBlock(string.Join("\n", messages), true));
        }
        string word = source.Substring(start, end - start);
        ApiDefinition? definition = word.Length == 0 ? null : (definitions ?? ApiDefinitionSet.Create(Array.Empty<ApiDefinition>())).Resolve(word, localFunctions);
        if (definition != null)
        {
            blocks.Add(new HoverBlock(definition.Signature, true));
            blocks.Add(new HoverBlock(definition.Info, false));
        }
        return blocks.Count == 0 ? null : new HoverContent(start, end - start, blocks, definition);
    }

    /// <summary>The hovered word around <paramref name="offset"/> (C++ <c>hoverWordRange</c>).</summary>
    /// <param name="text">The text.</param>
    /// <param name="offset">The offset.</param>
    /// <param name="start">Start of the word.</param>
    /// <param name="end">End of the word.</param>
    /// <returns>False at a line end or outside the text.</returns>
    public static bool WordRange(string text, int offset, out int start, out int end)
    {
        start = end = offset;
        if (offset < 0 || offset >= text.Length) return false;
        if (char.IsLowSurrogate(text[offset]) && offset > 0 && char.IsHighSurrogate(text[offset - 1])) start = end = --offset;
        if (!TextHelpers.IsWordCharacterAt(text, offset, out int width))
        {
            if (text[offset] == '\n' || text[offset] == '\r') return false;
            end = offset + width;
            return true;
        }
        while (start > 0 && text[start - 1] != '\n' && text[start - 1] != '\r' && TextHelpers.IsWordCharacterBefore(text, start, out int previous)) start = previous;
        while (end < text.Length && TextHelpers.IsWordCharacterAt(text, end, out int partWidth)) end += partWidth;
        return true;
    }
}

/// <summary>One styled piece of the signature hint.</summary>
/// <param name="Text">The text.</param>
/// <param name="Bold">Bold.</param>
/// <param name="Italic">Italic.</param>
/// <param name="Underline">Underlined (the active parameter).</param>
public sealed record SignaturePart(string Text, bool Bold = false, bool Italic = false, bool Underline = false);

/// <summary>The signature hint for the call around the caret.</summary>
/// <param name="CallName">The (possibly dotted) name before the innermost unclosed '('.</param>
/// <param name="OpenOffset">Offset of that '('.</param>
/// <param name="ArgumentIndex">Number of top-level commas between the '(' and the caret.</param>
/// <param name="ActiveParameter">The highlighted parameter: <paramref name="ArgumentIndex"/> clamped to the last parameter, or -1 without parameters.</param>
/// <param name="Definition">The definition shown.</param>
/// <param name="Parts">The popover text as styled parts, in the C++ markup order.</param>
public sealed record SignatureHelpResult(string CallName, int OpenOffset, int ArgumentIndex, int ActiveParameter, ApiDefinition Definition, IReadOnlyList<SignaturePart> Parts)
{
    /// <summary>The parts as plain text.</summary>
    /// <returns>The text.</returns>
    public string ToPlainText() => string.Concat(Parts.Select(part => part.Text));
}

/// <summary>The signature hint (C++ <c>findActiveCallContext</c> and <c>updateSignatureHint</c>).</summary>
public static class SignatureHelp
{
    /// <summary>
    /// Finds the innermost unclosed '(' between the start of the caret's line and the caret, ignoring strings and comments, takes
    /// the dotted name before it and counts the top-level commas after it.
    /// </summary>
    /// <param name="text">The editor text.</param>
    /// <param name="caretOffset">The caret.</param>
    /// <param name="definitions">The API definitions (may be null).</param>
    /// <param name="localFunctions">The script's functions (may be null).</param>
    /// <returns>The hint, or null when there is no call or the definition has nothing to show.</returns>
    public static SignatureHelpResult? At(string? text, int caretOffset, ApiDefinitionSet? definitions, IReadOnlyList<ApiDefinition>? localFunctions = null)
    {
        string source = text ?? string.Empty;
        int caret = Math.Clamp(caretOffset, 0, source.Length);
        int lineStart = TextHelpers.LineStart(source, caret);
        string line = source.Substring(lineStart, caret - lineStart);
        if (!FindActiveCall(line, out int open, out string name, out int argument)) return null;
        ApiDefinition? definition = (definitions ?? ApiDefinitionSet.Create(Array.Empty<ApiDefinition>())).Resolve(name, localFunctions);
        if (definition == null || !definition.HasSignatureDetails) return null;
        var parts = new List<SignaturePart> { new(definition.Signature, Bold: true) };
        string kind = definition.Type.Length == 0 && definition.Scope.Length == 0 ? string.Empty
            : definition.Type + (definition.Scope.Length == 0 ? string.Empty : "  " + definition.Scope.ToUpperInvariant());
        if (kind.Length > 0) { parts.Add(new SignaturePart("  ")); parts.Add(new SignaturePart(kind, Italic: true)); }
        int active = -1;
        if (definition.Parameters.Count > 0)
        {
            active = Math.Min(argument, definition.Parameters.Count - 1);
            parts.Add(new SignaturePart("\n"));
            for (int index = 0; index < definition.Parameters.Count; ++index)
            {
                if (index != 0) parts.Add(new SignaturePart(", "));
                parts.Add(index == active ? new SignaturePart(definition.Parameters[index], Bold: true, Underline: true) : new SignaturePart(definition.Parameters[index]));
            }
        }
        if (definition.Returns.Length > 0) { parts.Add(new SignaturePart("\n")); parts.Add(new SignaturePart("Returns:", Bold: true)); parts.Add(new SignaturePart(" " + definition.Returns)); }
        if (definition.Description.Length > 0) parts.Add(new SignaturePart("\n" + definition.Description));
        if (definition.Example.Length > 0) { parts.Add(new SignaturePart("\n\n")); parts.Add(new SignaturePart("Example:", Bold: true)); parts.Add(new SignaturePart("\n" + definition.Example)); }
        return new SignatureHelpResult(name, lineStart + open, argument, active, definition, parts);
    }

    /// <summary>The call context of a line prefix.</summary>
    /// <param name="line">Text from the line start to the caret.</param>
    /// <param name="open">Index of the innermost unclosed '('.</param>
    /// <param name="name">The name before it (letters, digits, '_', '$', '.').</param>
    /// <param name="argument">Top-level commas after it.</param>
    /// <returns>True when there is a call with a name.</returns>
    /// <remarks>
    /// The C++ counted commas after the '(' without skipping comments, so <c>f(a /* , */</c> counted two arguments; comments are
    /// skipped here as well.
    /// </remarks>
    public static bool FindActiveCall(string line, out int open, out string name, out int argument)
    {
        open = -1;
        name = string.Empty;
        argument = 0;
        // Only '(' matters for the result, and closing ']' or '}' only ever remove their own kind, so a stack of '(' is
        // equivalent to the C++ list of all delimiters (and linear where its backward search was quadratic).
        var parentheses = new Stack<int>();
        char quote = '\0';
        bool escaped = false;
        bool blockComment = false;
        for (int index = 0; index < line.Length; ++index)
        {
            char character = line[index];
            char next = index + 1 < line.Length ? line[index + 1] : '\0';
            if (blockComment)
            {
                if (character == '*' && next == '/') { blockComment = false; ++index; }
                continue;
            }
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == quote) quote = '\0';
                continue;
            }
            if (character == '/' && next == '/') break;
            if (character == '/' && next == '*') { blockComment = true; ++index; continue; }
            if (character == '"' || character == '\'') { quote = character; continue; }
            if (character == '(') parentheses.Push(index);
            else if (character == ')' && parentheses.Count > 0) parentheses.Pop();
        }
        if (parentheses.Count > 0) open = parentheses.Peek();
        if (open < 0) return false;
        int nameEnd = open;
        while (nameEnd > 0 && TextHelpers.IsCSpace(line[nameEnd - 1])) --nameEnd;
        int nameStart = nameEnd;
        while (nameStart > 0 && (char.IsAsciiLetterOrDigit(line[nameStart - 1]) || line[nameStart - 1] is '_' or '$' or '.')) --nameStart;
        name = line.Substring(nameStart, nameEnd - nameStart);
        int nested = 0;
        quote = '\0';
        escaped = false;
        blockComment = false;
        for (int index = open + 1; index < line.Length; ++index)
        {
            char character = line[index];
            char next = index + 1 < line.Length ? line[index + 1] : '\0';
            if (blockComment)
            {
                if (character == '*' && next == '/') { blockComment = false; ++index; }
                continue;
            }
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == quote) quote = '\0';
                continue;
            }
            if (character == '/' && next == '/') break;
            if (character == '/' && next == '*') { blockComment = true; ++index; continue; }
            if (character == '"' || character == '\'') { quote = character; continue; }
            if (character is '(' or '[' or '{') ++nested;
            else if (character is ')' or ']' or '}') { if (nested > 0) --nested; }
            else if (character == ',' && nested == 0) ++argument;
        }
        return name.Length > 0;
    }
}

/// <summary>The <c>/scripthelp</c> answer (C++ <c>formatScriptHelp</c>).</summary>
public static class ScriptHelp
{
    /// <summary>Formats the help lines for a query.</summary>
    /// <param name="definitions">The API definitions.</param>
    /// <param name="query">The query; empty lists everything.</param>
    /// <returns>An exact match's details, up to 30 matching entries, or a "no entries" line.</returns>
    public static IReadOnlyList<string> Format(IReadOnlyList<ApiDefinition> definitions, string? query)
    {
        string text = query ?? string.Empty;
        string needle = text.ToLowerInvariant();
        var matches = (definitions ?? Array.Empty<ApiDefinition>()).Where(definition => needle.Length == 0 || definition.SearchName.Contains(needle, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0) return new[] { "[Script Help] No API entries match '" + text + "'." };
        ApiDefinition? exact = matches.FirstOrDefault(definition => definition.SearchName == needle);
        if (exact != null)
        {
            var lines = new List<string> { "[Script Help] " + exact.Signature + (exact.Scope.Length == 0 ? "" : " [" + exact.Scope.ToUpperInvariant() + "]") };
            if (exact.Returns.Length > 0) lines.Add("Returns: " + exact.Returns);
            if (exact.Description.Length > 0) lines.Add(exact.Description);
            if (exact.Example.Length > 0) lines.Add("Example: " + exact.Example);
            return lines;
        }
        var result = new List<string> { "[Script Help] " + matches.Count + " entries matching '" + text + "':" };
        foreach (ApiDefinition definition in matches.Take(30))
        {
            result.Add(definition.Signature + (definition.Scope.Length == 0 ? "" : " [" + definition.Scope.ToUpperInvariant() + "]") + (definition.Description.Length == 0 ? "" : " - " + definition.Description));
        }
        if (matches.Count > 30) result.Add("Use a longer /scripthelp2 query to narrow the result.");
        return result;
    }
}
