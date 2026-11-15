using System.Diagnostics;
using RemoteControl.Scripting;

namespace RemoteControl.Scripting.Tests;

public sealed class Gs2DiagnosticsTests
{
    private static bool Has(IReadOnlyList<Diagnostic> diagnostics, DiagnosticSeverity severity, string text) =>
        diagnostics.Any(diagnostic => diagnostic.Severity == severity && diagnostic.Message.Contains(text, StringComparison.Ordinal));

    private static DiagnosticsOptions Api(params ApiFunctionSignature[] functions) => new() { ApiFunctions = functions };

    // ---- Ported from tests/GS2DiagnosticsTests.cpp ----

    [Fact]
    public void PortedMainProgram()
    {
        const string source = "// ignored badcall( {\n" +
            "function oncreated() {\n" +
            "  const value = \"ignored ) unknown()\";\n" +
            "  if (value = 3) {\n" +
            "    implicit = abs();\n" +
            "    mystery(1);\n" +
            "    join(\"Movement\");\n" +
            "    say(\"old\");\n" +
            "  }\n" +
            "}\n" +
            "]";
        IReadOnlyList<Diagnostic> diagnostics = Gs2Diagnostics.Analyze(source);
        // The C++ test expected "Unmatched closing"; the analyzer has never emitted that text. It reports the stray ']' as:
        Assert.True(Has(diagnostics, DiagnosticSeverity.Error, "Unexpected ']'"));
        Assert.False(Has(diagnostics, DiagnosticSeverity.Warning, "Event casing"));
        Assert.True(Has(diagnostics, DiagnosticSeverity.Warning, "Assignment inside condition"));
        Assert.True(Has(diagnostics, DiagnosticSeverity.Warning, "abs() expects 1"));
        Assert.True(Has(diagnostics, DiagnosticSeverity.Warning, "Unknown function 'mystery'"));
        // The C++ test expected a warning containing lower-case "deprecated"; the only such message is "Deprecated GS1
        // hash-string syntax", which needs '#x(' and the program has none, so no deprecation warning is the right result.
        Assert.False(Has(diagnostics, DiagnosticSeverity.Warning, "Deprecated"));
        Assert.True(Has(diagnostics, DiagnosticSeverity.Info, "implicit variable"));
        Assert.True(Has(diagnostics, DiagnosticSeverity.Info, "join() imports members from class 'Movement'"));
        Assert.False(Has(diagnostics, DiagnosticSeverity.Warning, "badcall"));
        Assert.False(Has(diagnostics, DiagnosticSeverity.Warning, "unknown"));

        Diagnostic bracket = Assert.Single(diagnostics, diagnostic => diagnostic.Message == "Unexpected ']'");
        Assert.Equal(source.Length - 1, bracket.Start);
        Assert.Equal(1, bracket.Length);
        Diagnostic assignment = Assert.Single(diagnostics, diagnostic => diagnostic.Message.StartsWith("Assignment inside", StringComparison.Ordinal));
        Assert.Equal(source.IndexOf("value = 3", StringComparison.Ordinal) + 6, assignment.Start);
    }

    [Fact]
    public void UnknownFunctionWithoutApi() =>
        Assert.True(Has(Gs2Diagnostics.Analyze("triggerClient(\"weapon\", \"name\");"), DiagnosticSeverity.Warning, "Unknown function 'triggerClient'"));

    [Fact]
    public void ApiNamesMatchWithoutCase() =>
        Assert.Empty(Gs2Diagnostics.Analyze("TRIGGERCLIENT(\"weapon\", \"name\");", Api(new ApiFunctionSignature("triggerClient", 2, 2))));

    [Fact]
    public void PutNpc2TakesThreeArguments() =>
        Assert.False(Has(Gs2Diagnostics.Analyze("putnpc2(1.5, 2.5, \"script\");"), DiagnosticSeverity.Warning, "putnpc2() expects"));

    [Fact]
    public void ApiArityIsChecked()
    {
        IReadOnlyList<Diagnostic> diagnostics = Gs2Diagnostics.Analyze("customCall(1, 2); customCall(1);", Api(new ApiFunctionSignature("customCall", 2, 2)));
        Assert.False(Has(diagnostics, DiagnosticSeverity.Warning, "Unknown function 'customCall'"));
        Assert.True(Has(diagnostics, DiagnosticSeverity.Warning, "customCall() expects 2 argument(s), got 1"));
    }

    [Fact]
    public void OptionalAndVariadicParameters()
    {
        ApiFunctionSignature optional = ApiFunctionSignature.FromParameters("waitfor", new[] { "object", "event[", "timeout]" });
        Assert.Equal(2, optional.MinimumParameters);
        Assert.Equal(3, optional.MaximumParameters);
        ApiFunctionSignature variadic = ApiFunctionSignature.FromParameters("trigger", new[] { "str", "params..." });
        Assert.Equal(1, variadic.MinimumParameters);
        Assert.Equal(-1, variadic.MaximumParameters);
        IReadOnlyList<Diagnostic> diagnostics = Gs2Diagnostics.Analyze("waitfor(object, event); waitfor(object, event, 3); trigger(\"event\", 1, 2, 3);", Api(optional, variadic));
        Assert.False(Has(diagnostics, DiagnosticSeverity.Warning, "expects"));
    }

    [Fact]
    public void ConstructorsAreNotArityChecked()
    {
        IReadOnlyList<Diagnostic> scroll = Gs2Diagnostics.Analyze("new GuiScrollCtrl(\"GUI Explorer\");", Api(ApiFunctionSignature.FromParameters("GuiScrollCtrl", new[] { "profile" })));
        Assert.False(Has(scroll, DiagnosticSeverity.Warning, "GuiScrollCtrl() expects"));
        IReadOnlyList<Diagnostic> profile = Gs2Diagnostics.Analyze("new GuiControlProfile(\"Classic_BackProfile\") { modal = false; }");
        Assert.False(Has(profile, DiagnosticSeverity.Warning, "Unknown function 'new'"));
        Assert.False(Has(profile, DiagnosticSeverity.Warning, "GuiControlProfile() expects"));
        Assert.DoesNotContain(profile, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ApiNamesAreKnownVariables()
    {
        IReadOnlyList<Diagnostic> diagnostics = Gs2Diagnostics.Analyze("new GuiMLTextCtrl(\"StaffConsole\");\nVisible = false;",
            Api(ApiFunctionSignature.FromParameters("GuiMLTextCtrl", new[] { "name" }), new ApiFunctionSignature("Visible", -1, -1)));
        Assert.False(Has(diagnostics, DiagnosticSeverity.Warning, "GuiMLTextCtrl() expects"));
        Assert.False(Has(diagnostics, DiagnosticSeverity.Info, "Likely implicit variable 'Visible'"));
    }

    [Fact]
    public void BuiltInArities()
    {
        IReadOnlyList<Diagnostic> builtins = Gs2Diagnostics.Analyze("getAngle(x, y); waitfor(object, event); random(1, 2);");
        Assert.False(Has(builtins, DiagnosticSeverity.Warning, "getAngle() expects"));
        Assert.False(Has(builtins, DiagnosticSeverity.Warning, "waitfor() expects"));
        Assert.False(Has(builtins, DiagnosticSeverity.Warning, "random() expects"));
        IReadOnlyList<Diagnostic> arrays = Gs2Diagnostics.Analyze("setarray(values, index); format(\"%s\", value, extra);");
        Assert.False(Has(arrays, DiagnosticSeverity.Warning, "setarray() expects"));
        Assert.False(Has(arrays, DiagnosticSeverity.Warning, "format() expects"));
    }

    [Fact]
    public void NamesMatchWithoutCase()
    {
        IReadOnlyList<Diagnostic> diagnostics = Gs2Diagnostics.Analyze("function OnActionServerside() { ABS(1); LocalHelper(); }\nfunction localhelper() {}");
        Assert.False(Has(diagnostics, DiagnosticSeverity.Warning, "Event casing"));
        Assert.False(Has(diagnostics, DiagnosticSeverity.Warning, "ABS"));
        Assert.False(Has(diagnostics, DiagnosticSeverity.Warning, "LocalHelper"));
    }

    [Fact]
    public void EventsAreNotArityChecked()
    {
        IReadOnlyList<Diagnostic> diagnostics = Gs2Diagnostics.Analyze("function onActionServerSide() { switch (params[0]) {} }",
            Api(ApiFunctionSignature.FromParameters("onActionServerSide", new[] { "action" })));
        Assert.False(Has(diagnostics, DiagnosticSeverity.Warning, "onActionServerSide() expects"));
    }

    // ---- Every rule: a script that triggers it and a corrected one that does not ----

    public static TheoryData<string, string, string> Rules() => new()
    {
        { "x = \"abc", "x = \"abc\";", "Unterminated string" },
        { "x = `;", "x = 1;", "Unexpected character '`'" },
        { "}", "{ }", "Unexpected '}'" },
        { "function 1() {}", "function a() {}", "Expected a function name" },
        { "function a {}", "function a() {}", "Expected '(' after the function name" },
        { "function a() {", "function a() {}", "Unclosed '{'" },
        { "if x {}", "if (x) {}", "Expected '(' after 'if'" },
        { "while x {}", "while (x) {}", "Expected '(' after 'while'" },
        { "with x {}", "with (x) {}", "Expected '(' after 'with'" },
        { "switch x {}", "switch (x) {}", "Expected '(' after 'switch'" },
        { "if (x {}", "if (x) {}", "Expected ')'" },
        { "if (x)", "if (x) y;", "Expected a statement" },
        { "do {} while (x)", "do {} while (x);", "Missing ';' after do-while" },
        { "do {} x;", "do {} while (x);", "Expected 'while' after the do body" },
        { "goto 1;", "goto here;", "Expected a label after 'goto'" },
        { "goto here x", "goto here;", "Missing ';' after goto" },
        { "return 1 2", "return 1;", "Missing ';' after return" },
        { "break 1", "break;", "Missing ';' after break" },
        { "continue 1", "continue;", "Missing ';' after continue" },
        { "x = 1 y = 2;", "x = 1; y = 2;", "Missing ';'" },
        { "else x;", "if (a) b; else x;", "'else' without a matching 'if'" },
        { "elseif (x) y;", "if (a) b; elseif (x) y;", "'elseif' without a matching 'if'" },
        { "case 1: x;", "switch (a) { case 1: x; }", "'case' outside of a switch" },
        { "default: x;", "switch (a) { default: x; }", "'default' outside of a switch" },
        { "const 1 = 2;", "const a = 2;", "Expected a constant name" },
        { "const a 2;", "const a = 2;", "Expected '=' in the constant declaration" },
        { "enum E (A)", "enum E { A }", "Expected '{' to start the enum" },
        { "enum E { 1 }", "enum E { A = 1 }", "Expected an enum member name" },
        { "enum E { A B }", "enum E { A, B }", "Expected '}' to close the enum" },
        { "for x", "for (;;) x;", "Expected '(' after 'for'" },
        { "for (i = 0 i < 3; i++) {}", "for (i = 0; i < 3; i++) {}", "Expected ';' in the for loop" },
        { "switch (x) case", "switch (x) { }", "Expected '{' to start the switch" },
        { "switch (x) { case 1 x; }", "switch (x) { case 1: x; }", "Expected ':' after the case value" },
        { "switch (x) { default x; }", "switch (x) { default: x; }", "Expected ':' after 'default'" },
        { "switch (x) { case 1: y;", "switch (x) { case 1: y; }", "Unclosed switch" },
        { "new Foo;", "new Foo();", "Expected '(' after the class name" },
        { "x = a[1;", "x = a[1];", "Expected ']'" },
        { "x = {1, 2;", "x = {1, 2};", "Expected '}'" },
        { "x = a ? b;", "x = a ? b : c;", "Expected ':' in the conditional expression" },
        { "if (x in |1, 2) {}", "if (x in |1, 2|) {}", "Expected '|' or '>' to close the range" },
        { "x = a.;", "x = a.b;", "Expected a member name after '.'" },
        { "x = while;", "while (x) y;", "Unexpected 'while'" },
        { "x = );", "x = (1);", "Unexpected ')'" },
        { "x =", "x = 1;", "Expected an expression" },
        { "x = new 5;", "x = new Foo(5);", "Expected a class name after 'new'" },
        { "if (value = 3) {}", "if (value == 3) {}", "Assignment inside condition; use == if comparison was intended" },
        { "join(\"Movement\");", "echo(\"join\");", "join() imports members from class 'Movement'; availability depends on the connected server" },
        { "abs(1, 2);", "abs(1);", "abs() expects 1 argument(s), got 2" },
        { "waitfor(a);", "waitfor(a, b);", "waitfor() expects 2-3 argument(s), got 1" },
        { "format();", "format(\"%s\");", "format() expects 1+ argument(s), got 0" },
        { "mystery();", "function mystery() {} mystery();", "Unknown function 'mystery'" },
        { "foo = 1;", "temp foo = 1;", "Likely implicit variable 'foo'; declare it if local" },
        { "echo(#s (1));", "echo(\"#s(1)\");", "Deprecated GS1 hash-string syntax" },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void RuleIsReported(string positive, string negative, string message)
    {
        IReadOnlyList<Diagnostic> found = Gs2Diagnostics.Analyze(positive);
        Assert.Contains(found, diagnostic => diagnostic.Message == message);
        IReadOnlyList<Diagnostic> corrected = Gs2Diagnostics.Analyze(negative);
        Assert.DoesNotContain(corrected, diagnostic => diagnostic.Message == message);
    }

    [Fact]
    public void RuleSeverities()
    {
        Assert.Equal(DiagnosticSeverity.Error, Gs2Diagnostics.Analyze("x = \"abc").Single(diagnostic => diagnostic.Message == "Unterminated string").Severity);
        Assert.Equal(DiagnosticSeverity.Warning, Gs2Diagnostics.Analyze("mystery();").Single().Severity);
        Assert.Equal(DiagnosticSeverity.Info, Gs2Diagnostics.Analyze("foo = 1;").Single().Severity);
        Assert.Equal(DiagnosticSeverity.Info, Gs2Diagnostics.Analyze("join(\"A\");").Single().Severity);
        Assert.Equal(DiagnosticSeverity.Warning, Gs2Diagnostics.Analyze("abs(1, 2);").Single().Severity);
        Assert.Equal(DiagnosticSeverity.Warning, Gs2Diagnostics.Analyze("echo(#s (1));").Single(diagnostic => diagnostic.Message.StartsWith("Deprecated", StringComparison.Ordinal)).Severity);
    }

    [Fact]
    public void NestingLimit()
    {
        string deep = "x = " + new string('(', 200) + "1" + new string(')', 200) + ";";
        IReadOnlyList<Diagnostic> diagnostics = Gs2Diagnostics.Analyze(deep);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message == "Nested too deeply to analyze" && diagnostic.Severity == DiagnosticSeverity.Error);
        string shallow = "echo(" + new string('(', 20) + "1" + new string(')', 20) + ");";
        Assert.Empty(Gs2Diagnostics.Analyze(shallow));
    }

    [Fact]
    public void AbortSkipsHeuristics()
    {
        string deep = "mystery(); x = " + new string('(', 200);
        IReadOnlyList<Diagnostic> diagnostics = Gs2Diagnostics.Analyze(deep);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Message.StartsWith("Unknown function", StringComparison.Ordinal));
    }

    [Fact]
    public void AtMostSixtySyntaxErrors()
    {
        string source = string.Concat(Enumerable.Repeat("x = );\n", 200));
        IReadOnlyList<Diagnostic> diagnostics = Gs2Diagnostics.Analyze(source);
        Assert.Equal(60, diagnostics.Count(diagnostic => diagnostic.Message == "Unexpected ')'"));
    }

    [Fact]
    public void UnknownCommandOnlyWithApiList()
    {
        Assert.Contains(Gs2Diagnostics.Analyze("foo 1, 2;", Api(new ApiFunctionSignature("bar", 1, 1))),
            diagnostic => diagnostic.Message == "Unknown command 'foo'" && diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Empty(Gs2Diagnostics.Analyze("bar 1, 2;", Api(new ApiFunctionSignature("BAR", 1, 1))));
        Assert.Empty(Gs2Diagnostics.Analyze("foo 1, 2;"));
    }

    [Fact]
    public void LegacyCommandsAndTextAreSkipped()
    {
        Assert.Empty(Gs2Diagnostics.Analyze("setplayerprop #c, hello world;"));
        Assert.Empty(Gs2Diagnostics.Analyze("if (playerchats) toweapons Bomb;"));
        Assert.DoesNotContain(Gs2Diagnostics.Analyze("message(hello there);"), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ReportUnknownFunctionsOffSilencesArityAndUnknown()
    {
        var options = new DiagnosticsOptions { ReportUnknownFunctions = false };
        Assert.Empty(Gs2Diagnostics.Analyze("mystery(); abs(1, 2);", options));
    }

    [Fact]
    public void GrammarFeaturesParse()
    {
        const string script = "//#CLIENTSIDE\n" +
            "enum Colors { Red, Green = 2, Blue };\n" +
            "public function helper.sub(a, b) { return a + b; }\n" +
            "function onCreated() {\n" +
            "  temp.f = function(x) { return x * 2; };\n" +
            "  for (i : this.items) { echo(i); }\n" +
            "  for (;;) { break; }\n" +
            "  with (findplayer(\"x\")) { chat = \"hi\" @ NL @ 1; }\n" +
            "  temp.arr = new [3][4];\n" +
            "  temp.o = new (\"Gui\" @ \"Control\")(\"n\");\n" +
            "  new GuiWindowCtrl(\"Test\") { profile = \"X\"; };\n" +
            "  temp.list = {1, 2, {3, 4}};\n" +
            "  temp.v = this.(\"dyn\" @ 1);\n" +
            "  if (temp.x in |1, 5|) label: echo(1);\n" +
            "  switch (temp.x) { case 1: case 2: echo(2); break; default: echo(3); }\n" +
            "  do { temp.x++; } while (temp.x < 10);\n" +
            "  temp.y = temp.x == 1 ? 2 : temp.x > 3 ? 4 : 5;\n" +
            "  temp.z = !temp.y && (temp.x || temp.y) xor 1;\n" +
            "  temp.s = \"a\" SPC \"b\" NL \"c\";\n" +
            "  temp.y <<= 2; temp.y >>= 1; temp.y := 3; temp.y @= \"a\";\n" +
            "  goto done;\n" +
            "  done: return;\n" +
            "}\n";
        Assert.DoesNotContain(Gs2Diagnostics.Analyze(script), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MessageCodesAreTokens()
    {
        Assert.DoesNotContain(Gs2Diagnostics.Analyze("x = #c; y = #P12; z = #s(name(1)); w = #U2; v = #C3;"), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void SortedByStartThenSeverity()
    {
        IReadOnlyList<Diagnostic> diagnostics = Gs2Diagnostics.Analyze("x = `; mystery(); foo = 1; }");
        for (int index = 1; index < diagnostics.Count; ++index)
        {
            Diagnostic previous = diagnostics[index - 1];
            Diagnostic current = diagnostics[index];
            Assert.True(previous.Start < current.Start || (previous.Start == current.Start && previous.Severity >= current.Severity));
        }
    }

    [Fact]
    public void OffsetsAreUtf16Indices()
    {
        // é is one UTF-16 unit but two UTF-8 bytes: the C++ reported byte offsets here (start inside the character).
        const string accented = "x = café y;";
        Diagnostic missing = Gs2Diagnostics.Analyze(accented).Single(diagnostic => diagnostic.Message == "Missing ';'");
        Assert.Equal(accented.IndexOf('é'), missing.Start);
        Assert.Equal(1, missing.Length);

        string astral = "x = \U0001D49C\U0001D49C y;";
        Diagnostic astralMissing = Gs2Diagnostics.Analyze(astral).Single(diagnostic => diagnostic.Message == "Missing ';'");
        Assert.Equal(6, astralMissing.Start);
        Assert.Equal(2, astralMissing.Length);

        string unclosed = "f(\U0001D49C";
        Diagnostic expected = Gs2Diagnostics.Analyze(unclosed).Single(diagnostic => diagnostic.Message == "Expected ')'");
        Assert.Equal(2, expected.Start);
        Assert.Equal(2, expected.Length);

        const string identifier = "naïve = 1;";
        Assert.Contains(Gs2Diagnostics.Analyze(identifier), diagnostic => diagnostic.Message == "Likely implicit variable 've'; declare it if local" && diagnostic.Start == 3);
    }

    [Fact]
    public void LargeContentIsNotAnalyzed()
    {
        string large = string.Concat(Enumerable.Repeat("mystery();\n", EditorLimits.LargeContentBytes / 11 + 1));
        Assert.True(EditorLimits.IsLargeContent(large));
        Assert.Empty(Gs2Diagnostics.Analyze(large));
        Assert.NotEmpty(Gs2Diagnostics.Analyze(large.Substring(0, 1100), new DiagnosticsOptions { AnalyzeLargeContent = true }));
        string multiByte = new string('é', EditorLimits.LargeContentBytes / 2);
        Assert.True(EditorLimits.IsLargeContent(multiByte));
        Assert.False(EditorLimits.IsLargeContent(new string('a', 1000)));
    }

    [Fact]
    public void OptionsForDefinitions()
    {
        ApiDefinitionSet builtIns = ApiDefinitionSet.Create(Array.Empty<ApiDefinition>());
        DiagnosticsOptions loading = DiagnosticsOptions.ForDefinitions(builtIns, completionEnabled: true);
        Assert.False(loading.ReportUnknownFunctions);
        Assert.NotEmpty(loading.ApiFunctions);
        DiagnosticsOptions loaded = DiagnosticsOptions.ForDefinitions(ApiDefinitionSet.Create(new[] { new ApiDefinition("echo", "function", new[] { "text" }) }), true);
        Assert.True(loaded.ReportUnknownFunctions);
        Assert.Contains(loaded.ApiFunctions, function => function.Name == "echo" && function.MinimumParameters == 1);
        DiagnosticsOptions off = DiagnosticsOptions.ForDefinitions(builtIns, completionEnabled: false);
        Assert.True(off.ReportUnknownFunctions);
        Assert.Empty(off.ApiFunctions);
    }

    [Fact]
    public void HoverTextFormat()
    {
        Assert.Equal("Script analysis — Error: Missing ';'", new Diagnostic(DiagnosticSeverity.Error, 0, 1, "Missing ';'").HoverText);
        Assert.Equal("Script analysis — Warning: w", new Diagnostic(DiagnosticSeverity.Warning, 0, 1, "w").HoverText);
        Assert.Equal("Script analysis — Info: i", new Diagnostic(DiagnosticSeverity.Info, 0, 1, "i").HoverText);
    }

    // ---- Hostile input: bounded time, no exception, ranges inside the text ----

    public static TheoryData<string> HostileInputs()
    {
        const int size = 1_000_000;
        static string Repeat(string piece, int length) => string.Concat(Enumerable.Repeat(piece, length / piece.Length + 1)).Substring(0, length);
        return new TheoryData<string>
        {
            Repeat("(", size),
            Repeat("{", size),
            Repeat("[", size),
            Repeat("f(", size),
            "foo " + Repeat("f(", size - 100) + new string(')', 50) + ";",
            "foo " + Repeat("if(", size),
            "foo " + Repeat("function a(", size) + ")",
            "foo " + Repeat("join(", size),
            Repeat("\\\"\n", size),
            Repeat("\\'\n", size),
            Repeat("#a(", size),
            Repeat("a=", size),
            Repeat("a = b\n", size),
            Repeat("x = )", size),
            Repeat("if (", size),
            Repeat("\"", size),
            "/*" + Repeat("x", size),
            "\"" + Repeat("x", size),
            "'" + Repeat("x\n", size),
            Repeat("\0", size),
            Repeat("\U0001D49C(", size),
            Repeat("\uD800", 1000) + Repeat("\uDC00x(", 1000),
            Repeat("!", size),
            Repeat("a ? ", size),
            Repeat("-", size),
            Repeat("x in |", size),
            Repeat("new ", size),
            Repeat("else ", size),
            Repeat("a.", size),
            Repeat("do ", size),
            Repeat("if (a) ", size),
            Repeat("function(", size),
        };
    }

    [Theory]
    [MemberData(nameof(HostileInputs))]
    public void HostileInputIsSafe(string input)
    {
        var watch = Stopwatch.StartNew();
        IReadOnlyList<Diagnostic> diagnostics = Gs2Diagnostics.Analyze(input);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "took " + watch.Elapsed);
        foreach (Diagnostic diagnostic in diagnostics)
        {
            Assert.InRange(diagnostic.Start, 0, input.Length - 1);
            Assert.InRange(diagnostic.End, diagnostic.Start + 1, input.Length);
            Assert.False(char.IsLowSurrogate(input[diagnostic.Start]) && diagnostic.Start > 0 && char.IsHighSurrogate(input[diagnostic.Start - 1]));
        }
    }

    [Fact]
    public void DeepRecursionOnASmallStack()
    {
        string input = "x = " + new string('(', 170) + new string('!', 170);
        IReadOnlyList<Diagnostic>? result = null;
        var thread = new Thread(() => result = Gs2Diagnostics.Analyze(input), 256 * 1024);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.NotNull(result);
        Assert.Contains(result!, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void EmptyAndNullInput()
    {
        Assert.Empty(Gs2Diagnostics.Analyze(null));
        Assert.Empty(Gs2Diagnostics.Analyze(string.Empty));
        Assert.Empty(Gs2Diagnostics.Analyze("   \n\t"));
    }

    [Fact]
    public void ControlCharacterMessage()
    {
        Assert.Contains(Gs2Diagnostics.Analyze("x = \0;"), diagnostic => diagnostic.Message == "Unexpected character 'U+0000'");
    }
}
