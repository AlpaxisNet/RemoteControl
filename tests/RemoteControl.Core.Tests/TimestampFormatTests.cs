namespace RemoteControl.Core.Tests;

public sealed class TimestampFormatTests
{
    private static readonly DateTime Afternoon = new(2026, 3, 7, 15, 4, 9, DateTimeKind.Local);
    private static readonly DateTime Midnight = new(2026, 12, 31, 0, 30, 0, DateTimeKind.Local);

    [Theory]
    [InlineData("[%I:%M %p]", "[03:04 PM]")]
    [InlineData("%H:%M:%S", "15:04:09")]
    [InlineData("%d/%m/%y", "07/03/26")]
    [InlineData("%Y-%m-%d", "2026-03-07")]
    [InlineData("%a %A %b %B", "Sat Saturday Mar March")]
    [InlineData("100%% done", "100% done")]
    [InlineData("%-I:%M%P", "3:04pm")]
    [InlineData("%e|%k|%l", " 7|15| 3")]
    [InlineData("%T %R %D %F", "15:04:09 15:04 03/07/26 2026-03-07")]
    [InlineData("%r", "03:04:09 PM")]
    [InlineData("%j %u %w", "066 6 6")]
    [InlineData("%c", "Sat Mar  7 15:04:09 2026")]
    [InlineData("%Q stays", "%Q stays")]
    [InlineData("ends with %", "ends with %")]
    [InlineData("", "")]
    [InlineData("no directives", "no directives")]
    public void Formats_like_strftime(string pattern, string expected) => Assert.Equal(expected, TimestampFormat.Format(pattern, Afternoon));

    [Fact]
    public void Midnight_is_twelve_am()
    {
        Assert.Equal("[12:30 AM]", TimestampFormat.Format("[%I:%M %p]", Midnight));
        Assert.Equal("00", TimestampFormat.Format("%H", Midnight));
    }

    [Fact]
    public void Utc_offset_of_a_utc_time_is_zero() => Assert.Equal("+0000 UTC", TimestampFormat.Format("%z %Z", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
}
