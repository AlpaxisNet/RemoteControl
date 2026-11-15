namespace RemoteControl.Core;

/// <summary>
/// Keeps a copy of every text an editor opens and saves (<c>TBackup.cpp</c>): <c>&lt;root&gt;/&lt;server&gt;/original|modified/&lt;type&gt;/&lt;target&gt;.txt</c>,
/// server and target escaped with <see cref="AppPaths.EscapeFileName"/>. Nothing is written without a server name, nor for the
/// types <c>file</c>, <c>banhistory</c>, <c>staffactivity</c>, <c>localbans</c> and any type containing <c>dump</c>.
/// </summary>
/// <remarks>
/// Differences from the C++: the root is passed in (normally <see cref="AppPaths.BackupDirectory"/>, the C++ used
/// <c>&lt;data&gt;/cache/backups</c>); the server name is per instance instead of a global; the type is escaped too and the path is
/// checked with <see cref="SafePath"/>; files are written atomically and asynchronously.
/// </remarks>
public sealed class EditorBackup
{
    /// <summary>Creates a backup writer.</summary>
    /// <param name="root">The backup folder.</param>
    /// <param name="serverName">The server the texts come from.</param>
    public EditorBackup(string root, string serverName)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = Path.GetFullPath(root);
        ServerName = serverName ?? string.Empty;
    }

    /// <summary>The backup folder.</summary>
    public string Root { get; }

    /// <summary>The server the texts come from; nothing is backed up while it is empty.</summary>
    public string ServerName { get; }

    /// <summary>Whether texts of <paramref name="type"/> are backed up (<c>shouldBackup</c>).</summary>
    /// <param name="type">The editor type, for example <c>weapon</c> or <c>serveroptions</c>.</param>
    /// <returns><c>true</c> when they are.</returns>
    public static bool ShouldBackup(string type) =>
        !string.IsNullOrEmpty(type) && type != "file" && !type.Contains("dump", StringComparison.Ordinal) && type != "banhistory" && type != "staffactivity" && type != "localbans";

    /// <summary>The file a backup goes to, or <c>null</c> when nothing is backed up.</summary>
    /// <param name="type">The editor type.</param>
    /// <param name="target">The script or text name; the type is used when empty.</param>
    /// <param name="modified">The saved text (<c>modified</c>) rather than the text as opened (<c>original</c>).</param>
    /// <returns>The full path, or <c>null</c>.</returns>
    public string? PathFor(string type, string target, bool modified)
    {
        if (ServerName.Length == 0 || !ShouldBackup(type)) return null;
        string relative = string.Join('/', AppPaths.EscapeFileName(ServerName), modified ? "modified" : "original", AppPaths.EscapeFileName(type),
            (string.IsNullOrEmpty(target) ? AppPaths.EscapeFileName(type) : AppPaths.EscapeFileName(target)) + ".txt");
        return SafePath.TryCombine(Root, relative, out string? path, out _) ? path : null;
    }

    /// <summary>Writes one backup. Never throws; a failure is logged.</summary>
    /// <param name="type">The editor type.</param>
    /// <param name="target">The script or text name.</param>
    /// <param name="content">The text.</param>
    /// <param name="modified">The saved text rather than the text as opened.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><c>true</c> when a file was written.</returns>
    public async Task<bool> WriteAsync(string type, string target, string content, bool modified, CancellationToken cancellationToken = default)
    {
        string? path = PathFor(type, target, modified);
        if (path is null) return false;
        try
        {
            await Task.Run(() => AtomicFile.WriteAllText(path, content ?? string.Empty), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            AppLog.Default.Warn($"Editor backup {path} was not written.", exception);
            return false;
        }
    }
}
