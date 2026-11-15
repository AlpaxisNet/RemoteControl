namespace RemoteControl.Extensions.Tests;

public sealed class ManifestTests
{
    private const string SmokeJson = "{\"id\":\"test.extension\",\"name\":\"Test Extension\",\"version\":\"1.0.0\",\"publisher\":\"RC Tests\",\"runtime\":\"gs2engine-stdio\",\"entry\":\"main.gs2\",\"capabilities\":[\"script-read\",\"file-read\"],\"ui\":[\"output-tab\"],\"themes\":[\"theme.dark\"],\"commands\":[\"test.run\"],\"readOnlyViews\":[\"test.status\"],\"windowActions\":[{\"kind\":\"script-editor\",\"id\":\"evaluate\",\"label\":\"Evaluate\",\"placement\":\"bottom\",\"icon\":\"gtk:document-properties-symbolic\"}],\"api\":1}";

    // Port of tests/ExtensionsTests.cpp, every case in order.
    [Fact]
    public void CppExtensionsTestCases()
    {
        using var root = new TempFolder();
        root.Write("gs2engine/extension.ini", "id=gs2engine\nname=GS2Engine\nversion=1.0.0\nentry=GS2Engine.exe\nruntime=\n");
        IReadOnlyList<ExtensionManifest> manifests = ExtensionManifestLoader.Scan(root.Path);
        Assert.Single(manifests);
        Assert.Equal("gs2engine", manifests[0].Id);
        Assert.True(manifests[0].IsValid);

        root.Write("smoke/extension.json", SmokeJson);
        root.Write("smoke/main.gs2", "echo(\"RC GS2 extension smoke ready\");\n");
        IReadOnlyList<ExtensionManifest> jsonManifests = ExtensionManifestLoader.Scan(root.Path);
        Assert.Equal(2, jsonManifests.Count);
        ExtensionManifest smoke = jsonManifests.Single(manifest => manifest.Id == "test.extension");
        Assert.Equal("gs2engine-stdio", smoke.Runtime);
        Assert.True(smoke.UsesGs2Engine);
        Assert.Equal("RC Tests", smoke.Publisher);
        Assert.Equal(new[] { "script-read", "file-read" }, smoke.RequestedCapabilities);
        Assert.Equal(new[] { "output-tab" }, smoke.UiContributions);
        Assert.Equal(new[] { "theme.dark" }, smoke.Themes);
        Assert.Equal(new[] { "test.run" }, smoke.Commands);
        Assert.Equal(new[] { "test.status" }, smoke.ReadOnlyViews);
        ExtensionWindowAction action = Assert.Single(smoke.WindowActions);
        Assert.Equal(new ExtensionWindowAction("script-editor", "evaluate", "Evaluate", ExtensionActionPlacement.Bottom, "gtk:document-properties-symbolic"), action);
        Assert.Equal(1, smoke.Api);

        root.Write("auto/extension.json", "{\"id\":\"auto.extension\",\"name\":\"Auto Extension\",\"version\":\"1.0.0\",\"runtime\":\"gs2engine-stdio\",\"mode\":\"auto\",\"api\":1}");
        ExtensionManifest automatic = ExtensionManifestLoader.Scan(root.Path).Single(manifest => manifest.Id == "auto.extension");
        Assert.True(automatic.AutoDiscover);
        Assert.Equal(string.Empty, automatic.Entry);
        Assert.True(automatic.IsValid);

        root.Write("broken/extension.json", "{\"id\":\"broken.extension\",\"name\":\"Broken Extension\"}");
        ExtensionManifest broken = ExtensionManifestLoader.Scan(root.Path).Single(manifest => manifest.Id == "broken.extension");
        Assert.NotEqual(string.Empty, broken.Error);
        Assert.False(broken.IsValid);

        string log = string.Empty;
        log = ExtensionProtocol.AppendLog(log, "one", 8);
        log = ExtensionProtocol.AppendLog(log, "two", 8);
        Assert.True(log.Length <= 8);
        Assert.Equal("one\ntwo", log);
    }

    [Fact]
    public void ScanSortsByIdAndPrefersTheJsonManifest()
    {
        using var root = new TempFolder();
        root.Write("z-folder/extension.ini", "id=a.first\nname=A\nversion=1\nentry=a.py\n");
        root.Write("a-folder/manifest.ini", "id=z.last\nname=Z\nversion=1\nentry=z.py\n");
        root.Write("both/extension.json", "{\"id\":\"m.json\",\"name\":\"M\",\"version\":\"1\",\"runtime\":\"python3\",\"entry\":\"m.py\"}");
        root.Write("both/extension.ini", "id=m.ini\nname=M\nversion=1\nentry=m.py\n");
        root.Write("ini-first/extension.ini", "id=n.ini\nname=N\nversion=1\nentry=n.py\n");
        root.Write("ini-first/manifest.ini", "id=n.manifest\nname=N\nversion=1\nentry=n.py\n");
        Directory.CreateDirectory(root.Combine("no-manifest"));
        root.Write("loose-file.json", "{}");
        IReadOnlyList<ExtensionManifest> manifests = ExtensionManifestLoader.Scan(root.Path);
        Assert.Equal(new[] { "a.first", "m.json", "n.ini", "z.last" }, manifests.Select(manifest => manifest.Id));
        Assert.Equal(ExtensionManifestFormat.Json, manifests[1].Format);
        Assert.EndsWith("manifest.ini", manifests[3].ManifestFile);
        Assert.Equal(Path.GetFullPath(root.Path), manifests[0].Root);
        Assert.Equal(root.Combine("z-folder"), manifests[0].Directory);
        Assert.Empty(ExtensionManifestLoader.Scan(root.Combine("missing")));
    }

    [Fact]
    public void ALaterFolderOverridesAnEarlierOneById()
    {
        using var program = new TempFolder();
        using var user = new TempFolder();
        program.Write("shared/extension.ini", "id=shared\nname=Program copy\nversion=1\nentry=a.py\n");
        program.Write("only-program/extension.ini", "id=only.program\nname=P\nversion=1\nentry=a.py\n");
        user.Write("shared/extension.ini", "id=shared\nname=User copy\nversion=2\nentry=a.py\n");
        user.Write("only-user/extension.ini", "id=a.only.user\nname=U\nversion=1\nentry=a.py\n");
        IReadOnlyList<ExtensionManifest> merged = ExtensionManifestLoader.ScanAll(new[] { program.Path, user.Path });
        Assert.Equal(new[] { "only.program", "shared", "a.only.user" }, merged.Select(manifest => manifest.Id));
        Assert.Equal("User copy", merged[1].Name);
        Assert.Equal(Path.GetFullPath(user.Path), merged[1].Root);
    }

    [Fact]
    public void JsonAlternativeKeysAndDefaults()
    {
        using var root = new TempFolder();
        string path = root.Write("alt/extension.json", """
            {
              // comments and trailing commas are accepted
              "id": "alt", "name": "Alt", "version": "0.1", "runtime": "node",
              "entryMode": "discover",
              "requestedCapabilities": ["net", "", 5, "files"],
              "uiContributions": ["panel"],
              "api": "2",
              "nested": { "id": "not-this-one" },
              "windowActions": [
                { "id": "plain" },
                { "kind": "file-editor", "id": "other", "label": "Other", "placement": "left", "icon": "file:images/x.png" },
                { "label": "no id" },
                "not an object",
              ],
            }
            """);
        ExtensionManifest manifest = ExtensionManifestLoader.Load(path);
        Assert.True(manifest.IsValid, manifest.Error);
        Assert.Equal("alt", manifest.Id);
        Assert.True(manifest.AutoDiscover);
        Assert.Equal(new[] { "net", "files" }, manifest.RequestedCapabilities);
        Assert.Equal(new[] { "panel" }, manifest.UiContributions);
        Assert.Equal(1, manifest.Api);
        Assert.Equal(2, manifest.WindowActions.Length);
        Assert.Equal(new ExtensionWindowAction("script-editor", "plain", "plain", ExtensionActionPlacement.Top, ""), manifest.WindowActions[0]);
        Assert.Equal(new ExtensionWindowAction("file-editor", "other", "Other", ExtensionActionPlacement.Top, "file:images/x.png"), manifest.WindowActions[1]);
        Assert.Equal(manifest.Directory, manifest.OpenPath);

        string auto = root.Write("flag/extension.json", "{\"id\":\"flag\",\"name\":\"F\",\"version\":\"1\",\"runtime\":\"x\",\"autoDiscover\":true,\"api\":3}");
        ExtensionManifest flagged = ExtensionManifestLoader.Load(auto);
        Assert.True(flagged.AutoDiscover);
        Assert.Equal(3, flagged.Api);
    }

    [Fact]
    public void IniManifestEveryKey()
    {
        using var root = new TempFolder();
        string path = root.Write("full/extension.ini", "\uFEFF# comment\n; other comment\n  id = full.ini  \nname = Full INI\nversion = 2.0\npublisher = Me\nruntime = python3\nentry = main.py\n"
            + "requestedCapabilities = a, b ,, c\nuiContributions = output-tab\nthemes = t1,t2\ncommands = c1\nreadOnlyViews = v1\n"
            + "windowActions = script-editor:format:Format Code, lint, other-kind:x:, kind-only:\nnot a key line\napi = 7\n");
        ExtensionManifest manifest = ExtensionManifestLoader.Load(path);
        Assert.True(manifest.IsValid, manifest.Error);
        Assert.Equal(ExtensionManifestFormat.Ini, manifest.Format);
        Assert.Equal("full.ini", manifest.Id);
        Assert.Equal("Full INI", manifest.Name);
        Assert.Equal("2.0", manifest.Version);
        Assert.Equal("Me", manifest.Publisher);
        Assert.Equal("python3", manifest.Runtime);
        Assert.Equal("main.py", manifest.Entry);
        Assert.Equal(root.Combine("full", "main.py"), manifest.EntryPath);
        Assert.Equal(manifest.EntryPath, manifest.OpenPath);
        Assert.Equal(new[] { "a", "b", "c" }, manifest.RequestedCapabilities);
        Assert.Equal(new[] { "output-tab" }, manifest.UiContributions);
        Assert.Equal(new[] { "t1", "t2" }, manifest.Themes);
        Assert.Equal(new[] { "c1" }, manifest.Commands);
        Assert.Equal(new[] { "v1" }, manifest.ReadOnlyViews);
        Assert.Equal(1, manifest.Api);
        Assert.Equal(new[]
        {
            new ExtensionWindowAction("script-editor", "format", "Format Code", ExtensionActionPlacement.Top, ""),
            new ExtensionWindowAction("script-editor", "lint", "lint", ExtensionActionPlacement.Top, ""),
            new ExtensionWindowAction("other-kind", "x", "", ExtensionActionPlacement.Top, ""),
        }, manifest.WindowActions);

        string discover = root.Write("auto/manifest.ini", "id=auto\nname=Auto\nversion=1\nautoDiscover=1\n");
        Assert.True(ExtensionManifestLoader.Load(discover).AutoDiscover);
        Assert.True(ExtensionManifestLoader.Load(discover).IsValid);
        string mode = root.Write("mode/manifest.ini", "id=mode\nname=Mode\nversion=1\nentryMode=auto\n");
        Assert.True(ExtensionManifestLoader.Load(mode).AutoDiscover);
    }

    [Theory]
    [InlineData("{\"name\":\"N\",\"version\":\"1\",\"runtime\":\"r\",\"entry\":\"e\"}", "Manifest requires id, name, version, runtime, and entry")]
    [InlineData("{\"id\":\"x\",\"version\":\"1\",\"runtime\":\"r\",\"entry\":\"e\"}", "Manifest requires id, name, version, runtime, and entry")]
    [InlineData("{\"id\":\"x\",\"name\":\"N\",\"runtime\":\"r\",\"entry\":\"e\"}", "Manifest requires id, name, version, runtime, and entry")]
    [InlineData("{\"id\":\"x\",\"name\":\"N\",\"version\":\"1\",\"entry\":\"e\"}", "Manifest requires id, name, version, runtime, and entry")]
    [InlineData("{\"id\":\"x\",\"name\":\"N\",\"version\":\"1\",\"runtime\":\"r\"}", "Manifest requires id, name, version, runtime, and entry")]
    [InlineData("{\"id\":\"x\",\"name\":\"N\",\"version\":\"1\",\"mode\":\"auto\"}", "Manifest requires id, name, version, and runtime")]
    [InlineData("{\"id\":\"x\",\"name\":\"N\",\"version\":\"1\",\"runtime\":\"r\",\"entry\":1}", "Manifest requires id, name, version, runtime, and entry")]
    public void JsonMissingKeys(string json, string error)
    {
        using var root = new TempFolder();
        ExtensionManifest manifest = ExtensionManifestLoader.Load(root.Write("pkg/extension.json", json));
        Assert.Equal(error, manifest.Error);
        Assert.False(manifest.IsValid);
        Assert.NotEqual(string.Empty, manifest.Id);
        Assert.NotEqual(string.Empty, manifest.Name);
    }

    [Theory]
    [InlineData("name=N\nversion=1\nentry=e\n", "Manifest requires id, name, version, and entry")]
    [InlineData("id=x\nversion=1\nentry=e\n", "Manifest requires id, name, version, and entry")]
    [InlineData("id=x\nname=N\nentry=e\n", "Manifest requires id, name, version, and entry")]
    [InlineData("id=x\nname=N\nversion=1\n", "Manifest requires id, name, version, and entry")]
    [InlineData("name=N\nversion=1\nmode=auto\n", "Manifest requires id, name, and version")]
    public void IniMissingKeys(string ini, string error)
    {
        using var root = new TempFolder();
        ExtensionManifest manifest = ExtensionManifestLoader.Load(root.Write("pkg/extension.ini", ini));
        Assert.Equal(error, manifest.Error);
    }

    [Fact]
    public void AnInvalidManifestFallsBackToTheFolderName()
    {
        using var root = new TempFolder();
        root.Write("folder-name/extension.json", "{\"version\":\"1\"}");
        ExtensionManifest manifest = Assert.Single(ExtensionManifestLoader.Scan(root.Path));
        Assert.Equal("folder-name", manifest.Id);
        Assert.Equal("folder-name", manifest.Name);
        Assert.Equal(root.Combine("folder-name"), manifest.Directory);

        root.Write("named/extension.json", "{\"name\":\"Has A Name\"}");
        ExtensionManifest named = ExtensionManifestLoader.Scan(root.Path).Single(value => value.Id == "named");
        Assert.Equal("Has A Name", named.Name);
    }

    [Theory]
    [InlineData("{\"id\":\"x\",")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void BadJsonIsListedWithAnError(string json)
    {
        using var root = new TempFolder();
        root.Write("bad/extension.json", json);
        ExtensionManifest manifest = Assert.Single(ExtensionManifestLoader.Scan(root.Path));
        Assert.StartsWith("Manifest is not valid JSON", manifest.Error);
        Assert.Equal("bad", manifest.Id);
    }

    [Fact]
    public void JsonThatIsNotAnObjectIsAnError()
    {
        using var root = new TempFolder();
        Assert.Equal("Manifest is not a JSON object", ExtensionManifestLoader.Load(root.Write("arr/extension.json", "[1,2]")).Error);
    }

    [Theory]
    [InlineData("../outside.py")]
    [InlineData("sub/../../outside.py")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/notepad.exe")]
    [InlineData("..\\outside.py")]
    public void AnEntryThatLeavesThePackageIsRefused(string entry)
    {
        using var root = new TempFolder();
        string json = "{\"id\":\"esc\",\"name\":\"E\",\"version\":\"1\",\"runtime\":\"python3\",\"entry\":" + System.Text.Json.JsonSerializer.Serialize(entry) + "}";
        ExtensionManifest manifest = ExtensionManifestLoader.Load(root.Write("esc/extension.json", json));
        Assert.StartsWith("Entry is not inside the package", manifest.Error);
        Assert.Equal(string.Empty, manifest.EntryPath);
        Assert.False(ExtensionManager.TryBuildCommand(manifest, out _, out _));
    }

    [Fact]
    public void AnIdWithSpacesIsAnError()
    {
        using var root = new TempFolder();
        ExtensionManifest manifest = ExtensionManifestLoader.Load(root.Write("sp/extension.ini", "id=has space\nname=N\nversion=1\nentry=a\n"));
        Assert.Equal("Manifest id must not contain white space", manifest.Error);
    }

    [Fact]
    public void IconsResolveInsideThePackageOnly()
    {
        using var root = new TempFolder();
        string path = root.Write("icons/extension.json", "{\"id\":\"icons\",\"name\":\"I\",\"version\":\"1\",\"runtime\":\"r\",\"entry\":\"e\",\"windowActions\":["
            + "{\"id\":\"a\",\"icon\":\"gtk:system-run-symbolic\"},{\"id\":\"b\",\"icon\":\"file:direct.png\"},{\"id\":\"c\",\"icon\":\"inimages.png\"},"
            + "{\"id\":\"d\",\"icon\":\"file:../../secret.png\"},{\"id\":\"e\",\"icon\":\"missing-icon\"},{\"id\":\"f\"}]}");
        root.Write("icons/direct.png", "x");
        root.Write("icons/images/inimages.png", "x");
        root.Write("secret.png", "x");
        ExtensionManifest manifest = ExtensionManifestLoader.Load(path);
        ExtensionIcon[] icons = manifest.WindowActions.Select(manifest.ResolveIcon).ToArray();
        Assert.Equal(new ExtensionIcon(ExtensionIconKind.Named, "system-run-symbolic"), icons[0]);
        Assert.Equal(new ExtensionIcon(ExtensionIconKind.File, root.Combine("icons", "direct.png")), icons[1]);
        Assert.Equal(new ExtensionIcon(ExtensionIconKind.File, root.Combine("icons", "images", "inimages.png")), icons[2]);
        Assert.Equal(ExtensionIconKind.Named, icons[3].Kind);
        Assert.Equal(new ExtensionIcon(ExtensionIconKind.Named, "missing-icon"), icons[4]);
        Assert.Equal(ExtensionIcon.None, icons[5]);
    }

    [Fact]
    public void CommandLinesFollowTheCpp()
    {
        using var root = new TempFolder();
        root.Write("p/main.py", "");
        ExtensionManifest withRuntime = ExtensionManifestLoader.Load(root.Write("p/extension.json", "{\"id\":\"p\",\"name\":\"P\",\"version\":\"1\",\"runtime\":\"python3\",\"entry\":\"main.py\"}"));
        Assert.True(ExtensionManager.TryBuildCommand(withRuntime, out IReadOnlyList<string> arguments, out _));
        Assert.Equal(new[] { "python3", root.Combine("p", "main.py") }, arguments);

        root.Write("q/tool", "");
        ExtensionManifest direct = ExtensionManifestLoader.Load(root.Write("q/extension.ini", "id=q\nname=Q\nversion=1\nentry=tool\n"));
        Assert.True(ExtensionManager.TryBuildCommand(direct, out arguments, out _));
        Assert.Equal(new[] { root.Combine("q", "tool") }, arguments);

        ExtensionManifest auto = ExtensionManifestLoader.Load(root.Write("r/extension.json", "{\"id\":\"r\",\"name\":\"R\",\"version\":\"1\",\"runtime\":\"node\",\"mode\":\"auto\"}"));
        Assert.True(ExtensionManager.TryBuildCommand(auto, out arguments, out _));
        Assert.Equal(new[] { "node" }, arguments);

        ExtensionManifest missing = ExtensionManifestLoader.Load(root.Write("s/extension.json", "{\"id\":\"s\",\"name\":\"S\",\"version\":\"1\",\"runtime\":\"python3\",\"entry\":\"absent.py\"}"));
        Assert.False(ExtensionManager.TryBuildCommand(missing, out _, out string error));
        Assert.Equal("Entry not found: " + root.Combine("s", "absent.py"), error);

        root.Write("t/main.gs2", "");
        ExtensionManifest api = ExtensionManifestLoader.Load(root.Write("t/extension.json", "{\"id\":\"t\",\"name\":\"T\",\"version\":\"1\",\"runtime\":\"gs2engine-stdio\",\"entry\":\"main.gs2\",\"api\":2}"));
        Assert.False(ExtensionManager.TryBuildCommand(api, out _, out error));
        Assert.Equal("Unsupported GS2 extension API: 2", error);

        ExtensionManifest host = ExtensionManifestLoader.Load(root.Write("u/extension.json", "{\"id\":\"u\",\"name\":\"U\",\"version\":\"1\",\"runtime\":\"gs2engine-stdio\",\"mode\":\"auto\"}"));
        Assert.False(ExtensionManager.TryBuildCommand(host, out _, out error));
        Assert.StartsWith("GS2Engine host not found: " + root.Combine("u", "GS2Engine"), error);
        root.Write(OperatingSystem.IsWindows() ? "u/GS2Engine.exe" : "u/GS2Engine", "");
        Assert.True(ExtensionManager.TryBuildCommand(host, out arguments, out _));
        Assert.Equal("--extension-stdio", arguments[1]);
    }
}
