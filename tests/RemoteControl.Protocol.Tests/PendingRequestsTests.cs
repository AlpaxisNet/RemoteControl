using RemoteControl.Protocol.Session;

namespace RemoteControl.Protocol.Tests;

public sealed class PendingRequestsTests
{
    [Fact]
    public void Answers_go_to_the_requests_in_order()
    {
        var pending = new PendingRequests<string>();
        pending.Add("a.nw");
        pending.Add("b.nw");
        Assert.Equal("a.nw", pending.Take(string.Empty));
        Assert.Equal("b.nw", pending.Take(string.Empty));
        Assert.Equal("none", pending.Take("none"));
    }

    [Fact]
    public void A_request_never_answered_does_not_shift_the_later_answers()
    {
        DateTime now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        var pending = new PendingRequests<string>(() => now);
        // The server has no level "missing.nw" loaded and does not answer.
        pending.Add("missing.nw");
        now += PendingRequests<string>.StaleAfter + TimeSpan.FromSeconds(1);
        pending.Add("start.nw");
        Assert.Equal("start.nw", pending.Take(string.Empty));
        Assert.Equal(0, pending.Count);
        pending.Add("next.nw");
        Assert.Equal("next.nw", pending.Take(string.Empty));
    }

    [Fact]
    public void A_slow_answer_alone_still_belongs_to_its_request()
    {
        DateTime now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        var pending = new PendingRequests<int>(() => now);
        pending.Add(7);
        now += TimeSpan.FromMinutes(5);
        Assert.Equal(7, pending.Take(-1));
        // Two recent requests: neither is stale, the first answer is the first one's.
        pending.Add(1);
        pending.Add(2);
        Assert.Equal(1, pending.Take(-1));
    }

    [Fact]
    public void A_request_that_was_not_sent_is_removed()
    {
        var pending = new PendingRequests<string>();
        object unsent = pending.Add("a.nw");
        pending.Add("b.nw");
        pending.Remove(unsent);
        pending.Remove(unsent);
        Assert.Equal("b.nw", pending.Take(string.Empty));
    }
}
