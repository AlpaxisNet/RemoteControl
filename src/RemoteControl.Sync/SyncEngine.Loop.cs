using RemoteControl.Core;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Sync;

public sealed partial class SyncEngine
{
    /// <summary>One run of the work loop: the session it works with and the token that ends it.</summary>
    private sealed class RunContext(IRcSession session, CancellationTokenSource cancellation)
    {
        public IRcSession Session { get; } = session;

        public IRcFileBrowser Browser { get; } = session.SyncFileBrowser;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public CancellationToken Token => Cancellation.Token;
    }

    private bool CanRun()
    {
        if (_disposed) return false;
        if (_session is null)
        {
            Log("Connect to a server first.");
            return false;
        }

        if (WorkspacePath() is null)
        {
            SetPhase("No workspace");
            Log("Choose a sync workspace first.");
            return false;
        }

        return true;
    }

    private void StartCore()
    {
        if (!CanRun()) return;
        _enabled = true;
        _wantRunning = true;
        _oneShot = false;
        _pullRequested = true;
        _fullRescan = true;
        SetPhase("Starting synchronization");
        Log("Synchronization started");
        SaveSettingsSoon();
        EnsureLoop();
        StartWatcher();
    }

    private void StopCore(bool log)
    {
        bool wasActive = _enabled || _oneShot;
        _enabled = false;
        _oneShot = false;
        _wantRunning = false;
        _progressUnseen = false;
        CancelRun();
        StopWatcher();
        ClearPassState();
        SaveManifestNow();
        if (!log) return;
        SetPhase("Stopped");
        if (wasActive) Log("Synchronization stopped");
        Notify();
    }

    private void EnsureLoop()
    {
        if (_run is not null || _session is null || _disposed) return;
        var run = new RunContext(_session, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        _run = run;
        _ = LoopAsync(run);
        Notify();
    }

    private void CancelRun()
    {
        RunContext? run = _run;
        _run = null;
        if (run is null) return;

        // Cancelled from a posted callback, so that no continuation of the loop runs inside the caller (a detach in progress).
        _context.Post(_ =>
        {
            try
            {
                run.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException exception)
            {
                AppLog.Default.Warn("Stopping the sync work raised an error.", exception);
            }
        }, null);
        Wake();
        CompleteIdleWaiters();
    }

    private void ClearPassState()
    {
        _pullRequested = _pullForced = _pushRequested = _pushForced = _downloadsForced = false;
        _fullRescan = false;
        _dirtyPaths.Clear();
        _scriptArrivals.Clear();
        _scriptArrivalOrder.Clear();
        _configArrivals.Clear();
        _expectedScripts.Clear();
        _expectedConfig.Clear();
        _currentItem = null;
        _currentProgress = null;
        _queuedFolders = 0;
        _queuedDownloads = 0;
        _fileSyncActive = false;
        _waitingForNc = false;
    }

    private void ResetProgress()
    {
        _workDone = 0;
        _workTotal = 0;
        _passStarted = DateTime.UtcNow;
    }

    private bool IsIdle() =>
        !_pullRequested && !_pushRequested && !_fullRescan && _dirtyPaths.Count == 0 && _scriptArrivals.Count == 0 && _configArrivals.Count == 0
        && _missingWatch.Count == 0 && _confirmedDeletions.Count == 0;

    private void CompleteIdleWaiters()
    {
        foreach (TaskCompletionSource waiter in _idleWaiters) waiter.TrySetResult();
        _idleWaiters.Clear();
    }

    private bool IsCurrent(RunContext run) => ReferenceEquals(_run, run) && !run.Token.IsCancellationRequested;

    private async Task LoopAsync(RunContext run)
    {
        try
        {
            string workspace = WorkspacePathOrThrow();
            string? error = await Task.Run(() => LocalFiles.PrepareWorkspace(workspace), run.Token);
            if (error is not null) Log("The workspace could not be prepared: " + error);
            _nextRescan = DateTime.UtcNow + _options.RescanInterval;
            _nextPull = DateTime.UtcNow + _options.PullInterval;
            while (IsCurrent(run))
            {
                if (_pullRequested)
                {
                    bool forced = _pullForced;
                    _pullRequested = _pullForced = false;
                    await PullPassAsync(run, forced);
                    continue;
                }

                await DrainArrivalsAsync(run);
                if (_pushRequested || _fullRescan || _dirtyPaths.Count > 0 || _confirmedDeletions.Count > 0)
                {
                    bool forced = _pushForced;
                    _pushRequested = _pushForced = false;
                    await PushPassAsync(run, forced, onlyDirty: false);
                }

                SaveManifestIfDue();
                if (!IsCurrent(run)) break;
                if (IsIdle())
                {
                    if (!_enabled)
                    {
                        FinishOneShot(run);
                        break;
                    }

                    CompleteIdleWaiters();
                }

                await WaitAsync(run);
                Tick();
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportInternalError(exception);
            if (ReferenceEquals(_run, run))
            {
                _enabled = false;
                _oneShot = false;
                _run = null;
                SetPhase("Stopped after an error");
            }
        }
        finally
        {
            if (ReferenceEquals(_run, run)) _run = null;
            run.Cancellation.Dispose();
            SaveManifestNow();
            CompleteIdleWaiters();
            Notify();
        }
    }

    private string WorkspacePathOrThrow() => WorkspacePath() ?? throw new InvalidOperationException("No workspace.");

    private void FinishOneShot(RunContext run)
    {
        if (!ReferenceEquals(_run, run)) return;
        _oneShot = false;
        _run = null;
        StopWatcher();
        SetPhase("Sync complete");
        Log("Sync complete");
        SaveManifestNow();
    }

    private async Task WaitAsync(RunContext run)
    {
        var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wake = wake;
        try
        {
            await Task.WhenAny(wake.Task, Task.Delay(_options.TickInterval, run.Token));
        }
        finally
        {
            if (ReferenceEquals(_wake, wake)) _wake = null;
        }

        run.Token.ThrowIfCancellationRequested();
    }

    private void Tick()
    {
        DateTime now = DateTime.UtcNow;
        if (_enabled && now >= _nextPull) _pullRequested = true;
        if (_enabled && now >= _nextRescan) _fullRescan = true;
        foreach (string key in _missingWatch.ToArray())
        {
            TrackedItem? item = FindItem(key);
            if (item?.MissingSince is not DateTime since)
            {
                _missingWatch.Remove(key);
                continue;
            }

            if (now - since >= _options.DeletionDelay) _dirtyPaths.Add(item.Path);
        }

        if (_currentItem is not null || _workDone < _workTotal) Notify();
    }

    // ---- Event handlers (on the context) ----------------------------------------------------------------------------------------------

    private bool Active => (_enabled || _oneShot) && _run is not null;

    private bool DownloadsAccepted => _settings.DownloadsAllowed || _downloadsForced;

    private void HandleNcConnected()
    {
        if (!Active) return;
        if (_waitingForNc)
        {
            _waitingForNc = false;
            _pullRequested = true;
        }

        _fullRescan = true;
        Wake();
        Notify();
    }

    private void HandleScriptReceived(ScriptReceivedEventArgs e)
    {
        if (!Active || !_settings.Scripts || !DownloadsAccepted) return;
        string key = SyncNames.ScriptKey(e.Kind, e.Name, e.Id);
        if (!_scriptArrivals.ContainsKey(key))
        {
            if (_scriptArrivals.Count >= 20000) return;
            _scriptArrivalOrder.Enqueue(key);
        }

        _scriptArrivals[key] = e;
        _lastArrival = DateTime.UtcNow;
        Wake();
    }

    private void HandleConfigReceived(SyncConfigKind kind, string content)
    {
        if (!Active || !_settings.ServerConfig || !DownloadsAccepted) return;
        _configArrivals[kind] = content ?? string.Empty;
        _lastArrival = DateTime.UtcNow;
        Wake();
    }

    private void HandleTransferProgress(FileTransferProgressEventArgs e)
    {
        if (_currentItem is null || e.Upload || !string.Equals(SyncNames.NormalizeRemotePath(e.Path), _currentItem, StringComparison.Ordinal)) return;
        _currentProgress = new TransferProgress(e.Transferred, e.Total);
        Notify();
    }

    // ---- The pull pass ------------------------------------------------------------------------------------------------------------------

    private async Task PullPassAsync(RunContext run, bool forced)
    {
        ResetProgress();
        _progressUnseen = true;
        _downloadsThisPass = 0;
        _passRoots = null;
        _refusedFolders.Clear();
        _nextPull = DateTime.UtcNow + _options.PullInterval;
        if (!forced && !_settings.DownloadsAllowed)
        {
            SetPhase("Watching for local changes");
            return;
        }

        _downloadsForced = forced;
        try
        {
            IRcSession session = run.Session;
            bool nc = session.IsNcAuthenticated;
            if ((_settings.Scripts || _settings.ServerConfig) && !nc)
            {
                _waitingForNc = true;
                Log("Waiting for the NPC-Server: scripts and configuration are pulled when it is connected");
            }

            if (_settings.Scripts && nc) await RequestScriptsAsync(run);
            if (_settings.ServerConfig && nc) await RequestConfigurationAsync(run);
            if (_settings.Scripts && nc) await ScriptMetadataAsync(run);
            if (_settings.Files) await FilePassAsync(run);
            await WaitForArrivalsAsync(run);
            SetPhase("Watching for changes");
            SaveManifestNow();
            if (_downloadsThisPass > 0 && _settings.GitAutoCommit) StartAutoCommit();
        }
        finally
        {
            _downloadsForced = false;
            _passRoots = null;
            _refusedFolders.Clear();
            Notify();
        }
    }

    private async Task WaitForArrivalsAsync(RunContext run)
    {
        if (_expectedScripts.Count == 0 && _expectedConfig.Count == 0) return;
        SetPhase("Receiving scripts");
        _lastArrival = DateTime.UtcNow;
        while (_expectedScripts.Count > 0 || _expectedConfig.Count > 0)
        {
            run.Token.ThrowIfCancellationRequested();
            await DrainArrivalsAsync(run);
            if (_expectedScripts.Count == 0 && _expectedConfig.Count == 0) break;
            if (DateTime.UtcNow - _lastArrival >= _options.ArrivalTimeout)
            {
                int missing = _expectedScripts.Count + _expectedConfig.Count;
                Log($"{missing} requested scripts or texts did not arrive");
                _workDone += missing;
                _expectedScripts.Clear();
                _expectedConfig.Clear();
                break;
            }

            if (_dirtyPaths.Count > 0) await PushPassAsync(run, forced: false, onlyDirty: true);
            await WaitAsync(run);
        }
    }

    private async Task DrainArrivalsAsync(RunContext run)
    {
        while (_scriptArrivalOrder.Count > 0)
        {
            run.Token.ThrowIfCancellationRequested();
            string key = _scriptArrivalOrder.Dequeue();
            if (!_scriptArrivals.Remove(key, out ScriptReceivedEventArgs? arrival)) continue;
            await ApplyScriptArrivalAsync(run, arrival);
        }

        while (_configArrivals.Count > 0)
        {
            run.Token.ThrowIfCancellationRequested();
            KeyValuePair<SyncConfigKind, string> arrival = _configArrivals.First();
            _configArrivals.Remove(arrival.Key);
            await ApplyConfigArrivalAsync(run, arrival.Key, arrival.Value);
        }
    }

    // ---- File watcher ---------------------------------------------------------------------------------------------------------------------

    private void StartWatcher()
    {
        StopWatcher();
        if (!_options.WatchFileSystem) return;
        string? workspace = WorkspacePath();
        if (workspace is null) return;
        try
        {
            Directory.CreateDirectory(workspace);
            var watcher = new FileSystemWatcher(workspace)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += (_, e) => OnWatcherPath(watcher, workspace, e.FullPath);
            watcher.Created += (_, e) => OnWatcherPath(watcher, workspace, e.FullPath);
            watcher.Deleted += (_, e) => OnWatcherPath(watcher, workspace, e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                OnWatcherPath(watcher, workspace, e.OldFullPath);
                OnWatcherPath(watcher, workspace, e.FullPath);
            };
            watcher.Error += (_, _) => Dispatch(() =>
            {
                if (!ReferenceEquals(_watcher, watcher)) return;
                _fullRescan = true;
                Wake();
            });
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Log("The workspace cannot be watched (" + exception.Message + "); it is scanned every " + _options.RescanInterval.TotalSeconds + " seconds.");
        }
    }

    private void OnWatcherPath(FileSystemWatcher watcher, string workspace, string fullPath)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(workspace, fullPath).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return;
        }

        if (relative.StartsWith(SyncNames.MetadataFolder, StringComparison.Ordinal) || relative.StartsWith(".git", StringComparison.Ordinal) || relative.StartsWith("..", StringComparison.Ordinal)) return;
        Dispatch(() =>
        {
            if (!ReferenceEquals(_watcher, watcher)) return;
            if (!_byPath.ContainsKey(relative))
            {
                // A folder that was renamed or removed: look at everything.
                if (!SyncNames.NameOf(relative).Contains('.', StringComparison.Ordinal))
                {
                    _fullRescan = true;
                    Wake();
                }

                return;
            }

            if (_dirtyPaths.Count < 10000) _dirtyPaths.Add(relative);
            else _fullRescan = true;
            Wake();
        });
    }

    private void StopWatcher()
    {
        FileSystemWatcher? watcher = _watcher;
        _watcher = null;
        if (watcher is null) return;
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException)
        {
            AppLog.Default.Warn("The workspace watcher could not be stopped.", exception);
        }
    }
}
