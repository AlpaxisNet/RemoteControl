using System.Diagnostics;
using RemoteControl.Scripting;

namespace RemoteControl.Scripting.Tests;

public sealed class Gs2FormatterTests
{
    private const string PortedSource = "//#CLIENTSIDE\nfunction test() {   \n// remove\nif (value) {\necho(\"// keep\"); /* remove */\n}\n}\n";

    [Fact]
    public void PortedCase()
    {
        // tests/EditorFormatTests.cpp expected "echo(\"// keep\"); \n" with a trailing space although trimming is on. The C++
        // formatter trims that space (TEditorFormat.h:92), so the test was wrong; the trimmed result is the intended one.
        const string expected = "//#CLIENTSIDE\nfunction test() {\n\n  if (value) {\n    echo(\"// keep\");\n  }\n}\n";
        Assert.Equal(expected, Gs2Formatter.Format(PortedSource, FormatterOptions.Default));
    }

    [Fact]
    public void PortedCaseKeepsCommentsWhenAsked()
    {
        var options = FormatterOptions.Default with { RemoveLineComments = false, RemoveBlockComments = false };
        string formatted = Gs2Formatter.Format(PortedSource, options);
        Assert.Contains("// remove", formatted, StringComparison.Ordinal);
        Assert.Contains("/* remove */", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void TabsAndWidth()
    {
        const string source = "function a() {\nif (x) {\ny();\n}\n}";
        Assert.Equal("function a() {\n\tif (x) {\n\t\ty();\n\t}\n}", Gs2Formatter.Format(source, FormatterOptions.Default with { UseTabs = true }));
        Assert.Equal("function a() {\n    if (x) {\n        y();\n    }\n}", Gs2Formatter.Format(source, FormatterOptions.Default with { IndentWidth = 4 }));
        Assert.Equal("function a() {\n if (x) {\n  y();\n }\n}", Gs2Formatter.Format(source, FormatterOptions.Default with { IndentWidth = 0 }));
        Assert.Equal(new string(' ', 16) + "y();", Gs2Formatter.Format("{\ny();\n}", FormatterOptions.Default with { IndentWidth = 99 }).Split('\n')[1]);
    }

    [Fact]
    public void BlankLinesAreLeftAlone()
    {
        Assert.Equal("{\n   \n\t\n}", Gs2Formatter.Format("{\n   \n\t\n}", FormatterOptions.Default));
    }

    [Fact]
    public void TrimmingCanBeOff()
    {
        Assert.Equal("{\n  x();  \n}", Gs2Formatter.Format("{\nx();  \n}", FormatterOptions.Default with { TrimTrailingWhitespace = false }));
    }

    [Fact]
    public void ClosingBracesOutdentAndDepthNeverNegative()
    {
        Assert.Equal("}}\nx;\n{\n} else {\n  y;\n}", Gs2Formatter.Format("}}\nx;\n{\n} else {\ny;\n}", FormatterOptions.Default));
    }

    [Fact]
    public void BracesInStringsAndCommentsDoNotCount()
    {
        const string source = "a = \"{\";\nb = '{';\nc; // {\n/* { */ d;\n/*\n{\n*/\ne;";
        var keep = FormatterOptions.Default with { RemoveLineComments = false, RemoveBlockComments = false };
        Assert.Equal(source, Gs2Formatter.Format(source, keep));
    }

    [Fact]
    public void ClientsideDirective()
    {
        Assert.Equal("  //#CLIENTSIDE  \nx;", Gs2Formatter.RemoveComments("  //#CLIENTSIDE  \nx;", FormatterOptions.Default));
        Assert.Equal("x; \ny;", Gs2Formatter.RemoveComments("x; //#CLIENTSIDE\ny;", FormatterOptions.Default));
        Assert.Equal("\ny;", Gs2Formatter.RemoveComments("//#CLIENTSIDE\ny;", FormatterOptions.Default with { PreserveClientside = false }));
        Assert.Equal("\ny;", Gs2Formatter.RemoveComments("//#CLIENTSIDE x\ny;", FormatterOptions.Default));
    }

    [Fact]
    public void BlockCommentsKeepLineBreaks()
    {
        Assert.Equal("a \n\n b", Gs2Formatter.RemoveComments("a /* x\ny\nz */ b", FormatterOptions.Default));
        Assert.Equal("a /* x\ny */ b", Gs2Formatter.RemoveComments("a /* x\ny */ b", FormatterOptions.Default with { RemoveBlockComments = false }));
    }

    [Fact]
    public void QuotesProtectCommentMarkers()
    {
        Assert.Equal("a = \"//x\"; b = '/*'; c = `//`;", Gs2Formatter.RemoveComments("a = \"//x\"; b = '/*'; c = `//`;", FormatterOptions.Default));
        Assert.Equal("a = \"\\\"//\";", Gs2Formatter.RemoveComments("a = \"\\\"//\";", FormatterOptions.Default));
    }

    [Fact]
    public void CrLfLineEndingsArePreserved()
    {
        Assert.Equal("{\r\n  x;\r\n\r\n}\r\n", Gs2Formatter.Format("{\r\nx;   \r\n// c\r\n}\r\n", FormatterOptions.Default));
        Assert.Equal("a /*\r\n*/", Gs2Formatter.RemoveComments("a /*\r\n*/", FormatterOptions.Default with { RemoveBlockComments = false }));
        Assert.Equal("a \r\nb", Gs2Formatter.RemoveComments("a /* x\r\n */b", FormatterOptions.Default));
    }

    public static TheoryData<string> RealisticScripts() => new()
    {
        "//#CLIENTSIDE\nfunction onCreated() {\n  this.x = 1; // count\n  if (this.x > 0) {\n    echo(\"positive {\");\n  } else {\n    echo('neg }');\n  }\n}\n",
        "function onActionServerSide() {\n    switch (params[0]) {\n        case \"warp\":\n            player.setlevel2(params[1], params[2], params[3]);\n            break;\n        default:\n            /* nothing\n               here */\n            break;\n    }\n}\n//#CLIENTSIDE\nfunction onWeaponFired() { triggerserver(\"gui\", name, \"warp\", \"onlinestartlocal.nw\", 30, 30); }\n",
        "\tfunction a() {\n\t\tfor (temp.i = 0; temp.i < 10; temp.i++) { if (temp.i % 2 == 0) { echo(temp.i); } }\n\t}\n\n\n   \nfunction b() { return { 1, 2, { 3 } }; }",
        "new GuiWindowCtrl(\"Test\") {\nprofile = GuiBlueWindowProfile;\nnew GuiButtonCtrl(\"Ok\") {\ntext = \"}\";\n}\n}\n",
        "if (playerenters) {\nsetplayerprop #c, hello; // gs1\n}\nmessage unterminated 'quote\n{\n}\n",
        "{\r\n  x();   \r\n\t// tail\r\n}\r\n",
        "a = \"multi\nline { string\";\n{\nb;\n}\n",
        "/* unterminated block {\n{\n",
    };

    [Theory]
    [MemberData(nameof(RealisticScripts))]
    public void FormattingIsIdempotent(string script)
    {
        foreach (FormatterOptions options in new[]
        {
            FormatterOptions.Default,
            FormatterOptions.Default with { UseTabs = true },
            FormatterOptions.Default with { IndentWidth = 4, TrimTrailingWhitespace = false },
            FormatterOptions.Default with { RemoveLineComments = false, RemoveBlockComments = false },
            FormatterOptions.Default with { PreserveClientside = false },
        })
        {
            string once = Gs2Formatter.Format(script, options);
            Assert.Equal(once, Gs2Formatter.Format(once, options));
        }
    }

    public static TheoryData<string> HostileInputs()
    {
        const int size = 1_000_000;
        static string Repeat(string piece, int length) => string.Concat(Enumerable.Repeat(piece, length / piece.Length + 1)).Substring(0, length);
        return new TheoryData<string>
        {
            Repeat("(", size),
            Repeat("{", size),
            Repeat("{\n", size),
            Repeat("}\n", size),
            Repeat("\"", size),
            "/*" + Repeat("x\n", size),
            "'" + Repeat("x\n", size),
            Repeat("//\n", size),
            Repeat("\0", size),
            Repeat("\U0001D49C{\n", size),
            Repeat("\r", size),
            Repeat("\\", size),
        };
    }

    [Theory]
    [MemberData(nameof(HostileInputs))]
    public void HostileInputIsSafe(string input)
    {
        var watch = Stopwatch.StartNew();
        string once = Gs2Formatter.Format(input, FormatterOptions.Default with { IndentWidth = 1 });
        string twice = Gs2Formatter.Format(once, FormatterOptions.Default with { IndentWidth = 1 });
        watch.Stop();
        Assert.Equal(once, twice);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "took " + watch.Elapsed);
    }

    [Fact]
    public void NullAndEmpty()
    {
        Assert.Equal(string.Empty, Gs2Formatter.Format(null));
        Assert.Equal(string.Empty, Gs2Formatter.Format(string.Empty));
        Assert.Equal("\n", Gs2Formatter.Format("\n"));
    }
}
