using System.Text.RegularExpressions;
using System.Xml.Linq;
using RemoteControl.Scripting;

namespace RemoteControl.Scripting.Tests;

public sealed class HighlightingTests
{
    private static readonly XNamespace Xshd = "http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008";

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "RemoteControl.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string LanguageSpecs => Path.Combine(RepositoryRoot(), "assets", "runtime", "language-specs");

    private static XDocument Load(HighlightingLanguage language)
    {
        using Stream stream = HighlightingResources.Open(language)!;
        return XDocument.Load(stream);
    }

    public static TheoryData<HighlightingLanguage> Languages() => new() { HighlightingLanguage.Graal, HighlightingLanguage.Ini };

    [Theory]
    [MemberData(nameof(Languages))]
    public void XshdIsWellFormedAndConsistent(HighlightingLanguage language)
    {
        XDocument document = Load(language);
        XElement root = document.Root!;
        Assert.Equal(Xshd + "SyntaxDefinition", root.Name);
        Assert.False(string.IsNullOrEmpty((string?)root.Attribute("name")));

        string[] defined = root.Elements(Xshd + "Color").Select(color => (string)color.Attribute("name")!).ToArray();
        Assert.Equal(defined.Length, defined.Distinct(StringComparer.Ordinal).Count());
        Assert.All(defined, name => Assert.Matches("^[A-Za-z][A-Za-z0-9]*$", name));
        Assert.Equal(HighlightingResources.ColorsOf(language).Select(color => color.Name).OrderBy(name => name, StringComparer.Ordinal), defined.OrderBy(name => name, StringComparer.Ordinal));
        foreach (XElement color in root.Elements(Xshd + "Color"))
        {
            Assert.True(ThemeColor.TryParse((string?)color.Attribute("foreground"), out _));
        }

        string[] ruleSetNames = root.Elements(Xshd + "RuleSet").Select(set => (string?)set.Attribute("name")).Where(name => name != null).Select(name => name!).ToArray();
        Assert.Single(root.Elements(Xshd + "RuleSet"), set => set.Attribute("name") == null);
        foreach (XElement element in root.Descendants())
        {
            string? color = (string?)element.Attribute("color");
            if (color != null) Assert.Contains(color, defined);
            string? ruleSet = (string?)element.Attribute("ruleSet");
            if (ruleSet != null) Assert.Contains(ruleSet, ruleSetNames);
            if (element.Name == Xshd + "Rule" || element.Name == Xshd + "Keywords") Assert.NotNull(color);
            if (element.Name == Xshd + "Span")
            {
                Assert.True(element.Parent!.Name == Xshd + "RuleSet");
                string? begin = (string?)element.Attribute("begin") ?? (string?)element.Element(Xshd + "Begin");
                Assert.False(string.IsNullOrEmpty(begin));
            }
        }

        foreach (string pattern in Patterns(root))
        {
            var regex = new Regex(pattern, RegexOptions.CultureInvariant);
            Assert.False(regex.Match(string.Empty).Success && regex.Match(string.Empty).Length == 0 && pattern != "$", "pattern matches the empty string: " + pattern);
            foreach (string sample in new[] { "abc", "  x = 1;", "[group]", "#", "if (a) b(c);", "0x1F 1.5e3 .5" })
            {
                foreach (Match match in regex.Matches(sample)) Assert.True(match.Length > 0, "zero-length match of " + pattern + " in " + sample);
            }
        }
    }

    private static IEnumerable<string> Patterns(XElement root)
    {
        foreach (XElement rule in root.Descendants(Xshd + "Rule")) yield return rule.Value;
        foreach (XElement span in root.Descendants(Xshd + "Span"))
        {
            string? begin = (string?)span.Attribute("begin") ?? (string?)span.Element(Xshd + "Begin");
            string? end = (string?)span.Attribute("end") ?? (string?)span.Element(Xshd + "End");
            if (begin != null) yield return begin;
            if (end != null) yield return end;
        }
        foreach (XElement keywords in root.Descendants(Xshd + "Keywords"))
        {
            // AvaloniaEdit builds this regex with the words sorted by descending length.
            yield return @"\b(?>" + string.Join("|", keywords.Elements(Xshd + "Word").Select(word => word.Value).OrderByDescending(word => word.Length).Select(Regex.Escape)) + @")\b";
        }
    }

    /// <summary>The keyword lists of a GtkSourceView v1 language file: (style, case sensitive, words).</summary>
    private static IEnumerable<(string Style, bool CaseSensitive, string[] Words)> LanguageKeywords(string file)
    {
        using var reader = System.Xml.XmlReader.Create(Path.Combine(LanguageSpecs, file), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null });
        XDocument language = XDocument.Load(reader);
        foreach (XElement list in language.Root!.Elements("keyword-list"))
        {
            bool caseSensitive = !string.Equals((string?)list.Attribute("case-sensitive"), "false", StringComparison.OrdinalIgnoreCase);
            yield return ((string)list.Attribute("style")!, caseSensitive, list.Elements("keyword").Select(keyword => keyword.Value).ToArray());
        }
    }

    [Theory]
    [InlineData("graal.lang", HighlightingLanguage.Graal)]
    [InlineData("ini.lang", HighlightingLanguage.Ini)]
    public void KeywordListsMatchTheLanguageFile(string file, HighlightingLanguage language)
    {
        XElement main = Load(language).Root!.Elements(Xshd + "RuleSet").Single(set => set.Attribute("name") == null);
        Assert.Equal("false", (string?)main.Attribute("ignoreCase"));
        Dictionary<string, string> colorByStyle = HighlightingResources.ColorsOf(language).ToDictionary(color => color.LanguageStyle, color => color.Name);
        foreach ((string style, bool caseSensitive, string[] words) in LanguageKeywords(file))
        {
            string color = colorByStyle[style];
            if (caseSensitive)
            {
                XElement keywords = Assert.Single(main.Elements(Xshd + "Keywords"), element => (string)element.Attribute("color")! == color
                    && element.Elements(Xshd + "Word").Select(word => word.Value).SequenceEqual(words));
                Assert.NotNull(keywords);
                // As AvaloniaEdit builds it: longest words first inside an atomic group, so "elseif" is not cut to "else".
                var regex = new Regex(@"\b(?>" + string.Join("|", words.OrderByDescending(word => word.Length).Select(Regex.Escape)) + @")\b");
                foreach (string word in words)
                {
                    Assert.Equal(word, regex.Match(" " + word + " ").Value);
                    if (word.ToUpperInvariant() != word) Assert.DoesNotMatch(regex, word.ToUpperInvariant());
                }
            }
            else
            {
                XElement rule = Assert.Single(main.Elements(Xshd + "Rule"), element => (string)element.Attribute("color")! == color && element.Value.StartsWith("(?i)", StringComparison.Ordinal));
                var regex = new Regex(rule.Value);
                foreach (string word in words)
                {
                    foreach (string variant in new[] { word, word.ToUpperInvariant(), word.ToLowerInvariant(), char.ToUpperInvariant(word[0]) + word.Substring(1) })
                    {
                        Match match = regex.Match(" " + variant + " ");
                        Assert.True(match.Success && match.Value == variant, variant + " in " + rule.Value);
                    }
                }
                Assert.DoesNotMatch(regex, "x" + words[0] + "x");
            }
        }
    }

    [Fact]
    public void FunctionRuleIsALookahead()
    {
        XElement main = Load(HighlightingLanguage.Graal).Root!.Elements(Xshd + "RuleSet").Single(set => set.Attribute("name") == null);
        var function = new Regex(main.Elements(Xshd + "Rule").Single(rule => (string)rule.Attribute("color")! == "Function").Value);
        Assert.Equal("echo", function.Match("x = echo(1);").Value);
        Assert.DoesNotMatch(function, "echo (1)");
        var number = new Regex(main.Elements(Xshd + "Rule").Single(rule => (string)rule.Attribute("color")! == "Decimal").Value);
        Assert.Equal(new[] { "12", "0x1F", "1", ".5", "2e10", "3f" }, number.Matches("12 0x1F 1.5 2e10 3f").Select(match => match.Value));
        Assert.Equal("Function", main.Elements().Last().Attribute("color")!.Value);
    }

    [Fact]
    public void LanguageTable()
    {
        Assert.Equal(HighlightingLanguage.Graal, HighlightingResources.LanguageForFile("levels/start.NW"));
        Assert.Equal(HighlightingLanguage.Graal, HighlightingResources.LanguageForFile("a.graal"));
        Assert.Equal(HighlightingLanguage.Ini, HighlightingResources.LanguageForFile("config/serveroptions.ini"));
        Assert.Equal(HighlightingLanguage.Ini, HighlightingResources.LanguageForFile("x.conf"));
        Assert.Equal(HighlightingLanguage.None, HighlightingResources.LanguageForFile("readme.txt"));
        Assert.Equal(HighlightingLanguage.None, HighlightingResources.LanguageForFile(null));
        Assert.Equal(HighlightingLanguage.Graal, HighlightingResources.LanguageForEditor(EditorContentKind.WeaponScript));
        Assert.Equal(HighlightingLanguage.Graal, HighlightingResources.LanguageForEditor(EditorContentKind.ClassScript));
        Assert.Equal(HighlightingLanguage.Graal, HighlightingResources.LanguageForEditor(EditorContentKind.NpcScript));
        Assert.Equal(HighlightingLanguage.Ini, HighlightingResources.LanguageForEditor(EditorContentKind.NpcFlags));
        Assert.Equal(HighlightingLanguage.Ini, HighlightingResources.LanguageForEditor(EditorContentKind.ServerOptions));
        Assert.Equal(HighlightingLanguage.Ini, HighlightingResources.LanguageForEditor(EditorContentKind.ServerFlags));
        Assert.Equal(HighlightingLanguage.Ini, HighlightingResources.LanguageForEditor(EditorContentKind.FolderConfig));
        Assert.Equal(HighlightingLanguage.None, HighlightingResources.LanguageForEditor(EditorContentKind.LevelList));
        Assert.Null(HighlightingResources.Open(HighlightingLanguage.None));
        using Stream graal = HighlightingResources.OpenGraal();
        using Stream ini = HighlightingResources.OpenIni();
        Assert.True(graal.Length > 0 && ini.Length > 0);
    }

    public static TheoryData<string> SchemeFiles()
    {
        var data = new TheoryData<string>();
        foreach (string file in Directory.GetFiles(LanguageSpecs, "*.xml").OrderBy(file => file, StringComparer.Ordinal)) data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(SchemeFiles))]
    public void ShippedSchemesLoadWithEveryGraalColour(string file)
    {
        SyntaxThemeLoadResult result = SyntaxThemeLoader.Load(Path.Combine(LanguageSpecs, file));
        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Warnings);
        SyntaxTheme theme = result.Theme!;
        Assert.Equal(Path.GetFileNameWithoutExtension(file), theme.Id);
        Assert.NotNull(theme.SelectionBackground);
        Assert.NotNull(theme.LineNumbersForeground);
        Assert.NotNull(theme.LineNumbersBackground);
        Assert.NotEqual(theme.Foreground, theme.Background);
        foreach (HighlightingColorName color in HighlightingResources.GraalColors)
        {
            Assert.True(theme.Colors.TryGetValue(color.Name, out SyntaxStyle? style), color.Name);
            Assert.True(style!.FromScheme, file + " has no style for " + color.Name);
        }
        Assert.All(HighlightingResources.IniColors, color => Assert.True(theme.Colors.ContainsKey(color.Name)));
    }

    [Fact]
    public void ColourReferencesAndUseStyleResolve()
    {
        SyntaxTheme graal = SyntaxThemeLoader.Load(Path.Combine(LanguageSpecs, "graalcolors.xml")).Theme!;
        Assert.Equal("#F57900", graal.Colors["Others5"].Foreground.ToHex());
        Assert.Equal("#3F8C61", graal.Colors["ObjectProperties"].Foreground.ToHex());
        Assert.True(graal.Colors["Others"].Bold);
        Assert.Equal("#75715E", graal.Colors["Comment"].Foreground.ToHex());
        Assert.Equal("#33322B", graal.Background.ToHex());
        Assert.Equal("#F8F8F2", graal.Foreground.ToHex());
        Assert.Equal("#333333", graal.CurrentLineBackground!.Value.ToHex());
        Assert.Equal("#F8F8F0", graal.Caret!.Value.ToHex());
        Assert.Equal(graal.SchemeStyles["def:type"], graal.SchemeStyles["diff:diff-file"]);
        Assert.Equal("#555753", graal.SchemeStyles["right-margin"].Foreground!.Value.ToHex());
        Assert.Equal("#AE81FF", graal.Colors["Comment2"].Foreground.ToHex());
        Assert.True(graal.Colors["Comment2"].Bold);

        SyntaxTheme ayu = SyntaxThemeLoader.Load(Path.Combine(LanguageSpecs, "ayu-mirage.xml")).Theme!;
        Assert.True(ayu.Colors["Comment"].Italic);
        Assert.Equal("#242936", ayu.Background.ToHex());
        Assert.Equal("#1A1F29", ayu.CurrentLineBackground!.Value.ToHex());
        Assert.Equal("#DFBFFF", ayu.Colors["Decimal"].Foreground.ToHex());
        Assert.False(ayu.Colors["Comment2"].FromScheme);
        Assert.Equal(ayu.Foreground, ayu.Colors["Comment2"].Foreground);
    }

    [Fact]
    public void ThemeNamesMapToSchemes()
    {
        foreach (string name in new[] { "dracula", "material", "ayu-mirage", "nord", "monokai", "one-dark", "tokyo-night", "gruvbox", "solarized", "catppuccin" })
        {
            Assert.Equal(name, SyntaxThemeLoader.SchemeIdForTheme(name));
            Assert.True(File.Exists(Path.Combine(LanguageSpecs, SyntaxThemeLoader.SchemeFileName(name)!)));
            Assert.True(SyntaxThemeLoader.LoadForTheme(name, LanguageSpecs).Success);
        }
        Assert.Equal("classic", SyntaxThemeLoader.SchemeIdForTheme("light"));
        Assert.Null(SyntaxThemeLoader.SchemeFileName("classic"));
        Assert.Equal("graalcolors", SyntaxThemeLoader.SchemeIdForTheme("dark"));
        Assert.Equal("graalcolors", SyntaxThemeLoader.SchemeIdForTheme("language-spec"));
        Assert.Equal("graalcolors", SyntaxThemeLoader.SchemeIdForTheme(null));
        Assert.Equal("graalcolors", SyntaxThemeLoader.LoadForTheme("whatever", LanguageSpecs).Theme!.Id);
        SyntaxTheme classic = SyntaxThemeLoader.LoadForTheme("light", "/nonexistent").Theme!;
        Assert.Same(SyntaxThemeLoader.Classic, classic);
        Assert.Equal("#FFFFFF", classic.Background.ToHex());
        Assert.Equal("#000000", classic.Foreground.ToHex());
        Assert.All(HighlightingResources.AllColors, color => Assert.True(classic.Colors[color.Name].FromScheme, color.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<style-scheme")]
    [InlineData("<language id='x'/>")]
    [InlineData("not xml at all")]
    [InlineData("<?xml version='1.0'?><!DOCTYPE x [<!ENTITY a 'aaaaaaaaaa'><!ENTITY b '&a;&a;&a;&a;&a;&a;&a;&a;'>]><style-scheme>&b;</style-scheme>")]
    public void MalformedSchemesGiveAnError(string xml)
    {
        SyntaxThemeLoadResult result = SyntaxThemeLoader.Parse(xml);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void BrokenReferencesAreWarnings()
    {
        SyntaxThemeLoadResult result = SyntaxThemeLoader.Parse("""
            <style-scheme id="t" _name="T">
              <color name="c" value="nonsense"/>
              <style name="text" foreground="#abc" background="missing"/>
              <style name="a" use-style="b"/>
              <style name="b" use-style="a"/>
              <style name="Comment" use-style="nothing"/>
              <style name="Keyword" foreground="#11223344" bold="TRUE" italic="false" underline="single"/>
            </style-scheme>
            """);
        Assert.True(result.Success);
        Assert.NotEmpty(result.Warnings);
        SyntaxTheme theme = result.Theme!;
        Assert.Equal("T", theme.Name);
        Assert.Equal("#AABBCC", theme.Foreground.ToHex());
        Assert.Equal("#FFFFFF", theme.Background.ToHex());
        Assert.False(theme.Colors["Comment"].FromScheme);
        SyntaxStyle keyword = theme.Colors["Keyword"];
        Assert.Equal("#11223344", keyword.Foreground.ToHex());
        Assert.True(keyword.Bold);
        Assert.False(keyword.Italic);
        Assert.True(keyword.Underline);
    }

    [Fact]
    public void MissingSchemeFileIsAnError()
    {
        SyntaxThemeLoadResult result = SyntaxThemeLoader.Load(Path.Combine(LanguageSpecs, "missing.xml"));
        Assert.False(result.Success);
        Assert.Contains("does not exist", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("#A1B2C3", "#A1B2C3")]
    [InlineData("#a1b2c3d4", "#A1B2C3D4")]
    [InlineData("#ffff00000000", "#FF0000")]
    [InlineData("white", "#FFFFFF")]
    public void ColourParsing(string text, string hex)
    {
        Assert.True(ThemeColor.TryParse(text, out ThemeColor color));
        Assert.Equal(hex, color.ToHex());
    }

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12")]
    [InlineData("#ggg")]
    [InlineData("chartreuse-ish")]
    public void BadColours(string text) => Assert.False(ThemeColor.TryParse(text, out _));
}
