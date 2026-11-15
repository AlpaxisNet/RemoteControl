namespace RemoteControl.Scripting;

/// <summary>
/// The GS2 script analysis (port of <c>TGS2Diagnostics.cpp</c>): a tokenizer, a syntax check with GS1 command handling, and
/// heuristics on the comment- and string-masked text. Offsets are UTF-16 indices into the analysed string. The analysis runs in
/// linear or near-linear time on any input, never recurses deeper than the nesting limit, and never throws.
/// </summary>
public static class Gs2Diagnostics
{
    private static readonly string[] Events =
    {
        "onCreated", "onTimeout", "onPlayerEnters", "onPlayerLeaves", "onPlayerChats", "onPlayerTouchsMe", "onWeaponFired", "onActionServerSide",
        "onActionClientSide", "onMouseDown", "onMouseUp", "onMouseMove", "onMouseWheel", "onKeyPressed", "onKeyDown", "onKeyUp", "onKeyReleased",
        "onPlayerLogin", "onPlayerLogout", "onPlayerDies", "onPlayerHurt", "onMovementFinished", "onWarp", "onNpcWarped", "onLevelLoaded",
        "onInitialized", "onDestroy", "onRemoved", "onClose", "onConnect", "onData", "onRender", "onWasHit", "onWasPelt", "onServerStart", "onServerStop",
    };

    private static readonly string[] PredefinedVariables = { "this", "thiso", "player", "level", "clientr", "serverr", "client", "server", "global", "params", "npc" };

    private static readonly (string Name, int Minimum, int Maximum)[] BuiltInSignatures =
    {
        ("abs", 1, 1), ("sin", 1, 1), ("char", 1, 1), ("cos", 1, 1), ("tan", 1, 1), ("arcsin", 1, 1), ("arccos", 1, 1), ("arctan", 1, 1), ("vecx", 1, 1),
        ("vecy", 1, 1), ("exp", 1, 1), ("min", 2, 2), ("max", 2, 2), ("log", 2, 2), ("pow", 2, 2), ("int", 1, 1), ("float", 1, 1), ("strlen", 1, 1),
        ("tokenize", 1, 1), ("arraylen", 1, 1), ("sarraylen", 1, 1), ("echo", 1, 1), ("say", 1, 1), ("say2", 1, 1), ("message", 1, 1), ("join", 1, 1),
        ("leave", 1, 1), ("format", 1, -1), ("makevar", 1, 1), ("setarray", 2, 2), ("putnpc", 3, 3), ("putnpc2", 3, 3), ("putexplosion", 3, 3),
        ("findplayer", 1, 1), ("findnpc", 1, 1), ("findlevel", 1, 1), ("settimer", 1, 1), ("sleep", 1, 1), ("waitfor", 2, 3), ("getangle", 2, 2),
        ("getdir", 2, 2), ("triggeraction", 5, 5), ("random", 2, 2),
    };

    private static readonly HashSet<string> KnownCalls = new(StringComparer.Ordinal)
    {
        "if", "elseif", "for", "while", "switch", "with", "new", "format", "makevar", "requesttext", "sendtext", "sendtorc", "sendtonc", "savelines",
        "loadlines", "addtiledef", "addtiledef2", "setarray", "insertarray", "deletearray", "replacearray", "cleararray", "arraylen", "getstringkeys",
        "getstringvalue", "setstring", "deletestring", "strequals", "starts", "ends", "contains", "positions", "substring", "trim", "lowercase", "uppercase",
    };

    private static readonly HashSet<string> KnownEvents = new(Events.Select(name => name.ToLowerInvariant()), StringComparer.Ordinal);

    /// <summary>Analyses a script.</summary>
    /// <param name="text">The script; null counts as empty.</param>
    /// <param name="options">API knowledge and switches; null means <see cref="DiagnosticsOptions.Default"/>.</param>
    /// <returns>The diagnostics sorted by start, more serious first at the same start. Empty for large content (see <see cref="EditorLimits"/>).</returns>
    public static IReadOnlyList<Diagnostic> Analyze(string? text, DiagnosticsOptions? options = null)
    {
        string source = text ?? string.Empty;
        options ??= DiagnosticsOptions.Default;
        if (!options.AnalyzeLargeContent && EditorLimits.IsLargeContent(source)) return Array.Empty<Diagnostic>();
        var sink = new DiagnosticSink(source);
        try
        {
            AnalyzeInto(source, options, sink);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Defensive only: the analysis is written not to throw. A failure keeps what was found so far.
        }
        return Sorted(sink.Items);
    }

    private static Diagnostic[] Sorted(List<Diagnostic> diagnostics) =>
        diagnostics.OrderBy(diagnostic => diagnostic.Start).ThenByDescending(diagnostic => diagnostic.Severity).ToArray();

    private static void AnalyzeInto(string source, DiagnosticsOptions options, DiagnosticSink sink)
    {
        IReadOnlyList<ApiFunctionSignature> apiFunctions = options.ApiFunctions ?? Array.Empty<ApiFunctionSignature>();
        var commands = new HashSet<string>(StringComparer.Ordinal);
        foreach (ApiFunctionSignature function in apiFunctions)
        {
            if (function?.Name != null) commands.Add(function.Name.ToLowerInvariant());
        }
        if (new Gs2SyntaxChecker(source, commands, sink).Run()) return;
        new Heuristics(source, apiFunctions, options.ReportUnknownFunctions, sink).Run();
    }

    /// <summary>Comments become spaces, string bodies become spaces and their quotes '_'; line breaks are kept (C++ <c>maskNonCode</c>).</summary>
    internal static string MaskNonCode(string source)
    {
        const int Code = 0, LineComment = 1, BlockComment = 2, SingleQuote = 3, DoubleQuote = 4;
        char[] result = source.ToCharArray();
        int state = Code;
        bool escaped = false;
        for (int index = 0; index < result.Length; ++index)
        {
            char character = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (state == Code)
            {
                if (character == '/' && next == '/') { result[index] = result[index + 1] = ' '; ++index; state = LineComment; }
                else if (character == '/' && next == '*') { result[index] = result[index + 1] = ' '; ++index; state = BlockComment; }
                else if (character == '\'') { result[index] = '_'; state = SingleQuote; escaped = false; }
                else if (character == '"') { result[index] = '_'; state = DoubleQuote; escaped = false; }
                continue;
            }
            if (character != '\n' && character != '\r') result[index] = ' ';
            if (state == LineComment)
            {
                if (character == '\n') state = Code;
                continue;
            }
            if (state == BlockComment)
            {
                if (character == '*' && next == '/') { result[index + 1] = ' '; ++index; state = Code; }
                continue;
            }
            if (escaped) { escaped = false; continue; }
            if (character == '\\') { escaped = true; continue; }
            if ((state == SingleQuote && character == '\'') || (state == DoubleQuote && character == '"')) { result[index] = '_'; state = Code; }
        }
        return new string(result);
    }

    private readonly record struct Identifier(int Start, int End);

    private readonly record struct Call(string Name, int Start, int Open, int Close, int Arguments, bool Constructor);

    /// <summary>Phase 3 of the C++: heuristics on the masked text, rewritten where the C++ was quadratic.</summary>
    private sealed class Heuristics
    {
        private readonly string source;
        private readonly string masked;
        private readonly IReadOnlyList<ApiFunctionSignature> apiFunctions;
        private readonly bool reportUnknownFunctions;
        private readonly DiagnosticSink sink;
        private readonly List<Identifier> identifiers;
        private int[]? nextClose;
        private int[]? nextSingleAssignment;

        public Heuristics(string source, IReadOnlyList<ApiFunctionSignature> apiFunctions, bool reportUnknownFunctions, DiagnosticSink sink)
        {
            this.source = source;
            masked = MaskNonCode(source);
            this.apiFunctions = apiFunctions;
            this.reportUnknownFunctions = reportUnknownFunctions;
            this.sink = sink;
            identifiers = CollectIdentifiers(masked);
        }

        private static bool IdentifierStart(char character) => TextHelpers.IsAsciiLetter(character) || character == '_';

        private static bool IdentifierCharacter(char character) => IdentifierStart(character) || TextHelpers.IsDigit(character);

        private static List<Identifier> CollectIdentifiers(string text)
        {
            var result = new List<Identifier>();
            int position = 0;
            while (position < text.Length)
            {
                if (!IdentifierStart(text[position]) || (position > 0 && IdentifierCharacter(text[position - 1]))) { ++position; continue; }
                int start = position++;
                while (position < text.Length && IdentifierCharacter(text[position])) ++position;
                result.Add(new Identifier(start, position));
            }
            return result;
        }

        private void SkipWhitespace(ref int position)
        {
            while (position < masked.Length && TextHelpers.IsCSpace(masked[position])) ++position;
        }

        private bool ReadIdentifier(ref int position, out Identifier identifier)
        {
            SkipWhitespace(ref position);
            identifier = default;
            if (position >= masked.Length || !IdentifierStart(masked[position])) return false;
            int start = position++;
            while (position < masked.Length && IdentifierCharacter(masked[position])) ++position;
            identifier = new Identifier(start, position);
            return true;
        }

        private string TextOf(Identifier identifier) => masked.Substring(identifier.Start, identifier.End - identifier.Start);

        private bool IdentifierIs(Identifier identifier, string word) =>
            identifier.End - identifier.Start == word.Length && string.CompareOrdinal(masked, identifier.Start, word, 0, word.Length) == 0;

        /// <summary>Index of the first ')' at or after <paramref name="position"/>, or -1 (the C++ <c>find(')')</c>, precomputed).</summary>
        private int NextClose(int position)
        {
            if (nextClose == null)
            {
                nextClose = new int[masked.Length + 1];
                int next = -1;
                nextClose[masked.Length] = -1;
                for (int index = masked.Length - 1; index >= 0; --index)
                {
                    if (masked[index] == ')') next = index;
                    nextClose[index] = next;
                }
            }
            return position >= 0 && position <= masked.Length ? nextClose[position] : -1;
        }

        /// <summary>
        /// Index of the first single '=' (not part of ==, !=, &lt;=, &gt;=) at or after <paramref name="position"/>, or -1. The C++
        /// scanned each condition separately; its neighbour test does not depend on the condition bounds, so it can be precomputed.
        /// </summary>
        private int NextSingleAssignment(int position)
        {
            if (nextSingleAssignment == null)
            {
                nextSingleAssignment = new int[masked.Length + 1];
                int next = -1;
                nextSingleAssignment[masked.Length] = -1;
                for (int index = masked.Length - 1; index >= 0; --index)
                {
                    if (masked[index] == '=')
                    {
                        char previous = index == 0 ? '\0' : masked[index - 1];
                        char following = index + 1 < masked.Length ? masked[index + 1] : '\0';
                        if (previous is not ('=' or '!' or '<' or '>') && following != '=') next = index;
                    }
                    nextSingleAssignment[index] = next;
                }
            }
            return position >= 0 && position <= masked.Length ? nextSingleAssignment[position] : -1;
        }

        public void Run()
        {
            var declarations = new HashSet<string>(StringComparer.Ordinal);
            var variables = new HashSet<string>(PredefinedVariables, StringComparer.Ordinal);
            var parameterSpans = new List<(int Start, int End)>();

            foreach (Identifier identifier in identifiers)
            {
                if (!IdentifierIs(identifier, "function")) continue;
                int position = identifier.End;
                if (position >= masked.Length || !TextHelpers.IsCSpace(masked[position])) continue;
                if (!ReadIdentifier(ref position, out Identifier name)) continue;
                SkipWhitespace(ref position);
                if (position >= masked.Length || masked[position] != '(') continue;
                int close = NextClose(position + 1);
                if (close < 0) continue;
                declarations.Add(TextOf(name).ToLowerInvariant());
                parameterSpans.Add((position + 1, close));
            }
            AddParameterVariables(parameterSpans, variables);

            foreach (Identifier identifier in identifiers)
            {
                if (!IdentifierIs(identifier, "public")) continue;
                int position = identifier.End;
                if (position >= masked.Length || !TextHelpers.IsCSpace(masked[position])) continue;
                if (!ReadIdentifier(ref position, out Identifier name)) continue;
                SkipWhitespace(ref position);
                if (position >= masked.Length || masked[position] != '(') continue;
                if (IdentifierIs(name, "function")) continue;
                declarations.Add(TextOf(name).ToLowerInvariant());
            }

            foreach (Identifier identifier in identifiers)
            {
                if (!IdentifierIs(identifier, "const") && !IdentifierIs(identifier, "var") && !IdentifierIs(identifier, "temp")) continue;
                int position = identifier.End;
                if (position >= masked.Length || !TextHelpers.IsCSpace(masked[position])) continue;
                if (ReadIdentifier(ref position, out Identifier name)) variables.Add(TextOf(name).ToLowerInvariant());
            }

            foreach (Identifier identifier in identifiers)
            {
                if (!IdentifierIs(identifier, "if") && !IdentifierIs(identifier, "elseif")) continue;
                int position = identifier.End;
                SkipWhitespace(ref position);
                if (position >= masked.Length || masked[position] != '(') continue;
                int close = NextClose(position + 1);
                if (close < 0) continue;
                int assignment = NextSingleAssignment(position + 1);
                if (assignment >= 0 && assignment < close)
                {
                    sink.Add(DiagnosticSeverity.Warning, assignment, assignment + 1, "Assignment inside condition; use == if comparison was intended");
                }
            }

            var signatures = new Dictionary<string, (int Minimum, int Maximum)>(StringComparer.Ordinal);
            foreach ((string name, int minimum, int maximum) in BuiltInSignatures) signatures[name] = (minimum, maximum);
            foreach (ApiFunctionSignature function in apiFunctions)
            {
                if (function?.Name == null) continue;
                string name = function.Name.ToLowerInvariant();
                signatures[name] = (function.MinimumParameters, function.MaximumParameters);
                variables.Add(name);
            }

            foreach (Call call in CollectCalls())
            {
                string name = call.Name.ToLowerInvariant();
                if (name == "join") AddJoinInfo(call);
                bool hasSignature = signatures.TryGetValue(name, out (int Minimum, int Maximum) signature);
                if (!call.Constructor && reportUnknownFunctions && !KnownEvents.Contains(name) && hasSignature && signature.Minimum >= 0
                    && (call.Arguments < signature.Minimum || (signature.Maximum >= 0 && call.Arguments > signature.Maximum)))
                {
                    string expected = signature.Maximum < 0 ? signature.Minimum + "+"
                        : signature.Minimum == signature.Maximum ? signature.Minimum.ToString()
                        : signature.Minimum + "-" + signature.Maximum;
                    sink.Add(DiagnosticSeverity.Warning, call.Start, call.Start + call.Name.Length, call.Name + "() expects " + expected + " argument(s), got " + call.Arguments);
                }
                if (!call.Constructor && reportUnknownFunctions && !hasSignature && !KnownCalls.Contains(name) && !declarations.Contains(name) && !KnownEvents.Contains(name))
                {
                    sink.Add(DiagnosticSeverity.Warning, call.Start, call.Start + call.Name.Length, "Unknown function '" + call.Name + "'");
                }
            }

            foreach (Identifier identifier in identifiers)
            {
                int operatorPosition = identifier.End;
                SkipWhitespace(ref operatorPosition);
                if (AssignmentOperatorLength(operatorPosition) == 0) continue;
                int start = identifier.Start;
                if (start > 0 && (masked[start - 1] == '.' || masked[start - 1] == '#')) continue;
                string spelling = TextOf(identifier);
                if (!variables.Contains(spelling.ToLowerInvariant()) && !IsDeclarationPrefix(start))
                {
                    sink.Add(DiagnosticSeverity.Info, start, identifier.End, "Likely implicit variable '" + spelling + "'; declare it if local");
                }
            }

            for (int position = 0; position + 1 < masked.Length; ++position)
            {
                if (masked[position] != '#' || !TextHelpers.IsAsciiLetter(masked[position + 1])) continue;
                int open = position + 2;
                SkipWhitespace(ref open);
                if (open < masked.Length && masked[open] == '(') sink.Add(DiagnosticSeverity.Warning, position, open + 1, "Deprecated GS1 hash-string syntax");
            }
        }

        /// <summary>
        /// Every identifier inside the parameter lists of declared functions becomes a variable. The lists are merged first so that
        /// overlapping lists (nested declarations inside skipped GS1 commands) are walked once instead of once per declaration.
        /// </summary>
        private void AddParameterVariables(List<(int Start, int End)> spans, HashSet<string> variables)
        {
            if (spans.Count == 0) return;
            spans.Sort((left, right) => left.Start.CompareTo(right.Start));
            var merged = new List<(int Start, int End)>();
            foreach ((int start, int end) in spans)
            {
                if (merged.Count > 0 && start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
                else merged.Add((start, end));
            }
            int cursor = 0;
            foreach ((int start, int end) in merged)
            {
                cursor = LowerBound(cursor, start);
                for (int index = cursor; index < identifiers.Count && identifiers[index].Start < end; ++index) variables.Add(TextOf(identifiers[index]).ToLowerInvariant());
            }
        }

        private int LowerBound(int from, int start)
        {
            int low = from;
            int high = identifiers.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (identifiers[middle].Start < start) low = middle + 1; else high = middle;
            }
            return low;
        }

        private void AddJoinInfo(Call call)
        {
            string className = string.Empty;
            int position = call.Open + 1;
            int limit = call.Close;
            while (position < limit && TextHelpers.IsCSpace(source[position])) ++position;
            if (position < limit && (source[position] == '"' || source[position] == '\''))
            {
                char quote = source[position++];
                int start = position;
                while (position < limit && source[position] != quote && source[position] != '"' && source[position] != '\'') ++position;
                if (position < limit && source[position] == quote && position > start) className = " '" + source.Substring(start, position - start) + "'";
            }
            sink.Add(DiagnosticSeverity.Info, call.Start, call.Start + call.Name.Length, "join() imports members from class" + className + "; availability depends on the connected server");
        }

        private List<Call> CollectCalls()
        {
            int length = masked.Length;
            var matches = new Dictionary<int, int>();
            var opens = new Stack<int>();
            // Bracket balance before each position; a comma is at the top level of a call when the balance there equals the
            // balance just inside the call's '('. This counts arguments in O(log n) per call where the C++ rescanned every call.
            var balanceBefore = new int[length + 1];
            var commasByBalance = new Dictionary<int, List<int>>();
            int balance = 0;
            for (int index = 0; index < length; ++index)
            {
                balanceBefore[index] = balance;
                char character = masked[index];
                if (character == '(') opens.Push(index);
                else if (character == ')' && opens.Count > 0) matches[opens.Pop()] = index;
                if (character is '(' or '[' or '{') ++balance;
                else if (character is ')' or ']' or '}') --balance;
                else if (character == ',')
                {
                    if (!commasByBalance.TryGetValue(balanceBefore[index], out List<int>? positions)) commasByBalance[balanceBefore[index]] = positions = new List<int>();
                    positions.Add(index);
                }
            }
            balanceBefore[length] = balance;

            var calls = new List<Call>();
            foreach (Identifier identifier in identifiers)
            {
                int start = identifier.Start;
                if (start > 0 && masked[start - 1] == '.') continue;
                int open = identifier.End;
                SkipWhitespace(ref open);
                if (open >= length || masked[open] != '(') continue;
                if (!matches.TryGetValue(open, out int close)) continue;
                int previous = start;
                while (previous > 0 && TextHelpers.IsCSpace(masked[previous - 1])) --previous;
                int tokenEnd = previous;
                while (previous > 0 && IdentifierCharacter(masked[previous - 1])) --previous;
                bool constructor = tokenEnd - previous == 3 && string.Compare(masked, previous, "new", 0, 3, StringComparison.OrdinalIgnoreCase) == 0;
                calls.Add(new Call(TextOf(identifier), start, open, close, ArgumentCount(open, close, balanceBefore, commasByBalance), constructor));
            }
            return calls;
        }

        private int ArgumentCount(int open, int close, int[] balanceBefore, Dictionary<int, List<int>> commasByBalance)
        {
            int first = open + 1;
            while (first < close && TextHelpers.IsCSpace(masked[first])) ++first;
            if (first >= close) return 0;
            if (!commasByBalance.TryGetValue(balanceBefore[open] + 1, out List<int>? positions)) return 1;
            int from = UpperBound(positions, open);
            int to = LowerBoundValue(positions, close);
            return 1 + Math.Max(0, to - from);
        }

        private static int UpperBound(List<int> values, int value)
        {
            int low = 0, high = values.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (values[middle] <= value) low = middle + 1; else high = middle;
            }
            return low;
        }

        private static int LowerBoundValue(List<int> values, int value)
        {
            int low = 0, high = values.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (values[middle] < value) low = middle + 1; else high = middle;
            }
            return low;
        }

        private int AssignmentOperatorLength(int position)
        {
            if (position + 1 < masked.Length && masked[position] is '+' or '-' or '*' or '/' or '%' && masked[position + 1] == '=') return 2;
            if (position < masked.Length && masked[position] == '=' && (position + 1 >= masked.Length || masked[position + 1] != '=')) return 1;
            return 0;
        }

        /// <summary>Whether the word before <paramref name="start"/> on the same line is const, var or temp.</summary>
        private bool IsDeclarationPrefix(int start)
        {
            int end = start;
            while (end > 0 && masked[end - 1] != '\n' && TextHelpers.IsCSpace(masked[end - 1])) --end;
            int wordStart = end;
            while (wordStart > 0 && IdentifierCharacter(masked[wordStart - 1])) --wordStart;
            int length = end - wordStart;
            return (length == 5 && string.CompareOrdinal(masked, wordStart, "const", 0, 5) == 0)
                || (length == 3 && string.CompareOrdinal(masked, wordStart, "var", 0, 3) == 0)
                || (length == 4 && string.CompareOrdinal(masked, wordStart, "temp", 0, 4) == 0);
        }
    }
}
