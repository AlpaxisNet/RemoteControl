using RemoteControl.Core;
using RemoteControl.Scripting;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Scripts;

/// <summary>
/// The script API definitions for autocomplete, hover, signature help and the diagnostics, loaded once per program and source
/// (C++ <c>startCompletionLoad</c>): the first editor starts the load, every editor shares the result, a change of the
/// <c>autocompletesource</c> option or turning <c>lsp</c> on loads again. Used on the UI thread; the load runs in the background.
/// </summary>
internal static class ApiDefinitionCatalog
{
    private static readonly ApiDefinitionLoader s_loader = new();
    private static string? s_source;
    private static Task<ApiDefinitionSet>? s_load;
    private static ApiDefinitionSet? s_loaded;

    /// <summary>
    /// What loads the definitions (the source option, the cache file, cancellation); the real loader by default. Tests replace it
    /// so that no editor ever reaches the network.
    /// </summary>
    internal static Func<string, string?, CancellationToken, Task<ApiLoadResult>> Load { get; set; } =
        (source, cache, token) => s_loader.LoadAsync(source, cache, token);

    /// <summary>Raised on the UI thread when a load finished; editors refresh their completion data and diagnostics.</summary>
    public static event EventHandler? Loaded;

    /// <summary>The definitions loaded so far: the built-in ones until the load has finished.</summary>
    public static ApiDefinitionSet Current => s_loaded ?? ApiDefinitionSet.BuiltInOnly;

    /// <summary>Whether a load for the current source has finished.</summary>
    public static bool IsLoaded => s_loaded is not null;

    /// <summary>Starts the load for the options' source unless it is running or done (nothing when completion is off).</summary>
    /// <param name="services">The program's services (options and the cache folder).</param>
    public static void Ensure(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        RcOptions options = services.Options.Current;
        if (!options.Lsp) return;
        string source = string.IsNullOrWhiteSpace(options.AutocompleteSource) ? ApiDefinitionLoader.DefaultSource : options.AutocompleteSource.Trim();
        if (s_load is not null && string.Equals(s_source, source, StringComparison.Ordinal)) return;
        s_source = source;
        s_loaded = null;
        string cache = Path.Combine(services.Paths.EditorCacheDirectory, "scriptapi.json");
        Task<ApiDefinitionSet> load = Run(source, cache);
        s_load = load;
        UiSafe.Fire(async () =>
        {
            ApiDefinitionSet set = await load.ConfigureAwait(true);
            if (!ReferenceEquals(s_load, load)) return;
            s_loaded = set;
            Loaded?.Invoke(null, EventArgs.Empty);
        }, "load the script API");
    }

    /// <summary>Waits for the running load (tests, <c>/scripthelp</c>-like callers).</summary>
    /// <returns>The definitions.</returns>
    public static Task<ApiDefinitionSet> WhenLoadedAsync() => s_load ?? Task.FromResult(Current);

    /// <summary>Forgets what was loaded (tests).</summary>
    internal static void Reset()
    {
        s_source = null;
        s_load = null;
        s_loaded = null;
    }

    private static async Task<ApiDefinitionSet> Run(string source, string cache)
    {
        try
        {
            ApiLoadResult result = await Task.Run(() => Load(source, cache, CancellationToken.None)).ConfigureAwait(false);
            if (result.SourceError is not null) UiSafe.Log.Warn("Script API: " + result.SourceError + " (" + result.Origin + ").");
            return result.Definitions;
        }
        catch (Exception exception)
        {
            UiSafe.Log.Warn("The script API could not be loaded.", exception);
            return ApiDefinitionSet.BuiltInOnly;
        }
    }
}
