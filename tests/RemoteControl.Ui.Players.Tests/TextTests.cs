namespace RemoteControl.Ui.Players.Tests;

public sealed class TextTests
{
    [Fact]
    public void Pm_label_lines_are_removed()
    {
        Assert.Equal("hello\nworld", PmText.StripLabel("  Private Message:  \r\nhello  \nworld"));
        Assert.Equal(string.Empty, PmText.StripLabel(null));
    }

    [Theory]
    [InlineData("a < b and c > d", false, false, "a < b and c > d")]
    [InlineData("<b>x</b>&lt;", false, false, "x<")]
    [InlineData("<img src=\"http://a/b/c.gif\">", false, false, "[image: c.gif]")]
    [InlineData("<IMG SRC=c.gif>", false, true, "")]
    [InlineData("one<br/>two<p>three", false, false, "one\ntwo\nthree")]
    [InlineData("<b>x</b>", true, false, "<b>x</b>")]
    public void Html_becomes_plain_text(string message, bool noHtml, bool noImages, string expected) =>
        Assert.Equal(expected, PmText.ToDisplay(message, noHtml, noImages));

    [Fact]
    public void Identity_names_the_player_like_the_cpp()
    {
        Assert.Equal("acc", PmText.Identity("acc", ""));
        Assert.Equal("acc", PmText.Identity("acc", "acc"));
        Assert.Equal("Bob (acc)", PmText.Identity("acc", "Bob"));
    }

    [Fact]
    public void History_entries_have_the_cpp_format()
    {
        Assert.Equal("Bob (Sun Nov 15 14:03:09 2026):\nhi\n\n", PmHistory.Entry("Bob", "Private message:\nhi", new DateTime(2026, 11, 15, 14, 3, 9)));
        Assert.True(PmHistory.IsHeader("Bob (Sun Nov 15 14:03:09 2026):"));
        Assert.True(PmHistory.IsHeader("You:"));
        Assert.False(PmHistory.IsHeader("just text"));
    }

    [Theory]
    [InlineData(0, "-")]
    [InlineData(60, "1 min")]
    [InlineData(300, "5 mins")]
    [InlineData(3600, "1 hour")]
    [InlineData(86400 * 3, "3 days")]
    [InlineData(315360000, "unlimited")]
    public void Ban_durations(long seconds, string expected) => Assert.Equal(expected, BanTexts.Duration(seconds));

    [Fact]
    public void Ban_types_and_records_are_read()
    {
        Assert.Equal(new[] { new BanType("Short", 3600), new BanType("Two, words", 60) }, BanTexts.ParseTypes("\"Short\",3600\r\nTwo, words,60\n\n"));
        BanRecord ban = BanTexts.ParseBans("junk\naccount=pc:9,world=all,bantype=X,reason=r").Single();
        Assert.Equal(3, ban.Scope);
        Assert.True(BanTexts.ParseLegacyBanned("banned=true\nreason"));
        Assert.False(BanTexts.ParseLegacyBanned("banned=0"));
        Assert.Equal("1 hour", BanTexts.TimeLeft("", 3600, DateTimeOffset.UtcNow));
        Assert.Equal("1 day", BanTexts.TimeLeft("100000", 0, DateTimeOffset.FromUnixTimeSeconds(10000)));
        Assert.True(BanTexts.IsHistoryHeader("2026-01-02 10:11:12 by x:"));
        Assert.False(BanTexts.IsHistoryHeader("2026-01-02 10:11:12 by x"));
    }
}
