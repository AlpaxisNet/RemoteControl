using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Text;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Players.Tests;

/// <summary>Answers the MCP server's tools asked for (claimed on the session) open no window; the next answer of the same kind does again.</summary>
public sealed class ClaimedAnswerTests
{
    [AvaloniaFact]
    public void Claimed_answers_open_no_window_and_write_no_editor_backup()
    {
        using var fixture = new Fixture();
        ClaimedAnswers claims = fixture.Context.ClaimedAnswers;
        using (claims.Claim("rights", "bob"))
        using (claims.Claim("comments", "bob"))
        using (claims.Claim("profile", "bob"))
        using (claims.Claim("account", "bob"))
        using (claims.Claim("ban", "bob"))
        using (claims.Claim("banhistory", "bob"))
        using (claims.Claim("accountlist", ClaimedAnswers.Any))
        {
            fixture.Fake.RaisePlayerRights("BOB", new PlayerRights(1, string.Empty, string.Empty));
            fixture.Fake.RaisePlayerComments("bob", "helpful");
            fixture.Fake.RaiseProfile("bob", new[] { "Bob" });
            fixture.Fake.RaiseAccount(new AccountData { Account = "bob" });
            fixture.Fake.RaiseBanData("bob", string.Empty, "banned=0");
            fixture.Fake.RaiseBanList("banhistory", "bob", "nothing");
            fixture.Fake.RaiseAccountList(new[] { "bob", "ann" });
            TestUi.Settle();
            Assert.DoesNotContain(fixture.Context.Windows.All, window => window.IsVisible);
            Assert.Equal(0, claims.Count);
        }

        // Without a claim the answer is the user's, as before.
        fixture.Fake.RaisePlayerRights("bob", new PlayerRights(1, string.Empty, string.Empty));
        Assert.Equal("Edit Rights of bob", fixture.Latest<RightsWindow>().Title);
    }

    [AvaloniaFact]
    public void A_claim_covers_one_answer_and_ends_when_let_go()
    {
        using var fixture = new Fixture();
        IDisposable claim = fixture.Context.ClaimedAnswers.Claim("rights", "bob");
        claim.Dispose();
        fixture.Fake.RaisePlayerRights("bob", new PlayerRights(1, string.Empty, string.Empty));
        fixture.Latest<RightsWindow>();

        using (fixture.Context.ClaimedAnswers.Claim("comments", "ann"))
        {
            fixture.Fake.RaisePlayerComments("bob", "for bob");
            Assert.Contains(fixture.Context.Windows.All, window => window is CommentsWindow && window.IsVisible);
            Assert.Equal(1, fixture.Context.ClaimedAnswers.Count);
        }
    }
}
