using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using AvaloniaEdit.Search;
using RemoteControl.Core;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Session;

/// <summary>What a chat tab shows.</summary>
internal enum ChatTabKind
{
    /// <summary>The RC chat (first, never closed).</summary>
    Main,

    /// <summary>The NC chat (<c>separatenc</c>).</summary>
    Nc,

    /// <summary>An IRC channel.</summary>
    Channel,

    /// <summary>An extension's output.</summary>
    Extension,

    /// <summary>The results of <c>/find</c>.</summary>
    FindResults,
}

/// <summary>One chat tab.</summary>
internal sealed class ChatTab
{
    /// <summary>Creates a tab.</summary>
    public ChatTab(string name, ChatTabKind kind, TabItem item, ChatView? view, ObservableCollection<FindResultLine>? results)
    {
        Name = name;
        Kind = kind;
        Item = item;
        View = view;
        Results = results;
    }

    /// <summary>The tab's name (its header).</summary>
    public string Name { get; }

    /// <summary>What it shows.</summary>
    public ChatTabKind Kind { get; }

    /// <summary>The tab.</summary>
    public TabItem Item { get; }

    /// <summary>The chat view (every kind but find results).</summary>
    public ChatView? View { get; }

    /// <summary>The lines of a find-results tab.</summary>
    public ObservableCollection<FindResultLine>? Results { get; }

    /// <summary>The find bar of the view, once opened.</summary>
    public SearchPanel? Search { get; set; }
}

internal sealed partial class MainWindow
{
    /// <summary>The name of the RC chat tab.</summary>
    public const string MainTabName = "RC Chat";

    /// <summary>The name of the NC tab.</summary>
    public const string NcTabName = "NC";

    /// <summary>The name of a find-results tab.</summary>
    public const string FindTabName = "Find Results";

    private readonly List<ChatTab> _tabs = [];
    private readonly FindResultsParser _find = new();
    private ChatTab _main = null!;
    private ChatTab? _findTab;

    /// <summary>The RC chat (tests).</summary>
    internal ChatView MainChat => _main.View!;

    /// <summary>The tab of a name, or null (tests).</summary>
    internal ChatTab? TabNamed(string name) => _tabs.LastOrDefault(tab => tab.Name == name);

    /// <summary>The tabs (tests).</summary>
    internal IReadOnlyList<ChatTab> ChatTabList => _tabs;

    private void InitializeChat()
    {
        _main = AddTab(MainTabName, ChatTabKind.Main, closable: false);
        _main.View!.AlwaysScrollToEnd = true;
        ChatTabs.SelectedIndex = 0;
    }

    private ChatView NewChatView()
    {
        var view = new ChatView { Services = _context.App };
        view.LinkClicked += UiSafe.Handler<ChatLinkEventArgs>((_, e) => _context.App.Urls.Open(e.Url), "MainWindow.Link");
        return view;
    }

    private ChatTab AddTab(string name, ChatTabKind kind, bool closable, bool select = false)
    {
        var item = new TabItem { Header = name, Name = "Tab" + kind };
        ChatView? view = null;
        ObservableCollection<FindResultLine>? results = null;
        if (kind == ChatTabKind.FindResults)
        {
            results = [];
            var list = new ListBox
            {
                Name = "FindResultsList",
                ItemsSource = results,
                Background = null,
                ItemTemplate = new FuncDataTemplate<FindResultLine>((line, _) => BuildFindRow(line), supportsRecycling: false),
            };
            list.Bind(BackgroundProperty, list.GetResourceObservable(ThemeKeys.ChatBackBrush));
            item.Content = list;
        }
        else
        {
            view = NewChatView();
            item.Content = view;
        }
        var tab = new ChatTab(name, kind, item, view, results);
        if (closable)
        {
            var close = new MenuItem { Header = "Close", Name = "CloseTab" };
            close.Click += UiSafe.Handler<Avalonia.Interactivity.RoutedEventArgs>((_, _) => CloseTab(tab, part: true), "MainWindow.CloseTab");
            item.ContextMenu = new ContextMenu { ItemsSource = new[] { close } };
        }
        _tabs.Add(tab);
        object? selected = ChatTabs.SelectedItem;
        ChatTabs.Items.Add(item);
        if (select) ChatTabs.SelectedItem = item;
        else if (selected is not null) ChatTabs.SelectedItem = selected;
        return tab;
    }

    private Control BuildFindRow(FindResultLine? line)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        if (line is null) return panel;
        TextBlock Text(string value)
        {
            var block = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center };
            block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(ThemeKeys.ChatBrush));
            block.Bind(TextBlock.FontFamilyProperty, block.GetResourceObservable(ThemeKeys.ChatFontFamily));
            block.Bind(TextBlock.FontSizeProperty, block.GetResourceObservable(ThemeKeys.ChatFontSize));
            return block;
        }
        if (line.Prefix.Length > 0) panel.Children.Add(Text(line.Prefix));
        if (line.HasLink)
        {
            var link = new Button { Classes = { "link" }, Content = new TextBlock { Text = line.File, TextDecorations = Avalonia.Media.TextDecorations.Underline }, Name = "FindLink" };
            string folder = line.Folder;
            link.Click += UiSafe.Handler<Avalonia.Interactivity.RoutedEventArgs>((_, _) => OpenFindFolder(folder), "MainWindow.FindLink");
            panel.Children.Add(link);
            panel.Children.Add(Text(line.Rest));
        }
        return panel;
    }

    /// <summary>Opens the file browser at the folder of a find result (<c>onFindResultClick</c>).</summary>
    internal void OpenFindFolder(string folder)
    {
        if (!_context.Session.IsConnected || IsFileSyncActive) return;
        _context.OpenTool(ToolIds.FileBrowser, folder);
    }

    private ChatTab? CurrentTab => _tabs.FirstOrDefault(tab => ReferenceEquals(tab.Item, ChatTabs.SelectedItem));

    private void CloseTab(string name, bool part)
    {
        ChatTab? tab = TabNamed(name);
        if (tab is not null) CloseTab(tab, part);
        else if (part) _controller.LeaveChannel(name, part: true);
    }

    private void CloseTab(ChatTab tab, bool part)
    {
        if (tab.Kind == ChatTabKind.Main) return;
        if (tab.Kind == ChatTabKind.Channel) _controller.LeaveChannel(tab.Name, part);
        if (ReferenceEquals(tab, _findTab))
        {
            _findTab = null;
            _find.Reset();
        }
        bool wasSelected = ReferenceEquals(ChatTabs.SelectedItem, tab.Item);
        _tabs.Remove(tab);
        ChatTabs.Items.Remove(tab.Item);
        if (wasSelected) ChatTabs.SelectedIndex = 0;
    }

    private void SetNcTabVisible(bool visible)
    {
        ChatTab? nc = _tabs.FirstOrDefault(tab => tab.Kind == ChatTabKind.Nc);
        if (visible && nc is null) AddTab(NcTabName, ChatTabKind.Nc, closable: false);
        else if (!visible && nc is not null) CloseTab(nc, part: false);
    }

    // ---- Appending ---------------------------------------------------------------------------------------------------------

    /// <summary>A line of the server for the RC chat: engine texts are hidden, find results go to their tab.</summary>
    private void OnServerChat(string text)
    {
        if (SessionText.IsInternalProtocolText(text)) return;
        if (_context.App.Options.Current.SeparateFindResults && TryFindResult(text)) return;
        AppendMain(text);
    }

    private void OnNcChat(string text)
    {
        if (SessionText.IsInternalProtocolText(text)) return;
        ChatTab? nc = _tabs.FirstOrDefault(tab => tab.Kind == ChatTabKind.Nc);
        if (nc is not null) Append(nc, text, null);
        else AppendMain(text);
    }

    /// <summary>An IRC line: <c>&lt;nick&gt; text</c> is shown as <c>&lt;nick&gt;: text</c>; <c>* Left &lt;channel&gt;</c> closes the tab.</summary>
    private void OnIrcMessage(string channel, string line)
    {
        if (string.IsNullOrEmpty(channel))
        {
            AppendMain(line);
            return;
        }
        if (line == "* Left " + channel)
        {
            CloseTab(channel, part: false);
            _controller.LeaveChannel(channel, part: false);
            return;
        }
        _controller.NoteChannelJoined(channel);
        int close = line.IndexOf("> ", StringComparison.Ordinal);
        string display = line.StartsWith('<') && close >= 0 ? line.Insert(close + 1, ":") : line;
        AppendToChannel(channel, display, ChatTabKind.Channel);
    }

    private void AppendToChannel(string channel, string text, ChatTabKind kind)
    {
        ChatTab tab = TabNamed(channel) ?? AddTab(channel, kind, closable: true);
        if (text.Length > 0) Append(tab, text, null);
    }

    /// <summary>Appends a line to the RC chat, asks for attention for an alert and writes the chat log.</summary>
    internal void AppendMain(string text, ChatAppendOptions? options = null, bool attention = true)
    {
        if (SessionText.IsInternalProtocolText(text)) return;
        Append(_main, text, options, attention);
        ChatLog.Append(_context.App.Paths, _context.App.Options.Current, text, _context.App.Log);
    }

    private void Append(ChatTab tab, string text, ChatAppendOptions? options, bool attention = true)
    {
        if (tab.View is null) return;
        ChatAppendResult result = tab.View.Append(text, options);
        if (!attention) return;
        if (result.Line.Alert == ChatAlertKind.Sound) _context.Shell.RequestAttention(_context, AttentionKind.AlertWithSound);
        else if (result.WantsUrgency) _context.Shell.RequestAttention(_context, AttentionKind.Alert);
    }

    private void AppendHelp(string text) => AppendMain(text, new ChatAppendOptions { SuppressEmotes = true }, attention: false);

    // ---- Find results ------------------------------------------------------------------------------------------------------

    private void BeginFindResults()
    {
        _findTab = AddTab(FindTabName, ChatTabKind.FindResults, closable: true, select: true);
        _find.Begin(string.Empty);
    }

    private bool TryFindResult(string text)
    {
        if (!_find.TryParse(text, out FindResultLine? line, out bool header) || line is null) return false;
        if (header && _findTab is null) _findTab = AddTab(FindTabName, ChatTabKind.FindResults, closable: true, select: true);
        if (_findTab?.Results is null) return false;
        _findTab.Results.Add(line);
        while (_findTab.Results.Count > ChatView.DefaultMaximumLines) _findTab.Results.RemoveAt(0);
        return true;
    }

    // ---- Find bar ----------------------------------------------------------------------------------------------------------

    private void OpenFind()
    {
        ChatTab? tab = CurrentTab;
        if (tab?.View is null) return;
        tab.Search ??= SearchPanel.Install(tab.View.Editor);
        tab.Search.Open();
    }

    private void ClearTab(ChatTab tab)
    {
        tab.View?.Clear();
        tab.Results?.Clear();
    }
}
