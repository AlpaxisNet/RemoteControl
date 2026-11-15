namespace RemoteControl.Scripting;

/// <summary>How serious a script diagnostic is. The order matters: a higher value is more serious.</summary>
public enum DiagnosticSeverity
{
    /// <summary>A hint, shown with a single blue underline.</summary>
    Info = 0,

    /// <summary>Probably a mistake, shown with an amber squiggle.</summary>
    Warning = 1,

    /// <summary>The script does not parse, shown with a red squiggle.</summary>
    Error = 2,
}

/// <summary>One finding of the script analysis.</summary>
/// <param name="Severity">How serious the finding is.</param>
/// <param name="Start">First character of the marked range, as an index into the analysed <see cref="string"/> (UTF-16 code units).</param>
/// <param name="Length">Length of the marked range in UTF-16 code units; at least 1 unless the text is empty. Never splits a surrogate pair.</param>
/// <param name="Message">The text shown to the user.</param>
public sealed record Diagnostic(DiagnosticSeverity Severity, int Start, int Length, string Message)
{
    /// <summary>Index just after the marked range.</summary>
    public int End => Start + Length;

    /// <summary>The line the hover popover shows for this diagnostic, for example <c>Script analysis — Error: Missing ';'</c>.</summary>
    public string HoverText => Severity switch
    {
        DiagnosticSeverity.Error => "Script analysis — Error: ",
        DiagnosticSeverity.Warning => "Script analysis — Warning: ",
        _ => "Script analysis — Info: ",
    } + Message;
}

/// <summary>The number of arguments a function accepts, used for the arity and unknown-function checks.</summary>
/// <param name="Name">Function name; matched without regard to case.</param>
/// <param name="MinimumParameters">Smallest accepted argument count; a negative value disables the arity check.</param>
/// <param name="MaximumParameters">Largest accepted argument count, or a negative value for "no upper limit".</param>
public sealed record ApiFunctionSignature(string Name, int MinimumParameters, int MaximumParameters)
{
    /// <summary>
    /// Derives the accepted argument count from a parameter list as the API documentation writes it (the C++ <c>gs2ApiFunction</c>).
    /// A parameter is optional when it is inside an optional group (<c>[</c> starts one, <c>]</c> ends it, a parameter ending in
    /// <c>[</c> opens a group for the following ones), or contains <c>...</c>, <c>variadic</c>, <c>?</c>, <c>=</c> or <c>optional</c>.
    /// <c>...</c> and <c>variadic</c> also remove the upper limit. Parameters without a letter, digit or underscore are ignored.
    /// </summary>
    /// <param name="name">The function name.</param>
    /// <param name="parameters">The parameter texts; null entries are ignored.</param>
    /// <returns>The signature.</returns>
    public static ApiFunctionSignature FromParameters(string name, IEnumerable<string?>? parameters)
    {
        int minimum = 0;
        int maximum = 0;
        bool optionalGroup = false;
        foreach (string? raw in parameters ?? Array.Empty<string?>())
        {
            string parameter = (raw ?? string.Empty).Trim(' ', '\t');
            string lowered = parameter.ToLowerInvariant();
            bool variadic = parameter.Contains("...", StringComparison.Ordinal) || lowered.Contains("variadic", StringComparison.Ordinal);
            bool startsOptionalGroup = parameter.Length > 0 && parameter[0] == '[';
            bool endsRequiredBeforeOptionalGroup = parameter.Length > 0 && parameter[^1] == '[';
            bool endsOptionalGroup = parameter.Length > 0 && parameter[^1] == ']' && !parameter.Contains("[]", StringComparison.Ordinal);
            bool optional = optionalGroup || startsOptionalGroup || variadic || parameter.Contains('?') || parameter.Contains('=')
                || lowered.Contains("optional", StringComparison.Ordinal);
            bool named = false;
            foreach (char character in parameter)
            {
                if (char.IsAsciiLetterOrDigit(character) || character == '_') { named = true; break; }
            }
            if (named && !optional) ++minimum;
            if (variadic) maximum = -1;
            else if (named && maximum >= 0) ++maximum;
            if (startsOptionalGroup || endsRequiredBeforeOptionalGroup) optionalGroup = true;
            if (endsOptionalGroup) optionalGroup = false;
        }
        return new ApiFunctionSignature(name ?? string.Empty, minimum, maximum);
    }
}

/// <summary>What the script analysis knows besides the text.</summary>
public sealed record DiagnosticsOptions
{
    /// <summary>The options without API knowledge: built-in arities only, unknown functions reported.</summary>
    public static DiagnosticsOptions Default { get; } = new();

    /// <summary>Signatures of the loaded API; they override the built-in arities and count as known names.</summary>
    public IReadOnlyList<ApiFunctionSignature> ApiFunctions { get; init; } = Array.Empty<ApiFunctionSignature>();

    /// <summary>Whether arity and "Unknown function" warnings are produced at all (the C++ <c>reportUnknownFunctions</c>).</summary>
    public bool ReportUnknownFunctions { get; init; } = true;

    /// <summary>
    /// Whether text of <see cref="EditorLimits.LargeContentBytes"/> or more is analysed. The editor leaves it false: like the C++,
    /// a large buffer gets no diagnostics at all.
    /// </summary>
    public bool AnalyzeLargeContent { get; init; }

    /// <summary>
    /// Builds the options the editor uses (C++ <c>runEditorDiagnostics</c>): with completion on, every loaded definition becomes a
    /// signature, and unknown functions are reported only once the remote API list has loaded; with completion off, no signatures
    /// are passed and unknown functions are always reported.
    /// </summary>
    /// <param name="definitions">The current definition set (may be null while nothing is loaded).</param>
    /// <param name="completionEnabled">The <c>lsp</c> option.</param>
    /// <returns>The options.</returns>
    public static DiagnosticsOptions ForDefinitions(ApiDefinitionSet? definitions, bool completionEnabled)
    {
        if (!completionEnabled) return new DiagnosticsOptions { ReportUnknownFunctions = true };
        var functions = new List<ApiFunctionSignature>();
        if (definitions != null)
        {
            foreach (ApiDefinition definition in definitions.Definitions) functions.Add(ApiFunctionSignature.FromParameters(definition.Name, definition.Parameters));
        }
        bool report = definitions != null && definitions.RemoteLoaded && definitions.Definitions.Count > 0;
        return new DiagnosticsOptions { ApiFunctions = functions, ReportUnknownFunctions = report };
    }
}

/// <summary>Size and timing limits the C++ editor used, so the editor control can apply the same ones.</summary>
public static class EditorLimits
{
    /// <summary>A buffer of this many UTF-8 bytes or more is "large": no highlighting, bracket matching, completion or diagnostics.</summary>
    public const int LargeContentBytes = 1024 * 1024;

    /// <summary>Pending inserted bytes that start bulk-insert mode (highlighting and diagnostics paused).</summary>
    public const int BulkInsertBytes = 8192;

    /// <summary>Pending inserted lines that start bulk-insert mode.</summary>
    public const int BulkInsertLines = 128;

    /// <summary>Milliseconds after the last bulk insert before highlighting and diagnostics resume.</summary>
    public const int BulkInsertResumeMilliseconds = 220;

    /// <summary>Milliseconds after a change before diagnostics and local symbols are recomputed.</summary>
    public const int AnalysisDelayMilliseconds = 180;

    /// <summary>Milliseconds the pointer rests on a word before the hover popover shows.</summary>
    public const int HoverDelayMilliseconds = 350;

    /// <summary>Milliseconds of typing pause before the interactive completion popup opens.</summary>
    public const int CompletionDelayMilliseconds = 180;

    /// <summary>Whether <paramref name="text"/> is large content, measured in UTF-8 bytes as the C++ did.</summary>
    /// <param name="text">The editor text.</param>
    /// <returns>True for large content.</returns>
    public static bool IsLargeContent(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Length >= LargeContentBytes) return true;
        if ((long)text.Length * 3 < LargeContentBytes) return false;
        return System.Text.Encoding.UTF8.GetByteCount(text) >= LargeContentBytes;
    }
}
