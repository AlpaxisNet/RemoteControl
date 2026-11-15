using System.Collections.Concurrent;
using RemoteControl.Core;

namespace RemoteControl.Sync;

/// <summary>A private single-threaded context: posted callbacks run one after another on one background thread, which is the context's thread.</summary>
internal sealed class PumpSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public PumpSynchronizationContext(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            _queue.Add((d, state));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // Disposed: nothing runs any more.
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Thread.CurrentThread == _thread)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        Post(_ =>
        {
            try
            {
                d(state);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                done.Set();
            }
        }, null);
        done.Wait();
        if (failure is not null) throw new InvalidOperationException("A callback sent to the sync context failed.", failure);
    }

    public override SynchronizationContext CreateCopy() => this;

    public void Dispose()
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
        SetSynchronizationContext(this);
        foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
        {
            try
            {
                callback(state);
            }
            catch (Exception exception)
            {
                AppLog.Default.Error("A sync engine callback failed.", exception);
            }
        }
    }
}
