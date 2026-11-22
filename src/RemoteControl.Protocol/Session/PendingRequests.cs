namespace RemoteControl.Protocol.Session;

/// <summary>
/// The requests whose answer does not say what it is about (the level of a level-NPC dump, the NPC of an attribute dump, the account of
/// a ban), oldest first: an answer belongs to the oldest request still waiting. A request the server never answers (a level it does not
/// have loaded, a request sent while the NC was down) would make every later answer be taken for the one before it, so that a window or
/// an MCP tool that waits for "its" answer never gets it. So when an answer comes while more than one request waits, the requests that
/// waited longer than <see cref="StaleAfter"/> are dropped first: their answer is not coming. A request that could not be sent is
/// removed (<see cref="Remove"/>). On the session's context only.
/// </summary>
/// <typeparam name="T">What a request is about.</typeparam>
internal sealed class PendingRequests<T>
{
    /// <summary>How long a request waits before a later one's answer may pass it by.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private readonly List<Entry> _entries = new();
    private readonly Func<DateTime> _clock;

    /// <summary>Creates an empty list.</summary>
    /// <param name="clock">The clock (UTC); the system's when <c>null</c> (tests give their own).</param>
    public PendingRequests(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>How many requests wait.</summary>
    public int Count => _entries.Count;

    /// <summary>Adds a request that was just sent.</summary>
    /// <param name="item">What it is about.</param>
    /// <returns>The entry, for <see cref="Remove"/>.</returns>
    public object Add(T item)
    {
        var entry = new Entry(item, _clock());
        _entries.Add(entry);
        return entry;
    }

    /// <summary>Removes a request that could not be sent (nothing happens when it was answered already).</summary>
    /// <param name="entry">What <see cref="Add"/> returned.</param>
    public void Remove(object entry)
    {
        if (entry is Entry pending) _entries.Remove(pending);
    }

    /// <summary>Takes the request an answer that just came belongs to.</summary>
    /// <param name="fallback">What the answer is about when no request waits.</param>
    /// <returns>The request, or <paramref name="fallback"/>.</returns>
    public T Take(T fallback)
    {
        DateTime now = _clock();
        while (_entries.Count > 1 && now - _entries[0].Time > StaleAfter) _entries.RemoveAt(0);
        if (_entries.Count == 0) return fallback;
        Entry first = _entries[0];
        _entries.RemoveAt(0);
        return first.Item;
    }

    /// <summary>One request; compared by reference, so that removing one removes no other about the same thing.</summary>
    private sealed class Entry(T item, DateTime time)
    {
        public T Item { get; } = item;

        public DateTime Time { get; } = time;
    }
}
