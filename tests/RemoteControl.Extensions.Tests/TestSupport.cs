using System.Collections.Concurrent;
using System.Diagnostics;

namespace RemoteControl.Extensions.Tests;

/// <summary>A temporary folder that is deleted at the end of the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(bool shortPath = false)
    {
        // Unix socket paths are limited to about 100 bytes, so socket tests use a short folder directly under /tmp.
        string root = shortPath && !OperatingSystem.IsWindows() ? "/tmp" : System.IO.Path.GetTempPath();
        Path = System.IO.Path.Combine(root, "rcx-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public string Write(string relative, string text)
    {
        string file = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

/// <summary>A thread that runs posted actions in order, like a UI thread.</summary>
internal sealed class UiThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public UiThread()
    {
        _thread = new Thread(() =>
        {
            foreach (Action action in _queue.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception exception) { Errors.Add(exception); }
            }
        }) { IsBackground = true, Name = "test UI thread" };
        _thread.Start();
    }

    public ConcurrentBag<Exception> Errors { get; } = new();

    public bool IsCurrent => Thread.CurrentThread == _thread;

    public void Post(Action action)
    {
        if (!_queue.IsAddingCompleted) _queue.Add(action);
    }

    public Task<T> InvokeAsync<T>(Func<T> function)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { completion.SetResult(function()); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}

internal static class Wait
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(8);

    /// <summary>Waits until <paramref name="condition"/> holds; fails the test after the timeout.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? Default;
        while (!condition())
        {
            if (watch.Elapsed > limit) Assert.Fail("Timed out waiting for " + what);
            await Task.Delay(20);
        }
    }

    /// <summary>Awaits a task with a timeout.</summary>
    public static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan? timeout = null)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(timeout ?? Default));
        if (finished != task) Assert.Fail("The operation timed out");
        return await task;
    }

    /// <summary>Awaits a task with a timeout.</summary>
    public static async Task WithTimeout(Task task, TimeSpan? timeout = null)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(timeout ?? Default));
        if (finished != task) Assert.Fail("The operation timed out");
        await task;
    }
}

internal static class Processes
{
    public static string? Python { get; } = Find("python3");

    public static string? Find(string name)
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (folder.Length == 0) continue;
            string candidate = Path.Combine(folder, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static bool IsAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static void KillQuietly(int? processId)
    {
        if (processId is not int id) return;
        try
        {
            using Process process = Process.GetProcessById(id);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}

/// <summary>A test that needs python3 on the path; skipped when there is none.</summary>
internal sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (Processes.Python is null) Skip = "python3 is not on the path";
        else if (OperatingSystem.IsWindows()) Skip = "The runtime tests use Unix process tools";
    }
}

/// <summary>A test of the Unix-domain socket transport; skipped on Windows.</summary>
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix-domain socket transport";
    }
}
