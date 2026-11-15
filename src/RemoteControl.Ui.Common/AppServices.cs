using Avalonia;
using RemoteControl.Core;

namespace RemoteControl.Ui.Common;

/// <summary>How <see cref="AppServices.Create"/> sets the services up.</summary>
public sealed record AppServicesSettings
{
    /// <summary>Whether sounds are played (off in tests).</summary>
    public bool EnableSounds { get; init; } = true;

    /// <summary>Whether the program log is appended to <see cref="AppPaths.AppLogFile"/>.</summary>
    public bool LogToFile { get; init; } = true;

    /// <summary>The log to use; <see cref="AppLog.Default"/> when <c>null</c>.</summary>
    public AppLog? Log { get; init; }

    /// <summary>Whether the theme is installed into <see cref="Application.Current"/> at once (when there is one).</summary>
    public bool InstallTheme { get; init; } = true;

    /// <summary>Whether <see cref="UiSafe"/> is configured with this log and crash log.</summary>
    public bool ConfigureUiSafe { get; init; } = true;
}

/// <summary>The outcome of <see cref="AppServices.Create"/>: the services and what the user should be told.</summary>
/// <param name="Services">The services, always usable (with defaults where a file could not be read).</param>
/// <param name="Problems">Sentences for the user: options, accounts or profiles that could not be read (they were kept aside), folders that could not be created.</param>
public sealed record AppServicesResult(AppServices Services, IReadOnlyList<string> Problems);

/// <summary>
/// What every window needs, created once at start-up and handed to every window: the paths, the one options store, the saved
/// accounts, the list-server profiles, the theme, pictures, sounds, links, the log and the crash log. No session, no network: the
/// session context is added on top of this by the program.
/// </summary>
/// <example>
/// <code>
/// AppServicesResult created = AppServices.Create(AppPaths.ForCurrentUser());
/// AppServices services = created.Services;
/// foreach (string problem in created.Problems) await Dialogs.ShowErrorAsync("Remote Control", problem);
/// var login = new LoginWindow(services);   // a RcWindow
/// login.ShowAndActivate();
/// </code>
/// </example>
public sealed class AppServices : IDisposable
{
    private bool _disposed;

    private AppServices(AppPaths paths, OptionsStore options, AccountStore accounts, ListServerProfileStore profiles, ThemeManager theme, AssetStore assets, SoundPlayer sounds, UrlOpener urls, AppLog log, CrashLog crashLog)
    {
        Paths = paths;
        Options = options;
        Accounts = accounts;
        Profiles = profiles;
        Theme = theme;
        Assets = assets;
        Sounds = sounds;
        Urls = urls;
        Log = log;
        CrashLog = crashLog;
    }

    /// <summary>Where the program's files are.</summary>
    public AppPaths Paths { get; }

    /// <summary>The one options store (read <see cref="OptionsStore.Current"/>, change through <see cref="OptionsStore.Update"/>).</summary>
    public OptionsStore Options { get; }

    /// <summary>The saved accounts.</summary>
    public AccountStore Accounts { get; }

    /// <summary>The list-server profiles.</summary>
    public ListServerProfileStore Profiles { get; }

    /// <summary>The theme of the application.</summary>
    public ThemeManager Theme { get; }

    /// <summary>The bundled pictures.</summary>
    public AssetStore Assets { get; }

    /// <summary>Sound playback.</summary>
    public SoundPlayer Sounds { get; }

    /// <summary>Opens links.</summary>
    public UrlOpener Urls { get; }

    /// <summary>The program log.</summary>
    public AppLog Log { get; }

    /// <summary>The crash log.</summary>
    public CrashLog CrashLog { get; }

    /// <summary>
    /// Creates the services: makes the user folder and its log folder, reads the options, the accounts and the profiles (each
    /// keeps an unreadable file aside and uses defaults, see the stores), and installs the theme when an application is running.
    /// Never throws for a file problem: the problems are returned for the user.
    /// </summary>
    /// <param name="paths">The folders (in tests, temporary ones).</param>
    /// <param name="settings">How to set up; the defaults when <c>null</c>.</param>
    /// <returns>The services and the problems to show.</returns>
    public static AppServicesResult Create(AppPaths paths, AppServicesSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        settings ??= new AppServicesSettings();
        AppLog log = settings.Log ?? AppLog.Default;
        var problems = new List<string>();

        try
        {
            Directory.CreateDirectory(paths.UserDirectory);
            Directory.CreateDirectory(paths.LogsDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problems.Add($"The folder {paths.UserDirectory} could not be created ({exception.Message}). Settings may not be saved.");
            log.Warn("The user folder could not be created.", exception);
        }
        if (settings.LogToFile) log.FilePath = paths.AppLogFile;
        var crashLog = new CrashLog(paths.CrashLogFile, log);

        var options = new OptionsStore(paths);
        Guard(problems, log, "options", () =>
        {
            OptionsLoadResult result = options.Load();
            if (result.NeedsAttention && result.Message is not null) problems.Add(result.Message);
        });

        var accounts = new AccountStore(paths);
        Guard(problems, log, "saved accounts", () =>
        {
            AccountLoadResult result = accounts.Load();
            if (result.NeedsAttention) problems.Add(result.Message ?? "The saved accounts could not be read.");
        });

        var profiles = new ListServerProfileStore(paths);
        Guard(problems, log, "list-server profiles", () =>
        {
            ProfileLoadResult result = profiles.Load();
            if (result.NeedsAttention) problems.Add(result.Message ?? "The list-server profiles could not be read.");
        });

        var theme = new ThemeManager(options, log);
        var assets = new AssetStore(paths, log);
        var sounds = new SoundPlayer(paths, log, settings.EnableSounds);
        var urls = new UrlOpener(options, log);
        var services = new AppServices(paths, options, accounts, profiles, theme, assets, sounds, urls, log, crashLog);

        if (settings.ConfigureUiSafe) UiSafe.Configure(log, crashLog);
        if (settings.InstallTheme && Application.Current is { } application)
        {
            Guard(problems, log, "theme", () => theme.Install(application));
            Guard(problems, log, "application icon", () => RcWindow.DefaultIcon = assets.AppIcon);
        }
        return new AppServicesResult(services, problems);
    }

    /// <summary>Stops the theme from following the options (the stores need no disposing).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Theme.Dispose();
    }

    private static void Guard(List<string> problems, AppLog log, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            log.Error("Loading the " + what + " failed.", exception);
            problems.Add($"The {what} could not be loaded: {exception.Message}");
        }
    }
}
