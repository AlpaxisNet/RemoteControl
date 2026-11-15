using Avalonia.Media;

namespace RemoteControl.Ui.Common.Tests;

public sealed class ColorParserTests
{
    [Theory]
    [InlineData("#1e1e2e", 255, 0x1e, 0x1e, 0x2e)]
    [InlineData("#00C000", 255, 0x00, 0xC0, 0x00)]
    [InlineData("#abc", 255, 0xaa, 0xbb, 0xcc)]
    [InlineData("#abcd", 0xdd, 0xaa, 0xbb, 0xcc)]
    [InlineData("#1e1e2e60", 0x60, 0x1e, 0x1e, 0x2e)]
    [InlineData("#00000000", 0, 0, 0, 0)]
    [InlineData("  #FFFFFF ", 255, 255, 255, 255)]
    [InlineData("red", 255, 255, 0, 0)]
    [InlineData("CornflowerBlue", 255, 0x64, 0x95, 0xED)]
    [InlineData("transparent", 0, 0, 0, 0)]
    [InlineData("rgb(10, 20, 30)", 255, 10, 20, 30)]
    [InlineData("rgba(10,20,30,0.5)", 128, 10, 20, 30)]
    [InlineData("rgb(100%, 0%, 50%)", 255, 255, 0, 128)]
    public void Reads_what_the_options_hold(string text, byte a, byte r, byte g, byte b)
    {
        Assert.True(ColorParser.TryParse(text, out Color color));
        Assert.Equal(Color.FromArgb(a, r, g, b), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#123456789")]
    [InlineData("#gggggg")]
    [InlineData("notacolour")]
    [InlineData("rgb(1,2)")]
    [InlineData("rgb(a,b,c)")]
    [InlineData("rgba(1,2,3)")]
    [InlineData("rgb(1,2,3")]
    [InlineData("hsl(10, 20%, 30%)")]
    [InlineData("#-12345")]
    [InlineData("red; drop table")]
    public void Never_throws_on_garbage(string? text)
    {
        Assert.False(ColorParser.TryParse(text, out Color color));
        Assert.Equal(default, color);
        Assert.Equal(Colors.Orange, ColorParser.Parse(text, Colors.Orange));
    }

    [Fact]
    public void Random_text_never_throws()
    {
        var random = new Random(7);
        const string alphabet = "#0123456789abcdefABCDEFxyz(),.% rgbaRGBA-";
        for (int index = 0; index < 5000; index++)
        {
            int length = random.Next(0, 14);
            string text = new(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            ColorParser.TryParse(text, out _);
        }
    }

    [Fact]
    public void Formats_as_the_options_hold_it()
    {
        Assert.Equal("#1e1e2e", ColorParser.Format(Color.FromRgb(0x1e, 0x1e, 0x2e)));
        Assert.Equal("#1e1e2e60", ColorParser.Format(Color.FromArgb(0x60, 0x1e, 0x1e, 0x2e)));
        Assert.True(ColorParser.TryParse(ColorParser.Format(Color.FromArgb(0x60, 1, 2, 3)), out Color back));
        Assert.Equal(Color.FromArgb(0x60, 1, 2, 3), back);
    }
}
