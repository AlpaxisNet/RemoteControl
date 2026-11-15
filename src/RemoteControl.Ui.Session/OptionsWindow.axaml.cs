using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using RemoteControl.Core;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Session;

/// <summary>
/// The RC options window of a session (<c>TOptionsWindow</c>, review section 9): every option on the tabs General, Files, Script, Format,
/// Custom and MCP. Every change goes at once to the one shared <see cref="OptionsStore"/>, so every window of every session follows it
/// (defect D12: the C++ edited a per-window copy and wrote it back whole). Toggles, lists, colours and numbers are taken when they change,
/// texts when the field is left or Enter is pressed (and when the window is closed). A control whose name is an option key is bound to
/// that key; the theme, syntax theme, hotkey and pictures have their own handling.
/// </summary>
internal sealed partial class OptionsWindow : RcWindow
{
    /// <summary>The choices of the away timeout (minutes), as the C++ combo offered them.</summary>
    internal static readonly int[] AwayTimeouts = [5, 15, 30, 60];

    private readonly SessionContext _context;
    private readonly Dictionary<string, Control> _fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _shownText = new(StringComparer.Ordinal);
    private bool _loading;

    /// <summary>For the XAML previewer only.</summary>
    public OptionsWindow() : this(null!)
    {
    }

    /// <summary>Creates the window.</summary>
    /// <param name="context">The session.</param>
    public OptionsWindow(SessionContext context) : base(context?.App)
    {
        InitializeComponent();
        _context = context!;
        if (context is null) return;
        HideOnClose = true;
        Title = string.IsNullOrEmpty(context.ServerName) ? "Options" : "Options - " + context.ServerName;
        McpLaunch.Text = OperatingSystem.IsWindows() ? "RemoteControl.exe --mcp" : "RemoteControl --mcp";
        FillChoices();
        foreach (ILogical logical in this.GetLogicalDescendants())
            if (logical is Control { Name: { Length: > 0 } name } control && RcOptions.IsKnownKey(name)) Wire(name, control);
        WireSpecial();
        Listen<OptionsChangedEventArgs>(h => context.App.Options.Changed += h, h => context.App.Options.Changed -= h, (_, e) =>
        {
            if (!ReferenceEquals(e.Source, this) || e.Contains(OptionKeys.Theme, OptionKeys.SyncSyntaxTheme, OptionKeys.SyncBackgroundTint)) Load();
        }, "Options.Changed");
        CloseButton.Click += UiSafe.Handler<RoutedEventArgs>((_, _) =>
        {
            CommitAll();
            HideAndRemember();
        }, "Options.Close");
        ActivityHook.Attach(this, context);
        Load();
    }

    /// <summary>The control of an option key (tests).</summary>
    internal Control? Field(string key) => _fields.TryGetValue(key, out Control? control) ? control : null;

    /// <summary>The option keys that have a control (tests).</summary>
    internal IReadOnlyCollection<string> FieldKeys => _fields.Keys;

    /// <summary>Re-reads the saved MCP approvals (the C++ did on every open, because the MCP server writes them too) and shows the window.</summary>
    internal void Open()
    {
        UiSafe.Run(() => _context.App.Options.Reload(this), "Options.Reload");
        Load();
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        UiSafe.Run(CommitAll, "Options.Closing");
        base.OnClosing(e);
    }

    // ---- Loading ---------------------------------------------------------------------------------------------------------

    private RcOptions Current => _context.App.Options.Current;

    private void FillChoices()
    {
        foreach (string id in ThemeManager.ThemeIds.OrderBy(ThemeManager.DisplayName, StringComparer.Ordinal))
            theme.Items.Add(new ComboBoxItem { Content = ThemeManager.DisplayName(id), Tag = id });
        foreach (int minutes in AwayTimeouts)
            afktimeout.Items.Add(new ComboBoxItem { Content = minutes == 60 ? "1 hour" : minutes.ToString(CultureInfo.InvariantCulture) + " minutes", Tag = minutes.ToString(CultureInfo.InvariantCulture) });
        List<string> fonts = SystemFontNames();
        foreach (ComboBox box in new[] { chatfontfamily, scriptfontfamily })
            foreach (string font in fonts) box.Items.Add(font);
        for (int index = 0; index < RcOptions.ButtonNames.Length; index++)
            ButtonPicker.Items.Add(new ComboBoxItem { Content = HeaderLayout.ToolTips[index] + " (" + RcOptions.ButtonNames[index] + ")", Tag = index });
        ButtonPicker.SelectedIndex = 0;
    }

    private static List<string> SystemFontNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "Sans", "Serif", "Monospace" };
        try
        {
            foreach (FontFamily family in FontManager.Current.SystemFonts) names.Add(family.Name);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            UiSafe.Log.Warn("The system fonts could not be listed.", exception);
        }
        return names.ToList();
    }

    /// <summary>Shows the current options in every control.</summary>
    internal void Load()
    {
        _loading = true;
        try
        {
            RcOptions options = Current;
            foreach ((string key, Control control) in _fields) Show(control, options.GetValue(key), key);
            syntaxtheme.IsEnabled = !options.SyncSyntaxTheme;
            backgroundtint.IsEnabled = !options.SyncBackgroundTint;
            ShowButtonPicture();
        }
        finally
        {
            _loading = false;
        }
    }

    private void Show(Control control, string value, string key)
    {
        switch (control)
        {
            case CheckBox box:
                box.IsChecked = value == "true";
                break;
            case TextBox box:
                // A field being edited keeps what the user typed; one that shows what was last saved follows the store.
                if (!box.IsFocused || key == OptionKeys.GlobalHotkey || box.Text == (_shownText.TryGetValue(key, out string? shown) ? shown : null)) box.Text = value;
                _shownText[key] = value;
                break;
            case NumericUpDown number:
                number.Value = decimal.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out decimal parsed) ? parsed : number.Minimum;
                break;
            case ColorField color:
                color.Value = value;
                break;
            case ComboBox combo:
                SelectInCombo(combo, value, key);
                break;
        }
    }

    private static void SelectInCombo(ComboBox combo, string value, string key)
    {
        for (int index = 0; index < combo.Items.Count; index++)
        {
            object? item = combo.Items[index];
            string? candidate = item is ComboBoxItem { Tag: string tag } ? tag : item as string;
            if (string.Equals(candidate, value, StringComparison.Ordinal))
            {
                combo.SelectedIndex = index;
                return;
            }
        }
        // A value the list does not have (an away timeout of 45 minutes, a font that is not installed) is added, not replaced (defect D19).
        if (key == OptionKeys.AfkTimeout) combo.Items.Add(new ComboBoxItem { Content = value + " minutes", Tag = value });
        else if (combo.Items.Count == 0 || combo.Items[0] is string) combo.Items.Add(value);
        else combo.Items.Add(new ComboBoxItem { Content = value, Tag = value });
        combo.SelectedIndex = combo.Items.Count - 1;
    }

    // ---- Wiring ----------------------------------------------------------------------------------------------------------

    private void Wire(string key, Control control)
    {
        _fields[key] = control;
        switch (control)
        {
            case CheckBox box:
                box.IsCheckedChanged += UiSafe.Handler<RoutedEventArgs>((_, _) => Set(key, box.IsChecked == true ? "true" : "false"), "Options." + key);
                break;
            case TextBox box when key != OptionKeys.GlobalHotkey:
                box.LostFocus += UiSafe.Handler<RoutedEventArgs>((_, _) => Commit(key, box), "Options." + key);
                box.AddHandler(KeyDownEvent, UiSafe.Handler<KeyEventArgs>((_, e) =>
                {
                    if (e.Key == Key.Enter) Commit(key, box);
                }, "Options." + key), RoutingStrategies.Tunnel);
                break;
            case NumericUpDown number:
                number.ValueChanged += UiSafe.Handler<NumericUpDownValueChangedEventArgs>((_, e) =>
                {
                    if (e.NewValue is { } value) Set(key, ((int)value).ToString(CultureInfo.InvariantCulture));
                }, "Options." + key);
                break;
            case ColorField color:
                color.ColorChanged += UiSafe.Handler((_, _) => Set(key, color.Value), "Options." + key);
                break;
            case ComboBox combo when key != OptionKeys.Theme:
                combo.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) =>
                {
                    string? value = combo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : combo.SelectedItem as string;
                    if (value is not null) Set(key, value);
                }, "Options." + key);
                break;
        }
    }

    private void WireSpecial()
    {
        theme.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) =>
        {
            if (!_loading && theme.SelectedItem is ComboBoxItem { Tag: string id }) ChooseTheme(id);
        }, "Options.theme");
        syncsyntaxtheme.IsCheckedChanged += UiSafe.Handler<RoutedEventArgs>((_, _) =>
        {
            if (_loading || syncsyntaxtheme.IsChecked != true) return;
            Update(options => options with { SyntaxTheme = SyncedSyntaxTheme(options.Theme) });
        }, "Options.syncsyntaxtheme.apply");
        syncbackgroundtint.IsCheckedChanged += UiSafe.Handler<RoutedEventArgs>((_, _) =>
        {
            if (_loading || syncbackgroundtint.IsChecked != true) return;
            Update(options => options with { BackgroundTint = ThemeOptionColors.TintFor(options.Theme) });
        }, "Options.syncbackgroundtint.apply");
        globalhotkey.AddHandler(KeyDownEvent, UiSafe.Handler<KeyEventArgs>((_, e) => OnHotkeyKey(e), "Options.Hotkey"), RoutingStrategies.Tunnel);
        HotkeyOk.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => SaveHotkey(), "Options.HotkeyOk");
        ResetApprovals.Click += UiSafe.Handler<RoutedEventArgs>((_, _) =>
            Update(options => options with { McpApproveWeapon = false, McpApproveClass = false, McpApproveNpc = false }), "Options.ResetApprovals");
        BrowseDownload.Click += Browse(async () => await FilePickers.PickFolderAsync(this, "Download folder", Existing(_context.App.Paths.DownloadDirectory(Current))).ConfigureAwait(true), downloadfolder, OptionKeys.DownloadFolder);
        BrowseWorkspace.Click += Browse(async () => await FilePickers.PickFolderAsync(this, "External editor workspace", Existing(_context.App.Paths.ExternalEditorDirectory(Current))).ConfigureAwait(true), externaleditorworkspace, OptionKeys.ExternalEditorWorkspace);
        BrowseEditor.Click += Browse(async () => await FilePickers.OpenFileAsync(this, "External editor executable").ConfigureAwait(true), externaleditorcommand, OptionKeys.ExternalEditorCommand);
        BrowseLog.Click += Browse(async () => await FilePickers.SaveFileAsync(this, "RC chat log", Path.GetFileName(_context.App.Paths.ChatLogFile(Current)), "txt").ConfigureAwait(true), chatlogfile, OptionKeys.ChatLogFile);
        BrowseAutocomplete.Click += Browse(async () => await FilePickers.OpenFileAsync(this, "Autocomplete source").ConfigureAwait(true), autocompletesource, OptionKeys.AutocompleteSource);
        BrowseBackground.Click += Browse(async () => ImageName(await FilePickers.OpenFileAsync(this, "Background image", FilePickers.Images, _context.App.Paths.ImagesDirectory).ConfigureAwait(true)), background, OptionKeys.Background);
        ButtonPicker.SelectionChanged += UiSafe.Handler<SelectionChangedEventArgs>((_, _) => ShowButtonPicture(), "Options.ButtonPicker");
        WireButtonPicture(ButtonNormal, pressed: false);
        WireButtonPicture(ButtonPressed, pressed: true);
        BrowseButtonNormal.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => BrowseButtonPictureAsync(false), "Options.BrowseButtonNormal");
        BrowseButtonPressed.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => BrowseButtonPictureAsync(true), "Options.BrowseButtonPressed");
        ViewAudit.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => ViewAuditLogAsync(), "Options.ViewAudit");
        ClearAudit.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => ClearAuditLogAsync(), "Options.ClearAudit");
    }

    private static string? Existing(string folder) => Directory.Exists(folder) ? folder : null;

    private EventHandler<RoutedEventArgs> Browse(Func<Task<string?>> pick, TextBox box, string key) =>
        UiSafe.AsyncHandler<RoutedEventArgs>(async (_, _) =>
        {
            string? chosen = await pick().ConfigureAwait(true);
            if (string.IsNullOrEmpty(chosen) || IsClosed) return;
            box.Text = chosen;
            Commit(key, box);
        }, "Options.Browse." + key);

    /// <summary>A picture inside the program's pictures folder is kept by its name, as the C++ options held it.</summary>
    private string? ImageName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        string folder = Path.GetFullPath(_context.App.Paths.ImagesDirectory);
        string full = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(full);
        return parent is not null && string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(folder), StringComparison.Ordinal) ? Path.GetFileName(full) : full;
    }

    // ---- Changing --------------------------------------------------------------------------------------------------------

    private void Commit(string key, TextBox box)
    {
        if (_loading || IsClosed) return;
        string text = box.Text ?? string.Empty;
        _shownText[key] = text;
        if (!string.Equals(Current.GetValue(key), text, StringComparison.Ordinal)) Set(key, text);
    }

    /// <summary>Takes every text field (Close, closing the window).</summary>
    internal void CommitAll()
    {
        if (_context is null || IsClosed) return;
        foreach ((string key, Control control) in _fields)
            if (control is TextBox box && key != OptionKeys.GlobalHotkey) Commit(key, box);
        CommitButtonPicture(ButtonNormal, false);
        CommitButtonPicture(ButtonPressed, true);
    }

    private void Set(string key, string value)
    {
        if (_loading || IsClosed) return;
        if (string.Equals(Current.GetValue(key), value, StringComparison.Ordinal)) return;
        UiSafe.Run(() => _context.App.Options.SetValue(key, value, this), "Options.Set " + key);
        if (key == OptionKeys.SyncSyntaxTheme) syntaxtheme.IsEnabled = value != "true";
        if (key == OptionKeys.SyncBackgroundTint) backgroundtint.IsEnabled = value != "true";
    }

    private void Update(Func<RcOptions, RcOptions> change) => UiSafe.Run(() => _context.App.Options.Update(change, this), "Options.Update");

    /// <summary>The syntax theme that goes with a UI theme (<c>dark</c> and <c>system</c> use the language-spec colours).</summary>
    internal static string SyncedSyntaxTheme(string theme) => theme is "dark" or "system" ? "language-spec" : theme;

    /// <summary>
    /// Chooses a theme (<c>applyThemeSelection</c>): with <c>synccolors</c> the seven chat colours, with <c>syncbackgroundtint</c> the tint and with
    /// <c>syncsyntaxtheme</c> the syntax theme follow it, in one change.
    /// </summary>
    internal void ChooseTheme(string id)
    {
        string normalized = ThemeManager.Normalize(id);
        Update(options =>
        {
            RcOptions next = options with { Theme = normalized, DarkMode = normalized == "system" ? options.DarkMode : normalized != "light" };
            if (next.SyncColors) next = ThemeOptionColors.Apply(next, normalized);
            if (next.SyncBackgroundTint) next = next with { BackgroundTint = ThemeOptionColors.TintFor(normalized) };
            if (next.SyncSyntaxTheme) next = next with { SyntaxTheme = SyncedSyntaxTheme(normalized) };
            return next;
        });
        Load();
    }

    // ---- Hotkey ----------------------------------------------------------------------------------------------------------

    private void OnHotkeyKey(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key is Key.Back or Key.Delete)
        {
            globalhotkey.Text = string.Empty;
            return;
        }
        if (e.Key == Key.Enter)
        {
            SaveHotkey();
            return;
        }
        if (e.Key == Key.Tab)
        {
            e.Handled = false;
            return;
        }
        string? text = HotkeyText(e.Key, e.KeyModifiers);
        if (text is not null) globalhotkey.Text = text;
    }

    /// <summary>The C++ text of a key combination (<c>onGlobalHotkeyKeyPress</c>), or null for a modifier alone.</summary>
    internal static string? HotkeyText(Key key, KeyModifiers modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None) return null;
        string name = key switch
        {
            >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
            >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1).ToString(CultureInfo.InvariantCulture),
            Key.Space => "space",
            Key.Escape => "Escape",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "Page_Up",
            Key.PageDown => "Page_Down",
            Key.Insert => "Insert",
            _ => key.ToString(),
        };
        string prefix = string.Empty;
        if (modifiers.HasFlag(KeyModifiers.Control)) prefix += "Ctrl+";
        if (modifiers.HasFlag(KeyModifiers.Alt)) prefix += "Alt+";
        if (modifiers.HasFlag(KeyModifiers.Shift)) prefix += "Shift+";
        if (modifiers.HasFlag(KeyModifiers.Meta)) prefix += "Super+";
        return prefix + name;
    }

    private void SaveHotkey()
    {
        Set(OptionKeys.GlobalHotkey, globalhotkey.Text ?? string.Empty);
        globalhotkey.Focus();
    }

    // ---- Button pictures -------------------------------------------------------------------------------------------------

    private int SelectedButton => ButtonPicker.SelectedItem is ComboBoxItem { Tag: int index } ? index : 0;

    private void ShowButtonPicture()
    {
        int index = SelectedButton;
        RcOptions options = Current;
        bool loading = _loading;
        _loading = true;
        try
        {
            ButtonNormal.Text = options.ButtonImages[index];
            ButtonPressed.Text = options.ButtonImagesPressed[index];
            ButtonNormalPreview.Source = _context.App.Assets.Get(options.ButtonImages[index]);
            ButtonPressedPreview.Source = _context.App.Assets.Get(_context.App.Assets.PressedImageName(options.ButtonImages[index], options.ButtonImagesPressed[index]));
        }
        finally
        {
            _loading = loading;
        }
    }

    private void WireButtonPicture(TextBox box, bool pressed)
    {
        box.LostFocus += UiSafe.Handler<RoutedEventArgs>((_, _) => CommitButtonPicture(box, pressed), "Options.ButtonPicture");
        box.AddHandler(KeyDownEvent, UiSafe.Handler<KeyEventArgs>((_, e) =>
        {
            if (e.Key == Key.Enter) CommitButtonPicture(box, pressed);
        }, "Options.ButtonPicture"), RoutingStrategies.Tunnel);
    }

    private void CommitButtonPicture(TextBox box, bool pressed)
    {
        if (_loading || IsClosed) return;
        string key = "icon" + RcOptions.ButtonNames[SelectedButton] + (pressed ? "pressed" : string.Empty);
        string text = (box.Text ?? string.Empty).Trim();
        if (text.Length == 0) return;
        Set(key, text);
        ShowButtonPicture();
    }

    private async Task BrowseButtonPictureAsync(bool pressed)
    {
        string? chosen = ImageName(await FilePickers.OpenFileAsync(this, "Button picture", FilePickers.Images, _context.App.Paths.ImagesDirectory).ConfigureAwait(true));
        if (string.IsNullOrEmpty(chosen) || IsClosed) return;
        TextBox box = pressed ? ButtonPressed : ButtonNormal;
        box.Text = chosen;
        CommitButtonPicture(box, pressed);
    }

    // ---- MCP audit log ---------------------------------------------------------------------------------------------------

    private async Task ViewAuditLogAsync()
    {
        string path = _context.App.Paths.McpAuditLogFile;
        string text = await Task.Run(() =>
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return "The audit log could not be read: " + exception.Message;
            }
        }).ConfigureAwait(true);
        if (IsClosed) return;
        if (text.Length == 0) text = "No MCP requests have been logged.";
        var window = new AuditLogWindow(_context.App, text);
        _context.Windows.Track(window);
        window.Show(this);
    }

    private async Task ClearAuditLogAsync()
    {
        string path = _context.App.Paths.McpAuditLogFile;
        await Task.Run(() =>
        {
            try
            {
                if (File.Exists(path)) File.WriteAllText(path, string.Empty);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _context.App.Log.Warn("The MCP audit log could not be cleared.", exception);
            }
        }).ConfigureAwait(true);
    }
}

/// <summary>The MCP audit log (700 by 420, read-only, Close); not modal (defect D6).</summary>
internal sealed class AuditLogWindow : RcWindow
{
    /// <summary>Creates the window.</summary>
    /// <param name="services">The program's services.</param>
    /// <param name="text">The log.</param>
    public AuditLogWindow(AppServices services, string text) : base(services)
    {
        Title = "MCP Audit Log";
        Width = 700;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable(ThemeKeys.BackgroundBrush));
        var box = new TextBox { Name = "AuditText", Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily(ThemeFonts.MonospaceFamilies) };
        var close = new Button { Name = "CloseButton", Content = "Close", MinWidth = 80, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        close.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Close(), "AuditLog.Close");
        var bottom = new Border { Padding = new Thickness(6), Child = close };
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(5), Children = { bottom, box } };
    }
}
