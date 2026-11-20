namespace RemoteControl.Core.Tests;

public sealed class OptionsTests
{
    /// <summary>A legacy Windows configuration as the C++ test fixture <c>original/rc_win_dark</c> held it: a partial file.</summary>
    private const string LegacyWindowsConfig =
        "nickname=\nattachaway=true\nlogrcchat=false\ngraphicalmenu=true\nchatfontsize=9\nchatfontfamily=Sans\ncoloredit=#00ff00\n" +
        "labelservers=Server:\nlabelplayers=Players:\nbackground=missing_background.png\n";

    [Fact]
    public void Legacy_program_folder_config_loads_like_the_cpp_test()
    {
        using var install = new TestInstall();
        File.WriteAllText(install.Paths.BundledOptionsFile, LegacyWindowsConfig.Replace("\n", "\r\n"));
        var store = new OptionsStore(install.Paths);

        OptionsLoadResult result = store.Load();

        Assert.Equal(OptionsLoadStatus.CopiedFromProgramFolder, result.Status);
        RcOptions options = store.Current;
        Assert.Empty(options.Nickname);
        Assert.True(options.AttachAway);
        Assert.False(options.LogRcChat);
        Assert.True(options.GraphicalMenu);
        Assert.Equal(9, options.ChatFontSize);
        Assert.Equal("Sans", options.ChatFontFamily);
        Assert.True(options.SyntaxHighlighting);
        Assert.True(options.ScriptDiagnostics);
        Assert.Equal(2, options.ScriptTabWidth);
        Assert.Equal(10, options.ScriptFontSize);
        Assert.Equal("Monospace", options.ScriptFontFamily);
        Assert.False(options.FileBrowserThumbnails);
        Assert.False(options.ExtensionsEnabled);
        Assert.False(options.SyncEnabled);
        Assert.True(options.LevelListEnabled);
        Assert.True(options.AutoReconnectNc);
        Assert.Equal("rc_background.png", options.Background);
        Assert.Equal("[%I:%M %p]", options.TimestampFormat);
        Assert.Equal("rc_playerlist_normal.png", options.ButtonImages[0]);
        Assert.Equal("rc_npclist_normal.png", options.ButtonImagesPressed[11]);
        Assert.Equal("rc_help_pressed.png", options.ButtonImagesPressed[12]);
        Assert.Equal("#00ff00", options.ColorEdit);
        Assert.Equal("Server:", options.LabelServers);
        Assert.Equal("Players:", options.LabelPlayers);
        Assert.True(File.Exists(install.Paths.OptionsFile));
        Assert.Equal(LegacyWindowsConfig.Replace("\n", "\r\n"), File.ReadAllText(install.Paths.BundledOptionsFile));
    }

    [Fact]
    public void Save_and_load_round_trips_flags_and_font_families_with_spaces()
    {
        using var install = new TestInstall();
        var store = new OptionsStore(install.Paths);
        store.Load();
        Assert.Equal(OptionsSaveStatus.Saved, store.Update(o => o with
        {
            McpApproveWeapon = true, McpApproveClass = true, McpApproveNpc = true, McpServer = true, McpLogin = true, McpWindows = true, McpFullControl = true,
            AfkEnabled = true, AfkTimeout = 30,
            ModernFileBrowser = true, FileBrowserHoverPreview = false, FileBrowserThumbnails = false,
            ExtensionsEnabled = true, SyncEnabled = true, LevelListEnabled = false, AutoReconnectNc = false,
            ChatFontFamily = "Tempus Sans ITC Bold Italic", ScriptFontFamily = "Serif Italic",
        }));

        var restored = new OptionsStore(install.Paths);
        Assert.Equal(OptionsLoadStatus.Loaded, restored.Load().Status);
        RcOptions o = restored.Current;
        Assert.True(o.McpApproveWeapon && o.McpApproveClass && o.McpApproveNpc);
        Assert.True(o.McpServer && o.McpLogin && o.McpWindows && o.McpFullControl);
        Assert.True(o.AfkEnabled);
        Assert.Equal(30, o.AfkTimeout);
        Assert.True(o.ModernFileBrowser);
        Assert.False(o.FileBrowserHoverPreview || o.FileBrowserThumbnails);
        Assert.True(o.ExtensionsEnabled && o.SyncEnabled && !o.LevelListEnabled);
        Assert.False(o.AutoReconnectNc);
        Assert.Equal("Tempus Sans ITC Bold Italic", o.ChatFontFamily);
        Assert.Equal("Serif Italic", o.ScriptFontFamily);
        Assert.Equal(store.Current, o);
    }

    [Fact]
    public void User_folder_wins_over_program_folder_and_program_folder_is_never_written()
    {
        using var install = new TestInstall();
        File.WriteAllText(install.Paths.BundledOptionsFile, "chatfontfamily=Bundled\n");
        File.WriteAllText(install.Paths.OptionsFile, "chatfontfamily=User Font\n");
        var store = new OptionsStore(install.Paths);
        Assert.Equal(OptionsLoadStatus.Loaded, store.Load().Status);
        Assert.Equal("User Font", store.Current.ChatFontFamily);
        store.Update(o => o with { Nickname = "Changed" });
        Assert.Equal("chatfontfamily=Bundled\n", File.ReadAllText(install.Paths.BundledOptionsFile));
        Assert.Contains("nickname=Changed\n", File.ReadAllText(install.Paths.OptionsFile));
    }

    [Fact]
    public void No_file_anywhere_writes_the_defaults()
    {
        using var install = new TestInstall();
        var store = new OptionsStore(install.Paths);
        Assert.Equal(OptionsLoadStatus.CreatedDefaults, store.Load().Status);
        Assert.Equal(RcOptions.Defaults, store.Current);
        string text = File.ReadAllText(install.Paths.OptionsFile);
        Assert.StartsWith("nickname=\nnomassmessages=false\n", text);
        Assert.Contains("\nafktimeout=15\n", text);
        Assert.Contains("\nwebbrowsers=firefox,mozilla,konqueror,netscape\n", text);
        Assert.Contains("\niconhelppressed=rc_help_pressed.png\n", text);
        Assert.EndsWith("mcpfileroots=.\nmcpserverscope=\nmcpreferencefolders=\nimportmissingfolder=\n", text);
        Assert.Equal(RcOptions.Keys.Count, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Bundled_runtime_config_loads_with_every_key_known()
    {
        using var install = new TestInstall();
        File.Copy(RepositoryFiles.Find("assets", "runtime", "control2config.txt"), install.Paths.BundledOptionsFile);
        var store = new OptionsStore(install.Paths);
        Assert.Equal(OptionsLoadStatus.CopiedFromProgramFolder, store.Load().Status);
        RcOptions o = store.Current;
        Assert.Empty(store.UnknownEntries);
        Assert.Equal("dark", o.Theme);
        Assert.Equal("language-spec", o.SyntaxTheme);
        Assert.Equal("#00ff00", o.ColorEdit);
        Assert.Equal("#00C000", o.ColorChatBold);
        Assert.Equal("Connected to NPC-Server", o.LabelNpcServer);
        Assert.True(o.RemoveLineComments);
        Assert.True(o.RemoveBlockComments);
        Assert.Equal(["firefox", "mozilla", "konqueror", "netscape"], o.WebBrowsers.ToArray());
        Assert.Equal("rc_guiscripts_pressed.png", o.ButtonImagesPressed[14]);
    }

    [Fact]
    public void The_import_missing_folder_is_remembered_and_an_older_file_without_it_still_reads()
    {
        using var install = new TestInstall();
        File.WriteAllText(install.Paths.OptionsFile, "theme=nord\n");
        var store = new OptionsStore(install.Paths);
        store.Load();
        Assert.Equal(string.Empty, store.Current.ImportMissingFolder);
        Assert.Equal(OptionsSaveStatus.Saved, store.Update(o => o with { ImportMissingFolder = "/home/me/UGD" }));
        Assert.Contains("\nimportmissingfolder=/home/me/UGD\n", File.ReadAllText(install.Paths.OptionsFile));
        Assert.Contains("\ntheme=nord\n", File.ReadAllText(install.Paths.OptionsFile));
        var again = new OptionsStore(install.Paths);
        again.Load();
        Assert.Equal("/home/me/UGD", again.Current.ImportMissingFolder);
        Assert.Empty(again.UnknownEntries);
    }

    [Fact]
    public void Unknown_keys_survive_a_rewrite()
    {
        using var install = new TestInstall();
        File.WriteAllText(install.Paths.OptionsFile, "futureoption=some value = with equals\ntheme=nord\nanother=1\nfutureoption=last wins\nno separator line\n");
        var store = new OptionsStore(install.Paths);
        store.Load();
        Assert.Equal(2, store.UnknownEntries.Count);
        store.Update(o => o with { Theme = "gruvbox" });
        string text = File.ReadAllText(install.Paths.OptionsFile);
        Assert.Contains("\nfutureoption=last wins\n", text);
        Assert.Contains("\nanother=1\n", text);
        Assert.Contains("\ntheme=gruvbox\n", text);

        var again = new OptionsStore(install.Paths);
        again.Load();
        Assert.Equal("gruvbox", again.Current.Theme);
        Assert.Equal(new KeyValuePair<string, string>("futureoption", "last wins"), again.UnknownEntries[0]);
    }

    [Theory]
    [InlineData("afktimeout", "5000", 1440)]
    [InlineData("afktimeout", "0", 1)]
    [InlineData("afktimeout", "-7", 1)]
    [InlineData("afktimeout", "abc", 15)]
    [InlineData("afktimeout", "12x", 15)]
    [InlineData("afktimeout", "99999999999", 15)]
    [InlineData("afktimeout", "+30", 30)]
    [InlineData("chatfontsize", "2000", 1000)]
    [InlineData("scripttabwidth", "0", 1)]
    [InlineData("scriptfontsize", "", 10)]
    [InlineData("formatindentwidth", "99", 16)]
    [InlineData("formatindentwidth", "4", 4)]
    public void Integers_are_clamped_like_the_cpp(string key, string value, int expected)
    {
        RcOptions options = RcOptions.Defaults.WithValue(key, value);
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), options.GetValue(key));
    }

    [Fact]
    public void Setting_values_in_code_clamps_and_falls_back_too()
    {
        RcOptions options = RcOptions.Defaults with { AfkTimeout = 100000, FormatIndentWidth = -1, ChatFontFamily = "", ScriptFontFamily = null!, TimestampFormat = "", Nickname = null! };
        Assert.Equal(1440, options.AfkTimeout);
        Assert.Equal(1, options.FormatIndentWidth);
        Assert.Equal("Sans", options.ChatFontFamily);
        Assert.Equal("Monospace", options.ScriptFontFamily);
        Assert.Equal("[%I:%M %p]", options.TimestampFormat);
        Assert.Equal(string.Empty, options.Nickname);
    }

    [Fact]
    public void Booleans_are_true_only_for_exactly_true()
    {
        Assert.False(RcOptions.Defaults.WithValue("attachaway", "TRUE").AttachAway);
        Assert.False(RcOptions.Defaults.WithValue("attachaway", "1").AttachAway);
        Assert.True(RcOptions.Defaults.WithValue("minimap", "true").Minimap);
    }

    [Fact]
    public void Button_arrays_always_have_fifteen_entries()
    {
        RcOptions shortArray = RcOptions.Defaults with { ButtonImages = ["only.png"] };
        Assert.Equal(15, shortArray.ButtonImages.Length);
        Assert.Equal("only.png", shortArray.ButtonImages[0]);
        Assert.Equal("rc_filebrowser_normal.png", shortArray.ButtonImages[1]);
        RcOptions viaKey = RcOptions.Defaults.WithValue(OptionKeys.ButtonImage("levellist", pressed: true), "custom.png");
        Assert.Equal("custom.png", viaKey.ButtonImagesPressed[13]);
        Assert.Equal(15, RcOptions.ButtonNames.Length);
    }

    [Fact]
    public void A_value_with_a_line_break_is_refused_and_the_file_is_unchanged()
    {
        using var install = new TestInstall();
        var store = new OptionsStore(install.Paths);
        store.Load();
        string before = File.ReadAllText(install.Paths.OptionsFile);
        Assert.Throws<ArgumentException>(() => store.Update(o => o with { Nickname = "evil\nbackground=x" }));
        Assert.Throws<ArgumentException>(() => store.SetValue(OptionKeys.LabelServers, "a\rb"));
        Assert.Equal(before, File.ReadAllText(install.Paths.OptionsFile));
        Assert.Equal(string.Empty, store.Current.Nickname);
    }

    [Fact]
    public void One_shared_store_keeps_changes_of_every_window()
    {
        using var install = new TestInstall();
        var store = new OptionsStore(install.Paths);
        store.Load();
        var windowA = new object();
        var windowB = new object();
        var events = new List<OptionsChangedEventArgs>();
        store.Changed += (_, args) => events.Add(args);

        store.Update(o => o with { Theme = "nord" }, windowA);
        store.Update(o => o with { ChatFontSize = 14, Nickname = "Staff" }, windowB);
        Assert.Equal(OptionsSaveStatus.Unchanged, store.Update(o => o with { ChatFontSize = 14 }, windowB));

        Assert.Equal(2, events.Count);
        Assert.Same(windowA, events[0].Source);
        Assert.Equal([OptionKeys.Theme], events[0].Keys);
        Assert.Same(windowB, events[1].Source);
        Assert.True(events[1].Contains(OptionKeys.ChatFontSize));
        Assert.True(events[1].Contains(OptionKeys.Nickname));
        Assert.False(events[1].Contains(OptionKeys.Theme));
        Assert.Equal(9, events[1].Previous.ChatFontSize);

        var reread = new OptionsStore(install.Paths);
        reread.Load();
        Assert.Equal("nord", reread.Current.Theme);
        Assert.Equal(14, reread.Current.ChatFontSize);
        Assert.Equal("Staff", reread.Current.Nickname);
    }

    [Fact]
    public void A_throwing_handler_does_not_stop_others_or_the_save()
    {
        using var install = new TestInstall();
        var store = new OptionsStore(install.Paths);
        store.Load();
        bool second = false;
        store.Changed += (_, _) => throw new InvalidOperationException("window bug");
        store.Changed += (_, _) => second = true;
        Assert.Equal(OptionsSaveStatus.Saved, store.Update(o => o with { Minimap = true }));
        Assert.True(second);
    }

    [Fact]
    public void SetValue_and_GetValue_work_by_key()
    {
        using var install = new TestInstall();
        var store = new OptionsStore(install.Paths);
        store.Load();
        store.SetValue(OptionKeys.McpApproveNpc, "true");
        store.SetValue(OptionKeys.WebBrowsers, "chromium,firefox");
        Assert.True(store.Current.McpApproveNpc);
        Assert.Equal("chromium,firefox", store.Current.GetValue(OptionKeys.WebBrowsers));
        Assert.Throws<ArgumentException>(() => store.SetValue("nosuchkey", "1"));
    }

    [Fact]
    public void Reload_reads_changes_made_on_disk()
    {
        using var install = new TestInstall();
        var store = new OptionsStore(install.Paths);
        store.Load();
        File.WriteAllText(install.Paths.OptionsFile, File.ReadAllText(install.Paths.OptionsFile).Replace("mcpapproveweapon=false", "mcpapproveweapon=true"));
        IReadOnlyList<string>? keys = null;
        store.Changed += (_, args) => keys = args.Keys;
        Assert.True(store.Reload());
        Assert.True(store.Current.McpApproveWeapon);
        Assert.Equal([OptionKeys.McpApproveWeapon], keys);
    }

    [Fact]
    public void A_missing_background_is_reset_and_saved()
    {
        using var install = new TestInstall();
        File.WriteAllText(install.Paths.OptionsFile, "background=gone.png\n");
        var store = new OptionsStore(install.Paths);
        store.Load();
        Assert.Equal("rc_background.png", store.Current.Background);
        Assert.Contains("\nbackground=rc_background.png\n", File.ReadAllText(install.Paths.OptionsFile));
    }

    [Fact]
    public void An_existing_custom_background_is_kept()
    {
        using var install = new TestInstall();
        File.WriteAllBytes(Path.Combine(install.Paths.ImagesDirectory, "mine.png"), [9]);
        File.WriteAllText(install.Paths.OptionsFile, "background=mine.png\n");
        var store = new OptionsStore(install.Paths);
        store.Load();
        Assert.Equal("mine.png", store.Current.Background);
        Assert.Equal("background=mine.png\n", File.ReadAllText(install.Paths.OptionsFile));
    }

    [Fact]
    public void A_damaged_file_is_moved_aside_not_overwritten()
    {
        using var install = new TestInstall();
        byte[] damaged = new byte[64];
        "theme=nord\n"u8.CopyTo(damaged);
        File.WriteAllBytes(install.Paths.OptionsFile, damaged);
        var store = new OptionsStore(install.Paths, () => new DateTime(2026, 1, 2, 3, 4, 5));

        OptionsLoadResult result = store.Load();

        Assert.Equal(OptionsLoadStatus.UnreadableMovedAside, result.Status);
        Assert.True(result.NeedsAttention);
        Assert.Equal(install.Paths.OptionsFile + ".unreadable-20260102-030405", result.MovedAsidePath);
        Assert.Equal(damaged, File.ReadAllBytes(result.MovedAsidePath!));
        Assert.Equal(RcOptions.Defaults, store.Current);
        Assert.True(store.CanSave);
    }

    [Fact]
    public void Webbrowsers_split_like_getline()
    {
        Assert.Empty(RcOptions.Defaults.WithValue(OptionKeys.WebBrowsers, "").WebBrowsers);
        Assert.Equal(["a", "", "b"], RcOptions.Defaults.WithValue(OptionKeys.WebBrowsers, "a,,b,").WebBrowsers.ToArray());
    }

    [Fact]
    public void Theme_colours_are_those_the_cpp_wrote()
    {
        RcOptions dracula = ThemeOptionColors.Apply(RcOptions.Defaults, "dracula");
        Assert.Equal("#f8f8f2", dracula.ColorEdit);
        Assert.Equal("#50fa7b", dracula.ColorChatBold);
        Assert.Equal("#282a36", dracula.ColorLabelBack);
        RcOptions unknown = ThemeOptionColors.Apply(RcOptions.Defaults, "system");
        Assert.Equal("#00ff00", unknown.ColorEdit);
        Assert.Equal("#d4d4d4", unknown.ColorChat);
        Assert.Equal("#1e1e2e60", ThemeOptionColors.TintFor("catppuccin"));
        Assert.Equal("#ffffff60", ThemeOptionColors.TintFor("light"));
        Assert.Equal("#00000000", ThemeOptionColors.TintForColor("bogus"));
    }
}
