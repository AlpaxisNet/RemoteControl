namespace RemoteControl.Scripting;

/// <summary>
/// One entry of the script API (a function, class, property, event ...) with the display texts the completion popup, the hover
/// popover and the signature hint show. Immutable; safe to share between threads.
/// </summary>
public sealed class ApiDefinition
{
    /// <summary>Creates a definition and computes its display fields (C++ <c>prepareCompletionDefinition</c>).</summary>
    /// <param name="name">The name, for example <c>setTimer</c>.</param>
    /// <param name="type">The kind as the API names it (<c>function</c>, <c>class</c>, <c>property</c> ...); may be empty.</param>
    /// <param name="parameters">Parameter texts in order.</param>
    /// <param name="returns">What it returns; may be empty.</param>
    /// <param name="scope">Where it runs (<c>script</c>, <c>client</c>, <c>server</c> ...); may be empty.</param>
    /// <param name="description">Description; may be empty.</param>
    /// <param name="example">Example code; may be empty.</param>
    public ApiDefinition(string name, string? type = null, IEnumerable<string>? parameters = null, string? returns = null, string? scope = null,
        string? description = null, string? example = null)
    {
        Name = name ?? string.Empty;
        Type = type ?? string.Empty;
        Parameters = (parameters ?? Array.Empty<string>()).Where(parameter => parameter != null).ToArray();
        Returns = returns ?? string.Empty;
        Scope = scope ?? string.Empty;
        Description = description ?? string.Empty;
        Example = example ?? string.Empty;
        SearchName = Name.ToLowerInvariant();

        string parameterList = string.Join(", ", Parameters);
        Signature = Name + "(" + parameterList + ")";
        string label = Parameters.Count == 0 ? Name : Signature;
        CompletionLabel = label.Length > 84 ? label.Substring(0, 81) + "..." : label;

        string info = Type;
        if (Scope.Length > 0) info += (info.Length == 0 ? "" : "  ") + Scope.ToUpperInvariant();
        if (Parameters.Count > 0) info += (info.Length == 0 ? "" : "\n") + "Parameters: " + parameterList;
        if (Returns.Length > 0) info += (info.Length == 0 ? "" : "\n") + "Returns: " + Returns;
        CompletionInfo = info;

        string detail = Description;
        if (detail.StartsWith(Name + "(", StringComparison.Ordinal))
        {
            int dash = detail.IndexOf(" - ", StringComparison.Ordinal);
            if (dash >= 0) detail = detail.Substring(dash + 3);
        }
        string summary = Signature;
        if (Returns.Length > 0 && !detail.Contains("return", StringComparison.OrdinalIgnoreCase)) summary += "  →  " + Returns;
        if (detail.Length > 0) summary += "\n" + detail;
        CompletionSummary = summary;

        string lowerScope = Scope.ToLowerInvariant();
        ScopeLabel = lowerScope switch
        {
            "script" => "GLOBAL",
            "client" => "CLIENTSIDE",
            "server" => "SERVERSIDE",
            _ => Scope.ToUpperInvariant(),
        };

        string full = info;
        if (Description.Length > 0) full += (full.Length == 0 ? "" : "\n") + Description;
        if (Example.Length > 0) full += "\n\nExample:\n" + Example;
        Info = full;
    }

    /// <summary>The name.</summary>
    public string Name { get; }

    /// <summary>The kind as the API names it; may be empty.</summary>
    public string Type { get; }

    /// <summary>Parameter texts in order.</summary>
    public IReadOnlyList<string> Parameters { get; }

    /// <summary>What it returns; may be empty.</summary>
    public string Returns { get; }

    /// <summary>Where it runs; may be empty.</summary>
    public string Scope { get; }

    /// <summary>Description; may be empty.</summary>
    public string Description { get; }

    /// <summary>Example code; may be empty.</summary>
    public string Example { get; }

    /// <summary>The lower-case name, the key for sorting, lookup and duplicate removal.</summary>
    public string SearchName { get; }

    /// <summary><c>name(p1, p2)</c> (always with parentheses), the bold line of the hover and signature popovers.</summary>
    public string Signature { get; }

    /// <summary>The completion list label: the name, with the parameter list when there is one; longer than 84 characters is cut to 81 plus "...".</summary>
    public string CompletionLabel { get; }

    /// <summary>The completion detail text: <c>type  SCOPE</c>, <c>Parameters: ...</c>, <c>Returns: ...</c> on separate lines.</summary>
    public string CompletionInfo { get; }

    /// <summary>The preview strip text: <c>name(params)  →  returns</c> and the description (a leading <c>name(...) - </c> removed).</summary>
    public string CompletionSummary { get; }

    /// <summary>The scope column: script → GLOBAL, client → CLIENTSIDE, server → SERVERSIDE, else the scope in capitals.</summary>
    public string ScopeLabel { get; }

    /// <summary>The hover text under the signature: completion info, description and <c>\n\nExample:\n...</c> (C++ <c>definitionInfo</c>).</summary>
    public string Info { get; }

    /// <summary>Whether the signature hint has anything to show (parameters, return value, description or example).</summary>
    public bool HasSignatureDetails => Parameters.Count > 0 || Returns.Length > 0 || Description.Length > 0 || Example.Length > 0;

    /// <inheritdoc />
    public override string ToString() => Signature;
}

/// <summary>
/// The API definitions the editor knows: the loaded ones plus the built-in Gui classes, sorted by lower-case name, one per name.
/// Immutable; a new set replaces the old one when loading finishes.
/// </summary>
public sealed class ApiDefinitionSet
{
    private static readonly string[] GuiClasses =
    {
        "GuiControl", "GuiMLTextCtrl", "GuiScrollCtrl", "GuiTextCtrl", "GuiTextEditCtrl", "GuiTextListCtrl", "GuiArrayCtrl", "GuiButtonCtrl",
        "GuiBitmapCtrl", "GuiBitmapButtonCtrl", "GuiCheckBoxCtrl", "GuiContainer", "GuiWindowCtrl", "GuiCanvas", "GuiPopUpMenuCtrl", "GuiShowImgCtrl",
        "GuiStretchCtrl", "GuiProgressCtrl", "GuiRadioCtrl",
    };

    private readonly ApiDefinition[] definitions;

    private ApiDefinitionSet(ApiDefinition[] definitions, bool remoteLoaded)
    {
        this.definitions = definitions;
        RemoteLoaded = remoteLoaded;
    }

    /// <summary>The built-in additions merged into every set: the Gui control classes (parameter <c>name</c>) and the property <c>visible</c>.</summary>
    public static IReadOnlyList<ApiDefinition> BuiltInDefinitions { get; } = GuiClasses
        .Select(name => new ApiDefinition(name, "class", new[] { "name" }))
        .Append(new ApiDefinition("visible", "property"))
        .ToArray();

    /// <summary>The set before anything has loaded: the built-in definitions only.</summary>
    public static ApiDefinitionSet BuiltInOnly { get; } = Create(Array.Empty<ApiDefinition>());

    /// <summary>Whether the definitions came from the API source or its cache (false: built-ins only). Unknown functions are only reported when true.</summary>
    public bool RemoteLoaded { get; }

    /// <summary>All definitions, sorted by <see cref="ApiDefinition.SearchName"/> (ordinal).</summary>
    public IReadOnlyList<ApiDefinition> Definitions => definitions;

    /// <summary>Builds a set from loaded definitions: duplicates (same lower-case name) keep the first, built-ins are added when missing.</summary>
    /// <param name="loaded">The loaded definitions.</param>
    /// <returns>The set; <see cref="RemoteLoaded"/> is true when <paramref name="loaded"/> was not empty.</returns>
    public static ApiDefinitionSet Create(IEnumerable<ApiDefinition> loaded)
    {
        var byName = new Dictionary<string, ApiDefinition>(StringComparer.Ordinal);
        bool any = false;
        foreach (ApiDefinition definition in loaded ?? Array.Empty<ApiDefinition>())
        {
            if (definition == null || definition.Name.Length == 0) continue;
            any = true;
            byName.TryAdd(definition.SearchName, definition);
        }
        foreach (ApiDefinition definition in BuiltInDefinitions) byName.TryAdd(definition.SearchName, definition);
        ApiDefinition[] sorted = byName.Values.OrderBy(definition => definition.SearchName, StringComparer.Ordinal).ToArray();
        return new ApiDefinitionSet(sorted, any);
    }

    /// <summary>The definition with exactly this name (case-insensitive), or null.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The definition or null.</returns>
    public ApiDefinition? Find(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string key = name.ToLowerInvariant();
        int low = 0, high = definitions.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (string.CompareOrdinal(definitions[middle].SearchName, key) < 0) low = middle + 1; else high = middle;
        }
        return low < definitions.Length && definitions[low].SearchName == key ? definitions[low] : null;
    }

    /// <summary>
    /// The definition the hover and signature hint show for a word (C++ <c>findEditorDefinition</c>): the exact name, else the
    /// part after the last '.', else a function of the current script.
    /// </summary>
    /// <param name="name">The word or dotted name.</param>
    /// <param name="localFunctions">Functions of the current script, or null.</param>
    /// <returns>The definition or null.</returns>
    public ApiDefinition? Resolve(string? name, IReadOnlyList<ApiDefinition>? localFunctions)
    {
        if (string.IsNullOrEmpty(name)) return null;
        ApiDefinition? found = Find(name);
        if (found != null) return found;
        int separator = name.LastIndexOf('.');
        if (separator >= 0)
        {
            found = Find(name.Substring(separator + 1));
            if (found != null) return found;
        }
        if (localFunctions == null) return null;
        string key = name.ToLowerInvariant();
        foreach (ApiDefinition definition in localFunctions)
        {
            if (definition.SearchName == key) return definition;
        }
        return null;
    }
}
