using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RemoteControl.Core;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Common.Tests;

public sealed class UrlOpenerTests
{
    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/a?b=c", true)]
    [InlineData("mailto:someone@example.com", true)]
    [InlineData("HTTPS://EXAMPLE.COM", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("example.com", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("https://", false)]
    [InlineData("https://exa\nmple.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_http_https_and_mailto_are_allowed(string? url, bool allowed) => Assert.Equal(allowed, UrlOpener.IsAllowed(url));

    [AvaloniaFact]
    public void Refused_links_start_nothing()
    {
        using var env = new TestEnvironment();
        var launched = new List<(string Program, IReadOnlyList<string> Arguments)>();
        var opener = new UrlOpener(env.Services.Options, env.Log, (program, arguments) => { launched.Add((program, arguments)); return true; });
        Assert.False(opener.Open("javascript:alert(1)"));
        Assert.False(opener.Open("file:///etc/passwd"));
        Assert.Empty(launched);
        Assert.Contains(env.Log.Snapshot(), line => line.Contains("Refused", StringComparison.Ordinal));

        Assert.True(opener.Open("https://example.com/page"));
        (string program, IReadOnlyList<string> arguments) = Assert.Single(launched);
        Assert.Equal("https://example.com/page", arguments.Count > 0 ? arguments[^1] : program);
    }

    [AvaloniaFact]
    public void The_old_default_browser_list_means_the_system_default()
    {
        using var env = new TestEnvironment();
        var opener = new UrlOpener(env.Services.Options, env.Log, (_, _) => true);
        Assert.Equal(RcOptions.Defaults.WebBrowsers.ToArray(), env.Options.WebBrowsers.ToArray());
        Assert.Empty(opener.ConfiguredBrowsers());
        env.SetOptions(o => o with { WebBrowsers = ImmutableArray.Create("no-such-browser-xyz", " ") });
        Assert.Equal(["no-such-browser-xyz"], opener.ConfiguredBrowsers());
        var launched = new List<string>();
        var fallback = new UrlOpener(env.Services.Options, env.Log, (program, _) => { launched.Add(program); return true; });
        Assert.True(fallback.Open("https://example.com"));
        Assert.DoesNotContain(launched, program => program.Contains("no-such-browser", StringComparison.Ordinal));
        Assert.True(new UrlOpener(null, env.Log, (_, _) => false).Open("https://x.org") == false);
    }
}

public sealed class ListSearchTests
{
    [Fact]
    public void Matches_contain_ignoring_case_and_wrap_around()
    {
        Assert.True(ListSearch.Matches("Graal123", "aal1"));
        Assert.True(ListSearch.Matches("Graal123", "GRA"));
        Assert.False(ListSearch.Matches("Graal123", "xyz"));
        Assert.True(ListSearch.Matches(null, ""));
        string?[] texts = ["Alpha", "Bravo", null, "bravado", "Charlie"];
        Assert.Equal(1, ListSearch.FindNext(texts, 0, "bra"));
        Assert.Equal(3, ListSearch.FindNext(texts, 2, "bra"));
        Assert.Equal(1, ListSearch.FindNext(texts, 4, "bra"));
        Assert.Equal(-1, ListSearch.FindNext(texts, 0, "zulu"));
        Assert.Equal(-1, ListSearch.FindNext(Array.Empty<string>(), 0, "a"));
    }

    [AvaloniaFact]
    public void Typing_in_a_list_selects_the_first_match()
    {
        using var env = new TestEnvironment();
        var list = new ListBox { Name = "Players", ItemsSource = new[] { "Alpha", "Bravo", "Charlie", "bravado", "Delta" } };
        var window = env.Show(new RcWindow(env.Services) { Content = list }, 200, 200);
        using ListSearch search = ListSearch.Attach(list);
        list.ContainerFromIndex(0)!.Focus();
        TestUi.TypeText(window, "c");
        Assert.Equal(2, list.SelectedIndex);
        TestUi.TypeText(window, "h");
        Assert.Equal("ch", search.Query);
        Assert.Equal(2, list.SelectedIndex);
        TestUi.PressKey(window, Key.Escape);
        Assert.Equal(string.Empty, search.Query);
        TestUi.TypeText(window, "BRAVA");
        Assert.Equal(3, list.SelectedIndex);
        TestUi.PressKey(window, Key.Back);
        Assert.Equal("BRAV", search.Query);
        Assert.Equal(3, list.SelectedIndex);
        Assert.False(search.Type("zzz"));
        Assert.Equal(3, list.SelectedIndex);
    }

    [AvaloniaFact]
    public void Typing_in_a_tree_selects_a_visible_match()
    {
        using var env = new TestEnvironment();
        var hidden = new TreeViewItem { Header = "secret weapon" };
        var tree = new TreeView
        {
            ItemsSource = new[]
            {
                new TreeViewItem { Header = "levels", IsExpanded = true, ItemsSource = new[] { new TreeViewItem { Header = "start.nw" }, new TreeViewItem { Header = "house.nw" } } },
                new TreeViewItem { Header = "weapons", IsExpanded = false, ItemsSource = new[] { hidden } },
            },
        };
        var window = env.Show(new RcWindow(env.Services) { Content = tree }, 200, 200);
        using ListSearch search = ListSearch.Attach(tree);
        TestUi.First<TreeViewItem>(tree).Focus();
        TestUi.TypeText(window, "house");
        Assert.Equal("house.nw", Assert.IsType<TreeViewItem>(tree.SelectedItem).Header);
        Assert.Throws<ArgumentException>(() => ListSearch.Attach(new Button()));
        search.Dispose();
        Assert.False(search.Type("weap"));
        Assert.False(search.Type("secret"));
    }
}

public sealed class AppServicesTests
{
    [AvaloniaFact]
    public void Creates_everything_over_temporary_folders()
    {
        using var env = new TestEnvironment();
        Assert.Empty(env.Problems);
        AppServices services = env.Services;
        Assert.Same(env.Paths, services.Paths);
        Assert.True(File.Exists(env.Paths.OptionsFile));
        Assert.Equal("dark", services.Options.Current.Theme);
        Assert.NotNull(services.Accounts);
        Assert.NotEmpty(services.Profiles.Profiles);
        Assert.True(services.Theme.IsInstalled);
        Assert.False(services.Sounds.Enabled);
        Assert.StartsWith(Path.GetTempPath(), env.Paths.UserDirectory, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(env.ProgramDirectory, "images", "rcicon.png")));
    }

    [AvaloniaFact]
    public void Unreadable_files_are_reported_not_thrown()
    {
        string root = Path.Combine(Path.GetTempPath(), "rc-ui-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(Path.Combine(root, "user"), Path.Combine(TestEnvironment.RepositoryRoot, "assets", "runtime"));
            Directory.CreateDirectory(paths.UserDirectory);
            File.WriteAllBytes(paths.OptionsFile, [(byte)'t', 0, 0, (byte)'x']);
            File.WriteAllBytes(paths.AccountsKeyFile, [1, 2, 3]);
            AppServicesResult result = AppServices.Create(paths, new AppServicesSettings { EnableSounds = false, LogToFile = false, Log = new AppLog(), InstallTheme = false, ConfigureUiSafe = false });
            using AppServices services = result.Services;
            Assert.NotEmpty(result.Problems);
            Assert.Contains(result.Problems, problem => problem.Contains("options", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("dark", services.Options.Current.Theme);
            Assert.False(services.Theme.IsInstalled);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
