using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteControl.Core;

/// <summary>
/// Writes crash reports to <see cref="AppPaths.CrashLogFile"/> (in the user folder). A report holds the time, the kind of failure,
/// the process and thread, the program version, the operating system and runtime, the exception with its inner exceptions and
/// managed stacks, and the last lines of <see cref="AppLog"/>. Safe to call from any thread and from last-resort handlers;
/// it never throws.
/// </summary>
/// <remarks>
/// Differences from the C++ <c>TCrashLogger</c>: the C++ caught POSIX signals and Windows structured exceptions, wrote raw addresses
/// and appended to <c>&lt;program&gt;/logs/crash.log</c>, which a read-only installation never wrote (D17). .NET reports managed
/// exceptions instead (with full stacks), so signals, <c>purecall</c> and <c>invalid_parameter</c> have no counterpart; the file is in
/// the user folder; a report also lists the recent log lines. The C++ wrote only the first crash of a process; here every report
/// is written (an unobserved task exception is not fatal), up to <see cref="MaximumReports"/> per process.
/// </remarks>
public sealed class CrashLog
{
    /// <summary>How many reports one process writes at most, so a failing loop cannot fill the disk.</summary>
    public const int MaximumReports = 50;

    /// <summary>The largest crash log before it is renamed to <c>.1</c> and started again.</summary>
    public const long MaximumFileSize = 2 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly AppLog _log;
    private int _reports;

    /// <summary>Creates a crash log that writes to <paramref name="filePath"/>.</summary>
    /// <param name="filePath">The crash log file, normally <see cref="AppPaths.CrashLogFile"/>.</param>
    /// <param name="log">The log whose last lines go into each report; <see cref="AppLog.Default"/> when <c>null</c>.</param>
    /// <param name="recentLineCount">How many log lines a report includes.</param>
    public CrashLog(string filePath, AppLog? log = null, int recentLineCount = 100)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = filePath;
        _log = log ?? AppLog.Default;
        RecentLineCount = Math.Max(0, recentLineCount);
    }

    /// <summary>The crash log file.</summary>
    public string FilePath { get; }

    /// <summary>How many recent log lines a report includes.</summary>
    public int RecentLineCount { get; }

    /// <summary>The program version written into reports (the entry assembly's informational version by default).</summary>
    public string Version { get; init; } = DefaultVersion();

    /// <summary>Writes <c>[time] crash_logger=installed version=...</c>, as the C++ did at every start.</summary>
    /// <returns><c>true</c> when the line was written.</returns>
    public bool WriteStartup() => Append($"[{Timestamp()}] crash_logger=installed version={Version}\n");

    /// <summary>Writes one crash report.</summary>
    /// <param name="kind">What failed, for example <c>unhandled_exception</c>, <c>unobserved_task</c> or <c>ui_dispatcher</c>.</param>
    /// <param name="exception">The exception, or <c>null</c>.</param>
    /// <param name="isTerminating">Whether the process is about to end.</param>
    /// <returns><c>true</c> when the report was written.</returns>
    public bool Write(string kind, Exception? exception, bool isTerminating = false)
    {
        try
        {
            if (Interlocked.Increment(ref _reports) > MaximumReports) return false;
            return Append(Format(kind, exception, isTerminating));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Writes one crash report for an exception object of unknown type (as <see cref="UnhandledExceptionEventArgs.ExceptionObject"/> gives).</summary>
    /// <param name="kind">What failed.</param>
    /// <param name="exceptionObject">The object that was thrown.</param>
    /// <param name="isTerminating">Whether the process is about to end.</param>
    /// <returns><c>true</c> when the report was written.</returns>
    public bool Write(string kind, object? exceptionObject, bool isTerminating)
    {
        if (exceptionObject is Exception exception) return Write(kind, exception, isTerminating);
        try
        {
            return Write(kind, new InvalidOperationException("Non-exception object thrown: " + (exceptionObject?.ToString() ?? "null")), isTerminating);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Hooks <see cref="AppDomain.UnhandledException"/> and <see cref="TaskScheduler.UnobservedTaskException"/> (the latter is marked
    /// observed so it does not end the process). The user interface adds its dispatcher's handler itself.
    /// </summary>
    /// <returns>Disposing it removes the handlers.</returns>
    public IDisposable InstallProcessHandlers()
    {
        UnhandledExceptionEventHandler domain = (_, args) => Write("unhandled_exception", args.ExceptionObject, args.IsTerminating);
        EventHandler<UnobservedTaskExceptionEventArgs> tasks = (_, args) =>
        {
            Write("unobserved_task", args.Exception, false);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += domain;
        TaskScheduler.UnobservedTaskException += tasks;
        return new Unhook(() =>
        {
            AppDomain.CurrentDomain.UnhandledException -= domain;
            TaskScheduler.UnobservedTaskException -= tasks;
        });
    }

    /// <summary>The text of one report.</summary>
    /// <param name="kind">What failed.</param>
    /// <param name="exception">The exception, or <c>null</c>.</param>
    /// <param name="isTerminating">Whether the process is about to end.</param>
    /// <returns>The report, ending with a blank line.</returns>
    public string Format(string kind, Exception? exception, bool isTerminating = false)
    {
        var text = new StringBuilder(4096);
        string stamp = Timestamp();
        text.Append('[').Append(stamp).Append("] crash kind=").Append(kind ?? "unknown").Append(isTerminating ? " terminating=true" : string.Empty).Append('\n');
        text.Append('[').Append(stamp).Append("] pid=").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
            .Append(" thread=").Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
        string? threadName = Thread.CurrentThread.Name;
        if (!string.IsNullOrEmpty(threadName)) text.Append(" (").Append(threadName).Append(')');
        text.Append('\n');
        text.Append("version=").Append(Version).Append('\n');
        text.Append("os=").Append(RuntimeInformation.OSDescription).Append(' ').Append(RuntimeInformation.OSArchitecture).Append('\n');
        text.Append("runtime=").Append(RuntimeInformation.FrameworkDescription).Append(' ').Append(RuntimeInformation.ProcessArchitecture).Append('\n');
        if (exception is null) text.Append("exception: none\n");
        else
        {
            text.Append("exception:\n");
            AppendException(text, exception, 0);
        }
        IReadOnlyList<string> recent = RecentLineCount == 0 ? Array.Empty<string>() : _log.Snapshot(RecentLineCount);
        text.Append("recent log (").Append(recent.Count.ToString(CultureInfo.InvariantCulture)).Append(" lines):\n");
        foreach (string line in recent) text.Append("  ").Append(line.Replace("\n", "\n  ", StringComparison.Ordinal)).Append('\n');
        text.Append('\n');
        return text.ToString();
    }

    private static void AppendException(StringBuilder text, Exception exception, int depth)
    {
        string indent = new(' ', 2 + depth * 2);
        text.Append(indent).Append(exception.GetType().FullName).Append(": ").Append(exception.Message).Append('\n');
        string? stack = exception.StackTrace;
        if (!string.IsNullOrEmpty(stack))
            foreach (string frame in stack.Split('\n')) text.Append(indent).Append(frame.TrimEnd('\r')).Append('\n');
        if (depth >= 8) return;
        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                text.Append(indent).Append("inner:\n");
                AppendException(text, inner, depth + 1);
            }
        }
        else if (exception.InnerException is not null)
        {
            text.Append(indent).Append("inner:\n");
            AppendException(text, exception.InnerException, depth + 1);
        }
    }

    private bool Append(string text)
    {
        bool locked = false;
        try
        {
            Monitor.TryEnter(_gate, TimeSpan.FromSeconds(2), ref locked);
            if (!locked) return false;
            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaximumFileSize) File.Move(FilePath, FilePath + ".1", overwrite: true);
            using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (locked) Monitor.Exit(_gate);
        }
    }

    private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string DefaultVersion()
    {
        try
        {
            Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(CrashLog).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return informational ?? assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private sealed class Unhook(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
