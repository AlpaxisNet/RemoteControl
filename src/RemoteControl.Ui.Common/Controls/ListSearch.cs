using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace RemoteControl.Ui.Common;

/// <summary>
/// Type-to-search for lists and trees (<c>TTreeSearch.h</c>: GTK's interactive search with a case-insensitive "contains" match).
/// Typing while a <see cref="ListBox"/>, <see cref="TreeView"/> or other <see cref="SelectingItemsControl"/> has
/// the focus selects the first item, from the selected one on and wrapping around, whose text contains what was typed (case ignored).
/// Typed characters add up; after <see cref="ResetDelay"/> without typing, or Escape, the search starts again. Backspace removes the
/// last character. Text typed into a text box inside an item is left alone. For a tree, the expanded (visible) items are searched.
/// </summary>
/// <example>
/// <code>
/// Track(ListSearch.Attach(playerList, item =&gt; ((PlayerRow)item).Account));   // in a RcWindow
/// Track(ListSearch.Attach(fileTree));                                           // item.ToString()
/// </code>
/// </example>
public sealed class ListSearch : IDisposable
{
    private readonly SelectingItemsControl? _list;
    private readonly TreeView? _tree;
    private readonly Control _control;
    private readonly Func<object, string?> _textOf;
    private readonly DispatcherTimer _reset;
    private bool _disposed;

    private ListSearch(Control control, Func<object, string?>? textOf)
    {
        _control = control;
        _list = control as SelectingItemsControl;
        _tree = control as TreeView;
        _textOf = textOf ?? (item => item is ContentControl { Content: { } content } ? content.ToString() : item?.ToString());
        _reset = new DispatcherTimer { Interval = ResetDelay };
        _reset.Tick += (_, _) => { _reset.Stop(); Query = string.Empty; };
        control.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Bubble);
        control.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>How long a pause starts a new search (one second, close to GTK's).</summary>
    public static TimeSpan ResetDelay { get; } = TimeSpan.FromSeconds(1);

    /// <summary>What was typed so far.</summary>
    public string Query { get; private set; } = string.Empty;

    /// <summary>Adds type-to-search to a list (any <see cref="SelectingItemsControl"/>) or a <see cref="TreeView"/>.</summary>
    /// <param name="control">The list or tree.</param>
    /// <param name="textOf">The text of an item; by default a content control's content or the item's <c>ToString()</c>.</param>
    /// <returns>The behaviour; dispose it to remove it.</returns>
    public static ListSearch Attach(Control control, Func<object, string?>? textOf = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (control is not (SelectingItemsControl or TreeView)) throw new ArgumentException("Type-to-search needs a list or a tree.", nameof(control));
        return new ListSearch(control, textOf);
    }

    /// <summary>Whether <paramref name="candidate"/> contains <paramref name="query"/>, case ignored (the C++ match).</summary>
    /// <param name="candidate">The item text.</param>
    /// <param name="query">What was typed.</param>
    /// <returns><c>true</c> on a match; an empty query matches everything.</returns>
    public static bool Matches(string? candidate, string? query) =>
        string.IsNullOrEmpty(query) || (candidate ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>The index of the first text, from <paramref name="start"/> on and wrapping around, that contains <paramref name="query"/>.</summary>
    /// <param name="texts">The item texts in order.</param>
    /// <param name="start">Where to begin (the selected item; it is tried first).</param>
    /// <param name="query">What was typed.</param>
    /// <returns>The index, or -1.</returns>
    public static int FindNext(IReadOnlyList<string?> texts, int start, string query)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0 || string.IsNullOrEmpty(query)) return -1;
        int first = Math.Clamp(start, 0, texts.Count - 1);
        for (int step = 0; step < texts.Count; step++)
        {
            int index = (first + step) % texts.Count;
            if (Matches(texts[index], query)) return index;
        }
        return -1;
    }

    /// <summary>Searches for <paramref name="text"/> as if it had been typed after what was typed so far.</summary>
    /// <param name="text">The typed text.</param>
    /// <returns>Whether an item matched and was selected.</returns>
    public bool Type(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text)) return false;
        Query += text;
        _reset.Stop();
        _reset.Start();
        return Search();
    }

    /// <summary>Removes the behaviour.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reset.Stop();
        _control.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        _control.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
    }

    private bool Search()
    {
        if (_tree is not null) return SearchTree();
        if (_list is null) return false;
        var items = _list.Items.Cast<object?>().ToList();
        var texts = items.Select(item => item is null ? null : _textOf(item)).ToList();
        int index = FindNext(texts, Math.Max(0, _list.SelectedIndex), Query);
        if (index < 0) return false;
        _list.SelectedIndex = index;
        if (_list is ListBox listBox && items[index] is { } item) listBox.ScrollIntoView(item);
        return true;
    }

    private bool SearchTree()
    {
        TreeView tree = _tree!;
        var visible = new List<(TreeViewItem Container, object Item)>();
        foreach (TreeViewItem container in tree.GetVisualDescendants().OfType<TreeViewItem>())
        {
            if (!container.IsEffectivelyVisible) continue;
            bool shown = true;
            for (Visual? parent = container.GetVisualParent(); parent is not null && !ReferenceEquals(parent, tree); parent = parent.GetVisualParent())
                if (parent is TreeViewItem { IsExpanded: false }) { shown = false; break; }
            if (!shown) continue;
            object? item = tree.TreeItemFromContainer(container) ?? container.DataContext ?? container;
            visible.Add((container, item));
        }
        if (visible.Count == 0) return false;
        int start = 0;
        for (int index = 0; index < visible.Count; index++)
            if (ReferenceEquals(visible[index].Item, tree.SelectedItem)) { start = index; break; }
        var texts = visible.Select(entry => _textOf(entry.Item is TreeViewItem own && ReferenceEquals(own, entry.Container) ? (own.Header ?? own) : entry.Item)).ToList();
        int found = FindNext(texts, start, Query);
        if (found < 0) return false;
        tree.SelectedItem = visible[found].Item;
        visible[found].Container.BringIntoView();
        return true;
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || string.IsNullOrEmpty(e.Text) || e.Source is TextBox || char.IsControl(e.Text[0])) return;
        if (Type(e.Text)) e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox || Query.Length == 0) return;
        if (e.Key == Key.Escape)
        {
            Query = string.Empty;
            _reset.Stop();
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            Query = Query[..^1];
            _reset.Stop();
            if (Query.Length > 0)
            {
                _reset.Start();
                Search();
            }
            e.Handled = true;
        }
    }
}
