using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using RemoteControl.Core;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Common.Tests;

/// <summary>One window with every control in each theme; with RC_TEST_SHOTS set, a picture per theme.</summary>
public sealed class GalleryTests
{
    public static IEnumerable<object[]> ThemeIds => ThemeManager.ThemeIds.Select(id => new object[] { id });

    [AvaloniaTheory]
    [MemberData(nameof(ThemeIds))]
    public void Every_control_in_every_theme(string id)
    {
        using var env = new TestEnvironment(theme: id);
        env.Services.Theme.ChooseTheme(id);
        TestUi.Settle();
        var window = env.Show(BuildGallery(env.Services), 900, 860);
        window.Title = "Gallery - " + ThemeManager.DisplayName(id);
        TestUi.Settle();
        Shots.Save(window, "gallery-" + id);

        if (id != "system")
        {
            using var frame = Shots.Capture(window);
            Pixels.AssertClose(env.Services.Theme.Palette.Background, Pixels.At(frame, 895, 855));
        }
    }

    internal static RcWindow BuildGallery(AppServices services)
    {
        RcOptions options = services.Options.Current;
        var menu = new Menu
        {
            ItemsSource = new[]
            {
                new MenuItem { Header = "_File", ItemsSource = new object[] { new MenuItem { Header = "Open" }, new Separator(), new MenuItem { Header = "Exit" } } },
                new MenuItem { Header = "_Edit" },
                new MenuItem { Header = "_Help" },
            },
        };
        DockPanel.SetDock(menu, Dock.Top);

        var header = new Canvas { Height = 180, ClipToBounds = true };
        var background = new AnimatedImage { Width = 900, Height = 180 };
        background.ApplyBackgroundOptions(services.Paths, options);
        header.Children.Add(background);
        int[][] positions = [[5, 15], [5, 48], [38, 15], [71, 15], [794, 15], [827, 15], [860, 15], [860, 48]];
        for (int index = 0; index < positions.Length; index++)
        {
            var button = ImageButton.FromAssets(services.Assets, options.ButtonImages[index], options.ButtonImagesPressed[index]);
            ToolTip.SetTip(button, RcOptions.ButtonNames[index]);
            Canvas.SetLeft(button, positions[index][0]);
            Canvas.SetTop(button, positions[index][1]);
            header.Children.Add(button);
        }
        var serverLabel = new ShadowLabel { Text = options.LabelServers + " Alpaxis Test" };
        Canvas.SetLeft(serverLabel, 10);
        Canvas.SetTop(serverLabel, 90);
        var playersLabel = new ShadowLabel { Text = options.LabelPlayers + " 12" };
        Canvas.SetLeft(playersLabel, 10);
        Canvas.SetTop(playersLabel, 110);
        var npcLabel = new ShadowLabel { Text = "Connected to NPC-Server", Width = 900, TextAlignment = TextAlignment.Center };
        Canvas.SetTop(npcLabel, 130);
        header.Children.Add(serverLabel);
        header.Children.Add(playersLabel);
        header.Children.Add(npcLabel);
        DockPanel.SetDock(header, Dock.Top);

        var left = new StackPanel { Spacing = 6, Margin = new Thickness(8), Width = 300 };
        left.Children.Add(new TextBlock { Text = "Text block in the theme's text colour" });
        left.Children.Add(new TextBox { Text = "Text box" });
        left.Children.Add(new TextBox { Watermark = "Placeholder text" });
        left.Children.Add(new TextBox { Text = "password", PasswordChar = '●' });
        left.Children.Add(new TextBox { Text = "Disabled text box", IsEnabled = false });
        left.Children.Add(new ComboBox { ItemsSource = new[] { "Combo box item", "Second" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch });
        left.Children.Add(new CheckBox { Content = "Checked box", IsChecked = true });
        left.Children.Add(new CheckBox { Content = "Unchecked box", IsChecked = false });
        left.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new RadioButton { Content = "Radio on", IsChecked = true, GroupName = "g" }, new RadioButton { Content = "Radio off", GroupName = "g" } } });
        left.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new Button { Content = "Button" },
                new Button { Content = "Default", Classes = { "accent" } },
                new Button { Content = "Disabled", IsEnabled = false },
                new ToggleButton { Content = "Toggle", IsChecked = true },
            },
        });
        left.Children.Add(new Slider { Minimum = 0, Maximum = 100, Value = 40 });
        left.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = 65, Height = 6 });
        left.Children.Add(new ToggleSwitch { Content = "Toggle switch", IsChecked = true });
        left.Children.Add(new Expander { Header = "Expander", Content = new TextBlock { Text = "Inside" }, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch });
        DockPanel.SetDock(left, Dock.Left);

        var chat = new ChatView { Services = services };
        var now = new DateTime(2024, 1, 1, 21, 15, 0);
        chat.Append("Server: Welcome to the RC chat", new ChatAppendOptions { Time = now });
        chat.Append("Graal123: hello there Kappa, see www.example.com", new ChatAppendOptions { Time = now });
        chat.Append("#ALERT New PM from Bob", new ChatAppendOptions { Time = now });
        chat.Append("Ünïcödé 😀: émotes :v and PMNormal", new ChatAppendOptions { Time = now });
        chat.Append("A long line that wraps because it is much longer than the width of the chat view in this gallery window, it keeps going.", new ChatAppendOptions { Time = now });
        chat.Editor.SelectionStart = chat.Text.IndexOf("hello", StringComparison.Ordinal);
        chat.Editor.SelectionLength = 5;

        var list = new ListBox { ItemsSource = new[] { "Selected row", "Second row", "Third row", "Fourth row" }, SelectedIndex = 0 };
        var tree = new TreeView
        {
            ItemsSource = new[]
            {
                new TreeViewItem { Header = "levels", IsExpanded = true, ItemsSource = new[] { new TreeViewItem { Header = "start.nw", IsSelected = true }, new TreeViewItem { Header = "house.nw" } } },
                new TreeViewItem { Header = "weapons" },
            },
        };
        var presenter = new MenuFlyoutPresenter
        {
            ItemsSource = new object[] { new MenuItem { Header = "Context menu item" }, new MenuItem { Header = "Disabled item", IsEnabled = false }, new Separator(), new MenuItem { Header = "Last item" } },
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("*,Auto") };
        lists.Children.Add(list);
        Grid.SetColumn(tree, 1);
        lists.Children.Add(tree);
        Grid.SetRow(presenter, 1);
        lists.Children.Add(presenter);
        var scroller = new ScrollViewer { Content = new TextBlock { Text = string.Join("\n", Enumerable.Range(1, 60).Select(n => "Scrolling line " + n)) }, AllowAutoHide = false };

        var tabs = new TabControl
        {
            Margin = new Thickness(4),
            ItemsSource = new[]
            {
                new TabItem { Header = "RC Chat", Content = chat },
                new TabItem { Header = "Lists", Content = lists },
                new TabItem { Header = "Scroll", Content = scroller },
            },
            SelectedIndex = 0,
        };
        var chatEntry = new TextBox { Text = "chat entry in coloredit", Margin = new Thickness(4) };
        chatEntry[!TextBox.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(ThemeKeys.EditBrush);
        chatEntry[!TextBox.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(ThemeKeys.EditBackBrush);
        DockPanel.SetDock(chatEntry, Dock.Bottom);

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(menu);
        root.Children.Add(header);
        root.Children.Add(left);
        root.Children.Add(chatEntry);
        root.Children.Add(tabs);
        return new RcWindow(services) { Content = root };
    }

    [AvaloniaTheory]
    [InlineData("dracula")]
    [InlineData("light")]
    public void Popups_take_the_theme(string id)
    {
        using var env = new TestEnvironment(theme: id);
        ThemePalette palette = env.Services.Theme.Palette;
        var combo = new ComboBox { ItemsSource = new[] { "First item", "Second item", "Third item" }, SelectedIndex = 1, Width = 200, VerticalAlignment = VerticalAlignment.Top };
        var tipped = new Button { Content = "Has a tool tip", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var tip = new ToolTip { Content = "A tool tip in the theme" };
        ToolTip.SetTip(tipped, tip);
        var window = env.Show(new RcWindow(env.Services) { Content = new Grid { Children = { combo, tipped } } }, 320, 260);

        combo.IsDropDownOpen = true;
        TestUi.Settle();
        ComboBoxItem item = TestUi.FindAll<ComboBoxItem>(TestUi.TopLevelOf(combo)).First();
        Shots.Save(window, "popup-combobox-" + id);
        Point probe = item.TranslatePoint(new Point(item.Bounds.Width - 6, item.Bounds.Height / 2), window)!.Value;
        using (var frame = Shots.Capture(window)) Pixels.AssertClose(palette.Editor, Pixels.At(frame, (int)probe.X, (int)probe.Y), 12);
        combo.IsDropDownOpen = false;

        ToolTip.SetIsOpen(tipped, true);
        TestUi.Settle();
        Assert.Equal(palette.Surface, ((ISolidColorBrush)tip.Background!).Color);
        Assert.Equal(palette.Text, ((ISolidColorBrush)tip.Foreground!).Color);
        ToolTip.SetIsOpen(tipped, false);

        var menu = new ContextMenu { ItemsSource = new object[] { new MenuItem { Header = "Copy" }, new MenuItem { Header = "Select all" } } };
        combo.ContextMenu = menu;
        menu.Open(combo);
        TestUi.Settle();
        Shots.Save(TestUi.TopLevelOf(TestUi.FindAll<MenuItem>(menu).First()), "popup-contextmenu-" + id);
        Assert.Equal(palette.Surface, ((ISolidColorBrush)menu.Background!).Color);
        menu.Close();
    }

    [AvaloniaFact]
    public void The_other_tabs_in_one_theme()
    {
        using var env = new TestEnvironment(theme: "catppuccin");
        var window = env.Show(BuildGallery(env.Services), 900, 860);
        TabControl tabs = TestUi.First<TabControl>(window);
        tabs.SelectedIndex = 1;
        TestUi.Settle();
        Shots.Save(window, "gallery-lists-catppuccin");
        tabs.SelectedIndex = 2;
        TestUi.Settle();
        Shots.Save(window, "gallery-scroll-catppuccin");
        env.SetOptions(o => o with { Theme = "light" });
        tabs.SelectedIndex = 1;
        TestUi.Settle();
        Shots.Save(window, "gallery-lists-light");
    }
}
