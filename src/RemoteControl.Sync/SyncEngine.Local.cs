using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Sync;

public sealed partial class SyncEngine
{
    /// <summary>
    /// Looks at the workspace (everything on a full rescan, otherwise what the watcher reported) on a background task, then uploads local
    /// edits and deletes what was deleted, one at a time (<c>scanLocalChanges</c>, <c>scanLocalFiles</c>, <c>scanLocalConfig</c>).
    /// </summary>
    private async Task PushPassAsync(RunContext run, bool forced, bool onlyDirty)
    {
        bool full = _fullRescan && !onlyDirty;
        if (full)
        {
            _fullRescan = false;
            _nextRescan = DateTime.UtcNow + _options.RescanInterval;
        }

        var candidates = new List<TrackedItem>();
        if (full)
        {
            // A file still being written is read when it settled, not by the rescan that happens to come meanwhile.
            candidates.AddRange(AllItems().Where(item => !_settlingPaths.ContainsKey(item.Path)));
        }
        else
        {
            foreach (string path in _dirtyPaths)
            {
                if (_byPath.TryGetValue(path, out TrackedItem? item)) candidates.Add(item);
            }
        }

        _dirtyPaths.Clear();
        foreach (string key in _confirmedDeletions)
        {
            if (FindItem(key) is { } confirmed && !candidates.Contains(confirmed)) candidates.Add(confirmed);
        }

        candidates.RemoveAll(item => item switch
        {
            ScriptItem => !_settings.Scripts,
            FileItem file => !_settings.Files || _exclusions.IsExcluded(file.RemotePath),
            _ => !_settings.ServerConfig,
        });
        if (candidates.Count == 0) return;
        string workspace = WorkspacePathOrThrow();
        ScanRequest[] requests = candidates.Select(item => new ScanRequest(item.Key, FullPath(item),
            Path.Combine(workspace, SyncNames.NameOf(item.Path.Split('/')[0])), item.Stamp, item.LocalHash)).ToArray();
        long maxFileSize = _options.MaxFileSize;
        ScanResult[] results = await Task.Run(() => LocalFiles.Scan(requests, maxFileSize, run.Token), run.Token);
        bool uploads = forced || _settings.UploadsAllowed;
        var toUpload = new List<TrackedItem>();
        var toDelete = new List<TrackedItem>();
        DateTime now = DateTime.UtcNow;
        foreach (ScanResult result in results)
        {
            TrackedItem? item = FindItem(result.Key);
            if (item is null) continue;
            if (!result.Exists)
            {
                HandleMissing(item, result.FolderExists, now, toDelete);
                continue;
            }

            item.MissingSince = null;
            _missingWatch.Remove(item.Key);
            _confirmedDeletions.Remove(item.Key);
            if (item.State is SyncItemState.PendingDeletion or SyncItemState.MissingLocally) SetState(item, SyncItemState.InSync);
            item.Stamp = result.Stamp;
            item.LocalHash = result.Hash;
            if (result.Error is not null)
            {
                SetState(item, SyncItemState.Failed, result.Error);
                continue;
            }

            if (item.State == SyncItemState.Conflict) continue;
            if (item.SyncedLocalHash is null)
            {
                bool legacyUnchanged = item.LoadedModified > 0 && result.Stamp is { } stamp
                    && new DateTimeOffset(new DateTime(stamp.LastWriteTicks, DateTimeKind.Utc)).ToUnixTimeSeconds() == item.LoadedModified;
                if (legacyUnchanged && !_settings.DownloadsAllowed)
                {
                    item.SyncedLocalHash = item.LocalHash;
                    MarkManifestDirty();
                }
                else if (forced && uploads)
                {
                    toUpload.Add(item);
                }
                else if (!_settings.DownloadsAllowed)
                {
                    MarkLocallyModified(item, "Not known to be synchronised; use Push to upload it.");
                }

                continue;
            }

            if (item.LocalHash == item.SyncedLocalHash)
            {
                if (item.State == SyncItemState.LocallyModified) SetState(item, SyncItemState.InSync);
                continue;
            }

            if (!uploads)
            {
                MarkLocallyModified(item);
                continue;
            }

            toUpload.Add(item);
        }

        if (toUpload.Count > 0 && _settings.GitAutoPull && now - _gitPulledAt > TimeSpan.FromSeconds(10))
        {
            if (now < _gitPullRetryAt)
            {
                foreach (TrackedItem item in toUpload) MarkLocallyModified(item, "Waiting for the Git pull before uploads to succeed.");
                toUpload.Clear();
            }
            else
            {
                SetPhase("Pulling the Git repository before uploads");
                GitResult pulled = await CreateGit().PullBeforeUploadAsync(run.Token);
                run.Token.ThrowIfCancellationRequested();
                _gitPulledAt = DateTime.UtcNow;
                if (!pulled.Success)
                {
                    _gitPullRetryAt = DateTime.UtcNow + TimeSpan.FromMinutes(1);
                    Log("Sync Git pull failed; uploads wait (" + FirstLine(pulled.Output) + ")");
                    RaiseGit("Pull before uploads", pulled);
                    foreach (TrackedItem item in toUpload) MarkLocallyModified(item, "Waiting for the Git pull before uploads to succeed.");
                }
                else
                {
                    // The pull may have changed the files: look again before uploading.
                    foreach (TrackedItem item in toUpload) _dirtyPaths.Add(item.Path);
                }

                toUpload.Clear();
            }
        }

        foreach (TrackedItem item in toUpload)
        {
            run.Token.ThrowIfCancellationRequested();
            await UploadAsync(run, item);
        }

        if (toDelete.Count > 0) await DeleteAsync(run, toDelete);
    }

    private void HandleMissing(TrackedItem item, bool folderExists, DateTime now, List<TrackedItem> toDelete)
    {
        if (item.SyncedLocalHash is null) return;
        if (!_settings.RemoteDeletesEffective || item is ConfigItem)
        {
            if (item.State != SyncItemState.MissingLocally)
            {
                SetState(item, SyncItemState.MissingLocally, item is ConfigItem
                    ? "Deleted locally; configuration is never deleted on the server."
                    : "Deleted locally; the server copy is kept (remote deletes are off).");
            }

            return;
        }

        if (!folderExists)
        {
            // The whole folder is gone (an unmounted drive, a checkout): never a reason to delete server content.
            SetState(item, SyncItemState.MissingLocally, "Its folder is missing; nothing is deleted on the server.");
            return;
        }

        if (item.State == SyncItemState.PendingDeletion && !_confirmedDeletions.Contains(item.Key)) return;
        if (item.MissingSince is null)
        {
            item.MissingSince = now;
            _missingWatch.Add(item.Key);
            return;
        }

        if (now - item.MissingSince.Value < _options.DeletionDelay && !_confirmedDeletions.Contains(item.Key))
        {
            _missingWatch.Add(item.Key);
            return;
        }

        _missingWatch.Remove(item.Key);
        toDelete.Add(item);
    }

    private async Task DeleteAsync(RunContext run, List<TrackedItem> toDelete)
    {
        var automatic = toDelete.Where(item => !_confirmedDeletions.Contains(item.Key)).ToList();
        if (automatic.Count > _options.MaxAutomaticDeletions)
        {
            foreach (TrackedItem item in automatic)
                SetState(item, SyncItemState.PendingDeletion, "Deleted locally; confirm to delete it on the server.");
            Log($"{automatic.Count} local deletions are held; confirm them to delete the server copies");
            toDelete.RemoveAll(automatic.Contains);
        }

        foreach (TrackedItem item in toDelete)
        {
            run.Token.ThrowIfCancellationRequested();
            _confirmedDeletions.Remove(item.Key);
            bool deleted = item switch
            {
                ScriptItem script when run.Session.IsNcAuthenticated => script.ScriptKind switch
                {
                    ScriptKind.Class => await run.Session.DeleteClassAsync(script.ScriptName),
                    ScriptKind.Npc => await run.Session.DeleteNpcAsync(script.Id),
                    _ => await run.Session.DeleteWeaponAsync(script.ScriptName),
                },
                FileItem file => await DeleteFileAsync(run, file.RemotePath),
                _ => false,
            };
            run.Token.ThrowIfCancellationRequested();
            if (!deleted)
            {
                SetState(item, SyncItemState.Failed, "The deletion was not sent to the server.");
                Log("Unable to delete " + item.Path + " from server");
                continue;
            }

            _uploaded++;
            SetPhase("Deleted " + item.Path + " from server");
            Log("Deleted " + item.Path + " from server");
            RemoveItem(item);
        }
    }

    private async Task<bool> DeleteFileAsync(RunContext run, string remote)
    {
        await _browserGate.WaitAsync(run.Token);
        try
        {
            return await run.Browser.DeleteAsync(remote);
        }
        finally
        {
            _browserGate.Release();
        }
    }

    private async Task UploadAsync(RunContext run, TrackedItem item)
    {
        string path = FullPath(item);
        long maxFileSize = _options.MaxFileSize;
        (byte[]? content, string? error) = await Task.Run(() =>
        {
            byte[]? bytes = LocalFiles.ReadForUpload(path, maxFileSize, out string? readError);
            return (bytes, readError);
        }, CancellationToken.None);
        run.Token.ThrowIfCancellationRequested();
        if (content is null)
        {
            SetState(item, SyncItemState.Failed, "The file could not be read: " + error);
            return;
        }

        string hash = SyncHash.Of(content);
        if (hash != item.LocalHash)
        {
            // Changed again since the scan: look at it once more.
            _dirtyPaths.Add(item.Path);
            return;
        }

        IRcSession session = run.Session;
        string serverHash = hash;
        bool sent;
        switch (item)
        {
            case ScriptItem script:
            {
                string? body = ScriptDocument.ExtractScript(script.ScriptKind, RcText.GetString(content));
                if (body is null)
                {
                    SetState(item, SyncItemState.Failed, "The file has no SCRIPT and SCRIPTEND lines; it was not uploaded.");
                    return;
                }

                if (!session.IsNcAuthenticated)
                {
                    _waitingForNc = true;
                    MarkLocallyModified(item, "Waiting for the NPC-Server to upload it.");
                    return;
                }

                serverHash = SyncHash.Of(RcText.GetBytes(body));
                item.PendingUploadHash = serverHash;
                item.PendingUploadUntil = DateTime.UtcNow + _options.UploadEchoWindow;
                sent = script.ScriptKind switch
                {
                    ScriptKind.Class => await session.UpdateClassAsync(script.ScriptName, body),
                    ScriptKind.Npc => await session.UpdateNpcScriptAsync(script.Id, body),
                    _ => await session.UpdateWeaponAsync(script.ScriptName, script.Image, body),
                };
                break;
            }

            case FileItem file:
            {
                if (session.MaxUploadFileSize > 0 && content.LongLength > session.MaxUploadFileSize)
                {
                    SetState(item, SyncItemState.Failed, "The file is larger than the server's upload limit.");
                    return;
                }

                item.PendingUploadHash = hash;
                item.PendingUploadUntil = DateTime.UtcNow + _options.UploadEchoWindow;
                SetPhase("Uploading " + file.RemotePath);
                await _browserGate.WaitAsync(run.Token);
                try
                {
                    sent = await run.Browser.UploadAsync(file.RemotePath, content, run.Token);
                }
                finally
                {
                    _browserGate.Release();
                }

                if (sent) file.Size = content.LongLength;
                break;
            }

            case ConfigItem config:
            {
                string text = RcText.GetString(content);
                item.PendingUploadHash = hash;
                item.PendingUploadUntil = DateTime.UtcNow + _options.UploadEchoWindow;
                sent = config.ConfigKind switch
                {
                    SyncConfigKind.ServerFlags => await session.UploadServerFlagsAsync(text),
                    SyncConfigKind.FolderConfig => await session.UploadFolderConfigAsync(text),
                    _ => await session.UploadServerOptionsAsync(text),
                };
                break;
            }

            default:
                return;
        }

        if (!sent)
        {
            item.PendingUploadHash = null;
            item.PendingUploadUntil = default;
            SetState(item, SyncItemState.Failed, "The upload was not sent.");
            Log("Unable to upload " + item.Path);
            return;
        }

        item.SyncedLocalHash = hash;
        if (item is ScriptItem uploaded) uploaded.SyncedServerHash = serverHash;
        _uploaded++;
        MarkManifestDirty();
        SetState(item, SyncItemState.InSync);
        SetPhase("Uploaded " + item.Path);
        Log("Uploaded " + item.Path);
    }

    // ---- Conflicts ----------------------------------------------------------------------------------------------------------------------

    private async Task ResolveConflictCoreAsync(string key, ConflictResolution resolution)
    {
        TrackedItem? item = FindItem(key);
        if (item is null || item.State != SyncItemState.Conflict || item.ConflictServerHash is null) return;
        string serverHash = item.ConflictServerHash;
        if (resolution == ConflictResolution.KeepLocal)
        {
            // The local file is now an edit of the server's current version.
            if (item is ScriptItem script)
            {
                script.SyncedServerHash = serverHash;
                script.SyncedLocalHash ??= SyncHash.Of([]);
            }
            else
            {
                item.SyncedLocalHash = serverHash;
            }

            if (item is FileItem file && file.ConflictListing is { } listing) (file.Size, file.Modified) = listing;
            ClearConflict(item);
            SetState(item, SyncItemState.InSync);
            Log("Kept the local version of " + item.Path);
            _dirtyPaths.Add(item.Path);
            MarkManifestDirty();
            if (_run is null && _session is not null && WorkspacePath() is not null)
            {
                _oneShot = true;
                EnsureLoop();
            }

            Wake();
            return;
        }

        string path = FullPath(item);
        string copy = FullPath(ConflictPath(item));
        byte[]? content = item.ConflictContent;
        ApplyResult result = await Task.Run(() =>
        {
            try
            {
                byte[] server = content ?? File.ReadAllBytes(copy);
                if (File.Exists(path)) AtomicFile.WriteAllBytes(copy + ".local", File.ReadAllBytes(path));
                FileStamp? stamp = LocalFiles.Write(path, server, 0);
                return new ApplyResult(ApplyOutcome.Written, SyncHash.Of(server), stamp, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new ApplyResult(ApplyOutcome.Failed, null, null, exception.Message);
            }
        }, CancellationToken.None);
        if (result.Outcome == ApplyOutcome.Failed)
        {
            SetState(item, SyncItemState.Conflict, "The server copy could not be written: " + result.Error, item.ConflictCopy);
            return;
        }

        item.SyncedLocalHash = result.LocalHash;
        item.LocalHash = result.LocalHash;
        item.Stamp = result.Stamp;
        if (item is ScriptItem resolved) resolved.SyncedServerHash = serverHash;
        if (item is FileItem resolvedFile && resolvedFile.ConflictListing is { } values) (resolvedFile.Size, resolvedFile.Modified) = values;
        ClearConflict(item);
        _downloaded++;
        SetState(item, SyncItemState.InSync);
        Log("Used the server version of " + item.Path + "; the local version is in " + ConflictPath(item) + ".local");
        MarkManifestDirty();
        SaveManifestNow();
    }

    private static void ClearConflict(TrackedItem item)
    {
        item.ConflictContent = null;
        item.ConflictServerHash = null;
        if (item is FileItem file) file.ConflictListing = null;
    }

    private static string FirstLine(string text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        int newline = trimmed.IndexOf('\n');
        string line = newline < 0 ? trimmed : trimmed[..newline].TrimEnd('\r');
        return line.Length > 200 ? line[..200] : line;
    }
}
