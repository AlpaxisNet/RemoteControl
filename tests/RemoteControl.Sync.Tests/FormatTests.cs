using System.Security.Cryptography;
using System.Text;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Sync.Tests;

public sealed class NameTests
{
    [Theory]
    [InlineData("Bow", "Bow")]
    [InlineData("Foo Bar", "Foo%032Bar")]
    [InlineData("a-b_c.d", "a%045b%095c%046d")]
    [InlineData("../x", "%046%046%047x")]
    [InlineData("a/b\\c", "a%047b%092c")]
    [InlineData("C:x", "C%058x")]
    [InlineData("\u00e9", "%233")]
    [InlineData("", "")]
    public void GraalEncodingKeepsLettersAndDigitsOnly(string name, string expected)
    {
        string encoded = SyncNames.EncodeGraalFileName(name);
        Assert.Equal(expected, encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('\\', encoded);
        Assert.DoesNotContain('.', encoded);
        Assert.Equal(name, SyncNames.DecodeGraalFileName(encoded + "x")[..^1]);
    }

    [Fact]
    public void DecodingFollowsTheCxx()
    {
        Assert.Equal("a b", SyncNames.DecodeGraalFileName("a%032b"));
        Assert.Equal("%03", SyncNames.DecodeGraalFileName("%03"));
        Assert.Equal(" ", SyncNames.DecodeGraalFileName("%032"));
        Assert.Equal("%x12", SyncNames.DecodeGraalFileName("%x12"));
        Assert.Equal(((char)0xE7).ToString() + "z", SyncNames.DecodeGraalFileName("%999z"));
    }

    [Theory]
    [InlineData("My Server", "My_Server")]
    [InlineData("a.b-c_d", "a.b-c_d")]
    [InlineData("name...", "name")]
    [InlineData("", "script")]
    [InlineData("...", "script")]
    [InlineData("../../etc/passwd", ".._.._etc_passwd")]
    public void SafeNameMatchesTheCxx(string value, string expected) => Assert.Equal(expected, SyncNames.SafeName(value));

    [Fact]
    public void StableSuffixIsTheCxxHash()
    {
        // TSyncManager.cpp:32 starts FNV-1a 64 at 1469598103934665603 (the standard basis without its last digit); kept for existing files.
        Assert.Equal("739d0383", SyncNames.StableSuffix(string.Empty));
        Assert.Equal("73cd9906", SyncNames.StableSuffix("a"));
        Assert.Equal(Path.Combine("/u", "sync", "My_Server-" + SyncNames.StableSuffix("My Server") + ".conf"), SyncSettingsFile.PathFor("/u", "My Server"));
    }

    [Theory]
    [InlineData(" /levels//a/./b/ ", "levels/a/b")]
    [InlineData("a/../b", "b")]
    [InlineData("../x", "")]
    [InlineData("a/../../x", "")]
    [InlineData("\\x\\y", "x/y")]
    [InlineData(".", "")]
    public void RemotePathsAreNormalised(string path, string expected) => Assert.Equal(expected, SyncNames.NormalizeRemotePath(path));

    [Fact]
    public void ScriptPathsAreTheCxxPathsAndAlwaysSafe()
    {
        Assert.Equal("weapons/weaponBow.txt", SyncNames.ScriptPath(ScriptKind.Weapon, "Bow"));
        Assert.Equal("weapons/weaponSystem%047Main.txt", SyncNames.ScriptPath(ScriptKind.Weapon, "System/Main"));
        Assert.Equal("npcs/npcGuard%032One.txt", SyncNames.ScriptPath(ScriptKind.Npc, "Guard One"));
        Assert.Equal("classes/util_x.txt", SyncNames.ScriptPath(ScriptKind.Class, "util/x"));
        foreach (string hostile in new[] { "..", "../../etc/passwd", "/abs", "C:\\x", "con", "NUL", "a:b", "x\u0001y" })
        {
            foreach (ScriptKind kind in new[] { ScriptKind.Weapon, ScriptKind.Npc })
                Assert.True(SyncNames.IsSafeWorkspacePath(SyncNames.ScriptPath(kind, hostile)), hostile);
            Assert.True(SyncNames.IsSafeWorkspacePath(SyncNames.ScriptPath(ScriptKind.Class, hostile)) || SyncNames.IsSafeWorkspacePath(SyncNames.AlternateScriptPath(ScriptKind.Class, hostile, 0)), hostile);
        }

        Assert.False(SyncNames.IsSafeWorkspacePath("classes/con.txt"));
        Assert.True(SyncNames.IsSafeWorkspacePath(SyncNames.AlternateScriptPath(ScriptKind.Class, "con", 0)));
        Assert.Equal("npcs/npcGuard-12.txt", SyncNames.AlternateScriptPath(ScriptKind.Npc, "Guard", 12));
    }

    [Theory]
    [InlineData("weapons/weaponBow.txt", true)]
    [InlineData("files/levels/a.nw", true)]
    [InlineData("../weapons/x.txt", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("weapons/../../x", false)]
    [InlineData("C:/x/y", false)]
    [InlineData("files/a:b", false)]
    [InlineData("files/con.nw", false)]
    [InlineData("other/x.txt", false)]
    [InlineData("weapons", false)]
    public void ManifestPathsMustStayInTheWorkspace(string path, bool safe) => Assert.Equal(safe, SyncNames.IsSafeWorkspacePath(path));

    [Fact]
    public void FoldersAreSplitAndJoinedLikeTheCxx()
    {
        Assert.Equal(new[] { "levels", "images/x", "ganis" }, SyncNames.SplitFolders(" levels/ ;images\\x,\nganis; levels;;../up"));
        Assert.Equal("a; b", SyncNames.JoinFolders(new[] { "a", "b" }));
    }

    [Fact]
    public void RootSelectionAddsSubRootsAndSorts()
    {
        Assert.Equal(new[] { "a", "b" }, SyncEngine.SelectRoots(Array.Empty<string>(), new[] { "b", "a" }));
        Assert.Equal(new[] { "levels", "levels/sub" }, SyncEngine.SelectRoots(new[] { "levels" }, new[] { "images", "levels/sub", "levelsx" }));
    }
}

public sealed class ExclusionTests
{
    [Theory]
    [InlineData("levels/a.bak", true)]
    [InlineData("levels/A.BAK", true)]
    [InlineData("x/y.tmp", true)]
    [InlineData("secret/plan.txt", true)]
    [InlineData("levels/secret.txt", false)]
    [InlineData("levels/thumbs.db", true)]
    [InlineData("levels/a.nw", false)]
    public void PatternsMatchPathOrName(string path, bool excluded)
    {
        var list = new ExclusionList("*.bak; .tmp,\nsecret/*\r\nThumbs.db");
        Assert.Equal(excluded, list.IsExcluded(path));
    }

    [Theory]
    [InlineData("*", "", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("*.nw", "x.nw", true)]
    [InlineData("a*b*c", "aXbYc", true)]
    [InlineData("a*b*c", "aXbY", false)]
    public void GlobMatchesLikeGlib(string pattern, string text, bool matches) => Assert.Equal(matches, ExclusionList.Matches(pattern, text));
}

public sealed class KeyFileTests
{
    [Fact]
    public void EscapesRoundTrip()
    {
        var file = new KeyFile();
        file.SetString("g", "k", "  lead\ttab\nline\\slash\r");
        string text = file.Serialize();
        Assert.Equal("[g]\nk=\\s\\slead\ttab\\nline\\\\slash\\r\n", text);
        Assert.True(KeyFile.TryParse(text, out KeyFile parsed, out _));
        Assert.Equal("  lead\ttab\nline\\slash\r", parsed.GetString("g", "k"));
    }

    [Fact]
    public void ParsesGlibFilesAndRefusesGarbage()
    {
        Assert.True(KeyFile.TryParse("# comment\n\n[a]\nx = 1\r\ny=true\n[b]\nz=\n", out KeyFile file, out _));
        Assert.Equal(1, file.GetInteger("a", "x"));
        Assert.True(file.GetBoolean("a", "y"));
        Assert.Equal(string.Empty, file.GetString("b", "z"));
        Assert.Equal(new[] { "a", "b" }, file.Groups);
        Assert.False(KeyFile.TryParse("x=1\n", out _, out _));
        Assert.False(KeyFile.TryParse("[a]\nnot a key line\n", out _, out _));
    }
}

public sealed class SettingsFileTests
{
    [Fact]
    public void ReadsTheCxxFile()
    {
        const string text = "[sync]\nworkspace=/home/u/ws\nautoStart=true\nscripts=false\nfiles=true\nserverConfig=true\ndirection=2\nallowRemoteDeletes=true\n"
            + "fileFolders=levels; images\nexcludeFiles=*.bak; .tmp\n\n[git]\nautoCommit=true\nautoPush=false\nautoPull=true\nremoteUrl=git@example.com:x.git\n";
        Assert.True(SyncSettingsFile.TryParse(text, out SyncSettings settings, out _));
        Assert.Equal(new SyncSettings
        {
            Workspace = "/home/u/ws",
            AutoStart = true,
            Scripts = false,
            Files = true,
            ServerConfig = true,
            Direction = SyncDirection.TwoWay,
            AllowRemoteDeletes = true,
            FileFolders = "levels; images",
            ExcludeFiles = "*.bak; .tmp",
            GitAutoCommit = true,
            GitAutoPush = false,
            GitAutoPull = true,
            GitRemoteUrl = "git@example.com:x.git",
        }, settings);
        Assert.Equal(text, SyncSettingsFile.Serialize(settings));
    }

    [Fact]
    public void MissingKeysGetTheCxxDefaults()
    {
        Assert.True(SyncSettingsFile.TryParse("[sync]\n", out SyncSettings settings, out _));
        Assert.Equal(SyncSettings.Default, settings);
        Assert.True(settings.Scripts);
        Assert.Equal(SyncDirection.DownloadOnly, settings.Direction);
        Assert.True(SyncSettingsFile.TryParse("[sync]\ndirection=7\nallowRemoteDeletes=true\n", out SyncSettings odd, out _));
        Assert.Equal(SyncDirection.DownloadOnly, odd.Direction);
        Assert.False(odd.AllowRemoteDeletes);
        Assert.Contains("allowRemoteDeletes=false", SyncSettingsFile.Serialize(new SyncSettings { AllowRemoteDeletes = true }));
    }

    [Fact]
    public void AnUnreadableFileIsKeptAside()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "sync", "x.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "this is not a key file\n");
        SyncSettingsLoadResult result = SyncSettingsFile.Load(path);
        Assert.False(result.Existed);
        Assert.NotNull(result.Warning);
        Assert.True(result.MayOverwrite);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "x.conf.unreadable-*"));
        SyncSettingsFile.Save(path, new SyncSettings { Workspace = "/w" });
        Assert.Equal("/w", SyncSettingsFile.Load(path).Settings.Workspace);
    }
}

public sealed class ManifestTests
{
    [Fact]
    public void ReadsACxxManifestAndDropsUnsafeOrUntrustedValues()
    {
        string text = string.Join('\n',
            "[weapon:Bow]", "type=weapon", "name=Bow", "image=bow.png", "npcType=", "level=", "id=0", "modified=1700000000", "path=weapons/weaponBow.txt", "",
            "[class:util]", "type=class", "name=util", "image=", "npcType=", "level=", "id=0", "modified=0", "path=classes/util.txt", "",
            "[npc:7]", "type=npc", "name=Guard", "image=", "npcType=OBJECT", "level=", "id=7", "modified=5", "path=npcs/npcGuard.txt", "",
            "[file:12345678]", "type=file", "name=levels/a.nw", "path=files/levels/a.nw", "size=3", "modified=1000", "",
            "[config:options]", "type=config", "name=options", "path=config/serveroptions.txt", "localHash=1234567890123", "",
            "[weapon:Evil]", "type=weapon", "name=Evil", "path=../../etc/passwd", "",
            "[weapon:Abs]", "type=weapon", "name=Abs", "path=/etc/passwd", "",
            "[class:Wrong]", "type=class", "name=Wrong", "path=weapons/weaponWrong.txt", "",
            "[file:00000000]", "type=file", "name=levels/b.nw", "path=files/other/b.nw", "") + "\n";
        Assert.True(SyncManifest.TryParse(text, out SyncManifest manifest, out _));
        Assert.Equal(3, manifest.Scripts.Count);
        ManifestScript bow = manifest.Scripts[0];
        Assert.Equal(new ManifestScript(ScriptKind.Weapon, "Bow", "bow.png", string.Empty, string.Empty, 0, 1700000000, "weapons/weaponBow.txt", null, null), bow);
        Assert.Equal("npc:7", manifest.Scripts[2].Key);
        Assert.Equal(new ManifestFile("levels/a.nw", "files/levels/a.nw", 3, 1000, null), Assert.Single(manifest.Files));
        Assert.Null(Assert.Single(manifest.Configs).LocalHash);
        Assert.Equal(4, manifest.Skipped.Count);
    }

    [Fact]
    public void RoundTripsWithHashes()
    {
        string hash = SyncHash.Of("x"u8);
        var manifest = new SyncManifest();
        manifest.Scripts.Add(new ManifestScript(ScriptKind.Npc, "Guard [1]", "img", "OBJECT", string.Empty, 3, 9, "npcs/npcGuard%032%0911%093.txt", hash, hash));
        manifest.Scripts.Add(new ManifestScript(ScriptKind.Weapon, "Bow", string.Empty, string.Empty, string.Empty, 0, 0, "weapons/weaponBow.txt", hash, null));
        manifest.Files.Add(new ManifestFile("levels/a b.nw", "files/levels/a b.nw", 3, 4, hash));
        manifest.Configs.Add(new ManifestConfig(SyncConfigKind.FolderConfig, "config/folderconfig.txt", hash));
        string text = manifest.Serialize();
        Assert.Contains("[file:" + SyncNames.StableSuffix("levels/a b.nw") + "]", text);
        Assert.Contains("[config:folder_config]", text);
        Assert.True(SyncManifest.TryParse(text, out SyncManifest parsed, out _));
        Assert.Equal(manifest.Scripts.OrderBy(item => item.Key, StringComparer.Ordinal), parsed.Scripts);
        Assert.Equal(manifest.Files, parsed.Files);
        Assert.Equal(manifest.Configs, parsed.Configs);
        Assert.Empty(parsed.Skipped);
    }
}

public sealed class ScriptDocumentTests
{
    [Fact]
    public void BuildsTheCxxDocuments()
    {
        Assert.Equal("GRAWP001\nREALNAME Bow\nIMAGE bow.png\nSCRIPT\n//a\n//b\nSCRIPTEND\n", ScriptDocument.BuildWeapon("Bow", "bow.png", "//a\n//b"));
        Assert.Equal("GRNPC001\nNAME Guard\nID 7\nTYPE OBJECT\nFLAG a=1\nFLAG b=2\nNPCSCRIPT\nx\nNPCSCRIPTEND\n", ScriptDocument.BuildNpc("Guard", 7, string.Empty, string.Empty, string.Empty, "a=1\n\nb=2\n", "x"));
        Assert.Equal("GRNPC001\nNAME G\nID 1\nTYPE LOCALN\nIMAGE i.png\nLEVEL l.nw\nNPCSCRIPT\n\nNPCSCRIPTEND\n", ScriptDocument.BuildNpc("G", 1, "LOCALN", "i.png", "l.nw", null, string.Empty));
    }

    [Theory]
    [InlineData("//a\n//b")]
    [InlineData("")]
    [InlineData("SCRIPT\nSCRIPTEND inside")]
    [InlineData("line\r\nwith crlf")]
    public void ExtractsWhatWasWrapped(string script)
    {
        Assert.Equal(script, ScriptDocument.ExtractScript(ScriptKind.Weapon, ScriptDocument.BuildWeapon("SCRIPT", "SCRIPT", script)));
        Assert.Equal(script, ScriptDocument.ExtractScript(ScriptKind.Npc, ScriptDocument.BuildNpc("NPCSCRIPT", 1, "", "", "", "NPCSCRIPT", script)));
        Assert.Equal(script, ScriptDocument.ExtractScript(ScriptKind.Class, script));
    }

    [Fact]
    public void AcceptsWindowsLineEndsAndRefusesMissingMarkers()
    {
        Assert.Equal("a\r\nb", ScriptDocument.ExtractScript(ScriptKind.Weapon, "GRAWP001\r\nREALNAME x\r\nIMAGE \r\nSCRIPT\r\na\r\nb\r\nSCRIPTEND\r\n"));
        Assert.Null(ScriptDocument.ExtractScript(ScriptKind.Weapon, "GRAWP001\nREALNAME x\nno markers\n"));
        Assert.Null(ScriptDocument.ExtractScript(ScriptKind.Npc, "GRNPC001\nNPCSCRIPT\nno end"));
    }

    [Fact]
    public void ParsesHeaders()
    {
        ParsedScriptDocument? npc = ScriptDocument.TryParse(ScriptDocument.BuildNpc("Guard", 7, "OBJECT", "g.png", "", "a=1\nb=2", "s"));
        Assert.NotNull(npc);
        Assert.Equal(("Guard", 7, "OBJECT", "g.png", "s"), (npc.Name, npc.NpcId, npc.NpcType, npc.Image, npc.Script));
        Assert.Equal(new[] { "a=1", "b=2" }, npc.Flags);
        ParsedScriptDocument? weapon = ScriptDocument.TryParse(ScriptDocument.BuildWeapon("Bow", "bow.png", "x"));
        Assert.Equal(("Bow", "bow.png", "x"), (weapon!.Name, weapon.Image, weapon.Script));
        Assert.Null(ScriptDocument.TryParse("just a class"));
    }
}

public sealed class CredentialTests
{
    [Fact]
    public async Task StoresAndReadsKeysAndTokens()
    {
        using var folder = new TempFolder();
        var store = new GitCredentialStore(folder.Path, "Server");
        const string key = "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----\n";
        Assert.NotNull(await store.StoreKeyAsync("not a key"));
        Assert.Null(await store.StoreKeyAsync(key));
        Assert.Null(await store.StoreTokenAsync("ghp_secret"));
        Assert.Equal(key, await store.LoadKeyAsync());
        Assert.Equal("ghp_secret", await store.LoadTokenAsync());
        Assert.Equal(Path.ChangeExtension(SyncSettingsFile.PathFor(folder.Path, "Server"), ".credentials"), store.KeyPath);
        byte[] stored = File.ReadAllBytes(store.TokenPath);
        Assert.Equal("GSRCGIT1"u8.ToArray(), stored[..8]);
        Assert.DoesNotContain("ghp_secret", Encoding.Latin1.GetString(stored));
        Assert.Equal(32, File.ReadAllBytes(Path.Combine(folder.Path, "accounts.key")).Length);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.TokenPath));
        Assert.True(store.ClearToken());
        Assert.Null(await store.LoadTokenAsync());
    }

    [Fact]
    public async Task ReadsWhatTheCxxWrote()
    {
        using var folder = new TempFolder();
        byte[] accountsKey = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(Path.Combine(folder.Path, "accounts.key"), accountsKey);
        var store = new GitCredentialStore(folder.Path, "Server");

        // TEncryption.cpp: magic || nonce(12) || tag(16) || ciphertext, AES-256-GCM without associated data.
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plaintext = "token-from-cxx"u8.ToArray();
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(accountsKey, 16)) aes.Encrypt(nonce, plaintext, ciphertext, tag);
        Directory.CreateDirectory(Path.GetDirectoryName(store.TokenPath)!);
        File.WriteAllBytes(store.TokenPath, "GSRCGIT1"u8.ToArray().Concat(nonce).Concat(tag).Concat(ciphertext).ToArray());
        Assert.Equal("token-from-cxx", await store.LoadTokenAsync());

        File.WriteAllBytes(store.TokenPath, "GSRCGIT1"u8.ToArray().Concat(nonce).Concat(new byte[16]).Concat(ciphertext).ToArray());
        Assert.Null(await store.LoadTokenAsync());
    }

    [Fact]
    public async Task NeverReplacesAnInvalidAccountsKey()
    {
        using var folder = new TempFolder();
        string keyFile = Path.Combine(folder.Path, "accounts.key");
        File.WriteAllBytes(keyFile, new byte[] { 1, 2, 3 });
        var store = new GitCredentialStore(folder.Path, "Server");
        Assert.NotNull(await store.StoreTokenAsync("secret"));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(keyFile));
        Assert.False(store.HasToken);
    }
}
