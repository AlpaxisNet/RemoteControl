using System.Collections.Immutable;
using RemoteControl.Core;

namespace RemoteControl.Extensions;

/// <summary>
/// The installed extensions of the program (<c>TExtensionsManager</c> without its window): finds the packages, keeps their saved state,
/// launches the runtime of every enabled valid package, reads its output into a bounded log and, when asked, into an output tab,
/// sends <c>window/action</c> requests, stops and removes packages.
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> Every event is raised through the post delegate (or <see cref="SynchronizationContext"/>) given at construction,
/// in the order the changes happened (AGENTS.md rule 1); in the program that is the UI thread. The methods may be called from any
/// thread (normally the UI thread) and never wait for a runtime process: launching, writing, stopping and deleting run on background
/// tasks. Methods that touch the disk at length are <c>async</c>.</para>
/// <para><b>Lifetime.</b> <see cref="Dispose"/> (or <see cref="DisposeAsync"/>, which also waits until the processes are gone) may be called
/// at any time, also while windows still hold the manager: afterwards every method is a harmless no-op (lists are empty, calls fail
/// with <see cref="ClosedMessage"/>) and no event is raised any more.</para>
/// <para><b>Lifecycle of a runtime.</b> Enabling launches it; the runtime of an ordinary package gets <see cref="ExtensionProtocol.StartMessage"/>,
/// the GS2Engine host its start request after it said <c>ready</c>. Disabling (and removing, rescanning, disposing) sends the stop
/// messages, closes its input, kills it after <see cref="ExtensionManagerOptions.StopTimeout"/> and reaps it. A runtime that ends by
/// itself is reported ("Runtime exited unexpectedly") and not restarted, as in the C++; disabling and enabling it again starts a new one.
/// Events of a runtime that was replaced or stopped are ignored (the C++ used vector indexes that could point at another package, D-23).</para>
/// </remarks>
public sealed class ExtensionManager : IDisposable, IAsyncDisposable
{
    /// <summary>The message of every call made after the manager was disposed.</summary>
    public const string ClosedMessage = "The extension manager is closed";

    /// <summary>The message when the extension id is not (any more) in the list.</summary>
    public const string NotInstalledMessage = "Extension is no longer installed";

    private readonly ExtensionManagerOptions _options;
    private readonly Action<Action> _post;
    private readonly AppLog _log;
    private readonly object _gate = new();
    private readonly object _flushGate = new();
    private readonly Queue<Action> _pending = new();
    private readonly List<Entry> _entries = new();
    private readonly HashSet<Task> _stopping = new();
    private Dictionary<string, ExtensionSavedState> _preserved = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _unreadableStateLines = Array.Empty<string>();
    private bool _stateWritable = true;
    private string? _stateError;
    private Task _saveChain = Task.CompletedTask;
    private int _scanGeneration;
    private volatile bool _disposed;

    /// <summary>Creates a manager; call <see cref="StartAsync"/> to find the packages and launch the enabled ones.</summary>
    /// <param name="options">Folders, state file and limits.</param>
    /// <param name="post">Runs an action on the owner's context (the UI thread), in order, without waiting (for example <c>Dispatcher.UIThread.Post</c>).</param>
    public ExtensionManager(ExtensionManagerOptions options, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(post);
        _options = options;
        _post = post;
        _log = options.Log ?? AppLog.Default;
    }

    /// <summary>Creates a manager that raises its events on <paramref name="context"/>.</summary>
    /// <param name="options">Folders, state file and limits.</param>
    /// <param name="context">The owner's context (the UI thread's).</param>
    public ExtensionManager(ExtensionManagerOptions options, SynchronizationContext context)
        : this(options, PostTo(context))
    {
    }

    /// <summary>The list or one extension changed (status, enabled, output tab, log became non-empty, ...).</summary>
    public event EventHandler<ExtensionChangedEventArgs>? Changed;

    /// <summary>A line was added to an extension's log (the shown text of standard output, standard error, and the program's own messages).</summary>
    public event EventHandler<ExtensionOutputEventArgs>? LogLine;

    /// <summary>A line for the output tab of an extension whose output goes to a tab.</summary>
    public event EventHandler<ExtensionOutputEventArgs>? OutputLine;

    /// <summary>An output tab should be shown (at start for packages whose output goes to a tab, and when it is switched on; with the log so far).</summary>
    public event EventHandler<ExtensionOutputTabEventArgs>? OutputTabOpened;

    /// <summary>An output tab should be closed (switched off, or the package was removed).</summary>
    public event EventHandler<ExtensionOutputTabEventArgs>? OutputTabClosed;

    /// <summary>Whether the manager was disposed.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Why the state file could not be read (it is then kept and not overwritten), or <c>null</c>.</summary>
    public string? StateFileError
    {
        get { lock (_gate) return _stateError; }
    }

    /// <summary>A snapshot of every extension, in list order (empty after disposal).</summary>
    public IReadOnlyList<ExtensionInfo> Extensions
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return Array.Empty<ExtensionInfo>();
                return _entries.Select(Snapshot).ToList();
            }
        }
    }

    /// <summary>The snapshot of one extension.</summary>
    /// <param name="id">The extension id.</param>
    /// <returns>The snapshot, or <c>null</c>.</returns>
    public ExtensionInfo? Find(string id)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            Entry? entry = FindEntry(id);
            return entry is null ? null : Snapshot(entry);
        }
    }

    /// <summary>The log of an extension (at most <see cref="ExtensionManagerOptions.LogLimit"/> characters, lines separated by <c>\n</c>).</summary>
    /// <param name="id">The extension id.</param>
    /// <returns>The log, or empty.</returns>
    public string GetLog(string id)
    {
        lock (_gate)
        {
            if (_disposed) return string.Empty;
            return FindEntry(id)?.Log.Text ?? string.Empty;
        }
    }

    /// <summary>
    /// Finds the packages and reads the state file on a background task, then (on any thread) replaces the list: running runtimes are
    /// stopped, output tabs are opened for packages whose output goes to a tab, and every enabled valid package is launched. Calling it
    /// again rescans.
    /// </summary>
    /// <returns>A task that ends when the list is replaced (the runtimes start in the background).</returns>
    public async Task StartAsync()
    {
        int generation;
        lock (_gate)
        {
            if (_disposed) return;
            generation = ++_scanGeneration;
        }
        var (manifests, state, stateError) = await Task.Run(() =>
        {
            IReadOnlyList<ExtensionManifest> found = ExtensionManifestLoader.ScanAll(_options.PackageRoots);
            try
            {
                return (found, ExtensionStateFile.Read(_options.StateFile), (string?)null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return (found, ExtensionStateContent.Empty, exception.Message);
            }
        }).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed || generation != _scanGeneration) return;
                foreach (Entry old in _entries) StopRuntime(old);
                _entries.Clear();
                _stateWritable = stateError is null;
                _stateError = stateError is null ? null : "The extension state file could not be read and is kept unchanged: " + stateError;
                if (stateError is not null) _log.Warn(_stateError!);
                _preserved = new Dictionary<string, ExtensionSavedState>(state.Entries, StringComparer.Ordinal);
                _unreadableStateLines = state.UnreadableLines;
                foreach (ExtensionManifest manifest in manifests)
                {
                    var entry = new Entry(manifest, _options.LogLimit);
                    if (_preserved.Remove(manifest.Id, out ExtensionSavedState? saved))
                    {
                        entry.Enabled = saved.Enabled;
                        entry.OutputToTab = saved.OutputToTab;
                        entry.Approved = saved.ApprovedCapabilities;
                    }
                    _entries.Add(entry);
                }
                Enqueue(() => Changed?.Invoke(this, new ExtensionChangedEventArgs(null)));
                foreach (Entry entry in _entries)
                {
                    if (entry.OutputToTab && entry.Manifest.IsValid) EnqueueTabOpened(entry, Array.Empty<string>());
                }
                foreach (Entry entry in _entries)
                {
                    if (entry.Enabled && entry.Manifest.IsValid) Launch(entry);
                }
            }
        }
        finally
        {
            Flush();
        }
    }

    /// <summary>Enables (launches) or disables (stops) an extension and saves the state.</summary>
    /// <param name="id">The extension id.</param>
    /// <param name="enabled">Whether to enable it.</param>
    /// <returns>Success, or why not (an invalid manifest cannot be enabled).</returns>
    public ExtensionCallResult SetEnabled(string id, bool enabled)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return ExtensionCallResult.Fail(ClosedMessage);
                Entry? entry = FindEntry(id);
                if (entry is null || entry.Removing) return ExtensionCallResult.Fail(NotInstalledMessage);
                if (enabled && !entry.Manifest.IsValid) return ExtensionCallResult.Fail("The manifest is invalid: " + entry.Manifest.Error);
                entry.Enabled = enabled;
                if (!enabled) entry.RuntimeError = string.Empty;
                QueueSave();
                if (enabled) Launch(entry);
                else StopRuntime(entry);
                EnqueueChanged(entry);
                return ExtensionCallResult.Ok;
            }
        }
        finally
        {
            Flush();
        }
    }

    /// <summary>Sends an extension's output to a tab or stops it, and saves the state. Switching on raises <see cref="OutputTabOpened"/> with the log so far; off raises <see cref="OutputTabClosed"/>.</summary>
    /// <param name="id">The extension id.</param>
    /// <param name="enabled">Whether the output goes to a tab.</param>
    /// <returns>Success, or why not.</returns>
    public ExtensionCallResult SetOutputTab(string id, bool enabled)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return ExtensionCallResult.Fail(ClosedMessage);
                Entry? entry = FindEntry(id);
                if (entry is null || entry.Removing) return ExtensionCallResult.Fail(NotInstalledMessage);
                bool changed = entry.OutputToTab != enabled;
                entry.OutputToTab = enabled;
                QueueSave();
                if (enabled) EnqueueTabOpened(entry, entry.Log.Lines);
                else if (changed) EnqueueTabClosed(entry);
                EnqueueChanged(entry);
                return ExtensionCallResult.Ok;
            }
        }
        finally
        {
            Flush();
        }
    }

    /// <summary>Saves which of the requested capabilities the user approved (the Details dialog's check boxes). Names the manifest does not request are ignored.</summary>
    /// <param name="id">The extension id.</param>
    /// <param name="capabilities">The approved capability names.</param>
    /// <returns>Success, or why not.</returns>
    public ExtensionCallResult SetApprovedCapabilities(string id, IEnumerable<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        try
        {
            lock (_gate)
            {
                if (_disposed) return ExtensionCallResult.Fail(ClosedMessage);
                Entry? entry = FindEntry(id);
                if (entry is null || entry.Removing) return ExtensionCallResult.Fail(NotInstalledMessage);
                entry.Approved = ImmutableSortedSet.CreateRange(StringComparer.Ordinal, capabilities.Where(entry.Manifest.RequestedCapabilities.Contains));
                QueueSave();
                EnqueueChanged(entry);
                return ExtensionCallResult.Ok;
            }
        }
        finally
        {
            Flush();
        }
    }

    /// <summary>
    /// Removes a package after the caller asked the user: stops its runtime (waiting until it is gone), closes its output tab, deletes
    /// its package folder (only that folder, and only when it lies directly in an extensions folder; a linked folder loses only the
    /// link, D-24) and saves the state. A failed deletion keeps the package in the list and logs why.
    /// </summary>
    /// <param name="id">The extension id.</param>
    /// <returns>Success, or why not.</returns>
    public async Task<ExtensionCallResult> RemoveAsync(string id)
    {
        Task stopped;
        ExtensionManifest manifest;
        try
        {
            lock (_gate)
            {
                if (_disposed) return ExtensionCallResult.Fail(ClosedMessage);
                Entry? entry = FindEntry(id);
                if (entry is null || entry.Removing) return ExtensionCallResult.Fail(NotInstalledMessage);
                entry.Removing = true;
                entry.Enabled = false;
                manifest = entry.Manifest;
                stopped = StopRuntime(entry);
                if (entry.OutputToTab) EnqueueTabClosed(entry);
                EnqueueChanged(entry);
            }
        }
        finally
        {
            Flush();
        }
        await stopped.ConfigureAwait(false);
        string? error = await Task.Run(() => DeletePackage(manifest)).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed) return error is null ? ExtensionCallResult.Ok : ExtensionCallResult.Fail(error);
                Entry? entry = _entries.FirstOrDefault(value => ReferenceEquals(value.Manifest, manifest));
                if (error is not null)
                {
                    if (entry is not null)
                    {
                        entry.Removing = false;
                        AddLine(entry, "Remove failed: " + error);
                        QueueSave();
                        EnqueueChanged(entry);
                    }
                    return ExtensionCallResult.Fail("Remove failed: " + error);
                }
                if (entry is not null) _entries.Remove(entry);
                _preserved.Remove(manifest.Id);
                QueueSave();
                Enqueue(() => Changed?.Invoke(this, new ExtensionChangedEventArgs(null)));
                return ExtensionCallResult.Ok;
            }
        }
        finally
        {
            Flush();
        }
    }

    /// <summary>The actions of enabled, valid extensions for one window kind (<c>windowActions(kind)</c>), in list order.</summary>
    /// <param name="kind">The window kind, such as <see cref="ExtensionWindowAction.ScriptEditorKind"/>.</param>
    /// <returns>The actions; empty after disposal.</returns>
    public IReadOnlyList<ExtensionWindowActionBinding> GetWindowActions(string kind)
    {
        lock (_gate)
        {
            if (_disposed) return Array.Empty<ExtensionWindowActionBinding>();
            var result = new List<ExtensionWindowActionBinding>();
            foreach (Entry entry in _entries)
            {
                if (!entry.Enabled || entry.Removing || !entry.Manifest.IsValid) continue;
                foreach (ExtensionWindowAction action in entry.Manifest.WindowActions)
                {
                    if (string.Equals(action.Kind, kind, StringComparison.Ordinal))
                        result.Add(new ExtensionWindowActionBinding(entry.Manifest.Id, entry.Manifest.Name, entry.Manifest.Directory, action, entry.Manifest.ResolveIcon(action)));
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Sends a <c>window/action</c> request (valid JSON, unlike the C++, D-20) for a pressed action button. The request is queued and
    /// written in the background; the call never waits for the extension.
    /// </summary>
    /// <param name="binding">The action, from <see cref="GetWindowActions"/>.</param>
    /// <param name="context">The window it was pressed in (<see cref="ExtensionWindowContext.ForScriptEditor"/>).</param>
    /// <returns>Success, or the message to show (not installed, disabled, not running, not declared, not reading its input).</returns>
    public ExtensionCallResult InvokeWindowAction(ExtensionWindowActionBinding binding, ExtensionWindowContext context)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            lock (_gate)
            {
                if (_disposed) return ExtensionCallResult.Fail(ClosedMessage);
                Entry? entry = FindEntry(binding.ExtensionId);
                if (entry is null || entry.Removing) return ExtensionCallResult.Fail(NotInstalledMessage);
                if (!entry.Enabled || !entry.Manifest.IsValid) return ExtensionCallResult.Fail("Extension is disabled or has an invalid manifest");
                if (entry.Runtime is null || entry.Launching || entry.Runtime.HasExited) return ExtensionCallResult.Fail("Extension runtime is not running");
                bool declared = entry.Manifest.WindowActions.Any(action => action.Kind == binding.Action.Kind && action.Id == binding.Action.Id);
                if (!declared) return ExtensionCallResult.Fail("Window action is not declared by the extension");
                if (entry.Runtime.TrySend(ExtensionProtocol.WindowActionRequest(binding.Action.Id, context), out string error)) return ExtensionCallResult.Ok;
                AddLine(entry, error);
                return ExtensionCallResult.Fail(error);
            }
        }
        finally
        {
            Flush();
        }
    }

    /// <summary>Waits until every state write queued so far is on the disk.</summary>
    /// <returns>A task that never faults (write errors are logged).</returns>
    public Task FlushAsync()
    {
        lock (_gate) return _saveChain;
    }

    /// <summary>Stops every runtime in the background and makes the manager inert at once. Never throws.</summary>
    public void Dispose() => BeginDispose();

    /// <summary>Like <see cref="Dispose"/>, then waits until the runtime processes are gone and the state is written (bounded by a few seconds beyond the stop timeout).</summary>
    /// <returns>A task that ends when everything is cleaned up.</returns>
    public async ValueTask DisposeAsync()
    {
        Task all = BeginDispose();
        await Task.WhenAny(all, Task.Delay(_options.StopTimeout + TimeSpan.FromSeconds(10))).ConfigureAwait(false);
    }

    private Task BeginDispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                foreach (Entry entry in _entries) StopRuntime(entry);
                _pending.Clear();
            }
            return Task.WhenAll(_stopping.Append(_saveChain).ToArray());
        }
    }

    private void Launch(Entry entry)
    {
        if (entry.Runtime is not null || !entry.Manifest.IsValid) return;
        entry.RuntimeError = string.Empty;
        if (!TryBuildCommand(entry.Manifest, out IReadOnlyList<string> arguments, out string error))
        {
            entry.RuntimeError = error;
            AddLine(entry, error);
            EnqueueChanged(entry);
            return;
        }
        var callbacks = new ExtensionRuntime.Callbacks(OnStandardOutput, OnStandardError, OnExited, OnProblem);
        var runtime = new ExtensionRuntime(entry.Manifest.Directory, arguments, callbacks, _options.MaximumQueuedMessages, _options.MaximumQueuedBytes, _options.MaximumLineLength);
        entry.Runtime = runtime;
        entry.Launching = true;
        entry.StartSent = false;
        EnqueueChanged(entry);
        _ = CompleteLaunchAsync(runtime);
    }

    private async Task CompleteLaunchAsync(ExtensionRuntime runtime)
    {
        string? error;
        try
        {
            error = await runtime.StartAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = "Failed to launch extension process: " + exception.Message;
        }
        try
        {
            lock (_gate)
            {
                Entry? entry = FindByRuntime(runtime);
                if (entry is null) return;
                entry.Launching = false;
                if (error is not null)
                {
                    entry.Runtime = null;
                    entry.RuntimeError = error;
                    AddLine(entry, error);
                    TrackStop(runtime.StopAsync(Array.Empty<string>(), TimeSpan.Zero));
                }
                else if (!entry.Manifest.UsesGs2Engine && !runtime.TrySend(ExtensionProtocol.StartMessage, out string sendError))
                {
                    AddLine(entry, sendError);
                }
                EnqueueChanged(entry);
            }
        }
        catch (Exception exception)
        {
            _log.Error("Finishing an extension launch failed", exception);
        }
        finally
        {
            Flush();
        }
    }

    /// <summary>Builds the command line of a package as <c>launch</c> did (<c>TExtensions.cpp:765-785</c>).</summary>
    internal static bool TryBuildCommand(ExtensionManifest manifest, out IReadOnlyList<string> arguments, out string error)
    {
        arguments = Array.Empty<string>();
        error = string.Empty;
        if (manifest.UsesGs2Engine && manifest.Api != 1)
        {
            error = "Unsupported GS2 extension API: " + manifest.Api.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return false;
        }
        string entry = manifest.EntryPath;
        if (!manifest.AutoDiscover && (entry.Length == 0 || (!File.Exists(entry) && !Directory.Exists(entry))))
        {
            error = "Entry not found: " + (entry.Length == 0 ? Path.Combine(manifest.Directory, manifest.Entry) : entry);
            return false;
        }
        var list = new List<string>();
        if (manifest.UsesGs2Engine)
        {
            string host = Path.Combine(manifest.Directory, OperatingSystem.IsWindows() ? "GS2Engine.exe" : "GS2Engine");
            if (!File.Exists(host))
            {
                error = "GS2Engine host not found: " + host;
                return false;
            }
            list.Add(host);
            list.Add("--extension-stdio");
        }
        else
        {
            list.Add(manifest.Runtime.Length == 0 ? entry : manifest.Runtime);
            if (manifest.Runtime.Length > 0 && !manifest.AutoDiscover) list.Add(entry);
        }
        if (list[0].Length == 0)
        {
            error = "Extension command is empty";
            return false;
        }
        arguments = list;
        return true;
    }

    private void OnStandardOutput(ExtensionRuntime runtime, string raw)
    {
        try
        {
            lock (_gate)
            {
                Entry? entry = FindForOutput(runtime);
                if (entry is null) return;
                string line = ExtensionManifestLoader.Trim(raw);
                if (ReferenceEquals(entry.Runtime, runtime) && entry.Manifest.UsesGs2Engine && !entry.StartSent && ExtensionProtocol.IsReady(line))
                {
                    entry.StartSent = true;
                    if (!runtime.TrySend(ExtensionProtocol.HostStartRequest(entry.Manifest), out string error)) AddLine(entry, error);
                }
                string display = ExtensionProtocol.DisplayLine(line);
                if (display.Length > 0) AddLine(entry, display);
            }
        }
        finally
        {
            Flush();
        }
    }

    private void OnStandardError(ExtensionRuntime runtime, string raw)
    {
        try
        {
            lock (_gate)
            {
                Entry? entry = FindForOutput(runtime);
                if (entry is null) return;
                string line = ExtensionManifestLoader.Trim(raw);
                if (line.Length > 0) AddLine(entry, line);
            }
        }
        finally
        {
            Flush();
        }
    }

    private void OnExited(ExtensionRuntime runtime, int? exitCode)
    {
        try
        {
            lock (_gate)
            {
                foreach (Entry retiring in _entries) retiring.Retiring.Remove(runtime);
                Entry? entry = FindByRuntime(runtime);
                if (entry is null) return;
                entry.Runtime = null;
                entry.Launching = false;
                if (entry.Enabled)
                {
                    entry.RuntimeError = "Runtime exited unexpectedly";
                    AddLine(entry, entry.RuntimeError + (exitCode is null ? string.Empty : $" (exit code {exitCode})"));
                }
                TrackStop(runtime.StopAsync(Array.Empty<string>(), TimeSpan.Zero));
                EnqueueChanged(entry);
            }
        }
        finally
        {
            Flush();
        }
    }

    private void OnProblem(ExtensionRuntime runtime, string message)
    {
        try
        {
            lock (_gate)
            {
                Entry? entry = FindForOutput(runtime);
                _log.Warn($"Extension {(entry?.Manifest.Id ?? "(stopped)")}: {message}");
                if (entry is not null) AddLine(entry, message);
            }
        }
        finally
        {
            Flush();
        }
    }

    private Task StopRuntime(Entry entry)
    {
        ExtensionRuntime? runtime = entry.Runtime;
        entry.Runtime = null;
        entry.Launching = false;
        if (runtime is null) return Task.CompletedTask;
        if (!runtime.HasExited) entry.Retiring.Add(runtime);
        IReadOnlyList<string> messages = entry.Manifest.UsesGs2Engine
            ? new[] { ExtensionProtocol.HostStopRequest, ExtensionProtocol.HostShutdownRequest }
            : new[] { ExtensionProtocol.StopMessage };
        return TrackStop(runtime.StopAsync(messages, _options.StopTimeout));
    }

    private Task TrackStop(Task task)
    {
        if (task.IsCompleted) return task;
        _stopping.Add(task);
        task.ContinueWith(done => { lock (_gate) _stopping.Remove(done); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private string? DeletePackage(ExtensionManifest manifest)
    {
        try
        {
            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.Directory));
            string? parent = Path.GetDirectoryName(directory);
            StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            bool insideRoot = parent is not null && _options.PackageRoots.Any(root => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), parent, comparison));
            if (!insideRoot) return "The package folder is not directly inside an extensions folder";
            var info = new DirectoryInfo(directory);
            if (!info.Exists && info.LinkTarget is null) return null;
            if (info.LinkTarget is not null)
            {
                if (OperatingSystem.IsWindows()) Directory.Delete(directory, false);
                else File.Delete(directory);
                return null;
            }
            Directory.Delete(directory, true);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return exception.Message;
        }
    }

    private void QueueSave()
    {
        if (!_stateWritable) return;
        var entries = new List<ExtensionSavedState>();
        foreach (Entry entry in _entries)
        {
            if (entry.Removing) continue;
            entries.Add(new ExtensionSavedState(entry.Manifest.Id, entry.Enabled, entry.OutputToTab, entry.Approved));
        }
        entries.AddRange(_preserved.Values.OrderBy(value => value.Id, StringComparer.Ordinal));
        string text = ExtensionStateFile.Format(entries, _unreadableStateLines);
        string path = _options.StateFile;
        _saveChain = _saveChain.ContinueWith(_ =>
        {
            try
            {
                AtomicFile.WriteAllText(path, text);
            }
            catch (Exception exception)
            {
                _log.Error("The extension state could not be saved", exception);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void AddLine(Entry entry, string line)
    {
        if (line.Length == 0) return;
        bool wasEmpty = entry.Log.IsEmpty;
        entry.Log.Add(line);
        string id = entry.Manifest.Id;
        string name = entry.Manifest.Name;
        Enqueue(() => LogLine?.Invoke(this, new ExtensionOutputEventArgs(id, name, line)));
        if (entry.OutputToTab) Enqueue(() => OutputLine?.Invoke(this, new ExtensionOutputEventArgs(id, name, line)));
        if (wasEmpty) EnqueueChanged(entry);
    }

    private void EnqueueChanged(Entry entry)
    {
        string id = entry.Manifest.Id;
        Enqueue(() => Changed?.Invoke(this, new ExtensionChangedEventArgs(id)));
    }

    private void EnqueueTabOpened(Entry entry, IReadOnlyList<string> backlog)
    {
        string id = entry.Manifest.Id;
        string name = entry.Manifest.Name;
        Enqueue(() => OutputTabOpened?.Invoke(this, new ExtensionOutputTabEventArgs(id, name, backlog)));
    }

    private void EnqueueTabClosed(Entry entry)
    {
        string id = entry.Manifest.Id;
        string name = entry.Manifest.Name;
        Enqueue(() => OutputTabClosed?.Invoke(this, new ExtensionOutputTabEventArgs(id, name, Array.Empty<string>())));
    }

    private void Enqueue(Action action)
    {
        if (!_disposed) _pending.Enqueue(action);
    }

    /// <summary>Hands the queued events to the owner's context in the order they were queued; called after the state lock is released.</summary>
    private void Flush()
    {
        lock (_flushGate)
        {
            while (true)
            {
                Action? action;
                lock (_gate)
                {
                    if (!_pending.TryDequeue(out action)) return;
                }
                try
                {
                    _post(() =>
                    {
                        if (_disposed) return;
                        try
                        {
                            action();
                        }
                        catch (Exception exception)
                        {
                            _log.Error("An extension event handler failed", exception);
                        }
                    });
                }
                catch (Exception exception)
                {
                    _log.Error("An extension event could not be posted", exception);
                }
            }
        }
    }

    private Entry? FindEntry(string id) => _entries.FirstOrDefault(entry => string.Equals(entry.Manifest.Id, id, StringComparison.Ordinal));

    /// <summary>The entry a runtime's output belongs to: its current runtime, or one that is stopping (its last words are still logged).</summary>
    private Entry? FindForOutput(ExtensionRuntime runtime) => _disposed ? null : _entries.FirstOrDefault(entry => ReferenceEquals(entry.Runtime, runtime) || entry.Retiring.Contains(runtime));

    private Entry? FindByRuntime(ExtensionRuntime runtime) => _disposed ? null : _entries.FirstOrDefault(entry => ReferenceEquals(entry.Runtime, runtime));

    private static ExtensionInfo Snapshot(Entry entry)
    {
        ExtensionManifest manifest = entry.Manifest;
        ExtensionStatus status = !manifest.IsValid || entry.RuntimeError.Length > 0 ? ExtensionStatus.Failed
            : entry.Launching ? ExtensionStatus.Starting
            : entry.Runtime is not null ? ExtensionStatus.Running
            : entry.Enabled ? ExtensionStatus.Stopped
            : ExtensionStatus.Disabled;
        string error = manifest.Error.Length > 0 ? manifest.Error : entry.RuntimeError;
        return new ExtensionInfo(manifest, entry.Enabled, entry.OutputToTab, entry.Approved, status, error, !entry.Log.IsEmpty, entry.Launching ? null : entry.Runtime?.ProcessId);
    }

    private static Action<Action> PostTo(SynchronizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return action => context.Post(state => ((Action)state!)(), action);
    }

    private sealed class Entry
    {
        public Entry(ExtensionManifest manifest, int logLimit)
        {
            Manifest = manifest;
            Log = new BoundedLog(logLimit);
        }

        public ExtensionManifest Manifest { get; }

        public bool Enabled { get; set; }

        public bool OutputToTab { get; set; }

        public ImmutableSortedSet<string> Approved { get; set; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);

        public string RuntimeError { get; set; } = string.Empty;

        public BoundedLog Log { get; }

        public ExtensionRuntime? Runtime { get; set; }

        public bool Launching { get; set; }

        public bool StartSent { get; set; }

        public bool Removing { get; set; }

        public HashSet<ExtensionRuntime> Retiring { get; } = new();
    }
}
