using System.Collections.Concurrent;

namespace RemoteControl.Protocol.Net;

/// <summary>Runs work on the one context that owns a session's state: a <see cref="SynchronizationContext"/>, a post delegate, or a private pump thread.</summary>
internal abstract class EventPoster : IDisposable
{
    /// <summary>Queues work; it runs later, in posting order.</summary>
    /// <returns>Whether it was queued (false once a private pump has been disposed).</returns>
    public abstract bool Post(Action action);

    /// <summary>Whether the calling thread is the context's thread (so work may run inline).</summary>
    public abstract bool IsOnContext { get; }

    public virtual void Dispose()
    {
    }

    public static EventPoster Create(SynchronizationContext? context, Action<Action>? post, string name) =>
        context is not null ? new SynchronizationContextPoster(context)
        : post is not null ? new DelegatePoster(post)
        : new PumpPoster(name);

    private sealed class SynchronizationContextPoster(SynchronizationContext context) : EventPoster
    {
        public override bool Post(Action action)
        {
            context.Post(static state => ((Action)state!)(), action);
            return true;
        }

        public override bool IsOnContext => SynchronizationContext.Current == context;
    }

    /// <summary>
    /// A post delegate. Posting is serialised, so a delegate that runs the action inline (a test's inline context) still never runs two
    /// actions at once, even when the RC and the NC reader post at the same moment.
    /// </summary>
    private sealed class DelegatePoster(Action<Action> post) : EventPoster
    {
        private readonly object _gate = new();

        public override bool Post(Action action)
        {
            lock (_gate) post(action);
            return true;
        }

        public override bool IsOnContext => false;
    }

    /// <summary>A private single-threaded pump (a background thread that ends when the session is disposed).</summary>
    private sealed class PumpPoster : EventPoster
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public PumpPoster(string name)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = name };
            _thread.Start();
        }

        public override bool IsOnContext => Thread.CurrentThread == _thread;

        public override bool Post(Action action)
        {
            try
            {
                _queue.Add(action);
                return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                // The pump has been disposed: nothing runs any more.
                return false;
            }
        }

        public override void Dispose()
        {
            try
            {
                _queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Run()
        {
            foreach (Action action in _queue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine($"Remote Control event pump: {exception}");
                }
            }
        }
    }
}
