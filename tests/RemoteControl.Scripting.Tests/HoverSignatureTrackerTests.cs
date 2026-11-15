using RemoteControl.Scripting;

namespace RemoteControl.Scripting.Tests;

public sealed class HoverAndSignatureTests
{
    private static readonly ApiDefinitionSet Api = ApiDefinitionSet.Create(new[]
    {
        new ApiDefinition("setTimer", "function", new[] { "seconds" }, "void", "script", "Starts the timer.", "setTimer(1);"),
        new ApiDefinition("putnpc2", "function", new[] { "x", "y", "script" }),
        new ApiDefinition("format", "function", new[] { "pattern", "values..." }, "string"),
        new ApiDefinition("chat", "property"),
        new ApiDefinition("bare"),
    });

    [Fact]
    public void HoverShowsDefinition()
    {
        const string text = "  setTimer(1);";
        HoverContent? hover = HoverInfo.At(text, 5, null, Api);
        Assert.NotNull(hover);
        Assert.Equal(2, hover!.WordStart);
        Assert.Equal(8, hover.WordLength);
        Assert.Equal(new[] { new HoverBlock("setTimer(seconds)", true), new HoverBlock("function  SCRIPT\nParameters: seconds\nReturns: void\nStarts the timer.\n\nExample:\nsetTimer(1);", false) }, hover.Blocks);
        Assert.Equal("setTimer(seconds)\nfunction  SCRIPT\nParameters: seconds\nReturns: void\nStarts the timer.\n\nExample:\nsetTimer(1);", hover.ToPlainText());
    }

    [Fact]
    public void HoverShowsDiagnosticsFirst()
    {
        const string text = "setTimer(1) x";
        IReadOnlyList<Diagnostic> diagnostics = Gs2Diagnostics.Analyze(text);
        Diagnostic missing = Assert.Single(diagnostics, diagnostic => diagnostic.Message == "Missing ';'");
        HoverContent? hover = HoverInfo.At(text, missing.Start, diagnostics, Api);
        Assert.NotNull(hover);
        Assert.Equal("Script analysis — Error: Missing ';'", hover!.Blocks[0].Text);
        Assert.True(hover.Blocks[0].Bold);
        Assert.Single(hover.Blocks);
        HoverContent? onEnd = HoverInfo.At(text, missing.End, diagnostics, Api);
        Assert.NotNull(onEnd);
        Assert.Contains("Missing ';'", onEnd!.Blocks[0].Text, StringComparison.Ordinal);
        HoverContent? both = HoverInfo.At("abs(1, 2);", 1, Gs2Diagnostics.Analyze("abs(1, 2);"), ApiDefinitionSet.Create(new[] { new ApiDefinition("abs", "function", new[] { "value" }) }));
        Assert.Equal(3, both!.Blocks.Count);
        Assert.StartsWith("Script analysis — Warning: abs() expects 1", both.Blocks[0].Text, StringComparison.Ordinal);
        Assert.Equal("abs(value)", both.Blocks[1].Text);
        Assert.StartsWith("Script analysis — Warning", both.ToPlainText(), StringComparison.Ordinal);
        Assert.Contains("\n\nabs(value)\n", both.ToPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public void HoverUsesLocalFunctionsAndIgnoresUnknownWords()
    {
        const string text = "function myHelper(a) {}\nmyHelper(1);";
        LocalSymbols symbols = LocalSymbols.Extract(text);
        HoverContent? hover = HoverInfo.At(text, text.LastIndexOf("Helper", StringComparison.Ordinal), null, Api, symbols.Functions);
        Assert.Equal("myHelper(a)", hover!.Blocks[0].Text);
        Assert.Null(HoverInfo.At(text, 1, null, Api));
        Assert.Null(HoverInfo.At(text, text.IndexOf('\n'), null, Api));
        Assert.Null(HoverInfo.At(text, -1, null, Api));
        Assert.Null(HoverInfo.At(text, text.Length, null, Api));
        Assert.Null(HoverInfo.At(null, 0, null, null));
    }

    [Fact]
    public void HoverWordRangeHandlesSurrogates()
    {
        const string text = "x \U0001D49Cy z";
        Assert.True(HoverInfo.WordRange(text, 3, out int start, out int end));
        Assert.Equal(2, start);
        Assert.Equal(5, end);
        Assert.True(HoverInfo.WordRange(text, 1, out start, out end));
        Assert.Equal(1, start);
        Assert.Equal(2, end);
    }

    [Fact]
    public void SignatureInSimpleCall()
    {
        const string text = "putnpc2(1, 2";
        SignatureHelpResult? help = SignatureHelp.At(text, text.Length, Api);
        Assert.NotNull(help);
        Assert.Equal("putnpc2", help!.CallName);
        Assert.Equal(7, help.OpenOffset);
        Assert.Equal(1, help.ArgumentIndex);
        Assert.Equal(1, help.ActiveParameter);
        Assert.Equal("putnpc2(x, y, script)  function\nx, y, script", help.ToPlainText());
        SignaturePart active = Assert.Single(help.Parts, part => part.Underline);
        Assert.Equal("y", active.Text);
        Assert.True(active.Bold);
        Assert.Contains(help.Parts, part => part.Italic && part.Text == "function");
    }

    [Fact]
    public void SignatureInNestedCalls()
    {
        const string text = "setTimer(putnpc2(a, f(b, c), ";
        SignatureHelpResult? help = SignatureHelp.At(text, text.Length, Api);
        Assert.Equal("putnpc2", help!.CallName);
        Assert.Equal(2, help.ArgumentIndex);
        Assert.Equal("script", help.Parts.Single(part => part.Underline).Text);
        const string closed = "setTimer(putnpc2(a, b), ";
        SignatureHelpResult? outer = SignatureHelp.At(closed, closed.Length, Api);
        Assert.Equal("setTimer", outer!.CallName);
        Assert.Equal(1, outer.ArgumentIndex);
        Assert.Equal(0, outer.ActiveParameter);
        Assert.Equal("setTimer(seconds)  function  SCRIPT\nseconds\nReturns: void\nStarts the timer.\n\nExample:\nsetTimer(1);", outer.ToPlainText());
    }

    [Fact]
    public void SignatureIgnoresStringsAndComments()
    {
        const string text = "putnpc2(\"(,\", ')', /* ( , */ x";
        SignatureHelpResult? help = SignatureHelp.At(text, text.Length, Api);
        Assert.Equal("putnpc2", help!.CallName);
        Assert.Equal(2, help.ArgumentIndex);
        Assert.Null(SignatureHelp.At("x = 1; // putnpc2(", 18, Api));
        Assert.Null(SignatureHelp.At("x = \"putnpc2(", 13, Api));
        Assert.Null(SignatureHelp.At("putnpc2(1)", 10, Api));
        SignatureHelpResult? inString = SignatureHelp.At("putnpc2(1, \"a, b", 16, Api);
        Assert.Equal(1, inString!.ArgumentIndex);
    }

    [Fact]
    public void SignatureUsesTheCurrentLineAndDottedNames()
    {
        Assert.Null(SignatureHelp.At("putnpc2(1,\n2", 12, Api));
        SignatureHelpResult? member = SignatureHelp.At("player.chat (", 13, ApiDefinitionSet.Create(new[] { new ApiDefinition("chat", "function", new[] { "text" }) }));
        Assert.Equal("player.chat", member!.CallName);
        Assert.Equal(0, member.ActiveParameter);
        Assert.Null(SignatureHelp.At("bare(", 5, Api));
        Assert.Null(SignatureHelp.At("unknown(", 8, Api));
        Assert.Null(SignatureHelp.At("(1, ", 4, Api));
        SignatureHelpResult? variadic = SignatureHelp.At("format(\"%s %s\", a, b, c", 23, Api);
        Assert.Equal(3, variadic!.ArgumentIndex);
        Assert.Equal(1, variadic.ActiveParameter);
    }

    [Fact]
    public void SignatureOnHostileText()
    {
        string text = new string('(', 200_000) + "putnpc2(" + new string('"', 3);
        Assert.Equal("putnpc2", SignatureHelp.At(text, text.Length, Api)!.CallName);
        string deep = "putnpc2(" + new string('[', 200_000) + new string(')', 200_000) + "(";
        Assert.Null(SignatureHelp.At(deep, deep.Length, Api));
        string brackets = "putnpc2(" + new string('[', 200_000) + new string(']', 200_000) + ", ";
        Assert.Equal(1, SignatureHelp.At(brackets, brackets.Length, Api)!.ArgumentIndex);
    }
}

public sealed class ScriptEditorTrackerTests
{
    [Fact]
    public void CleanOnlyAfterServerConfirmation()
    {
        var tracker = new ScriptEditorTracker();
        var connection = new object();
        TrackedEditor editor = tracker.Track("Weapon: -System (Server)", "a\nb\n", connection);
        Assert.False(editor.IsDirty);
        editor.UpdateText("a\nB\n");
        Assert.True(editor.IsDirty);
        PendingUpload? upload = editor.BeginUpload();
        Assert.NotNull(upload);
        Assert.Equal("a\nB\n", upload!.Text);
        Assert.Same(connection, upload.Connection);
        Assert.True(editor.IsDirty);
        editor.UpdateText("a\nB\nc\n");
        Assert.True(editor.ConfirmUpload(upload));
        Assert.True(editor.IsDirty);
        Assert.Equal("a\nB\n", editor.ConfirmedText);
        editor.UpdateText("a\nB\n");
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void DetachedEditorsCannotBeMarkedSaved()
    {
        // Review defect D-01: the C++ marked the editor saved although the connection was gone and nothing was sent.
        var tracker = new ScriptEditorTracker();
        var connection = new object();
        TrackedEditor editor = tracker.Track("Class: test", "x", connection);
        editor.UpdateText("y");
        tracker.DetachConnection(connection);
        Assert.Null(editor.Connection);
        Assert.Null(editor.BeginUpload());
        Assert.True(editor.IsDirty);
        Assert.Single(tracker.DirtyEditorsFor(connection));
        var replacement = new object();
        tracker.RebindConnection(connection, replacement);
        Assert.Same(replacement, editor.Connection);
        PendingUpload? upload = editor.BeginUpload();
        Assert.NotNull(upload);
        Assert.Same(replacement, upload!.Connection);
        Assert.True(editor.ConfirmUpload(upload));
        Assert.False(editor.IsDirty);
        Assert.Null(tracker.Track("NPC", "", null).BeginUpload());
    }

    [Fact]
    public void DirtyEditorsAreListedForClosingAndQuitting()
    {
        var tracker = new ScriptEditorTracker();
        var first = new object();
        var second = new object();
        TrackedEditor b = tracker.Track("b", "1", first);
        TrackedEditor a = tracker.Track("a", "1", second);
        tracker.Track("c", "1", first);
        var changes = new List<string>();
        tracker.DirtyChanged += (_, editor) => changes.Add(editor.Name + "=" + editor.IsDirty);
        Assert.Empty(tracker.DirtyEditors);
        b.UpdateText("2");
        a.UpdateText("2");
        a.UpdateText("3");
        Assert.Equal(new[] { "a", "b" }, tracker.DirtyEditors.Select(editor => editor.Name));
        Assert.Equal(new[] { "b" }, tracker.DirtyEditorsFor(first).Select(editor => editor.Name));
        Assert.Equal(new[] { "b=True", "a=True" }, changes);
        tracker.Untrack(b);
        Assert.True(b.IsClosed);
        Assert.Null(b.BeginUpload());
        Assert.Equal(new[] { "a" }, tracker.DirtyEditors.Select(editor => editor.Name));
        Assert.Equal(new[] { "a", "c" }, tracker.Editors.Select(editor => editor.Name));
    }

    [Fact]
    public void OlderConfirmationsAreIgnored()
    {
        var tracker = new ScriptEditorTracker();
        TrackedEditor editor = tracker.Track("x", "0", new object());
        editor.UpdateText("1");
        PendingUpload first = editor.BeginUpload()!;
        editor.UpdateText("2");
        PendingUpload second = editor.BeginUpload()!;
        Assert.True(editor.ConfirmUpload(second));
        Assert.False(editor.ConfirmUpload(first));
        Assert.False(editor.IsDirty);
        Assert.False(tracker.Track("y", "", new object()).ConfirmUpload(second));
    }

    [Fact]
    public void ServerTextReplacesTheBaseline()
    {
        var tracker = new ScriptEditorTracker();
        TrackedEditor editor = tracker.Track("x", "old", new object());
        editor.UpdateText("mine");
        editor.SetServerText("new", replaceCurrent: false);
        Assert.True(editor.IsDirty);
        editor.SetServerText("new", replaceCurrent: true);
        Assert.False(editor.IsDirty);
        Assert.Equal("new", editor.CurrentText);
        editor.Rename("renamed");
        Assert.Equal("renamed", editor.Name);
    }

    [Fact]
    public void ChangedLineRanges()
    {
        Assert.Null(ChangedLines.Compute("a\nb", "a\nb"));
        Assert.Equal(new ChangedLines(1, 2), ChangedLines.Compute("a\nb\nc", "a\nX\nc"));
        Assert.Equal(new ChangedLines(1, 3), ChangedLines.Compute("a\nc", "a\nX\nY\nc"));
        Assert.Equal(new ChangedLines(1, 2), ChangedLines.Compute("a\nb\nc", "a\nc"));
        Assert.Equal(new ChangedLines(2, 3), ChangedLines.Compute("a\nb", "a\nb\nc"));
        Assert.Equal(new ChangedLines(0, 1), ChangedLines.Compute("a", ""));
        Assert.Null(ChangedLines.Compute("a\r\nb", "a\nb"));
        var tracker = new ScriptEditorTracker();
        TrackedEditor editor = tracker.Track("x", "a\nb", new object());
        Assert.Null(editor.ChangedLines);
        editor.UpdateText("a\nc");
        Assert.Equal(new ChangedLines(1, 2), editor.ChangedLines);
    }

    [Fact]
    public async Task ConcurrentUse()
    {
        var tracker = new ScriptEditorTracker();
        var connection = new object();
        TrackedEditor[] editors = Enumerable.Range(0, 8).Select(index => tracker.Track("e" + index, "x", connection)).ToArray();
        await Task.WhenAll(editors.Select(editor => Task.Run(() =>
        {
            for (int index = 0; index < 500; ++index)
            {
                editor.UpdateText("y" + index);
                PendingUpload? upload = editor.BeginUpload();
                if (upload != null) editor.ConfirmUpload(upload);
                _ = tracker.DirtyEditors;
            }
        })));
        Assert.Empty(tracker.DirtyEditors);
    }
}
