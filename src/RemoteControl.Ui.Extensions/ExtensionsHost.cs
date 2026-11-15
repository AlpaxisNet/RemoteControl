using Avalonia.Controls;
using Avalonia.Threading;
using RemoteControl.Core;
using RemoteControl.Extensions;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Extensions;

/// <summary>
/// The extensions of the program: one for the whole program, made by the program at start. It owns the
/// <see cref="ExtensionManager"/> while the <c>extensionsenabled</c> option is on and follows the option live (switching it off
/// disposes the manager, defect D7 of the C++: nothing that still holds the host can crash, every member then works as "no
/// extensions"). It offers the extensions' buttons to the script editors (<see cref="IExtensionActions"/>, through
/// <see cref="IAppShell.GetService{T}"/>), puts the extensions' output into tabs of the main windows, and shows the extensions window.
/// Used on the UI thread only.
/// </summary>
public sealed class ExtensionsHost : IExtensionActions, IDisposable
{
    private readonly AppServices _services;
    private readonly IAppShell _shell;
    private readonly Func<ExtensionManagerOptions> _createOptions;
    private readonly ExtensionOutputRouter _router;
    private readonly EventHandler<OptionsChangedEventArgs> _optionsChanged;
    private ExtensionManager? _manager;
    private ExtensionManagerOptions? _managerOptions;
    private ExtensionsWindow? _window;
    private string _actionsSignature = string.Empty;
    private Task _stopped = Task.CompletedTask;
    private bool _disposed;

    /// <summary>Creates the host; it starts the extensions when the <c>extensionsenabled</c> option is on.</summary>
    /// <param name="services">The program's services (options, paths, log).</param>
    /// <param name="shell">The program (its sessions, for the output tabs).</param>
    public ExtensionsHost(AppServices services, IAppShell shell)
        : this(services, shell, () => ExtensionManagerOptions.ForUser(services.Paths) with { Log = services.Log })
    {
    }

    /// <summary>Creates the host with other extension folders (tests).</summary>
    /// <param name="services">The program's services.</param>
    /// <param name="shell">The program.</param>
    /// <param name="createOptions">Makes the manager's settings each time the extensions are switched on.</param>
    internal ExtensionsHost(AppServices services, IAppShell shell, Func<ExtensionManagerOptions> createOptions)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _createOptions = createOptions ?? throw new ArgumentNullException(nameof(createOptions));
        _router = new ExtensionOutputRouter(shell);
        _optionsChanged = (_, e) =>
        {
            if (e.Previous.ExtensionsEnabled == e.Current.ExtensionsEnabled) return;
            if (Dispatcher.UIThread.CheckAccess()) UiSafe.Run(ApplyOption, "extensions option");
            else UiDispatch.Post(ApplyOption, "extensions option");
        };
        _services.Options.Changed += _optionsChanged;
        ApplyOption();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>The list or one extension changed (<see cref="ExtensionChangedEventArgs.ExtensionId"/> null: the whole list, also when the extensions were switched on or off).</summary>
    internal event EventHandler<ExtensionChangedEventArgs>? ExtensionsChanged;

    /// <summary>A line was added to an extension's log.</summary>
    internal event EventHandler<ExtensionOutputEventArgs>? LogLine;

    /// <summary>Whether the extensions are switched on (the option) and the host is not disposed.</summary>
    public bool IsEnabled => _manager is { IsDisposed: false };

    /// <summary>Counts the times the extensions were switched on or off: a dialog that started before compares it before it acts.</summary>
    internal int Generation { get; private set; }

    /// <summary>The extensions (empty while switched off).</summary>
    internal IReadOnlyList<ExtensionInfo> Extensions => _manager?.Extensions ?? Array.Empty<ExtensionInfo>();

    /// <summary>Why the state file could not be read, or null.</summary>
    internal string? StateFileError => _manager?.StateFileError;

    /// <summary>The most characters of an extension's log that are kept.</summary>
    internal int LogLimit => _managerOptions?.LogLimit ?? ExtensionManagerOptions.DefaultLogLimit;

    /// <summary>The user's extensions folder (the last, highest-priority one).</summary>
    internal string UserExtensionsFolder => _managerOptions is { PackageRoots.Count: > 0 } options
        ? options.PackageRoots[^1]
        : Path.Combine(_services.Paths.UserDirectory, "extensions");

    /// <summary>The output router (tests).</summary>
    internal ExtensionOutputRouter Router => _router;

    /// <summary>The extensions window, when one was made and is not closed (tests).</summary>
    internal ExtensionsWindow? Window => _window is { IsClosed: false } window ? window : null;

    /// <summary>Completes when the last manager that was switched off has stopped its runtimes (tests, program exit).</summary>
    /// <returns>The task; it never faults.</returns>
    internal Task WhenStoppedAsync() => _stopped;

    /// <inheritdoc />
    public IReadOnlyList<WindowActionButton> ActionsFor(string windowKind)
    {
        ExtensionManager? manager = _manager;
        if (manager is null || string.IsNullOrEmpty(windowKind)) return Array.Empty<WindowActionButton>();
        return manager.GetWindowActions(windowKind)
            .Select(binding => new WindowActionButton(
                ActionKey(binding),
                binding.Action.Label,
                binding.Action.Placement == ExtensionActionPlacement.Bottom,
                binding.Icon.Kind == ExtensionIconKind.File ? binding.Icon.Value : null))
            .ToList();
    }

    /// <inheritdoc />
    public void Invoke(string actionId, SessionContext session, ScriptEditorSnapshot editor)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(editor);
        ExtensionManager? manager = _manager;
        if (manager is null || actionId is null) return;
        ExtensionWindowActionBinding? binding = manager.GetWindowActions(ExtensionWindowAction.ScriptEditorKind)
            .FirstOrDefault(candidate => string.Equals(ActionKey(candidate), actionId, StringComparison.Ordinal));
        if (binding is null)
        {
            _services.Log.Warn("Extension window action " + actionId + " is not offered any more.");
            return;
        }
        ExtensionWindowContext context = ExtensionWindowContext.ForScriptEditor(ScriptTypeOf(editor.Kind), editor.Name, editor.Title, editor.Text, editor.Selection);
        ExtensionCallResult result = manager.InvokeWindowAction(binding, context);
        if (result.Success)
        {
            _router.Concern(binding.ExtensionId, session);
            return;
        }
        _services.Log.Warn("Extension window action failed: " + result.Error);
        Window? owner = session.Services.Get<ISessionChat>()?.Window;
        UiSafe.Fire(() => Dialogs.ShowErrorAsync("Extension Action", $"{binding.Tooltip} failed:\n{result.Error}", owner), "extension action failed");
    }

    /// <summary>Shows the extensions window (made once, hidden when closed) and brings it to the front.</summary>
    /// <param name="owner">The window that asked, or null.</param>
    /// <returns>The window.</returns>
    public Window ShowWindow(Window? owner)
    {
        if (_window is null || _window.IsClosed) _window = new ExtensionsWindow(this, _services);
        _window.ShowAndActivate(owner);
        return _window;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _services.Options.Changed -= _optionsChanged;
        StopManager();
        _router.Dispose();
        ExtensionsWindow? window = _window;
        _window = null;
        if (window is not null) UiSafe.Run(window.Close, "close the extensions window");
    }

    internal ExtensionInfo? Find(string id) => _manager?.Find(id);

    internal string GetLog(string id) => _manager?.GetLog(id) ?? string.Empty;

    internal ExtensionCallResult SetEnabled(string id, bool enabled) => _manager?.SetEnabled(id, enabled) ?? Off;

    internal ExtensionCallResult SetOutputTab(string id, bool enabled) => _manager?.SetOutputTab(id, enabled) ?? Off;

    internal ExtensionCallResult SetApprovedCapabilities(string id, IEnumerable<string> capabilities) => _manager?.SetApprovedCapabilities(id, capabilities) ?? Off;

    /// <summary>Removes a package, when the extensions were not switched off or on since <paramref name="generation"/>.</summary>
    internal async Task<ExtensionCallResult> RemoveAsync(string id, int generation)
    {
        ExtensionManager? manager = _manager;
        if (manager is null || generation != Generation) return Off;
        return await manager.RemoveAsync(id).ConfigureAwait(true);
    }

    /// <summary>Finds the packages again (Refresh).</summary>
    internal Task RescanAsync() => _manager?.StartAsync() ?? Task.CompletedTask;

    /// <summary>Whether a package may be removed: not one installed with the program (its folder is the program's).</summary>
    internal bool CanRemove(ExtensionInfo info)
    {
        IReadOnlyList<string>? roots = _managerOptions?.PackageRoots;
        if (roots is null || roots.Count < 2) return roots is not null;
        return !SamePath(info.Manifest.Root, roots[0]);
    }

    /// <summary>Opens a package's entry or folder with the desktop's program for it.</summary>
    internal bool OpenPackage(ExtensionInfo info) => ExternalPrograms.OpenFile(info.Manifest.OpenPath, _services.Log);

    /// <summary>Opens the user's extensions folder (made when missing).</summary>
    internal bool OpenUserFolder()
    {
        string folder = UserExtensionsFolder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _services.Log.Warn("The extensions folder " + folder + " could not be made.", exception);
            return false;
        }
        return ExternalPrograms.OpenFile(folder, _services.Log);
    }

    private static ExtensionCallResult Off => ExtensionCallResult.Fail("Extensions are switched off");

    private void ApplyOption()
    {
        if (_disposed) return;
        bool wanted = _services.Options.Current.ExtensionsEnabled;
        if (wanted && _manager is null) StartManager();
        else if (!wanted && _manager is not null) StopManager();
    }

    private void StartManager()
    {
        ExtensionManagerOptions options;
        ExtensionManager manager;
        try
        {
            options = _createOptions();
            manager = new ExtensionManager(options, action => Dispatcher.UIThread.Post(action));
        }
        catch (Exception exception)
        {
            _services.Log.Error("The extensions could not be started.", exception);
            return;
        }
        manager.Changed += OnManagerChanged;
        manager.LogLine += OnLogLine;
        manager.OutputTabOpened += OnTabOpened;
        manager.OutputLine += OnOutputLine;
        manager.OutputTabClosed += OnTabClosed;
        _manager = manager;
        _managerOptions = options;
        Generation++;
        RaiseListChanged();
        UiSafe.Fire(manager.StartAsync, "start the extensions");
    }

    private void StopManager()
    {
        ExtensionManager? manager = _manager;
        if (manager is null) return;
        _manager = null;
        manager.Changed -= OnManagerChanged;
        manager.LogLine -= OnLogLine;
        manager.OutputTabOpened -= OnTabOpened;
        manager.OutputLine -= OnOutputLine;
        manager.OutputTabClosed -= OnTabClosed;
        Task stopping = manager.DisposeAsync().AsTask();
        Task previous = _stopped;
        _stopped = Task.WhenAll(previous, stopping).ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _router.CloseAll();
        Generation++;
        if (!_disposed) RaiseListChanged();
        else CheckActions();
    }

    private void RaiseListChanged()
    {
        UiSafe.Run(() => ExtensionsChanged?.Invoke(this, new ExtensionChangedEventArgs(null)), "extensions changed");
        CheckActions();
    }

    private void OnManagerChanged(object? sender, ExtensionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _manager)) return;
        if (e.ExtensionId is null)
        {
            var keep = new HashSet<string>(Extensions.Where(info => info.OutputToTab && info.Manifest.IsValid).Select(info => info.Id), StringComparer.Ordinal);
            _router.CloseAllBut(keep);
        }
        UiSafe.Run(() => ExtensionsChanged?.Invoke(this, e), "extension changed");
        CheckActions();
    }

    private void OnLogLine(object? sender, ExtensionOutputEventArgs e)
    {
        if (ReferenceEquals(sender, _manager)) UiSafe.Run(() => LogLine?.Invoke(this, e), "extension log line");
    }

    private void OnTabOpened(object? sender, ExtensionOutputTabEventArgs e)
    {
        if (ReferenceEquals(sender, _manager)) _router.Open(e.ExtensionId, e.ExtensionName, e.Backlog);
    }

    private void OnOutputLine(object? sender, ExtensionOutputEventArgs e)
    {
        if (ReferenceEquals(sender, _manager)) _router.Line(e.ExtensionId, e.ExtensionName, e.Line);
    }

    private void OnTabClosed(object? sender, ExtensionOutputTabEventArgs e)
    {
        if (ReferenceEquals(sender, _manager)) _router.Close(e.ExtensionId);
    }

    /// <summary>Raises <see cref="Changed"/> when the buttons the extensions offer are not the same any more.</summary>
    private void CheckActions()
    {
        string signature = ActionsSignature();
        if (string.Equals(signature, _actionsSignature, StringComparison.Ordinal)) return;
        _actionsSignature = signature;
        UiSafe.Run(() => Changed?.Invoke(this, EventArgs.Empty), "extension actions changed");
    }

    private string ActionsSignature()
    {
        ExtensionManager? manager = _manager;
        if (manager is null) return string.Empty;
        IEnumerable<string> kinds = manager.Extensions.SelectMany(info => info.Manifest.WindowActions).Select(action => action.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return string.Join("\n", kinds.SelectMany(kind => manager.GetWindowActions(kind)).Select(binding =>
            $"{binding.Action.Kind}\u001f{ActionKey(binding)}\u001f{binding.Action.Label}\u001f{binding.Action.Placement}\u001f{binding.Icon.Kind}\u001f{binding.Icon.Value}"));
    }

    private static string ActionKey(ExtensionWindowActionBinding binding) => binding.ExtensionId + "/" + binding.Action.Id;

    private static string ScriptTypeOf(ScriptKind kind) => kind switch
    {
        ScriptKind.Weapon => "weapons",
        ScriptKind.Class => "classes",
        ScriptKind.Npc => "npcs",
        _ => "text",
    };

    private static bool SamePath(string first, string second)
    {
        try
        {
            StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), comparison);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
