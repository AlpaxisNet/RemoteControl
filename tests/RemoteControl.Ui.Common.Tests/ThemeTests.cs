using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using RemoteControl.Core;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Common.Tests;

public sealed class ThemeTests
{
    public static IEnumerable<object[]> ThemeIds => ThemeManager.ThemeIds.Select(id => new object[] { id });

    private static Color BrushColor(object? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static Color Resource(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out object? value), key);
        return value switch
        {
            Color color => color,
            _ => BrushColor(value),
        };
    }

    [AvaloniaTheory]
    [MemberData(nameof(ThemeIds))]
    public void Every_theme_applies_and_gives_its_palette(string id)
    {
        using var env = new TestEnvironment(theme: id);
        ThemeManager theme = env.Services.Theme;
        Assert.Equal(id, theme.ThemeId);
        ThemePalette palette = theme.Palette;
        if (id != "system") Assert.Equal(ThemeManager.PaletteFor(id), palette);
        Assert.Equal(palette.Background, Resource(ThemeKeys.BackgroundBrush));
        Assert.Equal(palette.Accent, Resource(ThemeKeys.AccentColor));
        Assert.Equal(theme.ChatColors.ChatBack, Resource(ThemeKeys.ChatBackBrush));

        var button = new Button { Name = "Probe", Content = "Probe" };
        var box = new TextBox { Name = "Box", Text = "text" };
        var window = env.Show(new RcWindow(env.Services) { Content = new StackPanel { Children = { button, box } } }, 200, 120);
        if (id == "system")
        {
            Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);
            return;
        }
        Assert.Equal(palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
        Assert.Equal(palette.Background, BrushColor(window.Background));
        Assert.Equal(palette.Text, BrushColor(window.Foreground));
        Assert.Equal(palette.Surface, BrushColor(button.Background));
        Assert.Equal(palette.Editor, BrushColor(box.Background));
        Assert.Equal(palette.Text, BrushColor(box.Foreground));
    }

    [AvaloniaFact]
    public void Unknown_ids_fall_back_to_dark()
    {
        Assert.Equal("dark", ThemeManager.Normalize("no-such-theme"));
        Assert.Equal("dark", ThemeManager.Normalize(null));
        Assert.Equal("nord", ThemeManager.Normalize(" NORD "));
        Assert.Equal(ThemeManager.PaletteFor("dark"), ThemeManager.PaletteFor("whatever"));
        using var env = new TestEnvironment(theme: "whatever");
        Assert.Equal("dark", env.Services.Theme.ThemeId);
        Assert.Equal(Color.Parse("#454545"), env.Services.Theme.Palette.Background);
    }

    [Fact]
    public void The_palettes_are_those_of_the_cpp()
    {
        ThemePalette catppuccin = ThemeManager.PaletteFor("catppuccin");
        Assert.Equal(Color.Parse("#1e1e2e"), catppuccin.Background);
        Assert.Equal(Color.Parse("#313244"), catppuccin.Surface);
        Assert.Equal(Color.Parse("#1e1e2e"), catppuccin.Editor);
        Assert.Equal(Color.Parse("#cdd6f4"), catppuccin.Text);
        Assert.Equal(Color.Parse("#cba6f7"), catppuccin.Accent);
        Assert.Equal(Color.Parse("#585b70"), catppuccin.Border);
        ThemePalette monokai = ThemeManager.PaletteFor("monokai");
        Assert.Equal(Color.Parse("#272822"), monokai.Background);
        Assert.Equal(Color.Parse("#1e1f1c"), monokai.Editor);
        Assert.False(ThemeManager.PaletteFor("light").IsDark);
        Assert.Equal(13, ThemeManager.ThemeIds.Count);
        Assert.Equal("Solarized Dark", ThemeManager.DisplayName("solarized"));
        Assert.Equal("System (OS theme)", ThemeManager.DisplayName("system"));
    }

    [Fact]
    public void Every_palette_has_readable_text()
    {
        foreach (ThemePalette palette in ThemeManager.Palettes)
        {
            Assert.True(Pixels.Contrast(palette.Text, palette.Background) >= 4.0, palette.Id + " text on background");
            Assert.True(Pixels.Contrast(palette.Text, palette.Surface) >= 3.5, palette.Id + " text on surface");
            Assert.True(Pixels.Contrast(palette.Text, palette.Editor) >= 4.0, palette.Id + " text on editor");
            Assert.True(Pixels.Contrast(palette.OnAccent, palette.Accent) >= 3.0, palette.Id + " text on accent");
        }
    }

    [AvaloniaFact]
    public void Changing_the_option_rethemes_an_open_window()
    {
        using var env = new TestEnvironment(theme: "dark");
        var button = new Button { Content = "Probe" };
        var window = env.Show(new RcWindow(env.Services) { Content = button }, 200, 100);
        int changes = 0;
        env.Services.Theme.Changed += (_, _) => changes++;
        Assert.Equal(Color.Parse("#454545"), BrushColor(window.Background));

        env.SetOptions(o => o with { Theme = "nord" });

        Assert.Equal(1, changes);
        Assert.Equal("nord", env.Services.Theme.ThemeId);
        Assert.Equal(Color.Parse("#2e3440"), BrushColor(window.Background));
        Assert.Equal(Color.Parse("#3b4252"), BrushColor(button.Background));
        using var frame = Shots.Capture(window);
        Pixels.AssertClose(Color.Parse("#2e3440"), Pixels.At(frame, 2, 2));

        env.SetOptions(o => o with { Theme = "light" });
        Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
        Assert.Equal(ThemeManager.PaletteFor("light").Background, BrushColor(window.Background));
    }

    [AvaloniaFact]
    public void Options_that_do_not_concern_the_theme_do_not_reapply_it()
    {
        using var env = new TestEnvironment(theme: "dark");
        int changes = 0;
        env.Services.Theme.Changed += (_, _) => changes++;
        env.SetOptions(o => o with { AfkTimeout = 30, Nickname = "someone" });
        Assert.Equal(0, changes);
        env.SetOptions(o => o with { ColorChatBold = "#123456" });
        Assert.Equal(1, changes);
        Assert.Equal(Color.Parse("#123456"), Resource(ThemeKeys.ChatBoldBrush));
    }

    [AvaloniaFact]
    public void Rounded_corners_follow_the_option()
    {
        using var env = new TestEnvironment(theme: "dracula");
        Assert.True(Application.Current!.TryGetResource("ControlCornerRadius", null, out object? on));
        Assert.Equal(new CornerRadius(4), on);
        env.SetOptions(o => o with { RoundedCorners = false });
        Assert.False(env.Services.Theme.RoundedCorners);
        Assert.True(Application.Current!.TryGetResource("ControlCornerRadius", null, out object? off));
        Assert.Equal(new CornerRadius(0), off);
        Assert.True(Application.Current!.TryGetResource(ThemeKeys.OverlayCornerRadius, null, out object? overlay));
        Assert.Equal(new CornerRadius(0), overlay);
    }

    [AvaloniaFact]
    public void Choosing_a_theme_with_synccolors_writes_the_chat_colours()
    {
        using var env = new TestEnvironment(theme: "dark", options: o => o with { SyncColors = true, SyncBackgroundTint = true });
        env.Services.Theme.ChooseTheme("gruvbox");
        TestUi.Settle();
        RcOptions options = env.Options;
        Assert.Equal("gruvbox", options.Theme);
        Assert.True(options.DarkMode);
        Assert.Equal("#ebdbb2", options.ColorChat);
        Assert.Equal("#fabd2f", options.ColorChatBold);
        Assert.Equal("#282828", options.ColorChatBack);
        Assert.Equal("#28282860", options.BackgroundTint);
        Assert.Equal(Color.Parse("#fabd2f"), env.Services.Theme.ChatColors.ChatBold);
        Assert.Equal(Color.Parse("#ebdbb2"), Resource(ThemeKeys.ChatBrush));

        env.SetOptions(o => o with { SyncColors = false });
        env.Services.Theme.ChooseTheme("light");
        TestUi.Settle();
        Assert.False(env.Options.DarkMode);
        Assert.Equal("#ebdbb2", env.Options.ColorChat);
    }

    [AvaloniaFact]
    public void Unreadable_colour_options_fall_back_to_the_palette()
    {
        using var env = new TestEnvironment(theme: "nord", options: o => o with { ColorChat = "not a colour", ColorChatBack = "", ColorAlert = "#zzz" });
        ChatColors colors = env.Services.Theme.ChatColors;
        Assert.Equal(Color.Parse("#eceff4"), colors.Chat);
        Assert.Equal(Color.Parse("#2e3440"), colors.ChatBack);
        Assert.Equal(Color.Parse("#f48771"), colors.Alert);
    }

    [AvaloniaFact]
    public void Chat_fonts_follow_the_options()
    {
        using var env = new TestEnvironment(options: o => o with { ChatFontSize = 12, ChatFontFamily = "Monospace" });
        Assert.Equal(16, env.Services.Theme.ChatFontSize, 3);
        Assert.Contains("DejaVu Sans Mono", env.Services.Theme.ChatFontFamily.FamilyNames);
        Assert.True(Application.Current!.TryGetResource(ThemeKeys.ChatFontSize, null, out object? size));
        Assert.Equal(16.0, (double)size!, 3);
        Assert.Equal(FontFamily.Default, ThemeFonts.FromOption("Sans"));
    }

    [AvaloniaFact]
    public void Disposing_removes_the_theme_from_the_application()
    {
        int before = Application.Current!.Resources.MergedDictionaries.Count;
        var env = new TestEnvironment(theme: "nord");
        Assert.Equal(before + 1, Application.Current.Resources.MergedDictionaries.Count);
        env.Dispose();
        Assert.Equal(before, Application.Current.Resources.MergedDictionaries.Count);
        Assert.False(env.Services.Theme.IsInstalled);
    }
}
