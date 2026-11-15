using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace RemoteControl.Extensions;

/// <summary>
/// One runtime process of an extension: started from its package folder with piped standard input, output and error. Lines it
/// writes are read on background tasks and passed to the callbacks; lines for it go through a bounded queue written by one
/// background task, so a caller never waits for the process (D-06) and a process that ended or never reads is only reported
/// (D-05: a broken pipe is an <see cref="IOException"/>, never a signal). Stopping sends the stop messages, closes the input, waits
/// for the stop timeout, kills the whole process tree and reaps it (D-07, D-21).
/// </summary>
/// <remarks>Callbacks are called on background threads; the <see cref="ExtensionManager"/> takes them onto its own context.</remarks>
internal sealed class ExtensionRuntime
{
    /// <summary>What the runtime reports to its owner. Every callback is called on a background thread and must not throw.</summary>
    /// <param name="StandardOutput">A line of standard output.</param>
    /// <param name="StandardError">A line of standard error.</param>
    /// <param name="Exited">The process ended (its exit code when known); called once, after both output streams were read to the end (or one second passed).</param>
    /// <param name="Problem">Something to log, such as a closed input or a line that was too long.</param>
    internal sealed record Callbacks(Action<ExtensionRuntime, string> StandardOutput, Action<ExtensionRuntime, string> StandardError, Action<ExtensionRuntime, int?> Exited, Action<ExtensionRuntime, string> Problem);

    /// <summary>The message <see cref="TrySend(string, out string)"/> gives when the queue is full.</summary>
    public const string QueueFullMessage = "Extension is not reading its input; the message was dropped";

    private static readonly UTF8Encoding Utf8 = new(false, false);
    private readonly string _workingDirectory;
    private readonly IReadOnlyList<string> _arguments;
    private readonly Callbacks _callbacks;
    private readonly long _maximumQueuedBytes;
    private readonly int _maximumLineLength;
    private readonly Channel<byte[]> _queue;
    private readonly object _gate = new();
    private long _queuedBytes;
    private Process? _process;
    private Task<string?>? _startTask;
    private Task? _stopTask;
    private Task _outputTask = Task.CompletedTask;
    private Task _errorTask = Task.CompletedTask;
    private Task _writerTask = Task.CompletedTask;
    private Task _exitTask = Task.CompletedTask;
    private volatile bool _stopping;
    private volatile bool _inputClosed;
    private volatile bool _exited;

    /// <summary>Creates a runtime; nothing runs before <see cref="StartAsync"/>.</summary>
    /// <param name="workingDirectory">The package folder.</param>
    /// <param name="arguments">The program and its arguments; the program is looked up on the path when it has no folder.</param>
    /// <param name="callbacks">Where lines and the exit go.</param>
    /// <param name="maximumQueuedMessages">The most messages waiting to be written.</param>
    /// <param name="maximumQueuedBytes">The most bytes waiting to be written.</param>
    /// <param name="maximumLineLength">The longest line read.</param>
    public ExtensionRuntime(string workingDirectory, IReadOnlyList<string> arguments, Callbacks callbacks, int maximumQueuedMessages, long maximumQueuedBytes, int maximumLineLength)
    {
        _workingDirectory = workingDirectory;
        _arguments = arguments;
        _callbacks = callbacks;
        _maximumQueuedBytes = Math.Max(1, maximumQueuedBytes);
        _maximumLineLength = Math.Max(1, maximumLineLength);
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Math.Max(1, maximumQueuedMessages)) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    }

    /// <summary>The process id once started.</summary>
    public int? ProcessId { get; private set; }

    /// <summary>Whether the process ended.</summary>
    public bool HasExited => _exited;

    /// <summary>Starts the process on a background thread. Calling it again returns the same task.</summary>
    /// <returns>An error message, or <c>null</c> when the process runs.</returns>
    public Task<string?> StartAsync()
    {
        lock (_gate)
        {
            _startTask ??= Task.Run(Start);
            return _startTask;
        }
    }

    /// <summary>Queues one line (a line break is added) without waiting.</summary>
    /// <param name="line">One JSON line.</param>
    /// <param name="error">Why the line was not queued.</param>
    /// <returns><c>true</c> when queued.</returns>
    public bool TrySend(string line, out string error) => TrySend(line, false, out error);

    /// <summary>
    /// Stops the process: queues <paramref name="stopMessages"/>, closes its input after them, waits up to <paramref name="grace"/>,
    /// then kills the process tree, waits for it to be reaped and frees everything. Calling it again returns the same task. Never throws.
    /// </summary>
    /// <param name="stopMessages">The lines asking the runtime to end.</param>
    /// <param name="grace">How long the runtime may take to end by itself.</param>
    /// <returns>A task that ends when the process is gone (or could not be killed within a few seconds).</returns>
    public Task StopAsync(IReadOnlyList<string> stopMessages, TimeSpan grace)
    {
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            _stopping = true;
            _stopTask = Task.Run(() => StopCoreAsync(stopMessages, grace));
            return _stopTask;
        }
    }

    private string? Start()
    {
        if (_stopping || _arguments.Count == 0 || string.IsNullOrEmpty(_arguments[0]))
        {
            _inputClosed = true;
            _queue.Writer.TryComplete();
            return _stopping ? "Extension was stopped before it started" : "Extension command is empty";
        }
        var info = new ProcessStartInfo(_arguments[0])
        {
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            StandardInputEncoding = Utf8,
        };
        for (int index = 1; index < _arguments.Count; index++) info.ArgumentList.Add(_arguments[index]);
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("no process was started");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _inputClosed = true;
            _queue.Writer.TryComplete();
            return "Failed to launch extension process: " + exception.Message;
        }
        lock (_gate)
        {
            _process = process;
            try { ProcessId = process.Id; } catch (InvalidOperationException) { ProcessId = null; }
            _outputTask = Task.Run(() => ReadLoopAsync(process.StandardOutput.BaseStream, _callbacks.StandardOutput, "standard output"));
            _errorTask = Task.Run(() => ReadLoopAsync(process.StandardError.BaseStream, _callbacks.StandardError, "standard error"));
            _writerTask = Task.Run(() => WriteLoopAsync(process.StandardInput.BaseStream));
            _exitTask = Task.Run(() => WatchExitAsync(process));
        }
        return null;
    }

    private bool TrySend(string line, bool stopMessage, out string error)
    {
        error = string.Empty;
        if ((_stopping && !stopMessage) || _inputClosed || _exited)
        {
            error = "Extension runtime is not running";
            return false;
        }
        byte[] bytes = Utf8.GetBytes(line + "\n");
        if (Interlocked.Add(ref _queuedBytes, bytes.Length) > _maximumQueuedBytes && !stopMessage)
        {
            Interlocked.Add(ref _queuedBytes, -bytes.Length);
            error = QueueFullMessage;
            return false;
        }
        if (_queue.Writer.TryWrite(bytes)) return true;
        Interlocked.Add(ref _queuedBytes, -bytes.Length);
        error = _inputClosed || _exited ? "Extension runtime is not running" : QueueFullMessage;
        return false;
    }

    private async Task ReadLoopAsync(Stream stream, Action<ExtensionRuntime, string> deliver, string name)
    {
        var reader = new BoundedLineReader(stream, _maximumLineLength);
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Truncated) Report($"A line on {name} was longer than {_maximumLineLength} bytes; its end was dropped");
                try { deliver(this, line.Text); }
                catch (Exception exception) { Report("Extension output handler failed: " + exception.Message); }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe was closed (the process ended or was killed): the end of the output.
        }
        catch (Exception exception)
        {
            Report($"Reading {name} failed: " + exception.Message);
        }
    }

    private async Task WriteLoopAsync(Stream input)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out byte[]? message))
                {
                    await input.WriteAsync(message).ConfigureAwait(false);
                    await input.FlushAsync().ConfigureAwait(false);
                    Interlocked.Add(ref _queuedBytes, -message.Length);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _inputClosed = true;
            _queue.Writer.TryComplete();
            if (!_stopping && !_exited)
            {
                // A runtime that closed its input cannot be talked to any more: it counts as ended (D-05).
                Report("Extension closed its input (" + exception.Message + "); stopping it");
                Kill();
            }
        }
        catch (Exception exception)
        {
            _inputClosed = true;
            _queue.Writer.TryComplete();
            Report("Writing to the extension failed: " + exception.Message);
        }
        finally
        {
            _inputClosed = true;
            try { input.Dispose(); } catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }
    }

    private async Task WatchExitAsync(Process process)
    {
        int? code = null;
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            code = process.ExitCode;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or ObjectDisposedException)
        {
            code = null;
        }
        _exited = true;
        _queue.Writer.TryComplete();
        await Task.WhenAny(Task.WhenAll(_outputTask, _errorTask), Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        try { _callbacks.Exited(this, code); }
        catch (Exception exception) { Report("Extension exit handler failed: " + exception.Message); }
    }

    private async Task StopCoreAsync(IReadOnlyList<string> stopMessages, TimeSpan grace)
    {
        try
        {
            Task<string?>? start;
            lock (_gate) start = _startTask;
            if (start is not null) await start.ConfigureAwait(false);
            Process? process;
            lock (_gate) process = _process;
            if (process is null)
            {
                _queue.Writer.TryComplete();
                return;
            }
            if (!_exited)
            {
                foreach (string message in stopMessages) TrySend(message, true, out _);
            }
            _queue.Writer.TryComplete();
            if (!_exited && grace > TimeSpan.Zero) await WaitAsync(_exitTask, grace).ConfigureAwait(false);
            if (!_exited) Kill();
            await WaitAsync(_exitTask, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitAsync(Task.WhenAll(_outputTask, _errorTask, _writerTask), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (_exitTask.IsCompleted) process.Dispose();
        }
        catch (Exception exception)
        {
            Report("Stopping the extension failed: " + exception.Message);
        }
    }

    private void Kill()
    {
        Process? process;
        lock (_gate) process = _process;
        if (process is null) return;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Already gone.
        }
    }

    private void Report(string message)
    {
        try { _callbacks.Problem(this, message); }
        catch (Exception) { /* The owner's logging failed; nothing more can be done. */ }
    }

    private static async Task WaitAsync(Task task, TimeSpan timeout)
    {
        await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
    }
}
