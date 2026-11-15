using System.Collections.Immutable;
using RemoteControl.Core;

namespace RemoteControl.Extensions;

/// <summary>The state an extension is shown with (<c>failed</c>, <c>starting</c>, <c>running</c>, <c>stopped</c>, <c>disabled</c> in the C++ label).</summary>
public enum ExtensionStatus
{
    /// <summary>The manifest is invalid or the runtime could not be started or ended unexpectedly (see <see cref="ExtensionInfo.Error"/>).</summary>
    Failed,

    /// <summary>The runtime process is being started.</summary>
    Starting,

    /// <summary>The runtime process runs.</summary>
    Running,

    /// <summary>Enabled, but no runtime process runs.</summary>
    Stopped,

    /// <summary>Not enabled.</summary>
    Disabled,
}

/// <summary>A snapshot of one extension for a window: it never changes; read <see cref="ExtensionManager.Extensions"/> again after <see cref="ExtensionManager.Changed"/>.</summary>
/// <param name="Manifest">The package.</param>
/// <param name="Enabled">Whether it is enabled (saved).</param>
/// <param name="OutputToTab">Whether its output goes to a tab (saved).</param>
/// <param name="ApprovedCapabilities">The approved capabilities (saved), sorted.</param>
/// <param name="Status">The current state.</param>
/// <param name="Error">The manifest error, else the runtime error, else empty.</param>
/// <param name="HasLog">Whether the log has text (the C++ highlighted the Log button then).</param>
/// <param name="ProcessId">The runtime process id while it runs.</param>
public sealed record ExtensionInfo(ExtensionManifest Manifest, bool Enabled, bool OutputToTab, ImmutableSortedSet<string> ApprovedCapabilities, ExtensionStatus Status, string Error, bool HasLog, int? ProcessId)
{
    /// <summary>The extension id.</summary>
    public string Id => Manifest.Id;

    /// <summary>The display name.</summary>
    public string Name => Manifest.Name;

    /// <summary>The status word of the C++ label: <c>failed</c>, <c>starting</c>, <c>running</c>, <c>stopped</c> or <c>disabled</c>.</summary>
    public string StatusText => Status switch
    {
        ExtensionStatus.Failed => "failed",
        ExtensionStatus.Starting => "starting",
        ExtensionStatus.Running => "running",
        ExtensionStatus.Stopped => "stopped",
        _ => "disabled",
    };

    /// <summary>The row label of the C++ window: <c>&lt;name&gt; &lt;version&gt; (&lt;status&gt;) - &lt;error&gt;</c>.</summary>
    public string DisplayText => $"{Manifest.Name} {Manifest.Version} ({StatusText})" + (Error.Length == 0 ? string.Empty : " - " + Error);

    /// <summary>The details text of the C++ Details dialog (id, version, status, runtime, entry, publisher).</summary>
    public string DetailsText
    {
        get
        {
            string entry = Manifest.Entry.Length > 0 ? Manifest.Entry : Manifest.AutoDiscover ? "automatic discovery" : "none";
            string text = $"ID: {Manifest.Id}\nVersion: {Manifest.Version}\nStatus: {StatusText}\nRuntime: {Manifest.Runtime}\nEntry: {entry}";
            return Manifest.Publisher.Length == 0 ? text : text + "\nPublisher: " + Manifest.Publisher;
        }
    }
}

/// <summary>A window action together with the extension that declared it, as <see cref="ExtensionManager.GetWindowActions"/> lists them.</summary>
/// <param name="ExtensionId">The extension id.</param>
/// <param name="ExtensionName">The extension name.</param>
/// <param name="ExtensionDirectory">The package folder.</param>
/// <param name="Action">The action.</param>
/// <param name="Icon">The resolved icon.</param>
public sealed record ExtensionWindowActionBinding(string ExtensionId, string ExtensionName, string ExtensionDirectory, ExtensionWindowAction Action, ExtensionIcon Icon)
{
    /// <summary>The button tooltip of the C++: <c>Run &lt;label&gt; from &lt;extension&gt;</c>.</summary>
    public string Tooltip => $"Run {Action.Label} from {ExtensionName}";
}

/// <summary>The window an action was pressed in, sent as <c>params.context</c> of <c>window/action</c>.</summary>
/// <param name="WindowId">An id of the window, <c>&lt;scriptType&gt;:&lt;scriptName&gt;</c> for a script editor.</param>
/// <param name="Kind">The window kind (<see cref="ExtensionWindowAction.ScriptEditorKind"/>).</param>
/// <param name="Title">The window title.</param>
/// <param name="ScriptType">The script type: <c>weapons</c> or <c>classes</c>.</param>
/// <param name="ScriptName">The script name.</param>
/// <param name="Text">The whole editor text, unsaved changes included.</param>
/// <param name="Selection">The selected text, or empty.</param>
public sealed record ExtensionWindowContext(string WindowId, string Kind, string Title, string ScriptType, string ScriptName, string Text, string Selection)
{
    /// <summary>The context of a weapon or class script editor, as <c>TScriptList.cpp:124-149</c> built it.</summary>
    /// <param name="scriptType"><c>weapons</c> or <c>classes</c>.</param>
    /// <param name="scriptName">The script name.</param>
    /// <param name="title">The editor window title.</param>
    /// <param name="text">The whole editor text.</param>
    /// <param name="selection">The selected text, or empty.</param>
    /// <returns>The context.</returns>
    public static ExtensionWindowContext ForScriptEditor(string scriptType, string scriptName, string title, string text, string selection) =>
        new(scriptType + ":" + scriptName, ExtensionWindowAction.ScriptEditorKind, title, scriptType, scriptName, text ?? string.Empty, selection ?? string.Empty);
}

/// <summary>The outcome of a manager call: success, or the message to show.</summary>
/// <param name="Success">Whether it worked.</param>
/// <param name="Error">Why not, or empty.</param>
public readonly record struct ExtensionCallResult(bool Success, string Error)
{
    /// <summary>Success.</summary>
    public static ExtensionCallResult Ok => new(true, string.Empty);

    /// <summary>A failure with a message.</summary>
    /// <param name="error">The message.</param>
    /// <returns>The failure.</returns>
    public static ExtensionCallResult Fail(string error) => new(false, error);
}

/// <summary>Raised when an extension (or the whole list, <see cref="ExtensionId"/> <c>null</c>) changed.</summary>
public sealed class ExtensionChangedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="extensionId">The extension that changed, or <c>null</c> for the whole list.</param>
    public ExtensionChangedEventArgs(string? extensionId) => ExtensionId = extensionId;

    /// <summary>The extension that changed, or <c>null</c> when the list itself changed.</summary>
    public string? ExtensionId { get; }
}

/// <summary>A line added to an extension's log or shown in its output tab.</summary>
public sealed class ExtensionOutputEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="extensionId">The extension id.</param>
    /// <param name="extensionName">The extension name (the tab title).</param>
    /// <param name="line">The line.</param>
    public ExtensionOutputEventArgs(string extensionId, string extensionName, string line)
    {
        ExtensionId = extensionId;
        ExtensionName = extensionName;
        Line = line;
    }

    /// <summary>The extension id.</summary>
    public string ExtensionId { get; }

    /// <summary>The extension name (the tab title).</summary>
    public string ExtensionName { get; }

    /// <summary>The line (never empty).</summary>
    public string Line { get; }
}

/// <summary>An output tab should be opened (with the lines logged so far) or closed.</summary>
public sealed class ExtensionOutputTabEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="extensionId">The extension id.</param>
    /// <param name="extensionName">The extension name (the tab title).</param>
    /// <param name="backlog">The lines to show at once (the log so far); empty when closing.</param>
    public ExtensionOutputTabEventArgs(string extensionId, string extensionName, IReadOnlyList<string> backlog)
    {
        ExtensionId = extensionId;
        ExtensionName = extensionName;
        Backlog = backlog;
    }

    /// <summary>The extension id.</summary>
    public string ExtensionId { get; }

    /// <summary>The extension name (the tab title).</summary>
    public string ExtensionName { get; }

    /// <summary>The lines to show at once.</summary>
    public IReadOnlyList<string> Backlog { get; }
}

/// <summary>Settings of an <see cref="ExtensionManager"/>.</summary>
public sealed record ExtensionManagerOptions
{
    /// <summary>The default log size (64 KiB, as <c>appendExtensionLog</c>).</summary>
    public const int DefaultLogLimit = 65536;

    /// <summary>The extensions folders, lowest priority first (a package of a later folder overrides one with the same id).</summary>
    public required IReadOnlyList<string> PackageRoots { get; init; }

    /// <summary>The state file (<see cref="ExtensionStateFile.PathFor"/>).</summary>
    public required string StateFile { get; init; }

    /// <summary>How long a runtime has to end after the stop message before it is killed (750 ms, the C++ Windows grace).</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>The most characters kept of each extension's log.</summary>
    public int LogLimit { get; init; } = DefaultLogLimit;

    /// <summary>The most messages waiting to be written to one runtime; more are refused (a runtime that does not read is never waited for).</summary>
    public int MaximumQueuedMessages { get; init; } = 256;

    /// <summary>The most bytes waiting to be written to one runtime.</summary>
    public long MaximumQueuedBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>The longest line read from a runtime; the rest of a longer line is dropped.</summary>
    public int MaximumLineLength { get; init; } = 1024 * 1024;

    /// <summary>Where problems are logged; <see cref="AppLog.Default"/> when not set.</summary>
    public AppLog? Log { get; init; }

    /// <summary>
    /// The settings for the current user: the program folder's <c>extensions</c> first, then (on Linux and macOS) the C++ user
    /// folder <c>~/.local/share/GScriptRC/extensions</c> when it exists, then <c>&lt;user folder&gt;/extensions</c>; the state file in the user folder.
    /// </summary>
    /// <param name="paths">The program's folders.</param>
    /// <returns>The settings.</returns>
    public static ExtensionManagerOptions ForUser(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var roots = new List<string> { Path.Combine(paths.ProgramDirectory, "extensions") };
        if (!OperatingSystem.IsWindows())
        {
            string? data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(data)) data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            string legacy = Path.Combine(data, AppPaths.FolderName, "extensions");
            if (Directory.Exists(legacy)) roots.Add(legacy);
        }
        string user = Path.Combine(paths.UserDirectory, "extensions");
        if (!roots.Contains(user, StringComparer.Ordinal)) roots.Add(user);
        return new ExtensionManagerOptions { PackageRoots = roots, StateFile = ExtensionStateFile.PathFor(paths) };
    }
}
