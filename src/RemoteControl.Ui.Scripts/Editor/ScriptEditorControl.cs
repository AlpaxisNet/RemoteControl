using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Indentation;
using RemoteControl.Core;
using RemoteControl.Scripting;
using RemoteControl.Ui.Common;
using TextSearch = RemoteControl.Scripting.TextSearch;

namespace RemoteControl.Ui.Scripts;

/// <summary>What a <see cref="ScriptEditorControl"/> edits.</summary>
public enum ScriptEditorMode
{
    /// <summary>A GS2 script: Graal highlighting, analysis, autocomplete, hover and signature help.</summary>
    Script,

    /// <summary>Configuration text (NPC flags, server options, <c>.ini</c> files): ini highlighting only.</summary>
    Configuration,

    /// <summary>Plain text without highlighting.</summary>
    Plain,
}

/// <summary>
/// The script editor (C++ <c>TGScriptEditor</c>): an AvaloniaEdit editor with the options of the RC (tab width, font, line numbers,
/// highlighting with the chosen syntax theme, bracket matching, current line, auto-indent, minimap), the live script analysis
/// underlines, autocomplete, the hover and signature popovers, multi-caret editing, the formatter, a find and replace bar, go to line,
/// and the keyboard shortcuts of the C++ (Ctrl+S, Ctrl+F, Ctrl+H, Ctrl+G, Ctrl+L, Ctrl+D, Ctrl+Alt+Up/Down, Alt+click, Escape,
/// Ctrl+Space, Ctrl+mouse wheel). Texts of 1 MiB or more are shown without highlighting, bracket matching, completion and analysis.
/// Used on the UI thread; the analysis runs in the background on a copy of the text.
/// </summary>
public sealed class ScriptEditorControl : UserControl
{
    private static string s_lastFind = string.Empty;

    private readonly TextEditor _editor;
    private readonly MinimapView _minimap;
    private readonly DiagnosticsRenderer _diagnosticsRenderer = new();
    private readonly ChangedLinesRenderer _changedRenderer = new();
    private readonly BracketRenderer _bracketRenderer = new();
    private readonly MultiCaret _carets;
    private readonly MultiCaretRenderer _caretRenderer;
    private readonly DispatcherTimer _analysisTimer;
    private readonly DispatcherTimer _symbolsTimer;
    private readonly DispatcherTimer _completionTimer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _signatureTimer;
    private readonly Popup _completionPopup;
    private readonly ListBox _completionList;
    private readonly TextBlock _completionPreview;
    private readonly Popup _hoverPopup;
    private readonly TextBlock _hoverText;
    private readonly Popup _signaturePopup;
    private readonly TextBlock _signatureText;
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly TextBox _replaceBox;
    private readonly TextBlock _replaceLabel;
    private readonly CheckBox _wholeWord;
    private readonly CheckBox _caseSensitive;
    private readonly Button _replaceButton;
    private readonly Button _replaceAllButton;
    private readonly TextBlock _findStatus;
    private readonly Border _goToBar;
    private readonly TextBox _lineBox;
    private ScriptEditorMode _mode;
    private AppServices? _services;
    private bool _attached;
    private bool _largeContent;
    private bool _settingContent;
    private bool _completionArmed;
    private bool _ctrlSDown;
    private long _version;
    private int _pendingBulkBytes;
    private int _pendingBulkLines;
    private int? _zoomPoints;
    private int _lastLine = 1;
    private Point _hoverPoint;
    private CompletionResult? _completion;
    private long _completionVersion;
    private LocalSymbols _symbols = LocalSymbols.Empty;
    private IReadOnlyList<Diagnostic> _diagnostics = Array.Empty<Diagnostic>();
    private SyntaxTheme? _theme;

    /// <summary>Creates an editor for the XAML loader; set <see cref="Services"/> before it is shown.</summary>
    public ScriptEditorControl() : this(null, ScriptEditorMode.Script)
    {
    }

    /// <summary>Creates an editor.</summary>
    /// <param name="services">The program's services (options, theme, paths).</param>
    /// <param name="mode">What it edits.</param>
    public ScriptEditorControl(AppServices? services, ScriptEditorMode mode)
    {
        _services = services;
        _mode = mode;
        _editor = new TextEditor
        {
            Name = "Editor",
            ShowLineNumbers = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Document = new TextDocument(),
        };
        _editor.Options.HighlightCurrentLine = true;
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.EnableRectangularSelection = true;
        _editor.Options.AllowScrollBelowDocument = false;
        var textView = _editor.TextArea.TextView;
        textView.BackgroundRenderers.Add(_changedRenderer);
        textView.BackgroundRenderers.Add(_bracketRenderer);
        textView.BackgroundRenderers.Add(_diagnosticsRenderer);
        _carets = new MultiCaret(_editor.TextArea);
        _caretRenderer = new MultiCaretRenderer(_carets);
        textView.BackgroundRenderers.Add(_caretRenderer);
        _carets.Changed += (_, _) => textView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Caret);

        _minimap = new MinimapView { Name = "Minimap", Editor = _editor, IsVisible = false };

        _analysisTimer = Timer(EditorLimits.AnalysisDelayMilliseconds, RunAnalysis, "script analysis");
        _symbolsTimer = Timer(EditorLimits.AnalysisDelayMilliseconds, RunSymbols, "script symbols");
        _completionTimer = Timer(EditorLimits.CompletionDelayMilliseconds, () => OpenCompletion(CompletionTrigger.Interactive), "autocomplete");
        _hoverTimer = Timer(EditorLimits.HoverDelayMilliseconds, ShowHoverAtPointer, "hover");
        _signatureTimer = Timer(60, UpdateSignature, "signature help");

        _completionList = new ListBox
        {
            Name = "CompletionList",
            MaxHeight = 16 * 24,
            ItemsSource = CompletionRows,
            ItemTemplate = new FuncDataTemplate<CompletionRow>((row, _) => CompletionRowView()),
            Focusable = false,
        };
        _completionList.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => UpdatePreview(), "completion selection");
        _completionList.AddHandler(PointerReleasedEvent, UiSafe.Handler<PointerReleasedEventArgs>((_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) AcceptCompletion();
        }, "completion click"), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        _completionPreview = new TextBlock { Name = "CompletionPreview", TextWrapping = TextWrapping.Wrap, MaxLines = 4, Margin = new Thickness(6, 4), FontSize = 11, Opacity = 0.9 };
        var completionPanel = new DockPanel { Width = 420 };
        DockPanel.SetDock(_completionPreview, Dock.Bottom);
        completionPanel.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = _completionPreview, [!Border.BorderBrushProperty] = new DynamicResourceExtension(ThemeKeys.BorderBrush) });
        DockPanel.SetDock(completionPanel.Children[0], Dock.Bottom);
        completionPanel.Children.Add(_completionList);
        _completionPopup = MakePopup("CompletionPopup", completionPanel);

        _hoverText = new TextBlock { Name = "HoverText", TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(8, 6) };
        _hoverPopup = MakePopup("HoverPopup", _hoverText);
        _signatureText = new TextBlock { Name = "SignatureText", TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(8, 6) };
        _signaturePopup = MakePopup("SignaturePopup", _signatureText);

        (_findBar, _findBox, _replaceBox, _replaceLabel, _wholeWord, _caseSensitive, _replaceButton, _replaceAllButton, _findStatus) = BuildFindBar();
        (_goToBar, _lineBox) = BuildGoToBar();

        var body = new DockPanel();
        DockPanel.SetDock(_minimap, Dock.Right);
        body.Children.Add(_minimap);
        body.Children.Add(_editor);
        var root = new DockPanel();
        DockPanel.SetDock(_findBar, Dock.Top);
        DockPanel.SetDock(_goToBar, Dock.Top);
        root.Children.Add(_findBar);
        root.Children.Add(_goToBar);
        root.Children.Add(_completionPopup);
        root.Children.Add(_hoverPopup);
        root.Children.Add(_signaturePopup);
        root.Children.Add(body);
        Content = root;

        _editor.Document.Changed += UiSafe.Handler<DocumentChangeEventArgs>((_, e) => OnDocumentChanged(e), "editor change");
        _editor.TextArea.Caret.PositionChanged += UiSafe.Handler((_, _) => OnCaretMoved(), "editor caret");
        _editor.TextArea.SelectionChanged += UiSafe.Handler((_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty), "editor selection");
        _editor.TextArea.TextView.ScrollOffsetChanged += UiSafe.Handler((_, _) => HideHover(), "editor scroll");
        AddHandler(KeyDownEvent, UiSafe.Handler<KeyEventArgs>((_, e) => OnPreviewKeyDown(e), "editor key"), RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, UiSafe.Handler<KeyEventArgs>((_, e) => { if (e.Key == Key.S) _ctrlSDown = false; }, "editor key up"), RoutingStrategies.Tunnel);
        _editor.TextArea.AddHandler(TextInputEvent, UiSafe.Handler<TextInputEventArgs>((_, e) => OnPreviewTextInput(e), "editor text input"), RoutingStrategies.Tunnel);
        _editor.TextArea.TextEntered += UiSafe.Handler<TextInputEventArgs>((_, e) => OnTextEntered(e), "editor text entered");
        _editor.TextArea.AddHandler(PointerPressedEvent, UiSafe.Handler<PointerPressedEventArgs>((_, e) => OnPreviewPointerPressed(e), "editor click"), RoutingStrategies.Tunnel);
        _editor.AddHandler(PointerWheelChangedEvent, UiSafe.Handler<PointerWheelEventArgs>((_, e) => OnPreviewWheel(e), "editor wheel"), RoutingStrategies.Tunnel);
        _editor.TextArea.TextView.PointerMoved += UiSafe.Handler<PointerEventArgs>((_, e) => OnHoverMoved(e), "editor hover");
        _editor.TextArea.TextView.PointerExited += UiSafe.Handler<PointerEventArgs>((_, _) => HideHover(), "editor hover leave");
    }

    /// <summary>The program's services; changing them applies their options.</summary>
    public AppServices? Services
    {
        get => _services;
        set
        {
            if (ReferenceEquals(_services, value)) return;
            Detach();
            _services = value;
            if (VisualRoot is not null) Attach();
        }
    }

    /// <summary>What the editor edits.</summary>
    public ScriptEditorMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            ApplyOptions();
        }
    }

    /// <summary>The AvaloniaEdit editor inside.</summary>
    public TextEditor Editor => _editor;

    /// <summary>The text.</summary>
    public string Text
    {
        get => _editor.Document.Text;
        set => SetContent(value);
    }

    /// <summary>The selected text, or empty.</summary>
    public string SelectedText => _editor.TextArea.Selection.IsEmpty ? string.Empty : _editor.SelectedText;

    /// <summary>Whether the text cannot be changed.</summary>
    public bool IsReadOnly
    {
        get => _editor.IsReadOnly;
        set => _editor.IsReadOnly = value;
    }

    /// <summary>Whether the text is 1 MiB or more: no highlighting, bracket matching, completion or analysis.</summary>
    public bool IsLargeContent => _largeContent;

    /// <summary>The 1-based line of the caret.</summary>
    public int CaretLine => _editor.TextArea.Caret.Line;

    /// <summary>The diagnostics shown.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>The online players offered inside string literals (the session's player list), or null.</summary>
    public Func<IReadOnlyList<OnlinePlayer>>? PlayerSource { get; set; }

    /// <summary>The lines tinted as changed against the server's text, or null.</summary>
    public ChangedLines? ChangedLines
    {
        get => _changedRenderer.Lines;
        set
        {
            if (_changedRenderer.Lines == value) return;
            _changedRenderer.Lines = value;
            _editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        }
    }

    /// <summary>Raised after every change of the text (typing, <see cref="SetContent"/>, <see cref="ReplaceText"/>).</summary>
    public event EventHandler? TextChanged;

    /// <summary>Raised when the user asks to save (Ctrl+S, once per key press).</summary>
    public event EventHandler? SaveRequested;

    /// <summary>Raised when the caret moved to another line (the "Line: N" label).</summary>
    public event EventHandler? CaretLineChanged;

    /// <summary>Raised when the selection changed.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when new diagnostics are shown.</summary>
    public event EventHandler? DiagnosticsChanged;

    internal ObservableCollection<CompletionRow> CompletionRows { get; } = [];

    internal bool IsCompletionOpen => _completionPopup.IsOpen;

    internal CompletionResult? Completion => _completion;

    internal ListBox CompletionList => _completionList;

    internal string CompletionPreviewText => _completionPreview.Text ?? string.Empty;

    internal bool IsHoverOpen => _hoverPopup.IsOpen;

    internal string HoverText => InlinesText(_hoverText);

    internal bool IsSignatureOpen => _signaturePopup.IsOpen;

    internal string SignatureText => InlinesText(_signatureText);

    internal IHighlightingDefinition? HighlightingDefinition => _editor.SyntaxHighlighting;

    internal SyntaxTheme? SyntaxThemeInUse => _theme;

    internal MinimapView Minimap => _minimap;

    internal MultiCaret Carets => _carets;

    internal bool IsFindOpen => _findBar.IsVisible;

    internal bool IsReplaceOpen => _findBar.IsVisible && _replaceBox.IsVisible;

    internal bool IsGoToLineOpen => _goToBar.IsVisible;

    internal TextBox FindBox => _findBox;

    internal TextBox ReplaceBox => _replaceBox;

    internal TextBox LineBox => _lineBox;

    internal string FindStatus => _findStatus.Text ?? string.Empty;

    internal int FontPoints => _zoomPoints ?? _services?.Options.Current.ScriptFontSize ?? 10;

    internal bool BracketsEnabled { get; private set; }

    internal (int Open, int Close)? BracketPair => _bracketRenderer.Pair;

    /// <summary>
    /// Shows a text as opened: not undoable, the undo history is cleared, the caret goes to the start. A text of 1 MiB or more switches
    /// to the large-text mode.
    /// </summary>
    /// <param name="text">The text.</param>
    public void SetContent(string? text)
    {
        string value = text ?? string.Empty;
        _settingContent = true;
        try
        {
            _carets.Clear();
            HidePopups();
            _largeContent = EditorLimits.IsLargeContent(value);
            _diagnostics = Array.Empty<Diagnostic>();
            _diagnosticsRenderer.Diagnostics = _diagnostics;
            _symbols = LocalSymbols.Empty;
            _editor.Document.Text = value;
            _editor.Document.UndoStack.ClearAll();
            _editor.TextArea.ClearSelection();
            _editor.CaretOffset = 0;
            _editor.ScrollToHome();
        }
        finally
        {
            _settingContent = false;
        }
        ApplyOptions();
        TextChanged?.Invoke(this, EventArgs.Empty);
        DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the whole text as one undo step; the caret stays at the same offset (clamped).</summary>
    /// <param name="text">The new text.</param>
    /// <returns>Whether the text changed.</returns>
    public bool ReplaceText(string? text)
    {
        string value = text ?? string.Empty;
        TextDocument document = _editor.Document;
        if (string.Equals(document.Text, value, StringComparison.Ordinal)) return false;
        int caret = _editor.CaretOffset;
        _carets.Clear();
        document.Replace(0, document.TextLength, value);
        _editor.CaretOffset = Math.Clamp(caret, 0, document.TextLength);
        if (!_largeContent && EditorLimits.IsLargeContent(value))
        {
            _largeContent = true;
            ApplyOptions();
        }
        return true;
    }

    /// <summary>Formats the text (the Format button): comments removed and re-indented as the options say, as one undo step.</summary>
    public void Format()
    {
        if (IsReadOnly) return;
        RcOptions options = _services?.Options.Current ?? RcOptions.Defaults;
        var formatter = new FormatterOptions
        {
            IndentWidth = options.FormatIndentWidth,
            UseTabs = options.FormatUseTabs,
            TrimTrailingWhitespace = options.FormatTrimTrailing,
            RemoveLineComments = options.RemoveLineComments,
            RemoveBlockComments = options.RemoveBlockComments,
            PreserveClientside = options.PreserveClientside,
        };
        ReplaceText(Gs2Formatter.Format(Text, formatter));
    }

    /// <summary>Selects a range and scrolls to it.</summary>
    /// <param name="start">The start offset.</param>
    /// <param name="length">The length.</param>
    public void Select(int start, int length)
    {
        int textLength = _editor.Document.TextLength;
        start = Math.Clamp(start, 0, textLength);
        length = Math.Clamp(length, 0, textLength - start);
        _editor.Select(start, length);
        _editor.CaretOffset = start + length;
        TextLocation location = _editor.Document.GetLocation(start);
        _editor.ScrollTo(location.Line, location.Column);
    }

    /// <summary>Shows the find bar (Ctrl+F) or the find and replace bar (Ctrl+H).</summary>
    /// <param name="replace">Whether replacing is shown.</param>
    public void ShowFind(bool replace)
    {
        _goToBar.IsVisible = false;
        _findBar.IsVisible = true;
        _replaceLabel.IsVisible = replace;
        _replaceBox.IsVisible = replace;
        _replaceButton.IsVisible = replace;
        _replaceAllButton.IsVisible = replace;
        _findStatus.Text = string.Empty;
        string selected = SelectedText;
        if (selected.Length > 0 && !selected.Contains('\n')) _findBox.Text = selected;
        else if (string.IsNullOrEmpty(_findBox.Text) && s_lastFind.Length > 0) _findBox.Text = s_lastFind;
        _findBox.Focus();
        _findBox.SelectAll();
    }

    /// <summary>Hides the find bar.</summary>
    public void HideFind()
    {
        _findBar.IsVisible = false;
        _editor.TextArea.Focus();
    }

    /// <summary>Finds the next (or previous) match of the find bar's text, selects it and scrolls to it.</summary>
    /// <param name="backwards">Search backwards.</param>
    /// <returns>Whether a match was found.</returns>
    public bool FindNext(bool backwards = false)
    {
        string query = _findBox.Text ?? string.Empty;
        if (query.Length == 0) return false;
        s_lastFind = query;
        string text = Text;
        int start = _editor.SelectionStart;
        int end = start + _editor.SelectionLength;
        TextRange? found = backwards ? TextSearch.FindPrevious(text, query, start, FindOptions()) : TextSearch.FindNext(text, query, end, FindOptions());
        if (found is not { } range)
        {
            _findStatus.Text = "Not found";
            return false;
        }
        _findStatus.Text = string.Empty;
        Select(range.Start, range.Length);
        return true;
    }

    /// <summary>The Replace button: replaces the selected match (or finds one first), then finds the next.</summary>
    /// <returns>Whether something was replaced.</returns>
    public bool ReplaceNext()
    {
        if (IsReadOnly) return false;
        string query = _findBox.Text ?? string.Empty;
        if (query.Length == 0) return false;
        s_lastFind = query;
        string text = Text;
        ReplaceResult result = TextSearch.Replace(text, query, _replaceBox.Text, new TextRange(_editor.SelectionStart, _editor.SelectionLength), FindOptions());
        if (result.Replaced is not { } replaced)
        {
            if (result.NextMatch is { } next) Select(next.Start, next.Length);
            else _findStatus.Text = "Not found";
            return false;
        }
        int removed = replaced.Length - (result.Text.Length - text.Length);
        _editor.Document.Replace(replaced.Start, removed, result.Text.Substring(replaced.Start, replaced.Length));
        if (result.NextMatch is { } match) Select(match.Start, match.Length);
        else _editor.CaretOffset = replaced.End;
        _findStatus.Text = string.Empty;
        return true;
    }

    /// <summary>The All button: replaces every match as one undo step.</summary>
    /// <returns>How many were replaced.</returns>
    public int ReplaceAll()
    {
        if (IsReadOnly) return 0;
        string query = _findBox.Text ?? string.Empty;
        if (query.Length == 0) return 0;
        s_lastFind = query;
        ReplaceAllResult result = TextSearch.ReplaceAll(Text, query, _replaceBox.Text, _editor.CaretOffset, FindOptions());
        if (result.Count > 0)
        {
            ReplaceText(result.Text);
            _editor.CaretOffset = Math.Clamp(result.Caret, 0, _editor.Document.TextLength);
        }
        _findStatus.Text = result.Count == 1 ? "1 replacement" : result.Count + " replacements";
        return result.Count;
    }

    /// <summary>Shows the go to line bar (Ctrl+G) with the current line.</summary>
    public void ShowGoToLine()
    {
        _findBar.IsVisible = false;
        _goToBar.IsVisible = true;
        _lineBox.Text = CaretLine.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _lineBox.Focus();
        _lineBox.SelectAll();
    }

    /// <summary>Puts the caret at the start of a line (clamped to the text) and scrolls there.</summary>
    /// <param name="line">The 1-based line.</param>
    public void GoToLine(int line)
    {
        TextDocument document = _editor.Document;
        int number = Math.Clamp(line, 1, Math.Max(1, document.LineCount));
        _editor.TextArea.ClearSelection();
        _editor.CaretOffset = document.GetLineByNumber(number).Offset;
        _editor.ScrollToLine(number);
        _editor.TextArea.Focus();
    }

    /// <summary>Opens the autocomplete list as Ctrl+Space does.</summary>
    public void RequestCompletion() => OpenCompletion(CompletionTrigger.UserRequested);

    /// <summary>Moves the caret.</summary>
    /// <param name="offset">The offset.</param>
    public void SetCaret(int offset)
    {
        _editor.TextArea.ClearSelection();
        _editor.CaretOffset = Math.Clamp(offset, 0, _editor.Document.TextLength);
    }

    /// <summary>Gives the keyboard focus to the text.</summary>
    public void FocusText() => _editor.TextArea.Focus();

    /// <summary>Accepts the selected autocomplete item.</summary>
    /// <returns>Whether one was inserted.</returns>
    internal bool AcceptCompletion()
    {
        if (!_completionPopup.IsOpen || _completion is null || _completionList.SelectedItem is not CompletionRow row) return false;
        if (_completionVersion != _version)
        {
            Propose(CompletionTrigger.UserRequested);
            if (_completion is null || _completionList.SelectedItem is not CompletionRow current) return false;
            row = current;
        }
        CompletionResult result = _completion!;
        HideCompletion();
        _editor.Document.Replace(result.ReplaceStart, result.ReplaceLength, row.Item.InsertText);
        _editor.CaretOffset = Math.Clamp(result.ReplaceStart + row.Item.InsertText.Length, 0, _editor.Document.TextLength);
        _editor.TextArea.Focus();
        return true;
    }

    /// <summary>Shows the hover popover for an offset (what resting the pointer there shows).</summary>
    /// <param name="offset">The offset.</param>
    /// <returns>Whether something was shown.</returns>
    internal bool ShowHover(int offset)
    {
        if (_mode != ScriptEditorMode.Script || _largeContent) return false;
        bool lsp = _services?.Options.Current.Lsp ?? true;
        HoverContent? content = HoverInfo.At(Text, offset, _diagnostics, lsp ? ApiDefinitionCatalog.Current : null, lsp ? _symbols.Functions : null);
        if (content is null || content.Blocks.Count == 0)
        {
            HideHover();
            return false;
        }
        _hoverText.Inlines = new InlineCollection();
        for (int index = 0; index < content.Blocks.Count; index++)
        {
            HoverBlock block = content.Blocks[index];
            if (index > 0) _hoverText.Inlines.Add(new LineBreak());
            _hoverText.Inlines.Add(new Run(block.Text) { FontWeight = block.Bold ? FontWeight.Bold : FontWeight.Normal });
        }
        Place(_hoverPopup, content.WordStart, above: true);
        _hoverPopup.IsOpen = true;
        return true;
    }

    /// <summary>Recomputes the signature hint at the caret now.</summary>
    internal void UpdateSignature()
    {
        _signatureTimer.Stop();
        if (!CompletionEnabled || !_completionArmed)
        {
            _signaturePopup.IsOpen = false;
            return;
        }
        SignatureHelpResult? help = SignatureHelp.At(Text, _editor.CaretOffset, ApiDefinitionCatalog.Current, _symbols.Functions);
        if (help is null || help.Parts.Count == 0)
        {
            _signaturePopup.IsOpen = false;
            return;
        }
        var inlines = new InlineCollection();
        foreach (SignaturePart part in help.Parts)
        {
            string[] pieces = part.Text.Split('\n');
            for (int index = 0; index < pieces.Length; index++)
            {
                if (index > 0) inlines.Add(new LineBreak());
                if (pieces[index].Length == 0) continue;
                var run = new Run(pieces[index]);
                if (part.Bold) run.FontWeight = FontWeight.Bold;
                if (part.Italic) run.FontStyle = FontStyle.Italic;
                if (part.Underline) run.TextDecorations = TextDecorations.Underline;
                inlines.Add(run);
            }
        }
        _signatureText.Inlines = inlines;
        Place(_signaturePopup, _editor.CaretOffset, above: true);
        _signaturePopup.IsOpen = true;
    }

    /// <summary>Runs the analysis now (tests and option changes); the result arrives on the UI thread.</summary>
    /// <returns>Completes when the result was applied or dropped.</returns>
    internal Task AnalyzeNowAsync()
    {
        _analysisTimer.Stop();
        return RunAnalysisAsync();
    }

    /// <summary>Extracts the local functions and identifiers now.</summary>
    /// <returns>Completes when done.</returns>
    internal Task UpdateSymbolsNowAsync()
    {
        _symbolsTimer.Stop();
        return RunSymbolsAsync();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();
        HidePopups();
        _analysisTimer.Stop();
        _symbolsTimer.Stop();
        _completionTimer.Stop();
        _hoverTimer.Stop();
        _signatureTimer.Stop();
    }

    // ---- options and theme -------------------------------------------------------------------------------------------------

    private bool CompletionEnabled => _mode == ScriptEditorMode.Script && !_largeContent && (_services?.Options.Current.Lsp ?? false);

    private bool DiagnosticsEnabled => _mode == ScriptEditorMode.Script && !_largeContent && (_services?.Options.Current.ScriptDiagnostics ?? false);

    private void Attach()
    {
        if (_attached || _services is null) return;
        _attached = true;
        _services.Options.Changed += OnOptionsChanged;
        _services.Theme.Changed += OnThemeChanged;
        ApiDefinitionCatalog.Loaded += OnApiLoaded;
        ApiDefinitionCatalog.Ensure(_services);
        ApplyOptions();
    }

    private void Detach()
    {
        if (!_attached || _services is null) return;
        _attached = false;
        _services.Options.Changed -= OnOptionsChanged;
        _services.Theme.Changed -= OnThemeChanged;
        ApiDefinitionCatalog.Loaded -= OnApiLoaded;
    }

    private void OnOptionsChanged(object? sender, OptionsChangedEventArgs e) => UiSafe.Run(() =>
    {
        if (_services is not null) ApiDefinitionCatalog.Ensure(_services);
        ApplyOptions();
    }, "editor options");

    private void OnThemeChanged(object? sender, EventArgs e) => UiSafe.Run(ApplyOptions, "editor theme");

    private void OnApiLoaded(object? sender, EventArgs e) => UiSafe.Run(() =>
    {
        if (DiagnosticsEnabled) ScheduleAnalysis(EditorLimits.AnalysisDelayMilliseconds);
    }, "script API loaded");

    /// <summary>Applies the options and the syntax theme to the editor (C++ <c>applyEditorOptions</c>).</summary>
    internal void ApplyOptions()
    {
        RcOptions options = _services?.Options.Current ?? RcOptions.Defaults;
        string specs = _services?.Paths.LanguageSpecsDirectory ?? string.Empty;
        SyntaxTheme theme = _services is null ? SyntaxThemeLoader.Classic : EditorTheme.For(options, specs);
        _theme = theme;
        HighlightingLanguage language = _mode switch { ScriptEditorMode.Script => HighlightingLanguage.Graal, ScriptEditorMode.Configuration => HighlightingLanguage.Ini, _ => HighlightingLanguage.None };
        _editor.SyntaxHighlighting = options.SyntaxHighlighting && !_largeContent ? EditorTheme.Definition(language, theme) : null;
        _editor.Background = EditorTheme.Brush(theme.Background, Colors.White);
        _editor.Foreground = EditorTheme.Brush(theme.Foreground, Colors.Black);
        _editor.LineNumbersForeground = EditorTheme.Brush(theme.LineNumbersForeground, Color.FromArgb(150, 128, 128, 128));
        _editor.TextArea.SelectionBrush = EditorTheme.Brush(theme.SelectionBackground, Color.FromArgb(110, 80, 130, 220));
        if (theme.SelectionForeground is { } selectionForeground) _editor.TextArea.SelectionForeground = EditorTheme.Brush(selectionForeground, Colors.White);
        _editor.TextArea.CaretBrush = EditorTheme.Brush(theme.Caret ?? theme.Foreground, Colors.Black);
        _editor.TextArea.TextView.CurrentLineBackground = EditorTheme.Brush(theme.CurrentLineBackground, Color.FromArgb(24, 128, 128, 128));
        _editor.TextArea.TextView.CurrentLineBorder = null;
        _bracketRenderer.Brush = EditorTheme.Brush(theme.BracketMatchBackground, Color.FromArgb(80, 128, 160, 255));
        _caretRenderer.CaretPen = new Pen(_editor.TextArea.CaretBrush, 1.5);
        _caretRenderer.SelectionBrush = _editor.TextArea.SelectionBrush;
        _minimap.Background = _editor.Background;
        _minimap.LineBrush = new SolidColorBrush(EditorTheme.ToColor(theme.Foreground), 0.45);
        _editor.ShowLineNumbers = options.ShowLineNumbers;
        _editor.Options.IndentationSize = Math.Max(1, options.ScriptTabWidth);
        _editor.Options.ConvertTabsToSpaces = !options.ScriptUseTabs;
        _editor.TextArea.IndentationStrategy = options.AutoIndenting ? new DefaultIndentationStrategy() : null;
        _editor.FontFamily = ThemeFonts.FromOption(options.ScriptFontFamily);
        _editor.FontSize = ThemeFonts.PointsToPixels(_zoomPoints ?? Math.Max(6, options.ScriptFontSize));
        _minimap.IsVisible = options.Minimap;
        BracketsEnabled = options.ShowBrackets && !_largeContent;
        if (!BracketsEnabled && _bracketRenderer.Clear()) _editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
        if (!CompletionEnabled) HidePopups();
        if (DiagnosticsEnabled) ScheduleAnalysis(EditorLimits.AnalysisDelayMilliseconds);
        else ClearDiagnostics();
        if (CompletionEnabled) _symbolsTimer.Start();
        _minimap.Refresh();
    }

    // ---- changes ---------------------------------------------------------------------------------------------------------

    private void OnDocumentChanged(DocumentChangeEventArgs e)
    {
        _version++;
        _minimap.Refresh();
        if (_settingContent) return;
        if (!_largeContent && e.InsertionLength >= EditorLimits.LargeContentBytes / 2 && EditorLimits.IsLargeContent(_editor.Document.Text))
        {
            _largeContent = true;
            ApplyOptions();
        }
        _pendingBulkBytes += e.InsertionLength;
        if (e.InsertionLength > 0) _pendingBulkLines += CountLines(e.InsertedText.Text);
        bool bulk = _pendingBulkBytes >= EditorLimits.BulkInsertBytes || _pendingBulkLines >= EditorLimits.BulkInsertLines;
        if (DiagnosticsEnabled) ScheduleAnalysis(bulk ? EditorLimits.BulkInsertResumeMilliseconds : EditorLimits.AnalysisDelayMilliseconds);
        if (CompletionEnabled)
        {
            _symbolsTimer.Stop();
            _symbolsTimer.Start();
            if (_completionArmed) RestartSignature();
        }
        if (_carets.Count > 0) _editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Caret);
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCaretMoved()
    {
        int line = CaretLine;
        if (line != _lastLine)
        {
            _lastLine = line;
            CaretLineChanged?.Invoke(this, EventArgs.Empty);
        }
        if (BracketsEnabled && _bracketRenderer.Update(_editor.Document, _editor.CaretOffset))
            _editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
        if (_completionArmed && CompletionEnabled) RestartSignature();
        if (_completionPopup.IsOpen && _completion is { } result)
        {
            int caret = _editor.CaretOffset;
            if (caret < result.ReplaceStart || caret > result.ReplaceStart + result.ReplaceLength + 1) HideCompletion();
        }
    }

    private void RestartSignature()
    {
        _signatureTimer.Stop();
        _signatureTimer.Start();
    }

    private static int CountLines(string text)
    {
        int count = 0;
        foreach (char c in text) if (c == '\n') count++;
        return count;
    }

    // ---- analysis --------------------------------------------------------------------------------------------------------

    private void ScheduleAnalysis(int milliseconds)
    {
        _analysisTimer.Stop();
        _analysisTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _analysisTimer.Start();
    }

    private void RunAnalysis() => UiSafe.Fire(RunAnalysisAsync, "script analysis");

    private async Task RunAnalysisAsync()
    {
        _pendingBulkBytes = 0;
        _pendingBulkLines = 0;
        if (!DiagnosticsEnabled)
        {
            ClearDiagnostics();
            return;
        }
        string snapshot = Text;
        long version = _version;
        bool lsp = _services?.Options.Current.Lsp ?? false;
        DiagnosticsOptions options = DiagnosticsOptions.ForDefinitions(ApiDefinitionCatalog.IsLoaded ? ApiDefinitionCatalog.Current : null, lsp);
        IReadOnlyList<Diagnostic> result = await Task.Run(() => Gs2Diagnostics.Analyze(snapshot, options)).ConfigureAwait(true);
        if (version != _version || !DiagnosticsEnabled) return;
        _diagnostics = result;
        _diagnosticsRenderer.Diagnostics = result;
        _editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
        DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearDiagnostics()
    {
        _analysisTimer.Stop();
        if (_diagnostics.Count == 0) return;
        _version++;
        _diagnostics = Array.Empty<Diagnostic>();
        _diagnosticsRenderer.Diagnostics = _diagnostics;
        _editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
        DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RunSymbols() => UiSafe.Fire(RunSymbolsAsync, "script symbols");

    private async Task RunSymbolsAsync()
    {
        if (!CompletionEnabled) return;
        string snapshot = Text;
        long version = _version;
        LocalSymbols symbols = await Task.Run(() => LocalSymbols.Extract(snapshot)).ConfigureAwait(true);
        if (version == _version) _symbols = symbols;
    }

    // ---- completion ------------------------------------------------------------------------------------------------------

    private void OpenCompletion(CompletionTrigger trigger)
    {
        _completionTimer.Stop();
        if (!CompletionEnabled || IsReadOnly) return;
        Propose(trigger);
    }

    private void Propose(CompletionTrigger trigger)
    {
        string text = Text;
        var engine = new CompletionEngine(ApiDefinitionCatalog.Current, _symbols.Functions.Count > 0 || _symbols.Identifiers.Count > 0 ? _symbols : null);
        IReadOnlyList<OnlinePlayer>? players = null;
        try
        {
            players = PlayerSource?.Invoke();
        }
        catch (Exception exception)
        {
            UiSafe.Log.Warn("The players for autocomplete could not be read.", exception);
        }
        CompletionResult result = engine.Propose(text, _editor.CaretOffset, players, trigger);
        _completion = result;
        _completionVersion = _version;
        CompletionRows.Clear();
        foreach (CompletionItem item in result.Items.Take(CompletionEngine.MaximumProposals)) CompletionRows.Add(new CompletionRow(item));
        if (CompletionRows.Count == 0)
        {
            HideCompletion();
            return;
        }
        _completionList.SelectedIndex = 0;
        UpdatePreview();
        Place(_completionPopup, _editor.CaretOffset, above: false);
        _completionPopup.IsOpen = true;
    }

    private void HideCompletion()
    {
        _completionTimer.Stop();
        _completionPopup.IsOpen = false;
        _completion = null;
        CompletionRows.Clear();
    }

    private void UpdatePreview()
    {
        _completionPreview.Text = _completionList.SelectedItem is CompletionRow row ? row.Preview : string.Empty;
        if (_completionList.SelectedItem is { } item) _completionList.ScrollIntoView(item);
    }

    private void MoveCompletion(int delta)
    {
        if (CompletionRows.Count == 0) return;
        int index = Math.Clamp(_completionList.SelectedIndex + delta, 0, CompletionRows.Count - 1);
        _completionList.SelectedIndex = index;
    }

    private static Control CompletionRowView()
    {
        var badge = new TextBlock { Width = 18, FontWeight = FontWeight.Bold, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        badge.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(CompletionRow.Badge)));
        var scope = new TextBlock { Width = 84, TextAlignment = TextAlignment.Right, Opacity = 0.7, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        scope.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(CompletionRow.Scope)));
        var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
        label.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(CompletionRow.Label)));
        var panel = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        DockPanel.SetDock(scope, Dock.Right);
        panel.Children.Add(badge);
        panel.Children.Add(scope);
        panel.Children.Add(label);
        return panel;
    }

    // ---- input ------------------------------------------------------------------------------------------------------------

    private void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool control = (e.KeyModifiers & KeyModifiers.Control) != 0;
        bool alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (!IsHoverNeutral(e.Key)) HideHover();
        if (_completionPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down: MoveCompletion(1); e.Handled = true; return;
                case Key.Up: MoveCompletion(-1); e.Handled = true; return;
                case Key.PageDown: MoveCompletion(8); e.Handled = true; return;
                case Key.PageUp: MoveCompletion(-8); e.Handled = true; return;
                case Key.Enter or Key.Tab when !control && !alt: e.Handled = AcceptCompletion(); return;
                case Key.Escape: HideCompletion(); e.Handled = true; return;
            }
        }
        if (control && !alt)
        {
            switch (e.Key)
            {
                case Key.S:
                    e.Handled = true;
                    if (_ctrlSDown) return;
                    _ctrlSDown = true;
                    SaveRequested?.Invoke(this, EventArgs.Empty);
                    return;
                case Key.F: ShowFind(false); e.Handled = true; return;
                case Key.H: ShowFind(true); e.Handled = true; return;
                case Key.G: ShowGoToLine(); e.Handled = true; return;
                case Key.L when IsTextFocused(): SelectLine(); e.Handled = true; return;
                case Key.D when IsTextFocused(): _carets.SelectNextOccurrence(); e.Handled = true; return;
                case Key.Space when IsTextFocused():
                    _completionArmed = true;
                    OpenCompletion(CompletionTrigger.UserRequested);
                    e.Handled = true;
                    return;
            }
        }
        if (!IsTextFocused()) return;
        if (control && alt && e.Key is Key.Up or Key.Down)
        {
            _carets.AddAdjacent(e.Key == Key.Down ? 1 : -1, shift);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_signaturePopup.IsOpen) _signaturePopup.IsOpen = false;
            if (_carets.Count > 0) { _carets.Clear(); e.Handled = true; }
            return;
        }
        if (_carets.Count > 0 && !control && !alt && !IsReadOnly)
        {
            bool handled = e.Key switch
            {
                Key.Back => _carets.Replace(string.Empty, -1),
                Key.Delete => _carets.Replace(string.Empty, 1),
                Key.Enter => _carets.Replace("\n", 0),
                Key.Tab => _carets.Replace(_editor.Options.ConvertTabsToSpaces ? new string(' ', _editor.Options.IndentationSize) : "\t", 0),
                _ => false,
            };
            if (handled) { e.Handled = true; return; }
        }
        if (e.Key == Key.Home && !control && !(_services?.Options.Current.SmartHomeEnd ?? true))
        {
            DocumentLine line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
            int anchor = _editor.TextArea.Selection.IsEmpty ? _editor.CaretOffset : (_editor.CaretOffset == _editor.SelectionStart ? _editor.SelectionStart + _editor.SelectionLength : _editor.SelectionStart);
            if (shift) _editor.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(_editor.TextArea, Math.Min(anchor, line.Offset), Math.Max(anchor, line.Offset));
            else _editor.TextArea.ClearSelection();
            _editor.CaretOffset = line.Offset;
            e.Handled = true;
        }
    }

    private void OnPreviewTextInput(TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || IsReadOnly) return;
        if (_carets.Count > 0)
        {
            _carets.Replace(e.Text, 0);
            e.Handled = true;
        }
    }

    private void OnTextEntered(TextInputEventArgs e)
    {
        string text = e.Text ?? string.Empty;
        if (text.Length == 0 || !CompletionEnabled) return;
        bool printable = text.All(c => !char.IsControl(c));
        if (printable) _completionArmed = true;
        bool identifier = text.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '$');
        if (_completionPopup.IsOpen)
        {
            if (identifier) Propose(CompletionTrigger.Interactive);
            else HideCompletion();
            return;
        }
        if (identifier && printable)
        {
            _completionTimer.Stop();
            _completionTimer.Start();
        }
        RestartSignature();
    }

    private void OnPreviewPointerPressed(PointerPressedEventArgs e)
    {
        HideHover();
        PointerPoint point = e.GetCurrentPoint(_editor.TextArea.TextView);
        if (!point.Properties.IsLeftButtonPressed) return;
        if ((e.KeyModifiers & KeyModifiers.Alt) == 0)
        {
            _carets.Clear();
            if (_completionPopup.IsOpen) HideCompletion();
            return;
        }
        int? offset = OffsetAt(point.Position);
        if (offset is not { } value) return;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && _carets.Count > 0) _carets.MoveLast(value);
        else _carets.Add(value, value);
        e.Handled = true;
    }

    private void OnPreviewWheel(PointerWheelEventArgs e)
    {
        HideHover();
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        int change = e.Delta.Y > 0 ? 1 : e.Delta.Y < 0 ? -1 : 0;
        e.Handled = true;
        if (change != 0) Zoom(change);
    }

    /// <summary>Changes the font size by points (Ctrl+wheel), between 6 and 48; not saved.</summary>
    /// <param name="change">The change.</param>
    internal void Zoom(int change)
    {
        _zoomPoints = Math.Clamp(FontPoints + change, 6, 48);
        _editor.FontSize = ThemeFonts.PointsToPixels(_zoomPoints.Value);
    }

    private void SelectLine()
    {
        DocumentLine line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
        _editor.Select(line.Offset, line.Length);
    }

    private bool IsTextFocused() => _editor.TextArea.IsKeyboardFocusWithin || _editor.IsKeyboardFocusWithin || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is null;

    private static bool IsHoverNeutral(Key key) => key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt;

    // ---- hover -----------------------------------------------------------------------------------------------------------

    private void OnHoverMoved(PointerEventArgs e)
    {
        if (_mode != ScriptEditorMode.Script) return;
        PointerPoint point = e.GetCurrentPoint(_editor.TextArea.TextView);
        if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed) return;
        if (_hoverPopup.IsOpen && Math.Abs(point.Position.X - _hoverPoint.X) + Math.Abs(point.Position.Y - _hoverPoint.Y) < 3) return;
        HideHover();
        _hoverPoint = point.Position;
        _hoverTimer.Start();
    }

    private void ShowHoverAtPointer()
    {
        _hoverTimer.Stop();
        if (OffsetAt(_hoverPoint) is { } offset) ShowHover(offset);
    }

    private void HideHover()
    {
        _hoverTimer.Stop();
        if (_hoverPopup.IsOpen) _hoverPopup.IsOpen = false;
    }

    private int? OffsetAt(Point point)
    {
        var textView = _editor.TextArea.TextView;
        TextViewPosition? position = textView.GetPosition(point + textView.ScrollOffset);
        if (position is not { } value) return null;
        return _editor.Document.GetOffset(value.Location);
    }

    // ---- popups ----------------------------------------------------------------------------------------------------------

    private void HidePopups()
    {
        HideCompletion();
        HideHover();
        _signaturePopup.IsOpen = false;
    }

    private void Place(Popup popup, int offset, bool above)
    {
        var textView = _editor.TextArea.TextView;
        Rect rect;
        try
        {
            TextLocation location = _editor.Document.GetLocation(Math.Clamp(offset, 0, _editor.Document.TextLength));
            var position = new TextViewPosition(location);
            Point top = textView.GetVisualPosition(position, AvaloniaEdit.Rendering.VisualYPosition.LineTop) - textView.ScrollOffset;
            Point bottom = textView.GetVisualPosition(position, AvaloniaEdit.Rendering.VisualYPosition.LineBottom) - textView.ScrollOffset;
            rect = new Rect(top.X, top.Y, 1, Math.Max(1, bottom.Y - top.Y));
            if (double.IsNaN(rect.X) || double.IsNaN(rect.Y)) rect = new Rect(0, 0, 1, 1);
        }
        catch (InvalidOperationException)
        {
            rect = new Rect(0, 0, 1, 1);
        }
        popup.PlacementTarget = textView;
        popup.Placement = PlacementMode.AnchorAndGravity;
        popup.PlacementRect = rect;
        popup.PlacementAnchor = above ? PopupAnchor.TopLeft : PopupAnchor.BottomLeft;
        popup.PlacementGravity = above ? PopupGravity.TopRight : PopupGravity.BottomRight;
    }

    private static Popup MakePopup(string name, Control content) => new()
    {
        Name = name,
        IsLightDismissEnabled = false,
        Focusable = false,
        Child = new Border
        {
            Child = content,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            [!Border.BackgroundProperty] = new DynamicResourceExtension(ThemeKeys.SurfaceBrush),
            [!Border.BorderBrushProperty] = new DynamicResourceExtension(ThemeKeys.BorderBrush),
            [!TextElement.ForegroundProperty] = new DynamicResourceExtension(ThemeKeys.TextBrush),
        },
    };

    private static string InlinesText(TextBlock block)
    {
        if (block.Inlines is null || block.Inlines.Count == 0) return block.Text ?? string.Empty;
        var builder = new System.Text.StringBuilder();
        foreach (Inline inline in block.Inlines)
        {
            if (inline is Run run) builder.Append(run.Text);
            else if (inline is LineBreak) builder.Append('\n');
        }
        return builder.ToString();
    }

    // ---- find and go to line bars ------------------------------------------------------------------------------------------

    private FindOptions FindOptions() => new() { CaseSensitive = _caseSensitive.IsChecked == true, WholeWord = _wholeWord.IsChecked == true };

    private (Border, TextBox, TextBox, TextBlock, CheckBox, CheckBox, Button, Button, TextBlock) BuildFindBar()
    {
        var findBox = new TextBox { Name = "FindBox", MinWidth = 160 };
        var replaceBox = new TextBox { Name = "ReplaceBox", MinWidth = 160 };
        var replaceLabel = new TextBlock { Text = "Replace:", VerticalAlignment = VerticalAlignment.Center };
        var wholeWord = new CheckBox { Name = "FullWords", Content = "Full words only" };
        var caseSensitive = new CheckBox { Name = "CaseSensitive", Content = "Case sensitive" };
        var close = new Button { Name = "FindCloseButton", Content = "Close" };
        var previous = new Button { Name = "FindPrevButton", Content = "Prev" };
        var next = new Button { Name = "FindNextButton", Content = "Next" };
        var replace = new Button { Name = "ReplaceButton", Content = "Replace" };
        var all = new Button { Name = "ReplaceAllButton", Content = "All" };
        var status = new TextBlock { Name = "FindStatus", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
        close.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => HideFind(), "find close");
        previous.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => FindNext(true), "find previous");
        next.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => FindNext(), "find next");
        replace.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => ReplaceNext(), "replace");
        all.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => ReplaceAll(), "replace all");
        findBox.KeyDown += UiSafe.Handler<KeyEventArgs>((_, e) =>
        {
            if (e.Key == Key.Enter) { FindNext((e.KeyModifiers & KeyModifiers.Shift) != 0); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideFind(); e.Handled = true; }
        }, "find key");
        replaceBox.KeyDown += UiSafe.Handler<KeyEventArgs>((_, e) =>
        {
            if (e.Key == Key.Enter) { ReplaceNext(); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideFind(); e.Handled = true; }
        }, "replace key");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(6, 4) };
        void Put(Control control, int row, int column, int span = 1)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            Grid.SetColumnSpan(control, span);
            grid.Children.Add(control);
        }
        Put(new TextBlock { Text = "Find:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, 0, 0);
        Put(findBox, 0, 1);
        replaceLabel.Margin = new Thickness(0, 4, 6, 0);
        replaceBox.Margin = new Thickness(0, 4, 0, 0);
        Put(replaceLabel, 1, 0);
        Put(replaceBox, 1, 1);
        Put(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 4, 0, 0), Children = { wholeWord, caseSensitive, status } }, 2, 1);
        Put(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, Children = { close, previous, next, replace, all } }, 0, 2);
        var bar = new Border
        {
            Name = "FindBar",
            IsVisible = false,
            Child = grid,
            BorderThickness = new Thickness(0, 0, 0, 1),
            [!Border.BackgroundProperty] = new DynamicResourceExtension(ThemeKeys.SurfaceBrush),
            [!Border.BorderBrushProperty] = new DynamicResourceExtension(ThemeKeys.BorderBrush),
        };
        return (bar, findBox, replaceBox, replaceLabel, wholeWord, caseSensitive, replace, all, status);
    }

    private (Border, TextBox) BuildGoToBar()
    {
        var box = new TextBox { Name = "LineBox", Width = 120 };
        var go = new Button { Name = "GoButton", Content = "Go" };
        var close = new Button { Name = "GoCloseButton", Content = "Close" };
        void Go()
        {
            int line = GoToLineParse(box.Text);
            _goToBar.IsVisible = false;
            GoToLine(line);
        }
        go.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Go(), "go to line");
        close.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => { _goToBar.IsVisible = false; _editor.TextArea.Focus(); }, "go to line close");
        box.KeyDown += UiSafe.Handler<KeyEventArgs>((_, e) =>
        {
            if (e.Key == Key.Enter) { Go(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _goToBar.IsVisible = false; _editor.TextArea.Focus(); e.Handled = true; }
        }, "go to line key");
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(6, 4),
            Children = { new TextBlock { Text = "Go to line:", VerticalAlignment = VerticalAlignment.Center }, box, go, close },
        };
        var bar = new Border
        {
            Name = "GoToLineBar",
            IsVisible = false,
            Child = panel,
            BorderThickness = new Thickness(0, 0, 0, 1),
            [!Border.BackgroundProperty] = new DynamicResourceExtension(ThemeKeys.SurfaceBrush),
            [!Border.BorderBrushProperty] = new DynamicResourceExtension(ThemeKeys.BorderBrush),
        };
        return (bar, box);
    }

    private int GoToLineParse(string? input) => GoToLine_Parse(input, _editor.Document.LineCount);

    private static int GoToLine_Parse(string? input, int lineCount) => RemoteControl.Scripting.GoToLine.Parse(input, lineCount);

    private DispatcherTimer Timer(int milliseconds, Action action, string context)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            UiSafe.Run(action, context);
        };
        return timer;
    }
}
