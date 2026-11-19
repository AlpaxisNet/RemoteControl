using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using RemoteControl.Core;
using RemoteControl.Sync;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Files;

/// <summary>
/// The files group's part of one session: the session's <see cref="SyncEngine"/> while the option <c>syncenabled</c> is on (attached to
/// the connection now and after every reconnect, disposed when the session ends or the option goes off), the <see cref="ISyncStatus"/>
/// it offers, and the file browser and sync windows. Used on the UI thread only.
/// </summary>
internal sealed class FilesSession : ISyncStatus
{
    private static readonly ConditionalWeakTable<SessionContext, FilesSession> s_sessions = new();
    private readonly IDisposable _provided;
    private int? _percent;
    private bool _fileSyncActive;
    private bool _ended;

    private FilesSession(SessionContext context)
    {
        Context = context;
        _provided = context.Services.Provide<ISyncStatus>(this);
        context.App.Options.Changed += OnOptionsChanged;
        context.SessionReplaced += OnSessionReplaced;
        context.Ended += OnEnded;
        if (context.App.Options.Current.SyncEnabled) CreateEngine();
    }

    /// <summary>Changes the engine options (tests: no file watcher, short timeouts).</summary>
    internal static Func<SyncEngineOptions, SyncEngineOptions>? ConfigureEngine { get; set; }

    /// <summary>The session.</summary>
    public SessionContext Context { get; }

    /// <summary>The engine while sync is on, else null.</summary>
    public SyncEngine? Engine { get; private set; }

    /// <summary>The attach of the current connection (tests wait for it).</summary>
    public Task Attached { get; private set; } = Task.CompletedTask;

    /// <summary>Raised when <see cref="Engine"/> was created or disposed.</summary>
    public event EventHandler? EngineChanged;

    /// <summary>The engine reported that a server text changed while an editor of it was open.</summary>
    public event EventHandler<ServerConfigurationChangedEventArgs>? ConfigurationNotice;

    /// <inheritdoc />
    public int? Percent => _percent;

    /// <inheritdoc />
    public bool IsFileSyncActive => _fileSyncActive;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>The session's part, made on first use; null for a session that has ended.</summary>
    /// <param name="context">The session.</param>
    /// <returns>The part, or null.</returns>
    public static FilesSession? For(SessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsEnded) return null;
        return s_sessions.GetValue(context, created => new FilesSession(created));
    }

    /// <summary>Shows the file browser, unless files are being synchronised (then says why and shows nothing).</summary>
    /// <param name="folder">A folder to go to, or null.</param>
    /// <returns>The window, or null.</returns>
    public Window? ShowFileBrowser(string? folder)
    {
        if (_ended) return null;
        if ((Context.Services.Get<ISyncStatus>() ?? this).IsFileSyncActive)
        {
            Window? owner = Context.Services.Get<ISessionChat>()?.Window;
            UiSafe.Fire(() => Dialogs.ShowMessageAsync("File Browser", "The file browser is not available while files are being synchronized. Stop the file sync in the Sync window first.", owner), "Files.Refused");
            return null;
        }

        Window? window = Context.Windows.ShowSingle(ToolIds.FileBrowser, () => new FileBrowserWindow(Context));
        if (window is FileBrowserWindow browser && !string.IsNullOrWhiteSpace(folder)) browser.NavigateTo(folder);
        return window;
    }

    /// <summary>Shows the server backup window.</summary>
    /// <returns>The window, or null.</returns>
    public Window? ShowServerBackup() => _ended ? null : Context.Windows.ShowSingle(ToolIds.ServerBackup, () => new ServerBackupWindow(Context));

    /// <summary>Shows the sync window; with sync off it says how to turn it on.</summary>
    /// <returns>The window, or null.</returns>
    public Window? ShowSync()
    {
        if (_ended) return null;
        if (Engine is null)
        {
            Window? owner = Context.Services.Get<ISessionChat>()?.Window;
            UiSafe.Fire(() => Dialogs.ShowMessageAsync("Sync & Git Backups", "Sync is turned off. Turn on \"Enable sync and Git backups\" in the RC options first.", owner), "Files.SyncOff");
            return null;
        }

        return Context.Windows.ShowSingle(ToolIds.Sync, () => new SyncWindow(this));
    }

    private void CreateEngine()
    {
        if (Engine is not null || _ended) return;
        SynchronizationContext context = SynchronizationContext.Current ?? new AvaloniaSynchronizationContext();
        var options = new SyncEngineOptions { UserDirectory = Context.App.Paths.UserDirectory, Context = context };
        if (ConfigureEngine is { } configure) options = configure(options);
        var engine = new SyncEngine(options);
        engine.StateChanged += OnStateChanged;
        engine.ServerConfigurationChanged += OnServerConfigurationChanged;
        Engine = engine;
        Attach();
        UpdateStatus(engine.Snapshot);
        EngineChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeEngine()
    {
        SyncEngine? engine = Engine;
        if (engine is null) return;
        Engine = null;
        engine.StateChanged -= OnStateChanged;
        engine.ServerConfigurationChanged -= OnServerConfigurationChanged;
        UiSafe.Fire(async () => await engine.DisposeAsync().ConfigureAwait(true), "Files.DisposeEngine");
        UpdateStatus(null);
        Context.Windows.Find(ToolIds.Sync)?.Close();
        EngineChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Attach()
    {
        SyncEngine? engine = Engine;
        if (engine is null || _ended) return;
        Task attach = engine.AttachAsync(Context.Session, Context.ServerName);
        Attached = attach;
        UiSafe.Fire(() => attach, "Files.AttachSync");
    }

    private void OnStateChanged(object? sender, SyncSnapshotEventArgs e) => UiSafe.Run(() =>
    {
        if (!ReferenceEquals(sender, Engine)) return;
        UpdateStatus(e.Snapshot);
    }, "Files.SyncState");

    private void OnServerConfigurationChanged(object? sender, ServerConfigurationChangedEventArgs e) => UiSafe.Run(() =>
    {
        if (!ReferenceEquals(sender, Engine) || !IsEditorOpen(e.Kind)) return;
        ConfigurationNotice?.Invoke(this, e);
    }, "Files.ConfigurationChanged");

    /// <summary>Whether the editor of a server text is open in this session.</summary>
    /// <param name="kind">The text.</param>
    /// <returns>Whether it is open.</returns>
    internal bool IsEditorOpen(SyncConfigKind kind)
    {
        string tool = kind switch
        {
            SyncConfigKind.ServerFlags => ToolIds.ServerFlags,
            SyncConfigKind.FolderConfig => ToolIds.FolderConfig,
            _ => ToolIds.ServerOptions,
        };
        return Context.Windows.Find(tool) is { IsVisible: true };
    }

    private void UpdateStatus(SyncSnapshot? snapshot)
    {
        // As the C++ onProgress(percent, unseen): the main window shows the percentage while a pass runs that the sync window has not shown.
        int? percent = snapshot is { ProgressUnseen: true } ? snapshot.Percent : null;
        bool active = snapshot?.FileSyncActive ?? false;
        if (percent == _percent && active == _fileSyncActive) return;
        _percent = percent;
        _fileSyncActive = active;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnOptionsChanged(object? sender, OptionsChangedEventArgs e) => UiSafe.Run(() =>
    {
        if (_ended || !e.Contains(OptionKeys.SyncEnabled)) return;
        if (e.Current.SyncEnabled) CreateEngine();
        else DisposeEngine();
    }, "Files.Options");

    private void OnSessionReplaced(object? sender, EventArgs e) => UiSafe.Run(Attach, "Files.Reconnect");

    private void OnEnded(object? sender, EventArgs e) => UiSafe.Run(() =>
    {
        if (_ended) return;
        _ended = true;
        Context.App.Options.Changed -= OnOptionsChanged;
        Context.SessionReplaced -= OnSessionReplaced;
        Context.Ended -= OnEnded;
        DisposeEngine();
        _provided.Dispose();
        s_sessions.Remove(Context);
    }, "Files.Ended");
}
