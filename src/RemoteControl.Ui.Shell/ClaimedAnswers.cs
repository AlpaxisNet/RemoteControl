namespace RemoteControl.Ui.Shell;

/// <summary>
/// Answers of the server that something other than a window asked for (the MCP server's tools), so that the windows that show such
/// answers by themselves leave them alone: the Players group opens a rights, attributes, comments, profile, account, ban or accounts
/// window for every answer it sees, and the main window shows every level-NPC dump. A tool claims the answer it waits for before it
/// sends the request (<see cref="Claim"/>) and lets the claim go when it has its answer or gives up; a window asks
/// <see cref="TryTake"/> first and skips an answer that was claimed. One claim covers one answer. UI thread only.
/// </summary>
public sealed class ClaimedAnswers
{
    /// <summary>The key of a claim that covers an answer about anyone (a request by player id, the account list).</summary>
    public const string Any = "*";

    private readonly List<Entry> _claims = [];

    /// <summary>Claims the next answer of <paramref name="kind"/> about <paramref name="key"/> (an account, a level, or <see cref="Any"/>).</summary>
    /// <param name="kind">What the answer is: <c>account</c>, <c>rights</c>, <c>attributes</c>, <c>comments</c>, <c>profile</c>, <c>ban</c>, <c>banhistory</c>, <c>staffactivity</c>, <c>accountlist</c>, <c>localnpcs</c>.</param>
    /// <param name="key">What it is about, compared without regard to case: an account, a level, <c>#</c> and a player id, or <see cref="Any"/>.</param>
    /// <returns>Dispose it when the answer arrived or the wait ended; a claim that was taken is gone already.</returns>
    public IDisposable Claim(string kind, string key)
    {
        var entry = new Entry(kind ?? string.Empty, key ?? string.Empty);
        _claims.Add(entry);
        return new Unsubscriber(() => _claims.Remove(entry));
    }

    /// <summary>Takes the claim of an answer, if there is one: the caller then does not show the answer.</summary>
    /// <param name="kind">What the answer is.</param>
    /// <param name="key">What it is about.</param>
    /// <returns>Whether the answer was claimed.</returns>
    public bool TryTake(string kind, string? key)
    {
        Entry? entry = _claims.FirstOrDefault(item => item.Kind == kind && string.Equals(item.Key, key ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            ?? _claims.FirstOrDefault(item => item.Kind == kind && item.Key == Any);
        if (entry is null) return false;
        _claims.Remove(entry);
        return true;
    }

    /// <summary>
    /// Drops every claim: the connection was replaced (a reconnect, another server), and the requests the claims were made for were sent
    /// on the old one, so their answers will not come; a claim kept would swallow the user's next answer on the new connection.
    /// </summary>
    public void Clear() => _claims.Clear();

    /// <summary>How many claims wait for their answer.</summary>
    public int Count => _claims.Count;

    /// <summary>One claim; compared by reference, so a claim that is let go removes itself and no other of the same kind and key.</summary>
    private sealed class Entry(string kind, string key)
    {
        public string Kind { get; } = kind;

        public string Key { get; } = key;
    }
}
