using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Common.Tests;

public sealed class AssetStoreTests
{
    [AvaloniaFact]
    public void Missing_or_broken_pictures_give_the_placeholder_and_one_log_line()
    {
        using var env = new TestEnvironment();
        var assets = new AssetStore(env.Paths, env.Log);
        Bitmap missing = assets.Get("no_such_picture.png");
        Assert.Same(assets.Placeholder, missing);
        Assert.Equal(new Avalonia.PixelSize(16, 16), missing.PixelSize);
        Assert.Same(assets.Placeholder, assets.Get("no_such_picture.png"));
        Assert.Single(env.Log.Snapshot(), line => line.Contains("no_such_picture.png", StringComparison.Ordinal));
        Assert.False(assets.TryGet("no_such_picture.png", out _));

        string broken = Path.Combine(env.UserDirectory, "broken.png");
        File.WriteAllBytes(broken, [0x89, 0x50, 0x4e, 0x47, 1, 2, 3]);
        Assert.Same(assets.Placeholder, assets.Get(broken));
        Assert.Contains(env.Log.Snapshot(), line => line.Contains("broken.png", StringComparison.Ordinal));
        Assert.Same(assets.Placeholder, assets.Get(""));
    }

    [AvaloniaFact]
    public void Pictures_are_cached_and_the_cache_is_bounded()
    {
        using var env = new TestEnvironment();
        var assets = new AssetStore(env.Paths, env.Log, capacity: 8);
        Bitmap kappa = assets.Get("emote_kappa.png");
        Assert.NotSame(assets.Placeholder, kappa);
        Assert.Same(kappa, assets.Get("emote_kappa.png"));
        Assert.Equal(new Avalonia.PixelSize(25, 28), kappa.PixelSize);
        foreach (string file in Directory.GetFiles(env.Paths.ImagesDirectory, "plisticon*.png")) assets.Get(Path.GetFileName(file));
        Assert.Equal(8, assets.CachedCount);
        Assert.NotSame(kappa, assets.Get("emote_kappa.png"));
        assets.Clear();
        Assert.Equal(0, assets.CachedCount);
    }

    [Theory]
    [InlineData("Away", "plisticonaway.png")]
    [InlineData("No PMs", "plisticonno pms.png")]
    [InlineData("rping", "plisticonrole-playing.png")]
    [InlineData("Free To Chat", "plisticonfree to chat.png")]
    public void Status_icon_names(string status, string file) => Assert.Equal(file, AssetStore.StatusIconName(status));

    [AvaloniaFact]
    public void Unknown_statuses_show_the_online_icon()
    {
        using var env = new TestEnvironment();
        AssetStore assets = env.Services.Assets;
        Bitmap online = assets.Get(AssetStore.OnlineStatusIcon);
        Assert.Same(online, assets.StatusIcon("does not exist"));
        Assert.Same(online, assets.StatusIcon(null));
        Assert.NotSame(online, assets.StatusIcon("Away"));
        Assert.Same(assets.Get("pmicon_guild.png"), assets.PmIcon(AssetStore.ParsePmKind("guild")));
        Assert.Same(assets.Get("rcicon_bronze.png"), assets.ServerTypeIcon(2));
        Assert.NotNull(assets.EmoteImage("Kappa"));
        Assert.Null(assets.EmoteImage("NotAnEmote"));
        Assert.NotSame(assets.Placeholder, assets.ChannelIcon(true));
        Assert.NotSame(assets.Placeholder, assets.BanIcon(false));
    }

    [Theory]
    [InlineData("levels/start.nw", "rcfiles_nw.png")]
    [InlineData("world.GMAP", "rcfiles_gmap.png")]
    [InlineData("a.graal", "rcfiles_graal.png")]
    [InlineData("notes.txt", "rcfiles_text2.png")]
    [InlineData("font.otf", "rcfiles_ttf.png")]
    [InlineData("pack.7z", "rcfiles_archive.png")]
    [InlineData("compiled.gs2bc", "rcfiles_binary.png")]
    [InlineData("server.conf", "rcfiles_conf.png")]
    [InlineData("serveroptions.txt", "rcfiles_conf.png")]
    [InlineData("config/start.nw", "rcfiles_conf.png")]
    [InlineData("picture.png", "rcfiles_unknown.png")]
    [InlineData("noextension", "rcfiles_unknown.png")]
    [InlineData(null, "rcfiles_unknown.png")]
    public void File_type_icons(string? path, string icon) => Assert.Equal(icon, AssetStore.FileTypeIconName(path));

    [Fact]
    public void Pm_and_server_icon_names()
    {
        Assert.Equal(PmKind.Mass, AssetStore.ParsePmKind("mass"));
        Assert.Equal(PmKind.Normal, AssetStore.ParsePmKind("other"));
        Assert.Equal("pmicon_admin.png", AssetStore.PmIconName(PmKind.Admin));
        Assert.Equal("pmicon_normal.png", AssetStore.PmIconName(PmKind.Normal));
        Assert.Equal("rcicon_gold.png", AssetStore.ServerTypeIconName(0));
        Assert.Equal("rcicon_uc.png", AssetStore.ServerTypeIconName(1));
        Assert.Equal("rcicon_silver.png", AssetStore.ServerTypeIconName(3));
        Assert.Equal("rcicon_gold.png", AssetStore.ServerTypeIconName(99));
        Assert.Equal(["Kappa", "PMNormal", ":v"], AssetStore.Emotes.Select(emote => emote.Text));
    }

    [AvaloniaFact]
    public void The_bundled_font_is_registered()
    {
        Assert.True(BundledFonts.Register(), string.Join("\n", RemoteControl.Core.AppLog.Default.Snapshot(5)));
        Assert.True(BundledFonts.IsRegistered);
        var typeface = new Avalonia.Media.Typeface(BundledFonts.TempusSans);
        Assert.True(Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(typeface, out Avalonia.Media.IGlyphTypeface? glyphs));
        Assert.Equal(BundledFonts.TempusSansName, glyphs!.FamilyName);
    }
}

public sealed class SoundPlayerTests
{
    [AvaloniaFact]
    public void Never_throws_and_is_silent_when_disabled()
    {
        using var env = new TestEnvironment();
        SoundPlayer sounds = env.Services.Sounds;
        Assert.False(sounds.Enabled);
        Assert.False(sounds.Beep());
        Assert.False(sounds.Play("itemget.wav"));
        Assert.False(sounds.Play(""));
        Assert.False(sounds.Play(null!));
        Assert.Equal(0, sounds.StartedCount);
        _ = sounds.ExternalPlayer;
    }

    [AvaloniaFact]
    public void A_missing_file_is_logged_not_played()
    {
        using var env = new TestEnvironment();
        var sounds = new SoundPlayer(env.Paths, env.Log, enabled: true);
        Assert.False(sounds.Play("no-such-sound.wav"));
        Assert.False(sounds.Play(Path.Combine(env.UserDirectory, "missing.wav")));
        Assert.False(sounds.Play("../\0bad"));
        Assert.Equal(0, sounds.StartedCount);
        Assert.Contains(env.Log.Snapshot(), line => line.Contains("no-such-sound.wav", StringComparison.Ordinal));
    }
}
