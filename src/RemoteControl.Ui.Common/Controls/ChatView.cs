using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using RemoteControl.Core;

namespace RemoteControl.Ui.Common;

/// <summary>What <see cref="ChatView.LinkClicked"/> carries.</summary>
/// <param name="url">The URL of the link.</param>
public sealed class ChatLinkEventArgs(string url) : EventArgs
{
    /// <summary>The URL of the link (<c>www.</c> links carry <c>https://</c>).</summary>
    public string Url { get; } = url;
}

/// <summary>
/// The read-only chat log of every chat tab and PM window (<c>appendChat</c> and <c>configureChatField</c> of the C++). Each
/// <see cref="Append"/> adds one line in the C++ format (<see cref="ChatFormatter"/>): an optional time stamp in the bold colour,
/// a bold prefix up to the first colon, <c>#ALERT</c> lines in the alert colour without their prefix, underlined links that raise
/// <see cref="LinkClicked"/> (hand cursor), and emotes shown as pictures (at most one and a half lines high; the C++ used their own size). Text can be selected across lines and copied (Ctrl+C or
/// the context menu). The font and colours follow the theme resources (<see cref="ThemeKeys"/>), so option changes apply at once.
/// The view scrolls to the newest line only while it is at the bottom (or always, with <see cref="AlwaysScrollToEnd"/>). It keeps at
/// most <see cref="MaximumLines"/> lines and <see cref="MaximumCharacters"/> characters, dropping the oldest (defect D10).
/// </summary>
/// <remarks>
/// Built on a read-only AvaloniaEdit <see cref="TextEditor"/> (exposed as <see cref="Editor"/>, for example for a find bar), which
/// only lays out the visible lines, so appends stay cheap with thousands of lines. The formatting is kept per line beside the
/// document, never as text tags, so dropping old lines drops their formatting too. Use on the UI thread.
/// </remarks>
/// <example>
/// <code>
/// var chat = new ChatView { Services = services };
/// chat.LinkClicked += (_, e) =&gt; services.Urls.Open(e.Url);
/// ChatAppendResult result = chat.Append("Graal123: hello Kappa www.example.com");
/// if (result.WantsUrgency &amp;&amp; !IsActive) RequestAttention();
/// if (result.WantsBeep &amp;&amp; !IsActive) services.Sounds.Beep();
/// chat.Append("RC: /rchelp - show Remote Control commands", new ChatAppendOptions { Timestamp = false, SuppressEmotes = true });
/// string all = chat.Text;   // for the find feature
/// </code>
/// </example>
public sealed class ChatView : UserControl
{
    /// <summary>The default line limit (the C++ <c>maxChatLines</c>).</summary>
    public const int DefaultMaximumLines = 10_000;

    /// <summary>The default character limit (the C++ <c>maxChatCharacters</c>, 4 MiB).</summary>
    public const int DefaultMaximumCharacters = 4 * 1024 * 1024;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);
    private readonly TextEditor _editor;
    private readonly List<ChatLine> _lines = new();
    private readonly ChatColorizer _colorizer;
    private readonly EmoteGenerator _emotes;
    private ScrollViewer? _scroller;
    private ScrollViewer? _hooked;
    private bool _scrollPending;
    private bool _following = true;
    private int _ownScrolls;
    private bool _hoveringLink;
    private int _maximumLines = DefaultMaximumLines;
    private int _maximumCharacters = DefaultMaximumCharacters;

    /// <summary>Creates an empty chat view.</summary>
    public ChatView()
    {
        _editor = new TextEditor
        {
            Name = "ChatEditor",
            IsReadOnly = true,
            WordWrap = true,
            ShowLineNumbers = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(5, 2, 5, 2),
            Document = new TextDocument(),
        };
        _editor.Document.UndoStack.SizeLimit = 0;
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.HighlightCurrentLine = false;
        _editor.Options.EnableTextDragDrop = false;
        _editor.Options.AllowScrollBelowDocument = false;
        _colorizer = new ChatColorizer(this);
        _emotes = new EmoteGenerator(this);
        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _editor.TextArea.TextView.ElementGenerators.Add(_emotes);
        _editor.TextArea.AddHandler(PointerPressedEvent, OnTextPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _editor.TextArea.AddHandler(PointerMovedEvent, OnTextPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _editor.TextArea.PointerExited += (_, _) => SetHoveringLink(false);
        _editor.ContextMenu = BuildContextMenu();
        Bind(_editor, TemplatedControl.BackgroundProperty, ThemeKeys.ChatBackBrush);
        Bind(_editor, TemplatedControl.ForegroundProperty, ThemeKeys.ChatBrush);
        Bind(_editor, TemplatedControl.FontFamilyProperty, ThemeKeys.ChatFontFamily);
        Bind(_editor, TemplatedControl.FontSizeProperty, ThemeKeys.ChatFontSize);
        Content = _editor;
        ResourcesChanged += (_, _) => RefreshBrushes();
        _editor.TemplateApplied += (_, _) => HookScroller();
    }

    /// <summary>Raised when a link is clicked with the left button.</summary>
    public event EventHandler<ChatLinkEventArgs>? LinkClicked;

    /// <summary>The application services (options for time stamps and sounds, pictures for emotes); without them the defaults are used and emotes stay text.</summary>
    public AppServices? Services { get; set; }

    /// <summary>Whether emotes are shown as pictures (on by default; a line can suppress them).</summary>
    public bool EmotesEnabled { get; set; } = true;

    /// <summary>Whether every append scrolls to the end, also when the user scrolled up (the C++ main chat did; channel tabs did not).</summary>
    public bool AlwaysScrollToEnd { get; set; }

    /// <summary>The most lines kept (at least 1); the oldest are dropped.</summary>
    public int MaximumLines
    {
        get => _maximumLines;
        set
        {
            _maximumLines = Math.Max(1, value);
            Trim();
        }
    }

    /// <summary>The most characters kept (at least 1024); the oldest lines are dropped.</summary>
    public int MaximumCharacters
    {
        get => _maximumCharacters;
        set
        {
            _maximumCharacters = Math.Max(1024, value);
            Trim();
        }
    }

    /// <summary>The text editor that shows the chat (read only): for a find bar, or to select text in code.</summary>
    public TextEditor Editor => _editor;

    /// <summary>How many lines the view holds.</summary>
    public int LineCount => _lines.Count;

    /// <summary>The formatted lines, oldest first.</summary>
    public IReadOnlyList<ChatLine> Lines => _lines;

    /// <summary>All text, one line per chat line, for the find feature and for saving.</summary>
    public string Text => _editor.Document.Text;

    /// <summary>The selected text, or an empty text.</summary>
    public string SelectedText => _editor.SelectedText ?? string.Empty;

    /// <summary>Whether the pointer is over a link (the cursor is then a hand).</summary>
    public bool IsPointerOverLink => _hoveringLink;

    /// <summary>Whether the view shows its last line (or is not laid out yet).</summary>
    public bool IsAtBottom
    {
        get
        {
            ScrollViewer? scroller = Scroller;
            if (scroller is null) return true;
            double extent = scroller.Extent.Height;
            double viewport = scroller.Viewport.Height;
            return viewport <= 0 || extent <= viewport || scroller.Offset.Y + viewport >= extent - 2;
        }
    }

    /// <summary>
    /// Appends a message (line breaks in it make more lines) and reports what the caller should do about its alert.
    /// </summary>
    /// <param name="message">The message as the server sent it, <c>#ALERT</c> prefix included.</param>
    /// <param name="options">How to append; <see cref="ChatAppendOptions.Default"/> when <c>null</c>.</param>
    /// <returns>The formatted line and the urgency and beep requests.</returns>
    public ChatAppendResult Append(string? message, ChatAppendOptions? options = null)
    {
        options ??= ChatAppendOptions.Default;
        RcOptions rc = Services?.Options.Current ?? RcOptions.Defaults;
        bool timestamps = options.Timestamp ?? rc.RcTimestamps;
        string? stamp = timestamps ? TimestampFormat.Format(rc.TimestampFormat, options.Time ?? DateTime.Now) : null;
        bool emotes = EmotesEnabled && !options.SuppressEmotes;
        IReadOnlyList<ChatLine> lines = ChatFormatter.FormatLines(message, stamp, emotes);
        bool follow = AlwaysScrollToEnd || _scrollPending || _following;

        TextDocument document = _editor.Document;
        var text = new System.Text.StringBuilder();
        foreach (ChatLine line in lines)
        {
            if (_lines.Count > 0 || text.Length > 0) text.Append('\n');
            text.Append(line.Text);
            _lines.Add(line);
        }
        document.Insert(document.TextLength, text.ToString());
        Trim();
        if (follow) ScheduleScrollToEnd();

        ChatLine first = lines[0];
        bool sound = first.Alert == ChatAlertKind.Sound;
        bool urgency = sound || first.Alert == ChatAlertKind.Urgent && !options.SuppressUrgency;
        return new ChatAppendResult(first, urgency, sound && rc.NotificationSounds);
    }

    /// <summary>Removes every line.</summary>
    public void Clear()
    {
        _lines.Clear();
        _editor.Document.Text = string.Empty;
        _scrollPending = false;
    }

    /// <summary>Scrolls to the newest line; the view then follows new lines again.</summary>
    public void ScrollToEnd()
    {
        _scrollPending = false;
        _following = true;
        ScrollViewer? scroller = Scroller;
        if (scroller is null) return;
        var end = new Vector(scroller.Offset.X, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
        if (scroller.Offset == end) return;
        _ownScrolls++;
        try
        {
            scroller.Offset = end;
        }
        finally
        {
            _ownScrolls--;
        }
    }

    /// <summary>Selects every line.</summary>
    public void SelectAll() => _editor.SelectAll();

    /// <summary>Copies the selection to the clipboard.</summary>
    public void Copy() => _editor.Copy();

    /// <summary>The link at a character offset of <see cref="Text"/>, or <c>null</c>.</summary>
    /// <param name="offset">The offset.</param>
    /// <returns>The URL or <c>null</c>.</returns>
    public string? LinkAt(int offset)
    {
        if (offset < 0 || offset > _editor.Document.TextLength || _lines.Count == 0) return null;
        DocumentLine line = _editor.Document.GetLineByOffset(offset);
        ChatLine? chat = LineFor(line);
        if (chat is null) return null;
        int column = offset - line.Offset;
        foreach (ChatLink link in chat.Links)
            if (column >= link.Start && column < link.Start + link.Length) return link.Url;
        return null;
    }

    /// <summary>Raises <see cref="LinkClicked"/> for the link at a character offset, as a click on it does.</summary>
    /// <param name="offset">The offset in <see cref="Text"/>.</param>
    /// <returns>Whether there was a link.</returns>
    public bool ClickLinkAt(int offset)
    {
        string? url = LinkAt(offset);
        if (url is null) return false;
        UiSafe.Run(() => LinkClicked?.Invoke(this, new ChatLinkEventArgs(url)), "ChatView.LinkClicked");
        return true;
    }

    /// <summary>The chat line of a document line, or <c>null</c>.</summary>
    internal ChatLine? LineFor(DocumentLine line)
    {
        int index = line.LineNumber - 1;
        return index >= 0 && index < _lines.Count ? _lines[index] : null;
    }

    /// <summary>The brushes and the bold typeface the colouring uses, refreshed when the theme resources change.</summary>
    internal ChatBrushes Brushes { get; private set; } = ChatBrushes.Default;

    /// <summary>The picture of an emote, or <c>null</c> when it cannot be shown.</summary>
    internal Bitmap? EmoteImage(Emote emote) => Services?.Assets.EmoteImage(emote.Text);

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RefreshBrushes();
        HookScroller();
        if (_scrollPending || _following) ScheduleScrollToEnd();
    }

    private ScrollViewer? Scroller
    {
        get
        {
            if (_scroller is not null && _scroller.IsAttachedToVisualTree()) return _scroller;
            _scroller = _editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            return _scroller;
        }
    }

    /// <summary>
    /// Keeps the view at the bottom while it follows: AvaloniaEdit estimates the height of lines it has not laid out yet, so the
    /// extent grows after a scroll to the end; a scroll by the user decides whether the view follows (at the bottom) or not.
    /// </summary>
    private void HookScroller()
    {
        ScrollViewer? scroller = _editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroller is null || ReferenceEquals(scroller, _hooked)) return;
        if (_hooked is not null) _hooked.ScrollChanged -= OnScrollChanged;
        _hooked = scroller;
        _scroller = scroller;
        scroller.ScrollChanged += OnScrollChanged;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        bool layout = e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0;
        if (layout && _following)
        {
            ScheduleScrollToEnd();
            return;
        }
        if (e.OffsetDelta.Y == 0 || _ownScrolls != 0) return;
        _following = IsAtBottom;
        if (!_following) _scrollPending = false;
    }

    private void Trim()
    {
        TextDocument document = _editor.Document;
        int dropLines = Math.Max(0, _lines.Count - _maximumLines);
        if (document.TextLength - (dropLines > 0 && dropLines < document.LineCount ? document.GetLineByNumber(dropLines + 1).Offset : 0) > _maximumCharacters)
        {
            int keepFrom = document.TextLength - _maximumCharacters;
            DocumentLine cut = document.GetLineByOffset(Math.Min(document.TextLength, keepFrom));
            int lineNumber = cut.Offset < keepFrom ? cut.LineNumber + 1 : cut.LineNumber;
            dropLines = Math.Max(dropLines, lineNumber - 1);
        }
        dropLines = Math.Min(dropLines, _lines.Count - 1);
        if (dropLines <= 0) return;
        int end = document.GetLineByNumber(dropLines + 1).Offset;
        document.Remove(0, end);
        _lines.RemoveRange(0, dropLines);
    }

    private void ScheduleScrollToEnd()
    {
        if (_scrollPending) return;
        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (_scrollPending) UiSafe.Run(ScrollToEnd, "ChatView.ScrollToEnd");
        }, DispatcherPriority.Background);
    }

    private void RefreshBrushes()
    {
        IBrush Find(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : fallback;
        ChatBrushes defaults = ChatBrushes.Default;
        IBrush accent = Find(ThemeKeys.AccentBrush, defaults.Bold);
        Color selection = accent is ISolidColorBrush solid ? solid.Color : Colors.SteelBlue;
        Brushes = new ChatBrushes(Find(ThemeKeys.ChatBoldBrush, defaults.Bold), Find(ThemeKeys.AlertBrush, defaults.Alert));
        _editor.TextArea.SelectionBrush = new SolidColorBrush(ThemePalette.WithAlpha(selection, 0.4));
        _editor.TextArea.SelectionBorder = null;
        _editor.TextArea.SelectionForeground = null;
        _editor.TextArea.TextView.Redraw();
    }

    private ContextMenu BuildContextMenu()
    {
        var copy = new MenuItem { Header = "_Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copy.Click += UiSafe.Handler<Avalonia.Interactivity.RoutedEventArgs>((_, _) => Copy(), "ChatView.Copy");
        var selectAll = new MenuItem { Header = "Select _All", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
        selectAll.Click += UiSafe.Handler<Avalonia.Interactivity.RoutedEventArgs>((_, _) => SelectAll(), "ChatView.SelectAll");
        var menu = new ContextMenu { ItemsSource = new object[] { copy, selectAll } };
        menu.Opening += (_, _) => copy.IsEnabled = !string.IsNullOrEmpty(SelectedText);
        return menu;
    }

    private int? OffsetAt(PointerEventArgs e)
    {
        TextView view = _editor.TextArea.TextView;
        Point point = e.GetPosition(view) + view.ScrollOffset;
        TextViewPosition? position = view.GetPosition(point);
        if (position is not { } found) return null;
        if (found.Line < 1 || found.Line > _editor.Document.LineCount) return null;
        DocumentLine line = _editor.Document.GetLineByNumber(found.Line);
        VisualLine? visual = view.GetVisualLine(found.Line);
        if (visual is not null)
        {
            double x = point.X;
            TextLineForVisual(visual, point.Y, out double lineWidth);
            if (x > lineWidth + 1) return null;
        }
        return line.Offset + Math.Max(0, found.Column - 1);
    }

    private static void TextLineForVisual(VisualLine visual, double y, out double width)
    {
        width = double.MaxValue;
        Avalonia.Media.TextFormatting.TextLine? textLine = visual.GetTextLineByVisualYPosition(y);
        if (textLine is not null) width = textLine.WidthIncludingTrailingWhitespace;
    }

    private void OnTextPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_editor.TextArea).Properties.IsLeftButtonPressed || e.KeyModifiers != KeyModifiers.None) return;
        int? offset = OffsetAt(e);
        if (offset is null || LinkAt(offset.Value) is null) return;
        e.Handled = true;
        ClickLinkAt(offset.Value);
    }

    private void OnTextPointerMoved(object? sender, PointerEventArgs e)
    {
        int? offset = OffsetAt(e);
        SetHoveringLink(offset is not null && LinkAt(offset.Value) is not null);
    }

    private void SetHoveringLink(bool hovering)
    {
        if (_hoveringLink == hovering) return;
        _hoveringLink = hovering;
        _editor.TextArea.TextView.Cursor = hovering ? HandCursor : TextCursor;
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target[!property] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(key);

    /// <summary>The brushes of the formatted parts.</summary>
    internal sealed record ChatBrushes(IBrush Bold, IBrush Alert)
    {
        public static ChatBrushes Default { get; } = new(new SolidColorBrush(Color.FromRgb(0x00, 0xC0, 0x00)), new SolidColorBrush(Color.FromRgb(0xf4, 0x87, 0x71)));
    }

    /// <summary>Colours the time stamp, the bold prefix, alert lines and links of each line from the per-line formatting.</summary>
    private sealed class ChatColorizer(ChatView owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            ChatLine? chat = owner.LineFor(line);
            if (chat is null) return;
            ChatBrushes brushes = owner.Brushes;
            int length = line.Length;
            foreach (ChatSpan span in chat.Spans)
            {
                int start = Math.Clamp(span.Start, 0, length);
                int end = Math.Clamp(span.Start + span.Length, 0, length);
                if (end <= start) continue;
                ChangeLinePart(line.Offset + start, line.Offset + end, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(span.Kind == ChatSpanKind.Alert ? brushes.Alert : brushes.Bold);
                    if (span.Kind != ChatSpanKind.Timestamp) SetBold(element);
                });
            }
            foreach (ChatLink link in chat.Links)
            {
                int start = Math.Clamp(link.Start, 0, length);
                int end = Math.Clamp(link.Start + link.Length, 0, length);
                if (end <= start) continue;
                ChangeLinePart(line.Offset + start, line.Offset + end, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brushes.Bold);
                    element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                });
            }
        }

        private static void SetBold(VisualLineElement element)
        {
            Typeface current = element.TextRunProperties.Typeface;
            element.TextRunProperties.SetTypeface(new Typeface(current.FontFamily, current.Style, FontWeight.Bold, current.Stretch));
        }
    }

    /// <summary>Replaces each emote of a line by its picture (the text stays in the document, so copying gives the word).</summary>
    private sealed class EmoteGenerator(ChatView owner) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            TextDocument? document = CurrentContext?.Document as TextDocument;
            if (document is null || startOffset >= document.TextLength) return -1;
            DocumentLine line = document.GetLineByOffset(startOffset);
            ChatLine? chat = owner.LineFor(line);
            if (chat is null || chat.Emotes.Count == 0) return -1;
            int column = startOffset - line.Offset;
            foreach (ChatEmote emote in chat.Emotes)
                if (emote.Start >= column && emote.Start + emote.Length <= line.Length && owner.EmoteImage(emote.Emote) is not null) return line.Offset + emote.Start;
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            TextDocument? document = CurrentContext?.Document as TextDocument;
            if (document is null) return null;
            DocumentLine line = document.GetLineByOffset(offset);
            ChatLine? chat = owner.LineFor(line);
            if (chat is null) return null;
            int column = offset - line.Offset;
            foreach (ChatEmote emote in chat.Emotes)
            {
                if (emote.Start != column) continue;
                Bitmap? picture = owner.EmoteImage(emote.Emote);
                if (picture is null) return null;
                double lineHeight = Math.Max(8, Math.Ceiling(owner._editor.FontSize * 1.5));
                double scale = Math.Min(1, lineHeight / Math.Max(1, picture.Size.Height));
                var image = new Image { Source = picture, Width = Math.Round(picture.Size.Width * scale), Height = Math.Round(picture.Size.Height * scale), Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
                return new InlineObjectElement(emote.Length, image);
            }
            return null;
        }
    }
}
