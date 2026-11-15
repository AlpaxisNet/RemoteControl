using RemoteControl.Core;

namespace RemoteControl.Sync;

/// <summary>Which way changes travel (the <c>Direction:</c> combo; the number is what the settings file stores).</summary>
public enum SyncDirection
{
    /// <summary>Server changes come down; local edits are kept and reported, never uploaded (the default).</summary>
    DownloadOnly = 0,

    /// <summary>Local edits go up; the server is not pulled.</summary>
    UploadOnly = 1,

    /// <summary>Both.</summary>
    TwoWay = 2,
}

/// <summary>
/// The settings of the sync window for one server (<c>loadConfig</c>/<c>saveConfig</c>, <c>TSyncManager.cpp:304-357</c>), with the C++
/// defaults: scripts on, files and configuration off, download only, no remote deletes, no auto-start, no git automation.
/// </summary>
public sealed record SyncSettings
{
    /// <summary>The defaults.</summary>
    public static SyncSettings Default { get; } = new();

    /// <summary>The workspace folder; empty until the user chooses one (<c>[sync] workspace</c>).</summary>
    public string Workspace { get; init; } = string.Empty;

    /// <summary>"Start synchronization automatically when connected" (<c>[sync] autoStart</c>, default off).</summary>
    public bool AutoStart { get; init; }

    /// <summary>"Script sync": weapons, classes and NPCs (<c>[sync] scripts</c>, default on).</summary>
    public bool Scripts { get; init; } = true;

    /// <summary>"File sync": the server's files (<c>[sync] files</c>, default off).</summary>
    public bool Files { get; init; }

    /// <summary>"Server configuration": options, flags and folder configuration (<c>[sync] serverConfig</c>, default off).</summary>
    public bool ServerConfig { get; init; }

    /// <summary>The direction (<c>[sync] direction</c> 0, 1 or 2; default download only).</summary>
    public SyncDirection Direction { get; init; } = SyncDirection.DownloadOnly;

    /// <summary>
    /// "Allow local deletions to delete server content" (<c>[sync] allowRemoteDeletes</c>, default off; only effective, and only saved
    /// as on, when uploads are allowed).
    /// </summary>
    public bool AllowRemoteDeletes { get; init; }

    /// <summary>"Remote file folders" (<c>[sync] fileFolders</c>, <c>"a; b"</c>; empty means every root the server offers).</summary>
    public string FileFolders { get; init; } = string.Empty;

    /// <summary>"Exclude files" (<c>[sync] excludeFiles</c>; see <see cref="ExclusionList"/>).</summary>
    public string ExcludeFiles { get; init; } = string.Empty;

    /// <summary>"Commit server downloads automatically" (<c>[git] autoCommit</c>).</summary>
    public bool GitAutoCommit { get; init; }

    /// <summary>"Push after automatic commits" (<c>[git] autoPush</c>).</summary>
    public bool GitAutoPush { get; init; }

    /// <summary>"Pull before local uploads" (<c>[git] autoPull</c>).</summary>
    public bool GitAutoPull { get; init; }

    /// <summary>"Upstream URL" (<c>[git] remoteUrl</c>).</summary>
    public string GitRemoteUrl { get; init; } = string.Empty;

    /// <summary>Whether server changes are downloaded.</summary>
    public bool DownloadsAllowed => Direction != SyncDirection.UploadOnly;

    /// <summary>Whether local edits are uploaded.</summary>
    public bool UploadsAllowed => Direction != SyncDirection.DownloadOnly;

    /// <summary>Whether local deletions delete server content.</summary>
    public bool RemoteDeletesEffective => UploadsAllowed && AllowRemoteDeletes;

    /// <summary>The settings as the C++ kept them: an unknown direction becomes download only, remote deletes need uploads.</summary>
    /// <returns>The normalised settings.</returns>
    public SyncSettings Normalized()
    {
        SyncDirection direction = Enum.IsDefined(Direction) ? Direction : SyncDirection.DownloadOnly;
        bool deletes = AllowRemoteDeletes && direction != SyncDirection.DownloadOnly;
        return this with
        {
            Direction = direction,
            AllowRemoteDeletes = deletes,
            Workspace = Workspace ?? string.Empty,
            FileFolders = FileFolders ?? string.Empty,
            ExcludeFiles = ExcludeFiles ?? string.Empty,
            GitRemoteUrl = GitRemoteUrl ?? string.Empty,
        };
    }
}

/// <summary>The outcome of reading a settings file.</summary>
/// <param name="Settings">The settings (the defaults when the file is missing or unreadable).</param>
/// <param name="Existed">Whether the file existed and was read.</param>
/// <param name="Warning">What went wrong, for the user; <c>null</c> when nothing did.</param>
/// <param name="MayOverwrite">Whether saving may replace the file (false when it exists, could not be read and could not be moved aside).</param>
public sealed record SyncSettingsLoadResult(SyncSettings Settings, bool Existed, string? Warning, bool MayOverwrite);

/// <summary>
/// The settings file: <c>&lt;user folder&gt;/sync/&lt;safeName(server)&gt;-&lt;stableSuffix(server)&gt;.conf</c> (<c>configPath</c>), a GLib key file
/// with the groups <c>[sync]</c> and <c>[git]</c>. The stored Git credentials sit beside it with the extensions <c>.credentials</c> and
/// <c>.token</c>.
/// </summary>
public static class SyncSettingsFile
{
    /// <summary>The settings file of a server.</summary>
    /// <param name="userDirectory">The user folder (<see cref="AppPaths.UserDirectory"/>).</param>
    /// <param name="serverName">The server name.</param>
    /// <returns>The path.</returns>
    public static string PathFor(string userDirectory, string serverName) =>
        Path.Combine(userDirectory, "sync", SyncNames.SafeName(serverName) + "-" + SyncNames.StableSuffix(serverName) + ".conf");

    /// <summary>Parses the content of a settings file the way <c>loadConfig</c> did.</summary>
    /// <param name="text">The content.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="error">Why it could not be parsed.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string text, out SyncSettings settings, out string? error)
    {
        settings = SyncSettings.Default;
        if (!KeyFile.TryParse(text, out KeyFile file, out error)) return false;
        long direction = file.HasKey("sync", "direction") ? file.GetInteger("sync", "direction") : 0;
        if (direction is < 0 or > 2) direction = 0;
        settings = new SyncSettings
        {
            Workspace = file.GetString("sync", "workspace") ?? string.Empty,
            AutoStart = file.HasKey("sync", "autoStart") && file.GetBoolean("sync", "autoStart"),
            Scripts = !file.HasKey("sync", "scripts") || file.GetBoolean("sync", "scripts"),
            Files = file.GetBoolean("sync", "files"),
            ServerConfig = file.GetBoolean("sync", "serverConfig"),
            Direction = (SyncDirection)direction,
            AllowRemoteDeletes = file.HasKey("sync", "allowRemoteDeletes") && file.GetBoolean("sync", "allowRemoteDeletes"),
            FileFolders = file.GetString("sync", "fileFolders") ?? string.Empty,
            ExcludeFiles = file.GetString("sync", "excludeFiles") ?? string.Empty,
            GitAutoCommit = file.GetBoolean("git", "autoCommit"),
            GitAutoPush = file.GetBoolean("git", "autoPush"),
            GitAutoPull = file.GetBoolean("git", "autoPull"),
            GitRemoteUrl = file.GetString("git", "remoteUrl") ?? string.Empty,
        }.Normalized();
        return true;
    }

    /// <summary>The content <c>saveConfig</c> wrote.</summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The file content.</returns>
    public static string Serialize(SyncSettings settings)
    {
        SyncSettings value = settings.Normalized();
        var file = new KeyFile();
        file.SetString("sync", "workspace", value.Workspace);
        file.SetBoolean("sync", "autoStart", value.AutoStart);
        file.SetBoolean("sync", "scripts", value.Scripts);
        file.SetBoolean("sync", "files", value.Files);
        file.SetBoolean("sync", "serverConfig", value.ServerConfig);
        file.SetInteger("sync", "direction", (int)value.Direction);
        file.SetBoolean("sync", "allowRemoteDeletes", value.RemoteDeletesEffective);
        file.SetString("sync", "fileFolders", value.FileFolders);
        file.SetString("sync", "excludeFiles", value.ExcludeFiles);
        file.SetBoolean("git", "autoCommit", value.GitAutoCommit);
        file.SetBoolean("git", "autoPush", value.GitAutoPush);
        file.SetBoolean("git", "autoPull", value.GitAutoPull);
        file.SetString("git", "remoteUrl", value.GitRemoteUrl);
        return file.Serialize();
    }

    /// <summary>
    /// Reads a settings file. A file that cannot be parsed is moved aside (<c>.unreadable-&lt;time&gt;</c>) and the defaults are used, so it
    /// is never overwritten (AGENTS.md rule 6).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The outcome.</returns>
    public static SyncSettingsLoadResult Load(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return new SyncSettingsLoadResult(SyncSettings.Default, false, null, true);
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SyncSettingsLoadResult(SyncSettings.Default, false, $"The sync settings {path} could not be read ({exception.Message}); they are not changed.", false);
        }

        if (TryParse(text, out SyncSettings settings, out string? error)) return new SyncSettingsLoadResult(settings, true, null, true);
        try
        {
            string? aside = AtomicFile.MoveAside(path, "unreadable", DateTime.Now);
            return new SyncSettingsLoadResult(SyncSettings.Default, false, $"The sync settings could not be read ({error}); the file was kept as {aside}.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SyncSettingsLoadResult(SyncSettings.Default, false, $"The sync settings could not be read ({error}) nor moved aside; they are not changed.", false);
        }
    }

    /// <summary>Writes the settings atomically.</summary>
    /// <param name="path">The file.</param>
    /// <param name="settings">The settings.</param>
    /// <exception cref="IOException">The file could not be written; the old one is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    public static void Save(string path, SyncSettings settings) => AtomicFile.WriteAllText(path, Serialize(settings));
}
