using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RemoteControl.Core;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Session.Tests;

public sealed class OptionsWindowTests
{
    /// <summary>The keys the C++ options window had no control for (review section 9, "Keys without UI").</summary>
    private static readonly HashSet<string> s_withoutUi =
    [
        OptionKeys.DontSavePassword, OptionKeys.GraphicalMenu, OptionKeys.DarkMode, OptionKeys.ShowBuddies, OptionKeys.WebBrowsers, OptionKeys.ColorLink,
        OptionKeys.ColorAlert, OptionKeys.TimestampFormat, OptionKeys.NoHtmlInPms, OptionKeys.NoHtmlImages, OptionKeys.ImportMissingFolder,
    ];

    private static OptionsWindow Show(TestSession test) => test.Show(new OptionsWindow(test.Context), 460, 520);

    private static void Press(Button button)
    {
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        TestUi.Settle();
    }

    /// <summary>Selects the tab a control is on (a control of another tab is not shown and cannot take the focus).</summary>
    private static void SelectTabOf(OptionsWindow window, Control control)
    {
        TabItem tab = Avalonia.LogicalTree.LogicalExtensions.GetLogicalAncestors(control).OfType<TabItem>().First();
        TestUi.Find<TabControl>(window, "OptionTabs").SelectedItem = tab;
        TestUi.Settle();
    }

    [AvaloniaFact]
    public void Every_option_of_the_cpp_window_has_a_control()
    {
        using var test = new TestSession();
        OptionsWindow window = Show(test);
        string[] missing = RcOptions.Keys.Where(key => !s_withoutUi.Contains(key) && !key.StartsWith("icon", StringComparison.Ordinal) && window.Field(key) is null).ToArray();
        Assert.Empty(missing);
        Assert.Equal("Options - Test Server", window.Title);
    }

    [AvaloniaFact]
    public void Every_option_round_trips_between_its_control_and_the_store()
    {
        using var test = new TestSession(options: o => o with { SyncColors = false, SyncSyntaxTheme = false, SyncBackgroundTint = false });
        OptionsWindow window = Show(test);
        foreach (string key in window.FieldKeys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (key is OptionKeys.Theme or OptionKeys.GlobalHotkey) continue;
            Control control = window.Field(key)!;
            SelectTabOf(window, control);
            string before = test.Environment.Options.GetValue(key);
            string expected = ChangeControl(window, control, key, before);
            Assert.True(expected == test.Environment.Options.GetValue(key), key + ": the store has '" + test.Environment.Options.GetValue(key) + "', expected '" + expected + "'");

            string outside = OutsideValue(control, key, expected);
            test.Environment.SetOptions(o => o.WithValue(key, outside));
            Assert.True(outside == Shown(control), key + ": the control shows '" + Shown(control) + "', expected '" + outside + "'");
        }
    }

    private static string ChangeControl(OptionsWindow window, Control control, string key, string before)
    {
        switch (control)
        {
            case CheckBox box:
                box.IsChecked = before != "true";
                TestUi.Settle();
                return before == "true" ? "false" : "true";
            case TextBox box:
                string text = "changed-" + key;
                box.Focus();
                box.Text = text;
                TestUi.PressKey(TopLevel.GetTopLevel(window)!, Key.Enter);
                return text;
            case NumericUpDown number:
                int value = (int)number.Minimum + 3;
                number.Value = value;
                TestUi.Settle();
                return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case ColorField color:
                string chosen = color.AllowAlpha ? "#12345678" : "#123456";
                color.Choose(chosen);
                return chosen;
            case ComboBox combo:
                int index = combo.SelectedIndex == 1 ? 2 : 1;
                combo.SelectedIndex = index;
                TestUi.Settle();
                return combo.Items[index] is ComboBoxItem { Tag: string tag } ? tag : (string)combo.Items[index]!;
            default:
                throw new InvalidOperationException(key);
        }
    }

    private static string OutsideValue(Control control, string key, string current) => control switch
    {
        CheckBox => current == "true" ? "false" : "true",
        TextBox => "outside-" + key,
        NumericUpDown number => ((int)number.Minimum + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ColorField color => color.AllowAlpha ? "#abcdef80" : "#abcdef",
        ComboBox combo => combo.Items[0] is ComboBoxItem { Tag: string tag } ? tag : (string)combo.Items[0]!,
        _ => throw new InvalidOperationException(key),
    };

    private static string Shown(Control control)
    {
        TestUi.Settle();
        return control switch
        {
            CheckBox box => box.IsChecked == true ? "true" : "false",
            TextBox box => box.Text ?? string.Empty,
            NumericUpDown number => ((int)(number.Value ?? 0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ColorField color => color.Value,
            ComboBox combo => combo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : combo.SelectedItem as string ?? string.Empty,
            _ => string.Empty,
        };
    }

    [AvaloniaFact]
    public void Choosing_a_theme_syncs_colours_tint_and_syntax_theme()
    {
        using var test = new TestSession(theme: "dark", options: o => o with { SyncColors = true, SyncSyntaxTheme = true, SyncBackgroundTint = true });
        OptionsWindow window = Show(test);
        var theme = (ComboBox)window.Field(OptionKeys.Theme)!;
        theme.SelectedItem = theme.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag! == "nord");
        TestUi.Settle();
        RcOptions options = test.Environment.Options;
        Assert.Equal("nord", options.Theme);
        Assert.Equal("#88c0d0", options.ColorChatBold);
        Assert.Equal("#2e3440", options.ColorChatBack);
        Assert.Equal("#2e344060", options.BackgroundTint);
        Assert.Equal("nord", options.SyntaxTheme);
        Assert.Equal("#88c0d0", ((ColorField)window.Field(OptionKeys.ColorChatBold)!).Value);
        Assert.False(window.Field(OptionKeys.SyntaxTheme)!.IsEnabled);
        Assert.False(window.Field(OptionKeys.BackgroundTint)!.IsEnabled);
        Shots.Save(window, "options-general-nord");

        ((CheckBox)window.Field(OptionKeys.SyncColors)!).IsChecked = false;
        theme.SelectedItem = theme.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag! == "dark");
        TestUi.Settle();
        Assert.Equal("language-spec", test.Environment.Options.SyntaxTheme);
        Assert.Equal("#88c0d0", test.Environment.Options.ColorChatBold);
        Assert.True(test.Environment.Options.DarkMode);
    }

    [AvaloniaFact]
    public void The_hotkey_field_captures_a_combination_and_ok_saves_it()
    {
        using var test = new TestSession();
        OptionsWindow window = Show(test);
        var hotkey = (TextBox)window.Field(OptionKeys.GlobalHotkey)!;
        hotkey.Focus();
        TestUi.PressKey(window, Key.R, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.Equal("Ctrl+Alt+r", hotkey.Text);
        Assert.Equal(string.Empty, test.Environment.Options.GlobalHotkey);
        TestUi.Click(TestUi.Find<Button>(window, "HotkeyOk"));
        Assert.Equal("Ctrl+Alt+r", test.Environment.Options.GlobalHotkey);
        hotkey.Focus();
        TestUi.PressKey(window, Key.Back);
        Assert.Equal(string.Empty, hotkey.Text);
        TestUi.PressKey(window, Key.Enter);
        Assert.Equal(string.Empty, test.Environment.Options.GlobalHotkey);
        Assert.Null(OptionsWindow.HotkeyText(Key.LeftShift, KeyModifiers.Shift));
        Assert.Equal("Super+F5", OptionsWindow.HotkeyText(Key.F5, KeyModifiers.Meta));
        Assert.Equal("Ctrl+Page_Up", OptionsWindow.HotkeyText(Key.PageUp, KeyModifiers.Control));
    }

    [AvaloniaFact]
    public void An_away_timeout_the_list_lacks_is_kept()
    {
        using var test = new TestSession(options: o => o with { AfkTimeout = 45 });
        OptionsWindow window = Show(test);
        var combo = (ComboBox)window.Field(OptionKeys.AfkTimeout)!;
        Assert.Equal("45", ((ComboBoxItem)combo.SelectedItem!).Tag);
        window.CommitAll();
        Assert.Equal(45, test.Environment.Options.AfkTimeout);
    }

    [AvaloniaFact]
    public void Button_pictures_and_approvals_are_saved()
    {
        using var test = new TestSession(options: o => o with { McpApproveWeapon = true, McpApproveNpc = true });
        OptionsWindow window = Show(test);
        var picker = TestUi.Find<ComboBox>(window, "ButtonPicker");
        SelectTabOf(window, picker);
        picker.SelectedIndex = 4;
        TestUi.Settle();
        TextBox normal = TestUi.Find<TextBox>(window, "ButtonNormal");
        Assert.Equal("rc_options_normal.png", normal.Text);
        normal.Focus();
        normal.Text = "rc_help_normal.png";
        TestUi.PressKey(window, Key.Enter);
        Assert.Equal("rc_help_normal.png", test.Environment.Options.ButtonImages[4]);
        Assert.Equal("rc_help_normal.png", test.Environment.Options.GetValue("iconoptions"));

        SelectTabOf(window, TestUi.Find<Button>(window, "ResetApprovals"));
        Press(TestUi.Find<Button>(window, "ResetApprovals"));
        Assert.False(test.Environment.Options.McpApproveWeapon);
        Assert.False(test.Environment.Options.McpApproveNpc);
    }

    [AvaloniaFact]
    public async Task The_audit_log_is_shown_and_cleared()
    {
        using var test = new TestSession();
        File.WriteAllText(test.Environment.Paths.McpAuditLogFile, "2026-11-15 tool=chat ok\n");
        OptionsWindow window = Show(test);
        SelectTabOf(window, window.Field(OptionKeys.McpEnabled)!);
        Press(TestUi.Find<Button>(window, "ViewAudit"));
        await TestUi.WaitUntilAsync(() => Common.RcWindow.OpenWindows.OfType<AuditLogWindow>().Any());
        AuditLogWindow log = Common.RcWindow.OpenWindows.OfType<AuditLogWindow>().Single();
        Assert.Contains("tool=chat", TestUi.Find<TextBox>(log, "AuditText").Text);
        log.Close();
        Press(TestUi.Find<Button>(window, "ClearAudit"));
        await TestUi.WaitUntilAsync(() => new FileInfo(test.Environment.Paths.McpAuditLogFile).Length == 0);
    }

    [AvaloniaTheory]
    [InlineData("catppuccin")]
    [InlineData("light")]
    public void Pictures_of_every_tab(string theme)
    {
        using var test = new TestSession(theme: theme);
        OptionsWindow window = Show(test);
        var tabs = TestUi.Find<TabControl>(window, "OptionTabs");
        string[] names = ["general", "files", "script", "format", "custom", "mcp"];
        for (int index = 0; index < names.Length; index++)
        {
            tabs.SelectedIndex = index;
            TestUi.Settle();
            Shots.Save(window, "options-" + names[index] + "-" + theme);
        }
    }
}
