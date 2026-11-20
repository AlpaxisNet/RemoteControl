using System.Runtime.CompilerServices;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Sync.ImportMissing;

/// <summary>
/// The one request for the missing files that a session has outstanding. The answers carry no request number, so a second request
/// for the same session must not be sent: it waits for this one's result. The request listens to the session until it ends (a result, an
/// error, no answer of any kind within the time, or the end of the connection); the time counts only until the server's first answer.
/// All of it runs on the session's context, except the timer, which only completes the request.
/// </summary>
internal sealed class PendingRequest
{
    private static readonly ConditionalWeakTable<IRcSession, PendingRequest> Requests = new();

    private readonly IRcSession _session;
    private readonly TaskCompletionSource<MissingFilesAnswer> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Action<MissingFilesProgressEventArgs>> _listeners = [];
    private readonly CancellationTokenSource _firstAnswerTimer = new();
    private readonly object _gate = new();
    private MissingFilesProgressEventArgs? _latest;
    private bool _answered;

    private PendingRequest(IRcSession session)
    {
        _session = session;
    }

    /// <summary>The request of the session that is outstanding, or a new one that was started (sent) now.</summary>
    public static PendingRequest GetOrStart(IRcSession session, MissingFilesImporterOptions options)
    {
        if (Requests.TryGetValue(session, out PendingRequest? existing)) return existing;
        var request = new PendingRequest(session);
        Requests.Add(session, request);
        request.Start(options);
        return request;
    }

    private void Start(MissingFilesImporterOptions options)
    {
        _session.MissingFilesReceived += OnReceived;
        _session.MissingFilesProgress += OnProgress;
        _session.Disconnected += OnDisconnected;
        if (!_session.IsConnected)
        {
            Complete(Lost());
            return;
        }

        _ = SendAsync(options);
    }

    private async Task SendAsync(MissingFilesImporterOptions options)
    {
        try
        {
            bool sent = await _session.RequestMissingFilesAsync().ConfigureAwait(true);
            if (!sent)
            {
                Complete(new MissingFilesAnswer(Array.Empty<MissingFile>(), "The request could not be sent.", TimedOut: false));
                return;
            }

            _ = WaitForFirstAnswerAsync(options.RequestTimeout);
        }
        catch (Exception exception)
        {
            Complete(new MissingFilesAnswer(Array.Empty<MissingFile>(), "The request could not be sent: " + exception.Message, TimedOut: false));
        }
    }

    private async Task WaitForFirstAnswerAsync(TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout, _firstAnswerTimer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (_answered) return;
        }

        Complete(new MissingFilesAnswer(Array.Empty<MissingFile>(), null, TimedOut: true));
    }

    /// <summary>Waits for the result; see <see cref="MissingFilesImporter.RequestAsync"/>.</summary>
    public async Task<MissingFilesAnswer> WaitAsync(Action<MissingFilesProgressEventArgs>? onProgress, CancellationToken connectionEnded, CancellationToken cancellationToken)
    {
        if (onProgress is not null)
        {
            _listeners.Add(onProgress);
            if (_latest is not null) onProgress(_latest);
        }

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration cancelRegistration = cancellationToken.Register(() => cancelled.TrySetResult());
        using CancellationTokenRegistration lostRegistration = connectionEnded.Register(() => lost.TrySetResult());
        try
        {
            Task finished = await Task.WhenAny(_done.Task, cancelled.Task, lost.Task).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (finished == lost.Task && !_done.Task.IsCompleted) return Lost();
            return await _done.Task.ConfigureAwait(true);
        }
        finally
        {
            if (onProgress is not null) _listeners.Remove(onProgress);
        }
    }

    /// <summary>Ends the request with an answer (the first one counts), stops listening and lets the session have a new request.</summary>
    private void Complete(MissingFilesAnswer answer)
    {
        if (_done.TrySetResult(answer)) Finish();
    }

    private static MissingFilesAnswer Lost() => new(Array.Empty<MissingFile>(), null, TimedOut: false, ConnectionLost: true);

    private void OnReceived(object? sender, MissingFilesEventArgs e)
    {
        MarkAnswered();
        Complete(new MissingFilesAnswer(e.Files, e.Error, TimedOut: false));
    }

    private void OnProgress(object? sender, MissingFilesProgressEventArgs e)
    {
        MarkAnswered();
        _latest = e;
        foreach (Action<MissingFilesProgressEventArgs> listener in _listeners.ToArray()) listener(e);
    }

    private void OnDisconnected(object? sender, DisconnectedEventArgs e) => Complete(Lost());

    private void MarkAnswered()
    {
        lock (_gate) _answered = true;
        _firstAnswerTimer.Cancel();
    }

    /// <summary>Stops listening and forgets the request; runs once, on any thread.</summary>
    private void Finish()
    {
        _firstAnswerTimer.Cancel();
        Requests.Remove(_session);
        _session.MissingFilesReceived -= OnReceived;
        _session.MissingFilesProgress -= OnProgress;
        _session.Disconnected -= OnDisconnected;
    }
}
