using System.Text;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Tests;

/// <summary>Line lists as the RC uploads them (<see cref="LineListText"/>), checked against the rule the Alpaxis server reads them by.</summary>
public sealed class LineListTextTests
{
    [Fact]
    public void The_chain_test_is_the_one_of_the_RCs_reader()
    {
        var random = new Random(1123);
        for (int round = 0; round < 5000; round++)
        {
            var bytes = new byte[random.Next(0, 40)];
            // Mostly bytes of text, so that chains are common enough to be tested both ways.
            for (int index = 0; index < bytes.Length; index++) bytes[index] = (byte)(random.Next(4) == 0 ? random.Next(256) : random.Next(32, 48));
            Assert.Equal(RcSession.ReadExactGStrings(bytes, 0) is not null, LineListText.IsExactGStringChain(bytes));
        }
        Assert.True(LineListText.IsExactGStringChain([]));
        Assert.True(LineListText.IsExactGStringChain([32]));
        Assert.False(LineListText.IsExactGStringChain([31]));
        Assert.False(LineListText.IsExactGStringChain([34, 65]));
    }

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\n\nb", "a,\"\",b")]
    [InlineData("a\r\nb\n", "a,b,\"\"")]
    [InlineData("x,y", "\"x,y\"")]
    [InlineData("//#CLIENTSIDE", "\"//#CLIENTSIDE\"")]
    [InlineData("say(\"hi\\n\");", "\"say(\"\"hi\\\\n\"\");\"")]
    [InlineData("\ttab", "\"\ttab\"")]
    [InlineData("café", "\"café\"")]
    [InlineData("~", "~")]
    public void Lines_are_quoted_by_the_servers_rules(string text, string expected) =>
        Assert.Equal(expected, LineListText.CommaText(text));

    [Fact]
    public void A_text_that_would_chain_gets_its_last_bare_line_quoted()
    {
        // "~" is a length of 94: the first line is one GString of its own, and ",bbbbbbbbbbbb" (a comma is 12) another.
        string first = "~" + new string('a', 94);
        string last = new string('b', 12);
        Assert.True(LineListText.IsExactGStringChain(RcText.GetBytes(first + "," + last)));
        string sent = LineListText.CommaText(first + "\n" + last);
        Assert.Equal(first + ",\"" + last + "\"", sent);
        Assert.False(LineListText.IsExactGStringChain(RcText.GetBytes(sent)));
        Assert.Equal(first + "\n" + last, FakeNcServer.ReadLineList(RcText.GetBytes(sent)));
    }

    [Fact]
    public void A_text_that_chains_with_every_line_quoted_is_refused()
    {
        // Quoted: '"' is a length of 2, then '(' one of 8 that ends exactly at the closing quote.
        Assert.True(LineListText.IsExactGStringChain(RcText.GetBytes("\"a (defghij\"")));
        PacketTooLongException refused = Assert.Throws<PacketTooLongException>(() => LineListText.CommaText("a (defghij"));
        Assert.Contains("blank line", refused.Message);
        // One blank line more at the end, and it goes.
        Assert.False(LineListText.IsExactGStringChain(RcText.GetBytes(LineListText.CommaText("a (defghij\n"))));
    }

    [Fact]
    public void What_is_sent_never_chains_and_reads_back_as_it_was()
    {
        var random = new Random(20261122);
        string[] pieces = ["}", "{", "  ", "\t", "\"", "\\", ",", "/", "x", "this.a = 1;", "echo(\"a\\n\");", "café", "~", "!", "(", "0", "§"];
        for (int round = 0; round < 3000; round++)
        {
            var lines = new List<string>();
            int count = random.Next(1, 12);
            for (int line = 0; line < count; line++)
            {
                var builder = new StringBuilder();
                int length = random.Next(0, 6);
                for (int piece = 0; piece < length; piece++) builder.Append(pieces[random.Next(pieces.Length)]);
                lines.Add(builder.ToString());
            }
            // One line too long for a GString, as when the comma text is used.
            lines[random.Next(lines.Count)] += new string((char)random.Next(33, 127), random.Next(224, 400)).Replace(',', 'c').Replace('/', 's').Replace('"', 'q').Replace('\\', 'b');
            string text = string.Join('\n', lines);
            string sent;
            try
            {
                sent = LineListText.CommaText(text);
            }
            catch (PacketTooLongException)
            {
                continue;
            }
            byte[] bytes = RcText.GetBytes(sent);
            Assert.False(LineListText.IsExactGStringChain(bytes));
            Assert.Equal(text, FakeNcServer.ReadLineList(bytes));
        }
    }
}
