using Avalonia.Controls;
using RemoteControl.Protocol.Session;
using RemoteControl.Sync;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Files;

/// <summary>
/// "Server Backup - &lt;server&gt;": downloads every file the account may read into a folder of this computer, in the server's folder
/// structure (<see cref="ServerBackup"/>), with a status line, a progress bar, the counts and a log. One per session. The folder starts
/// as <c>&lt;download folder&gt;/&lt;server&gt; backup</c>. Closing the window while a backup runs asks, then stops it.
/// </summary>
internal sealed partial class ServerBackupWindow : RcWindow
{
    private readonly SessionContext? _context;
    private CancellationTokenSource? _stop;
    private bool _closeAfterStop;

    /// <summary>For the designer.</summary>
    public ServerBackupWindow() : this(null)
    {
    }

    /// <summary>Creates the window of a session.</summary>
    /// <param name="context">The session.</param>
    public ServerBackupWindow(SessionContext? context) : base(context?.App)
    {
        InitializeComponent();
        _context = context;
        Title = context is null ? "Server Backup" : "Server Backup - " + context.ServerName;
        StartButton.Click += On.ClickAsync(StartAsync, "ServerBackup.Start");
        StopButton.Click += On.Click(Stop, "ServerBackup.Stop");
        CloseButton.Click += On.ClickAsync(CloseOrAskAsync, "ServerBackup.Close");
        ChooseFolderButton.Click += On.ClickAsync(ChooseFolderAsync, "ServerBackup.Choose");
        OpenFolderButton.Click += On.Click(OpenFolder, "ServerBackup.Open");
        OnClosedOnce(() => _stop?.Cancel());
        UpdateCounts(new ServerBackupProgress());
        if (context is null) return;
        string downloads = context.App.Paths.DownloadDirectory(context.App.Options.Current);
        FolderBox.Text = Path.Combine(downloads, LocalCopies.SafeComponent(context.ServerName, "Server") + " backup");
    }

    /// <summary>The time limits of the backups this window starts (tests make them short).</summary>
    internal ServerBackupOptions BackupOptions { get; set; } = new();

    /// <summary>Replaces the folder picker (tests); returns the chosen folder or null.</summary>
    internal Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>The backup that runs, or the last one; completes with its result (tests await it).</summary>
    internal Task<ServerBackupResult?> Running { get; private set; } = Task.FromResult<ServerBackupResult?>(null);

    /// <summary>Whether a backup runs.</summary>
    internal bool IsRunning => _stop is not null;

    /// <summary>The log's lines.</summary>
    internal IReadOnlyList<string> LogLines => Log.Lines;

    /// <summary>The folder as typed, as a full path (<c>~</c> is the home folder), or null with the reason logged.</summary>
    internal string? ChosenFolder()
    {
        string text = (FolderBox.Text ?? string.Empty).Trim();
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal))
            text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + text[1..];
        if (text.Length == 0 || !Path.IsPathFullyQualified(text))
        {
            AppendLog("Choose the folder for the backup first (a full path, such as " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Backups") + ").");
            return null;
        }

        try
        {
            return Path.GetFullPath(text);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AppendLog("The folder " + text + " cannot be used: " + exception.Message);
            return null;
        }
    }

    /// <summary>Starts a backup into the chosen folder; nothing when one runs, the folder is missing or the session cannot.</summary>
    /// <returns>Completes when the backup has ended.</returns>
    internal async Task StartAsync()
    {
        if (_context is null || IsRunning || IsClosed) return;
        if (_context.Services.Get<ISyncStatus>()?.IsFileSyncActive == true)
        {
            AppendLog("Files are being synchronised; stop the file sync in the Sync window first.");
            return;
        }

        if (_context.IsEnded || !_context.Session.IsConnected)
        {
            AppendLog("Not connected.");
            return;
        }

        string? folder = ChosenFolder();
        if (folder is null) return;
        FolderBox.Text = folder;
        var backup = new ServerBackup(_context.Session, folder, BackupOptions);
        var stop = new CancellationTokenSource();
        _stop = stop;
        backup.LogLine += (_, e) => AppendLog(e.Text);
        backup.ProgressChanged += (_, e) => ShowProgress(e.Value);
        SetRunning(true);
        Task<ServerBackupResult?> running = RunAsync(backup, stop);
        Running = running;
        await running.ConfigureAwait(true);
    }

    private async Task<ServerBackupResult?> RunAsync(ServerBackup backup, CancellationTokenSource stop)
    {
        ServerBackupResult result;
        try
        {
            result = await backup.RunAsync(stop.Token).ConfigureAwait(true);
        }
        finally
        {
            _stop = null;
            stop.Dispose();
            if (!IsClosed) SetRunning(false);
        }

        if (!IsClosed)
        {
            PhaseLabel.Text = result.Message;
            Progress.IsIndeterminate = false;
            if (result.Outcome == ServerBackupOutcome.Completed) Progress.Value = Progress.Maximum;
            UpdateCounts(result.Totals);
        }

        if (_closeAfterStop && !IsClosed) Close();
        return result;
    }

    /// <summary>Stops the backup that runs (the file on its way is given up).</summary>
    internal void Stop()
    {
        if (_stop is not { } stop) return;
        AppendLog("Stopping the backup.");
        stop.Cancel();
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        ChooseFolderButton.IsEnabled = !running;
        FolderBox.IsReadOnly = running;
        Progress.IsIndeterminate = running;
        if (running)
        {
            Progress.Value = 0;
            PhaseLabel.Text = "Starting the backup.";
            UpdateCounts(new ServerBackupProgress());
        }
    }

    private void ShowProgress(ServerBackupProgress progress)
    {
        if (IsClosed) return;
        PhaseLabel.Text = progress.Phase;
        if (progress.FoldersTotal > 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Maximum = progress.FoldersTotal;
            Progress.Value = Math.Min(progress.FoldersDone, progress.FoldersTotal);
        }

        UpdateCounts(progress);
    }

    private void UpdateCounts(ServerBackupProgress progress)
    {
        string text = $"{progress.Saved} saved · {progress.UpToDate} up to date";
        if (progress.Skipped > 0) text += $" · {progress.Skipped} left out";
        if (progress.Failed > 0) text += $" · {progress.Failed} failed";
        if (progress.BytesSaved > 0) text += " · " + Size(progress.BytesSaved);
        CountsLabel.Text = text;
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " GB",
    };

    private async Task ChooseFolderAsync()
    {
        string? start = (FolderBox.Text ?? string.Empty).Trim();
        if (start.Length == 0 || !Directory.Exists(start)) start = null;
        string? chosen = PickFolder is not null ? await PickFolder().ConfigureAwait(true) : await FilePickers.PickFolderAsync(this, "Backup folder", start).ConfigureAwait(true);
        if (IsClosed || string.IsNullOrWhiteSpace(chosen) || IsRunning) return;
        FolderBox.Text = chosen;
    }

    private void OpenFolder()
    {
        string? folder = ChosenFolder();
        if (folder is null) return;
        if (!Directory.Exists(folder))
        {
            AppendLog("The folder " + folder + " does not exist yet; it is made when the backup starts.");
            return;
        }

        if (!ExternalPrograms.OpenFile(folder, _context?.App.Log)) AppendLog("Could not open " + folder + ".");
    }

    /// <summary>Adds a line to the log.</summary>
    /// <param name="text">The line.</param>
    internal void AppendLog(string text)
    {
        if (!IsClosed) Log.Append(text);
    }

    /// <summary>Closes the window; while a backup runs it asks first, then stops the backup and closes when it has ended.</summary>
    /// <returns>Completes when answered.</returns>
    internal async Task CloseOrAskAsync()
    {
        if (IsClosed) return;
        if (!IsRunning || _closeAfterStop)
        {
            if (!IsRunning) Close();
            return;
        }

        if (!await Dialogs.ConfirmAsync("Server Backup", "A backup is running. Stop it and close the window?", this, "Stop and close").ConfigureAwait(true) || IsClosed) return;
        _closeAfterStop = true;
        if (IsRunning) Stop();
        else Close();
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // The user closing the window (its frame) asks while a backup runs; closing from code (the session ended) just stops it.
        if (e.Cancel || e.IsProgrammatic || e.CloseReason != WindowCloseReason.WindowClosing || !IsRunning || _closeAfterStop) return;
        e.Cancel = true;
        UiSafe.Fire(CloseOrAskAsync, "ServerBackup.Closing");
    }
}
