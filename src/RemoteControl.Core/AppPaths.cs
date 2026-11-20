using System.Text;

namespace RemoteControl.Core;

/// <summary>
/// Where the program keeps its files. There are two folders: the <see cref="UserDirectory"/> (per-user settings, accounts,
/// profiles, logs, caches; always writable) and the <see cref="ProgramDirectory"/> (the executable and the bundled assets;
/// possibly read-only). Tests create an instance with temporary folders and never touch the real user folder.
/// </summary>
/// <remarks>
/// Differences from the C++ (<c>TMain.cpp:686-704</c>, <c>TRCAccounts.cpp:219</c>, <c>TServerList.cpp:23</c>):
/// <list type="bullet">
/// <item>The user folder is <c>GScriptRC</c> under the user configuration folder on every system (<c>$XDG_CONFIG_HOME/GScriptRC</c> or
/// <c>~/.config/GScriptRC</c> on Linux and macOS, <c>%LOCALAPPDATA%\GScriptRC</c> on Windows). The C++ already kept the accounts and the
/// list-server profiles there on every system, so existing users keep them.</item>
/// <item>On Windows the C++ kept <c>control2config.txt</c> in the program folder; the C# keeps it in the user folder and, while there is none
/// there yet, reads the one beside the executable once and saves a copy (see <see cref="OptionsStore"/>).</item>
/// <item>The C++ changed the working folder to <c>~/.local/share/GScriptRC</c> (Linux) or the program folder (Windows) and resolved relative
/// option values (chat log, downloads, external-editor workspace) against it. The C# never changes the working folder and resolves them
/// against the user folder (<see cref="ResolveUserPath"/>); absolute values are kept as they are. An old relative chat log or download
/// folder under <c>~/.local/share/GScriptRC</c> is therefore not used any more.</item>
/// <item>PM history was written to <c>&lt;program&gt;/PMs</c> and the crash log to <c>&lt;program&gt;/logs/crash.log</c>; both are now in the
/// user folder, which is writable on an installed system (defect D17).</item>
/// <item>The caches (<c>~/.cache/GScriptRC</c> on Linux, <c>&lt;program&gt;/cache</c> on Windows) and the editor backups (<c>&lt;data&gt;/cache/backups</c>)
/// are under <c>&lt;user&gt;/cache</c>.</item>
/// </list>
/// </remarks>
public sealed class AppPaths
{
    /// <summary>The name of the per-user folder, the same as the C++ used.</summary>
    public const string FolderName = "GScriptRC";

    /// <summary>The options file name.</summary>
    public const string OptionsFileName = "control2config.txt";

    /// <summary>Creates paths for the given folders.</summary>
    /// <param name="userDirectory">The per-user folder (in tests, a temporary folder).</param>
    /// <param name="programDirectory">The folder of the executable and the bundled assets.</param>
    public AppPaths(string userDirectory, string programDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(userDirectory);
        ArgumentException.ThrowIfNullOrEmpty(programDirectory);
        UserDirectory = Path.GetFullPath(userDirectory);
        ProgramDirectory = Path.GetFullPath(programDirectory);
    }

    /// <summary>The paths of the current user and the running executable.</summary>
    /// <returns>The paths the program uses.</returns>
    public static AppPaths ForCurrentUser() => new(DefaultUserDirectory(), AppContext.BaseDirectory);

    /// <summary>
    /// The per-user folder: <c>%LOCALAPPDATA%\GScriptRC</c> on Windows, otherwise <c>$XDG_CONFIG_HOME/GScriptRC</c> when that variable
    /// holds an absolute path, else <c>~/.config/GScriptRC</c> (what GLib's <c>g_get_user_config_dir</c> gave the C++).
    /// </summary>
    /// <returns>The folder path; it may not exist yet.</returns>
    public static string DefaultUserDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), FolderName);
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)) return Path.Combine(xdg, FolderName);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
        return Path.Combine(home, ".config", FolderName);
    }

    /// <summary>The per-user folder (settings, accounts, profiles, logs, caches).</summary>
    public string UserDirectory { get; }

    /// <summary>The folder of the executable and the bundled assets.</summary>
    public string ProgramDirectory { get; }

    /// <summary>The options file in the user folder.</summary>
    public string OptionsFile => Path.Combine(UserDirectory, OptionsFileName);

    /// <summary>The options file beside the executable: the bundled defaults, and the file the C++ used on Windows.</summary>
    public string BundledOptionsFile => Path.Combine(ProgramDirectory, OptionsFileName);

    /// <summary>The 32-byte key of the saved accounts.</summary>
    public string AccountsKeyFile => Path.Combine(UserDirectory, "accounts.key");

    /// <summary>The encrypted saved accounts.</summary>
    public string AccountsDataFile => Path.Combine(UserDirectory, "accounts.dat");

    /// <summary>The index of the active account (a fallback the C++ also wrote).</summary>
    public string SelectedAccountFile => Path.Combine(UserDirectory, "selected-account");

    /// <summary>The list-server profiles.</summary>
    public string ListServersFile => Path.Combine(UserDirectory, "listservers.conf");

    /// <summary>The MCP audit log.</summary>
    public string McpAuditLogFile => Path.Combine(UserDirectory, "mcp-audit.log");

    /// <summary>Where MCP tools keep the previous version of what they overwrite or delete on a server.</summary>
    public string McpBackupDirectory => Path.Combine(UserDirectory, "mcp-backups");

    /// <summary>The MCP workspace: the local folder MCP tools may use by default (the option <c>mcpfileroots</c> is relative to it).</summary>
    public string McpWorkspaceDirectory => Path.Combine(UserDirectory, "mcp-files");

    /// <summary>The folder of the log files.</summary>
    public string LogsDirectory => Path.Combine(UserDirectory, "logs");

    /// <summary>The crash log (the C++ wrote <c>&lt;program&gt;/logs/crash.log</c>).</summary>
    public string CrashLogFile => Path.Combine(LogsDirectory, "crash.log");

    /// <summary>The program log that <see cref="AppLog"/> can append to.</summary>
    public string AppLogFile => Path.Combine(LogsDirectory, "remotecontrol.log");

    /// <summary>The folder of the private-message history files.</summary>
    public string PmHistoryDirectory => Path.Combine(UserDirectory, "PMs");

    /// <summary>The cache folder.</summary>
    public string CacheDirectory => Path.Combine(UserDirectory, "cache");

    /// <summary>The file-browser preview cache.</summary>
    public string PreviewCacheDirectory => Path.Combine(CacheDirectory, "previews");

    /// <summary>The script-editor cache.</summary>
    public string EditorCacheDirectory => Path.Combine(CacheDirectory, "editors");

    /// <summary>The root of the editor-text backups written by <see cref="EditorBackup"/>.</summary>
    public string BackupDirectory => Path.Combine(CacheDirectory, "backups");

    /// <summary>The bundled pictures.</summary>
    public string ImagesDirectory => Path.Combine(ProgramDirectory, "images");

    /// <summary>The bundled sounds.</summary>
    public string SoundsDirectory => Path.Combine(ProgramDirectory, "sounds");

    /// <summary>The bundled fonts.</summary>
    public string FontsDirectory => Path.Combine(ProgramDirectory, "fonts");

    /// <summary>The bundled syntax language files.</summary>
    public string LanguageSpecsDirectory => Path.Combine(ProgramDirectory, "language-specs");

    /// <summary>The GS2 language reference the MCP tools read (a copy of the GScript.GS2Taco documentation), beside the program.</summary>
    public string Gs2ReferenceDirectory => Path.Combine(ProgramDirectory, "gs2-reference");

    /// <summary>
    /// Resolves an option value that names a file or folder: an absolute value is kept, a relative one is taken relative to the
    /// user folder, and an empty one gives <paramref name="fallback"/> relative to the user folder.
    /// </summary>
    /// <param name="value">The option value.</param>
    /// <param name="fallback">The relative default used for an empty value.</param>
    /// <returns>A full path.</returns>
    public string ResolveUserPath(string? value, string fallback = "")
    {
        string chosen = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (chosen.Length == 0) return UserDirectory;
        if (chosen.StartsWith('~') && (chosen.Length == 1 || chosen[1] is '/' or '\\'))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrEmpty(home)) return Path.GetFullPath(Path.Combine(home, chosen.Length > 2 ? chosen[2..] : string.Empty));
        }
        return Path.IsPathRooted(chosen) ? Path.GetFullPath(chosen) : Path.GetFullPath(Path.Combine(UserDirectory, chosen));
    }

    /// <summary>The RC chat log file of <paramref name="options"/>.</summary>
    /// <param name="options">The current options.</param>
    /// <returns>A full path.</returns>
    public string ChatLogFile(RcOptions options) => ResolveUserPath(options.ChatLogFile, RcOptions.Defaults.ChatLogFile);

    /// <summary>The download folder of <paramref name="options"/>.</summary>
    /// <param name="options">The current options.</param>
    /// <returns>A full path.</returns>
    public string DownloadDirectory(RcOptions options) => ResolveUserPath(options.DownloadFolder, RcOptions.Defaults.DownloadFolder);

    /// <summary>The external-editor workspace of <paramref name="options"/>.</summary>
    /// <param name="options">The current options.</param>
    /// <returns>A full path.</returns>
    public string ExternalEditorDirectory(RcOptions options) => ResolveUserPath(options.ExternalEditorWorkspace, RcOptions.Defaults.ExternalEditorWorkspace);

    /// <summary>The PM history file of one account, named as the C++ named it (see <see cref="EscapeFileName"/>).</summary>
    /// <param name="account">The account name.</param>
    /// <returns>A full path inside <see cref="PmHistoryDirectory"/>.</returns>
    public string PmHistoryFile(string account) => Path.Combine(PmHistoryDirectory, EscapeFileName(account) + ".txt");

    /// <summary>
    /// The background picture of <paramref name="options"/>: an absolute value as it is, otherwise inside <see cref="ImagesDirectory"/>.
    /// </summary>
    /// <param name="options">The current options.</param>
    /// <returns>A full path.</returns>
    public string BackgroundFile(RcOptions options)
    {
        string value = options.Background;
        return Path.IsPathRooted(value) ? value : Path.Combine(ImagesDirectory, value);
    }

    /// <summary>
    /// Finds a bundled picture the way <c>resolveRuntimeImage</c> did: <c>&lt;program&gt;/images</c>, <c>$APPDIR/images</c> (AppImage),
    /// <c>&lt;program&gt;/../images</c>, then <c>&lt;working folder&gt;/images</c>; the first candidate when none exists.
    /// </summary>
    /// <param name="name">The file name of the picture.</param>
    /// <returns>A full path.</returns>
    public string ResolveImage(string name)
    {
        var candidates = new List<string> { Path.Combine(ImagesDirectory, name) };
        string? appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (!string.IsNullOrEmpty(appDir)) candidates.Add(Path.Combine(appDir, "images", name));
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(ProgramDirectory));
        if (!string.IsNullOrEmpty(parent)) candidates.Add(Path.Combine(parent, "images", name));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "images", name));
        foreach (string candidate in candidates) if (File.Exists(candidate)) return candidate;
        return candidates[0];
    }

    /// <summary>
    /// Escapes a name for use as a file name, as the C++ did for PM history and editor backups: ASCII letters and digits are
    /// kept, every other UTF-8 byte becomes <c>%</c> followed by its decimal value in three digits (a space is <c>%032</c>).
    /// </summary>
    /// <param name="value">The name.</param>
    /// <returns>The escaped name.</returns>
    public static string EscapeFileName(string value)
    {
        var result = new StringBuilder();
        foreach (byte character in Encoding.UTF8.GetBytes(value ?? string.Empty))
        {
            if (character is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z') result.Append((char)character);
            else result.Append('%').Append(character.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }
}
