using System.Collections.Concurrent;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;

namespace RemoteControl.Sync.Tests;

/// <summary>A temporary folder, removed on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rc-sync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>What the fake server holds; the responder answers the engine's requests from it.</summary>
internal sealed class FakeServer
{
    public ConcurrentDictionary<string, (string Image, string Script)> Weapons { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, string> Classes { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<int, (string Name, string Type, string Script)> Npcs { get; } = new();

    public ConcurrentDictionary<int, string> NpcFlags { get; } = new();

    public ConcurrentDictionary<SyncConfigKind, string> Config { get; } = new();

    public List<FileBrowserFolder> Roots { get; } = new();

    public ConcurrentDictionary<string, FileBrowserEntry[]> Listings { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, bool> SilentFolders { get; } = new(StringComparer.Ordinal);

    public void AddFile(string remote, string content, long modified)
    {
        string folder = SyncNames.ParentOf(remote);
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes(content);
        Files[remote] = bytes;
        FileBrowserEntry entry = new(SyncNames.NameOf(remote), "rw", bytes.Length, modified);
        Listings.AddOrUpdate(folder, _ => new[] { entry }, (_, old) => old.Where(item => item.Name != entry.Name).Append(entry).ToArray());
    }

    public void AddFolder(string parent, string name) =>
        Listings.AddOrUpdate(parent, _ => new[] { new FileBrowserEntry(name + "/", "rw", 0, 0) }, (_, old) => old.Append(new FileBrowserEntry(name + "/", "rw", 0, 0)).ToArray());
}

/// <summary>An engine on a private context, a fake session, and a responder playing the server.</summary>
internal sealed class SyncHarness : IAsyncDisposable
{
    public const string ServerName = "TestServer";

    private readonly CancellationTokenSource _stop = new();
    private readonly List<SyncSnapshot> _snapshots = new();
    private readonly bool _ownsFolder;
    private Task _responder = Task.CompletedTask;
    private CancellationTokenSource _responderStop = new();

    private SyncHarness(TempFolder folder, bool ownsFolder, FakeServer server, SyncEngineOptions options)
    {
        Folder = folder;
        _ownsFolder = ownsFolder;
        Server = server;
        Context = new PumpSynchronizationContext("test sync context");
        Engine = new SyncEngine(options with { Context = Context });
        Engine.StateChanged += (_, e) =>
        {
            lock (_snapshots) _snapshots.Add(e.Snapshot);
        };
        Session = NewSession();
    }

    public TempFolder Folder { get; }

    public FakeServer Server { get; }

    public PumpSynchronizationContext Context { get; }

    public SyncEngine Engine { get; }

    public FakeRcSession Session { get; private set; }

    public string UserDirectory => Path.Combine(Folder.Path, "user");

    public string Workspace => Path.Combine(Folder.Path, "ws");

    public IReadOnlyList<SyncSnapshot> Snapshots
    {
        get
        {
            lock (_snapshots) return _snapshots.ToArray();
        }
    }

    public static SyncEngineOptions DefaultOptions(string userDirectory) => new()
    {
        UserDirectory = userDirectory,
        TickInterval = TimeSpan.FromMilliseconds(20),
        RescanInterval = TimeSpan.FromMinutes(10),
        FileRootsTimeout = TimeSpan.FromSeconds(2),
        FolderListTimeout = TimeSpan.FromSeconds(2),
        MetadataFolderTimeout = TimeSpan.FromMilliseconds(300),
        ArrivalTimeout = TimeSpan.FromSeconds(1),
        DownloadTimeout = TimeSpan.FromSeconds(1),
        DeletionDelay = TimeSpan.FromMilliseconds(50),
        ManifestSaveDelay = TimeSpan.Zero,
        WatchFileSystem = false,
    };

    /// <summary>Creates a harness; the settings are written where the engine reads them (as an existing user's file would be).</summary>
    public static async Task<SyncHarness> CreateAsync(SyncSettings settings, FakeServer? server = null, TempFolder? folder = null, Func<SyncEngineOptions, SyncEngineOptions>? configure = null, bool attach = true)
    {
        TempFolder used = folder ?? new TempFolder();
        string user = Path.Combine(used.Path, "user");
        SyncEngineOptions options = DefaultOptions(user);
        if (configure is not null) options = configure(options);
        var harness = new SyncHarness(used, folder is null, server ?? new FakeServer(), options);
        SyncSettingsFile.Save(SyncSettingsFile.PathFor(user, ServerName), settings with { Workspace = harness.Workspace });
        if (attach) await harness.AttachAsync(harness.Session);
        return harness;
    }

    public FakeRcSession NewSession()
    {
        var session = new FakeRcSession { IsNcConnected = true, IsNcAuthenticated = true };
        return session;
    }

    /// <summary>Attaches a session and starts answering its requests.</summary>
    public async Task AttachAsync(FakeRcSession session)
    {
        _responderStop.Cancel();
        await _responder;
        Session = session;
        ServeFiles(session);
        await OnContext(SyncCaches);
        _responderStop = new CancellationTokenSource();
        CancellationToken token = CancellationTokenSource.CreateLinkedTokenSource(_responderStop.Token, _stop.Token).Token;
        _responder = Task.Run(() => RespondAsync(session, token));
        await Engine.AttachAsync(session, ServerName).WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Copies the server's lists into the session's caches (on the engine's context).</summary>
    public void SyncCaches()
    {
        FakeRcSession session = Session;
        session.WeaponList.Clear();
        session.WeaponList.AddRange(Server.Weapons.Select(pair => new RcWeapon(pair.Key, pair.Value.Image, string.Empty)));
        session.ClassList.Clear();
        session.ClassList.AddRange(Server.Classes.Keys.Select(name => new RcClass(name, string.Empty)));
        session.NpcList.Clear();
        session.NpcList.AddRange(Server.Npcs.Select(pair => new RcNpc(pair.Key, pair.Value.Name, pair.Value.Type, string.Empty, string.Empty)));
        session.NpcFlagMap.Clear();
        foreach (KeyValuePair<int, string> flags in Server.NpcFlags) session.NpcFlagMap[flags.Key] = flags.Value;
    }

    public Task OnContext(Action action) => Engine.RunOnContextAsync(action);

    /// <summary>The current snapshot, read on the engine's context.</summary>
    public async Task<SyncSnapshot> SnapshotAsync()
    {
        SyncSnapshot? snapshot = null;
        await OnContext(() => snapshot = Engine.Snapshot);
        return snapshot!;
    }

    /// <summary>One pass (Sync now) and the wait for it.</summary>
    public async Task SyncNowAsync()
    {
        await OnContext(SyncCaches);
        Engine.SyncNow();
        await IdleAsync();
    }

    public async Task IdleAsync()
    {
        await Engine.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await Engine.FlushAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    public async Task WaitUntilAsync(Func<bool> condition, string what, double seconds = 10)
    {
        DateTime end = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (true)
        {
            bool done = false;
            await OnContext(() => done = condition());
            if (done) return;
            if (DateTime.UtcNow > end) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    public string Local(string relative) => Path.Combine(Workspace, relative.Replace('/', Path.DirectorySeparatorChar));

    public string ReadLocal(string relative) => File.ReadAllText(Local(relative), System.Text.Encoding.Latin1);

    public void WriteLocal(string relative, string content)
    {
        string path = Local(relative);
        File.WriteAllText(path, content, System.Text.Encoding.Latin1);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
    }

    public async Task<SyncItemStatus> StatusOf(string key) => (await Engine.GetItemStatusesAsync().WaitAsync(TimeSpan.FromSeconds(5))).Single(item => item.Key == key);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _responder;
        }
        catch (OperationCanceledException)
        {
        }

        await Engine.DisposeAsync();
        if (_ownsFolder) Folder.Dispose();
    }

    private async Task RespondAsync(FakeRcSession session, CancellationToken token)
    {
        int seen = 0;
        while (!token.IsCancellationRequested)
        {
            IReadOnlyList<RecordedCall> calls = session.Calls;
            for (; seen < calls.Count; seen++)
            {
                RecordedCall call = calls[seen];
                try
                {
                    await Engine.RunOnContextAsync(() => Respond(session, call));
                }
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
                {
                    return;
                }
            }

            try
            {
                await Task.Delay(5, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Respond(FakeRcSession session, RecordedCall call)
    {
        switch (call.Method)
        {
            case nameof(IRcSession.RequestWeaponScriptAsync):
                if (Server.Weapons.TryGetValue(call.Argument<string>(0), out (string Image, string Script) weapon))
                    session.RaiseScript(ScriptKind.Weapon, call.Argument<string>(0), weapon.Script, 0, weapon.Image);
                break;
            case nameof(IRcSession.RequestClassScriptAsync):
                if (Server.Classes.TryGetValue(call.Argument<string>(0), out string? script)) session.RaiseScript(ScriptKind.Class, call.Argument<string>(0), script);
                break;
            case nameof(IRcSession.RequestNpcScriptAsync):
                if (Server.Npcs.TryGetValue(call.Argument<int>(0), out (string Name, string Type, string Script) npc)) session.RaiseScript(ScriptKind.Npc, npc.Name, npc.Script, call.Argument<int>(0));
                break;
            case nameof(IRcSession.UpdateWeaponAsync):
                Server.Weapons[call.Argument<string>(0)] = (call.Argument<string>(1), call.Argument<string>(2));
                break;
            case nameof(IRcSession.UpdateClassAsync):
                Server.Classes[call.Argument<string>(0)] = call.Argument<string>(1);
                break;
            case nameof(IRcSession.UpdateNpcScriptAsync):
                if (Server.Npcs.TryGetValue(call.Argument<int>(0), out var old)) Server.Npcs[call.Argument<int>(0)] = (old.Name, old.Type, call.Argument<string>(1));
                break;
            case nameof(IRcSession.RequestServerOptionsAsync):
                if (Server.Config.TryGetValue(SyncConfigKind.ServerOptions, out string? options)) session.RaiseServerOptions(options);
                break;
            case nameof(IRcSession.RequestServerFlagsAsync):
                if (Server.Config.TryGetValue(SyncConfigKind.ServerFlags, out string? flags)) session.RaiseServerFlags(flags);
                break;
            case nameof(IRcSession.RequestFolderConfigAsync):
                if (Server.Config.TryGetValue(SyncConfigKind.FolderConfig, out string? folders)) session.RaiseFolderConfig(folders);
                break;
            case "SyncFileBrowser.StartAsync":
                session.FakeSyncFileBrowser.RaiseFoldersReceived(Server.Roots.ToArray());
                break;
            case "SyncFileBrowser.ChangeFolderAsync":
            {
                string folder = call.Argument<string>(0);
                string key = folder.Trim('/');
                if (!Server.SilentFolders.ContainsKey(key) && Server.Listings.TryGetValue(key, out FileBrowserEntry[]? entries))
                    session.FakeSyncFileBrowser.RaiseFilesReceived(folder, entries);
                break;
            }
        }
    }

    /// <summary>Makes the fake browser answer downloads from <see cref="FakeServer.Files"/>.</summary>
    public void ServeFiles(FakeRcSession session) => session.FakeSyncFileBrowser.DownloadHandler = path =>
        Task.FromResult(Server.Files.TryGetValue(path, out byte[]? content) ? content : null);
}
