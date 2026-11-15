using RemoteControl.Scripting;

namespace RemoteControl.Ui.Session;

/// <summary>
/// The local script help of <c>/scripthelp2 [query]</c> (<c>requestGScriptHelp</c>, <c>formatScriptHelp</c>, <c>TGScriptEditor.cpp:1667-1689, 2228-2238</c>):
/// the script API definitions are loaded once (from the <c>autocompletesource</c> option, cached in the editor cache folder) off the UI
/// thread and searched by name.
/// </summary>
internal static class ScriptHelp
{
    private static readonly object s_gate = new();
    private static Task<IReadOnlyList<ApiDefinition>>? s_loading;

    /// <summary>Replaces how the definitions are loaded (tests); null restores the default.</summary>
    internal static Func<CancellationToken, Task<IReadOnlyList<ApiDefinition>>>? Source { get; set; }

    /// <summary>Forgets the loaded definitions (tests).</summary>
    internal static void Reset()
    {
        lock (s_gate) s_loading = null;
    }

    /// <summary>Searches the definitions.</summary>
    /// <param name="source">The <c>autocompletesource</c> option.</param>
    /// <param name="cacheFile">The cache file.</param>
    /// <param name="query">What to look for.</param>
    /// <param name="cancellationToken">Cancels the wait (the window closed).</param>
    /// <returns>The chat lines.</returns>
    public static async Task<IReadOnlyList<string>> SearchAsync(string source, string cacheFile, string query, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<ApiDefinition>> loading;
        lock (s_gate)
        {
            loading = s_loading ??= Load(source, cacheFile);
        }
        IReadOnlyList<ApiDefinition> definitions = await loading.WaitAsync(cancellationToken).ConfigureAwait(true);
        if (definitions.Count == 0)
        {
            lock (s_gate)
            {
                if (ReferenceEquals(s_loading, loading)) s_loading = null;
            }
        }
        return Format(definitions, query);
    }

    /// <summary>The lines the C++ showed for a query.</summary>
    /// <param name="definitions">The definitions.</param>
    /// <param name="query">The query.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> Format(IReadOnlyList<ApiDefinition> definitions, string query)
    {
        string needle = query.ToLowerInvariant();
        List<ApiDefinition> matches = definitions.Where(definition => needle.Length == 0 || definition.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return ["[Script Help] No API entries match '" + query + "'."];
        ApiDefinition? exact = matches.FirstOrDefault(definition => string.Equals(definition.Name, needle, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            var lines = new List<string> { "[Script Help] " + exact.Signature + Scope(exact) };
            if (exact.Returns.Length > 0) lines.Add("Returns: " + exact.Returns);
            if (exact.Description.Length > 0) lines.Add(exact.Description);
            if (exact.Example.Length > 0) lines.Add("Example: " + exact.Example);
            return lines;
        }
        var result = new List<string> { "[Script Help] " + matches.Count + " entries matching '" + query + "':" };
        foreach (ApiDefinition definition in matches.Take(30))
            result.Add(definition.Signature + Scope(definition) + (definition.Description.Length == 0 ? string.Empty : " - " + definition.Description));
        if (matches.Count > 30) result.Add("Use a longer /scripthelp2 query to narrow the result.");
        return result;
    }

    private static string Scope(ApiDefinition definition) => definition.Scope.Length == 0 ? string.Empty : " [" + definition.Scope.ToUpperInvariant() + "]";

    private static Task<IReadOnlyList<ApiDefinition>> Load(string source, string cacheFile)
    {
        Func<CancellationToken, Task<IReadOnlyList<ApiDefinition>>>? custom = Source;
        if (custom is not null) return custom(CancellationToken.None);
        return Task.Run<IReadOnlyList<ApiDefinition>>(async () =>
        {
            ApiLoadResult result = await new ApiDefinitionLoader().LoadAsync(source, cacheFile).ConfigureAwait(false);
            return result.Definitions.Definitions;
        });
    }
}
