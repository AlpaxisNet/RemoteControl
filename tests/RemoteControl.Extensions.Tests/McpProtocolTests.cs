using System.Text;

namespace RemoteControl.Extensions.Tests;

public sealed class McpProtocolTests
{
    // Port of tests/McpProtocolTests.cpp, every assertion in order.
    [Fact]
    public void CppMcpProtocolTestCases()
    {
        // The C++ test expected "\\\\" (two backslashes) after decoding the JSON escape \\, which is one backslash: the C++
        // expectation was wrong (the assert would have failed in a Debug build). The decoded value has one backslash.
        Assert.True(McpProtocol.TryGetStringField("{\"text\":\"\\u003c \\u003e \\u0026 \\\" \\\\ \\u00e9 \\ud83d\\ude00\"}", "text", out string value));
        Assert.Equal("< > & \" \\ é 😀", value);
        Assert.True(McpProtocol.TryGetStringField("{\"text\":\"literal < > & \\\" \\\\\\\\ Unicode é 😀\"}", "text", out value));
        Assert.Equal("literal < > & \" \\\\ Unicode é 😀", value);
        Assert.False(McpProtocol.TryGetStringField("{\"text\":\"\\ud83d broken\"}", "text", out _));
        Assert.True(McpProtocol.GetBoolField("{\"caseSensitive\":true}", "caseSensitive", false));
        Assert.Equal(12, McpProtocol.GetIntField("{\"start\":12}", "start", 1));
        const string text = "alpha\nBeta match\ngamma\nmatch delta\nomega\n";
        Assert.Equal("2: Beta match\n3: gamma\n4: match delta\n", McpProtocol.EditorLineRange(text, 2, 4));
        Assert.Equal("1: alpha\n2: Beta match\n3: gamma\n4: match delta\n5: omega\n", McpProtocol.EditorSearch(text, "MATCH", false, 1));
        Assert.Equal(string.Empty, McpProtocol.EditorSearch(text, "MATCH", true, 1));
        Assert.True(McpProtocol.TryGuardedReplace("before\nunique block\nafter", "unique block", "changed block", out string edited, out _));
        Assert.Equal("before\nchanged block\nafter", edited);
        Assert.False(McpProtocol.TryGuardedReplace("same same", "same", "changed", out _, out string error));
        Assert.Equal("Expected text is not unique in the current buffer", error);
        Assert.True(McpProtocol.InstanceScore(true, true, 1) > McpProtocol.InstanceScore(false, true, 5));
        Assert.True(McpProtocol.InstanceScore(false, true, 1) > McpProtocol.InstanceScore(false, true, 0));
        Assert.True(McpProtocol.InstanceScore(false, true, 0) > McpProtocol.InstanceScore(false, false, 0));
    }

    [Fact]
    public void FieldLookupsSearchEveryDepthAndValidate()
    {
        const string request = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"rc_read_script_lines\",\"arguments\":{\"editor\":\"Weapon: bow\",\"start\":-3,\"end\":9999999999,\"flag\":false}}}";
        Assert.True(McpProtocol.TryGetStringField(request, "editor", out string editor));
        Assert.Equal("Weapon: bow", editor);
        Assert.True(McpProtocol.TryGetStringField(request, "name", out string name));
        Assert.Equal("rc_read_script_lines", name);
        Assert.Equal(-3, McpProtocol.GetIntField(request, "start", 1));
        Assert.Equal(int.MaxValue, McpProtocol.GetIntField(request, "end", 1));
        Assert.Equal(5, McpProtocol.GetIntField(request, "missing", 5));
        Assert.Equal(5, McpProtocol.GetIntField("{\"start\":\"12\"}", "start", 5));
        Assert.False(McpProtocol.GetBoolField(request, "flag", true));
        Assert.True(McpProtocol.GetBoolField("{\"flag\":1}", "flag", true));
        Assert.False(McpProtocol.TryGetStringField("{\"text\":12}", "text", out _));
        Assert.False(McpProtocol.TryGetStringField("{\"text\":\"raw\u0001control\"}", "text", out _));
        Assert.False(McpProtocol.TryGetStringField("{\"text\":\"bad \\x escape\"}", "text", out _));
        Assert.False(McpProtocol.TryGetStringField("{\"text\":\"\\udc00 low first\"}", "text", out _));
        Assert.False(McpProtocol.TryGetStringField("{\"text\":\"unterminated", "text", out _));
        Assert.True(McpProtocol.TryGetStringField("{\"a\":\"text\",\"text\":\"value \\/ \\b\\f\\n\\r\\t\"}", "text", out string escapes));
        Assert.Equal("value / \b\f\n\r\t", escapes);
        Assert.True(McpProtocol.TryGetStringField("{\"te\\u0078t\":\"escaped key\"}", "text", out string escapedKey));
        Assert.Equal("escaped key", escapedKey);
    }

    [Fact]
    public void LineRangesAreBounded()
    {
        string text = string.Join("\n", Enumerable.Range(1, 500).Select(index => "line " + index));
        Assert.Equal("1: line 1\n", McpProtocol.EditorLineRange(text, 0, 1));
        Assert.Equal("3: line 3\n", McpProtocol.EditorLineRange(text, 3, 1));
        Assert.Equal(string.Empty, McpProtocol.EditorLineRange(text, 501, 600));
        Assert.Equal("500: line 500\n", McpProtocol.EditorLineRange(text, 500, 900));
        Assert.Equal(200, McpProtocol.EditorLineRange(text, 1, 500).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal("1: \n", McpProtocol.EditorLineRange(string.Empty, 1, 5));
        Assert.Equal("1: a\n2: b\n", McpProtocol.EditorLineRange("a\r\nb", 1, 5));
        string wide = string.Join("\n", Enumerable.Range(1, 100).Select(_ => new string('é', 1000)));
        string limited = McpProtocol.EditorLineRange(wide, 1, 100);
        Assert.True(Encoding.UTF8.GetByteCount(limited) <= McpProtocol.MaximumOutput);
        Assert.True(Encoding.UTF8.GetByteCount(limited) > McpProtocol.MaximumOutput - 4);
        Assert.DoesNotContain('�', limited);
    }

    [Fact]
    public void SearchesGroupAndLimit()
    {
        const string text = "hit one\nb\nc\nd\ne\nhit two\ng";
        Assert.Equal("1: hit one\n2: b\n--\n5: e\n6: hit two\n7: g\n", McpProtocol.EditorSearch(text, "hit", true, 1));
        Assert.Equal("1: hit one\n--\n6: hit two\n", McpProtocol.EditorSearch(text, "HIT", false, -5));
        Assert.Equal(string.Empty, McpProtocol.EditorSearch(text, string.Empty, false, 0));
        Assert.Equal(7, McpProtocol.EditorSearch(text, "hit", false, 100).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        string many = string.Join("\n", Enumerable.Range(1, 300).Select(index => "x" + index));
        Assert.Equal(200, McpProtocol.EditorSearch(many, "x", false, 0).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal("1: Élan\n", McpProtocol.EditorSearch("Élan", "élan", false, 0));
    }

    [Fact]
    public void GuardedReplaceErrors()
    {
        Assert.False(McpProtocol.TryGuardedReplace("abc", "", "x", out _, out string error));
        Assert.Equal("Expected text must not be empty", error);
        Assert.False(McpProtocol.TryGuardedReplace("abc", new string('a', 65537), "x", out _, out error));
        Assert.Equal("Partial edit is limited to 65536 bytes", error);
        Assert.False(McpProtocol.TryGuardedReplace("abc", "b", new string('é', 40000), out _, out error));
        Assert.Equal("Partial edit is limited to 65536 bytes", error);
        Assert.False(McpProtocol.TryGuardedReplace("abc", "z", "x", out _, out error));
        Assert.Equal("Expected text was not found in the current buffer", error);
        Assert.True(McpProtocol.TryGuardedReplace("aaa", "aa", "b", out string nonOverlapping, out _));
        Assert.Equal("ba", nonOverlapping);
        Assert.True(McpProtocol.TryGuardedReplace("abab", "aba", "x", out string overlap, out _));
        Assert.Equal("xb", overlap);
    }

    [Fact]
    public void ScriptTypesAndViews()
    {
        Assert.Equal("Weapon", McpProtocol.ScriptTypeOf("Weapon: bow"));
        Assert.Equal("Weapon", McpProtocol.ScriptTypeOf("Weapon/GUI Script: menu"));
        Assert.Equal("Class", McpProtocol.ScriptTypeOf("Class: util"));
        Assert.Equal("NPC", McpProtocol.ScriptTypeOf("NPC: door"));
        Assert.Equal("NPC", McpProtocol.ScriptTypeOf("Npcscript: door"));
        Assert.Equal(string.Empty, McpProtocol.ScriptTypeOf("Server Options"));
        Assert.Equal(11, McpProtocol.Views.Length);
        Assert.Contains("player_list", McpProtocol.Views);
    }

    [Fact]
    public void CommandLine()
    {
        McpCommandLine parsed = McpCommandLine.Parse(new[] { "--debug", "--mcp", "--mcp-instance=1234", "file" });
        Assert.True(parsed.McpMode);
        Assert.Equal(1234, parsed.Instance);
        Assert.Equal(new[] { "--debug", "file" }, parsed.Remaining);
        Assert.Equal(77, McpCommandLine.Parse(new[] { "--mcp-instance", "77" }).Instance);
        Assert.False(McpCommandLine.Parse(new[] { "--mcp-instance", "77" }).McpMode);
        Assert.Equal(0, McpCommandLine.Parse(new[] { "--mcp-instance=abc" }).Instance);
        Assert.Equal(12, McpCommandLine.Parse(new[] { "--mcp-instance=12x" }).Instance);
        Assert.Equal(new[] { "--mcp-instance" }, McpCommandLine.Parse(new[] { "--mcp-instance" }).Remaining);
    }
}
