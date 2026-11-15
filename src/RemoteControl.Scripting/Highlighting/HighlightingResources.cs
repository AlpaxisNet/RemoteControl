namespace RemoteControl.Scripting;

/// <summary>The highlighting languages the editors use.</summary>
public enum HighlightingLanguage
{
    /// <summary>Plain text.</summary>
    None,

    /// <summary>GS2 scripts (<c>Graal.xshd</c>).</summary>
    Graal,

    /// <summary>Key/value text such as flags and server options (<c>Ini.xshd</c>).</summary>
    Ini,
}

/// <summary>What an editor window edits, to choose its highlighting.</summary>
public enum EditorContentKind
{
    /// <summary>A weapon or GUI script.</summary>
    WeaponScript,

    /// <summary>A class script.</summary>
    ClassScript,

    /// <summary>An NPC script.</summary>
    NpcScript,

    /// <summary>The flags of an NPC.</summary>
    NpcFlags,

    /// <summary>The attributes of an NPC (read-only).</summary>
    NpcAttributes,

    /// <summary>The server options.</summary>
    ServerOptions,

    /// <summary>The server flags.</summary>
    ServerFlags,

    /// <summary>The folder configuration.</summary>
    FolderConfig,

    /// <summary>The level list.</summary>
    LevelList,
}

/// <summary>One highlighting colour of an xshd file and the style-scheme styles it takes its look from.</summary>
/// <param name="Name">The colour name in the xshd file (identifier-safe).</param>
/// <param name="LanguageStyle">The style name the GtkSourceView language file used, for example <c>Others 4</c>.</param>
/// <param name="SchemeStyles">
/// Scheme styles tried in order: the <c>def:</c> style GtkSourceView mapped the v1 name to (when it did), the v1 name itself, then
/// a more general <c>def:</c> fallback.
/// </param>
public sealed record HighlightingColorName(string Name, string LanguageStyle, IReadOnlyList<string> SchemeStyles);

/// <summary>The embedded xshd files and which editor uses which.</summary>
public static class HighlightingResources
{
    private const string GraalResource = "RemoteControl.Scripting.Highlighting.Graal.xshd";
    private const string IniResource = "RemoteControl.Scripting.Highlighting.Ini.xshd";

    /// <summary>The colours of <c>Graal.xshd</c>.</summary>
    public static IReadOnlyList<HighlightingColorName> GraalColors { get; } = new[]
    {
        new HighlightingColorName("Comment", "Comment", new[] { "def:comment", "Comment" }),
        new HighlightingColorName("String", "String", new[] { "def:string", "String" }),
        new HighlightingColorName("Decimal", "Decimal", new[] { "def:decimal", "Decimal", "def:number" }),
        new HighlightingColorName("Keyword", "Keyword", new[] { "def:keyword", "Keyword", "def:statement" }),
        new HighlightingColorName("Others", "Others", new[] { "Others" }),
        new HighlightingColorName("Others4", "Others 4", new[] { "Others 4" }),
        new HighlightingColorName("Others5", "Others 5", new[] { "Others 5" }),
        new HighlightingColorName("Operators", "Operators", new[] { "Operators", "def:operator" }),
        new HighlightingColorName("ObjectProperties", "Object Properties", new[] { "Object Properties" }),
        new HighlightingColorName("Function", "Function", new[] { "def:function", "Function", "def:identifier" }),
    };

    /// <summary>The colours of <c>Ini.xshd</c>.</summary>
    public static IReadOnlyList<HighlightingColorName> IniColors { get; } = new[]
    {
        new HighlightingColorName("Comment3", "Comment 3", new[] { "Comment 3", "def:comment" }),
        new HighlightingColorName("Comment2", "Comment 2", new[] { "Comment 2" }),
        new HighlightingColorName("DataType", "Data Type", new[] { "def:type", "Data Type" }),
        new HighlightingColorName("Decimal", "Decimal", new[] { "def:decimal", "Decimal", "def:number" }),
        new HighlightingColorName("Preprocessor", "Preprocessor", new[] { "def:preprocessor", "Preprocessor" }),
        new HighlightingColorName("String", "String", new[] { "def:string", "String" }),
        new HighlightingColorName("FloatingPoint", "Floating Point", new[] { "def:floating-point", "Floating Point", "def:number" }),
    };

    /// <summary>Every colour of both files, without repeating a name.</summary>
    public static IReadOnlyList<HighlightingColorName> AllColors { get; } = GraalColors.Concat(IniColors).GroupBy(color => color.Name).Select(group => group.First()).ToArray();

    /// <summary>Opens <c>Graal.xshd</c>; the caller disposes the stream.</summary>
    /// <returns>The stream.</returns>
    public static Stream OpenGraal() => OpenResource(GraalResource);

    /// <summary>Opens <c>Ini.xshd</c>; the caller disposes the stream.</summary>
    /// <returns>The stream.</returns>
    public static Stream OpenIni() => OpenResource(IniResource);

    /// <summary>Opens the xshd of a language.</summary>
    /// <param name="language">The language.</param>
    /// <returns>The stream, or null for <see cref="HighlightingLanguage.None"/>.</returns>
    public static Stream? Open(HighlightingLanguage language) => language switch
    {
        HighlightingLanguage.Graal => OpenGraal(),
        HighlightingLanguage.Ini => OpenIni(),
        _ => null,
    };

    /// <summary>The colours of a language's xshd.</summary>
    /// <param name="language">The language.</param>
    /// <returns>The colours (empty for none).</returns>
    public static IReadOnlyList<HighlightingColorName> ColorsOf(HighlightingLanguage language) => language switch
    {
        HighlightingLanguage.Graal => GraalColors,
        HighlightingLanguage.Ini => IniColors,
        _ => Array.Empty<HighlightingColorName>(),
    };

    /// <summary>The language of a file in the file browser's text editor: <c>.graal</c> and <c>.nw</c> are GS2, <c>.ini</c> and <c>.conf</c> INI.</summary>
    /// <param name="path">The file name or path.</param>
    /// <returns>The language.</returns>
    public static HighlightingLanguage LanguageForFile(string? path)
    {
        string extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".graal" or ".nw" => HighlightingLanguage.Graal,
            ".ini" or ".conf" => HighlightingLanguage.Ini,
            _ => HighlightingLanguage.None,
        };
    }

    /// <summary>The language of an editor window: scripts are GS2; flags, options and folder configuration INI; the rest plain.</summary>
    /// <param name="kind">What the window edits.</param>
    /// <returns>The language.</returns>
    public static HighlightingLanguage LanguageForEditor(EditorContentKind kind) => kind switch
    {
        EditorContentKind.WeaponScript or EditorContentKind.ClassScript or EditorContentKind.NpcScript => HighlightingLanguage.Graal,
        EditorContentKind.NpcFlags or EditorContentKind.ServerOptions or EditorContentKind.ServerFlags or EditorContentKind.FolderConfig => HighlightingLanguage.Ini,
        _ => HighlightingLanguage.None,
    };

    private static Stream OpenResource(string name) =>
        typeof(HighlightingResources).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing embedded resource " + name);
}
