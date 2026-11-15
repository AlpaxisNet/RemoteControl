using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using RemoteControl.Core;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Scripts;

/// <summary>
/// Editing a script or text in the user's own editor (C++ <c>TExternalEditor</c>, options <c>externaleditorscope</c>,
/// <c>externaleditorworkspace</c>, <c>externaleditorcommand</c>): the text is written to
/// <c>&lt;workspace&gt;/&lt;server&gt;/&lt;category&gt;/&lt;name&gt;</c>, the command is started (without a shell, not waited for), and every
/// save of the file is handed to the editor window, which uploads it through its normal save flow. Watching stops when the window
/// closes (<see cref="Dispose"/>). Fixed against review defects D-25 and D-26: names that are changed by making them safe get a
/// short hash so two scripts never share a file; a file that already has other text is not overwritten without asking; an empty or
/// still-growing file is not uploaded; no event is delivered after <see cref="Dispose"/>.
/// </summary>
internal sealed class ExternalEditor : IDisposable
{
    private const int DebounceMilliseconds = 250;
    private readonly Action<string> _saved;
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce;
    private string _known;
    private bool _disposed;

    private ExternalEditor(string path, string known, Action<string> saved)
    {
        Path = path;
        _known = known;
        _saved = saved;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            UiSafe.Fire(ReadAsync, "external editor file");
        };
        try
        {
            _watcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            UiSafe.Log.Warn("The external editor file " + path + " cannot be watched.", exception);
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>
    /// A folder whose programs tests may start while <see cref="ExternalPrograms.Disabled"/> is set (a harmless script the test wrote);
    /// null: nothing is started in a test.
    /// </summary>
    internal static string? TestCommandFolder { get; set; }

    /// <summary>The file.</summary>
    public string Path { get; }

    /// <summary>Raised on the UI thread with a line for the window's status (what was started, what was not uploaded).</summary>
    public event EventHandler<string>? Status;

    /// <summary>Whether a scope option value covers a kind of text.</summary>
    /// <param name="scope">The <c>externaleditorscope</c> option.</param>
    /// <param name="isScript">Weapon, class and NPC scripts (<c>scripts</c> and <c>text</c>); otherwise NPC flags and server texts (<c>text</c> only).</param>
    /// <returns>Whether the external editor is used.</returns>
    public static bool Covers(string? scope, bool isScript) => scope == "text" || (isScript && scope == "scripts");

    /// <summary>The workspace file of a text (C++ <c>safePathPart</c>, with a hash when making the name safe changed it).</summary>
    /// <param name="workspace">The workspace folder.</param>
    /// <param name="server">The server.</param>
    /// <param name="category"><c>weapons</c>, <c>classes</c>, <c>npcs</c>, <c>npcflags</c> or <c>text</c>.</param>
    /// <param name="name">The name.</param>
    /// <returns>The full path.</returns>
    public static string PathFor(string workspace, string server, string category, string name)
    {
        string safeName = SafePart(name);
        if (!string.Equals(safeName, name, StringComparison.Ordinal))
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
            string suffix = "-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
            string extension = System.IO.Path.GetExtension(safeName);
            safeName = extension.Length > 0 && extension.Length < safeName.Length ? safeName[..^extension.Length] + suffix + extension : safeName + suffix;
        }
        string path = System.IO.Path.Combine(workspace, SafePart(server), SafePart(category), safeName);
        if (System.IO.Path.GetExtension(path).Length == 0) path += ".gs2";
        return System.IO.Path.GetFullPath(path);
    }

    /// <summary>Makes one path part safe (C++ <c>safePathPart</c>).</summary>
    /// <param name="value">The part.</param>
    /// <returns>The safe part.</returns>
    public static string SafePart(string? value)
    {
        var builder = new StringBuilder(value ?? string.Empty);
        for (int index = 0; index < builder.Length; index++)
        {
            char c = builder[index];
            if (c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || c < 0x20) builder[index] = '_';
        }
        string result = builder.ToString();
        return result.Length == 0 ? "untitled" : result == "." ? "_" : result == ".." ? "__" : result;
    }

    /// <summary>
    /// Writes the text into the workspace (unless the window decided to keep the file's text) and watches the file; <see cref="Start"/>
    /// then starts the command.
    /// </summary>
    /// <param name="path">The file (<see cref="PathFor"/>).</param>
    /// <param name="text">The text to edit.</param>
    /// <param name="writeText">Whether to write <paramref name="text"/> (false keeps the file as it is).</param>
    /// <param name="saved">Gets each saved text of the file, on the UI thread.</param>
    /// <returns>The session, or null when the file could not be written.</returns>
    public static async Task<ExternalEditor?> OpenAsync(string path, string text, bool writeText, Action<string> saved)
    {
        string known = text;
        try
        {
            if (writeText) await Task.Run(() => AtomicFile.WriteAllText(path, text)).ConfigureAwait(true);
            else known = await File.ReadAllTextAsync(path).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UiSafe.Log.Warn("The external editor file " + path + " could not be written.", exception);
            return null;
        }
        return new ExternalEditor(path, known, saved);
    }

    /// <summary>Reads the file's text, or null when it does not exist.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The text or null.</returns>
    public static async Task<string?> ReadExistingAsync(string path)
    {
        try
        {
            return File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(true) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UiSafe.Log.Warn("The external editor file " + path + " could not be read.", exception);
            return null;
        }
    }

    /// <summary>Splits a command line into the program and its arguments (double and single quotes group, backslash escapes in double quotes).</summary>
    /// <param name="command">The command line.</param>
    /// <returns>The words.</returns>
    public static List<string> SplitCommand(string command)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        bool inWord = false;
        char quote = '\0';
        for (int index = 0; index < command.Length; index++)
        {
            char c = command[index];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c == '\\' && quote == '"' && index + 1 < command.Length && command[index + 1] is '"' or '\\') current.Append(command[++index]);
                else current.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                inWord = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord) words.Add(current.ToString());
                current.Clear();
                inWord = false;
            }
            else
            {
                current.Append(c);
                inWord = true;
            }
        }
        if (inWord) words.Add(current.ToString());
        return words;
    }

    /// <summary>The program and arguments for a command and a file: <c>{file}</c> is replaced, otherwise the file is the last argument; a command that is an existing file is the program itself.</summary>
    /// <param name="command">The command option.</param>
    /// <param name="path">The file.</param>
    /// <returns>The words, the program first.</returns>
    public static List<string> CommandLine(string command, string path)
    {
        List<string> words = File.Exists(command.Trim()) ? [command.Trim()] : SplitCommand(command);
        bool replaced = false;
        for (int index = 1; index < words.Count; index++)
        {
            if (!words[index].Contains("{file}", StringComparison.Ordinal)) continue;
            words[index] = words[index].Replace("{file}", path, StringComparison.Ordinal);
            replaced = true;
        }
        if (!replaced) words.Add(path);
        return words;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileEvent;
            _watcher.Created -= OnFileEvent;
            _watcher.Renamed -= OnFileEvent;
            _watcher.Dispose();
        }
    }

    /// <summary>Starts the editor command (or the desktop's default program when none is set).</summary>
    /// <param name="command">The <c>externaleditorcommand</c> option.</param>
    public void Start(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            bool started = ExternalPrograms.OpenFile(Path, UiSafe.Log);
            Status?.Invoke(this, started ? "Opened in the default editor: " + Path : "Open this file in your editor: " + Path);
            return;
        }
        List<string> words = CommandLine(command, Path);
        if (words.Count == 0) return;
        if (ExternalPrograms.Disabled && !IsTestCommand(words[0]))
        {
            UiSafe.Log.Info("External editor not started (external programs are disabled): " + words[0]);
            return;
        }
        var start = new ProcessStartInfo(words[0]) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string word in words.Skip(1)) start.ArgumentList.Add(word);
        UiSafe.Fire(async () =>
        {
            string? error = await Task.Run(() =>
            {
                try
                {
                    Process? process = Process.Start(start);
                    if (process is not null)
                    {
                        process.EnableRaisingEvents = true;
                        process.Exited += (sender, _) => (sender as Process)?.Dispose();
                    }
                    return null;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                {
                    return exception.Message;
                }
            }).ConfigureAwait(true);
            if (_disposed) return;
            Status?.Invoke(this, error is null ? "Editing in the external editor: " + Path : "The external editor could not be started: " + error);
        }, "start the external editor");
    }

    private static bool IsTestCommand(string program)
    {
        string? folder = TestCommandFolder;
        if (folder is null) return false;
        try
        {
            string full = System.IO.Path.GetFullPath(program);
            return full.StartsWith(System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private async Task ReadAsync()
    {
        if (_disposed) return;
        (string? text, bool stable) = await Task.Run(async () =>
        {
            try
            {
                var before = new FileInfo(Path);
                if (!before.Exists) return ((string?)null, true);
                long length = before.Length;
                DateTime written = before.LastWriteTimeUtc;
                string content = await File.ReadAllTextAsync(Path).ConfigureAwait(false);
                var after = new FileInfo(Path);
                return (content, after.Exists && after.Length == length && after.LastWriteTimeUtc == written);
            }
            catch (IOException)
            {
                return ((string?)null, false);
            }
            catch (UnauthorizedAccessException)
            {
                return ((string?)null, true);
            }
        }).ConfigureAwait(true);
        if (_disposed) return;
        if (!stable)
        {
            _debounce.Start();
            return;
        }
        if (text is null || string.Equals(text, _known, StringComparison.Ordinal)) return;
        if (text.Length == 0 && _known.Length > 0)
        {
            Status?.Invoke(this, "The external file is empty; it was not uploaded.");
            return;
        }
        _known = text;
        _saved(text);
    }
}
