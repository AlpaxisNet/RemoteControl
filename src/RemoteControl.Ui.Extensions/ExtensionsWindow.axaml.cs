using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteControl.Extensions;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Extensions;

/// <summary>
/// The extensions window (<c>TExtensionsManager::showWindow</c>/<c>refresh</c>, review editors 6), one for the program: every package
/// with its name, version, publisher and state (an invalid one with its load error), Enabled, the output-tab toggle, Details and
/// Remove per row (and in the row's menu, with Open), the log of the selected package, Refresh, Open Extensions Folder and Close
/// (which hides it, as the C++ did). The rows are updated in place from the manager's events, so the selection stays (the C++
/// destroyed and rebuilt every row).
/// </summary>
internal sealed partial class ExtensionsWindow : RcWindow, IExtensionRowActions
{
    private readonly ExtensionsHost _host;
    private readonly Dictionary<string, ExtensionDetailsWindow> _details = new(StringComparer.Ordinal);
    private string? _logId;

    /// <summary>For the XAML previewer only.</summary>
    public ExtensionsWindow() : this(null!, null!)
    {
    }

    /// <summary>Creates the window.</summary>
    /// <param name="host">The program's extensions.</param>
    /// <param name="services">The program's services.</param>
    public ExtensionsWindow(ExtensionsHost host, AppServices services) : base(services)
    {
        InitializeComponent();
        _host = host;
        ExtensionList.ItemsSource = Rows;
        if (host is null) return;
        HideOnClose = true;

        RefreshButton.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => RefreshAsync(), "Extensions.Refresh");
        ToolTip.SetTip(OpenFolderButton, "Open " + host.UserExtensionsFolder);
        OpenFolderButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => host.OpenUserFolder(), "Extensions.OpenFolder");
        CloseButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => HideAndRemember(), "Extensions.Close");
        ExtensionList.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => ShowLogOfSelection(), "Extensions.Selection");

        Listen<ExtensionChangedEventArgs>(handler => host.ExtensionsChanged += handler, handler => host.ExtensionsChanged -= handler, (_, e) => OnExtensionsChanged(e.ExtensionId), "Extensions.Changed");
        Listen<ExtensionOutputEventArgs>(handler => host.LogLine += handler, handler => host.LogLine -= handler, (_, e) => OnLogLine(e), "Extensions.LogLine");
        OnClosedOnce(() =>
        {
            foreach (ExtensionDetailsWindow window in _details.Values.ToArray()) window.Close();
            _details.Clear();
        });
        Synchronize();
    }

    /// <summary>The rows, in the manager's order.</summary>
    internal ObservableCollection<ExtensionRow> Rows { get; } = [];

    /// <summary>The open details windows by extension id (tests).</summary>
    internal IReadOnlyDictionary<string, ExtensionDetailsWindow> DetailsWindows => _details;

    /// <summary>The row of an extension, or null.</summary>
    internal ExtensionRow? RowOf(string id) => Rows.FirstOrDefault(row => row.Id == id);

    /// <summary>Finds the packages again.</summary>
    internal async Task RefreshAsync()
    {
        await _host.RescanAsync().ConfigureAwait(true);
        if (!IsClosed) Synchronize();
    }

    /// <inheritdoc />
    public void RequestEnabled(ExtensionRow row, bool enabled) => Report(_host.SetEnabled(row.Id, enabled), row, "Enable Extension");

    /// <inheritdoc />
    public void RequestOutputTab(ExtensionRow row, bool enabled) => Report(_host.SetOutputTab(row.Id, enabled), row, "Extension Output Tab");

    /// <inheritdoc />
    public void ShowDetails(ExtensionRow row)
    {
        if (IsClosed || _host.Find(row.Id) is not { } info) return;
        if (_details.TryGetValue(row.Id, out ExtensionDetailsWindow? open) && !open.IsClosed)
        {
            open.ShowAndActivate(this);
            return;
        }
        var window = new ExtensionDetailsWindow(_host, Services!, info);
        _details[row.Id] = window;
        window.OnClosedOnce(() =>
        {
            if (_details.TryGetValue(row.Id, out ExtensionDetailsWindow? current) && ReferenceEquals(current, window)) _details.Remove(row.Id);
        });
        window.ShowAndActivate(this);
    }

    /// <inheritdoc />
    public void Remove(ExtensionRow row) => UiSafe.Fire(() => RemoveAsync(row), "Extensions.Remove");

    /// <inheritdoc />
    public void Open(ExtensionRow row)
    {
        if (_host.Find(row.Id) is not { } info) return;
        if (!_host.OpenPackage(info))
            UiSafe.Fire(() => Dialogs.ShowErrorAsync("Unable to open extension path", "The target path could not be opened:\n" + info.Manifest.OpenPath, this), "Extensions.OpenFailed");
    }

    /// <summary>Asks, naming the folder that will be deleted, then removes the package.</summary>
    internal async Task RemoveAsync(ExtensionRow row)
    {
        if (IsClosed || _host.Find(row.Id) is not { } info || !_host.CanRemove(info)) return;
        int generation = _host.Generation;
        string message = $"Remove extension '{info.Name}'?\n\nThis stops its runtime and deletes its folder:\n{info.Manifest.Directory}";
        bool confirmed = await Dialogs.ConfirmAsync("Remove Extension", message, this, "Remove", "Cancel").ConfigureAwait(true);
        if (!confirmed || IsClosed || generation != _host.Generation) return;
        ExtensionCallResult result = await _host.RemoveAsync(info.Id, generation).ConfigureAwait(true);
        if (!result.Success && !IsClosed) await Dialogs.ShowErrorAsync("Remove Extension", result.Error, this).ConfigureAwait(true);
    }

    private void Report(ExtensionCallResult result, ExtensionRow row, string title)
    {
        if (result.Success) return;
        if (_host.Find(row.Id) is { } info) row.Update(info, _host.CanRemove(info));
        UiSafe.Fire(() => Dialogs.ShowErrorAsync(title, result.Error, this), "Extensions.Report");
    }

    private void OnExtensionsChanged(string? id)
    {
        if (id is null)
        {
            Synchronize();
            return;
        }
        ExtensionInfo? info = _host.Find(id);
        ExtensionRow? row = RowOf(id);
        if (info is null || row is null)
        {
            Synchronize();
            return;
        }
        row.Update(info, _host.CanRemove(info));
        if (_details.TryGetValue(id, out ExtensionDetailsWindow? details)) details.Update(info);
    }

    /// <summary>Makes the rows match the manager's list: updates the rows that stay, adds and removes the others, keeps the order.</summary>
    private void Synchronize()
    {
        IReadOnlyList<ExtensionInfo> list = _host.Extensions;
        var ids = new HashSet<string>(list.Select(info => info.Id), StringComparer.Ordinal);
        for (int index = Rows.Count - 1; index >= 0; index--)
            if (!ids.Contains(Rows[index].Id)) Rows.RemoveAt(index);
        for (int index = 0; index < list.Count; index++)
        {
            ExtensionInfo info = list[index];
            ExtensionRow? row = RowOf(info.Id);
            if (row is null)
            {
                row = new ExtensionRow(info.Id, this);
                Rows.Insert(index, row);
            }
            else
            {
                int current = Rows.IndexOf(row);
                if (current != index) Rows.Move(current, index);
            }
            row.Update(info, _host.CanRemove(info));
        }
        foreach (string id in _details.Keys.ToArray())
        {
            ExtensionDetailsWindow window = _details[id];
            if (_host.Find(id) is { } info) window.Update(info);
            else window.Close();
        }
        bool enabled = _host.IsEnabled;
        DisabledBanner.IsVisible = !enabled;
        RefreshButton.IsEnabled = enabled;
        EmptyLabel.IsVisible = enabled && Rows.Count == 0;
        string? stateError = _host.StateFileError;
        StateFileBanner.IsVisible = stateError is not null;
        StateFileText.Text = stateError ?? string.Empty;
        ShowLogOfSelection();
    }

    private void ShowLogOfSelection()
    {
        var row = ExtensionList.SelectedItem as ExtensionRow;
        string? id = row?.Id;
        if (id == _logId && id is not null) return;
        _logId = id;
        LogTitle.Text = row is null ? "Log" : "Log of " + row.Name;
        LogView.Watermark = row is null ? "Select an extension to see its log" : "No output yet";
        string log = id is null ? string.Empty : _host.GetLog(id);
        LogView.Text = log;
        LogView.CaretIndex = log.Length;
    }

    private void OnLogLine(ExtensionOutputEventArgs e)
    {
        if (_logId is null || e.ExtensionId != _logId) return;
        string text = LogView.Text ?? string.Empty;
        text = text.Length == 0 ? e.Line : text + "\n" + e.Line;
        int limit = _host.LogLimit;
        if (text.Length > limit)
        {
            int cut = text.IndexOf('\n', text.Length - limit);
            text = cut < 0 ? text[^limit..] : text[(cut + 1)..];
        }
        LogView.Text = text;
        LogView.CaretIndex = text.Length;
    }
}
