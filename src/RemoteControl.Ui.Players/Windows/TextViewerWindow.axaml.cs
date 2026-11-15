using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>
/// A read-only monospace text with a Find button (and Ctrl+F), a line status and Close: the PM history ("History: account - nick",
/// <c>onPMHistory</c>) and the ban history / staff activity windows (<c>handleBanListData</c>). Header lines are bold magenta
/// (<c>#ff00ff</c> on dark themes, <c>#a000a0</c> on light ones), as in the C++.
/// </summary>
internal sealed partial class TextViewerWindow : RcWindow
{
    private readonly TextEditor _editor;
    private readonly SearchPanel _search;

    /// <summary>For the designer.</summary>
    public TextViewerWindow() : this(null, "Text", string.Empty, _ => false)
    {
    }

    /// <summary>Creates the window.</summary>
    /// <param name="services">The application services.</param>
    /// <param name="title">The title.</param>
    /// <param name="text">The text.</param>
    /// <param name="isHeader">Which lines are headers.</param>
    public TextViewerWindow(AppServices? services, string title, string text, Func<string, bool> isHeader) : base(services)
    {
        InitializeComponent();
        Title = title;
        IsHeader = isHeader;
        _editor = new TextEditor
        {
            Name = "ViewerText",
            IsReadOnly = true,
            ShowLineNumbers = false,
            WordWrap = false,
            Document = new TextDocument(text ?? string.Empty),
            Padding = new Thickness(4, 2),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _editor.Document.UndoStack.SizeLimit = 0;
        _editor.Options.HighlightCurrentLine = false;
        _editor.Options.EnableHyperlinks = false;
        _editor[!TemplatedControl.FontFamilyProperty] = new DynamicResourceExtension(ThemeKeys.ScriptFontFamily);
        _editor[!TemplatedControl.FontSizeProperty] = new DynamicResourceExtension(ThemeKeys.ScriptFontSize);
        _editor[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension(ThemeKeys.EditorBrush);
        _editor[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension(ThemeKeys.TextBrush);
        bool dark = services?.Theme.IsDark ?? true;
        _editor.TextArea.TextView.LineTransformers.Add(new HeaderColorizer(this, new SolidColorBrush(dark ? Color.FromRgb(0xff, 0x00, 0xff) : Color.FromRgb(0xa0, 0x00, 0xa0))));
        _search = SearchPanel.Install(_editor);
        _editor.TextArea.Caret.PositionChanged += UiSafe.Handler((_, _) => UpdateStatus(), "TextViewer.Caret");
        EditorHost.Content = _editor;
        FindButton.Click += On.Click(OpenFind, "TextViewer.Find");
        CloseButton.Click += On.Click(Close, "TextViewer.Close");
        OnClosedOnce(() => _search.Uninstall());
    }

    /// <summary>Which lines are headers.</summary>
    public Func<string, bool> IsHeader { get; }

    /// <summary>The text shown.</summary>
    public string Text
    {
        get => _editor.Document.Text;
        set
        {
            _editor.Document.Text = value ?? string.Empty;
            UpdateStatus();
        }
    }

    /// <summary>The editor (for tests).</summary>
    internal TextEditor Editor => _editor;

    /// <summary>Whether the find bar is open.</summary>
    internal bool IsFindOpen => !_search.IsClosed;

    /// <summary>Opens the find bar.</summary>
    internal void OpenFind()
    {
        _search.Open();
        _search.Reactivate();
    }

    private void UpdateStatus()
    {
        var caret = _editor.TextArea.Caret;
        LineStatus.Text = "Line " + caret.Line + ", Column " + caret.Column;
    }

    private sealed class HeaderColorizer(TextViewerWindow owner, IBrush brush) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0) return;
            string text = CurrentContext.Document.GetText(line);
            if (!owner.IsHeader(text)) return;
            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(brush);
                Typeface current = element.TextRunProperties.Typeface;
                element.TextRunProperties.SetTypeface(new Typeface(current.FontFamily, current.Style, FontWeight.Bold, current.Stretch));
            });
        }
    }
}
