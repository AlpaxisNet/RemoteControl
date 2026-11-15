using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using RemoteControl.Sync;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Files;

/// <summary>
/// "Sync &amp; Git Backups (Alpha) - &lt;server&gt; (&lt;n&gt;%)" (<c>TSyncManager::createWindow</c>, 560 by 500): the Setup page (status,
/// workspace, what is synchronised and how, the remote folders, exclusions, Start / Stop / Sync now), the Changes page (conflicts with
/// "Keep local" and "Take server", local deletions waiting for confirmation, failures), Git, the activity log and the credentials.
/// Every control works on the session's <see cref="SyncEngine"/>; nothing blocks. Closing only hides it.
/// </summary>
/// <remarks>
/// New against the C++: the Changes page (the C++ reported no conflicts, D-10), deletions are confirmed by the user, a change of a
/// server text while its editor is open is told in a bar at the top instead of replacing the editor's text (D-12).
/// </remarks>
internal sealed partial class SyncWindow : RcWindow
{
    private static readonly IBrush TimeBrush = new SolidColorBrush(Color.Parse("#7f849c"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#f85149"));
    private static readonly IBrush DownloadBrush = new SolidColorBrush(Color.Parse("#4da3ff"));
    private static readonly IBrush UploadBrush = new SolidColorBrush(Color.Parse("#3fb950"));
    private static readonly IBrush QueueBrush = new SolidColorBrush(Color.Parse("#d29922"));
    private static readonly IBrush LifecycleBrush = new SolidColorBrush(Color.Parse("#a371f7"));
    private readonly FilesSession? _owner;
    private readonly Dictionary<string, SyncItemRow> _rows = new(StringComparer.Ordinal);
    private readonly Debouncer? _excludeEdit;
    private readonly Debouncer? _urlEdit;
    private bool _rendering;
    private SyncSnapshot _snapshot = new();

    /// <summary>For the designer.</summary>
    public SyncWindow() : this(null)
    {
    }

    /// <summary>Creates the sync window of a session.</summary>
    /// <param name="owner">The session's files part (its engine).</param>
    public SyncWindow(FilesSession? owner) : base(owner?.Context.App)
    {
        InitializeComponent();
        _owner = owner;
        HideOnClose = true;
        ItemsList.ItemsSource = Items;
        ActivityLog.MaximumLines = 300;
        ActivityLog.LineBrush = ActivityBrush;
        CloseButton.Click += On.Click(() => Close(), "Sync.Close");
        DismissNoticeButton.Click += On.Click(() => NoticeBar.IsVisible = false, "Sync.Notice");
        if (owner is null) return;

        _excludeEdit = Track(UiDispatch.Debounce(TimeSpan.FromMilliseconds(600), CommitExclusions, "Sync.Exclude", this));
        _urlEdit = Track(UiDispatch.Debounce(TimeSpan.FromMilliseconds(600), CommitRemoteUrl, "Sync.RemoteUrl", this));
        ChooseWorkspaceButton.Click += On.ClickAsync(() => ChooseWorkspaceAsync(), "Sync.ChooseWorkspace");
        OpenWorkspaceButton.Click += On.ClickAsync(OpenWorkspaceAsync, "Sync.OpenWorkspace");
        ScriptSyncBox.IsCheckedChanged += On.Click(() => Change(s => s with { Scripts = ScriptSyncBox.IsChecked == true }), "Sync.Scripts");
        FileSyncBox.IsCheckedChanged += On.Click(() => Change(s => s with { Files = FileSyncBox.IsChecked == true }), "Sync.Files");
        ConfigSyncBox.IsCheckedChanged += On.Click(() => Change(s => s with { ServerConfig = ConfigSyncBox.IsChecked == true }), "Sync.Config");
        AllowDeletesBox.IsCheckedChanged += On.Click(() => Change(s => s with { AllowRemoteDeletes = AllowDeletesBox.IsChecked == true }), "Sync.Deletes");
        AutoStartBox.IsCheckedChanged += On.Click(() => Change(s => s with { AutoStart = AutoStartBox.IsChecked == true }), "Sync.AutoStart");
        AutoCommitBox.IsCheckedChanged += On.Click(() => Change(s => s with { GitAutoCommit = AutoCommitBox.IsChecked == true }), "Sync.AutoCommit");
        AutoPushBox.IsCheckedChanged += On.Click(() => Change(s => s with { GitAutoPush = AutoPushBox.IsChecked == true }), "Sync.AutoPush");
        AutoPullBox.IsCheckedChanged += On.Click(() => Change(s => s with { GitAutoPull = AutoPullBox.IsChecked == true }), "Sync.AutoPull");
        DirectionCombo.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => Change(s => s with { Direction = DirectionCombo.SelectedIndex switch { 1 => SyncDirection.UploadOnly, 2 => SyncDirection.TwoWay, _ => SyncDirection.DownloadOnly } }), "Sync.Direction");
        ExcludeBox.TextChanged += UiSafe.Handler<TextChangedEventArgs>((_, _) => { if (!_rendering) _excludeEdit?.Trigger(); }, "Sync.Exclude");
        ExcludeBox.LostFocus += UiSafe.Handler<RoutedEventArgs>((_, _) => _excludeEdit?.Flush(), "Sync.Exclude");
        RemoteUrlBox.TextChanged += UiSafe.Handler<TextChangedEventArgs>((_, _) => { if (!_rendering) _urlEdit?.Trigger(); }, "Sync.RemoteUrl");
        RemoteUrlBox.LostFocus += UiSafe.Handler<RoutedEventArgs>((_, _) => _urlEdit?.Flush(), "Sync.RemoteUrl");
        ChooseFoldersButton.Click += On.Click(() => ChooseFolders(), "Sync.ChooseFolders");
        StartButton.Click += On.ClickAsync(StartAsync, "Sync.Start");
        StopButton.Click += On.Click(() => Engine?.Stop(), "Sync.Stop");
        SyncNowButton.Click += On.ClickAsync(SyncNowAsync, "Sync.SyncNow");
        ConfirmDeletionsButton.Click += On.ClickAsync(ConfirmDeletionsAsync, "Sync.ConfirmDeletions");
        DiscardDeletionsButton.Click += On.Click(() => Engine?.DiscardPendingDeletions(), "Sync.DiscardDeletions");
        GitInitButton.Click += On.ClickAsync(() => RunGitAsync(engine => engine.GitInitializeAsync()), "Sync.GitInit");
        GitCommitButton.Click += On.ClickAsync(() => RunGitAsync(engine => engine.GitCommitAsync()), "Sync.GitCommit");
        GitPullButton.Click += On.ClickAsync(() => RunGitAsync(engine => engine.GitPullAsync()), "Sync.GitPull");
        GitPushButton.Click += On.ClickAsync(() => RunGitAsync(engine => engine.GitPushAsync()), "Sync.GitPush");
        ImportKeyButton.Click += On.ClickAsync(ImportKeyAsync, "Sync.ImportKey");
        RemoveKeyButton.Click += On.Click(() => Engine?.ClearGitKey(), "Sync.RemoveKey");
        SaveTokenButton.Click += On.ClickAsync(SaveTokenAsync, "Sync.SaveToken");
        RemoveTokenButton.Click += On.Click(() => Engine?.ClearGitToken(), "Sync.RemoveToken");

        Listen(h => owner.EngineChanged += h, h => owner.EngineChanged -= h, (_, _) => Bind(), "Sync.EngineChanged");
        Listen<ServerConfigurationChangedEventArgs>(h => owner.ConfigurationNotice += h, h => owner.ConfigurationNotice -= h, (_, e) => ShowConfigurationNotice(e), "Sync.ConfigurationNotice");
        Opened += (_, _) => MarkSeen();
        Bind();
    }

    /// <summary>The rows of the Changes page.</summary>
    internal ObservableCollection<SyncItemRow> Items { get; } = new();

    /// <summary>The snapshot shown.</summary>
    internal SyncSnapshot Shown => _snapshot;

    /// <summary>Replaces the workspace folder picker (tests).</summary>
    internal Func<Task<string?>>? PickWorkspace { get; set; }

    /// <summary>Replaces the key file picker (tests).</summary>
    internal Func<Task<string?>>? PickKeyFile { get; set; }

    /// <summary>The last git result shown.</summary>
    internal string GitResult => GitResultText.Text ?? string.Empty;

    private SyncEngine? Engine => _owner?.Engine;

    private IDisposable? _engineEvents;

    private void Bind()
    {
        _engineEvents?.Dispose();
        _engineEvents = null;
        SyncEngine? engine = Engine;
        if (engine is null) return;
        EventHandler<SyncSnapshotEventArgs> state = (_, e) => Deliver(() => Render(e.Snapshot));
        EventHandler<SyncLogEventArgs> log = (_, e) => Deliver(() => ActivityLog.Append(e.Line));
        EventHandler<SyncItemEventArgs> item = (_, e) => Deliver(() => UpdateItem(e.Status));
        EventHandler<SyncGitEventArgs> git = (_, e) => Deliver(() => GitResultText.Text = e.Operation + ": " + (e.Result.Success ? "done" : FirstLine(e.Result.Output)));
        engine.StateChanged += state;
        engine.LogAdded += log;
        engine.ItemStatusChanged += item;
        engine.GitCompleted += git;
        _engineEvents = Track(new RemoteControl.Ui.Shell.Unsubscriber(() =>
        {
            engine.StateChanged -= state;
            engine.LogAdded -= log;
            engine.ItemStatusChanged -= item;
            engine.GitCompleted -= git;
        }));
        SyncSnapshot snapshot = engine.Snapshot;
        ActivityLog.Clear();
        foreach (string line in snapshot.Log) ActivityLog.Append(line);
        Render(snapshot);
    }

    private void Deliver(Action action)
    {
        if (IsClosed) return;
        UiSafe.Run(action, "Sync.Event");
    }

    private static string FirstLine(string text)
    {
        string value = (text ?? string.Empty).Trim();
        int newline = value.IndexOf('\n');
        return newline < 0 ? value : value[..newline].TrimEnd('\r');
    }

    // ---- Rendering --------------------------------------------------------------------------------------------------------------

    /// <summary>Shows a snapshot (settings into the controls, unless the user is typing in that box).</summary>
    /// <param name="snapshot">The snapshot.</param>
    internal void Render(SyncSnapshot snapshot)
    {
        _snapshot = snapshot;
        _rendering = true;
        try
        {
            SyncSettings settings = snapshot.Settings;
            bool running = snapshot.State is SyncRunState.Running or SyncRunState.OneShot or SyncRunState.WaitingForNcServer;
            string server = string.IsNullOrEmpty(snapshot.ServerName) ? _owner?.Context.ServerName ?? string.Empty : snapshot.ServerName;
            Title = "Sync & Git Backups (Alpha) - " + server + (running ? " (" + snapshot.Percent + "%)" : string.Empty);
            StatusText.Text = snapshot.StatusText;
            WorkspaceBox.Text = settings.Workspace;
            ScriptSyncBox.IsChecked = settings.Scripts;
            FileSyncBox.IsChecked = settings.Files;
            ConfigSyncBox.IsChecked = settings.ServerConfig;
            DirectionCombo.SelectedIndex = settings.Direction switch { SyncDirection.UploadOnly => 1, SyncDirection.TwoWay => 2, _ => 0 };
            AllowDeletesBox.IsChecked = settings.AllowRemoteDeletes;
            AllowDeletesBox.IsEnabled = settings.UploadsAllowed;
            AutoStartBox.IsChecked = settings.AutoStart;
            FoldersBox.Text = settings.FileFolders;
            if (!ExcludeBox.IsFocused && _excludeEdit?.IsPending != true) ExcludeBox.Text = settings.ExcludeFiles;
            if (!RemoteUrlBox.IsFocused && _urlEdit?.IsPending != true) RemoteUrlBox.Text = settings.GitRemoteUrl;
            AutoCommitBox.IsChecked = settings.GitAutoCommit;
            AutoPushBox.IsChecked = settings.GitAutoPush;
            AutoPullBox.IsChecked = settings.GitAutoPull;
            StartButton.IsEnabled = snapshot.State != SyncRunState.Running && snapshot.State != SyncRunState.WaitingForNcServer;
            StopButton.IsEnabled = running;
            ChooseFoldersButton.IsEnabled = snapshot.State != SyncRunState.Detached;
            OpenWorkspaceButton.IsEnabled = settings.Workspace.Length != 0;
            PhaseLabel.Text = "Current: " + snapshot.Phase + (snapshot.CurrentItem is { } current ? " - " + current : string.Empty);
            QueueLabel.Text = "Queued: " + snapshot.QueuedFolders + " folders, " + snapshot.QueuedDownloads + " downloads";
            TotalsLabel.Text = "Tracked: " + snapshot.TrackedScripts + " scripts, " + snapshot.TrackedFiles + " files, " + snapshot.TrackedConfiguration + " configuration files";
            KeyStatus.Text = snapshot.HasGitKey ? "An encrypted SSH key is stored." : "No SSH key is stored.";
            RemoveKeyButton.IsEnabled = snapshot.HasGitKey;
            TokenStatus.Text = snapshot.HasGitToken ? "An encrypted token is stored." : "No token is stored.";
            RemoveTokenButton.IsEnabled = snapshot.HasGitToken;
            RenderItems(snapshot.Items);
        }
        finally
        {
            _rendering = false;
        }

        MarkSeen();
    }

    private void RenderItems(IReadOnlyList<SyncItemStatus> statuses)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (SyncItemStatus status in statuses)
        {
            if (status.State == SyncItemState.InSync) continue;
            wanted.Add(status.Key);
            UpdateItem(status);
        }

        for (int index = Items.Count - 1; index >= 0; index--)
        {
            if (wanted.Contains(Items[index].Key)) continue;
            _rows.Remove(Items[index].Key);
            Items.RemoveAt(index);
        }

        RenderSummary();
    }

    private void UpdateItem(SyncItemStatus status)
    {
        if (status.State == SyncItemState.InSync)
        {
            if (_rows.Remove(status.Key, out SyncItemRow? gone)) Items.Remove(gone);
        }
        else if (_rows.TryGetValue(status.Key, out SyncItemRow? row))
        {
            row.Update(status);
        }
        else
        {
            row = new SyncItemRow(status, ResolveAsync);
            _rows[status.Key] = row;
            Items.Add(row);
        }

        RenderSummary();
    }

    private void RenderSummary()
    {
        int conflicts = Items.Count(row => row.Status.State == SyncItemState.Conflict);
        int pending = Items.Count(row => row.Status.State == SyncItemState.PendingDeletion);
        ItemsSummary.Text = Items.Count == 0
            ? "Everything is in sync."
            : Items.Count + (Items.Count == 1 ? " item needs" : " items need") + " attention" + (conflicts > 0 ? ", " + conflicts + (conflicts == 1 ? " conflict" : " conflicts") : string.Empty) + ".";
        PendingBar.IsVisible = pending > 0;
        PendingText.Text = pending + (pending == 1 ? " local deletion waits" : " local deletions wait") + " for your confirmation before the server copy is deleted.";
        ItemsTab.Header = conflicts + pending > 0 ? "Changes (" + (conflicts + pending) + ")" : "Changes";
    }

    private void MarkSeen()
    {
        if (IsVisible && _snapshot.ProgressUnseen && _snapshot.Percent >= 100) Engine?.MarkProgressSeen();
    }

    private static IBrush? ActivityBrush(string line)
    {
        string text = line.ToLowerInvariant();
        if (text.Contains("failed", StringComparison.Ordinal) || text.Contains("unable", StringComparison.Ordinal) || text.Contains("error", StringComparison.Ordinal)) return ErrorBrush;
        if (text.Contains("downloaded", StringComparison.Ordinal)) return DownloadBrush;
        if (text.Contains("uploaded", StringComparison.Ordinal)) return UploadBrush;
        if (text.Contains("queued", StringComparison.Ordinal) || text.Contains("listing", StringComparison.Ordinal) || text.Contains("request", StringComparison.Ordinal) || text.Contains("selected", StringComparison.Ordinal) || text.Contains("discovered", StringComparison.Ordinal)) return QueueBrush;
        if (text.Contains("started", StringComparison.Ordinal) || text.Contains("stopped", StringComparison.Ordinal) || text.Contains("watching", StringComparison.Ordinal)) return LifecycleBrush;
        return line.Length > 0 && char.IsDigit(line[0]) ? TimeBrush : null;
    }

    private void ShowConfigurationNotice(ServerConfigurationChangedEventArgs e)
    {
        string what = e.Kind switch
        {
            SyncConfigKind.ServerFlags => "server flags",
            SyncConfigKind.FolderConfig => "folder configuration",
            _ => "server options",
        };
        NoticeText.Text = "The " + what + " changed on the server while its editor is open. The editor was not changed; reopen it to load the server's version (the synchronised copy is in the workspace).";
        NoticeBar.IsVisible = true;
    }

    // ---- Settings -----------------------------------------------------------------------------------------------------------------

    private void Change(Func<SyncSettings, SyncSettings> change)
    {
        if (_rendering) return;
        Engine?.UpdateSettings(change);
    }

    private void CommitExclusions()
    {
        string text = ExcludeBox.Text ?? string.Empty;
        if (text != _snapshot.Settings.ExcludeFiles) Engine?.UpdateSettings(s => s with { ExcludeFiles = text });
    }

    private void CommitRemoteUrl()
    {
        string text = (RemoteUrlBox.Text ?? string.Empty).Trim();
        if (text != _snapshot.Settings.GitRemoteUrl) Engine?.UpdateSettings(s => s with { GitRemoteUrl = text });
    }

    /// <summary>Asks for the workspace folder (<c>onChooseWorkspace</c>).</summary>
    /// <returns>Whether one was chosen.</returns>
    internal async Task<bool> ChooseWorkspaceAsync()
    {
        string start = _snapshot.Settings.Workspace;
        string? folder = PickWorkspace is not null
            ? await PickWorkspace().ConfigureAwait(true)
            : await FilePickers.PickFolderAsync(this, "Choose script sync workspace", start.Length == 0 ? null : start).ConfigureAwait(true);
        if (IsClosed || string.IsNullOrWhiteSpace(folder) || Engine is not { } engine) return false;
        engine.UpdateSettings(s => s with { Workspace = folder });
        return true;
    }

    private async Task OpenWorkspaceAsync()
    {
        string workspace = _snapshot.Settings.Workspace;
        if (workspace.Length == 0)
        {
            await ChooseWorkspaceAsync().ConfigureAwait(true);
            return;
        }

        if (!ExternalPrograms.OpenFile(workspace, Services?.Log))
            await Dialogs.ShowMessageAsync("Unable to open workspace", "The folder " + workspace + " could not be opened.", this).ConfigureAwait(true);
    }

    private async Task StartAsync()
    {
        if (Engine is not { } engine) return;
        if (_snapshot.Settings.Workspace.Length == 0 && !await ChooseWorkspaceAsync().ConfigureAwait(true)) return;
        engine.Start();
    }

    private async Task SyncNowAsync()
    {
        if (Engine is not { } engine) return;
        if (_snapshot.Settings.Workspace.Length == 0 && !await ChooseWorkspaceAsync().ConfigureAwait(true)) return;
        engine.SyncNow();
    }

    private async Task ConfirmDeletionsAsync()
    {
        if (Engine is not { } engine) return;
        string[] names = Items.Where(row => row.Status.State == SyncItemState.PendingDeletion).Select(row => row.Title).ToArray();
        if (names.Length == 0) return;
        string list = string.Join("\n", names.Take(15)) + (names.Length > 15 ? "\n… and " + (names.Length - 15) + " more" : string.Empty);
        if (!await Dialogs.ConfirmAsync("Delete on server", "Delete these items on the server? Their local files were deleted.\n\n" + list + "\n\nThis cannot be undone.", this, "Delete").ConfigureAwait(true)) return;
        if (!IsClosed) engine.ConfirmPendingDeletions();
    }

    private Task ResolveAsync(string key, ConflictResolution resolution) => Engine?.ResolveConflictAsync(key, resolution) ?? Task.CompletedTask;

    private void ChooseFolders()
    {
        if (_owner is null || Engine is null) return;
        var picker = new FolderPickerWindow(_owner);
        _owner.Context.Windows.Track(picker);
        Track(new RemoteControl.Ui.Shell.Unsubscriber(() => picker.Close()));
        picker.Show(this);
    }

    private async Task RunGitAsync(Func<SyncEngine, Task<RemoteControl.Core.GitResult>> command)
    {
        if (Engine is not { } engine) return;
        CommitRemoteUrl();
        GitResultText.Text = "Running…";
        RemoteControl.Core.GitResult result = await command(engine).ConfigureAwait(true);
        // The engine tells the result of every git step, a button's and its own, through GitCompleted, and that is what the line
        // shows ("Commit: done"). Writing it here as well gave two texts whose order depended on the machine. Only a step that
        // never reached git (no engine event) is told here.
        if (!IsClosed && GitResultText.Text == "Running…") GitResultText.Text = result.Success ? "Done." : FirstLine(result.Output);
    }

    private async Task ImportKeyAsync()
    {
        if (Engine is not { } engine) return;
        string? file = PickKeyFile is not null ? await PickKeyFile().ConfigureAwait(true) : await FilePickers.OpenFileAsync(this, "Import SSH private key").ConfigureAwait(true);
        if (IsClosed || string.IsNullOrEmpty(file)) return;
        string text;
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 1024 * 1024)
            {
                await Dialogs.ShowErrorAsync("SSH key import failed", "The file is too large to be a private key.", this).ConfigureAwait(true);
                return;
            }

            text = await File.ReadAllTextAsync(file, ClosedToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (!IsClosed) await Dialogs.ShowErrorAsync("SSH key import failed", "The file could not be read: " + exception.Message, this).ConfigureAwait(true);
            return;
        }

        if (!text.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            await Dialogs.ShowErrorAsync("SSH key import failed", "The selected file is not a private key (it has no \"PRIVATE KEY\" block).", this).ConfigureAwait(true);
            return;
        }

        string? error = await engine.ImportGitKeyAsync(text).ConfigureAwait(true);
        if (error is not null && !IsClosed) await Dialogs.ShowErrorAsync("SSH key import failed", error, this).ConfigureAwait(true);
    }

    private async Task SaveTokenAsync()
    {
        if (Engine is not { } engine) return;
        string token = (TokenBox.Text ?? string.Empty).Trim();
        if (token.Length == 0) return;
        string? error = await engine.SaveGitTokenAsync(token).ConfigureAwait(true);
        if (IsClosed) return;
        if (error is null) TokenBox.Text = string.Empty;
        else await Dialogs.ShowErrorAsync("Git token save failed", error, this).ConfigureAwait(true);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Close();
        }
    }
}
