using System.Globalization;
using System.Text;

namespace RemoteControl.Core;

/// <summary>
/// The program log: a thread-safe ring buffer of the last lines in memory, optionally appended to a file. The crash log
/// includes the last lines. Never log passwords, keys or tokens.
/// </summary>
/// <remarks>Logging never throws; a file that cannot be written is ignored (and the file is turned off after repeated failures).</remarks>
public sealed class AppLog
{
    /// <summary>The largest log file before it is renamed to <c>.1</c> and started again.</summary>
    public const long MaximumFileSize = 4 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly object _gate = new();
    private readonly string[] _lines;
    private int _next;
    private int _count;
    private string? _filePath;
    private int _fileFailures;

    /// <summary>Creates a log that keeps the last <paramref name="capacity"/> lines.</summary>
    /// <param name="capacity">How many lines are kept in memory (at least 1).</param>
    public AppLog(int capacity = 500)
    {
        _lines = new string[Math.Max(1, capacity)];
    }

    /// <summary>The log the program writes to.</summary>
    public static AppLog Default { get; } = new();

    /// <summary>How many lines are kept in memory.</summary>
    public int Capacity => _lines.Length;

    /// <summary>The file lines are appended to, or <c>null</c> for memory only.</summary>
    public string? FilePath
    {
        get { lock (_gate) return _filePath; }
        set
        {
            lock (_gate)
            {
                _filePath = string.IsNullOrEmpty(value) ? null : value;
                _fileFailures = 0;
            }
        }
    }

    /// <summary>Adds an information line.</summary>
    /// <param name="message">The text.</param>
    public void Info(string message) => Write("info", message, null);

    /// <summary>Adds a warning line.</summary>
    /// <param name="message">The text.</param>
    /// <param name="exception">An optional exception, logged with its type and message.</param>
    public void Warn(string message, Exception? exception = null) => Write("warn", message, exception);

    /// <summary>Adds an error line.</summary>
    /// <param name="message">The text.</param>
    /// <param name="exception">An optional exception, logged in full (type, message, stack, inner exceptions).</param>
    public void Error(string message, Exception? exception = null) => Write("error", message, exception);

    /// <summary>Adds a line with the current time and a level.</summary>
    /// <param name="level">A short word such as <c>info</c>.</param>
    /// <param name="message">The text.</param>
    /// <param name="exception">An optional exception.</param>
    public void Write(string level, string message, Exception? exception = null)
    {
        try
        {
            var line = new StringBuilder();
            line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ').Append(level ?? "info").Append(' ').Append(message ?? string.Empty);
            if (exception is not null)
            {
                if (level == "error") line.Append('\n').Append(exception);
                else line.Append(": ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            }
            Add(line.ToString());
        }
        catch (Exception)
        {
            // Logging must never fail the caller.
        }
    }

    /// <summary>Adds a line as it is, without time or level.</summary>
    /// <param name="line">The line.</param>
    public void Add(string line)
    {
        string? path;
        lock (_gate)
        {
            _lines[_next] = line ?? string.Empty;
            _next = (_next + 1) % _lines.Length;
            if (_count < _lines.Length) _count++;
            path = _filePath;
            if (path is not null) AppendToFile(path, line ?? string.Empty);
        }
    }

    /// <summary>The kept lines, oldest first.</summary>
    /// <param name="maximum">At most this many of the newest lines; all by default.</param>
    /// <returns>A copy of the lines.</returns>
    public IReadOnlyList<string> Snapshot(int maximum = int.MaxValue)
    {
        lock (_gate)
        {
            int take = Math.Clamp(maximum, 0, _count);
            var result = new string[take];
            int first = (_next - take + _lines.Length) % _lines.Length;
            for (int index = 0; index < take; index++) result[index] = _lines[(first + index) % _lines.Length];
            return result;
        }
    }

    /// <summary>Removes the lines kept in memory.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_lines);
            _next = 0;
            _count = 0;
        }
    }

    private void AppendToFile(string path, string line)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaximumFileSize) File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path, line + Environment.NewLine, Utf8NoBom);
            _fileFailures = 0;
        }
        catch (Exception)
        {
            if (++_fileFailures >= 3) _filePath = null;
        }
    }
}
