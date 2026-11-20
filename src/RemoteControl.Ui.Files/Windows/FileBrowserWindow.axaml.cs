using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Files;

/// <summary>
/// The file browser of a session (<c>TFileBrowserTree</c>), "File Browser - &lt;server&gt;", 800 by 600: the header (classic
/// "Current Folder:" label, or the modern address bar with parent and refresh), the folder tree built from the folder rules, the
/// classic file list (Name, Rights, Size, Modified; sortable, multi-select) or the modern icon grid (folders first, type icons or
/// thumbnails, type to search), the log and the footer counts. Downloads, edits, uploads, renames, moves and deletes go through one
/// ordered <see cref="TransferQueue"/>, previews behind what the user asked for.
/// </summary>
/// <remarks>
/// Differences from the C++: Delete asks first, naming the files; a download never overwrites a different local copy (it is moved
/// aside, D-28); a double click hands only harmless files to the desktop (D-29) and watches every file opened that way, not only the
/// last one; Edit refuses binary files and saves in the encoding it read (D-27); a preview that fails is not retried and never stalls
/// the others (D-30); the dialogs are asynchronous (D-19) and context menus are made per use (D-33).
/// </remarks>
internal sealed partial class FileBrowserWindow : RcWindow, IFileDropTarget
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(60);
    private static readonly Cursor DragCursor = new(StandardCursorType.DragMove);
    private readonly SessionContext? _context;
    private readonly TransferQueue _queue = new();
    private readonly PreviewCache _cache;
    private readonly Dictionary<string, WatchedFile> _watches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _chunkSegments = new(StringComparer.Ordinal);
    private readonly HashSet<string> _previewTransfers = new(StringComparer.Ordinal);
    private readonly Debouncer? _mutationRefresh;
    private readonly Debouncer? _thumbnailRefresh;
    private IReadOnlyList<string> _ruleFolders = Array.Empty<string>();
    private string _currentFolder = string.Empty;
    private int _generation;
    private int _sortColumn;
    private bool _descending;
    private bool _modern;
    private bool _thumbnails;
    private bool _hover = true;
    private bool _selectingFolder;
    private string? _hoveredPath;
    private Point? _dragStart;
    private IReadOnlyList<string> _dragPaths = Array.Empty<string>();
    private bool _dragging;

    /// <summary>For the designer.</summary>
    public FileBrowserWindow() : this(null)
    {
    }

    /// <summary>Creates the file browser of a session.</summary>
    /// <param name="context">The session.</param>
    public FileBrowserWindow(SessionContext? context) : base(context?.App)
    {
        InitializeComponent();
        _context = context;
        _cache = new PreviewCache(context?.App.Paths.PreviewCacheDirectory);
        Title = context is null ? "File Browser" : "File Browser - " + context.ServerName;
        FolderTree.ItemsSource = Folders;
        FileList.ItemsSource = Rows;
        ModernView.ItemsSource = ModernItems;

        CloseButton.Click += On.Click(() => Close(), "FileBrowser.Close");
        ImportMissingButton.Click += On.Click(() => _context?.OpenTool(ToolIds.ImportMissing), "FileBrowser.ImportMissing");
        UpButton.Click += On.Click(() => NavigateTo(RemotePaths.Parent(_currentFolder)), "FileBrowser.Up");
        RefreshButton.Click += On.Click(() => RefreshCurrent(), "FileBrowser.Refresh");
        AddressBox.KeyDown += UiSafe.Handler<KeyEventArgs>((_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            NavigateTo(AddressBox.Text);
        }, "FileBrowser.Address");
        SortName.Click += On.Click(() => SortBy(0), "FileBrowser.Sort");
        SortRights.Click += On.Click(() => SortBy(1), "FileBrowser.Sort");
        SortSize.Click += On.Click(() => SortBy(2), "FileBrowser.Sort");
        SortModified.Click += On.Click(() => SortBy(3), "FileBrowser.Sort");
        FolderTree.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => OnFolderSelected(), "FileBrowser.FolderSelected");
        FileList.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => UpdateStatus(), "FileBrowser.Selection");
        ModernView.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => UpdateStatus(), "FileBrowser.Selection");
        FileList.DoubleTapped += UiSafe.AsyncHandler<TappedEventArgs>((_, e) => OnDoubleTappedAsync(e.Source), "FileBrowser.DoubleClick");
        ModernView.DoubleTapped += UiSafe.AsyncHandler<TappedEventArgs>((_, e) => OnDoubleTappedAsync(e.Source), "FileBrowser.DoubleClick");
        FileList.ContextRequested += UiSafe.Handler<ContextRequestedEventArgs>((_, e) => OnContextRequested(FileList, e, folder: false), "FileBrowser.Menu");
        ModernView.ContextRequested += UiSafe.Handler<ContextRequestedEventArgs>((_, e) => OnContextRequested(ModernView, e, folder: false), "FileBrowser.Menu");
        FolderTree.ContextRequested += UiSafe.Handler<ContextRequestedEventArgs>((_, e) => OnContextRequested(FolderTree, e, folder: true), "FileBrowser.Menu");
        FileList.KeyDown += UiSafe.AsyncHandler<KeyEventArgs>((_, e) => OnKeyAsync(e), "FileBrowser.Key");
        ModernView.KeyDown += UiSafe.AsyncHandler<KeyEventArgs>((_, e) => OnKeyAsync(e), "FileBrowser.Key");
        FileList.PointerMoved += UiSafe.Handler<PointerEventArgs>((_, e) => OnHover(e.Source), "FileBrowser.Hover");
        ModernView.PointerMoved += UiSafe.Handler<PointerEventArgs>((_, e) => OnHover(e.Source), "FileBrowser.Hover");
        FileList.PointerExited += UiSafe.Handler<PointerEventArgs>((_, _) => HidePreview(), "FileBrowser.Hover");
        ModernView.PointerExited += UiSafe.Handler<PointerEventArgs>((_, _) => HidePreview(), "FileBrowser.Hover");
        Track(ListSearch.Attach(FileList, item => (item as FileRow)?.Name));
        Track(ListSearch.Attach(ModernView, item => (item as ModernItem)?.Name));

        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Track(FileDrop.Enable(this, this));

        if (context is null) return;
        _mutationRefresh = Track(UiDispatch.Debounce(TimeSpan.FromMilliseconds(500), RefreshCurrent, "FileBrowser.MutationRefresh", this));
        _thumbnailRefresh = Track(UiDispatch.Debounce(TimeSpan.FromMilliseconds(300), QueueVisibleThumbnails, "FileBrowser.Thumbnails", this));
        ModernView.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => _thumbnailRefresh?.Trigger(), handledEventsToo: true);
        ModernView.SizeChanged += (_, _) => _thumbnailRefresh?.Trigger();
        Track(_queue);
        OnClosedOnce(() =>
        {
            _generation++;
            _watches.Clear();
            PreviewPopup.IsOpen = false;
        });
        RcOptions options = context.App.Options.Current;
        _hover = options.FileBrowserHoverPreview;
        _thumbnails = options.FileBrowserThumbnails;
        ApplyMode(options.ModernFileBrowser);
        Listen<OptionsChangedEventArgs>(h => context.App.Options.Changed += h, h => context.App.Options.Changed -= h, (_, e) => OnOptionsChanged(e), "FileBrowser.Options");
        Track(context.Attach(Subscribe));
        Listen(h => context.SessionReplaced += h, h => context.SessionReplaced -= h, (_, _) => OnSessionReplaced(), "FileBrowser.Replaced");
        Every(WatchInterval, () => UiSafe.Fire(CheckWatchedFilesAsync, "FileBrowser.Watch"), "FileBrowser.Watch");
        if (context.Session.FileBrowser.Folders.Count > 0) ShowFolders(context.Session.FileBrowser.Folders);
        Start();
    }

    // ---- State the tests read --------------------------------------------------------------------------------------------------

    /// <summary>The folder tree's roots.</summary>
    internal ObservableCollection<FolderNode> Folders { get; } = new();

    /// <summary>The rows of the classic list, in display order.</summary>
    internal ObservableCollection<FileRow> Rows { get; } = new();

    /// <summary>The items of the modern grid, folders first.</summary>
    internal ObservableCollection<ModernItem> ModernItems { get; } = new();

    /// <summary>The folder shown (normalised, ending in <c>/</c>, empty for the root).</summary>
    internal string CurrentFolder => _currentFolder;

    /// <summary>The transfer queue.</summary>
    internal TransferQueue Queue => _queue;

    /// <summary>The preview cache.</summary>
    internal PreviewCache Previews => _cache;

    /// <summary>The local files watched for changes (double click), by local path.</summary>
    internal IReadOnlyDictionary<string, WatchedFile> Watches => _watches;

    /// <summary>The log's lines.</summary>
    internal IReadOnlyList<string> LogLines => Log.Lines;

    /// <summary>Whether the modern view is shown.</summary>
    internal bool IsModern => _modern;

    /// <summary>The sort column (0 Name, 1 Rights, 2 Size, 3 Modified) and direction.</summary>
    internal (int Column, bool Descending) Sort => (_sortColumn, _descending);

    /// <summary>Replaces the upload file picker (tests); returns the chosen local files.</summary>
    internal Func<Task<IReadOnlyList<string>>>? PickUploadFiles { get; set; }

    // ---- Session -----------------------------------------------------------------------------------------------------------------

    private IDisposable? Subscribe(IRcSession session)
    {
        IRcFileBrowser browser = session.FileBrowser;
        EventHandler<ValueEventArgs<IReadOnlyList<FileBrowserFolder>>> folders = UiSafe.Handler<ValueEventArgs<IReadOnlyList<FileBrowserFolder>>>((_, e) => ShowFolders(e.Value), "FileBrowser.Folders");
        EventHandler<FileBrowserFilesEventArgs> files = UiSafe.Handler<FileBrowserFilesEventArgs>((_, e) => ShowFiles(e.Folder, e.Files), "FileBrowser.Files");
        EventHandler<FileTransferFailedEventArgs> failed = UiSafe.Handler<FileTransferFailedEventArgs>((_, e) => OnTransferFailed(e), "FileBrowser.TransferFailed");
        EventHandler<FileTransferProgressEventArgs> progress = UiSafe.Handler<FileTransferProgressEventArgs>((_, e) => OnProgress(e), "FileBrowser.Progress");
        EventHandler<TextEventArgs> message = UiSafe.Handler<TextEventArgs>((_, e) => OnMessage(e.Text), "FileBrowser.Message");
        EventHandler<DisconnectedEventArgs> disconnected = UiSafe.Handler<DisconnectedEventArgs>((_, e) => OnDisconnected(e), "FileBrowser.Disconnected");
        browser.FoldersReceived += folders;
        browser.FilesReceived += files;
        browser.TransferFailed += failed;
        browser.TransferProgressChanged += progress;
        session.FileBrowserMessageReceived += message;
        session.Disconnected += disconnected;
        return new Unsubscriber(() =>
        {
            browser.FoldersReceived -= folders;
            browser.FilesReceived -= files;
            browser.TransferFailed -= failed;
            browser.TransferProgressChanged -= progress;
            session.FileBrowserMessageReceived -= message;
            session.Disconnected -= disconnected;
        });
    }

    private void Start()
    {
        if (_context is null) return;
        _ = Request("start", browser => browser.StartAsync());
    }

    private void OnSessionReplaced()
    {
        if (_context is null || IsClosed) return;
        _queue.Clear();
        _generation++;
        _previewTransfers.Clear();
        _chunkSegments.Clear();
        _cache.ClearMemory();
        HidePreview();
        AppendLog("Connected again; reading the server's folders.");
        string folder = _currentFolder;
        Folders.Clear();
        _ruleFolders = Array.Empty<string>();
        Start();
        if (folder.Length != 0) NavigateTo(folder);
    }

    private void OnDisconnected(DisconnectedEventArgs e)
    {
        _queue.Clear();
        AppendLog("Disconnected: " + e.Reason);
    }

    private bool Connected()
    {
        if (_context is not null && !_context.IsEnded && _context.Session.IsConnected) return true;
        AppendLog("Not connected.");
        return false;
    }

    /// <summary>Queues a request that only sends (start, change folder, delete, rename, move); a refusal is logged.</summary>
    private Task<bool> Request(string description, Func<IRcFileBrowser, Task<bool>> send) =>
        _queue.Enqueue(TransferPriority.User, description, async _ =>
        {
            if (!Connected()) return false;
            bool sent = await send(_context!.Session.FileBrowser).ConfigureAwait(true);
            if (!sent) AppendLog(_context.Session.LastError is { Length: > 0 } error ? error : "The server could not be asked (" + description + ").");
            return sent;
        }, false);

    // ---- Folders and listings ------------------------------------------------------------------------------------------------------

    /// <summary>Builds the folder tree from the folder rules, keeping what was expanded.</summary>
    /// <param name="rules">The folder rules.</param>
    internal void ShowFolders(IReadOnlyList<FileBrowserFolder> rules)
    {
        var expanded = new HashSet<string>(Folders.SelectMany(node => node.SelfAndDescendants()).Where(node => node.IsExpanded).Select(node => node.FullPath), StringComparer.Ordinal);
        _ruleFolders = RemotePaths.FoldersOfRules(rules);
        _selectingFolder = true;
        try
        {
            Folders.Clear();
            foreach (FolderNode node in RemoteControl.Ui.Files.FolderTree.Build(rules))
            {
                foreach (FolderNode item in node.SelfAndDescendants()) item.IsExpanded = expanded.Contains(item.FullPath);
                Folders.Add(node);
            }
        }
        finally
        {
            _selectingFolder = false;
        }

        UpdateFolderIcons();
        if (_modern) RebuildModernItems();
    }

    /// <summary>Shows a listing; one for another folder than the current one is ignored (<c>refreshFiles</c>).</summary>
    /// <param name="folder">The listing's folder.</param>
    /// <param name="files">The entries.</param>
    internal void ShowFiles(string folder, IReadOnlyList<FileBrowserEntry> files)
    {
        string normalized = RemotePaths.NormalizeFolder(folder);
        if (_currentFolder.Length != 0 && normalized != _currentFolder) return;
        if (normalized != _currentFolder)
        {
            _generation++;
            _queue.ClearPreviews();
        }

        _currentFolder = normalized;
        UpdateHeader();
        var selected = new HashSet<string>(SelectedRows().Select(row => row.Name), StringComparer.Ordinal);
        var existing = Rows.ToDictionary(row => row.Name, StringComparer.Ordinal);
        var next = new List<FileRow>(files.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (FileBrowserEntry entry in files)
        {
            if (string.IsNullOrEmpty(entry.Name) || !seen.Add(entry.Name)) continue;
            if (existing.TryGetValue(entry.Name, out FileRow? row) && row.Folder == normalized)
            {
                row.Update(entry);
                next.Add(row);
            }
            else
            {
                next.Add(new FileRow(normalized, entry, _context?.App.Assets.FileTypeIcon(entry.Name)));
            }
        }

        next.Sort(Comparer);
        Apply(Rows, next);
        FileList.SelectedItems?.Clear();
        foreach (FileRow row in Rows)
            if (selected.Contains(row.Name)) FileList.SelectedItems?.Add(row);
        UpdateFolderIcons();
        UpdateStatus();
        if (_modern) RebuildModernItems();
        else if (_hover) QueueListingPreviews();
    }

    /// <summary>Goes to a folder (<c>navigateTo</c>): normalises it, empties the lists and asks the server.</summary>
    /// <param name="folder">The folder as typed or chosen.</param>
    internal void NavigateTo(string? folder)
    {
        if (_context is null || IsClosed) return;
        string normalized = RemotePaths.NormalizeFolder(folder);
        _generation++;
        _queue.ClearPreviews();
        if (normalized != _currentFolder) _cache.ClearMemory();
        _currentFolder = normalized;
        Rows.Clear();
        ModernItems.Clear();
        HidePreview();
        UpdateHeader();
        UpdateFolderIcons();
        UpdateStatus();
        if (_modern) RebuildModernItems();
        _ = Request("cd " + normalized, browser => browser.ChangeFolderAsync(normalized));
    }

    /// <summary>Asks for the current folder's listing again.</summary>
    internal void RefreshCurrent()
    {
        if (_context is null || IsClosed) return;
        string folder = _currentFolder;
        _ = Request("cd " + folder, browser => browser.ChangeFolderAsync(folder));
    }

    private void QueueMutationRefresh() => _mutationRefresh?.Trigger();

    private void UpdateHeader()
    {
        FolderLabel.Text = "Current Folder: " + _currentFolder;
        if (!AddressBox.IsFocused) AddressBox.Text = _currentFolder;
    }

    private void OnFolderSelected()
    {
        if (_selectingFolder || FolderTree.SelectedItem is not FolderNode node) return;
        NavigateTo(node.Folder);
    }

    private void UpdateFolderIcons()
    {
        string active = _currentFolder.TrimEnd('/');
        foreach (FolderNode node in Folders.SelectMany(root => root.SelfAndDescendants()))
            node.IsCurrent = active.Length != 0 && (active == node.FullPath || active.StartsWith(node.FullPath + "/", StringComparison.Ordinal));
    }

    private void UpdateStatus()
    {
        int files = Rows.Count(row => !row.IsDirectory);
        int selected = _modern ? (ModernView.SelectedItems?.OfType<ModernItem>().Count(item => !item.IsFolder) ?? 0) : (FileList.SelectedItems?.Count ?? 0);
        string text = files + (files == 1 ? " file" : " files");
        if (selected > 0) text += "  |  " + selected + " selected";
        StatusLabel.Text = text;
    }

    // ---- Sorting -----------------------------------------------------------------------------------------------------------------

    private int Comparer(FileRow a, FileRow b)
    {
        int result = _sortColumn switch
        {
            1 => string.Compare(a.Rights, b.Rights, StringComparison.Ordinal),
            2 => a.Size.CompareTo(b.Size),
            3 => a.Modified.CompareTo(b.Modified),
            _ => 0,
        };
        if (result == 0) result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        if (result == 0) result = string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        return _descending ? -result : result;
    }

    /// <summary>Sorts by a column; the same column again turns the direction.</summary>
    /// <param name="column">0 Name, 1 Rights, 2 Size, 3 Modified.</param>
    internal void SortBy(int column)
    {
        if (column == _sortColumn) _descending = !_descending;
        else
        {
            _sortColumn = column;
            _descending = false;
        }

        string arrow = _descending ? " ▼" : " ▲";
        SortName.Content = "Name" + (_sortColumn == 0 ? arrow : string.Empty);
        SortRights.Content = "Rights" + (_sortColumn == 1 ? arrow : string.Empty);
        SortSize.Content = "Size" + (_sortColumn == 2 ? arrow : string.Empty);
        SortModified.Content = "Modified" + (_sortColumn == 3 ? arrow : string.Empty);
        var selected = SelectedRows().ToArray();
        var next = Rows.ToList();
        next.Sort(Comparer);
        Apply(Rows, next);
        foreach (FileRow row in selected)
            if (FileList.SelectedItems is { } items && !items.Contains(row)) items.Add(row);
        if (_modern) RebuildModernItems();
    }

    /// <summary>Makes a collection equal to a list with moves, inserts and removals (rows keep their identity).</summary>
    private static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> next) where T : class
    {
        var keep = new HashSet<T>(next, ReferenceEqualityComparer.Instance);
        for (int index = target.Count - 1; index >= 0; index--)
            if (!keep.Contains(target[index])) target.RemoveAt(index);
        for (int index = 0; index < next.Count; index++)
        {
            if (index < target.Count && ReferenceEquals(target[index], next[index])) continue;
            int current = -1;
            for (int search = index + 1; search < target.Count; search++)
            {
                if (!ReferenceEquals(target[search], next[index])) continue;
                current = search;
                break;
            }

            if (current >= 0) target.Move(current, index);
            else target.Insert(index, next[index]);
        }

        while (target.Count > next.Count) target.RemoveAt(target.Count - 1);
    }

    // ---- Modern view -------------------------------------------------------------------------------------------------------------

    private void OnOptionsChanged(OptionsChangedEventArgs e)
    {
        RcOptions options = e.Current;
        if (e.Contains(OptionKeys.ModernFileBrowser)) ApplyMode(options.ModernFileBrowser);
        if (e.Contains(OptionKeys.FileBrowserThumbnails))
        {
            _thumbnails = options.FileBrowserThumbnails;
            if (!_thumbnails && !_hover) _queue.ClearPreviews();
            if (_modern) RebuildModernItems();
        }

        if (e.Contains("filebrowserhoverpreview"))
        {
            _hover = options.FileBrowserHoverPreview;
            if (!_hover)
            {
                HidePreview();
                if (!_thumbnails) _queue.ClearPreviews();
            }
            else if (_modern) _thumbnailRefresh?.Trigger();
            else QueueListingPreviews();
        }
    }

    private void ApplyMode(bool modern)
    {
        _modern = modern;
        _generation++;
        _queue.ClearPreviews();
        HidePreview();
        FolderLabel.IsVisible = !modern;
        AddressRow.IsVisible = modern;
        ClassicView.IsVisible = !modern;
        ModernHost.IsVisible = modern;
        if (modern) RebuildModernItems();
        else
        {
            ModernItems.Clear();
            if (_hover) QueueListingPreviews();
        }

        UpdateStatus();
    }

    /// <summary>Folders first (from the folder rules), then the files in the list's order, with thumbnails when they are cached.</summary>
    private void RebuildModernItems()
    {
        var items = new List<ModernItem>();
        foreach (string name in RemotePaths.SubfoldersOf(_ruleFolders, _currentFolder))
            items.Add(new ModernItem(name, _currentFolder + name + "/", true, string.Empty, null, 48, null));
        foreach (FileRow row in Rows)
        {
            if (row.IsDirectory)
            {
                string name = row.Name.TrimEnd('/');
                if (items.Any(item => item.IsFolder && item.Name == name)) continue;
                items.Add(new ModernItem(name, _currentFolder + name + "/", true, row.Rights, null, 48, row));
                continue;
            }

            PreviewImage? cached = _thumbnails && PreviewRules.CanAutoPreview(row.Name, row.Size) ? _cache.Get(KeyOf(row)) : null;
            items.Add(cached is null
                ? new ModernItem(row.Name, row.Path, false, row.Rights, row.Icon, 44, row)
                : new ModernItem(row.Name, row.Path, false, row.Rights, cached.Thumbnail, PreviewRules.ThumbnailSize, row));
        }

        ModernItems.Clear();
        foreach (ModernItem item in items) ModernItems.Add(item);
        UpdateStatus();
        _thumbnailRefresh?.Trigger();
    }

    /// <summary>Queues the previews of the modern items in view (at most 12 at a time, <c>loadVisibleThumbnails</c>).</summary>
    internal void QueueVisibleThumbnails()
    {
        if (!_modern || (!_thumbnails && !_hover) || IsClosed) return;
        _queue.ClearPreviews();
        ScrollViewer? scroller = ModernView.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var view = new Rect(scroller?.Bounds.Size ?? ModernView.Bounds.Size);
        int queued = 0;
        foreach (ModernItem item in ModernItems)
        {
            if (queued >= 12) break;
            if (item.IsFolder || item.Row is not { } row || !PreviewRules.CanAutoPreview(row.Name, row.Size)) continue;
            if (ModernView.ContainerFromItem(item) is Control container && scroller is not null)
            {
                Point? origin = container.TranslatePoint(default, scroller);
                if (origin is { } at && !view.Intersects(new Rect(at, container.Bounds.Size))) continue;
            }

            // A picture that is cached by now (asked for by the list view, or finished since the items were made) is shown at once.
            if (_thumbnails && item.ImageSize != PreviewRules.ThumbnailSize && _cache.Get(KeyOf(row)) is { } cached)
            {
                item.Image = cached.Thumbnail;
                item.ImageSize = PreviewRules.ThumbnailSize;
                continue;
            }

            if (QueuePreview(row)) queued++;
        }
    }

    // ---- Previews ----------------------------------------------------------------------------------------------------------------

    private string KeyOf(FileRow row) => PreviewCache.KeyOf(_context?.ServerName ?? string.Empty, row.Path, row.Size, row.Modified);

    private void QueueListingPreviews()
    {
        int budget = 50 - _cache.Count - _queue.PreviewCount;
        foreach (FileRow row in Rows)
        {
            if (budget <= 0) break;
            if (QueuePreview(row)) budget--;
        }
    }

    /// <summary>Queues the preview download of a file when it is eligible and not cached, broken or queued.</summary>
    /// <param name="row">The file.</param>
    /// <returns>Whether it was queued.</returns>
    internal bool QueuePreview(FileRow row)
    {
        if (_context is null || !PreviewRules.CanAutoPreview(row.Name, row.Size)) return false;
        string key = KeyOf(row);
        string description = "preview " + row.Path;
        if (_cache.Get(key) is not null || _cache.IsBroken(key) || _queue.HasPreview(description)) return false;
        int generation = _generation;
        string path = row.Path;
        string name = row.Name;
        _ = _queue.Enqueue(TransferPriority.Preview, description, async token =>
        {
            if (generation != _generation || IsClosed) return false;
            byte[]? content = await _cache.ReadAsync(key).ConfigureAwait(true);
            bool fromDisk = content is not null;
            if (content is null)
            {
                if (_context.IsEnded || !_context.Session.IsConnected) return false;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(PreviewTimeout);
                _previewTransfers.Add(path);
                try
                {
                    content = await _context.Session.FileBrowser.DownloadAsync(path, timeout.Token).ConfigureAwait(true);
                }
                finally
                {
                    _previewTransfers.Remove(path);
                }
            }

            if (content is null)
            {
                _cache.MarkBroken(key);
                return false;
            }

            PreviewImage? image = await PreviewDecoder.DecodeAsync(name, content, token).ConfigureAwait(true);
            if (image is null)
            {
                _cache.MarkBroken(key);
                return false;
            }

            if (!fromDisk) _ = _cache.WriteAsync(key, content);
            _cache.Put(key, image);
            // Shown wherever that file is on screen now, also when the view changed while the picture was on its way (it was only
            // cached then, and the file kept its plain icon). ApplyPreview touches nothing but the item with this very path.
            if (!IsClosed) ApplyPreview(path, image);
            return true;
        }, false);
        return true;
    }

    private void ApplyPreview(string path, PreviewImage image)
    {
        if (_modern && _thumbnails)
        {
            foreach (ModernItem item in ModernItems)
            {
                if (item.IsFolder || item.Path != path) continue;
                item.Image = image.Thumbnail;
                item.ImageSize = PreviewRules.ThumbnailSize;
            }
        }

        if (_hover && _hoveredPath == path) ShowPreview(path);
    }

    private void OnHover(object? source)
    {
        if (!_hover)
        {
            HidePreview();
            return;
        }

        string? path = (source as StyledElement)?.DataContext switch
        {
            FileRow row => row.Path,
            ModernItem { IsFolder: false } item => item.Path,
            _ => null,
        };
        if (path is null)
        {
            HidePreview();
            return;
        }

        if (path == _hoveredPath && PreviewPopup.IsOpen) return;
        _hoveredPath = path;
        ShowPreview(path);
    }

    /// <summary>Shows the cached preview of a file next to the pointer, or hides the popup when there is none.</summary>
    /// <param name="path">The file.</param>
    internal void ShowPreview(string path)
    {
        _hoveredPath = path;
        FileRow? row = Rows.FirstOrDefault(item => item.Path == path);
        PreviewImage? image = row is null ? null : _cache.Get(KeyOf(row));
        if (image is null)
        {
            PreviewPopup.IsOpen = false;
            return;
        }

        PreviewImage.Source = image.Preview;
        PreviewLabel.Text = RemotePaths.NameOf(path);
        PreviewPopup.PlacementTarget = _modern ? ModernView : FileList;
        PreviewPopup.IsOpen = true;
    }

    private void HidePreview()
    {
        _hoveredPath = null;
        PreviewPopup.IsOpen = false;
    }

    // ---- Log -----------------------------------------------------------------------------------------------------------------------

    /// <summary>Adds a line to the log.</summary>
    /// <param name="text">The line.</param>
    internal void AppendLog(string text)
    {
        if (IsClosed) return;
        Log.Append(text);
    }

    private void OnTransferFailed(FileTransferFailedEventArgs e)
    {
        if (_previewTransfers.Contains(e.Path)) return;
        AppendLog("Transfer failed: " + e.Path + (string.IsNullOrEmpty(e.Reason) ? string.Empty : " (" + e.Reason + ")"));
    }

    private void OnProgress(FileTransferProgressEventArgs e)
    {
        if (_previewTransfers.Contains(e.Path)) return;
        LogChunk(e.Path, e.Transferred, e.Total, (e.Upload ? "Sent chunk: " : "Received chunk: ") + e.Transferred + "/" + e.Total + " bytes for " + e.Path);
    }

    private void OnMessage(string text)
    {
        if (text is null) return;
        const string chunk = "Received chunk: ";
        if (text.StartsWith(chunk, StringComparison.Ordinal))
        {
            if (_previewTransfers.Any(path => text.Contains(path, StringComparison.Ordinal) || text.Contains(RemotePaths.NameOf(path), StringComparison.Ordinal))) return;
            int slash = text.IndexOf('/', chunk.Length);
            int marker = slash < 0 ? -1 : text.IndexOf(" bytes for ", slash + 1, StringComparison.Ordinal);
            if (marker > 0 && long.TryParse(text.AsSpan(chunk.Length, slash - chunk.Length), out long received) && long.TryParse(text.AsSpan(slash + 1, marker - slash - 1), out long total))
            {
                LogChunk(text[(marker + 11)..], received, total, text);
                return;
            }
        }

        AppendLog(text);
    }

    /// <summary>Logs transfer progress only per 10 % step (<c>onMessage</c>).</summary>
    private void LogChunk(string path, long received, long total, string text)
    {
        long segmentSize = total <= 0 ? 1 : Math.Max(1, (total + 9) / 10);
        int segment = total <= 0 ? 0 : received >= total ? 10 : (int)Math.Min(10, received / segmentSize);
        if (_chunkSegments.TryGetValue(path, out int previous) && previous == segment) return;
        _chunkSegments[path] = segment;
        AppendLog(text);
        if (total > 0 && received >= total) _chunkSegments.Remove(path);
    }

    // ---- Selection and menus --------------------------------------------------------------------------------------------------------

    private IEnumerable<FileRow> SelectedRows() =>
        _modern
            ? ModernView.SelectedItems?.OfType<ModernItem>().Where(item => !item.IsFolder && item.Row is not null).Select(item => item.Row!) ?? Enumerable.Empty<FileRow>()
            : FileList.SelectedItems?.OfType<FileRow>() ?? Enumerable.Empty<FileRow>();

    /// <summary>The paths of the selected files.</summary>
    /// <returns>The paths, in list order.</returns>
    internal IReadOnlyList<string> SelectedPaths() => SelectedRows().Where(row => !row.IsDirectory).OrderBy(row => Rows.IndexOf(row)).Select(row => row.Path).ToArray();

    private void OnContextRequested(Control view, ContextRequestedEventArgs e, bool folder)
    {
        e.Handled = true;
        object? item = (e.Source as StyledElement)?.DataContext;
        IReadOnlyList<string> paths;
        bool folders = folder;
        if (folder)
        {
            if (item is not FolderNode node) return;
            _selectingFolder = true;
            try
            {
                FolderTree.SelectedItem = node;
            }
            finally
            {
                _selectingFolder = false;
            }

            paths = [node.FullPath];
        }
        else
        {
            if (item is FileRow row && FileList.SelectedItems is { } rows && !rows.Contains(row))
            {
                rows.Clear();
                rows.Add(row);
            }
            else if (item is ModernItem modern && ModernView.SelectedItems is { } items && !items.Contains(modern))
            {
                items.Clear();
                items.Add(modern);
            }

            if (item is ModernItem { IsFolder: true } folderItem)
            {
                folders = true;
                paths = [folderItem.Path.TrimEnd('/')];
            }
            else paths = SelectedPaths();
        }

        if (paths.Count == 0) return;
        ContextMenu menu = BuildMenu(paths, folders);
        menu.Open(view);
    }

    /// <summary>The context menu of files or folders (<c>showItemMenu</c>).</summary>
    /// <param name="paths">The paths (folders without trailing <c>/</c>).</param>
    /// <param name="folders">Whether they are folders.</param>
    /// <returns>The menu.</returns>
    internal ContextMenu BuildMenu(IReadOnlyList<string> paths, bool folders)
    {
        var items = new List<Control>();
        MenuItem Item(string header, string name, Func<Task> action)
        {
            var item = new MenuItem { Header = header, Name = name };
            item.Click += On.ClickAsync(action, "FileBrowser." + name);
            return item;
        }

        string folder = _currentFolder;
        if (!folders)
        {
            items.Add(Item(paths.Count == 1 ? "Download" : "Download selected", "MenuDownload", () => DownloadAsync(paths)));
            if (paths.Count == 1 && IsEditable(paths[0])) items.Add(Item("Edit", "MenuEdit", () => EditAsync(paths[0])));
            items.Add(Item("Upload file(s)", "MenuUpload", () => PickAndUploadAsync(folder)));
            items.Add(new Separator());
            items.Add(Item(paths.Count == 1 ? "Move" : "Move selected", "MenuMove", () => MoveAsync(paths)));
        }
        else
        {
            string target = RemotePaths.NormalizeFolder(paths[0]);
            items.Add(Item("Upload file(s)", "MenuUpload", () => PickAndUploadAsync(target)));
            items.Add(new Separator());
        }

        if (paths.Count == 1) items.Add(Item("Rename", "MenuRename", () => RenameAsync(paths[0])));
        items.Add(Item("Delete", "MenuDelete", () => DeleteAsync(paths)));
        return new ContextMenu { ItemsSource = items };
    }

    /// <summary>Whether Edit is offered: not for pictures, archives, fonts and compiled scripts (D-27).</summary>
    private static bool IsEditable(string path)
    {
        if (PreviewRules.IsImage(path)) return false;
        string icon = AssetStore.FileTypeIconName(RemotePaths.NameOf(path));
        if (icon is "rcfiles_binary.png" or "rcfiles_archive.png" or "rcfiles_ttf.png") return false;
        string extension = Path.GetExtension(path);
        return !extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
    }

    private async Task OnKeyAsync(KeyEventArgs e)
    {
        IReadOnlyList<string> paths = SelectedPaths();
        switch (e.Key)
        {
            case Key.Delete when paths.Count > 0:
                e.Handled = true;
                await DeleteAsync(paths).ConfigureAwait(true);
                break;
            case Key.F2 when paths.Count == 1:
                e.Handled = true;
                await RenameAsync(paths[0]).ConfigureAwait(true);
                break;
            case Key.F5:
                e.Handled = true;
                RefreshCurrent();
                break;
            case Key.Back:
                e.Handled = true;
                NavigateTo(RemotePaths.Parent(_currentFolder));
                break;
            case Key.Enter:
                e.Handled = true;
                if (_modern && ModernView.SelectedItem is ModernItem { IsFolder: true } folder) NavigateTo(folder.Path);
                else if (SelectedRows().FirstOrDefault() is { } row) await OpenAsync(row).ConfigureAwait(true);
                break;
        }
    }

    private async Task OnDoubleTappedAsync(object? source)
    {
        switch ((source as StyledElement)?.DataContext)
        {
            case ModernItem { IsFolder: true } folder:
                NavigateTo(folder.Path);
                break;
            case ModernItem { Row: { } row }:
                await OpenAsync(row).ConfigureAwait(true);
                break;
            case FileRow { IsDirectory: true } directory:
                NavigateTo(directory.Path);
                break;
            case FileRow row:
                await OpenAsync(row).ConfigureAwait(true);
                break;
        }
    }

    // ---- Actions -------------------------------------------------------------------------------------------------------------------

    private long ModifiedOf(string path) => Rows.FirstOrDefault(row => row.Path == path)?.Modified ?? 0;

    /// <summary>Downloads a file through the queue at the user's priority; null when it failed.</summary>
    private Task<byte[]?> FetchAsync(string path) =>
        _queue.Enqueue<byte[]?>(TransferPriority.User, "download " + path, async token =>
        {
            if (!Connected()) return null;
            byte[]? content = await _context!.Session.FileBrowser.DownloadAsync(path, token).ConfigureAwait(true);
            if (content is null && !IsClosed) AppendLog("Download failed: " + path);
            return content;
        }, null);

    /// <summary>Downloads files into the download folder (<c>onDownload</c>).</summary>
    /// <param name="paths">The remote paths.</param>
    /// <returns>Completes when every file was handled.</returns>
    internal async Task DownloadAsync(IReadOnlyList<string> paths)
    {
        if (_context is null) return;
        var work = new List<Task>();
        foreach (string path in paths)
        {
            long modified = ModifiedOf(path);
            Task<byte[]?> fetch = FetchAsync(path);
            work.Add(SaveWhenDoneAsync(path, fetch, modified));
        }

        await Task.WhenAll(work).ConfigureAwait(true);
    }

    private async Task<string?> SaveWhenDoneAsync(string path, Task<byte[]?> fetch, long modified)
    {
        byte[]? content = await fetch.ConfigureAwait(true);
        if (content is null || IsClosed || _context is null) return null;
        return await SaveAsync(path, content, modified).ConfigureAwait(true);
    }

    private async Task<string?> SaveAsync(string path, byte[] content, long modified)
    {
        string downloads = _context!.App.Paths.DownloadDirectory(_context.App.Options.Current);
        if (!LocalCopies.TryTarget(downloads, _context.ServerName, path, out string target, out string reason))
        {
            AppendLog(reason);
            return null;
        }

        LocalWrite written = await LocalCopies.WriteAsync(target, content, modified).ConfigureAwait(true);
        if (written.Path is null)
        {
            AppendLog(written.Error ?? "Could not save downloaded file.");
            return null;
        }

        if (written.KeptAside is { } aside) AppendLog("Kept the earlier, different local copy as " + aside);
        AppendLog("Downloaded file " + written.Path);
        return written.Path;
    }

    /// <summary>Downloads a file and opens it in the script editor as text (<c>onEditAsText</c>).</summary>
    /// <param name="path">The remote path.</param>
    /// <returns>Completes when the editor was asked to open.</returns>
    internal async Task EditAsync(string path)
    {
        if (_context is null) return;
        byte[]? content = await FetchAsync(path).ConfigureAwait(true);
        if (content is null || IsClosed) return;
        ShowTextEditor(path, content);
    }

    private void ShowTextEditor(string path, byte[] content)
    {
        if (_context is null) return;
        if (content.Length > LocalCopies.MaximumEditorBytes)
        {
            AppendLog("File is too large to open in the built-in editor.");
            return;
        }

        EditableText? text = EditableText.Decode(content);
        if (text is null)
        {
            AppendLog(path + " is not a text file; it is not opened in the editor.");
            return;
        }

        if (!_context.Tools.IsRegistered(ToolIds.ScriptEditor))
        {
            AppendLog("The script editor is not available.");
            return;
        }

        string title = string.IsNullOrEmpty(_context.ServerName) ? path : path + " - " + _context.ServerName;
        _context.OpenTool(ToolIds.ScriptEditor, new ScriptRequest(RemoteControl.Ui.Shell.ScriptKind.Text, title, Text: text.Text, Save: edited => UploadBytesAsync(path, text.Encode(edited))));
    }

    /// <summary>
    /// A double click on a file: downloads it, then opens it with the desktop's program (or in the text editor for anything a desktop
    /// would run) and watches the local copy to upload it again when it changes.
    /// </summary>
    /// <param name="row">The file.</param>
    /// <returns>Completes when opened.</returns>
    internal async Task OpenAsync(FileRow row)
    {
        if (_context is null || row.IsDirectory) return;
        string path = row.Path;
        bool writable = row.Writable;
        long modified = row.Modified;
        byte[]? content = await FetchAsync(path).ConfigureAwait(true);
        if (content is null || IsClosed) return;
        if (LocalCopies.IsRunnable(row.Name))
        {
            ShowTextEditor(path, content);
            return;
        }

        string? local = await SaveAsync(path, content, modified).ConfigureAwait(true);
        if (local is null || IsClosed) return;
        if (!ExternalPrograms.OpenFile(local, _context.App.Log)) AppendLog("Could not open downloaded file.");
        if (LocalCopies.StampOf(local) is { } stamp) _watches[local] = new WatchedFile(local, path, writable, stamp);
    }

    /// <summary>Uploads the watched local files that changed (<c>watchExternalFile</c>).</summary>
    /// <returns>Completes when the uploads were queued and done.</returns>
    internal async Task CheckWatchedFilesAsync()
    {
        if (_context is null || IsClosed) return;
        foreach (WatchedFile watch in _watches.Values.ToArray())
        {
            if (LocalCopies.StampOf(watch.LocalPath) is not { } stamp || stamp == watch.Stamp) continue;
            watch.Stamp = stamp;
            if (!watch.Writable)
            {
                AppendLog("No write rights for " + watch.RemotePath);
                continue;
            }

            (byte[]? content, string? error) = await LocalCopies.ReadForUploadAsync(watch.LocalPath, _context.Session.MaxUploadFileSize).ConfigureAwait(true);
            if (content is null)
            {
                AppendLog(error ?? "Could not read changed file.");
                continue;
            }

            AppendLog("Uploading changed file " + watch.RemotePath);
            await UploadBytesAsync(watch.RemotePath, content).ConfigureAwait(true);
        }
    }

    /// <summary>Uploads bytes to a remote path through the queue; the folder is listed again afterwards.</summary>
    /// <param name="path">The remote path.</param>
    /// <param name="content">The bytes.</param>
    /// <returns>Whether every piece was sent.</returns>
    internal Task<bool> UploadBytesAsync(string path, byte[] content) =>
        _queue.Enqueue(TransferPriority.User, "upload " + path, async token =>
        {
            if (!Connected()) return false;
            bool sent = await _context!.Session.FileBrowser.UploadAsync(path, content, token).ConfigureAwait(true);
            if (!sent) AppendLog(_context.Session.LastError is { Length: > 0 } error ? error : "Upload failed: " + path);
            else QueueMutationRefresh();
            return sent;
        }, false);

    private async Task PickAndUploadAsync(string folder)
    {
        if (_context is null || !Connected()) return;
        IReadOnlyList<string> files = PickUploadFiles is not null ? await PickUploadFiles().ConfigureAwait(true) : await FilePickers.OpenFilesAsync(this, "Upload file(s)").ConfigureAwait(true);
        if (IsClosed) return;
        await UploadLocalFilesAsync(files, folder).ConfigureAwait(true);
    }

    /// <summary>Uploads local files into a folder (the picker, a drop from the desktop).</summary>
    /// <param name="files">The local files.</param>
    /// <param name="folder">The remote folder.</param>
    /// <returns>Completes when sent.</returns>
    internal async Task UploadLocalFilesAsync(IReadOnlyList<string> files, string folder)
    {
        if (_context is null) return;
        string target = RemotePaths.NormalizeFolder(folder);
        foreach (string file in files)
        {
            if (Directory.Exists(file))
            {
                AppendLog("Folders cannot be uploaded (the server takes files): " + file);
                continue;
            }

            (byte[]? content, string? error) = await LocalCopies.ReadForUploadAsync(file, _context.Session.MaxUploadFileSize).ConfigureAwait(true);
            if (IsClosed) return;
            if (content is null)
            {
                AppendLog(error ?? "Unable to read selected file");
                continue;
            }

            await UploadBytesAsync(target + Path.GetFileName(file), content).ConfigureAwait(true);
        }
    }

    /// <summary>Asks for a new name and renames (<c>onRename</c>).</summary>
    /// <param name="path">The file or folder.</param>
    /// <returns>Completes when sent or cancelled.</returns>
    internal async Task RenameAsync(string path)
    {
        if (_context is null || !Connected()) return;
        string? name = await Dialogs.PromptAsync("Rename", "New name for " + path + ":", RemotePaths.NameOf(path), this).ConfigureAwait(true);
        if (IsClosed || string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            AppendLog("A new name cannot contain a folder: " + name);
            return;
        }

        if (await Request("rename " + path, browser => browser.RenameAsync(path, name)).ConfigureAwait(true)) QueueMutationRefresh();
    }

    /// <summary>Asks for a destination folder and moves (<c>onMove</c>).</summary>
    /// <param name="paths">The files.</param>
    /// <returns>Completes when sent or cancelled.</returns>
    internal async Task MoveAsync(IReadOnlyList<string> paths)
    {
        if (_context is null || paths.Count == 0 || !Connected()) return;
        string title = paths.Count == 1 ? "Move " + paths[0] : "Move selected files";
        string? destination = await Dialogs.PromptAsync(title, "Destination folder:", _currentFolder, this).ConfigureAwait(true);
        if (IsClosed || destination is null) return;
        await MoveIntoFolderAsync(paths, destination).ConfigureAwait(true);
    }

    /// <summary>Moves files into a folder (the Move dialog, a drag onto a folder).</summary>
    /// <param name="paths">The files.</param>
    /// <param name="folder">The destination.</param>
    /// <returns>Completes when sent.</returns>
    internal async Task MoveIntoFolderAsync(IReadOnlyList<string> paths, string folder)
    {
        string destination = RemotePaths.NormalizeFolder(folder);
        bool changed = false;
        foreach (string path in paths)
        {
            if (RemotePaths.FolderOf(path) == destination) continue;
            if (!await Request("move " + path, browser => browser.MoveAsync(path, destination)).ConfigureAwait(true)) continue;
            AppendLog("Moved file " + path + " to " + destination);
            changed = true;
        }

        if (changed) QueueMutationRefresh();
    }

    /// <summary>Asks, naming the files, and deletes them (the C++ deleted without asking).</summary>
    /// <param name="paths">The files or folders.</param>
    /// <returns>Completes when sent or cancelled.</returns>
    internal async Task DeleteAsync(IReadOnlyList<string> paths)
    {
        if (_context is null || paths.Count == 0 || !Connected()) return;
        const int listed = 15;
        string names = string.Join("\n", paths.Take(listed));
        if (paths.Count > listed) names += "\n… and " + (paths.Count - listed) + " more";
        string question = (paths.Count == 1 ? "Delete this from the server?" : "Delete these " + paths.Count + " items from the server?") + "\n\n" + names + "\n\nThis cannot be undone.";
        if (!await Dialogs.ConfirmAsync("Delete", question, this, "Delete").ConfigureAwait(true) || IsClosed) return;
        bool changed = false;
        foreach (string path in paths)
            if (await Request("delete " + path, browser => browser.DeleteAsync(path)).ConfigureAwait(true)) changed = true;
        if (changed) QueueMutationRefresh();
    }

    // ---- Drag and drop ---------------------------------------------------------------------------------------------------------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = false;
        _dragStart = null;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        object? item = (e.Source as StyledElement)?.DataContext;
        if (item is not (FileRow { IsDirectory: false } or ModernItem { IsFolder: false })) return;
        _dragStart = e.GetPosition(this);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || _dragging) return;
        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - start.X) < 8 && Math.Abs(now.Y - start.Y) < 8) return;
        _dragPaths = SelectedPaths();
        _dragging = _dragPaths.Count > 0;
        if (_dragging) Cursor = DragCursor;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool dragging = _dragging;
        _dragging = false;
        _dragStart = null;
        if (!dragging) return;
        Cursor = Cursor.Default;
        string? folder = FolderAt(e.GetPosition(this));
        if (folder is null) return;
        IReadOnlyList<string> paths = _dragPaths;
        UiSafe.Fire(() => MoveIntoFolderAsync(paths, folder), "FileBrowser.DragMove");
    }

    /// <summary>The folder under a point of the window: a node of the tree or a folder item of the grid.</summary>
    /// <param name="point">The point in window coordinates.</param>
    /// <returns>The folder (ending in <c>/</c>), or null.</returns>
    internal string? FolderAt(Point point)
    {
        for (Visual? visual = this.GetVisualAt(point); visual is not null; visual = visual.GetVisualParent())
        {
            switch ((visual as StyledElement)?.DataContext)
            {
                case FolderNode node:
                    return node.Folder;
                case ModernItem { IsFolder: true } item:
                    return item.Path;
            }

            if (ReferenceEquals(visual, FileList) || ReferenceEquals(visual, ModernView)) return null;
        }

        return null;
    }

    // ---- Files dropped from the desktop (FileDrop: Avalonia's events on Windows, the XDND bridge on X11) ------------------------

    /// <summary>The folder files dropped at a point go to: the folder under the pointer (tree node, grid folder), else the folder shown.</summary>
    /// <param name="point">The point in window coordinates.</param>
    /// <returns>The folder (ending in <c>/</c>), or null when no folder is open.</returns>
    internal string? DropFolderAt(Point point)
    {
        string folder = FolderAt(point) ?? _currentFolder;
        return folder.Length == 0 ? null : folder;
    }

    /// <summary>Whether a drop at a point is taken, with the hint naming the folder (<see cref="IFileDropTarget"/>).</summary>
    bool IFileDropTarget.FileDragOver(Point point)
    {
        if (_context is null || IsClosed || _context.IsEnded || !_context.Session.IsConnected || DropFolderAt(point) is not { } folder)
        {
            DropHint.IsVisible = false;
            return false;
        }

        DropHintText.Text = "Drop to upload into " + folder;
        DropHint.IsVisible = true;
        return true;
    }

    /// <inheritdoc />
    void IFileDropTarget.FileDragEnded() => DropHint.IsVisible = false;

    /// <inheritdoc />
    void IFileDropTarget.FilesDropped(Point point, IReadOnlyList<string> paths)
    {
        DropHint.IsVisible = false;
        if (_context is null || IsClosed) return;
        if (DropFolderAt(point) is not { } folder)
        {
            AppendLog("Open a folder first: dropped files are uploaded into the folder shown (or the folder they are dropped on).");
            return;
        }

        AppendLog(paths.Count == 1 ? "Uploading the dropped file into " + folder : "Uploading " + paths.Count + " dropped files into " + folder);
        UiSafe.Fire(() => UploadLocalFilesAsync(paths, folder), "FileBrowser.Drop");
    }
}

/// <summary>A local copy opened with the desktop's program and uploaded again when it changes.</summary>
/// <param name="localPath">The local file.</param>
/// <param name="remotePath">The remote path.</param>
/// <param name="writable">Whether the file's rights allow writing.</param>
/// <param name="stamp">The time and size last seen.</param>
internal sealed class WatchedFile(string localPath, string remotePath, bool writable, (DateTime Modified, long Length) stamp)
{
    /// <summary>The local file.</summary>
    public string LocalPath { get; } = localPath;

    /// <summary>The remote path.</summary>
    public string RemotePath { get; } = remotePath;

    /// <summary>Whether the rights allow writing.</summary>
    public bool Writable { get; } = writable;

    /// <summary>The time and size last seen.</summary>
    public (DateTime Modified, long Length) Stamp { get; set; } = stamp;
}
