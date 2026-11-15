using System.Diagnostics;
using RemoteControl.Scripting;

namespace RemoteControl.Scripting.Tests;

public sealed class TextSearchTests
{
    private const string Text = "echo(Foo); foo = max(foo, 1); food();";

    [Fact]
    public void FindNextIsCaseInsensitiveByDefaultAndWraps()
    {
        Assert.Equal(new TextRange(5, 3), TextSearch.FindNext(Text, "foo", 0));
        Assert.Equal(new TextRange(11, 3), TextSearch.FindNext(Text, "foo", 8));
        Assert.Equal(new TextRange(5, 3), TextSearch.FindNext(Text, "foo", Text.Length));
        Assert.Null(TextSearch.FindNext(Text, "foo", Text.Length, FindOptions.Default with { Wrap = false }));
        Assert.Equal(new TextRange(11, 3), TextSearch.FindNext(Text, "foo", 0, FindOptions.Default with { CaseSensitive = true }));
    }

    [Fact]
    public void FindPreviousEndsAtOrBeforeTheStart()
    {
        Assert.Equal(new TextRange(21, 3), TextSearch.FindPrevious(Text, "foo", 30));
        Assert.Equal(new TextRange(11, 3), TextSearch.FindPrevious(Text, "foo", 21));
        Assert.Equal(new TextRange(30, 3), TextSearch.FindPrevious(Text, "foo", 0));
        Assert.Null(TextSearch.FindPrevious(Text, "foo", 0, FindOptions.Default with { Wrap = false }));
    }

    [Fact]
    public void WholeWordsOnly()
    {
        var options = FindOptions.Default with { WholeWord = true };
        IReadOnlyList<TextRange> all = TextSearch.FindAll(Text, "foo", options);
        Assert.Equal(new[] { new TextRange(5, 3), new TextRange(11, 3), new TextRange(21, 3) }, all);
        Assert.Equal(new TextRange(5, 3), TextSearch.FindNext(Text, "foo", 25, options));
        Assert.Equal(new TextRange(21, 3), TextSearch.FindPrevious(Text, "foo", 33, options));
        // A query that ends in a non-word character only checks the word side.
        Assert.Equal(new TextRange(17, 4), TextSearch.FindNext(Text, "max(", 0, options));
        Assert.Null(TextSearch.FindNext(Text, "ax(", 0, options));
        Assert.Single(TextSearch.FindAll("_foo foo", "foo", options));
        Assert.Single(TextSearch.FindAll("éfoo foo", "foo", options));
    }

    [Fact]
    public void EmptyQueryFindsNothing()
    {
        Assert.Null(TextSearch.FindNext(Text, string.Empty, 0));
        Assert.Null(TextSearch.FindPrevious(Text, string.Empty, 5));
        Assert.Empty(TextSearch.FindAll(Text, string.Empty));
    }

    [Fact]
    public void ReplaceSelectsTheNextMatchAndReplacesIt()
    {
        // As in the C++: when the selection is not a match, the next match is selected and replaced in the same step.
        ReplaceResult first = TextSearch.Replace(Text, "foo", "bar", new TextRange(0, 0));
        Assert.Equal("echo(bar); foo = max(foo, 1); food();", first.Text);
        Assert.Equal(new TextRange(5, 3), first.Replaced);
        Assert.Equal(new TextRange(11, 3), first.NextMatch);

        ReplaceResult second = TextSearch.Replace(first.Text, "foo", "bar", first.NextMatch!.Value);
        Assert.Equal("echo(bar); bar = max(foo, 1); food();", second.Text);
        Assert.Equal(new TextRange(11, 3), second.Replaced);
        Assert.Equal(new TextRange(21, 3), second.NextMatch);

        ReplaceResult none = TextSearch.Replace(Text, "zzz", "bar", new TextRange(2, 0));
        Assert.Same(Text, none.Text);
        Assert.Null(none.Replaced);
        Assert.Null(none.NextMatch);
    }

    [Fact]
    public void ReplaceAllContinuesAfterEachReplacement()
    {
        ReplaceAllResult result = TextSearch.ReplaceAll("aaa", "a", "aa");
        Assert.Equal("aaaaaa", result.Text);
        Assert.Equal(3, result.Count);
        Assert.Equal(6, result.Caret);
        ReplaceAllResult words = TextSearch.ReplaceAll(Text, "foo", "x", 3, FindOptions.Default with { WholeWord = true });
        Assert.Equal("echo(x); x = max(x, 1); food();", words.Text);
        Assert.Equal(3, words.Count);
        Assert.Equal(18, words.Caret);
        ReplaceAllResult none = TextSearch.ReplaceAll(Text, "zzz", "x", 7);
        Assert.Same(Text, none.Text);
        Assert.Equal(7, none.Caret);
    }

    [Fact]
    public void RegexSearchAndReplace()
    {
        var options = FindOptions.Default with { UseRegex = true, CaseSensitive = true };
        Assert.Equal(new TextRange(17, 3), TextSearch.FindNext(Text, "m[a-z]x", 0, options));
        Assert.Equal("echo(Foo); foo = max(1, foo); food();", TextSearch.ReplaceAll(Text, @"max\((\w+), (\w+)\)", "max($2, $1)", 0, options).Text);
        Assert.Null(TextSearch.FindNext(Text, "(", 0, options));
        Assert.Equal(new TextRange(30, 3), TextSearch.FindPrevious(Text, "fo+", Text.Length, options));
        Assert.Equal("a-b-c", TextSearch.ReplaceAll("abc", "(?<=.)(?=.)", "-", 0, options).Text);
    }

    [Fact]
    public void CatastrophicRegexTimesOut()
    {
        string text = new string('a', 30_000) + "!";
        var options = FindOptions.Default with { UseRegex = true, RegexTimeout = TimeSpan.FromMilliseconds(50) };
        var watch = Stopwatch.StartNew();
        Assert.Null(TextSearch.FindNext(text, "(a+)+$x", 0, options));
        Assert.Empty(TextSearch.FindAll(text, "(a|aa)+b", options));
        Assert.Equal(0, TextSearch.ReplaceAll(text, "(a+)+b", "x", 0, options).Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void HostileText()
    {
        string text = new string('(', 1_000_000) + "\0\U0001D49C";
        Assert.Equal(new TextRange(1_000_001, 2), TextSearch.FindNext(text, "\U0001D49C", 0));
        Assert.Equal(1_000_000, TextSearch.FindAll(text, "(").Count);
        Assert.Equal(new TextRange(1_000_000, 1), TextSearch.FindPrevious(text, "\0", text.Length));
        Assert.Equal(1_000_000, TextSearch.ReplaceAll(text, "(", "", 0).Count);
        Assert.Null(TextSearch.FindNext(text, "x", -5));
        Assert.Null(TextSearch.FindPrevious(text, "x", int.MaxValue));
    }

    [Fact]
    public void GoToLine()
    {
        const string text = "a\nb\r\nc\rd";
        Assert.Equal(4, RemoteControl.Scripting.GoToLine.LineCount(text));
        Assert.Equal(1, RemoteControl.Scripting.GoToLine.LineCount(string.Empty));
        Assert.Equal(3, RemoteControl.Scripting.GoToLine.LineNumberAt(text, 5));
        Assert.Equal(4, RemoteControl.Scripting.GoToLine.LineNumberAt(text, 99));
        Assert.Equal(2, RemoteControl.Scripting.GoToLine.Parse(" 2", 4));
        Assert.Equal(3, RemoteControl.Scripting.GoToLine.Parse("+3abc", 4));
        Assert.Equal(4, RemoteControl.Scripting.GoToLine.Parse("99999999999999999999", 4));
        Assert.Equal(1, RemoteControl.Scripting.GoToLine.Parse("-5", 4));
        Assert.Equal(1, RemoteControl.Scripting.GoToLine.Parse("abc", 4));
        Assert.Equal(1, RemoteControl.Scripting.GoToLine.Parse(null, 0));
        Assert.Equal(0, RemoteControl.Scripting.GoToLine.LineStartOffset(text, 1));
        Assert.Equal(2, RemoteControl.Scripting.GoToLine.LineStartOffset(text, 2));
        Assert.Equal(5, RemoteControl.Scripting.GoToLine.LineStartOffset(text, 3));
        Assert.Equal(7, RemoteControl.Scripting.GoToLine.LineStartOffset(text, 4));
        Assert.Equal(7, RemoteControl.Scripting.GoToLine.LineStartOffset(text, 40));
    }
}
