namespace RemoteControl.Scripting;

/// <summary>An online player offered when completing inside a string literal.</summary>
/// <param name="Account">Account name (what is inserted).</param>
/// <param name="Nick">Nickname; may be empty.</param>
/// <param name="CommunityName">Community name from the player properties; may be empty.</param>
public sealed record OnlinePlayer(string Account, string Nick, string CommunityName);

/// <summary>How the completion was requested.</summary>
public enum CompletionTrigger
{
    /// <summary>Ctrl+Space: proposals even for an empty or one-letter prefix.</summary>
    UserRequested,

    /// <summary>While typing: nothing until the prefix has two characters. The editor only asks after a printable key without modifiers.</summary>
    Interactive,
}

/// <summary>What a completion item is, for its icon.</summary>
public enum CompletionItemKind
{
    /// <summary>A function, event or anything else from the API or the script.</summary>
    Function,

    /// <summary>An API entry of type <c>class</c>.</summary>
    Class,

    /// <summary>An API entry of type <c>property</c>.</summary>
    Property,

    /// <summary>An online player.</summary>
    Player,

    /// <summary>An identifier found in the current script.</summary>
    Identifier,
}

/// <summary>One completion proposal.</summary>
/// <param name="Label">The list text.</param>
/// <param name="InsertText">What replaces the prefix.</param>
/// <param name="Kind">The icon category.</param>
/// <param name="KindLabel">The preview strip's kind: the API type (or "Function"), "Player" or "Identifier".</param>
/// <param name="Scope">The right-aligned scope column (GLOBAL, CLIENTSIDE ...); may be empty.</param>
/// <param name="Info">The detail text.</param>
/// <param name="Summary">The preview strip text.</param>
/// <param name="Definition">The API or local definition behind the item, or null for players and identifiers.</param>
public sealed record CompletionItem(string Label, string InsertText, CompletionItemKind Kind, string KindLabel, string Scope, string Info, string Summary, ApiDefinition? Definition);

/// <summary>The proposals for one caret position.</summary>
/// <param name="Prefix">The identifier characters before the caret on its line, as typed.</param>
/// <param name="ReplaceStart">Start of the text an accepted item replaces (the prefix).</param>
/// <param name="ReplaceLength">Length of the text an accepted item replaces.</param>
/// <param name="Items">At most 16 items: API and script functions by rank, then players, then identifiers.</param>
public sealed record CompletionResult(string Prefix, int ReplaceStart, int ReplaceLength, IReadOnlyList<CompletionItem> Items)
{
    /// <summary>A result without items.</summary>
    /// <param name="caret">The caret offset.</param>
    /// <returns>The empty result.</returns>
    public static CompletionResult Empty(int caret) => new(string.Empty, caret, 0, Array.Empty<CompletionItem>());
}

/// <summary>The completion match score (C++ <c>completionMatchScoreLower</c>).</summary>
public static class CompletionRanking
{
    /// <summary>Scores a candidate against what was typed; lower is better, negative means "no match".</summary>
    /// <param name="candidate">The candidate (compared case-insensitively).</param>
    /// <param name="needle">What was typed (compared case-insensitively).</param>
    /// <returns>0 for an empty needle or an exact match, 10 for a prefix, 20 + position for a substring, 100 + gaps for a subsequence, else -1.</returns>
    public static int Score(string candidate, string needle)
    {
        string lowerNeedle = (needle ?? string.Empty).ToLowerInvariant();
        if (lowerNeedle.Length == 0) return 0;
        return ScoreLower((candidate ?? string.Empty).ToLowerInvariant(), lowerNeedle);
    }

    internal static int ScoreLower(string lowerCandidate, string needle)
    {
        if (needle.Length == 0) return 0;
        if (lowerCandidate == needle) return 0;
        if (lowerCandidate.StartsWith(needle, StringComparison.Ordinal)) return 10;
        int position = lowerCandidate.IndexOf(needle, StringComparison.Ordinal);
        if (position >= 0) return 20 + position;
        int candidatePosition = 0;
        int gaps = 0;
        foreach (char character in needle)
        {
            int found = lowerCandidate.IndexOf(character, candidatePosition);
            if (found < 0) return -1;
            gaps += found - candidatePosition;
            candidatePosition = found + 1;
        }
        return 100 + gaps;
    }
}

/// <summary>
/// The functions and identifiers of the current script (C++ <c>localFunctionDefinitions</c> and <c>localIdentifiers</c>).
/// The editor recomputes it on a background thread 180 ms after a change; it is immutable.
/// </summary>
public sealed class LocalSymbols
{
    private LocalSymbols(IReadOnlyList<ApiDefinition> functions, IReadOnlyList<string> identifiers)
    {
        Functions = functions;
        Identifiers = identifiers;
    }

    /// <summary>No symbols.</summary>
    public static LocalSymbols Empty { get; } = new(Array.Empty<ApiDefinition>(), Array.Empty<string>());

    /// <summary>Functions declared in the script, one per name (case-insensitive), in order of appearance.</summary>
    public IReadOnlyList<ApiDefinition> Functions { get; }

    /// <summary>Identifiers and dotted chains (<c>a.b.c</c>) of the whole text, the first spelling of each (case-insensitive).</summary>
    public IReadOnlyList<string> Identifiers { get; }

    /// <summary>Extracts the symbols of a script.</summary>
    /// <param name="text">The script.</param>
    /// <returns>The symbols.</returns>
    public static LocalSymbols Extract(string? text)
    {
        string source = text ?? string.Empty;
        return new LocalSymbols(ExtractFunctions(source), ExtractIdentifiers(source));
    }

    /// <summary>
    /// Every line containing <c>"function "</c> not preceded by '/': the name (ASCII letters, digits, '_' and '$' after optional
    /// whitespace) and the comma-separated parameters inside the first <c>(...)</c> after it on the same line. Scope is
    /// <c>clientside</c> when the script has <c>//#CLIENTSIDE</c> and no <c>//#SERVERSIDE</c>, else <c>script</c>.
    /// </summary>
    /// <param name="source">The script.</param>
    /// <returns>The functions.</returns>
    public static IReadOnlyList<ApiDefinition> ExtractFunctions(string source)
    {
        var definitions = new List<ApiDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string scope = source.Contains("//#CLIENTSIDE", StringComparison.Ordinal) && !source.Contains("//#SERVERSIDE", StringComparison.Ordinal) ? "clientside" : "script";
        int lineStart = 0;
        while (lineStart <= source.Length)
        {
            int newline = source.IndexOf('\n', lineStart);
            int lineEnd = newline < 0 ? source.Length : newline;
            ReadOnlySpan<char> line = source.AsSpan(lineStart, lineEnd - lineStart);
            int marker = line.IndexOf("function ", StringComparison.Ordinal);
            if (marker >= 0 && (marker == 0 || line[marker - 1] != '/'))
            {
                int nameStart = marker + 9;
                while (nameStart < line.Length && TextHelpers.IsCSpace(line[nameStart])) ++nameStart;
                int nameEnd = nameStart;
                while (nameEnd < line.Length && (char.IsAsciiLetterOrDigit(line[nameEnd]) || line[nameEnd] == '_' || line[nameEnd] == '$')) ++nameEnd;
                int open = line[nameEnd..].IndexOf('(');
                if (open >= 0) open += nameEnd;
                int close = open < 0 ? -1 : line[(open + 1)..].IndexOf(')');
                if (close >= 0) close += open + 1;
                if (nameEnd > nameStart && open >= 0 && close >= 0)
                {
                    string name = line[nameStart..nameEnd].ToString();
                    if (seen.Add(name.ToLowerInvariant()))
                    {
                        var parameters = new List<string>();
                        int parameterStart = open + 1;
                        while (parameterStart < close)
                        {
                            int comma = line[parameterStart..close].IndexOf(',');
                            int parameterEnd = comma < 0 ? close : parameterStart + comma;
                            string parameter = line[parameterStart..parameterEnd].Trim(" \t").ToString();
                            if (parameter.Length > 0) parameters.Add(parameter);
                            parameterStart = parameterEnd + 1;
                        }
                        definitions.Add(new ApiDefinition(name, "Function", parameters, null, scope, "Function defined in the current script."));
                    }
                }
            }
            if (newline < 0) break;
            lineStart = newline + 1;
        }
        return definitions;
    }

    /// <summary>Identifiers (a Unicode letter, '_' or '$', then letters, digits, '_', '$') joined by single dots, first spelling kept.</summary>
    /// <param name="source">The script.</param>
    /// <returns>The identifiers in order of first appearance.</returns>
    public static IReadOnlyList<string> ExtractIdentifiers(string source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        int cursor = 0;
        while (cursor < source.Length)
        {
            if (!IsIdentifierStartAt(source, cursor, out int width)) { cursor += width; continue; }
            int wordStart = cursor;
            cursor += width;
            while (cursor < source.Length)
            {
                while (TextHelpers.IsWordCharacterAt(source, cursor, out int partWidth)) cursor += partWidth;
                if (cursor >= source.Length || source[cursor] != '.' || !TextHelpers.IsWordCharacterAt(source, cursor + 1, out _)) break;
                ++cursor;
            }
            string name = source.Substring(wordStart, cursor - wordStart);
            if (seen.Add(name.ToLowerInvariant())) names.Add(name);
        }
        return names;
    }

    private static bool IsIdentifierStartAt(string text, int index, out int width)
    {
        width = 1;
        char character = text[index];
        if (character == '_' || character == '$') return true;
        if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            width = 2;
            return char.IsLetter(text, index);
        }
        return char.IsLetter(character);
    }
}

/// <summary>
/// Completion for the script editor (C++ <c>remoteCompletionProviderPopulate</c>). An instance is an immutable snapshot of the
/// API definitions and the script's symbols, so <see cref="Propose"/> can run on any thread.
/// </summary>
public sealed class CompletionEngine
{
    /// <summary>The C++ limit on proposals.</summary>
    public const int MaximumProposals = 16;

    /// <summary>Creates an engine.</summary>
    /// <param name="definitions">The API definitions; null means built-ins only.</param>
    /// <param name="localSymbols">The script's symbols as last computed; null means they are extracted from the text on each call.</param>
    public CompletionEngine(ApiDefinitionSet? definitions = null, LocalSymbols? localSymbols = null)
    {
        Definitions = definitions ?? ApiDefinitionSet.Create(Array.Empty<ApiDefinition>());
        LocalSymbols = localSymbols;
    }

    /// <summary>The API definitions.</summary>
    public ApiDefinitionSet Definitions { get; }

    /// <summary>The script's symbols, or null to extract them from the text on each call.</summary>
    public LocalSymbols? LocalSymbols { get; }

    /// <summary>A copy with other definitions.</summary>
    /// <param name="definitions">The definitions.</param>
    /// <returns>The new engine.</returns>
    public CompletionEngine WithDefinitions(ApiDefinitionSet definitions) => new(definitions, LocalSymbols);

    /// <summary>A copy with other script symbols.</summary>
    /// <param name="localSymbols">The symbols.</param>
    /// <returns>The new engine.</returns>
    public CompletionEngine WithLocalSymbols(LocalSymbols? localSymbols) => new(Definitions, localSymbols);

    /// <summary>The identifier characters before <paramref name="caret"/> on its line (letters, digits, '_' and '$').</summary>
    /// <param name="text">The text.</param>
    /// <param name="caret">The caret offset.</param>
    /// <returns>The start of the prefix.</returns>
    public static int PrefixStart(string text, int caret)
    {
        int start = Math.Clamp(caret, 0, text.Length);
        while (start > 0 && text[start - 1] != '\n' && text[start - 1] != '\r' && TextHelpers.IsWordCharacterBefore(text, start, out int previous)) start = previous;
        return start;
    }

    /// <summary>Whether the caret is inside a string literal that started on the current line (C++ <c>completionInsideString</c>).</summary>
    /// <param name="text">The text.</param>
    /// <param name="caret">The caret offset.</param>
    /// <returns>True inside a string.</returns>
    public static bool IsInsideString(string text, int caret)
    {
        int end = Math.Clamp(caret, 0, text.Length);
        int start = TextHelpers.LineStart(text, end);
        char quote = '\0';
        bool escaped = false;
        for (int index = start; index < end; ++index)
        {
            char character = text[index];
            if (escaped) escaped = false;
            else if (character == '\\') escaped = true;
            else if (quote == '\0' && (character == '"' || character == '\'')) quote = character;
            else if (character == quote) quote = '\0';
        }
        return quote != '\0';
    }

    /// <summary>
    /// The object prefix before the caret: from the start of the dotted chain (identifier characters and dots on the line) up to
    /// and including its last dot, for example <c>player.</c> in <c>player.ch|</c>; empty without a dot.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="caret">The caret offset.</param>
    /// <returns>The object prefix.</returns>
    public static string ObjectPrefix(string text, int caret)
    {
        int end = Math.Clamp(caret, 0, text.Length);
        int start = end;
        while (start > 0 && text[start - 1] != '\n' && text[start - 1] != '\r')
        {
            if (text[start - 1] == '.') { --start; continue; }
            if (!TextHelpers.IsWordCharacterBefore(text, start, out int previous)) break;
            start = previous;
        }
        string context = text.Substring(start, end - start);
        int dot = context.LastIndexOf('.');
        return dot < 0 ? string.Empty : context.Substring(0, dot + 1);
    }

    /// <summary>Computes the proposals at the caret.</summary>
    /// <param name="text">The editor text.</param>
    /// <param name="caretOffset">The caret offset.</param>
    /// <param name="players">Online players of a live connection; pass an empty list when disconnected.</param>
    /// <param name="trigger">How the completion was requested.</param>
    /// <returns>The proposals; empty for an interactive request with a prefix shorter than two characters.</returns>
    public CompletionResult Propose(string? text, int caretOffset, IReadOnlyList<OnlinePlayer>? players, CompletionTrigger trigger = CompletionTrigger.UserRequested)
    {
        string source = text ?? string.Empty;
        int caret = Math.Clamp(caretOffset, 0, source.Length);
        int prefixStart = PrefixStart(source, caret);
        string typed = source.Substring(prefixStart, caret - prefixStart);
        string prefix = typed.ToLowerInvariant();
        if (trigger == CompletionTrigger.Interactive && prefix.Length < 2) return new CompletionResult(typed, prefixStart, typed.Length, Array.Empty<CompletionItem>());
        string objectPrefix = ObjectPrefix(source, caret);
        LocalSymbols symbols = LocalSymbols ?? LocalSymbols.Extract(source);

        var ranked = new List<(int Score, ApiDefinition Definition)>(MaximumProposals);
        foreach (ApiDefinition definition in Definitions.Definitions) Rank(ranked, definition, prefix);
        foreach (ApiDefinition definition in symbols.Functions)
        {
            if (Definitions.Find(definition.Name) == null) Rank(ranked, definition, prefix);
        }
        ranked.Sort(CompareRanked);

        var items = new List<CompletionItem>(MaximumProposals);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((_, ApiDefinition definition) in ranked)
        {
            seen.Add(definition.SearchName);
            string type = definition.Type.ToLowerInvariant();
            CompletionItemKind kind = type == "class" ? CompletionItemKind.Class : type == "property" ? CompletionItemKind.Property : CompletionItemKind.Function;
            items.Add(new CompletionItem(definition.CompletionLabel, definition.Name, kind, definition.Type.Length == 0 ? "Function" : definition.Type,
                definition.ScopeLabel, definition.CompletionInfo, definition.CompletionSummary, definition));
        }

        if (items.Count < MaximumProposals && players != null && players.Count > 0 && IsInsideString(source, caret))
        {
            foreach (OnlinePlayer player in players)
            {
                if (player == null) continue;
                string account = player.Account ?? string.Empty;
                var aliasesSeen = new HashSet<string>(StringComparer.Ordinal);
                foreach ((string alias, string kind) in new[] { (account, "ACCOUNT"), (player.Nick ?? string.Empty, "NICK"), (player.CommunityName ?? string.Empty, "COMMUNITY") })
                {
                    if (items.Count >= MaximumProposals) break;
                    string lowerAlias = alias.ToLowerInvariant();
                    if (alias.Length == 0 || prefix.Length == 0 || !aliasesSeen.Add(lowerAlias) || CompletionRanking.ScoreLower(lowerAlias, prefix) < 0) continue;
                    if (!seen.Add("player:" + lowerAlias)) continue;
                    string detail = "Online player" + (account.Length == 0 ? string.Empty : " — " + account);
                    items.Add(new CompletionItem(alias + "  [" + kind + "]", account.Length == 0 ? alias : account, CompletionItemKind.Player, "Player", string.Empty, detail, detail, null));
                }
            }
        }

        if (items.Count < MaximumProposals)
        {
            string lowerObjectPrefix = objectPrefix.ToLowerInvariant();
            foreach (string name in symbols.Identifiers)
            {
                string insertText = name;
                if (objectPrefix.Length > 0)
                {
                    if (!name.ToLowerInvariant().StartsWith(lowerObjectPrefix, StringComparison.Ordinal)) continue;
                    insertText = name.Substring(objectPrefix.Length);
                }
                string lowerInsert = insertText.ToLowerInvariant();
                if (CompletionRanking.ScoreLower(lowerInsert, prefix) < 0 || !seen.Add(lowerInsert)) continue;
                items.Add(new CompletionItem(insertText, insertText, CompletionItemKind.Identifier, "Identifier", string.Empty, "Current script identifier", "Current script identifier", null));
                if (items.Count >= MaximumProposals) break;
            }
        }
        return new CompletionResult(typed, prefixStart, typed.Length, items);
    }

    private static int CompareRanked((int Score, ApiDefinition Definition) left, (int Score, ApiDefinition Definition) right)
    {
        if (left.Score != right.Score) return left.Score.CompareTo(right.Score);
        return string.CompareOrdinal(left.Definition.SearchName, right.Definition.SearchName);
    }

    /// <summary>Keeps the best <see cref="MaximumProposals"/> definitions by (score, name), replacing the worst kept one when a better arrives.</summary>
    private static void Rank(List<(int Score, ApiDefinition Definition)> ranked, ApiDefinition definition, string prefix)
    {
        int score = CompletionRanking.ScoreLower(definition.SearchName, prefix);
        if (score < 0) return;
        (int, ApiDefinition) candidate = (score, definition);
        if (ranked.Count < MaximumProposals) { ranked.Add(candidate); return; }
        int worst = 0;
        for (int index = 1; index < ranked.Count; ++index)
        {
            if (CompareRanked(ranked[worst], ranked[index]) < 0) worst = index;
        }
        if (CompareRanked(candidate, ranked[worst]) < 0) ranked[worst] = candidate;
    }
}
