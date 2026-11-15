using System.Runtime.CompilerServices;

namespace RemoteControl.Scripting;

/// <summary>
/// The recursive-descent syntax check of the C++ <c>GS2SyntaxChecker</c>, message for message. Recursion is bounded by the
/// nesting limit (180 levels) and by a stack probe, error recovery always makes progress, and at most 60 errors are reported.
/// </summary>
internal sealed class Gs2SyntaxChecker
{
    private const int MaximumErrors = 60;
    private const int MaximumDepth = 180;

    private static readonly HashSet<string> AssignmentOperators = new(StringComparer.Ordinal)
    {
        "=", ":=", "+=", "-=", "*=", "/=", "^=", "%=", "@=", "|=", "&=", "<<=", ">>=",
    };

    private readonly DiagnosticSink diagnostics;
    private readonly HashSet<string> commands;
    private readonly List<Gs2Token> tokens;
    private int index;
    private int depth;
    private int errors;
    private int rangeDepth;
    private bool aborted;

    public Gs2SyntaxChecker(string source, HashSet<string> commands, DiagnosticSink diagnostics)
    {
        this.diagnostics = diagnostics;
        this.commands = commands;
        tokens = Gs2Tokenizer.Tokenize(source, diagnostics);
    }

    /// <summary>Checks the whole script; true when the check was aborted (too many errors or too deep).</summary>
    public bool Run()
    {
        while (!AtEnd() && !aborted)
        {
            int before = index;
            if (IsOperator("}")) { Error(Peek(), "Unexpected '}'"); ++index; continue; }
            ParseBodyStatement();
            if (index == before) ++index;
        }
        return aborted;
    }

    private Gs2Token Peek(int offset = 0) => tokens[Math.Min(index + offset, tokens.Count - 1)];

    private Gs2Token Previous() => tokens[index == 0 ? 0 : Math.Min(index, tokens.Count) - 1];

    private bool AtEnd() => Peek().Kind == Gs2TokenKind.End;

    private bool IsOperator(string text, int offset = 0)
    {
        Gs2Token token = Peek(offset);
        return token.Kind == Gs2TokenKind.Operator && token.Text == text;
    }

    private bool IsKeyword(string text, int offset = 0)
    {
        Gs2Token token = Peek(offset);
        return token.Kind == Gs2TokenKind.Keyword && token.Text == text;
    }

    private bool AcceptOperator(string text)
    {
        if (!IsOperator(text)) return false;
        ++index;
        return true;
    }

    private static bool IsValueToken(Gs2Token token) => token.Kind is Gs2TokenKind.Identifier or Gs2TokenKind.Number or Gs2TokenKind.String or Gs2TokenKind.Message;

    private void Error(Gs2Token token, string message)
    {
        if (aborted) return;
        if (++errors > MaximumErrors) { aborted = true; return; }
        bool end = token.Kind == Gs2TokenKind.End;
        int start = end ? (token.Start == 0 ? 0 : token.Start - 1) : token.Start;
        diagnostics.Add(DiagnosticSeverity.Error, start, Math.Max(start + 1, end ? token.Start : token.End), message);
    }

    private void ErrorAfterPrevious(string message)
    {
        Gs2Token token = Previous();
        int end = token.End;
        int start = end > token.Start ? end - 1 : end;
        if (aborted) return;
        if (++errors > MaximumErrors) { aborted = true; return; }
        diagnostics.Add(DiagnosticSeverity.Error, start, Math.Max(start + 1, end), message);
    }

    private bool ExpectOperator(string text, string message)
    {
        if (AcceptOperator(text)) return true;
        if (IsOperator("}") || AtEnd()) ErrorAfterPrevious(message); else Error(Peek(), message);
        return false;
    }

    private bool Enter()
    {
        if (++depth > MaximumDepth || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            Error(Peek(), "Nested too deeply to analyze");
            aborted = true;
            return false;
        }
        return true;
    }

    private void Leave() => --depth;

    private void Synchronize()
    {
        while (!AtEnd() && !aborted)
        {
            if (IsOperator(";")) { ++index; return; }
            if (IsOperator("}")) return;
            Gs2Token token = Peek();
            if (token.Kind == Gs2TokenKind.Keyword && token.Text is "function" or "public" or "if" or "for" or "while" or "do" or "switch" or "return" or "with" or "const" or "enum") return;
            ++index;
        }
    }

    private void ParseBodyStatement()
    {
        if (IsKeyword("function") && !IsOperator("(", 1)) { ParseFunctionDeclaration(); return; }
        if (IsKeyword("public") && IsKeyword("function", 1)) { ParseFunctionDeclaration(); return; }
        ParseStatement();
    }

    private void ParseFunctionDeclaration()
    {
        if (IsKeyword("public")) ++index;
        ++index;
        if (Peek().Kind != Gs2TokenKind.Identifier) { Error(Peek(), "Expected a function name"); Synchronize(); return; }
        ++index;
        while (IsOperator(".") && Peek(1).Kind == Gs2TokenKind.Identifier) index += 2;
        if (!ExpectOperator("(", "Expected '(' after the function name")) { Synchronize(); return; }
        if (!ParseArguments(")")) { Synchronize(); return; }
        if (IsKeyword("function") || IsKeyword("public") || IsOperator("}") || AtEnd()) return;
        ParseStatement();
    }

    private bool ParseBlock()
    {
        Gs2Token opener = Peek();
        ++index;
        if (!Enter()) return false;
        while (!AtEnd() && !IsOperator("}") && !aborted)
        {
            int before = index;
            ParseBodyStatement();
            if (index == before) ++index;
        }
        Leave();
        if (IsOperator("}")) { ++index; return true; }
        Error(opener, "Unclosed '{'");
        return false;
    }

    private bool ParseCondition(string what)
    {
        if (!ExpectOperator("(", "Expected '(' after '" + what + "'")) return false;
        if (!ParseExpression()) return false;
        return ExpectOperator(")", "Expected ')'");
    }

    private void ParseControlBody()
    {
        if (IsOperator("}") || AtEnd()) { ErrorAfterPrevious("Expected a statement"); return; }
        ParseStatement();
    }

    private void ParseIfTail()
    {
        if (!ParseCondition("if")) { Synchronize(); return; }
        ParseControlBody();
        if (IsKeyword("else")) { ++index; ParseControlBody(); }
        else if (IsKeyword("elseif"))
        {
            ++index;
            if (!Enter()) return;
            ParseIfTail();
            Leave();
        }
    }

    private void ParseStatement()
    {
        if (aborted || !Enter()) return;
        ParseStatementInner();
        Leave();
    }

    private void ParseStatementInner()
    {
        Gs2Token token = Peek();
        if (IsOperator(";")) { ++index; return; }
        if (IsOperator("{")) { ParseBlock(); return; }
        if (token.Kind == Gs2TokenKind.Keyword)
        {
            string word = token.Text;
            switch (word)
            {
                case "if": ++index; ParseIfTail(); return;
                case "while":
                case "with":
                    ++index;
                    if (!ParseCondition(word)) { Synchronize(); return; }
                    ParseControlBody();
                    return;
                case "do":
                    ++index;
                    ParseControlBody();
                    if (IsKeyword("while"))
                    {
                        ++index;
                        if (ParseCondition("while")) ExpectOperator(";", "Missing ';' after do-while"); else Synchronize();
                    }
                    else { Error(Peek(), "Expected 'while' after the do body"); Synchronize(); }
                    return;
                case "for": ParseFor(); return;
                case "switch": ParseSwitch(); return;
                case "return":
                    ++index;
                    if (!IsOperator(";") && !IsOperator("}") && !AtEnd())
                    {
                        if (!ParseExpression()) { Synchronize(); return; }
                    }
                    EndOfJump("return");
                    return;
                case "break":
                case "continue":
                    ++index;
                    if (Peek().Kind == Gs2TokenKind.Identifier) ++index;
                    EndOfJump(word);
                    return;
                case "goto":
                    ++index;
                    if (Peek().Kind != Gs2TokenKind.Identifier) { Error(Peek(), "Expected a label after 'goto'"); Synchronize(); return; }
                    ++index;
                    ExpectOperator(";", "Missing ';' after goto");
                    return;
                case "const": ParseConst(); return;
                case "enum": ParseEnum(); return;
                case "new" when Peek(1).Kind == Gs2TokenKind.Identifier || IsOperator("(", 1): ParseNewStatement(); return;
                case "else":
                case "elseif":
                    Error(token, "'" + word + "' without a matching 'if'");
                    ++index;
                    return;
                case "case":
                case "default":
                    Error(token, "'" + word + "' outside of a switch");
                    ++index;
                    Synchronize();
                    return;
            }
        }
        if (token.Kind == Gs2TokenKind.Identifier && IsOperator(":", 1) && !IsOperator(":", 2))
        {
            index += 2;
            if (!IsOperator("}") && !AtEnd()) ParseStatement();
            return;
        }
        if (token.Kind == Gs2TokenKind.Identifier && IsLegacyCommand()) { SkipLegacyCommand(); return; }
        ParseExpressionStatement();
    }

    private void EndOfJump(string what)
    {
        if (AcceptOperator(";")) return;
        if (IsOperator("}")) return;
        ErrorAfterPrevious("Missing ';' after " + what);
        Synchronize();
    }

    private void ParseExpressionStatement()
    {
        if (!ParseExpression()) { Synchronize(); return; }
        while (IsOperator(","))
        {
            ++index;
            if (!ParseExpression()) { Synchronize(); return; }
        }
        if (AcceptOperator(";")) return;
        if (IsOperator("}") || AtEnd()) return;
        ErrorAfterPrevious("Missing ';'");
        Synchronize();
    }

    /// <summary>A GS1 command: an identifier followed by a value, a keyword (other than else/elseif/xor/in), ',' or '#'.</summary>
    private bool IsLegacyCommand()
    {
        Gs2Token next = Peek(1);
        return IsValueToken(next)
            || (next.Kind == Gs2TokenKind.Operator && next.Text is "," or "#")
            || (next.Kind == Gs2TokenKind.Keyword && next.Text is not ("else" or "elseif" or "xor" or "in"));
    }

    private void SkipLegacyCommand()
    {
        Gs2Token command = Peek();
        if (commands.Count > 0 && !commands.Contains(command.Text.ToLowerInvariant()))
        {
            if (++errors > MaximumErrors) { aborted = true; return; }
            diagnostics.Add(DiagnosticSeverity.Warning, command.Start, command.End, "Unknown command '" + command.Text + "'");
        }
        int braces = 0;
        while (!AtEnd())
        {
            if (IsOperator("{")) ++braces;
            else if (IsOperator("}")) { if (braces == 0) return; --braces; }
            else if (IsOperator(";") && braces == 0) { ++index; return; }
            ++index;
        }
    }

    private void ParseConst()
    {
        ++index;
        if (Peek().Kind != Gs2TokenKind.Identifier) { Error(Peek(), "Expected a constant name"); Synchronize(); return; }
        ++index;
        if (!ExpectOperator("=", "Expected '=' in the constant declaration") || !ParseExpression()) { Synchronize(); return; }
        AcceptOperator(";");
    }

    private void ParseEnum()
    {
        ++index;
        if (Peek().Kind == Gs2TokenKind.Identifier) ++index;
        if (!ExpectOperator("{", "Expected '{' to start the enum")) { Synchronize(); return; }
        while (!AtEnd() && !IsOperator("}") && !aborted)
        {
            if (Peek().Kind != Gs2TokenKind.Identifier) { Error(Peek(), "Expected an enum member name"); Synchronize(); return; }
            ++index;
            if (AcceptOperator("=") && !ParseExpression()) { Synchronize(); return; }
            if (!AcceptOperator(",")) break;
        }
        if (!ExpectOperator("}", "Expected '}' to close the enum")) { Synchronize(); return; }
        AcceptOperator(";");
    }

    private void ParseFor()
    {
        ++index;
        if (!ExpectOperator("(", "Expected '(' after 'for'")) { Synchronize(); return; }
        if (!IsOperator(";") && !ParseExpression()) { Synchronize(); return; }
        if (AcceptOperator(":"))
        {
            if (!ParseExpression() || !ExpectOperator(")", "Expected ')'")) { Synchronize(); return; }
            ParseControlBody();
            return;
        }
        if (!ExpectOperator(";", "Expected ';' in the for loop")) { Synchronize(); return; }
        if (!IsOperator(";") && !ParseExpression()) { Synchronize(); return; }
        if (!ExpectOperator(";", "Expected ';' in the for loop")) { Synchronize(); return; }
        if (!IsOperator(")"))
        {
            if (!ParseExpression()) { Synchronize(); return; }
            AcceptOperator(";");
        }
        if (!ExpectOperator(")", "Expected ')'")) { Synchronize(); return; }
        ParseControlBody();
    }

    private void ParseSwitch()
    {
        ++index;
        if (!ParseCondition("switch")) { Synchronize(); return; }
        Gs2Token opener = Peek();
        if (!ExpectOperator("{", "Expected '{' to start the switch")) { Synchronize(); return; }
        while (!AtEnd() && !IsOperator("}") && !aborted)
        {
            int before = index;
            if (IsKeyword("case"))
            {
                ++index;
                if (!ParseExpression() || !ExpectOperator(":", "Expected ':' after the case value")) Synchronize();
            }
            else if (IsKeyword("default"))
            {
                ++index;
                if (!ExpectOperator(":", "Expected ':' after 'default'")) Synchronize();
            }
            else ParseBodyStatement();
            if (index == before) ++index;
        }
        if (!AcceptOperator("}")) Error(opener, "Unclosed switch");
    }

    private void ParseNewStatement()
    {
        ++index;
        if (AcceptOperator("("))
        {
            if (!ParseExpression() || !ExpectOperator(")", "Expected ')'")) { Synchronize(); return; }
        }
        else ++index;
        if (!ExpectOperator("(", "Expected '(' after the class name")) { Synchronize(); return; }
        if (!ParseArguments(")")) { Synchronize(); return; }
        if (IsOperator("{")) { ParseBlock(); return; }
        if (IsOperator(".") || IsOperator("[") || IsOperator("("))
        {
            while (!AtEnd() && !IsOperator(";") && !IsOperator("}")) ++index;
        }
        AcceptOperator(";");
    }

    private bool ParseArguments(string close)
    {
        if (AcceptOperator(close)) return true;
        int open = index == 0 ? 0 : index - 1;
        if (LooksLikeLegacyText(open)) { SkipToMatching(open); return true; }
        if (!ParseExpression()) return false;
        while (AcceptOperator(","))
        {
            if (IsOperator(close)) break;
            if (!ParseExpression()) return false;
        }
        return ExpectOperator(close, "Expected '" + close + "'");
    }

    /// <summary>GS1 text in an argument list: two adjacent values, "(," or ",,", or ".#x".</summary>
    private bool LooksLikeLegacyText(int open)
    {
        if (open >= tokens.Count || tokens[open].Text != "(") return false;
        int nesting = 0;
        Gs2Token before = tokens[open];
        for (int cursor = open + 1; cursor < tokens.Count; ++cursor)
        {
            Gs2Token token = tokens[cursor];
            if (token.Kind == Gs2TokenKind.End || (token.Kind == Gs2TokenKind.Operator && token.Text is ";" or "{" or "}")) return false;
            if (token.Kind == Gs2TokenKind.Operator && token.Text == "(") ++nesting;
            else if (token.Kind == Gs2TokenKind.Operator && token.Text == ")")
            {
                if (nesting == 0) return false;
                --nesting;
            }
            else if (token.Kind == Gs2TokenKind.Operator && token.Text == "," && before.Kind == Gs2TokenKind.Operator && before.Text is "(" or ",") return true;
            else if (IsValueToken(token) && IsValueToken(before)) return true;
            else if (token.Kind == Gs2TokenKind.Message && before.Kind == Gs2TokenKind.Operator && before.Text == ".") return true;
            before = token;
        }
        return false;
    }

    private void SkipToMatching(int open)
    {
        int nesting = 0;
        for (int cursor = open; cursor < tokens.Count; ++cursor)
        {
            Gs2Token token = tokens[cursor];
            if (token.Kind != Gs2TokenKind.Operator) continue;
            if (token.Text == "(") ++nesting;
            else if (token.Text == ")" && --nesting == 0) { index = cursor + 1; return; }
        }
    }

    private bool ParseExpression()
    {
        if (aborted || !Enter()) return false;
        bool ok = ParseAssignment();
        Leave();
        return ok;
    }

    private bool ParseAssignment()
    {
        if (!ParseConditional()) return false;
        Gs2Token token = Peek();
        if (token.Kind == Gs2TokenKind.Operator && AssignmentOperators.Contains(token.Text)) { ++index; return ParseExpression(); }
        return true;
    }

    private bool ParseConditional()
    {
        if (!ParseBinary(1)) return false;
        if (!IsOperator("?")) return true;
        ++index;
        if (!ParseExpression()) return false;
        if (!ExpectOperator(":", "Expected ':' in the conditional expression")) return false;
        // The C++ recursed here; the loop is equivalent (a chain of conditionals) without growing the stack.
        while (true)
        {
            if (!ParseBinary(1)) return false;
            if (!IsOperator("?")) return true;
            ++index;
            if (!ParseExpression()) return false;
            if (!ExpectOperator(":", "Expected ':' in the conditional expression")) return false;
        }
    }

    private static int LevelOf(Gs2Token token)
    {
        if (token.Kind == Gs2TokenKind.Keyword) return token.Text == "xor" ? 3 : token.Text == "in" ? 4 : 0;
        if (token.Kind != Gs2TokenKind.Operator) return 0;
        return token.Text switch
        {
            "||" => 1,
            "&&" => 2,
            "&" or "|" or "<<" or ">>" => 3,
            "==" or "!=" or "<>" => 4,
            "<" or "<=" or "=<" or ">" or ">=" or "=>" => 5,
            "@" => 6,
            "+" or "-" => 7,
            "*" or "/" or "%" or "^" => 8,
            _ => 0,
        };
    }

    private bool ParseBinary(int minimum)
    {
        if (!ParsePrefix()) return false;
        while (true)
        {
            Gs2Token token = Peek();
            int level = LevelOf(token);
            if (level == 0 || level < minimum) return true;
            if (rangeDepth > 0 && token.Kind == Gs2TokenKind.Operator && token.Text == "|") return true;
            ++index;
            if (token.Kind == Gs2TokenKind.Keyword && token.Text == "in")
            {
                if (!ParseRange()) return false;
                continue;
            }
            if (!ParseBinary(level + 1)) return false;
        }
    }

    private bool ParseRange()
    {
        if (IsOperator("|") || IsOperator("<"))
        {
            ++index;
            ++rangeDepth;
            bool ok = ParseBinary(1) && (!AcceptOperator(",") || ParseBinary(1));
            --rangeDepth;
            if (!ok) return false;
            if (IsOperator("|") || IsOperator(">")) { ++index; return true; }
            Error(Peek(), "Expected '|' or '>' to close the range");
            return false;
        }
        return ParseBinary(5);
    }

    private bool ParsePrefix()
    {
        if (aborted || !Enter()) return false;
        bool ok;
        Gs2Token token = Peek();
        if (token.Kind == Gs2TokenKind.Operator && token.Text is "++" or "--" or "!" or "-" or "+" or "@" or "~") { ++index; ok = ParsePrefix(); }
        else ok = ParsePostfix();
        Leave();
        return ok;
    }

    private bool ParsePostfix()
    {
        if (!ParsePrimary()) return false;
        while (true)
        {
            if (IsOperator("("))
            {
                ++index;
                if (!ParseArguments(")")) return false;
            }
            else if (IsOperator("."))
            {
                ++index;
                if (AcceptOperator("("))
                {
                    if (!ParseExpression() || !ExpectOperator(")", "Expected ')'")) return false;
                }
                else if (Peek().Kind is Gs2TokenKind.Identifier or Gs2TokenKind.Keyword or Gs2TokenKind.Number or Gs2TokenKind.Message) ++index;
                else { Error(Peek(), "Expected a member name after '.'"); return false; }
            }
            else if (IsOperator("["))
            {
                ++index;
                if (!ParseArguments("]")) return false;
            }
            else if (IsOperator("++") || IsOperator("--")) ++index;
            else return true;
        }
    }

    private bool ParsePrimary()
    {
        Gs2Token token = Peek();
        switch (token.Kind)
        {
            case Gs2TokenKind.Number:
            case Gs2TokenKind.String:
            case Gs2TokenKind.Message:
            case Gs2TokenKind.Identifier:
                ++index;
                return true;
            case Gs2TokenKind.Keyword:
            {
                string word = token.Text;
                if (word is "true" or "false" or "null" or "NULL") { ++index; return true; }
                if (word is "int" or "float" && IsOperator("(", 1)) { ++index; return true; }
                if (word == "function" && IsOperator("(", 1))
                {
                    index += 2;
                    if (!ParseArguments(")")) return false;
                    ParseControlBody();
                    return true;
                }
                if (word == "new") return ParseNewExpression();
                if (word is "in" or "xor") { ++index; return true; }
                Error(token, "Unexpected '" + word + "'");
                return false;
            }
            case Gs2TokenKind.Operator:
                if (token.Text == "(")
                {
                    ++index;
                    if (!ParseExpression()) return false;
                    return ExpectOperator(")", "Expected ')'");
                }
                if (token.Text == "{") { ++index; return ParseArguments("}"); }
                if (token.Text == "#") { ++index; return true; }
                Error(token, "Unexpected '" + token.Text + "'");
                return false;
            default:
                ErrorAfterPrevious("Expected an expression");
                return false;
        }
    }

    private bool ParseNewExpression()
    {
        ++index;
        if (IsOperator("["))
        {
            while (AcceptOperator("["))
            {
                if (!ParseExpression() || !ExpectOperator("]", "Expected ']'")) return false;
            }
            return true;
        }
        if (AcceptOperator("("))
        {
            if (!ParseExpression() || !ExpectOperator(")", "Expected ')'")) return false;
        }
        else if (Peek().Kind == Gs2TokenKind.Identifier) ++index;
        else { Error(Peek(), "Expected a class name after 'new'"); return false; }
        if (!ExpectOperator("(", "Expected '(' after the class name")) return false;
        return ParseArguments(")");
    }
}
