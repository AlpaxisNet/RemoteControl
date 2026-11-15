namespace RemoteControl.Ui.Common.Tests;

public sealed class ChatFormatterTests
{
    [Fact]
    public void Time_stamp_comes_first_in_the_bold_colour_with_a_space()
    {
        ChatLine line = ChatFormatter.Format("hello", "[01:02 PM]", emotes: false);
        Assert.Equal("[01:02 PM] hello", line.Text);
        Assert.Equal(new ChatSpan(0, 11, ChatSpanKind.Timestamp), Assert.Single(line.Spans));
        Assert.False(line.IsAlert);
    }

    [Fact]
    public void Prefix_up_to_the_first_colon_is_bold()
    {
        ChatLine line = ChatFormatter.Format("Graal123: hi: there", "[9]", emotes: false);
        Assert.Equal("[9] Graal123: hi: there", line.Text);
        Assert.Equal([new ChatSpan(0, 4, ChatSpanKind.Timestamp), new ChatSpan(4, 9, ChatSpanKind.Prefix)], line.Spans);
    }

    [Fact]
    public void A_colon_at_the_start_or_no_colon_gives_no_prefix()
    {
        Assert.Empty(ChatFormatter.Format(":v hi", null, false).Spans);
        Assert.Empty(ChatFormatter.Format("no colon here", null, false).Spans);
        Assert.Equal("no colon here", ChatFormatter.Format("no colon here", "", false).Text);
    }

    [Theory]
    [InlineData("#ALERTSF x: y", " x: y", ChatAlertKind.Urgent)]
    [InlineData("#ALERTFS x: y", " x: y", ChatAlertKind.Urgent)]
    [InlineData("#ALERT New PM from Bob", " New PM from Bob", ChatAlertKind.Urgent)]
    [InlineData("#ALERTSP hello", " hello", ChatAlertKind.Sound)]
    [InlineData("#ALERTPS hello", " hello", ChatAlertKind.Sound)]
    [InlineData("#ALERTF hello", " hello", ChatAlertKind.Sound)]
    [InlineData("#ALERTP hello", " hello", ChatAlertKind.Sound)]
    [InlineData("#ALERTX hello", "X hello", ChatAlertKind.Urgent)]
    [InlineData("#ALERT", "", ChatAlertKind.Urgent)]
    public void Alert_prefixes_are_stripped_and_colour_the_line(string message, string shown, ChatAlertKind kind)
    {
        ChatLine line = ChatFormatter.Format(message, "[t]", emotes: true);
        Assert.Equal(kind, line.Alert);
        Assert.Equal("[t] " + shown, line.Text);
        Assert.DoesNotContain(line.Spans, span => span.Kind == ChatSpanKind.Prefix);
        if (shown.Length > 0) Assert.Contains(new ChatSpan(4, shown.Length, ChatSpanKind.Alert), line.Spans);
        Assert.Empty(line.Emotes);
    }

    [Theory]
    [InlineData("hello #ALERT", ChatAlertKind.None)]
    [InlineData(" #ALERT", ChatAlertKind.None)]
    [InlineData("#alert", ChatAlertKind.None)]
    public void Only_lines_starting_with_ALERT_are_alerts(string message, ChatAlertKind kind) =>
        Assert.Equal(kind, ChatFormatter.Format(message, null, false).Alert);

    [Fact]
    public void Alert_lines_get_no_emotes_but_keep_links()
    {
        ChatLine line = ChatFormatter.Format("#ALERT Kappa see http://x.org", null, emotes: true);
        Assert.Empty(line.Emotes);
        Assert.Equal("http://x.org", Assert.Single(line.Links).Url);
    }

    [Theory]
    [InlineData("see http://example.com now", "http://example.com", 4)]
    [InlineData("see https://example.com/a?b=c#d.", "https://example.com/a?b=c#d", 4)]
    [InlineData("go www.example.com, ok", "https://www.example.com", 3)]
    [InlineData("(http://a.b/c)", "http://a.b/c", 1)]
    [InlineData("'https://q.org'", "https://q.org", 1)]
    [InlineData("<http://lt.org>", "http://lt.org", 1)]
    [InlineData("\"http://dq.org\"x", "http://dq.org", 1)]
    [InlineData("end http://e.org!;:", "http://e.org", 4)]
    [InlineData("tab\thttp://t.org", "http://t.org", 4)]
    public void Links_are_found_as_the_cpp_found_them(string text, string url, int start)
    {
        ChatLink link = Assert.Single(ChatFormatter.FindUrls(text));
        Assert.Equal(url, link.Url);
        Assert.Equal(start, link.Start);
    }

    [Theory]
    [InlineData("xhttp://a.org")]
    [InlineData("foo_http://a.org")]
    [InlineData("9www.a.org")]
    [InlineData("http:/a.org")]
    [InlineData("ftp://a.org")]
    [InlineData("nothing here")]
    public void Not_links(string text) => Assert.Empty(ChatFormatter.FindUrls(text));

    [Fact]
    public void Several_links_and_the_link_after_the_time_stamp()
    {
        ChatLine line = ChatFormatter.Format("Bob: http://a.org and www.b.org", "[12:00 AM]", false);
        Assert.Equal(2, line.Links.Count);
        Assert.Equal(16, line.Links[0].Start);
        Assert.Equal("http://a.org", line.Text.Substring(line.Links[0].Start, line.Links[0].Length));
        Assert.Equal("www.b.org", line.Text.Substring(line.Links[1].Start, line.Links[1].Length));
        Assert.Equal("https://www.b.org", line.Links[1].Url);
    }

    [Fact]
    public void Emote_offsets_are_characters_not_bytes()
    {
        // Defect D9: the C++ used byte offsets of UTF-8 text as character offsets.
        ChatLine line = ChatFormatter.Format("Jörg: ça va? 😀 Kappa :v Привет PMNormal", null, emotes: true);
        Assert.Equal(3, line.Emotes.Count);
        foreach (ChatEmote emote in line.Emotes) Assert.Equal(emote.Emote.Text, line.Text.Substring(emote.Start, emote.Length));
        Assert.Equal(["Kappa", ":v", "PMNormal"], line.Emotes.Select(emote => emote.Emote.Text));
        Assert.True(line.Emotes[0].Start < line.Emotes[1].Start && line.Emotes[1].Start < line.Emotes[2].Start);
    }

    [Fact]
    public void Emotes_are_only_after_the_bold_prefix_and_not_in_links()
    {
        ChatLine line = ChatFormatter.Format("Kappa: Kappa http://k.org/Kappa", "[t]", emotes: true);
        ChatEmote emote = Assert.Single(line.Emotes);
        Assert.Equal(11, emote.Start);
        Assert.Empty(ChatFormatter.Format("Kappa Kappa", null, emotes: false).Emotes);
        Assert.Equal(2, ChatFormatter.Format("Kappa Kappa", null, emotes: true).Emotes.Count);
        Assert.Equal(2, ChatFormatter.Format("KappaKappa", null, emotes: true).Emotes.Count);
    }

    [Fact]
    public void Line_breaks_make_more_lines_formatted_as_the_cpp_buffer_showed_them()
    {
        IReadOnlyList<ChatLine> lines = ChatFormatter.FormatLines("Server: first\r\nsecond: line www.x.org\nthird", "[t]", true);
        Assert.Equal(3, lines.Count);
        Assert.Equal("[t] Server: first", lines[0].Text);
        Assert.Equal("second: line www.x.org", lines[1].Text);
        Assert.Empty(lines[1].Spans);
        Assert.Single(lines[1].Links);
        IReadOnlyList<ChatLine> alert = ChatFormatter.FormatLines("#ALERTP one\ntwo", null, true);
        Assert.Equal(ChatAlertKind.Sound, alert[0].Alert);
        Assert.Equal(new ChatSpan(0, 3, ChatSpanKind.Alert), Assert.Single(alert[1].Spans));
    }

    [Fact]
    public void Control_characters_never_reach_the_view()
    {
        ChatLine line = ChatFormatter.Format("a\u0001b\u0007c\rd", null, false);
        Assert.Equal("abc d", line.Text);
    }
}
