namespace RemoteControl.Protocol.Session;

/// <summary>What a session needs: the account, the context its events run on, and the time limits.</summary>
public sealed class RcSessionOptions
{
    /// <summary>The account.</summary>
    public required string Account { get; init; }

    /// <summary>The password.</summary>
    public required string Password { get; init; }

    /// <summary>
    /// The context every event and every cache change runs on (the UI thread's). When this and <see cref="Post"/> are both
    /// <c>null</c>, the session runs its own single-threaded pump.
    /// </summary>
    public SynchronizationContext? SynchronizationContext { get; init; }

    /// <summary>
    /// Instead of a <see cref="SynchronizationContext"/>: a delegate that queues an action for the owning thread (in order), or runs it
    /// at once (an inline context for tests; the session then serialises the calls). It must not wait for the owning thread.
    /// </summary>
    public Action<Action>? Post { get; init; }

    /// <summary>The PC-id list sent with the login (RClib <c>rc_set_login_pcid_list</c>); generated from this machine when <c>null</c>.</summary>
    public string? PcIdList { get; init; }

    /// <summary>The version string of the RC login.</summary>
    public string RcVersion { get; init; } = "GSERV025";

    /// <summary>The version string of the NC login.</summary>
    public string NcVersion { get; init; } = "NCL21075";

    /// <summary>How long a TCP connect may take.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the server may take to accept the login after the connect.</summary>
    public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the NC server may take to answer the NC login.</summary>
    public TimeSpan NcLoginTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a download may go without receiving anything before it fails.</summary>
    public TimeSpan DownloadInactivityTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How class scripts, NPC flags and level lists are sent to the NC server.</summary>
    public NcLineListFormat NcLineListFormat { get; init; } = NcLineListFormat.LineStrings;

    /// <summary>Receives log lines (dropped packets, refused sends). Called on any thread; must not throw.</summary>
    public Action<string>? Log { get; init; }
}
