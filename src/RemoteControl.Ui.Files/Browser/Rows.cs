using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Ui.Files;

/// <summary>A view model that tells bindings when a property changed.</summary>
internal abstract class Observable : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets a field and raises <see cref="PropertyChanged"/> when it changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A row of the classic file list: icon and name, rights, size, modified.</summary>
internal sealed class FileRow : Observable
{
    private IImage? _icon;
    private FileBrowserEntry _entry;

    /// <summary>Creates a row.</summary>
    /// <param name="folder">The folder of the listing.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="icon">The type icon.</param>
    public FileRow(string folder, FileBrowserEntry entry, IImage? icon)
    {
        Folder = folder;
        _entry = entry;
        _icon = icon;
    }

    /// <summary>The folder of the listing.</summary>
    public string Folder { get; }

    /// <summary>The listing entry.</summary>
    public FileBrowserEntry Entry => _entry;

    /// <summary>The name.</summary>
    public string Name => _entry.Name;

    /// <summary><c>folder/name</c>.</summary>
    public string Path => RemotePaths.Combine(Folder, Name);

    /// <summary>The rights (<c>rw</c>).</summary>
    public string Rights => _entry.Rights;

    /// <summary>Whether the rights allow writing.</summary>
    public bool Writable => _entry.Rights.Contains('w', StringComparison.OrdinalIgnoreCase);

    /// <summary>The size in bytes.</summary>
    public long Size => _entry.Size;

    /// <summary>The size as shown: blank when 0.</summary>
    public string SizeText => _entry.Size == 0 ? string.Empty : _entry.Size.ToString(CultureInfo.InvariantCulture);

    /// <summary>The time in Unix seconds.</summary>
    public long Modified => _entry.Modified;

    /// <summary>The time as shown (<c>YYYY-MM-DD HH:MM:SS</c>, local time; blank when unknown).</summary>
    public string ModifiedText => _entry.Modified <= 0 ? string.Empty : _entry.ModifiedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Whether the entry is a folder.</summary>
    public bool IsDirectory => _entry.IsDirectory;

    /// <summary>The type icon.</summary>
    public IImage? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    /// <summary>Takes the values of a newer listing of the same file.</summary>
    /// <param name="entry">The entry.</param>
    public void Update(FileBrowserEntry entry)
    {
        if (entry == _entry) return;
        _entry = entry;
        Raise(nameof(Rights));
        Raise(nameof(SizeText));
        Raise(nameof(ModifiedText));
    }
}

/// <summary>An item of the modern icon grid: a folder or a file.</summary>
internal sealed class ModernItem : Observable
{
    private IImage? _image;
    private double _imageSize;

    /// <summary>Creates an item.</summary>
    public ModernItem(string name, string path, bool isFolder, string rights, IImage? image, double imageSize, FileRow? row)
    {
        Name = name;
        Path = path;
        IsFolder = isFolder;
        Rights = rights;
        _image = image;
        _imageSize = imageSize;
        Row = row;
    }

    /// <summary>The name shown.</summary>
    public string Name { get; }

    /// <summary>The folder (ending in <c>/</c>) or the file path.</summary>
    public string Path { get; }

    /// <summary>Whether it is a folder.</summary>
    public bool IsFolder { get; }

    /// <summary>The rights.</summary>
    public string Rights { get; }

    /// <summary>The file's row, or null for a folder.</summary>
    public FileRow? Row { get; }

    /// <summary>The picture: the folder icon, the type icon or the thumbnail.</summary>
    public IImage? Image
    {
        get => _image;
        set => Set(ref _image, value);
    }

    /// <summary>The picture's size (48 for folders, 44 for type icons, 88 for thumbnails).</summary>
    public double ImageSize
    {
        get => _imageSize;
        set => Set(ref _imageSize, value);
    }

    /// <summary>Whether a geometry folder icon is shown instead of <see cref="Image"/>.</summary>
    public bool ShowFolderIcon => IsFolder;

    /// <summary>Whether <see cref="Image"/> is shown.</summary>
    public bool ShowImage => !IsFolder;
}

/// <summary>A node of the folder tree (<c>addFolder</c>): label <c>part/</c>, the rule's rights on the leaf.</summary>
internal sealed class FolderNode : Observable
{
    private bool _isExpanded;
    private bool _isCurrent;

    /// <summary>Creates a node.</summary>
    /// <param name="part">The last part of the path.</param>
    /// <param name="path">The path without trailing <c>/</c>.</param>
    public FolderNode(string part, string path)
    {
        Part = part;
        FullPath = path;
    }

    /// <summary>The last part.</summary>
    public string Part { get; }

    /// <summary>The label (<c>part/</c>).</summary>
    public string Label => Part + "/";

    /// <summary>The path without trailing <c>/</c>.</summary>
    public string FullPath { get; }

    /// <summary>The folder as navigated to (with trailing <c>/</c>).</summary>
    public string Folder => FullPath + "/";

    /// <summary>The rights of the rule that ends here, or empty.</summary>
    public string Rights { get; set; } = string.Empty;

    /// <summary>The subfolders, sorted by label.</summary>
    public ObservableCollection<FolderNode> Children { get; } = new();

    /// <summary>Whether it is expanded.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value)) Raise(nameof(IsOpen));
        }
    }

    /// <summary>Whether it is on the path of the current folder.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (Set(ref _isCurrent, value)) Raise(nameof(IsOpen));
        }
    }

    /// <summary>Whether the open-folder icon is shown (expanded or on the current path).</summary>
    public bool IsOpen => _isExpanded || _isCurrent;

    /// <summary>The node and its descendants.</summary>
    /// <returns>Them, parents first.</returns>
    public IEnumerable<FolderNode> SelfAndDescendants()
    {
        yield return this;
        foreach (FolderNode child in Children)
            foreach (FolderNode node in child.SelfAndDescendants()) yield return node;
    }
}

/// <summary>Builds the folder tree from the folder rules (<c>refreshFolders</c>, <c>addFolder</c>).</summary>
internal static class FolderTree
{
    /// <summary>The root nodes for the rules: each pattern cut at the first <c>*</c>, split on <c>/</c>, sorted by label.</summary>
    /// <param name="folders">The folder rules.</param>
    /// <returns>The roots.</returns>
    public static List<FolderNode> Build(IEnumerable<FileBrowserFolder> folders)
    {
        var roots = new List<FolderNode>();
        foreach (FileBrowserFolder rule in folders)
        {
            string path = RemotePaths.FolderOfRule(rule.Pattern);
            if (path.Length == 0) continue;
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            IList<FolderNode> level = roots;
            string full = string.Empty;
            for (int index = 0; index < parts.Length; index++)
            {
                full = full.Length == 0 ? parts[index] : full + "/" + parts[index];
                FolderNode? node = level.FirstOrDefault(item => item.FullPath == full);
                if (node is null)
                {
                    node = new FolderNode(parts[index], full);
                    if (index == parts.Length - 1) node.Rights = rule.Rights;
                    Insert(level, node);
                }
                else if (index == parts.Length - 1 && node.Rights.Length == 0)
                {
                    node.Rights = rule.Rights;
                }

                level = node.Children;
            }
        }

        return roots;
    }

    private static void Insert(IList<FolderNode> level, FolderNode node)
    {
        int index = 0;
        while (index < level.Count && string.Compare(level[index].Label, node.Label, StringComparison.Ordinal) < 0) index++;
        level.Insert(index, node);
    }
}
