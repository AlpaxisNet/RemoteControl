using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Sync;

public sealed partial class SyncEngine
{
    private async Task RequestScriptsAsync(RunContext run)
    {
        SetPhase("Requesting server scripts");
        IRcSession session = run.Session;
        int requested = 0;
        foreach (RcWeapon weapon in session.Weapons.ToArray())
        {
            run.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(weapon.Name)) continue;
            if (await Request(SyncNames.ScriptKey(ScriptKind.Weapon, weapon.Name, 0), () => session.RequestWeaponScriptAsync(weapon.Name))) requested++;
        }

        foreach (RcClass scriptClass in session.Classes.ToArray())
        {
            run.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(scriptClass.Name)) continue;
            if (await Request(SyncNames.ScriptKey(ScriptKind.Class, scriptClass.Name, 0), () => session.RequestClassScriptAsync(scriptClass.Name))) requested++;
        }

        foreach (RcNpc npc in session.Npcs.ToArray())
        {
            run.Token.ThrowIfCancellationRequested();
            if (await Request(SyncNames.ScriptKey(ScriptKind.Npc, npc.Name, npc.Id), () => session.RequestNpcScriptAsync(npc.Id))) requested++;
        }

        _lastArrival = DateTime.UtcNow;
        if (requested > 0) Log($"Requested {requested} scripts");
        Notify();
    }

    private async Task<bool> Request(string key, Func<Task<bool>> send)
    {
        if (!_expectedScripts.Add(key)) return false;
        _workTotal++;
        if (await send()) return true;
        if (_expectedScripts.Remove(key)) _workDone++;
        return false;
    }

    private async Task RequestConfigurationAsync(RunContext run)
    {
        IRcSession session = run.Session;
        foreach (SyncConfigKind kind in SyncConfigKinds.All)
        {
            run.Token.ThrowIfCancellationRequested();
            if (!_expectedConfig.Add(kind)) continue;
            _workTotal++;
            bool sent = kind switch
            {
                SyncConfigKind.ServerFlags => await session.RequestServerFlagsAsync(),
                SyncConfigKind.FolderConfig => await session.RequestFolderConfigAsync(),
                _ => await session.RequestServerOptionsAsync(),
            };
            if (!sent && _expectedConfig.Remove(kind)) _workDone++;
        }

        _lastArrival = DateTime.UtcNow;
        Log("Requested Server Options, Server Flags, and Folder Config");
    }

    // ---- Script modification dates ------------------------------------------------------------------------------------------------------

    private async Task ScriptMetadataAsync(RunContext run)
    {
        SetPhase("Checking script modification dates");
        IReadOnlyList<string>? roots = await GetRootsAsync(run);
        if (roots is null) return;
        var touched = new List<ScriptItem>();
        foreach (string wanted in new[] { "weapons", "scripts", "npcs" })
        {
            run.Token.ThrowIfCancellationRequested();
            bool readable = roots.Any(root => root == wanted || (wanted.Length > root.Length && wanted.StartsWith(root + "/", StringComparison.Ordinal)));
            if (!readable) continue;
            SetPhase("Checking " + wanted + " modification dates");
            IReadOnlyList<FileBrowserEntry>? files = await ListFolderAsync(run, wanted, _options.MetadataFolderTimeout);
            if (files is not null) touched.AddRange(ApplyScriptMetadata(wanted, files));
        }

        if (touched.Count == 0) return;
        MarkManifestDirty();
        var times = touched.Where(item => item.SyncedLocalHash is not null && item.Modified > 0)
            .Select(item => (Path: FullPath(item), Hash: item.SyncedLocalHash!, item.Modified)).ToArray();
        await Task.Run(() =>
        {
            foreach ((string path, string hash, long modified) in times)
            {
                try
                {
                    if (File.Exists(path) && SyncHash.OfFile(path) == hash) LocalFiles.SetTime(path, modified);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // The time is cosmetic; the next download sets it again.
                }
            }
        }, CancellationToken.None);
    }

    /// <summary><c>applyScriptMetadata</c>: file names of <c>weapons/</c>, <c>scripts/</c>, <c>npcs/</c> give the server times of the scripts.</summary>
    private List<ScriptItem> ApplyScriptMetadata(string folder, IReadOnlyList<FileBrowserEntry> files)
    {
        var touched = new List<ScriptItem>();
        ScriptKind kind = folder switch
        {
            "scripts" => ScriptKind.Class,
            "npcs" => ScriptKind.Npc,
            _ => ScriptKind.Weapon,
        };

        // The scripts of this kind by lower-case name (and NPCs also by "name-id"), so each listed file is one lookup.
        var byName = new Dictionary<string, ScriptItem>(StringComparer.Ordinal);
        foreach (ScriptItem item in _scripts.Values.Where(item => item.ScriptKind == kind).OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            string name = SyncNames.LowerAscii(item.ScriptName);
            byName.TryAdd(name, item);
            if (kind == ScriptKind.Npc && item.Id > 0) byName.TryAdd(name + "-" + item.Id, item);
        }

        foreach (FileBrowserEntry entry in files)
        {
            if (entry.IsDirectory || entry.Modified <= 0) continue;
            string stem = SyncNames.NameOf(SyncNames.NormalizeRemotePath(entry.Name));
            if (stem.Length >= 4 && SyncNames.LowerAscii(stem[^4..]) == ".txt") stem = stem[..^4];
            string lowerStem = SyncNames.LowerAscii(SyncNames.DecodeGraalFileName(stem));
            if (_scriptModifiedTimes.Count < 100000 || _scriptModifiedTimes.ContainsKey(folder + ":" + lowerStem)) _scriptModifiedTimes[folder + ":" + lowerStem] = entry.Modified;
            string candidate = lowerStem;
            if (kind == ScriptKind.Weapon && candidate.StartsWith("weapon", StringComparison.Ordinal)) candidate = candidate[6..];
            if (kind == ScriptKind.Npc && candidate.StartsWith("npc", StringComparison.Ordinal)) candidate = candidate[3..];
            if (!byName.TryGetValue(candidate, out ScriptItem? match)) continue;
            match.Modified = entry.Modified;
            touched.Add(match);
        }

        return touched;
    }

    private long? MetadataTime(ScriptKind kind, string name, int id)
    {
        string lower = SyncNames.LowerAscii(name);
        string[] keys = kind switch
        {
            ScriptKind.Class => ["scripts:" + lower],
            ScriptKind.Npc => ["npcs:npc" + lower + "-" + id, "npcs:npc" + lower],
            _ => ["weapons:weapon" + lower],
        };
        foreach (string key in keys)
        {
            if (_scriptModifiedTimes.TryGetValue(key, out long modified)) return modified;
        }

        return null;
    }

    // ---- Script arrivals ----------------------------------------------------------------------------------------------------------------

    private async Task ApplyScriptArrivalAsync(RunContext run, ScriptReceivedEventArgs arrival)
    {
        IRcSession session = run.Session;
        ScriptKind kind = arrival.Kind;
        int id = kind == ScriptKind.Npc ? arrival.Id : 0;
        string name = arrival.Name ?? string.Empty;
        RcNpc? npc = kind == ScriptKind.Npc ? session.Npcs.FirstOrDefault(candidate => candidate.Id == id) : null;
        if (kind == ScriptKind.Npc && name.Length == 0 && npc is not null) name = npc.Name;
        string key = SyncNames.ScriptKey(kind, name, id);
        if (_expectedScripts.Remove(key)) _workDone++;
        Notify();
        ScriptItem? item = GetOrCreateScript(kind, name, id, out string? refusal);
        if (item is null)
        {
            Log($"Refused {SyncNames.TypeName(kind)} {name}: {refusal}");
            return;
        }

        await RenameIfNeededAsync(item);
        item.ScriptName = name;
        item.Image = kind switch
        {
            ScriptKind.Weapon => arrival.Image.Length != 0 ? arrival.Image : session.Weapons.FirstOrDefault(weapon => weapon.Name == name)?.Image ?? item.Image,
            ScriptKind.Npc => arrival.Image,
            _ => string.Empty,
        };
        item.NpcType = kind == ScriptKind.Npc ? npc?.Type ?? item.NpcType : string.Empty;
        item.Level = string.Empty;
        if (MetadataTime(kind, name, id) is long modified) item.Modified = modified;

        string script = arrival.Script ?? string.Empty;
        string serverHash = SyncHash.Of(RcText.GetBytes(script));
        if (item.PendingUploadUntil > DateTime.UtcNow)
        {
            if (serverHash != item.PendingUploadHash) return;
            item.PendingUploadHash = null;
            item.PendingUploadUntil = default;
        }

        if (item.State == SyncItemState.Conflict && serverHash == item.ConflictServerHash) return;
        string? flags = kind == ScriptKind.Npc && session.NpcFlags.TryGetValue(id, out string? cached) ? cached : null;
        string document = kind switch
        {
            ScriptKind.Weapon => ScriptDocument.BuildWeapon(item.ScriptName, item.Image, script),
            ScriptKind.Npc => ScriptDocument.BuildNpc(item.ScriptName, id, item.NpcType, item.Image, item.Level, flags, script),
            _ => script,
        };
        byte[] bytes = RcText.GetBytes(document);
        string documentHash = SyncHash.Of(bytes);
        var input = new ApplyInput(FullPath(item), bytes, documentHash, item.SyncedLocalHash, serverHash == item.SyncedServerHash,
            item.SyncedLocalHash is null ? item.LoadedModified : 0, item.SyncedLocalHash is not null && _settings.RemoteDeletesEffective,
            FullPath(ConflictPath(item)), item.Modified);
        ApplyResult result = await Task.Run(() => LocalFiles.ApplyServerVersion(input), CancellationToken.None);
        RecordApply(item, result, bytes, documentHash, serverHash);
        MarkManifestDirty();
    }

    /// <summary>Records what applying a server version did; <paramref name="serverHash"/> is the server script's hash (or the content's).</summary>
    private void RecordApply(TrackedItem item, ApplyResult result, byte[] serverContent, string localHash, string serverHash)
    {
        switch (result.Outcome)
        {
            case ApplyOutcome.Written:
            case ApplyOutcome.Unchanged:
                item.SyncedLocalHash = localHash;
                if (item is ScriptItem script) script.SyncedServerHash = serverHash;
                item.LocalHash = localHash;
                item.Stamp = result.Stamp;
                item.ConflictContent = null;
                item.ConflictServerHash = null;
                item.MissingSince = null;
                if (result.Outcome == ApplyOutcome.Written)
                {
                    _downloaded++;
                    _downloadsThisPass++;
                    Log("Downloaded " + item.Path);
                }

                SetState(item, SyncItemState.InSync);
                break;
            case ApplyOutcome.LocalModified:
                item.LocalHash = result.LocalHash;
                item.Stamp = result.Stamp;
                if (_settings.UploadsAllowed) _dirtyPaths.Add(item.Path);
                else MarkLocallyModified(item);
                break;
            case ApplyOutcome.Conflict:
                item.LocalHash = result.LocalHash;
                item.Stamp = result.Stamp;
                item.ConflictContent = serverContent;
                item.ConflictServerHash = serverHash;
                string copy = ConflictPath(item);
                string detail = item.SyncedLocalHash is null
                    ? "The local file differs from the server and was never synchronised; the server copy is in " + copy + "."
                    : "Changed locally and on the server; the local file is kept and the server copy is in " + copy + ".";
                if (item.State != SyncItemState.Conflict) Log("Conflict: " + item.Path + " changed locally and on the server");
                SetState(item, SyncItemState.Conflict, detail, copy);
                break;
            case ApplyOutcome.DeletedLocally:
                _dirtyPaths.Add(item.Path);
                break;
            default:
                Log("Unable to write " + item.Path + ": " + result.Error);
                SetState(item, SyncItemState.Failed, "Unable to write the file: " + result.Error);
                break;
        }
    }

    private void MarkLocallyModified(TrackedItem item, string? detail = null)
    {
        string text = detail ?? "Changed locally; not uploaded (download only).";
        if (item.State != SyncItemState.LocallyModified || item.Detail != text) Log("Kept local change to " + item.Path + (detail is null ? " (download only)" : ": " + detail));
        SetState(item, SyncItemState.LocallyModified, text);
    }

    /// <summary>The item of a script, created with a path that is unique and safe; <c>null</c> when no such path exists.</summary>
    private ScriptItem? GetOrCreateScript(ScriptKind kind, string name, int id, out string? refusal)
    {
        refusal = null;
        string key = SyncNames.ScriptKey(kind, name, id);
        if (_scripts.TryGetValue(key, out ScriptItem? existing)) return existing;
        string? path = ChooseScriptPath(kind, name, id, null);
        if (path is null)
        {
            refusal = "no safe file name";
            return null;
        }

        var item = new ScriptItem(kind, name, id, path);
        AddItem(item);
        MarkManifestDirty();
        return item;
    }

    private string? ChooseScriptPath(ScriptKind kind, string name, int id, TrackedItem? owner)
    {
        foreach (string candidate in new[] { SyncNames.ScriptPath(kind, name), SyncNames.AlternateScriptPath(kind, name, id) })
        {
            if (!SyncNames.IsSafeWorkspacePath(candidate)) continue;
            if (_byPath.TryGetValue(candidate, out TrackedItem? other) && !ReferenceEquals(other, owner)) continue;
            return candidate;
        }

        return null;
    }

    /// <summary>An NPC renamed on the server moves its file (<c>addOrUpdateEntry</c> renamed it too).</summary>
    private async Task RenameIfNeededAsync(ScriptItem item)
    {
        if (item.Path == SyncNames.ScriptPath(item.ScriptKind, item.ScriptName) || item.Path == SyncNames.AlternateScriptPath(item.ScriptKind, item.ScriptName, item.Id)) return;
        string? desired = ChooseScriptPath(item.ScriptKind, item.ScriptName, item.Id, item);
        if (desired is null || desired == item.Path) return;
        string from = FullPath(item);
        string to = FullPath(desired);
        bool moved = await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(from)) return true;
                if (File.Exists(to)) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }, CancellationToken.None);
        if (!moved) return;
        if (_byPath.TryGetValue(item.Path, out TrackedItem? current) && ReferenceEquals(current, item)) _byPath.Remove(item.Path);
        item.Path = desired;
        _byPath[desired] = item;
        MarkManifestDirty();
    }

    // ---- Configuration arrivals -----------------------------------------------------------------------------------------------------------

    private async Task ApplyConfigArrivalAsync(RunContext run, SyncConfigKind kind, string content)
    {
        _ = run;
        if (_expectedConfig.Remove(kind)) _workDone++;
        if (!_configs.TryGetValue(kind, out ConfigItem? item))
        {
            item = new ConfigItem(kind);
            AddItem(item);
        }

        byte[] bytes = RcText.GetBytes(content);
        string hash = SyncHash.Of(bytes);
        if (item.PendingUploadUntil > DateTime.UtcNow)
        {
            if (hash != item.PendingUploadHash) return;
            item.PendingUploadHash = null;
            item.PendingUploadUntil = default;
        }

        if (item.State == SyncItemState.Conflict && hash == item.ConflictServerHash) return;
        bool serverChanged = item.SyncedLocalHash is not null && hash != item.SyncedLocalHash;
        var input = new ApplyInput(FullPath(item), bytes, hash, item.SyncedLocalHash, hash == item.SyncedLocalHash, 0, false, FullPath(ConflictPath(item)), 0);
        ApplyResult result = await Task.Run(() => LocalFiles.ApplyServerVersion(input), CancellationToken.None);
        RecordApply(item, result, bytes, hash, hash);
        MarkManifestDirty();
        if (!serverChanged) return;
        try
        {
            ServerConfigurationChanged?.Invoke(this, new ServerConfigurationChangedEventArgs(kind, content));
        }
        catch (Exception exception)
        {
            ReportInternalError(exception);
        }
    }
}
