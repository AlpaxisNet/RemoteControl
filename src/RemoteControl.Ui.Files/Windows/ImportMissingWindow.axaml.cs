using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using RemoteControl.Protocol.Session;
using RemoteControl.Sync;
using RemoteControl.Sync.ImportMissing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Files;

/// <summary>
/// "Import Missing - &lt;server&gt;": asks the server for the files its scripts and NPCs name and it lacks (<see cref="MissingFilesImporter"/>),
/// finds files of those names under a folder of this computer, lists each with the server's folder, the local file and why it is or is
/// not ticked, and uploads the ticked ones after the admin confirmed how many files and how many bytes go to the server. One per session.
/// Closing the window while a search runs stops it; while an upload runs it asks, then stops the upload after the file on its way.
/// </summary>
internal sealed partial class ImportMissingWindow : RcWindow
{
    private enum Phase
    {
        Idle,
        Searching,
        Confirming,
        Uploading,
    }

    private readonly SessionContext? _context;
    private readonly ObservableCollection<ImportMissingItem> _items = [];
    private CancellationTokenSource? _stop;
    private Phase _phase;
    private bool _closeAfterStop;
    private string _root = string.Empty;
    private IReadOnlyList<FileBrowserFolder> _rights = [];
    private long _maxUpload;

    /// <summary>For the designer.</summary>
    public ImportMissingWindow() : this(null)
    {
    }

    /// <summary>Creates the window of a session.</summary>
    /// <param name="context">The session.</param>
    public ImportMissingWindow(SessionContext? context) : base(context?.App)
    {
        InitializeComponent();
        _context = context;
        Title = context is null ? "Import Missing" : "Import Missing - " + context.ServerName;
        RowsList.ItemsSource = _items;
        FindButton.Click += On.ClickAsync(FindAsync, "ImportMissing.Find");
        UploadButton.Click += On.ClickAsync(UploadAsync, "ImportMissing.Upload");
        CancelButton.Click += On.Click(Cancel, "ImportMissing.Cancel");
        CloseButton.Click += On.ClickAsync(CloseOrAskAsync, "ImportMissing.Close");
        ChooseFolderButton.Click += On.ClickAsync(ChooseFolderAsync, "ImportMissing.Choose");
        OnClosedOnce(() => _stop?.Cancel());
        if (context is not null) FolderBox.Text = context.App.Options.Current.ImportMissingFolder;
        UpdateButtons();
    }

    /// <summary>The waits of the importer this window makes (tests make them short).</summary>
    internal MissingFilesImporterOptions ImporterOptions { get; set; } = new();

    /// <summary>Replaces the folder picker (tests); returns the chosen folder or null.</summary>
    internal Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>The search or upload that runs, or the last one; completes when it has ended (tests await it).</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Whether a search or an upload runs, or the confirmation of one is asked.</summary>
    internal bool IsBusy => _phase != Phase.Idle;

    /// <summary>The rows of the list.</summary>
    internal IReadOnlyList<ImportMissingItem> Items => _items;

    /// <summary>The status line.</summary>
    internal string StatusText => PhaseLabel.Text ?? string.Empty;

    /// <summary>The folder as typed, as a full path (<c>~</c> is the home folder), or null with the reason shown.</summary>
    internal string? ChosenFolder()
    {
        string text = (FolderBox.Text ?? string.Empty).Trim();
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal))
            text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + text[1..];
        if (text.Length == 0 || !Path.IsPathFullyQualified(text))
        {
            ShowStatus("Choose the folder to search first (a full path, such as " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UGD") + ").");
            return null;
        }

        try
        {
            string full = Path.GetFullPath(text);
            if (Directory.Exists(full)) return full;
            ShowStatus("The folder " + full + " does not exist.");
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowStatus("The folder " + text + " cannot be used: " + exception.Message);
            return null;
        }
    }

    /// <summary>Asks the server for the missing files and looks for them in the chosen folder; nothing when a search or an upload runs.</summary>
    /// <returns>Completes when the search has ended.</returns>
    internal async Task FindAsync()
    {
        if (_context is null || _phase != Phase.Idle || IsClosed) return;
        if (!CanUseSession()) return;
        string? folder = ChosenFolder();
        if (folder is null) return;
        FolderBox.Text = folder;
        if (!string.Equals(_context.App.Options.Current.ImportMissingFolder, folder, StringComparison.Ordinal))
            _context.App.Options.Update(options => options with { ImportMissingFolder = folder }, this);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ClosedToken);
        _stop = stop;
        SetPhase(Phase.Searching);
        Task running = SearchAsync(folder, stop);
        Running = running;
        await running.ConfigureAwait(true);
    }

    private bool CanUseSession()
    {
        if (_context is null) return false;
        if (_context.Services.Get<ISyncStatus>()?.IsFileSyncActive == true)
        {
            ShowStatus("Files are being synchronised; stop the file sync in the Sync window first.");
            return false;
        }

        if (_context.Windows.Find(ToolIds.ServerBackup) is ServerBackupWindow { IsRunning: true })
        {
            ShowStatus("A server backup is running; wait for it to end first.");
            return false;
        }

        if (_context.IsEnded || !_context.Session.IsConnected)
        {
            ShowStatus("Not connected.");
            return false;
        }

        return true;
    }

    private async Task SearchAsync(string folder, CancellationTokenSource stop)
    {
        CancellationToken token = stop.Token;
        try
        {
            _items.Clear();
            _root = folder;
            CountsLabel.Text = string.Empty;
            ShowStatus("Asking the server for the missing files. It may take a while: the server searches its levels, NPCs, weapons and classes.");
            var importer = new MissingFilesImporter(_context!.Session, ImporterOptions);
            MissingFilesAnswer answer = await importer.RequestAsync(token).ConfigureAwait(true);
            if (IsClosed) return;
            if (answer.TimedOut)
            {
                ShowStatus("The server did not answer: this server does not know the request for missing files. It needs a newer game server.");
                return;
            }

            if (answer.Error is not null)
            {
                ShowStatus("The server did not search: " + answer.Error);
                return;
            }

            if (answer.Files.Count == 0)
            {
                ShowStatus("Nothing is missing: the server has every file its scripts and NPCs name.");
                return;
            }

            ShowStatus("The server names " + answer.Files.Count.ToString(CultureInfo.InvariantCulture) + " missing files. Reading this account's folder rights.");
            _rights = await importer.LoadRightsAsync(token).ConfigureAwait(true);
            if (IsClosed) return;
            _maxUpload = importer.MaxUploadSize;
            ShowStatus("Searching " + folder + ".");
            var wanted = new HashSet<string>(answer.Files.Select(file => file.Name), StringComparer.OrdinalIgnoreCase);
            var scan = new Progress<LocalScanProgress>(p =>
            {
                if (!IsClosed && ReferenceEquals(_stop, stop) && !token.IsCancellationRequested)
                    ShowStatus("Searching " + folder + ": " + p.Folders.ToString(CultureInfo.InvariantCulture) + " folders, " + p.Files.ToString(CultureInfo.InvariantCulture) + " files.");
            });
            LocalFileIndex index = await LocalFileIndex.BuildAsync(folder, wanted, scan, token).ConfigureAwait(true);
            if (IsClosed) return;
            ImportPlan plan = ImportPlanner.Plan(answer.Files, index, _rights, _maxUpload);
            foreach (ImportRow row in plan.Rows)
            {
                var item = new ImportMissingItem(row, folder, ChooseLocalFileAsync);
                item.TickedChanged += OnTickedChanged;
                _items.Add(item);
            }

            ShowStatus(Summary(plan, index));
        }
        catch (OperationCanceledException)
        {
            if (!IsClosed) ShowStatus("Cancelled.");
        }
        finally
        {
            _stop = null;
            stop.Dispose();
            if (!IsClosed) SetPhase(Phase.Idle);
        }
    }

    private string Summary(ImportPlan plan, LocalFileIndex index)
    {
        int found = plan.Rows.Count(row => row.LocalPath is not null);
        int ticked = plan.TickedByDefault.Count();
        string text = $"{Plural(plan.Rows.Count, "file")} missing on the server, {found} found under the folder, {ticked} ready to upload.";
        if (index.FoldersSkipped > 0) text += $" {Plural(index.FoldersSkipped, "folder")} could not be read and {(index.FoldersSkipped == 1 ? "was" : "were")} skipped.";
        if (_rights.Count == 0) text += " The server listed no folder rights for this account.";
        if (ticked > 0) text += " Tick what to send and click Upload.";
        return text;
    }

    private static string Plural(int count, string noun) => count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? string.Empty : "s");

    /// <summary>Lets the admin choose another local file of the same name for a row; nothing while a search or an upload runs.</summary>
    /// <param name="item">The row.</param>
    /// <returns>Completes when the choice was made or given up.</returns>
    internal async Task ChooseLocalFileAsync(ImportMissingItem item)
    {
        if (_phase != Phase.Idle || IsClosed || !item.HasAlternatives) return;
        IReadOnlyList<string> paths = item.LocalChoices;
        string[] shown = paths.Select(path => item.RelativePath(path) + " (" + ImportMissingItem.FormatSize(SizeOf(path)) + ")").ToArray();
        int? choice = await Dialogs.ChooseAsync("Import Missing", "Files named " + item.Name + " under the folder; the first one is chosen now.", shown, this).ConfigureAwait(true);
        if (IsClosed || _phase != Phase.Idle || choice is not { } index || index == 0 || !_items.Contains(item)) return;
        item.Replace(ImportPlanner.WithLocalFile(item.Row, paths[index], _rights, _maxUpload));
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>Asks to confirm, then uploads the ticked rows one after the other; nothing when a search or an upload runs or nothing is ticked.</summary>
    /// <returns>Completes when the upload has ended or was refused.</returns>
    internal async Task UploadAsync()
    {
        if (_context is null || _phase != Phase.Idle || IsClosed) return;
        List<ImportMissingItem> rows = _items.Where(item => item.Ticked && item.CanTick).ToList();
        if (rows.Count == 0)
        {
            ShowStatus("Nothing is ticked.");
            return;
        }

        if (!CanUseSession()) return;
        (int count, long bytes) = ImportPlan.Summarize(rows.Select(item => item.Row));
        SetPhase(Phase.Confirming);
        bool accepted = false;
        try
        {
            string question = $"Upload {Plural(count, "file")} ({ImportMissingItem.FormatSize(bytes)} in total) from {_root} to {_context.ServerName}?\n\nEach file goes into the server folder shown for it. These files are missing on the server, so nothing there is replaced.";
            accepted = await Dialogs.ConfirmAsync("Import Missing", question, this, "Upload").ConfigureAwait(true);
        }
        finally
        {
            if (!accepted && !IsClosed) SetPhase(Phase.Idle);
        }

        if (!accepted || IsClosed) return;
        if (_context.IsEnded || !_context.Session.IsConnected)
        {
            SetPhase(Phase.Idle);
            ShowStatus("Not connected.");
            return;
        }

        var stop = CancellationTokenSource.CreateLinkedTokenSource(ClosedToken);
        _stop = stop;
        SetPhase(Phase.Uploading);
        Task running = RunUploadAsync(rows, stop);
        Running = running;
        await running.ConfigureAwait(true);
    }

    private async Task RunUploadAsync(List<ImportMissingItem> rows, CancellationTokenSource stop)
    {
        try
        {
            foreach (ImportMissingItem row in rows) row.ShowProgress("waiting");
            Progress.IsIndeterminate = false;
            Progress.Maximum = rows.Count;
            Progress.Value = 0;
            ShowStatus("Uploading " + Plural(rows.Count, "file") + ".");
            var importer = new MissingFilesImporter(_context!.Session, ImporterOptions);
            var reporter = new DirectProgress<ImportProgress>(p => ShowUploadProgress(rows, p));
            ImportRun run = await importer.UploadAsync(rows.Select(row => row.Row).ToList(), _root, reporter, stop.Token).ConfigureAwait(true);
            if (IsClosed) return;
            for (int index = 0; index < run.Results.Count; index++) rows[index].SetResult(run.Results[index]);
            foreach (ImportMissingItem row in rows.Where(row => row.Result is null)) row.ShowNotSent();
            int uploaded = run.Results.Count(result => result.Outcome == ImportOutcome.Uploaded);
            int other = run.Results.Count - uploaded;
            string text = $"Uploaded {uploaded} of {Plural(rows.Count, "file")}.";
            if (other > 0) text += $" {other} could not be uploaded: the reason is in each row.";
            if (run.Cancelled) text += " Cancelled.";
            ShowStatus(text + " Click Find missing files to see what the server still lacks.");
            Progress.Value = run.Results.Count;
        }
        catch (OperationCanceledException)
        {
            if (!IsClosed) ShowStatus("Cancelled.");
        }
        finally
        {
            _stop = null;
            stop.Dispose();
            if (!IsClosed) SetPhase(Phase.Idle);
        }

        if (_closeAfterStop && !IsClosed) Close();
    }

    private void ShowUploadProgress(List<ImportMissingItem> rows, ImportProgress p)
    {
        if (IsClosed || p.Index < 0 || p.Index >= rows.Count) return;
        ImportMissingItem row = rows[p.Index];
        if (p.Result is { } result)
        {
            row.SetResult(result);
            Progress.Value = p.Index + 1;
            return;
        }

        if (row.Result is not null) return;
        row.ShowProgress(p.Total > 0 ? $"sending {ImportMissingItem.FormatSize(p.Sent)} of {ImportMissingItem.FormatSize(p.Total)}" : "sending");
        ShowStatus($"Uploading file {p.Index + 1} of {p.Count}: {row.Name}");
    }

    /// <summary>Stops the search or the upload that runs (the file on its way is given up).</summary>
    internal void Cancel()
    {
        if (_stop is not { } stop || _phase is not (Phase.Searching or Phase.Uploading)) return;
        ShowStatus("Cancelling.");
        stop.Cancel();
    }

    private void OnTickedChanged(object? sender, EventArgs e) => UpdateButtons();

    private void SetPhase(Phase phase)
    {
        _phase = phase;
        Progress.IsIndeterminate = phase == Phase.Searching;
        if (phase == Phase.Searching) Progress.Value = 0;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool idle = _phase == Phase.Idle;
        FindButton.IsEnabled = idle;
        ChooseFolderButton.IsEnabled = idle;
        FolderBox.IsReadOnly = !idle;
        CancelButton.IsEnabled = _phase is Phase.Searching or Phase.Uploading;
        List<ImportMissingItem> ticked = _items.Where(item => item.Ticked && item.CanTick).ToList();
        UploadButton.IsEnabled = idle && ticked.Count > 0;
        if (_items.Count == 0) return;
        (int count, long bytes) = ImportPlan.Summarize(ticked.Select(item => item.Row));
        CountsLabel.Text = $"{count} of {_items.Count} ticked · {ImportMissingItem.FormatSize(bytes)}";
    }

    private void ShowStatus(string text)
    {
        if (!IsClosed) PhaseLabel.Text = text;
    }

    private async Task ChooseFolderAsync()
    {
        string? start = (FolderBox.Text ?? string.Empty).Trim();
        if (start.Length == 0 || !Directory.Exists(start)) start = null;
        string? chosen = PickFolder is not null ? await PickFolder().ConfigureAwait(true) : await FilePickers.PickFolderAsync(this, "Folder to search", start).ConfigureAwait(true);
        if (IsClosed || string.IsNullOrWhiteSpace(chosen) || _phase != Phase.Idle) return;
        FolderBox.Text = chosen;
    }

    /// <summary>Closes the window; while an upload runs it asks first, then stops the upload and closes when it has ended.</summary>
    /// <returns>Completes when answered.</returns>
    internal async Task CloseOrAskAsync()
    {
        if (IsClosed) return;
        if (_phase != Phase.Uploading || _closeAfterStop)
        {
            if (_phase != Phase.Uploading) Close();
            return;
        }

        if (!await Dialogs.ConfirmAsync("Import Missing", "An upload is running. Stop it and close the window? The file on its way may still arrive.", this, "Stop and close").ConfigureAwait(true) || IsClosed) return;
        _closeAfterStop = true;
        if (_phase == Phase.Uploading) Cancel();
        else Close();
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // The user closing the window (its frame) asks while an upload runs; closing from code (the session ended) just stops it.
        if (e.Cancel || e.IsProgrammatic || e.CloseReason != WindowCloseReason.WindowClosing || _phase != Phase.Uploading || _closeAfterStop) return;
        e.Cancel = true;
        UiSafe.Fire(CloseOrAskAsync, "ImportMissing.Closing");
    }

    /// <summary>An <see cref="IProgress{T}"/> that runs its handler at once, on the caller's context (the uploader continues on the UI context).</summary>
    private sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
