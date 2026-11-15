using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Session;

internal sealed partial class MainWindow
{
    /// <summary>How many lines the history keeps.</summary>
    public const int HistoryLimit = 30;

    /// <summary>How many rows the completion list shows at most.</summary>
    public const int CompletionLimit = 60;

    /// <summary>The emoji the "Insert Emoji" menu offers.</summary>
    internal static readonly string[] Emoji =
    [
        "😀", "😁", "😂", "🤣", "😊", "😉", "😍", "😘", "😎", "🤔", "😐", "😴", "😢", "😭", "😡", "😱",
        "👍", "👎", "👏", "🙏", "👋", "💪", "🎉", "🔥", "❤", "💔", "⭐", "✅", "❌", "⚠", "💤", "🍕",
    ];

    /// <summary>The commands that get your own account when typed without one (<c>send</c>, <c>TRemoteFrame.cpp:2750</c>).</summary>
    private static readonly string[] s_accountCommands = ["/open", "/openacc", "/opencomments", "/openrights", "/openaccess", "/openprofile", "/openban", "/reset"];

    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private string? _cycleBase;
    private int _cyclePosition = -1;
    private bool _applying;
    private List<CompletionItem> _completions = [];

    /// <summary>The history, newest first (tests).</summary>
    internal IReadOnlyList<string> History => _history;

    /// <summary>The rows of the completion list (tests).</summary>
    internal IReadOnlyList<CompletionItem> CompletionRows => _completions;

    /// <summary>Whether the completion list is shown (tests).</summary>
    internal bool IsCompletionOpen => CompletionBorder.IsVisible;

    /// <summary>The last /scripthelp2 search (tests).</summary>
    internal Task? PendingScriptHelp { get; private set; }

    private void InitializeEntry()
    {
        ChatEntry.AddHandler(KeyDownEvent, OnEntryKeyDown, RoutingStrategies.Tunnel);
        ChatEntry.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) UiSafe.Run(OnEntryTextChanged, "MainWindow.EntryText");
        };
        ChatEntry.LostFocus += (_, _) => CloseCompletion();
        CompletionList.ItemTemplate = new FuncDataTemplate<CompletionItem>((item, _) => BuildCompletionRow(item), supportsRecycling: false);
        CompletionList.AddHandler(PointerReleasedEvent, (_, _) => UiSafe.Run(AcceptCompletion, "MainWindow.CompletionClick"), RoutingStrategies.Bubble, handledEventsToo: true);
        ChatEntry.ContextMenu = BuildEntryMenu();
    }

    private ContextMenu BuildEntryMenu()
    {
        MenuItem Item(string header, string name, Action action)
        {
            var item = new MenuItem { Header = header, Name = name };
            item.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => action(), "MainWindow." + name);
            return item;
        }
        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                Item("Cut", "EntryCut", ChatEntry.Cut),
                Item("Copy", "EntryCopy", ChatEntry.Copy),
                Item("Paste", "EntryPaste", ChatEntry.Paste),
                Item("Select All", "EntrySelectAll", ChatEntry.SelectAll),
                new Separator(),
                Item("Insert Emoji", "EntryEmoji", ShowEmojiFlyout),
            },
        };
    }

    /// <summary>The panel of the last emoji chooser (tests).</summary>
    internal WrapPanel? EmojiPanel { get; private set; }

    /// <summary>The emoji chooser of the entry's "Insert Emoji" (the GTK chooser).</summary>
    internal void ShowEmojiFlyout()
    {
        var panel = new WrapPanel { Width = 8 * 34, Name = "EmojiPanel" };
        EmojiPanel = panel;
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.TopEdgeAlignedLeft };
        foreach (string emoji in Emoji)
        {
            var button = new Button { Content = emoji, Width = 32, Height = 32, Padding = new Thickness(0), FontSize = 16, HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Click += UiSafe.Handler<RoutedEventArgs>((_, _) =>
            {
                InsertAtCaret(emoji);
                flyout.Hide();
            }, "MainWindow.Emoji");
            panel.Children.Add(button);
        }
        flyout.ShowAt(ChatEntry);
    }

    /// <summary>Puts a text at the entry's cursor.</summary>
    internal void InsertAtCaret(string text)
    {
        string current = ChatEntry.Text ?? string.Empty;
        int caret = Math.Clamp(ChatEntry.CaretIndex, 0, current.Length);
        ChatEntry.Text = current.Insert(caret, text);
        ChatEntry.CaretIndex = caret + text.Length;
        ChatEntry.Focus();
    }

    // ---- Keys ----------------------------------------------------------------------------------------------------------------

    private void OnEntryKeyDown(object? sender, KeyEventArgs e)
    {
        UiSafe.Run(() =>
        {
            switch (e.Key)
            {
                case Key.Tab:
                    e.Handled = CycleCompletion(!e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                    break;
                case Key.Up:
                    e.Handled = true;
                    if (IsCompletionOpen) MoveCompletion(-1);
                    else HistoryUp();
                    break;
                case Key.Down:
                    e.Handled = true;
                    if (IsCompletionOpen) MoveCompletion(1);
                    else HistoryDown();
                    break;
                case Key.Escape when IsCompletionOpen:
                    e.Handled = true;
                    CloseCompletion();
                    break;
                case Key.Enter:
                    e.Handled = true;
                    if (IsCompletionOpen && CompletionList.SelectedItem is not null) AcceptCompletion();
                    else SendEntry();
                    break;
            }
        }, "MainWindow.EntryKey");
    }

    private void HistoryUp()
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Min(_historyIndex + 1, _history.Count - 1);
        SetEntry(_history[_historyIndex]);
    }

    private void HistoryDown()
    {
        if (_historyIndex < 0) return;
        _historyIndex--;
        SetEntry(_historyIndex < 0 ? string.Empty : _history[_historyIndex]);
    }

    private void SetEntry(string text, int? caret = null)
    {
        _applying = true;
        try
        {
            ChatEntry.Text = text;
            ChatEntry.CaretIndex = caret ?? text.Length;
        }
        finally
        {
            _applying = false;
        }
    }

    // ---- Completion ----------------------------------------------------------------------------------------------------------

    private IReadOnlyList<CompletionItem> PlayerRows() => ChatCompletion.PlayerRows(_context.Session.Players, _context.Account);

    private void OnEntryTextChanged()
    {
        if (_applying) return;
        _cycleBase = null;
        _cyclePosition = -1;
        string text = ChatEntry.Text ?? string.Empty;
        int caret = ChatEntry.CaretIndex;
        if (caret < text.Length && caret == 0) caret = text.Length;
        ShowCompletion(ChatCompletion.Match(text, Math.Clamp(caret, 0, text.Length), PlayerRows()));
    }

    private void ShowCompletion(IReadOnlyList<CompletionItem> rows)
    {
        _completions = rows.Take(CompletionLimit).ToList();
        CompletionList.ItemsSource = _completions;
        CompletionList.SelectedIndex = -1;
        CompletionBorder.IsVisible = _completions.Count > 0;
    }

    private void CloseCompletion()
    {
        CompletionBorder.IsVisible = false;
        CompletionList.SelectedIndex = -1;
    }

    private void MoveCompletion(int step)
    {
        int count = _completions.Count;
        if (count == 0) return;
        int index = CompletionList.SelectedIndex;
        index = index < 0 ? (step > 0 ? 0 : count - 1) : (index + step + count) % count;
        CompletionList.SelectedIndex = index;
        CompletionList.ScrollIntoView(index);
    }

    private void AcceptCompletion()
    {
        if (CompletionList.SelectedItem is not CompletionItem item) return;
        string text = ChatEntry.Text ?? string.Empty;
        (string next, int caret) = ChatCompletion.Apply(text, Math.Clamp(ChatEntry.CaretIndex, 0, text.Length), item);
        CloseCompletion();
        SetEntry(next, caret);
        _cycleBase = null;
        ChatEntry.Focus();
    }

    /// <summary>Tab and Shift+Tab: puts the next (previous) match for the word at the cursor in place (<c>onEditKey</c>, <c>TRemoteFrame.cpp:1270-1296</c>).</summary>
    internal bool CycleCompletion(bool forward)
    {
        string current = ChatEntry.Text ?? string.Empty;
        string baseText = _cycleBase ?? current;
        if (baseText.Length == 0) return false;
        IReadOnlyList<CompletionItem> matches = ChatCompletion.Match(baseText, baseText.Length, PlayerRows());
        if (matches.Count == 0) return false;
        _cycleBase = baseText;
        _cyclePosition = forward ? (_cyclePosition + 1) % matches.Count : (_cyclePosition <= 0 ? matches.Count - 1 : _cyclePosition - 1);
        (string next, int caret) = ChatCompletion.Apply(baseText, baseText.Length, matches[_cyclePosition]);
        CloseCompletion();
        SetEntry(next, caret);
        return true;
    }

    private Control BuildCompletionRow(CompletionItem? item)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (item is null) return row;
        var circle = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        circle.Children.Add(new Ellipse { Fill = new SolidColorBrush(CategoryColor(item.Category)) });
        circle.Children.Add(new TextBlock { Text = item.Glyph, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(circle);
        row.Children.Add(Themed(new TextBlock { Text = item.Name, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }, ThemeKeys.TextBrush));
        foreach (string chip in item.ParameterChips)
        {
            var border = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center, Child = Themed(new TextBlock { Text = chip, FontSize = 11 }, ThemeKeys.AccentBrush) };
            border.Bind(Border.BackgroundProperty, border.GetResourceObservable(ThemeKeys.BackgroundBrush));
            row.Children.Add(border);
        }
        if (item.Detail.Length > 0) row.Children.Add(Themed(new TextBlock { Text = item.Detail, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }, ThemeKeys.DisabledTextBrush));
        return row;
    }

    private static TextBlock Themed(TextBlock block, string brush)
    {
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(brush));
        return block;
    }

    /// <summary>The circle colour of a row (<c>commandCompletionIcon</c>, theme-dependent accents, <c>TRemoteFrame.cpp:274-299</c>).</summary>
    internal Color CategoryColor(string category)
    {
        string theme = ThemeManager.Normalize(_context.App.Options.Current.Theme);
        (string player, string security, string text, string info, string network, string danger) = theme switch
        {
            "nord" => ("#5e81ac", "#bf616a", "#b48ead", "#d08770", "#88c0d0", "#bf616a"),
            "dracula" => ("#6272a4", "#ff5555", "#bd93f9", "#ffb86c", "#8be9fd", "#ff5555"),
            "gruvbox" => ("#458588", "#cc241d", "#b16286", "#d79921", "#689d6a", "#fb4934"),
            "catppuccin" => ("#89b4fa", "#f38ba8", "#cba6f7", "#fab387", "#89dceb", "#eba0ac"),
            "solarized" => ("#268bd2", "#dc322f", "#6c71c4", "#cb4b16", "#2aa198", "#dc322f"),
            _ => ("#4c78a8", "#b94e63", "#8f6bb3", "#d08a52", "#4c9bb5", "#c45b5b"),
        };
        string value = category switch
        {
            "player" => player,
            "security" => security,
            "text" => text,
            "network" => network,
            "danger" => danger,
            _ => info,
        };
        return ColorParser.Parse(value, Colors.Gray);
    }

    // ---- Sending -------------------------------------------------------------------------------------------------------------

    private void SendEntry()
    {
        string text = (ChatEntry.Text ?? string.Empty).Trim();
        if (text.Length == 0) return;
        if (Execute(text))
        {
            SetEntry(string.Empty);
            CloseCompletion();
        }
    }

    /// <summary>
    /// What Enter does with a line (<c>send</c>, <c>TRemoteFrame.cpp:2666-2754</c>): the program's own commands, a line for the selected
    /// IRC channel, or a command or chat line for the server.
    /// </summary>
    /// <param name="message">The trimmed line.</param>
    /// <returns>Whether it was handled (the entry is then cleared); false when not connected.</returns>
    internal bool Execute(string message)
    {
        if (message.Length == 0) return false;
        if (_history.Count == 0 || _history[0] != message) _history.Insert(0, message);
        if (_history.Count > HistoryLimit) _history.RemoveAt(_history.Count - 1);
        _historyIndex = -1;

        string[] words = message.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        string command = words.Length > 0 ? words[0].ToLowerInvariant() : string.Empty;
        if (command == "/nc")
        {
            string sub = words.Length > 1 ? words[1].ToLowerInvariant() : string.Empty;
            if (sub is "connect" or "c" or "rc") _controller.ReconnectNc();
            else if (sub is "disconnect" or "dc") _controller.DisconnectNc();
            else AppendMain("Usage: /nc connect, /nc disconnect, or /nc rc");
            return true;
        }
        IRcSession session = _context.Session;
        if (!session.IsConnected) return false;
        RcOptions options = _context.App.Options.Current;
        switch (message)
        {
            case "/rchelp":
                AppendMain("RC commands:");
                AppendMain("  /nc connect (or /nc c) - connect to the NPC server");
                AppendMain("  /nc disconnect (or /nc dc) - disconnect the NPC server");
                AppendMain("  /nc rc - reconnect the NPC server");
                AppendMain("  /reconnect or /rc - reconnect the RC server");
                AppendMain("  /scripthelp2 [query] - search script help");
                AppendMain("  /find [text] or /finddef [text] - find in the current editor");
                AppendMain("  /clear or /clear all - clear the current chat or all chats");
                AppendHelp("Emotes: type Kappa, PMNormal, or :v in chat");
                return true;
            case "/help":
                SendToServer(session, message);
                AppendMain("RC: /rchelp - show Remote Control commands");
                AppendHelp("RC emotes: type Kappa, PMNormal, or :v in chat");
                return true;
            case "/reconnect" or "/rc":
                UiSafe.Fire(_controller.ReconnectServerAsync, "MainWindow.Reconnect");
                return true;
            case "/clear":
                if (CurrentTab is { } current) ClearTab(current);
                return true;
            case "/clear all":
                foreach (ChatTab tab in _tabs.Where(tab => tab.Kind != ChatTabKind.FindResults)) ClearTab(tab);
                return true;
        }
        if (message.StartsWith("/scripthelp2", StringComparison.Ordinal) && (message.Length == 12 || char.IsWhiteSpace(message[12])))
        {
            string query = message[12..].Trim();
            string cache = System.IO.Path.Combine(_context.App.Paths.EditorCacheDirectory, "scriptapi.json");
            PendingScriptHelp = UiSafe.RunAsync(async () =>
            {
                IReadOnlyList<string> lines = await ScriptHelp.SearchAsync(options.AutocompleteSource, cache, query, ClosedToken).ConfigureAwait(true);
                if (IsClosed) return;
                foreach (string line in lines) AppendMain(line);
            }, "MainWindow.ScriptHelp");
            return true;
        }
        if (options.SeparateFindResults && (message == "/find" || message.StartsWith("/find ", StringComparison.Ordinal) || message == "/finddef" || message.StartsWith("/finddef ", StringComparison.Ordinal)))
            BeginFindResults();
        if (CurrentTab is { Kind: ChatTabKind.Channel } channel)
        {
            ChatTab target = channel;
            UiSafe.Fire(async () =>
            {
                bool sent = await session.SendIrcTextAsync("privmsg", target.Name, message).ConfigureAwait(true);
                if (!sent && !IsClosed && _tabs.Contains(target)) Append(target, "* Failed to send message", null);
            }, "MainWindow.IrcSend");
            return true;
        }
        string serverCommand = message;
        if (s_accountCommands.Contains(message, StringComparer.Ordinal) && _context.Account.Length > 0) serverCommand += " " + _context.Account;
        SendToServer(session, serverCommand);
        return true;
    }

    private void SendToServer(IRcSession session, string text)
    {
        UiSafe.Fire(async () =>
        {
            bool sent = await session.SendRcChatAsync(text, alsoOnNc: session.IsNcAuthenticated).ConfigureAwait(true);
            if (!sent && !IsClosed && ReferenceEquals(session, _context.Session))
                AppendMain(string.IsNullOrEmpty(session.LastError) ? "The command could not be sent." : session.LastError!);
        }, "MainWindow.Send");
    }
}
