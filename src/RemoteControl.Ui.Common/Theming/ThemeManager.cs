using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using RemoteControl.Core;

namespace RemoteControl.Ui.Common;

/// <summary>
/// The user-interface themes of the C++ (<c>TMain.cpp:492-562</c>), applied to the whole Avalonia application: the Fluent theme
/// variant, Fluent's colour palette, the brushes of buttons, text boxes, combo boxes, check boxes, menus, lists, trees, tabs, tool
/// tips, popups and scroll bars, and the <see cref="ThemeKeys"/> resources (palette, chat colours, fonts) windows bind to.
/// </summary>
/// <remarks>
/// <para>One instance per program (<see cref="AppServices.Theme"/>). <see cref="Install"/> applies the current options and then
/// follows <see cref="OptionsStore.Changed"/>: a change of the theme, the rounded corners, a colour or a font re-applies everything
/// and raises <see cref="Changed"/>. Open windows follow at once because every brush is looked up as a dynamic resource.</para>
/// <para>Theme ids: <c>dark</c>, <c>dracula</c>, <c>material</c>, <c>ayu-mirage</c>, <c>nord</c>, <c>monokai</c>, <c>one-dark</c>,
/// <c>tokyo-night</c>, <c>gruvbox</c>, <c>solarized</c>, <c>catppuccin</c>, <c>light</c> and <c>system</c>; any other id is <c>dark</c>.
/// <c>system</c> follows the operating system's light or dark mode with Fluent's own colours, as the C++ used the toolkit's colours.
/// <c>light</c> uses a light palette (the C++ used the toolkit's default light colours).</para>
/// <para>Must be used on the UI thread; an options change raised on another thread is posted to it.</para>
/// </remarks>
/// <example>
/// At start-up (normally done by <see cref="AppServices.Create"/>): <c>services.Theme.Install(Application.Current!);</c>
/// <para>In the options window: <c>services.Theme.ChooseTheme("nord", this);</c> (writes <c>theme</c>, <c>darkmode</c> and, with
/// <c>synccolors</c>, the chat colours).</para>
/// <para>In code that draws: <c>Color accent = services.Theme.Palette.Accent;</c>, <c>IBrush bold = services.Theme.Brush(ThemeKeys.ChatBoldBrush);</c></para>
/// </example>
public sealed class ThemeManager : IDisposable
{
    private static readonly ThemePalette[] s_palettes =
    [
        P("dark", "Dark", "#454545", "#383838", "#1e1e1e", "#dddddd", "#00ff00", "#555555", true),
        P("dracula", "Dracula", "#282a36", "#44475a", "#282a36", "#f8f8f2", "#bd93f9", "#6272a4", true),
        P("material", "Material", "#263238", "#37474f", "#263238", "#eeffff", "#80cbc4", "#546e7a", true),
        P("ayu-mirage", "Ayu Mirage", "#1f2430", "#242936", "#242936", "#cccac2", "#ffcc66", "#4b5263", true),
        P("nord", "Nord", "#2e3440", "#3b4252", "#2e3440", "#eceff4", "#88c0d0", "#4c566a", true),
        P("monokai", "Monokai", "#272822", "#30312b", "#1e1f1c", "#f8f8f2", "#a6e22e", "#75715e", true),
        P("one-dark", "One Dark", "#282c34", "#21252b", "#282c34", "#abb2bf", "#61afef", "#3e4451", true),
        P("tokyo-night", "Tokyo Night", "#1a1b26", "#24283b", "#1a1b26", "#c0caf5", "#7aa2f7", "#3b4261", true),
        P("gruvbox", "Gruvbox", "#282828", "#3c3836", "#282828", "#ebdbb2", "#fabd2f", "#665c54", true),
        P("solarized", "Solarized Dark", "#002b36", "#073642", "#002b36", "#839496", "#b58900", "#586e75", true),
        P("catppuccin", "Catppuccin Mocha", "#1e1e2e", "#313244", "#1e1e2e", "#cdd6f4", "#cba6f7", "#585b70", true),
        P("light", "Light", "#f6f5f4", "#e8e6e3", "#ffffff", "#202020", "#3584e4", "#c0bab4", false),
    ];

    /// <summary>The internal key of the colour of a row under the pointer.</summary>
    internal const string HoverBrush = "RcHoverBrush";

    /// <summary>The internal key of the background of a selected row.</summary>
    internal const string SelectedBrush = "RcSelectedBrush";

    /// <summary>The internal key of the background of a selected row under the pointer.</summary>
    internal const string SelectedHoverBrush = "RcSelectedHoverBrush";

    /// <summary>The internal key of the text of a selected row.</summary>
    internal const string SelectedTextBrush = "RcSelectedTextBrush";

    private const string EditorStylesUri = "avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml";

    private readonly OptionsStore _options;
    private readonly AppLog _log;
    private Application? _application;
    private ResourceDictionary? _resources;
    private Styles? _styles;
    private ColorPaletteResources? _darkPalette;
    private ColorPaletteResources? _lightPalette;
    private bool _disposed;

    /// <summary>Creates the manager; nothing is applied until <see cref="Install"/>.</summary>
    /// <param name="options">The shared options.</param>
    /// <param name="log">Where problems are logged; <see cref="AppLog.Default"/> when <c>null</c>.</param>
    public ThemeManager(OptionsStore options, AppLog? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? AppLog.Default;
        Palette = s_palettes[0];
        ChatColors = ChatColors.From(_options.Current, Palette);
    }

    /// <summary>Raised on the UI thread after the theme was applied again (theme, colour, corner or font options changed).</summary>
    public event EventHandler? Changed;

    /// <summary>Every theme id, in the order of the options window (<c>system</c> included).</summary>
    public static IReadOnlyList<string> ThemeIds { get; } =
        ["ayu-mirage", "catppuccin", "dark", "dracula", "gruvbox", "light", "material", "monokai", "nord", "one-dark", "solarized", "system", "tokyo-night"];

    /// <summary>The palettes of the themes that have colours (every id but <c>system</c>).</summary>
    public static IReadOnlyList<ThemePalette> Palettes => s_palettes;

    /// <summary>The theme manager installed last, for controls created without services (the previewer, tests); prefer <see cref="AppServices.Theme"/>.</summary>
    public static ThemeManager? Current { get; private set; }

    /// <summary>The normalized id of the applied theme.</summary>
    public string ThemeId { get; private set; } = "dark";

    /// <summary>The applied palette. For <c>system</c>, the <c>dark</c> or <c>light</c> palette matching the operating system.</summary>
    public ThemePalette Palette { get; private set; }

    /// <summary>The applied chat, label and entry colours.</summary>
    public ChatColors ChatColors { get; private set; }

    /// <summary>Whether the theme is dark.</summary>
    public bool IsDark => Palette.IsDark;

    /// <summary>Whether controls and popups have rounded corners (option <c>roundedcorners</c>).</summary>
    public bool RoundedCorners { get; private set; } = true;

    /// <summary>The Fluent variant requested from the application.</summary>
    public ThemeVariant Variant { get; private set; } = ThemeVariant.Dark;

    /// <summary>The chat font (option <c>chatfontfamily</c>).</summary>
    public FontFamily ChatFontFamily { get; private set; } = FontFamily.Default;

    /// <summary>The chat font size in pixels.</summary>
    public double ChatFontSize { get; private set; } = ThemeFonts.PointsToPixels(9);

    /// <summary>The script font (option <c>scriptfontfamily</c>).</summary>
    public FontFamily ScriptFontFamily { get; private set; } = new(ThemeFonts.MonospaceFamilies);

    /// <summary>The script font size in pixels.</summary>
    public double ScriptFontSize { get; private set; } = ThemeFonts.PointsToPixels(10);

    /// <summary>Whether <see cref="Install"/> was called (and <see cref="Dispose"/> was not).</summary>
    public bool IsInstalled => _application is not null;

    /// <summary>The id <paramref name="id"/> stands for: a known id as it is (case ignored), anything else <c>dark</c>.</summary>
    /// <param name="id">A theme id from the options.</param>
    /// <returns>A member of <see cref="ThemeIds"/>.</returns>
    public static string Normalize(string? id)
    {
        string value = (id ?? string.Empty).Trim().ToLowerInvariant();
        return ThemeIds.Contains(value) ? value : "dark";
    }

    /// <summary>The palette of a theme id; <c>system</c> and unknown ids give the <c>dark</c> palette.</summary>
    /// <param name="id">A theme id.</param>
    /// <returns>The palette.</returns>
    public static ThemePalette PaletteFor(string? id)
    {
        string normalized = Normalize(id);
        foreach (ThemePalette palette in s_palettes)
            if (palette.Id == normalized) return palette;
        return s_palettes[0];
    }

    /// <summary>The name the options window shows for a theme id.</summary>
    /// <param name="id">A theme id.</param>
    /// <returns>The display name, for example <c>Tokyo Night</c> or <c>System (OS theme)</c>.</returns>
    public static string DisplayName(string? id) => Normalize(id) == "system" ? "System (OS theme)" : PaletteFor(id).DisplayName;

    /// <summary>Applies the current options to <paramref name="application"/> and follows later option changes. Calling it again re-applies.</summary>
    /// <param name="application">The application, normally <see cref="Application.Current"/>.</param>
    public void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(_application, application))
        {
            if (_application is not null) Uninstall();
            _application = application;
            _options.Changed += OnOptionsChanged;
            if (application.PlatformSettings is { } platform) platform.ColorValuesChanged += OnPlatformColorsChanged;
        }
        Current = this;
        EnsureEditorStyles(application);
        Apply();
    }

    /// <summary>Applies the current options again (theme, colours, corners, fonts) and raises <see cref="Changed"/>. Never throws.</summary>
    public void Apply()
    {
        try
        {
            ApplyCore();
        }
        catch (Exception exception)
        {
            _log.Error("The theme could not be applied.", exception);
        }
        RaiseChanged();
    }

    /// <summary>
    /// Chooses a theme as the C++ options window did: writes <c>theme</c> and <c>darkmode</c> (every theme but <c>light</c> is dark),
    /// with <c>synccolors</c> the seven chat colours of the theme (<see cref="ThemeOptionColors"/>), and with <c>syncbackgroundtint</c> the
    /// background tint. The options change then re-applies the theme.
    /// </summary>
    /// <param name="id">A theme id (normalized first).</param>
    /// <param name="source">The window making the change, passed on in <see cref="OptionsChangedEventArgs.Source"/>.</param>
    /// <returns>What the options store did.</returns>
    public OptionsSaveStatus ChooseTheme(string id, object? source = null)
    {
        string theme = Normalize(id);
        return _options.Update(options =>
        {
            RcOptions next = options with { Theme = theme, DarkMode = theme != "light" };
            if (next.SyncColors) next = ThemeOptionColors.Apply(next, theme);
            if (next.SyncBackgroundTint) next = next with { BackgroundTint = ThemeOptionColors.TintFor(theme) };
            return next;
        }, source);
    }

    /// <summary>A brush of the applied theme by its <see cref="ThemeKeys"/> key.</summary>
    /// <param name="key">A <c>...Brush</c> key of <see cref="ThemeKeys"/>.</param>
    /// <returns>The brush, or a transparent brush for an unknown key.</returns>
    public IBrush Brush(string key) => _resources is not null && _resources.TryGetValue(key, out object? value) && value is IBrush brush ? brush : Brushes.Transparent;

    /// <summary>Stops following the options and removes the theme's resources and styles from the application.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>Whether a change of <paramref name="args"/> concerns the theme.</summary>
    internal static bool Concerns(OptionsChangedEventArgs args) => args.Contains(
        OptionKeys.Theme, OptionKeys.DarkMode, OptionKeys.RoundedCorners, OptionKeys.ChatFontFamily, OptionKeys.ChatFontSize,
        OptionKeys.ScriptFontFamily, OptionKeys.ScriptFontSize, OptionKeys.ColorChat, OptionKeys.ColorChatBack, OptionKeys.ColorChatBold,
        OptionKeys.ColorAlert, OptionKeys.ColorLink, OptionKeys.ColorLabel, OptionKeys.ColorLabelBack, OptionKeys.ColorEdit, OptionKeys.ColorEditBack);

    private void Uninstall()
    {
        _options.Changed -= OnOptionsChanged;
        Application? application = _application;
        _application = null;
        if (application is null) return;
        if (application.PlatformSettings is { } platform) platform.ColorValuesChanged -= OnPlatformColorsChanged;
        try
        {
            if (_resources is not null) application.Resources.MergedDictionaries.Remove(_resources);
            if (_styles is not null) application.Styles.Remove(_styles);
            FluentTheme? fluent = application.Styles.OfType<FluentTheme>().FirstOrDefault();
            if (fluent is not null)
            {
                if (_darkPalette is not null && fluent.Palettes.TryGetValue(ThemeVariant.Dark, out ColorPaletteResources? dark) && ReferenceEquals(dark, _darkPalette)) fluent.Palettes.Remove(ThemeVariant.Dark);
                if (_lightPalette is not null && fluent.Palettes.TryGetValue(ThemeVariant.Light, out ColorPaletteResources? light) && ReferenceEquals(light, _lightPalette)) fluent.Palettes.Remove(ThemeVariant.Light);
            }
        }
        catch (Exception exception)
        {
            _log.Warn("The theme could not be removed from the application.", exception);
        }
        _resources = null;
        _styles = null;
        _darkPalette = null;
        _lightPalette = null;
    }

    private void OnOptionsChanged(object? sender, OptionsChangedEventArgs args)
    {
        if (!Concerns(args)) return;
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(() => { if (!_disposed) Apply(); });
    }

    private void OnPlatformColorsChanged(object? sender, PlatformColorValues values)
    {
        if (ThemeId != "system") return;
        Dispatcher.UIThread.Post(() => { if (!_disposed) Apply(); });
    }

    private void RaiseChanged()
    {
        EventHandler? handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                _log.Error("A theme change handler failed.", exception);
            }
        }
    }

    private void ApplyCore()
    {
        RcOptions options = _options.Current;
        string id = Normalize(options.Theme);
        bool system = id == "system";
        ThemePalette palette = system ? PaletteFor(SystemIsDark() ? "dark" : "light") : PaletteFor(id);
        ThemeId = id;
        Palette = palette;
        ChatColors = ChatColors.From(options, palette);
        RoundedCorners = options.RoundedCorners;
        Variant = system ? ThemeVariant.Default : palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        ChatFontFamily = ThemeFonts.FromOption(options.ChatFontFamily);
        ChatFontSize = ThemeFonts.PointsToPixels(options.ChatFontSize);
        ScriptFontFamily = ThemeFonts.FromOption(options.ScriptFontFamily);
        ScriptFontSize = ThemeFonts.PointsToPixels(options.ScriptFontSize);

        Application? application = _application;
        if (application is null) return;

        ResourceDictionary resources = BuildResources(palette, ChatColors, !system);
        if (_resources is null) application.Resources.MergedDictionaries.Add(resources);
        else
        {
            int index = application.Resources.MergedDictionaries.IndexOf(_resources);
            if (index >= 0) application.Resources.MergedDictionaries[index] = resources;
            else application.Resources.MergedDictionaries.Add(resources);
        }
        _resources = resources;

        _styles ??= BuildStyles();
        bool hasStyles = application.Styles.Contains(_styles);
        if (system && hasStyles) application.Styles.Remove(_styles);
        else if (!system && !hasStyles) application.Styles.Add(_styles);

        ApplyFluentPalettes(application, palette, system);
        application.RequestedThemeVariant = Variant;
    }

    private bool SystemIsDark()
    {
        try
        {
            PlatformColorValues? values = _application?.PlatformSettings?.GetColorValues();
            return values is null || values.ThemeVariant == PlatformThemeVariant.Dark;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void ApplyFluentPalettes(Application application, ThemePalette palette, bool system)
    {
        FluentTheme? fluent = application.Styles.OfType<FluentTheme>().FirstOrDefault();
        if (fluent is null) return;
        if (system)
        {
            if (_darkPalette is not null) fluent.Palettes.Remove(ThemeVariant.Dark);
            if (_lightPalette is not null) fluent.Palettes.Remove(ThemeVariant.Light);
            _darkPalette = null;
            _lightPalette = null;
            return;
        }
        ThemeVariant variant = palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        ColorPaletteResources target = palette.IsDark ? (_darkPalette ??= new ColorPaletteResources()) : (_lightPalette ??= new ColorPaletteResources());
        Fill(target, palette);
        if (!fluent.Palettes.TryGetValue(variant, out ColorPaletteResources? existing) || !ReferenceEquals(existing, target)) fluent.Palettes[variant] = target;
    }

    private static void Fill(ColorPaletteResources target, ThemePalette p)
    {
        Color text = p.Text;
        Color back = p.Background;
        target.Accent = p.Accent;
        target.RegionColor = back;
        target.AltHigh = back;
        target.AltMediumHigh = ThemePalette.WithAlpha(back, 0.8);
        target.AltMedium = ThemePalette.WithAlpha(back, 0.6);
        target.AltMediumLow = ThemePalette.WithAlpha(back, 0.4);
        target.AltLow = ThemePalette.WithAlpha(back, 0.2);
        target.BaseHigh = text;
        target.BaseMediumHigh = ThemePalette.WithAlpha(text, 0.8);
        target.BaseMedium = ThemePalette.WithAlpha(text, 0.6);
        target.BaseMediumLow = ThemePalette.WithAlpha(text, 0.4);
        target.BaseLow = ThemePalette.WithAlpha(text, 0.2);
        target.ChromeAltLow = text;
        target.ChromeBlackHigh = Colors.Black;
        target.ChromeBlackMedium = ThemePalette.WithAlpha(Colors.Black, 0.8);
        target.ChromeBlackMediumLow = ThemePalette.WithAlpha(Colors.Black, 0.6);
        target.ChromeBlackLow = ThemePalette.WithAlpha(Colors.Black, 0.2);
        target.ChromeDisabledHigh = p.Border;
        target.ChromeDisabledLow = p.DisabledText;
        target.ChromeGray = ThemePalette.Mix(p.Border, text, 0.4);
        target.ChromeHigh = p.Border;
        target.ChromeLow = p.Editor;
        target.ChromeMedium = back;
        target.ChromeMediumLow = p.Surface;
        target.ChromeWhite = p.IsDark ? Colors.White : text;
        target.ListLow = ThemePalette.WithAlpha(text, 0.1);
        target.ListMedium = ThemePalette.WithAlpha(text, 0.2);
    }

    private ResourceDictionary BuildResources(ThemePalette p, ChatColors chat, bool fluentOverrides)
    {
        var resources = new ResourceDictionary();
        void Brush(string key, Color color) => resources[key] = new ImmutableSolidColorBrush(color);

        resources[ThemeKeys.BackgroundColor] = p.Background;
        resources[ThemeKeys.SurfaceColor] = p.Surface;
        resources[ThemeKeys.EditorColor] = p.Editor;
        resources[ThemeKeys.TextColor] = p.Text;
        resources[ThemeKeys.AccentColor] = p.Accent;
        resources[ThemeKeys.BorderColor] = p.Border;
        Brush(ThemeKeys.BackgroundBrush, p.Background);
        Brush(ThemeKeys.SurfaceBrush, p.Surface);
        Brush(ThemeKeys.EditorBrush, p.Editor);
        Brush(ThemeKeys.TextBrush, p.Text);
        Brush(ThemeKeys.AccentBrush, p.Accent);
        Brush(ThemeKeys.OnAccentBrush, p.OnAccent);
        Brush(ThemeKeys.BorderBrush, p.Border);
        Brush(ThemeKeys.DisabledTextBrush, p.DisabledText);
        Brush(HoverBrush, p.Hover);
        Brush(SelectedBrush, p.IsDark ? p.Surface : ThemePalette.Mix(p.Editor, p.Accent, 0.25));
        Brush(SelectedHoverBrush, p.IsDark ? ThemePalette.Mix(p.Surface, p.Text, 0.08) : ThemePalette.Mix(p.Editor, p.Accent, 0.35));
        Brush(SelectedTextBrush, p.SelectedText);

        Brush(ThemeKeys.ChatBrush, chat.Chat);
        Brush(ThemeKeys.ChatBackBrush, chat.ChatBack);
        Brush(ThemeKeys.ChatBoldBrush, chat.ChatBold);
        Brush(ThemeKeys.AlertBrush, chat.Alert);
        Brush(ThemeKeys.LinkBrush, chat.Link);
        Brush(ThemeKeys.LabelBrush, chat.Label);
        Brush(ThemeKeys.LabelBackBrush, chat.LabelBack);
        Brush(ThemeKeys.EditBrush, chat.Edit);
        Brush(ThemeKeys.EditBackBrush, chat.EditBack);
        resources[ThemeKeys.ChatFontFamily] = ChatFontFamily;
        resources[ThemeKeys.ChatFontSize] = ChatFontSize;
        resources[ThemeKeys.ScriptFontFamily] = ScriptFontFamily;
        resources[ThemeKeys.ScriptFontSize] = ScriptFontSize;

        var controlRadius = new CornerRadius(RoundedCorners ? 4 : 0);
        var overlayRadius = new CornerRadius(RoundedCorners ? 8 : 0);
        resources[ThemeKeys.ControlCornerRadius] = controlRadius;
        resources[ThemeKeys.OverlayCornerRadius] = overlayRadius;
        resources["ControlCornerRadius"] = controlRadius;
        resources["OverlayCornerRadius"] = overlayRadius;

        if (fluentOverrides) AddFluentOverrides(resources, p);
        return resources;
    }

    /// <summary>The control brushes of the Fluent theme, set from the palette as the C++ CSS styled the GTK widgets.</summary>
    private static void AddFluentOverrides(ResourceDictionary r, ThemePalette p)
    {
        void B(string key, Color color) => r[key] = new ImmutableSolidColorBrush(color);
        Color transparent = Colors.Transparent;
        Color text = p.Text;
        Color hoverSurface = ThemePalette.Mix(p.Surface, text, 0.08);
        Color strongBorder = ThemePalette.Mix(p.Border, text, 0.25);
        Color boxStroke = ThemePalette.Mix(p.Border, text, 0.45);
        Color accentHover = ThemePalette.Mix(p.Accent, text, 0.15);
        Color selected = p.IsDark ? p.Surface : ThemePalette.Mix(p.Editor, p.Accent, 0.25);
        Color selectedHover = p.IsDark ? hoverSurface : ThemePalette.Mix(p.Editor, p.Accent, 0.35);

        foreach (string prefix in new[] { "Button", "RepeatButton", "ToggleButton", "SplitButton" })
        {
            B(prefix + "Background", p.Surface);
            B(prefix + "BackgroundPointerOver", hoverSurface);
            B(prefix + "BackgroundPressed", p.Editor);
            B(prefix + "BackgroundDisabled", p.Surface);
            B(prefix + "BorderBrush", p.Border);
            B(prefix + "BorderBrushPointerOver", strongBorder);
            B(prefix + "BorderBrushPressed", p.Border);
            B(prefix + "BorderBrushDisabled", p.Border);
            B(prefix + "Foreground", text);
            B(prefix + "ForegroundPointerOver", text);
            B(prefix + "ForegroundPressed", text);
            B(prefix + "ForegroundDisabled", p.DisabledText);
        }
        foreach (string prefix in new[] { "ToggleButton", "SplitButton" })
        {
            B(prefix + "BackgroundChecked", p.Accent);
            B(prefix + "BackgroundCheckedPointerOver", accentHover);
            B(prefix + "BackgroundCheckedPressed", p.Accent);
            B(prefix + "BackgroundCheckedDisabled", p.Surface);
            B(prefix + "BorderBrushChecked", p.Accent);
            B(prefix + "BorderBrushCheckedPointerOver", accentHover);
            B(prefix + "BorderBrushCheckedPressed", p.Accent);
            B(prefix + "BorderBrushCheckedDisabled", p.Border);
            B(prefix + "ForegroundChecked", p.OnAccent);
            B(prefix + "ForegroundCheckedPointerOver", p.OnAccent);
            B(prefix + "ForegroundCheckedPressed", p.OnAccent);
            B(prefix + "ForegroundCheckedDisabled", p.DisabledText);
        }
        B("AccentButtonBackground", p.Accent);
        B("AccentButtonBackgroundPointerOver", accentHover);
        B("AccentButtonBackgroundPressed", ThemePalette.Mix(p.Accent, p.Editor, 0.2));
        B("AccentButtonBackgroundDisabled", p.Surface);
        B("AccentButtonForeground", p.OnAccent);
        B("AccentButtonForegroundPointerOver", p.OnAccent);
        B("AccentButtonForegroundPressed", p.OnAccent);
        B("AccentButtonForegroundDisabled", p.DisabledText);
        B("AccentButtonBorderBrush", p.Accent);
        B("AccentButtonBorderBrushPointerOver", accentHover);
        B("AccentButtonBorderBrushPressed", p.Accent);
        B("AccentButtonBorderBrushDisabled", p.Border);
        B("ToggleButtonBackgroundIndeterminate", p.Surface);
        B("ToggleButtonForegroundIndeterminate", text);
        B("HyperlinkButtonForeground", p.Accent);
        B("HyperlinkButtonForegroundPointerOver", accentHover);
        B("HyperlinkButtonForegroundPressed", p.Accent);
        B("HyperlinkButtonBackground", transparent);
        B("HyperlinkButtonBackgroundPointerOver", transparent);
        B("HyperlinkButtonBackgroundPressed", transparent);

        B("TextControlBackground", p.Editor);
        B("TextControlBackgroundPointerOver", p.Editor);
        B("TextControlBackgroundFocused", p.Editor);
        B("TextControlBackgroundDisabled", p.Surface);
        B("TextControlBorderBrush", p.Border);
        B("TextControlBorderBrushPointerOver", strongBorder);
        B("TextControlBorderBrushFocused", p.Accent);
        B("TextControlBorderBrushDisabled", p.Border);
        B("TextControlForeground", text);
        B("TextControlForegroundPointerOver", text);
        B("TextControlForegroundFocused", text);
        B("TextControlForegroundDisabled", p.DisabledText);
        B("TextControlPlaceholderForeground", p.DisabledText);
        B("TextControlPlaceholderForegroundPointerOver", p.DisabledText);
        B("TextControlPlaceholderForegroundFocused", p.DisabledText);
        B("TextControlPlaceholderForegroundDisabled", p.DisabledText);
        B("TextControlSelectionHighlightColor", ThemePalette.WithAlpha(p.Accent, 0.45));
        B("TextControlButtonBackground", transparent);
        B("TextControlButtonBackgroundPointerOver", p.Surface);
        B("TextControlButtonBackgroundPressed", p.Accent);
        B("TextControlButtonBorderBrush", transparent);
        B("TextControlButtonBorderBrushPointerOver", transparent);
        B("TextControlButtonBorderBrushPressed", transparent);
        B("TextControlButtonForeground", text);
        B("TextControlButtonForegroundPointerOver", text);
        B("TextControlButtonForegroundPressed", p.OnAccent);

        B("ComboBoxBackground", p.Surface);
        B("ComboBoxBackgroundPointerOver", hoverSurface);
        B("ComboBoxBackgroundPressed", p.Editor);
        B("ComboBoxBackgroundDisabled", p.Surface);
        B("ComboBoxBackgroundUnfocused", p.Surface);
        B("ComboBoxBorderBrush", p.Border);
        B("ComboBoxBorderBrushPointerOver", strongBorder);
        B("ComboBoxBorderBrushPressed", p.Border);
        B("ComboBoxBorderBrushDisabled", p.Border);
        B("ComboBoxBackgroundBorderBrushFocused", p.Accent);
        B("ComboBoxBackgroundBorderBrushUnfocused", p.Border);
        B("ComboBoxForeground", text);
        B("ComboBoxForegroundDisabled", p.DisabledText);
        B("ComboBoxForegroundFocused", text);
        B("ComboBoxForegroundFocusedPressed", text);
        B("ComboBoxPlaceHolderForeground", p.DisabledText);
        B("ComboBoxDropDownBackground", p.Editor);
        B("ComboBoxDropDownBorderBrush", p.Border);
        B("ComboBoxDropDownGlyphForeground", text);
        B("ComboBoxDropDownGlyphForegroundDisabled", p.DisabledText);
        B("ComboBoxDropDownGlyphForegroundFocused", text);
        B("ComboBoxDropDownGlyphForegroundFocusedPressed", text);
        B("ComboBoxItemBackground", transparent);
        B("ComboBoxItemBackgroundPointerOver", p.Hover);
        B("ComboBoxItemBackgroundPressed", selected);
        B("ComboBoxItemBackgroundSelected", selected);
        B("ComboBoxItemBackgroundSelectedPointerOver", selectedHover);
        B("ComboBoxItemBackgroundSelectedPressed", selected);
        B("ComboBoxItemBackgroundDisabled", transparent);
        B("ComboBoxItemForeground", text);
        B("ComboBoxItemForegroundPointerOver", text);
        B("ComboBoxItemForegroundPressed", p.SelectedText);
        B("ComboBoxItemForegroundSelected", p.SelectedText);
        B("ComboBoxItemForegroundSelectedPointerOver", p.SelectedText);
        B("ComboBoxItemForegroundSelectedPressed", p.SelectedText);
        B("ComboBoxItemForegroundDisabled", p.DisabledText);

        foreach (string state in new[] { "Unchecked", "Checked", "Indeterminate" })
        {
            bool on = state != "Unchecked";
            foreach (string suffix in new[] { string.Empty, "PointerOver", "Pressed", "Disabled" })
            {
                bool disabled = suffix == "Disabled";
                B("CheckBoxForeground" + state + suffix, disabled ? p.DisabledText : text);
                B("CheckBoxBackground" + state + suffix, transparent);
                B("CheckBoxBorderBrush" + state + suffix, transparent);
                Color fill = on ? (disabled ? p.Border : suffix == "PointerOver" ? accentHover : p.Accent) : (suffix == "PointerOver" ? p.Hover : suffix == "Pressed" ? p.Surface : p.Editor);
                B("CheckBoxCheckBackgroundFill" + state + suffix, fill);
                B("CheckBoxCheckBackgroundStroke" + state + suffix, on ? fill : disabled ? p.Border : boxStroke);
                B("CheckBoxCheckGlyphForeground" + state + suffix, on ? (disabled ? p.DisabledText : p.OnAccent) : transparent);
            }
        }
        foreach (string suffix in new[] { string.Empty, "PointerOver", "Pressed", "Disabled" })
        {
            bool disabled = suffix == "Disabled";
            B("RadioButtonForeground" + suffix, disabled ? p.DisabledText : text);
            B("RadioButtonBackground" + suffix, transparent);
            B("RadioButtonBorderBrush" + suffix, transparent);
            B("RadioButtonOuterEllipseFill" + suffix, suffix == "PointerOver" ? p.Hover : p.Editor);
            B("RadioButtonOuterEllipseStroke" + suffix, disabled ? p.Border : boxStroke);
            B("RadioButtonOuterEllipseCheckedFill" + suffix, disabled ? p.Border : suffix == "PointerOver" ? accentHover : p.Accent);
            B("RadioButtonOuterEllipseCheckedStroke" + suffix, disabled ? p.Border : suffix == "PointerOver" ? accentHover : p.Accent);
            B("RadioButtonCheckGlyphFill" + suffix, disabled ? p.DisabledText : p.OnAccent);
            B("RadioButtonCheckGlyphStroke" + suffix, disabled ? p.DisabledText : p.OnAccent);
        }

        B("ToggleSwitchFillOn", p.Accent);
        B("ToggleSwitchFillOnPointerOver", accentHover);
        B("ToggleSwitchFillOnPressed", p.Accent);
        B("ToggleSwitchStrokeOn", p.Accent);
        B("ToggleSwitchKnobFillOn", p.OnAccent);
        B("ToggleSwitchKnobFillOnPointerOver", p.OnAccent);
        B("ToggleSwitchKnobFillOnPressed", p.OnAccent);
        B("ToggleSwitchFillOff", p.Editor);
        B("ToggleSwitchFillOffPointerOver", p.Hover);
        B("ToggleSwitchFillOffPressed", p.Surface);
        B("ToggleSwitchStrokeOff", boxStroke);
        B("ToggleSwitchStrokeOffPointerOver", text);
        B("ToggleSwitchKnobFillOff", text);
        B("ToggleSwitchKnobFillOffPointerOver", text);
        B("ToggleSwitchContentForeground", text);

        B("TabItemHeaderBackgroundUnselected", transparent);
        B("TabItemHeaderBackgroundUnselectedPointerOver", p.Surface);
        B("TabItemHeaderBackgroundUnselectedPressed", p.Surface);
        B("TabItemHeaderBackgroundSelected", transparent);
        B("TabItemHeaderBackgroundSelectedPointerOver", p.Surface);
        B("TabItemHeaderBackgroundSelectedPressed", p.Surface);
        B("TabItemHeaderBackgroundDisabled", transparent);
        Color dimText = ThemePalette.Mix(text, p.Background, 0.35);
        B("TabItemHeaderForegroundUnselected", dimText);
        B("TabItemHeaderForegroundUnselectedPointerOver", text);
        B("TabItemHeaderForegroundUnselectedPressed", text);
        B("TabItemHeaderForegroundSelected", text);
        B("TabItemHeaderForegroundSelectedPointerOver", text);
        B("TabItemHeaderForegroundSelectedPressed", text);
        B("TabItemHeaderForegroundDisabled", p.DisabledText);
        B("TabItemHeaderSelectedPipeFill", p.Accent);
        r["TabItemHeaderFontSize"] = 15.0;
        r["TabItemMinHeight"] = 34.0;
        B("TabControlBackground", transparent);

        foreach (string suffix in new[] { string.Empty, "PointerOver", "Pressed", "Selected", "SelectedPointerOver", "SelectedPressed", "Disabled", "SelectedDisabled" })
        {
            Color background = suffix switch
            {
                "PointerOver" => p.Hover,
                "Pressed" or "Selected" or "SelectedPressed" => selected,
                "SelectedPointerOver" => selectedHover,
                "SelectedDisabled" => p.Surface,
                _ => transparent,
            };
            B("TreeViewItemBackground" + suffix, background);
            B("TreeViewItemBorderBrush" + suffix, transparent);
            B("TreeViewItemForeground" + suffix, suffix.Contains("Disabled", StringComparison.Ordinal) ? p.DisabledText : suffix.StartsWith("Selected", StringComparison.Ordinal) ? p.SelectedText : text);
        }

        B("MenuFlyoutPresenterBackground", p.Surface);
        B("MenuFlyoutPresenterBorderBrush", p.Border);
        B("MenuFlyoutItemBackground", transparent);
        B("MenuFlyoutItemBackgroundPointerOver", ThemePalette.Mix(p.Surface, text, 0.12));
        B("MenuFlyoutItemBackgroundPressed", p.Border);
        B("MenuFlyoutItemBackgroundDisabled", transparent);
        B("MenuFlyoutItemForeground", text);
        B("MenuFlyoutItemForegroundPointerOver", text);
        B("MenuFlyoutItemForegroundPressed", text);
        B("MenuFlyoutItemForegroundDisabled", p.DisabledText);
        B("MenuFlyoutItemKeyboardAcceleratorTextForeground", ThemePalette.Mix(text, p.Surface, 0.35));
        B("MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", text);
        B("MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", text);
        B("MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", p.DisabledText);
        B("MenuFlyoutSubItemChevron", text);
        B("MenuFlyoutSubItemChevronPointerOver", text);
        B("MenuFlyoutSubItemChevronPressed", text);
        B("MenuFlyoutSubItemChevronSubMenuOpened", text);
        B("MenuFlyoutSubItemChevronDisabled", p.DisabledText);
        B("FlyoutPresenterBackground", p.Surface);
        B("FlyoutBorderThemeBrush", p.Border);

        B("ToolTipBackground", p.Surface);
        B("ToolTipForeground", text);
        B("ToolTipBorderBrush", p.Border);

        r["ScrollBarThumbBackgroundColor"] = ThemePalette.Mix(p.Border, text, 0.2);
        r["ScrollBarPanningThumbBackgroundColor"] = ThemePalette.Mix(p.Border, text, 0.2);
        B("ScrollBarPanningThumbBackground", ThemePalette.Mix(p.Border, text, 0.2));
        B("ScrollBarThumbFillPointerOver", ThemePalette.Mix(p.Border, text, 0.45));
        B("ScrollBarThumbFillPressed", p.Accent);
        B("ScrollBarThumbFillDisabled", p.Border);
        B("ScrollBarTrackFill", p.Editor);
        B("ScrollBarTrackFillPointerOver", p.Editor);
        B("ScrollBarTrackStroke", transparent);
        B("ScrollBarTrackStrokePointerOver", transparent);
        B("ScrollBarBackground", transparent);
        B("ScrollBarBackgroundPointerOver", transparent);
        B("ScrollBarForeground", text);
        B("ScrollBarButtonBackground", transparent);
        B("ScrollBarButtonBackgroundPointerOver", p.Surface);
        B("ScrollBarButtonBackgroundPressed", p.Border);
        B("ScrollBarButtonArrowForeground", ThemePalette.Mix(text, p.Editor, 0.3));
        B("ScrollBarButtonArrowForegroundPointerOver", text);
        B("ScrollBarButtonArrowForegroundPressed", text);
        B("ScrollBarButtonArrowForegroundDisabled", p.DisabledText);
        B("ScrollViewerScrollBarsSeparatorBackground", transparent);

        B("ExpanderHeaderBackground", p.Surface);
        B("ExpanderHeaderBackgroundPointerOver", hoverSurface);
        B("ExpanderHeaderBackgroundPressed", p.Editor);
        B("ExpanderHeaderBorderBrush", p.Border);
        B("ExpanderHeaderBorderBrushPointerOver", strongBorder);
        B("ExpanderHeaderBorderBrushPressed", p.Border);
        B("ExpanderHeaderForeground", text);
        B("ExpanderHeaderForegroundPointerOver", text);
        B("ExpanderHeaderForegroundPressed", text);
        B("ExpanderChevronForeground", text);
        B("ExpanderChevronForegroundPointerOver", text);
        B("ExpanderChevronForegroundPressed", text);
        B("ExpanderChevronBackground", transparent);
        B("ExpanderChevronBackgroundPointerOver", transparent);
        B("ExpanderChevronBackgroundPressed", transparent);
        B("ExpanderChevronBorderBrush", transparent);
        B("ExpanderChevronBorderBrushPointerOver", transparent);
        B("ExpanderChevronBorderBrushPressed", transparent);
        B("ExpanderContentBackground", p.Background);
        B("ExpanderContentBorderBrush", p.Border);

        B("SliderTrackFill", p.Border);
        B("SliderTrackFillPointerOver", strongBorder);
        B("SliderTrackFillPressed", p.Border);
        B("SliderTrackValueFill", p.Accent);
        B("SliderTrackValueFillPointerOver", accentHover);
        B("SliderTrackValueFillPressed", p.Accent);
        B("SliderThumbBackground", p.Accent);
        B("SliderThumbBackgroundPointerOver", accentHover);
        B("SliderThumbBackgroundPressed", p.Accent);

        B("AutoCompleteBoxSuggestionsListBackground", p.Editor);
        B("AutoCompleteBoxSuggestionsListBorderBrush", p.Border);
    }

    /// <summary>The styles the resource keys cannot reach: window colours, list and tree backgrounds, list selection, menu bar, separators.</summary>
    private static Styles BuildStyles()
    {
        static Setter Dynamic(AvaloniaProperty property, string key) => new(property, new DynamicResourceExtension(key));
        var styles = new Styles
        {
            new Style(x => x.OfType<Window>())
            {
                Setters = { Dynamic(TemplatedControl.BackgroundProperty, ThemeKeys.BackgroundBrush), Dynamic(TemplatedControl.ForegroundProperty, ThemeKeys.TextBrush) },
            },
            new Style(x => x.OfType<ListBox>())
            {
                Setters = { Dynamic(TemplatedControl.BackgroundProperty, ThemeKeys.EditorBrush), Dynamic(TemplatedControl.ForegroundProperty, ThemeKeys.TextBrush), Dynamic(TemplatedControl.BorderBrushProperty, ThemeKeys.BorderBrush) },
            },
            new Style(x => x.OfType<TreeView>())
            {
                Setters = { Dynamic(TemplatedControl.BackgroundProperty, ThemeKeys.EditorBrush), Dynamic(TemplatedControl.ForegroundProperty, ThemeKeys.TextBrush), Dynamic(TemplatedControl.BorderBrushProperty, ThemeKeys.BorderBrush) },
            },
            new Style(x => x.OfType<ListBoxItem>().Class(":pointerover").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { Dynamic(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, HoverBrush), Dynamic(Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, ThemeKeys.TextBrush) },
            },
            new Style(x => x.OfType<ListBoxItem>().Class(":selected").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { Dynamic(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, SelectedBrush), Dynamic(Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, SelectedTextBrush) },
            },
            new Style(x => x.OfType<ListBoxItem>().Class(":selected").Class(":pointerover").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { Dynamic(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, SelectedHoverBrush), Dynamic(Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, SelectedTextBrush) },
            },
            new Style(x => x.OfType<ListBoxItem>().Class(":selected").Class(":pressed").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { Dynamic(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, SelectedBrush), Dynamic(Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, SelectedTextBrush) },
            },
            new Style(x => x.OfType<Menu>())
            {
                Setters = { Dynamic(TemplatedControl.BackgroundProperty, ThemeKeys.SurfaceBrush), Dynamic(TemplatedControl.ForegroundProperty, ThemeKeys.TextBrush) },
            },
            new Style(x => x.OfType<Separator>())
            {
                Setters = { Dynamic(TemplatedControl.BackgroundProperty, ThemeKeys.BorderBrush) },
            },
            new Style(x => x.OfType<GridSplitter>())
            {
                Setters = { Dynamic(TemplatedControl.BackgroundProperty, ThemeKeys.BorderBrush) },
            },
        };
        return styles;
    }

    /// <summary>Adds the AvaloniaEdit Fluent styles (used by <see cref="ChatView"/> and the script editors) unless the application already has them.</summary>
    private void EnsureEditorStyles(Application application)
    {
        try
        {
            foreach (IStyle style in application.Styles)
                if (style is StyleInclude include && include.Source?.OriginalString == EditorStylesUri) return;
            application.Styles.Add(new StyleInclude(new Uri("avares://RemoteControl.Ui.Common/")) { Source = new Uri(EditorStylesUri) });
        }
        catch (Exception exception)
        {
            _log.Warn("The editor styles could not be added.", exception);
        }
    }

    private static ThemePalette P(string id, string name, string background, string surface, string editor, string text, string accent, string border, bool dark) =>
        new(id, name, Color.Parse(background), Color.Parse(surface), Color.Parse(editor), Color.Parse(text), Color.Parse(accent), Color.Parse(border), dark);
}
