using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Players.Tests;

/// <summary>The ban texts as Alpaxis writes them (the literals of its own tests, PlayerConnectionTests.cs:23399-23592).</summary>
public sealed class AlpaxisBanTextTests
{
    [Fact]
    public void Ban_lines_keep_a_quoted_type_and_reason()
    {
        IReadOnlyList<BanRecord> bans = BanTexts.ParseBans(
            "account=bob,world=local,banned=1,\"bantype=Bug abuse\",releasetime=100,\"reason=spam, again \"\"now\"\"\"\n"
            + "account=pc:777,world=all,banned=1,bantype=Warning,releasetime=86400,reason=alts");
        Assert.Equal(new BanRecord("bob", "local", "Bug abuse", "100", "spam, again \"now\""), bans[0]);
        Assert.Equal(new BanRecord("pc:777", "all", "Warning", "86400", "alts"), bans[1]);
        Assert.Equal(3, bans[1].Scope);
    }

    [Fact]
    public void Unquoted_lines_of_older_servers_read_as_before()
    {
        BanRecord ban = BanTexts.ParseBans("account=x,world=local,bantype=Long,releasetime=1700000000,reason=cheating a lot").Single();
        Assert.Equal(new BanRecord("x", "local", "Long", "1700000000", "cheating a lot"), ban);
    }

    [Fact]
    public void Ban_types_with_a_quoted_name()
    {
        Assert.Equal(
            new[] { new BanType("Spamming", 100), new BanType("Bug abuse", 2592000), new BanType("Other (unlimited)", 315360001) },
            BanTexts.ParseTypes("Spamming,100\n\"Bug abuse\",2592000\nOther (unlimited),315360001"));
    }

    [Theory]
    [InlineData("100", 0, "1 min")]
    [InlineData("7200", 86400, "2 hours")]
    [InlineData("315360000", 86400, "unlimited")]
    [InlineData("", 86400, "1 day")]
    [InlineData("0", 3600, "1 hour")]
    public void Time_left_is_the_seconds_left(string release, long typeSeconds, string expected) =>
        Assert.Equal(expected, BanTexts.TimeLeft(release, typeSeconds, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)));

    [Fact]
    public void An_older_servers_unix_time_still_counts_down()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Assert.Equal("2 hours", BanTexts.TimeLeft("1800007230", 86400, now));
        Assert.Equal("1 day", BanTexts.TimeLeft("1700000000", 86400, now));
    }

    [Theory]
    [InlineData("", false, 259200, 0, "259200", false)]
    [InlineData("100", false, 259200, 0, "100", false)]
    [InlineData("3600", false, 259200, 1200, "2400", false)]
    [InlineData("3600", false, 259200, 3600, "259200", true)]
    [InlineData("3600", false, 0, 9000, "", true)]
    [InlineData("100", true, 259200, 50, "259200", false)]
    [InlineData("315360000", false, 259200, 99999, "315360000", false)]
    [InlineData("1800007230", false, 259200, 500, "1800007230", false)]
    [InlineData("", false, 0, 0, "", false)]
    [InlineData(" ", true, 0, 0, "", false)]
    public void A_ban_that_is_set_carries_its_length(string received, bool reset, long typeSeconds, long elapsed, string expected, bool expectedEnded)
    {
        Assert.Equal(expected, BanTexts.ReleaseToSend(received, reset, typeSeconds, elapsed, out bool ended));
        Assert.Equal(expectedEnded, ended);
    }

    [Fact]
    public void Lengths_from_one_hundred_million_seconds_have_no_end()
    {
        Assert.Equal("Permanent (unlimited)", new BanType("Permanent", 200_000_000).Label);
        Assert.Equal("unlimited", BanTexts.TimeLeft(string.Empty, 200_000_000, DateTimeOffset.UnixEpoch));
        Assert.Equal("1157 days", BanTexts.Duration(99_999_999));
        Assert.Equal("(no type)", new BanType(string.Empty, 0) { NotListed = true }.Label);
        Assert.Equal("Ban evasion (not in the server's list)", new BanType("Ban evasion", 0) { NotListed = true }.Label);
    }

    [Fact]
    public void A_quote_inside_an_unquoted_field_is_kept()
    {
        Assert.Equal("said \"noob\"", BanTexts.ParseBans("account=x,world=local,bantype=Long,releasetime=100,reason=said \"noob\"").Single().Reason);
        Assert.Equal(new BanRecord("x", "local", "O\"Neil", "100", "r"), BanTexts.ParseBans("account=x,world=local,bantype=O\"Neil,releasetime=100,reason=r").Single());
        Assert.Equal(new[] { new BanType("Bob\"s ban", 3600) }, BanTexts.ParseTypes("Bob\"s ban,3600"));
        Assert.Equal(new[] { "a\\b", "c\"d", "e" }, BanTexts.SplitFields("\"a\\\\b\",\"c\"\"d\",e"));
        Assert.Equal(new[] { "a", "", "b", "" }, BanTexts.SplitFields("a,,b,"));
    }

    [Fact]
    public void Legacy_details_are_told_apart()
    {
        Assert.True(BanTexts.IsLegacyDetails("banned=1\nold reason"));
        Assert.True(BanTexts.IsLegacyDetails("banned=0\n"));
        Assert.False(BanTexts.IsLegacyDetails("account=bob,world=local,banned=1,bantype=Warning,releasetime=100,reason=r"));
        Assert.False(BanTexts.IsLegacyDetails(string.Empty));
    }

    [Theory]
    [InlineData("777", true)]
    [InlineData("-1", false)]
    [InlineData("0", false)]
    [InlineData("000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("12a", false)]
    public void Only_digits_name_a_computer(string? id, bool expected) => Assert.Equal(expected, BanTexts.IsComputerId(id));

    [Fact]
    public void Alpaxis_history_entries_are_headers()
    {
        string content = "1970-01-01 00:00 UTC  bob: banned by staffer (Spamming, 3 days): chat spam\n1970-01-01 00:02 UTC  bob: ban lifted by boss";
        Assert.True(BanTexts.IsHistoryHeader("1970-01-01 00:00 UTC  bob: banned by staffer (Spamming, 3 days): chat spam"));
        Assert.True(BanTexts.IsHistoryHeader("2026-01-02 10:11:12 by x:"));
        Assert.False(BanTexts.IsHistoryHeader("1970-01-01 00:00 bob: no zone"));
        Assert.Equal("1970-01-01 00:00 UTC  bob: banned by staffer (Spamming, 3 days): chat spam\n\n1970-01-01 00:02 UTC  bob: ban lifted by boss\n", BanTexts.HistoryText(content));
    }
}

/// <summary>The Edit Access window against Alpaxis's answers.</summary>
public sealed class AlpaxisBanWindowTests
{
    private static BanWindow Open(Fixture fixture, string computerId, string details)
    {
        fixture.Fake.RaiseBanList("bantypes", string.Empty, "Spamming,259200\n\"Bug abuse\",2592000\nHacking,315360000");
        fixture.Fake.RaiseBanData("bob", computerId, details);
        return fixture.Latest<BanWindow>();
    }

    [AvaloniaFact]
    public async Task A_new_ban_carries_the_length_of_its_type()
    {
        using var fixture = new Fixture();
        BanWindow window = Open(fixture, "-1", string.Empty);
        Assert.Equal("Edit Access of bob", window.Title);
        Assert.True(window.ScopeAt(0).Tab.IsVisible);
        Assert.True(window.ScopeAt(1).Tab.IsVisible);
        Assert.False(window.ScopeAt(2).Tab.IsVisible);
        Assert.False(window.ScopeAt(3).Tab.IsVisible);

        BanWindow.Scope global = window.ScopeAt(1);
        global.Banned.IsChecked = true;
        global.Type.SelectedIndex = 1;
        global.Reason.Text = "duping";
        Assert.Equal("Ban time left: 30 days", global.TimeLeft.Text);
        Assert.True(await window.ApplyAsync(1));
        Assert.Equal(new object?[] { "bob", "all", true, "Bug abuse", "2592000", "duping" }, fixture.Fake.LastCall("SetBanAsync")!.Arguments);
    }

    private const string Existing = "account=bob,world=local,banned=1,\"bantype=Bug abuse\",releasetime=3600,\"reason=spam, again\"\naccount=pc:777,world=local,banned=1,bantype=Hacking,releasetime=315360000,reason=alts";

    /// <summary>Opens the window with a clock of the test's own (the data comes twice: the first creates the window).</summary>
    private static BanWindow OpenAt(Fixture fixture, Func<DateTimeOffset> clock, string computerId, string details)
    {
        BanWindow window = Open(fixture, computerId, details);
        window.Now = clock;
        fixture.Fake.RaiseBanData("bob", computerId, details);
        return window;
    }

    [AvaloniaFact]
    public async Task An_existing_ban_keeps_its_end_and_a_reset_starts_the_types_length()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        BanWindow window = OpenAt(fixture, () => now, "777", Existing);
        Assert.Equal("Edit Access of bob (computer: 777)", window.Title);
        BanWindow.Scope local = window.ScopeAt(0);
        Assert.Equal("Bug abuse", ((BanType)local.Type.SelectedItem!).Name);
        Assert.Equal("spam, again", local.Reason.Text);
        Assert.Equal("Ban time left: 1 hour", local.TimeLeft.Text);
        Assert.Equal("Ban time left: unlimited", window.ScopeAt(2).TimeLeft.Text);

        // Twenty minutes later the ban still ends when it did: the seconds that are left now go out.
        now = now.AddMinutes(20);
        local.Reason.Text = "spam, again (edited)";
        Assert.True(await window.ApplyAsync(0));
        Assert.Equal(new object?[] { "bob", "local", true, "Bug abuse", "2400", "spam, again (edited)" }, fixture.Fake.LastCall("SetBanAsync")!.Arguments);

        window.ShowAndActivate();
        local.ResetTime.IsChecked = true;
        Assert.True(await window.ApplyAsync(0));
        Assert.Equal("2592000", fixture.Fake.LastCall("SetBanAsync")!.Argument<string>(4));
        Assert.True(await window.ApplyAsync(2));
        Assert.Equal(new object?[] { "pc:777", "local", true, "Hacking", "315360000", "alts" }, fixture.Fake.LastCall("SetBanAsync")!.Arguments);
    }

    [AvaloniaFact]
    public async Task A_ban_that_ended_while_the_window_was_open_asks_before_banning_again()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        BanWindow window = OpenAt(fixture, () => now, "777", Existing);
        now = now.AddHours(2);
        fixture.Fake.ClearCalls();

        Task<bool> refused = window.ApplyAsync(0);
        RcDialog question = TestUi.FindDialog();
        Assert.Contains("has ended since this window showed it", question.Message, StringComparison.Ordinal);
        question.Cancel();
        Assert.False(await refused);
        Assert.False(fixture.Fake.WasCalled("SetBanAsync"));

        Task<bool> again = window.ApplyAsync(0);
        TestUi.FindDialog().Accept();
        Assert.True(await again);
        Assert.Equal("2592000", fixture.Fake.LastCall("SetBanAsync")!.Argument<string>(4));
    }

    [AvaloniaFact]
    public async Task A_ban_whose_type_is_not_listed_keeps_its_type()
    {
        using var fixture = new Fixture();
        BanWindow window = Open(fixture, "-1", "account=bob,world=local,banned=1,bantype=,releasetime=315360000,reason=plain\naccount=bob,world=all,banned=1,\"bantype=Ban evasion\",releasetime=500,reason=alt");
        BanWindow.Scope local = window.ScopeAt(0);
        Assert.Equal("(no type)", ((BanType)local.Type.SelectedItem!).Label);
        Assert.Equal("Ban time left: unlimited", local.TimeLeft.Text);
        BanWindow.Scope global = window.ScopeAt(1);
        Assert.Equal("Ban evasion (not in the server's list)", ((BanType)global.Type.SelectedItem!).Label);

        Assert.True(await window.ApplyAsync(0));
        Assert.Equal(new object?[] { "bob", "local", true, string.Empty, "315360000", "plain" }, fixture.Fake.LastCall("SetBanAsync")!.Arguments);

        // A list that arrives later and knows the type selects it.
        fixture.Fake.RaiseBanList("bantypes", string.Empty, "Spamming,259200\n\"Ban evasion\",2592000");
        TestUi.Settle();
        Assert.Equal("Ban evasion (30 days)", ((BanType)global.Type.SelectedItem!).Label);
    }

    [AvaloniaFact]
    public void A_guest_shows_its_computer_bans_on_the_computer_tabs()
    {
        using var fixture = new Fixture();
        fixture.Fake.RaiseBanList("bantypes", string.Empty, "Warning,86400");
        fixture.Fake.RaiseBanData("pc:777", "777", "account=pc:777,world=local,banned=1,bantype=Warning,releasetime=100,reason=alts");
        BanWindow window = fixture.Latest<BanWindow>();
        Assert.Equal("Edit Access of pc:777", window.Title);
        Assert.False(window.ScopeAt(0).Tab.IsVisible);
        Assert.False(window.ScopeAt(1).Tab.IsVisible);
        Assert.True(window.ScopeAt(2).Tab.IsVisible);
        Assert.True(window.ScopeAt(2).Banned.IsChecked);
    }

    [AvaloniaFact]
    public void The_legacy_answer_of_openban_shows_the_account_as_banned()
    {
        using var fixture = new Fixture();
        fixture.Fake.RaiseBanData("bob", string.Empty, "banned=1\nold reason");
        BanWindow window = fixture.Latest<BanWindow>();
        Assert.False(window.UseNewBanType);
        Assert.True(window.ScopeAt(0).Banned.IsChecked);
        Assert.False(window.ScopeAt(1).Tab.IsVisible);
    }

    [AvaloniaFact]
    public async Task A_type_of_unknown_length_asks_before_a_ban_without_an_end()
    {
        using var fixture = new Fixture();
        fixture.Fake.RaiseBanList("bantypes", string.Empty, "Mystery");
        fixture.Fake.RaiseBanData("bob", string.Empty, string.Empty);
        BanWindow window = fixture.Latest<BanWindow>();
        window.ScopeAt(0).Banned.IsChecked = true;

        Task<bool> refused = window.ApplyAsync(0);
        RcDialog question = TestUi.FindDialog();
        Assert.Contains("\"Mystery\" is not known", question.Message, StringComparison.Ordinal);
        question.Cancel();
        Assert.False(await refused);
        Assert.False(fixture.Fake.WasCalled("SetBanAsync"));

        Task<bool> accepted = window.ApplyAsync(0);
        TestUi.FindDialog().Accept();
        Assert.True(await accepted);
        // A ban without an end names its length, 0: left out, Alpaxis would look for the type's length and ban nothing.
        Assert.Equal(new object?[] { "bob", "local", true, "Mystery", "0", "" }, fixture.Fake.LastCall("SetBanAsync")!.Arguments);
    }
}
