using System.Globalization;
using System.Text;
using RemoteControl.Core;

namespace RemoteControl.Ui.Players;

/// <summary>
/// The PM history files (<c>writePMHistory</c>, <c>TPlayerList.cpp:244-249</c>): one file per account,
/// <see cref="AppPaths.PmHistoryFile"/>, each message appended as <c>&lt;sender&gt; (&lt;Www Mmm dd HH:MM:SS yyyy&gt;):\n&lt;message&gt;\n\n</c>.
/// Appends run one after another on a background task (never on the UI thread, never two at once into a file); a failure is
/// logged, not thrown.
/// </summary>
internal sealed class PmHistory
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly AppPaths _paths;
    private readonly AppLog _log;
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>Creates the history writer.</summary>
    /// <param name="paths">Where the files are.</param>
    /// <param name="log">The log for failures.</param>
    public PmHistory(AppPaths paths, AppLog log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>The file of an account.</summary>
    /// <param name="account">The account.</param>
    /// <returns>The path.</returns>
    public string FileOf(string account) => _paths.PmHistoryFile(account);

    /// <summary>The C++ time stamp of a history entry (<c>%a %b %d %H:%M:%S %Y</c>).</summary>
    /// <param name="time">The time.</param>
    /// <returns>For example <c>Sun Nov 15 14:03:09 2026</c>.</returns>
    public static string Timestamp(DateTime time) => time.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture);

    /// <summary>One entry as it is written.</summary>
    /// <param name="sender">Who wrote it.</param>
    /// <param name="message">The message.</param>
    /// <param name="time">When.</param>
    /// <returns>The text.</returns>
    public static string Entry(string sender, string message, DateTime time) =>
        sender + " (" + Timestamp(time) + "):\n" + PmText.StripLabel(message) + "\n\n";

    /// <summary>
    /// Appends a message to an account's file. Nothing is written without an account, a sender or a message (as in the C++).
    /// </summary>
    /// <param name="account">The other player's account (names the file).</param>
    /// <param name="sender">Who wrote the message.</param>
    /// <param name="message">The message.</param>
    /// <returns>Completes when the entry was written (or failed).</returns>
    public Task AppendAsync(string account, string sender, string message)
    {
        if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(message)) return Task.CompletedTask;
        string path = FileOf(account);
        string entry = Entry(sender, message, DateTime.Now);
        lock (_gate)
        {
            _tail = _tail.ContinueWith(_ => Write(path, entry), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return _tail;
        }
    }

    /// <summary>Completes when every append started so far has finished.</summary>
    /// <returns>The task.</returns>
    public Task FlushAsync()
    {
        lock (_gate) return _tail;
    }

    /// <summary>Reads an account's history (empty when there is none), off the UI thread.</summary>
    /// <param name="account">The account.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The text, label lines removed.</returns>
    public async Task<string> ReadAsync(string account, CancellationToken cancellationToken)
    {
        await FlushAsync().ConfigureAwait(false);
        string path = FileOf(account);
        return await Task.Run(() =>
        {
            try
            {
                return File.Exists(path) ? PmText.StripLabel(File.ReadAllText(path, Utf8)) : string.Empty;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log.Warn("The PM history " + path + " could not be read.", exception);
                return string.Empty;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private void Write(string path, string entry)
    {
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.AppendAllText(path, entry, Utf8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warn("The PM history " + path + " could not be written.", exception);
        }
    }

    /// <summary>Whether a line of a history file is an entry's header (<c>... (...):</c>, or the legacy <c>You:</c> / <c>Opposite:</c>).</summary>
    /// <param name="line">The line.</param>
    /// <returns>Whether it is drawn as a header.</returns>
    public static bool IsHeader(string line) =>
        line.Contains(" (", StringComparison.Ordinal) && line.EndsWith("):", StringComparison.Ordinal) || line is "You:" or "Opposite:";
}
