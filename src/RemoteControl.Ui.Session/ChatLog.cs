using System.Text;
using RemoteControl.Core;

namespace RemoteControl.Ui.Session;

/// <summary>
/// Appends RC chat lines to the chat log file (<c>appendChatLog</c>, <c>TRemoteFrame.cpp:1981-1991</c>): <c>"&lt;timestamp&gt; &lt;message&gt;\n"</c>,
/// the time stamp only with <c>rctimestamps</c>. Every session writes to the same file, so the lines go through one queue off the
/// UI thread, in order, each one appended whole; a line that cannot be written is logged once and dropped.
/// </summary>
internal static class ChatLog
{
    private static readonly object s_gate = new();
    private static Task s_tail = Task.CompletedTask;
    private static string? s_lastFailure;

    /// <summary>Queues a line when <c>logrcchat</c> is on.</summary>
    /// <param name="paths">The program's folders (a relative log path is inside the user folder).</param>
    /// <param name="options">The options at the time of the line.</param>
    /// <param name="message">The line as the server sent it (alert prefix included).</param>
    /// <param name="log">Where a failure is logged.</param>
    public static void Append(AppPaths paths, RcOptions options, string message, AppLog log)
    {
        if (!options.LogRcChat || string.IsNullOrWhiteSpace(options.ChatLogFile)) return;
        string path = paths.ChatLogFile(options);
        string line = (options.RcTimestamps ? TimestampFormat.Format(options.TimestampFormat, DateTime.Now) + " " : string.Empty) + message + "\n";
        lock (s_gate)
        {
            s_tail = s_tail.ContinueWith(_ => Write(path, line, log), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>Completes when every queued line was written (tests and program exit).</summary>
    /// <returns>The task.</returns>
    public static Task FlushAsync()
    {
        lock (s_gate) return s_tail;
    }

    private static void Write(string path, string line, AppLog log)
    {
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
            s_lastFailure = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            if (s_lastFailure != path) log.Warn("The RC chat log " + path + " could not be written.", exception);
            s_lastFailure = path;
        }
    }
}
