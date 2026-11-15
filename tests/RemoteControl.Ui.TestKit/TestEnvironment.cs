using Avalonia;
using Avalonia.Controls;
using RemoteControl.Core;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.TestKit;

/// <summary>
/// One test's world: a temporary user folder (deleted on <see cref="Dispose"/>), the program folder pointing at the repository's
/// <c>assets/runtime</c> (read only), and an <see cref="AppServices"/> over them with sounds off, an in-memory log and the theme
/// installed into the headless application. Windows opened during the test are closed on <see cref="Dispose"/>.
/// </summary>
/// <example>
/// <code>
/// using var env = new TestEnvironment(theme: "dracula", options: o =&gt; o with { RcTimestamps = false });
/// var window = env.Show(new OptionsWindow(env.Services), 400, 420);
/// env.SetOptions(o =&gt; o with { ChatFontSize = 14 });
/// Assert.Contains("refused", env.Log.Snapshot().Last());
/// </code>
/// </example>
public sealed class TestEnvironment : IDisposable
{
    private static string? s_repositoryRoot;
    private readonly HashSet<RcWindow> _existing;
    private readonly List<Window> _shown = new();
    private bool _disposed;

    /// <summary>Creates the environment.</summary>
    /// <param name="theme">A theme id to apply, or <c>null</c> for the bundled options' theme.</param>
    /// <param name="options">A change applied to the options after they were read, or <c>null</c>.</param>
    public TestEnvironment(string? theme = null, Func<RcOptions, RcOptions>? options = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "rc-ui-tests", Guid.NewGuid().ToString("N"));
        UserDirectory = Path.Combine(Root, "user");
        Directory.CreateDirectory(UserDirectory);
        ProgramDirectory = Path.Combine(RepositoryRoot, "assets", "runtime");
        Paths = new AppPaths(UserDirectory, ProgramDirectory);
        Log = new AppLog(2000);
        _existing = new HashSet<RcWindow>(RcWindow.OpenWindows);
        AppServicesResult created = AppServices.Create(Paths, new AppServicesSettings { EnableSounds = false, LogToFile = false, Log = Log });
        Services = created.Services;
        Problems = created.Problems;
        UiSafe.ResetStatistics();
        if (theme is not null || options is not null)
        {
            Services.Options.Update(current =>
            {
                RcOptions next = options?.Invoke(current) ?? current;
                return theme is null ? next : next with { Theme = theme, DarkMode = theme != "light" };
            });
        }
        Services.Theme.Apply();
        TestUi.Settle();
    }

    /// <summary>The repository folder (the one with <c>RemoteControl.sln</c>), found by walking up from the test binaries.</summary>
    public static string RepositoryRoot => s_repositoryRoot ??= FindRepositoryRoot();

    /// <summary>The temporary folder of this test.</summary>
    public string Root { get; }

    /// <summary>The temporary user folder.</summary>
    public string UserDirectory { get; }

    /// <summary>The repository's <c>assets/runtime</c> folder.</summary>
    public string ProgramDirectory { get; }

    /// <summary>The paths over both folders.</summary>
    public AppPaths Paths { get; }

    /// <summary>The services of the test.</summary>
    public AppServices Services { get; }

    /// <summary>What <see cref="AppServices.Create"/> reported (normally nothing).</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>The in-memory log of the test.</summary>
    public AppLog Log { get; }

    /// <summary>The current options.</summary>
    public RcOptions Options => Services.Options.Current;

    /// <summary>Changes options (through the store, so windows and the theme follow) and lets the UI settle.</summary>
    /// <param name="change">The change.</param>
    public void SetOptions(Func<RcOptions, RcOptions> change)
    {
        Services.Options.Update(change);
        TestUi.Settle();
    }

    /// <summary>Shows a window at a size, lets it lay out and remembers it to close it on <see cref="Dispose"/>.</summary>
    /// <typeparam name="T">The window type.</typeparam>
    /// <param name="window">The window.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns>The same window.</returns>
    public T Show<T>(T window, double width = 800, double height = 600) where T : Window
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Width = width;
        window.Height = height;
        _shown.Add(window);
        window.Show();
        TestUi.Settle();
        return window;
    }

    /// <summary>Closes the windows of the test, stops the theme following the options and deletes the temporary folder.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (Window window in _shown) CloseQuietly(window);
        foreach (RcWindow window in RcWindow.OpenWindows)
            if (!_existing.Contains(window)) CloseQuietly(window);
        TestUi.Settle();
        Services.Dispose();
        UiSafe.Configure(AppLog.Default, null);
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // A file still open (a crash log being written) is left for the system's temporary-file cleanup.
        }
    }

    private static void CloseQuietly(Window window)
    {
        try
        {
            window.Close();
        }
        catch (Exception)
        {
            // Closing a window that never opened is harmless.
        }
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "RemoteControl.sln"))) return folder.FullName;
        throw new InvalidOperationException("The repository (the folder with RemoteControl.sln) was not found above " + AppContext.BaseDirectory);
    }
}
