using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Tests;

public sealed class GEncodingTests
{
    // Expected bytes worked out from RClib's protocol.cpp: each group is (value >> shift) & 0x7F plus 32 (the GShort's high group
    // is masked with 0xFF), e.g. 1000 = 7 * 128 + 104, so GShort(1000) is 7 + 32 = 0x27 and 104 + 32 = 0x88.
    [Theory]
    [InlineData(0, new byte[] { 0x20, 0x20 })]
    [InlineData(1000, new byte[] { 0x27, 0x88 })]
    [InlineData(0x6FFF, new byte[] { 0xFF, 0x9F })]
    public void GShort_matches_rclib(int value, byte[] expected)
    {
        byte[] actual = new byte[2];
        GEncoding.EncodeGShort(value, actual);
        Assert.Equal(expected, actual);
        Assert.Equal(value, GEncoding.DecodeGShort(actual));
    }

    [Fact]
    public void GShort_high_group_is_masked_with_0xFF_and_wraps()
    {
        // 0x7FFF >> 7 = 255; 255 + 32 wraps to 0x1F. RClib masks the high group with 0xFF, not 0x7F.
        byte[] actual = new byte[2];
        GEncoding.EncodeGShort(0x7FFF, actual);
        Assert.Equal(new byte[] { 0x1F, 0x9F }, actual);
    }

    [Fact]
    public void GInt3_and_GInt5_match_rclib()
    {
        // 100000 = 6 * 16384 + 13 * 128 + 32, so 6+32, 13+32, 32+32.
        byte[] three = new byte[3];
        GEncoding.EncodeGInt3(100000, three);
        Assert.Equal(new byte[] { 0x26, 0x2D, 0x40 }, three);
        Assert.Equal(100000, GEncoding.DecodeGInt3(three));

        // 20 MiB = 10 << 21: the upload limit Alpaxis sends as "0x87 {GInt5 20971520}".
        byte[] five = new byte[5];
        GEncoding.EncodeGInt5(20971520, five);
        Assert.Equal(new byte[] { 0x20, 0x2A, 0x20, 0x20, 0x20 }, five);
        Assert.Equal(20971520, GEncoding.DecodeGInt5(five));
    }

    [Fact]
    public void GByte_wraps_and_decodes_negative()
    {
        Assert.Equal(0x1F, GEncoding.EncodeGByte(-1));
        Assert.Equal(-1, GEncoding.DecodeGByte(0x1F));
        Assert.Equal(-32, GEncoding.DecodeGByte(0));
        Assert.Equal((byte)'v', GEncoding.EncodeGByte(0x56));
    }

    [Fact]
    public void GInt4_is_the_world_time_layout()
    {
        byte[] four = new byte[4];
        GEncoding.EncodeGInt4(123456, four);
        Assert.Equal(123456, GEncoding.DecodeGInt4(four));
    }
}

public sealed class RcTextTests
{
    [Fact]
    public void Every_byte_round_trips()
    {
        byte[] all = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        string text = RcText.GetString(all);
        Assert.Equal(256, text.Length);
        Assert.Equal(all, RcText.GetBytes(text));
    }

    [Fact]
    public void Windows1252_characters_map_to_their_bytes()
    {
        Assert.Equal(new byte[] { 0x80, 0xA7, 0xE9 }, RcText.GetBytes("€§é"));
        Assert.Equal("€", RcText.GetString(new byte[] { 0x80 }));
        Assert.Equal(new byte[] { (byte)'?' }, RcText.GetBytes("一"));
    }
}

public sealed class CommaTextTests
{
    // Expected strings follow RClib's gtokenize: a line is quoted when it starts with '"' or holds a byte below 33 or above 126, a
    // comma or a slash, or is only spaces; inside quotes '\' doubles and '"' becomes '""'; lines are joined with commas; a text that
    // ends in a newline gets an empty last field.
    [Theory]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("hello world", "\"hello world\"")]
    [InlineData("a/b", "\"a/b\"")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"\\", "\"say \"\"hi\"\"\\\\\"")]
    [InlineData("\"q", "\"\"\"q\"")]
    [InlineData("   ", "\"   \"")]
    [InlineData("x\n\ny", "x,,y")]
    [InlineData("a\r\nb", "a,b")]
    [InlineData("café", "\"café\"")]
    [InlineData("GraalEngine\npmserverplayers\nX\n", "GraalEngine,pmserverplayers,X,")]
    [InlineData("GraalEngine\npmguilds\n\n", "GraalEngine,pmguilds,,")]
    // Deliberately unlike RClib (which left these two unquoted and so lost them on the way back, and Alpaxis dropped the backslash):
    [InlineData("echo(\"hi\");", "\"echo(\"\"hi\"\");\"")]
    [InlineData("a\\b", "\"a\\\\b\"")]
    public void Tokenize_matches_rclib(string text, string expected) => Assert.Equal(expected, CommaText.Tokenize(text));

    [Theory]
    [InlineData("a,", "a")]
    [InlineData("\"a,b\",c", "a,b\nc")]
    [InlineData(",a", "\na")]
    [InlineData("a,,b", "a\n\nb")]
    [InlineData("\"say \"\"hi\"\"\\\\\"", "say \"hi\"\\")]
    [InlineData("\"unterminated", "")]
    [InlineData("", "")]
    public void Untokenize_matches_rclib(string content, string expected) => Assert.Equal(expected, CommaText.Untokenize(content));

    [Fact]
    public void Round_trip_drops_exactly_one_trailing_newline()
    {
        Assert.Equal("a", CommaText.Untokenize(CommaText.Tokenize("a\n")));
        Assert.Equal("a\n", CommaText.Untokenize(CommaText.Tokenize("a\n\n")));
    }

    [Fact]
    public void Random_texts_round_trip()
    {
        var random = new Random(1234);
        const string alphabet = "ab ,\"\\/\n\t;=é§xyz09";
        for (int round = 0; round < 2000; round++)
        {
            int length = random.Next(0, 30);
            string text = new(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            if (text.EndsWith('\n')) text += "z";
            Assert.Equal(text, CommaText.Untokenize(CommaText.Tokenize(text)));
        }
    }

    [Fact]
    public void Split_never_throws_on_odd_quotes()
    {
        foreach (string odd in new[] { "\"", "\"\"", "a\"", "\",", ",\"", "\"a\"b,", "a\"b\",c", "\"\"\"", ",,,", "\"a\"\"" })
            _ = CommaText.Split(odd);
    }
}

public sealed class PacketReaderTests
{
    [Fact]
    public void Truncated_values_fail_without_moving()
    {
        var reader = new PacketReader(new byte[] { 0x21 });
        Assert.False(reader.TryReadGShort(out _));
        Assert.False(reader.TryReadGInt3(out _));
        Assert.False(reader.TryReadGInt5(out _));
        Assert.Equal(0, reader.Position);
        Assert.True(reader.TryReadGByte(out int value));
        Assert.Equal(1, value);
        Assert.False(reader.TryReadGByte(out _));
    }

    [Fact]
    public void Negative_and_oversized_lengths_are_refused()
    {
        var negative = new PacketReader(new byte[] { 0x10, (byte)'a' });
        Assert.False(negative.TryReadGString(out _));
        Assert.Equal(0, negative.Position);
        var oversized = new PacketReader(new byte[] { 0x30, (byte)'a' });
        Assert.False(oversized.TryReadGString(out _));
        Assert.False(oversized.TryReadBytes(-1, out _));
        Assert.Equal(string.Empty, oversized.ReadGStringOrEmpty());
    }

    [Fact]
    public void Writer_refuses_texts_too_long_for_their_prefix()
    {
        Assert.Throws<PacketTooLongException>(() => new PacketWriter().WriteGString(new string('x', 224)));
        Assert.Equal(224, new PacketWriter().WriteGString(new string('x', 223)).Length);
        Assert.Throws<PacketTooLongException>(() => new PacketWriter().WriteGShortString(new string('x', 0x7000)));
    }
}
