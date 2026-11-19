using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace RemoteControl.Ui.Common;

/// <summary>A window that takes files dropped onto it from the desktop's file manager (see <see cref="FileDrop"/>).</summary>
public interface IFileDropTarget
{
    /// <summary>Files are dragged over a point: whether they would be taken there (the window may show where they would go).</summary>
    /// <param name="point">The point, in the window's coordinates.</param>
    /// <returns>Whether a drop there is taken.</returns>
    bool FileDragOver(Point point);

    /// <summary>The drag left the window, was cancelled, or ended in a drop.</summary>
    void FileDragEnded();

    /// <summary>Files were dropped at a point.</summary>
    /// <param name="point">The point, in the window's coordinates.</param>
    /// <param name="paths">The local files and folders, as full paths (at least one).</param>
    void FilesDropped(Point point, IReadOnlyList<string> paths);
}

/// <summary>What the platform adds where Avalonia cannot deliver files dropped from other programs itself.</summary>
public interface IFileDropBridge
{
    /// <summary>Makes a window receive drops from other programs and hand them to its target, until the result is disposed.</summary>
    /// <param name="window">The window (its platform window exists).</param>
    /// <param name="target">Where the drag and the drop go, on the UI thread.</param>
    /// <returns>The registration, or null when this window cannot take part.</returns>
    IDisposable? Attach(Window window, IFileDropTarget target);
}

/// <summary>
/// Files dropped onto the program's windows from the desktop. On Windows (and macOS) Avalonia delivers them as its drag-and-drop
/// events; on Linux it cannot (Avalonia 11 does not speak the X11 drag-and-drop protocol, XDND), so the program sets a
/// <see cref="Bridge"/> that does. <see cref="Enable"/> connects both to one <see cref="IFileDropTarget"/>, so a window handles a drop
/// the same way on every system. Use on the UI thread.
/// </summary>
/// <example>
/// <code>
/// // In a window's constructor (the window implements IFileDropTarget):
/// Track(FileDrop.Enable(this, this));
/// </code>
/// </example>
public static class FileDrop
{
    /// <summary>The platform's bridge (set by the program at start on X11), or null.</summary>
    public static IFileDropBridge? Bridge { get; set; }

    /// <summary>Makes a window take files dropped from the desktop until the result is disposed (give it to <see cref="RcWindow.Track{T}"/>).</summary>
    /// <param name="window">The window.</param>
    /// <param name="target">What takes the files.</param>
    /// <returns>The registration.</returns>
    public static IDisposable Enable(Window window, IFileDropTarget target)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(target);
        string context = window.GetType().Name + ".FileDrop";
        EventHandler<DragEventArgs> over = (_, e) => UiSafe.Run(() =>
        {
            e.DragEffects = HasFiles(e) && target.FileDragOver(e.GetPosition(window)) ? DragDropEffects.Copy : DragDropEffects.None;
        }, context);
        EventHandler<DragEventArgs> left = (_, _) => UiSafe.Run(target.FileDragEnded, context);
        EventHandler<DragEventArgs> drop = (_, e) => UiSafe.Run(() =>
        {
            target.FileDragEnded();
            IReadOnlyList<string> paths = PathsOf(e);
            if (paths.Count == 0) return;
            e.Handled = true;
            e.DragEffects = DragDropEffects.Copy;
            target.FilesDropped(e.GetPosition(window), paths);
        }, context);

        DragDrop.SetAllowDrop(window, true);
        window.AddHandler(DragDrop.DragEnterEvent, over);
        window.AddHandler(DragDrop.DragOverEvent, over);
        window.AddHandler(DragDrop.DragLeaveEvent, left);
        window.AddHandler(DragDrop.DropEvent, drop);
        IDisposable? bridged = null;
        UiSafe.Run(() => bridged = Bridge?.Attach(window, target), context);
        return new Registration(() =>
        {
            window.RemoveHandler(DragDrop.DragEnterEvent, over);
            window.RemoveHandler(DragDrop.DragOverEvent, over);
            window.RemoveHandler(DragDrop.DragLeaveEvent, left);
            window.RemoveHandler(DragDrop.DropEvent, drop);
            DragDrop.SetAllowDrop(window, false);
            bridged?.Dispose();
        });
    }

    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private static IReadOnlyList<string> PathsOf(DragEventArgs e)
    {
        IStorageItem[]? items = e.DataTransfer.TryGetFiles();
        if (items is null) return Array.Empty<string>();
        return items.Select(item => item.TryGetLocalPath()).Where(path => !string.IsNullOrEmpty(path)).Select(path => path!).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Reads a <c>text/uri-list</c> (RFC 2483), the list of dropped files that X11 and other desktops pass: one URI per line,
    /// <c>#</c> lines are comments. Only <c>file:</c> URIs of this computer are kept (no host, <c>localhost</c>, or this computer's
    /// name), percent-decoded as UTF-8, in order, each once.
    /// </summary>
    /// <param name="text">The list.</param>
    /// <returns>The local paths.</returns>
    public static IReadOnlyList<string> ParseUriList(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\0');
            if (line.Length == 0 || line[0] == '#') continue;
            if (!line.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
            string rest = line[5..];
            if (rest.StartsWith("//", StringComparison.Ordinal))
            {
                rest = rest[2..];
                int slash = rest.IndexOf('/');
                if (slash < 0) continue;
                string host = rest[..slash];
                if (host.Length != 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && !host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) continue;
                rest = rest[slash..];
            }

            if (!rest.StartsWith('/')) continue;
            string path;
            try
            {
                path = Uri.UnescapeDataString(rest);
            }
            catch (UriFormatException)
            {
                continue;
            }

            // file:///C:/x on Windows.
            if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':') path = path[1..].Replace('/', '\\');
            if (path.IndexOf('\0') >= 0 || result.Contains(path, StringComparer.Ordinal)) continue;
            result.Add(path);
        }

        return result;
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
