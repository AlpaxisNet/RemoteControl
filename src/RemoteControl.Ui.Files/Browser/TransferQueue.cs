namespace RemoteControl.Ui.Files;

/// <summary>How urgent a request of the file browser is.</summary>
internal enum TransferPriority
{
    /// <summary>Something the user asked for (a listing, a download, an upload, a rename ...).</summary>
    User = 0,

    /// <summary>A picture downloaded for a preview or a thumbnail.</summary>
    Preview = 1,
}

/// <summary>
/// The one ordered queue of everything the file browser asks the server (the server's rule: one transfer at a time; the C++
/// "transfer routing" of <c>onFileReceived</c>, <c>TFileBrowserTree.cpp:1938-2048</c>). Jobs run one after the other on the UI
/// thread; what the user asked for runs before any preview, in the order asked; previews run one at a time behind them. A job
/// that is running is never interrupted by a later one. Used on the UI thread only.
/// </summary>
internal sealed class TransferQueue : IDisposable
{
    private readonly LinkedList<Job> _user = new();
    private readonly LinkedList<Job> _previews = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Job? _running;
    private bool _disposed;

    /// <summary>Whether a job runs now.</summary>
    public bool IsBusy => _running is not null;

    /// <summary>The priority of the job running now, or null.</summary>
    public TransferPriority? RunningPriority => _running?.Priority;

    /// <summary>The description of the job running now, or null.</summary>
    public string? RunningDescription => _running?.Description;

    /// <summary>Jobs waiting (not the running one).</summary>
    public int PendingCount => _user.Count + _previews.Count;

    /// <summary>Preview jobs waiting or running.</summary>
    public int PreviewCount => _previews.Count + (_running?.Priority == TransferPriority.Preview ? 1 : 0);

    /// <summary>Every job description in the order the jobs ran (for tests and the debug log).</summary>
    public List<string> History { get; } = new();

    /// <summary>Queues a job.</summary>
    /// <typeparam name="T">What it gives.</typeparam>
    /// <param name="priority">Its priority.</param>
    /// <param name="description">What it is, for the log and tests (<c>download levels/a.nw</c>).</param>
    /// <param name="work">The job; its token is cancelled when the queue is cleared or disposed.</param>
    /// <param name="fallback">What the job gives when it is cancelled before or while running.</param>
    /// <returns>The job's result.</returns>
    public Task<T> Enqueue<T>(TransferPriority priority, string description, Func<CancellationToken, Task<T>> work, T fallback)
    {
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed)
        {
            completion.SetResult(fallback);
            return completion.Task;
        }

        var job = new Job(priority, description, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        job.Run = async () =>
        {
            T result = fallback;
            try
            {
                if (!job.Cancellation.IsCancellationRequested) result = await work(job.Cancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                result = fallback;
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                return;
            }

            completion.TrySetResult(job.Cancellation.IsCancellationRequested ? fallback : result);
        };
        job.Cancel = () => completion.TrySetResult(fallback);
        (priority == TransferPriority.User ? _user : _previews).AddLast(job);
        Pump();
        return completion.Task;
    }

    /// <summary>Whether a preview of a path is waiting or running.</summary>
    /// <param name="description">The preview job's description.</param>
    /// <returns>Whether it is queued.</returns>
    public bool HasPreview(string description) =>
        (_running is { Priority: TransferPriority.Preview } running && running.Description == description) || _previews.Any(job => job.Description == description);

    /// <summary>
    /// Drops the preview jobs that wait (navigation, a mode change). A running one finishes (the server is sending it; starting the
    /// next transfer meanwhile would break the one-at-a-time rule) and its result is ignored by the caller.
    /// </summary>
    public void ClearPreviews()
    {
        foreach (Job job in _previews) Drop(job);
        _previews.Clear();
    }

    /// <summary>Drops every job (a new connection); the running one is cancelled.</summary>
    public void Clear()
    {
        foreach (Job job in _user) Drop(job);
        foreach (Job job in _previews) Drop(job);
        _user.Clear();
        _previews.Clear();
        _running?.Cancellation.Cancel();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        _lifetime.Cancel();
    }

    private static void Drop(Job job)
    {
        job.Cancellation.Cancel();
        job.Cancel?.Invoke();
        job.Cancellation.Dispose();
    }

    private void Pump()
    {
        if (_running is not null || _disposed) return;
        LinkedList<Job> source = _user.Count > 0 ? _user : _previews;
        if (source.First is not { } node) return;
        Job job = node.Value;
        source.RemoveFirst();
        _running = job;
        History.Add(job.Description);
        _ = RunAsync(job);
    }

    private async Task RunAsync(Job job)
    {
        try
        {
            await job.Run!().ConfigureAwait(true);
        }
        finally
        {
            job.Cancellation.Dispose();
            if (ReferenceEquals(_running, job)) _running = null;
            Pump();
        }
    }

    private sealed class Job(TransferPriority priority, string description, CancellationTokenSource cancellation)
    {
        public TransferPriority Priority { get; } = priority;

        public string Description { get; } = description;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Func<Task>? Run { get; set; }

        public Action? Cancel { get; set; }
    }
}
