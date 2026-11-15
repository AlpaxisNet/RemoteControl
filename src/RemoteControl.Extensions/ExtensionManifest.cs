using System.Collections.Immutable;

namespace RemoteControl.Extensions;

/// <summary>Where a window action's button goes in its host window.</summary>
public enum ExtensionActionPlacement
{
    /// <summary>The action bar above the editor (the default).</summary>
    Top,

    /// <summary>The action bar below the editor.</summary>
    Bottom,
}

/// <summary>
/// One button an extension adds to a host window (<c>windowActions</c> of the manifest). In a JSON manifest it is an object
/// <c>{"kind","id","label","placement","icon"}</c>; in an INI manifest an item <c>kind:id:label</c> of a comma list.
/// </summary>
/// <param name="Kind">The kind of host window, <c>script-editor</c> when the manifest leaves it out.</param>
/// <param name="Id">The action id, sent back as <c>params.action</c> of <c>window/action</c>; never empty.</param>
/// <param name="Label">The button text; the id when the manifest leaves it out.</param>
/// <param name="Placement">The bar the button goes in; anything but <c>bottom</c> means <see cref="ExtensionActionPlacement.Top"/>.</param>
/// <param name="Icon">The icon text as written: <c>gtk:&lt;name&gt;</c>, <c>file:&lt;relative path&gt;</c>, a relative path or empty.</param>
public sealed record ExtensionWindowAction(string Kind, string Id, string Label, ExtensionActionPlacement Placement, string Icon)
{
    /// <summary>The window kind of the weapon and class script editors, the only kind the C++ offered actions for.</summary>
    public const string ScriptEditorKind = "script-editor";
}

/// <summary>What an action's icon text names.</summary>
public enum ExtensionIconKind
{
    /// <summary>No icon.</summary>
    None,

    /// <summary>A named theme icon (<c>gtk:&lt;name&gt;</c>, or an icon text that names no file in the package).</summary>
    Named,

    /// <summary>A picture file inside the package.</summary>
    File,
}

/// <summary>The resolved icon of a window action.</summary>
/// <param name="Kind">What <paramref name="Value"/> is.</param>
/// <param name="Value">The icon name (for <see cref="ExtensionIconKind.Named"/>) or the full path of the file (for <see cref="ExtensionIconKind.File"/>).</param>
public sealed record ExtensionIcon(ExtensionIconKind Kind, string Value)
{
    /// <summary>No icon.</summary>
    public static ExtensionIcon None { get; } = new(ExtensionIconKind.None, string.Empty);
}

/// <summary>The manifest format a package was read from.</summary>
public enum ExtensionManifestFormat
{
    /// <summary><c>extension.json</c>.</summary>
    Json,

    /// <summary><c>extension.ini</c> or <c>manifest.ini</c>.</summary>
    Ini,
}

/// <summary>
/// One extension package as its manifest describes it. A package whose manifest is invalid is still listed (so the window can show
/// it with its <see cref="Error"/>), with the folder name as id and name when the manifest gave none.
/// </summary>
public sealed record ExtensionManifest
{
    /// <summary>The runtime name of the GS2Engine host, launched as <c>&lt;package&gt;/GS2Engine(.exe) --extension-stdio</c>.</summary>
    public const string Gs2EngineRuntime = "gs2engine-stdio";

    /// <summary>The package folder (full path, symbolic links not resolved).</summary>
    public required string Directory { get; init; }

    /// <summary>The extensions folder the package was found in (full path).</summary>
    public string Root { get; init; } = string.Empty;

    /// <summary>The manifest file that was read.</summary>
    public string ManifestFile { get; init; } = string.Empty;

    /// <summary>The format of <see cref="ManifestFile"/>.</summary>
    public ExtensionManifestFormat Format { get; init; }

    /// <summary>The unique id (key <c>id</c>); the state file and the output tabs use it.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The display name (key <c>name</c>); output tabs are titled with it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The version text (key <c>version</c>).</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>The publisher (key <c>publisher</c>), possibly empty.</summary>
    public string Publisher { get; init; } = string.Empty;

    /// <summary>The entry file relative to the package (key <c>entry</c>), possibly empty with automatic discovery.</summary>
    public string Entry { get; init; } = string.Empty;

    /// <summary>The full path of <see cref="Entry"/> inside the package, or empty when there is no entry or it was refused.</summary>
    public string EntryPath { get; init; } = string.Empty;

    /// <summary>The runtime command (key <c>runtime</c>): a program found on the path such as <c>python3</c>, <see cref="Gs2EngineRuntime"/>, or empty to run the entry itself.</summary>
    public string Runtime { get; init; } = string.Empty;

    /// <summary>The capabilities the package asks for (<c>capabilities</c> or <c>requestedCapabilities</c>); shown and approved, never enforced (as in the C++).</summary>
    public ImmutableArray<string> RequestedCapabilities { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>The user-interface slots (<c>ui</c> or <c>uiContributions</c>); only shown (the C++ did not use them either).</summary>
    public ImmutableArray<string> UiContributions { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Theme ids (<c>themes</c>); only shown.</summary>
    public ImmutableArray<string> Themes { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Command ids (<c>commands</c>); only shown.</summary>
    public ImmutableArray<string> Commands { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Read-only view ids (<c>readOnlyViews</c>); only shown.</summary>
    public ImmutableArray<string> ReadOnlyViews { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>The buttons the package adds to host windows.</summary>
    public ImmutableArray<ExtensionWindowAction> WindowActions { get; init; } = ImmutableArray<ExtensionWindowAction>.Empty;

    /// <summary>Whether the runtime finds the package's files itself (<c>mode</c>/<c>entryMode</c> <c>auto</c> or <c>discover</c>, or <c>autoDiscover</c>); then no entry is needed.</summary>
    public bool AutoDiscover { get; init; }

    /// <summary>The extension API revision (key <c>api</c> of a JSON manifest, 1 by default); the GS2Engine host needs 1.</summary>
    public int Api { get; init; } = 1;

    /// <summary>Why the manifest is invalid, or empty when it is valid.</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>Whether the manifest is valid (an invalid package is listed but never launched).</summary>
    public bool IsValid => Error.Length == 0;

    /// <summary>Whether the package runs in the GS2Engine host.</summary>
    public bool UsesGs2Engine => string.Equals(Runtime, Gs2EngineRuntime, StringComparison.Ordinal);

    /// <summary>What "Open" shows: the entry file, or the package folder with automatic discovery or without an entry.</summary>
    public string OpenPath => AutoDiscover || EntryPath.Length == 0 ? Directory : EntryPath;

    /// <summary>
    /// Resolves the icon text of <paramref name="action"/> as the C++ did: <c>gtk:&lt;name&gt;</c> is a named icon; otherwise the
    /// text (without a <c>file:</c> prefix) is a file in the package folder or its <c>images</c> folder; a text that names no file
    /// there is used as an icon name. A path that would leave the package is never used as a file.
    /// </summary>
    /// <param name="action">One of <see cref="WindowActions"/>.</param>
    /// <returns>The icon to show.</returns>
    public ExtensionIcon ResolveIcon(ExtensionWindowAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        string specification = action.Icon;
        if (string.IsNullOrEmpty(specification)) return ExtensionIcon.None;
        if (specification.StartsWith("gtk:", StringComparison.Ordinal)) return new ExtensionIcon(ExtensionIconKind.Named, specification[4..]);
        string relative = specification.StartsWith("file:", StringComparison.Ordinal) ? specification[5..] : specification;
        foreach (string folder in new[] { Directory, Path.Combine(Directory, "images") })
        {
            if (RemoteControl.Core.SafePath.TryCombine(folder, relative, out string? path, out _) && File.Exists(path)) return new ExtensionIcon(ExtensionIconKind.File, path);
        }
        return new ExtensionIcon(ExtensionIconKind.Named, specification);
    }
}
