using System.Collections.ObjectModel;
using RemoteControl.Sync;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Files;

/// <summary>A folder of the server-folder picker; its subfolders are listed when it is first expanded.</summary>
internal sealed class PickerNode : Observable
{
    private readonly Action<PickerNode>? _expand;
    private bool _isChecked;
    private bool _isExpanded;
    private bool _loaded;

    /// <summary>Creates a node.</summary>
    public PickerNode(string path, string name, bool isChecked, Action<PickerNode>? expand, bool placeholder = false)
    {
        Path = path;
        Name = name;
        _isChecked = isChecked;
        _expand = expand;
        IsPlaceholder = placeholder;
        if (!placeholder) Children.Add(new PickerNode(path + "/…", "Loading...", false, null, placeholder: true));
    }

    /// <summary>The server folder (no trailing <c>/</c>).</summary>
    public string Path { get; }

    /// <summary>The name shown.</summary>
    public string Name { get; }

    /// <summary>Whether it is the "Loading..." row.</summary>
    public bool IsPlaceholder { get; }

    /// <summary>The subfolders.</summary>
    public ObservableCollection<PickerNode> Children { get; } = new();

    /// <summary>Whether it is chosen.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value)) CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether it is expanded; the first time it lists its subfolders.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value) || !value || _loaded || IsPlaceholder) return;
            _loaded = true;
            _expand?.Invoke(this);
        }
    }

    /// <summary>Raised when <see cref="IsChecked"/> changed.</summary>
    public event EventHandler? CheckedChanged;
}

/// <summary>
/// "Select server folders" (<c>onChooseFileFolders</c>, 430 by 380, not modal): the server's file roots from the engine, expanded on
/// demand, with a check box per folder; Apply stores the chosen folders as the "Remote file folders" setting.
/// </summary>
internal sealed partial class FolderPickerWindow : RcWindow
{
    private readonly FilesSession? _owner;
    private readonly HashSet<string> _chosen;

    /// <summary>For the designer.</summary>
    public FolderPickerWindow() : this(null)
    {
    }

    /// <summary>Creates the picker.</summary>
    /// <param name="owner">The session's files part.</param>
    public FolderPickerWindow(FilesSession? owner) : base(owner?.Context.App)
    {
        InitializeComponent();
        _owner = owner;
        _chosen = new HashSet<string>(SyncNames.SplitFolders(owner?.Engine?.Settings.FileFolders), StringComparer.Ordinal);
        FolderTree.ItemsSource = Roots;
        CloseButton.Click += On.Click(() => Close(), "FolderPicker.Close");
        ApplyButton.Click += On.Click(() => Apply(), "FolderPicker.Apply");
        if (owner is null) return;
        UiSafe.Fire(LoadRootsAsync, "FolderPicker.Roots");
    }

    /// <summary>The roots.</summary>
    internal ObservableCollection<PickerNode> Roots { get; } = new();

    /// <summary>The folders chosen now.</summary>
    internal IReadOnlyCollection<string> Chosen => _chosen;

    private PickerNode Node(string path)
    {
        string name = SyncNames.NameOf(path);
        var node = new PickerNode(path, name.Length == 0 ? path : name + "/", _chosen.Contains(path), expand => UiSafe.Fire(() => LoadChildrenAsync(expand), "FolderPicker.Expand"));
        node.CheckedChanged += (_, _) =>
        {
            if (node.IsChecked) _chosen.Add(node.Path);
            else _chosen.Remove(node.Path);
            ApplyButton.IsEnabled = true;
        };
        return node;
    }

    private async Task LoadRootsAsync()
    {
        StateText.Text = "Requesting selectable server file roots";
        SyncEngine? engine = _owner?.Engine;
        IReadOnlyList<string>? roots = engine is null ? null : await engine.RequestFileRootsAsync(ClosedToken).ConfigureAwait(true);
        if (IsClosed) return;
        if (roots is null)
        {
            StateText.Text = "File folders unavailable: the server did not accept the file-root request.";
            return;
        }

        if (roots.Count == 0)
        {
            StateText.Text = "No usable server file roots were advertised.";
            return;
        }

        StateText.Text = string.Empty;
        StateText.IsVisible = false;
        foreach (string root in roots) Roots.Add(Node(root));
    }

    private async Task LoadChildrenAsync(PickerNode node)
    {
        SyncEngine? engine = _owner?.Engine;
        IReadOnlyList<string>? folders = engine is null ? null : await engine.ListSubfoldersAsync(node.Path, ClosedToken).ConfigureAwait(true);
        if (IsClosed) return;
        node.Children.Clear();
        if (folders is null) return;
        foreach (string folder in folders) node.Children.Add(Node(folder));
    }

    /// <summary>Stores the chosen folders and closes.</summary>
    internal void Apply()
    {
        string value = SyncNames.JoinFolders(_chosen.OrderBy(path => path, StringComparer.Ordinal));
        _owner?.Engine?.UpdateSettings(settings => settings with { FileFolders = value });
        Close();
    }
}
