using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RemoteControl.Core;
using RemoteControl.Ui.TestKit;
using SkiaSharp;

namespace RemoteControl.Ui.Common.Tests;

public sealed class ImageButtonTests
{
    [AvaloniaFact]
    public void Shows_the_pressed_picture_and_clicks_on_release_inside()
    {
        using var env = new TestEnvironment();
        var button = ImageButton.FromAssets(env.Services.Assets, "rc_options_normal.png", "rc_options_normal.png");
        button.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        button.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        ToolTip.SetTip(button, "Options");
        int clicks = 0;
        button.Click += (_, _) => clicks++;
        var window = env.Show(new RcWindow(env.Services) { Content = button }, 120, 80);
        Assert.Equal(new Size(32, 32), button.Bounds.Size);
        Assert.NotSame(button.Source, button.PressedSource);

        TestUi.MouseDown(button);
        Assert.True(button.IsPressed);
        Assert.Equal(0, clicks);
        TestUi.MouseUp(button);
        Assert.False(button.IsPressed);
        Assert.Equal(1, clicks);

        TestUi.MouseDown(button);
        TestUi.MouseUp(button, new Point(80, 60));
        Assert.False(button.IsPressed);
        Assert.Equal(1, clicks);

        button.PerformClick();
        Assert.Equal(2, clicks);
        Assert.Equal("Options", ToolTip.GetTip(button));
        Shots.Save(window, "imagebutton");
    }

    [AvaloniaFact]
    public void The_pressed_picture_follows_the_cpp_rule()
    {
        using var env = new TestEnvironment();
        AssetStore assets = env.Services.Assets;
        Assert.Equal("rc_help_pressed.png", assets.PressedImageName("rc_help_normal.png", "rc_help_normal.png"));
        Assert.Equal("custom.png", assets.PressedImageName("rc_help_normal.png", "custom.png"));
        Assert.Equal("emote_kappa.png", assets.PressedImageName("emote_kappa.png", "emote_kappa.png"));
        Assert.Equal("rc_nothing_normal.png", assets.PressedImageName("rc_nothing_normal.png", "rc_nothing_normal.png"));
    }

    [AvaloniaFact]
    public void A_disabled_button_does_not_click()
    {
        using var env = new TestEnvironment();
        var button = new ImageButton { Source = env.Services.Assets.Get("rc_help_normal.png"), IsEnabled = false };
        bool clicked = false;
        button.Click += (_, _) => clicked = true;
        env.Show(new RcWindow(env.Services) { Content = new StackPanel { Children = { button } } }, 100, 60);
        TestUi.Click(button);
        Assert.False(clicked);
    }
}

public sealed class ShadowLabelTests
{
    [AvaloniaFact]
    public void Draws_the_text_with_an_outline_in_the_label_colours()
    {
        using var env = new TestEnvironment(theme: "dark", options: o => o with { ColorLabel = "#00ff00", ColorLabelBack = "#ff0000" });
        var label = new ShadowLabel { Text = "Players: 12", FontSize = 40, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = env.Show(new RcWindow(env.Services) { Content = label, Background = Brushes.Black }, 400, 100);
        Assert.Equal(Color.Parse("#00ff00"), ((ISolidColorBrush)label.Foreground!).Color);
        Assert.Equal(Color.Parse("#ff0000"), ((ISolidColorBrush)label.Outline!).Color);
        Assert.True(label.Bounds.Width > 100);
        using var frame = Shots.Capture(window);
        bool green = false, red = false;
        for (int x = 0; x < (int)label.Bounds.Width; x++)
            for (int y = 0; y < (int)label.Bounds.Height; y++)
            {
                Color pixel = Pixels.At(frame, x, y);
                if (pixel.G > 200 && pixel.R < 60) green = true;
                if (pixel.R > 200 && pixel.G < 60) red = true;
            }
        Assert.True(green, "text colour drawn");
        Assert.True(red, "outline colour drawn");
        Shots.Save(window, "shadowlabel");
    }
}

public sealed class AnimatedImageTests
{
    private static string Write(TestEnvironment env, string name, byte[] content)
    {
        string path = Path.Combine(env.UserDirectory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [AvaloniaFact]
    public void Shows_a_still_png_stretched_with_a_tint()
    {
        using var env = new TestEnvironment();
        string path = Write(env, "still.png", TestImages.Png(4, 2, new SKColor(255, 0, 0)));
        var image = new AnimatedImage();
        Assert.True(image.Load(path));
        Assert.Equal(1, image.FrameCount);
        Assert.False(image.IsAnimated);
        Assert.Equal(new PixelSize(4, 2), image.PixelSize);
        var window = env.Show(new RcWindow(env.Services) { Content = image }, 200, 100);
        Assert.False(image.IsPlaying);
        using (var frame = Shots.Capture(window))
        {
            Pixels.AssertClose(Colors.Red, Pixels.At(frame, 10, 10));
            Pixels.AssertClose(Colors.Red, Pixels.At(frame, 190, 90));
        }

        image.SetTint("#0000ff", solid: true);
        TestUi.Settle();
        using (var frame = Shots.Capture(window)) Pixels.AssertClose(Colors.Blue, Pixels.At(frame, 100, 50));

        image.SetTint("#0000ff", solid: false);
        TestUi.Settle();
        using (var frame = Shots.Capture(window)) Pixels.AssertClose(Color.FromRgb(166, 0, 89), Pixels.At(frame, 100, 50), 8);

        image.SetTint("#00000000", solid: false);
        TestUi.Settle();
        using (var frame = Shots.Capture(window)) Pixels.AssertClose(Colors.Red, Pixels.At(frame, 100, 50));

        image.SetTint("garbage", solid: true);
        Assert.Null(image.TintColor);
    }

    [AvaloniaFact]
    public async Task Plays_a_gif_with_its_frame_durations_while_shown()
    {
        using var env = new TestEnvironment();
        SKColor[] colours = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255)];
        string path = Write(env, "anim.gif", TestImages.Gif(8, 8, colours, [10, 5, 0]));
        var image = new AnimatedImage { SourcePath = path };
        Assert.True(image.HasImage);
        Assert.Equal(3, image.FrameCount);
        Assert.True(image.IsAnimated);
        Assert.Equal(100, image.FrameDuration(0));
        Assert.Equal(50, image.FrameDuration(1));
        Assert.Equal(AnimatedImage.MinimumFrameDuration, image.FrameDuration(2));
        Assert.False(image.IsPlaying);

        var window = env.Show(new RcWindow(env.Services) { Content = image }, 80, 80);
        Assert.True(image.IsPlaying);
        using (var frame = Shots.Capture(window)) Pixels.AssertClose(Colors.Red, Pixels.At(frame, 40, 40));
        Assert.True(image.Advance());
        Assert.Equal(1, image.CurrentFrame);
        using (var frame = Shots.Capture(window)) Pixels.AssertClose(Color.FromRgb(0, 255, 0), Pixels.At(frame, 40, 40));
        Assert.True(image.Advance());
        Assert.True(image.Advance());
        Assert.Equal(0, image.CurrentFrame);

        await TestUi.WaitUntilAsync(() => image.CurrentFrame != 0, TimeSpan.FromSeconds(2), "the timer to advance the animation");

        window.Content = null;
        TestUi.Settle();
        Assert.False(image.IsPlaying);
        window.Content = image;
        TestUi.Settle();
        Assert.True(image.IsPlaying);
        window.Close();
        TestUi.Settle();
        Assert.False(image.IsPlaying);
    }

    [AvaloniaFact]
    public void Plays_an_mng()
    {
        using var env = new TestEnvironment();
        string path = Write(env, "anim.mng", TestImages.Mng(6, 6, [new SKColor(255, 0, 0), new SKColor(0, 0, 255)], 20));
        var image = new AnimatedImage();
        Assert.True(image.Load(path));
        Assert.Equal(2, image.FrameCount);
        Assert.Equal(200, image.FrameDuration(0));
        var window = env.Show(new RcWindow(env.Services) { Content = image }, 60, 60);
        using (var frame = Shots.Capture(window)) Pixels.AssertClose(Colors.Red, Pixels.At(frame, 30, 30));
        Assert.True(image.Advance());
        using (var frame = Shots.Capture(window)) Pixels.AssertClose(Colors.Blue, Pixels.At(frame, 30, 30));
    }

    [AvaloniaFact]
    public void A_broken_or_missing_file_shows_nothing_and_logs()
    {
        using var env = new TestEnvironment();
        var image = new AnimatedImage { Log = env.Log };
        string broken = Write(env, "broken.webp", [0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 5, 6, 7]);
        Assert.False(image.Load(broken));
        Assert.False(image.HasImage);
        Assert.Equal(0, image.FrameCount);
        Assert.Contains(env.Log.Snapshot(), line => line.Contains("broken.webp", StringComparison.Ordinal));

        Assert.False(image.Load(Path.Combine(env.UserDirectory, "missing.png")));
        Assert.Contains(env.Log.Snapshot(), line => line.Contains("missing.png", StringComparison.Ordinal));
        Assert.False(image.Load(null));
        Assert.False(image.LoadBytes([]));

        var window = env.Show(new RcWindow(env.Services) { Content = image, Background = Brushes.Black }, 50, 50);
        using var frame = Shots.Capture(window);
        Pixels.AssertClose(Colors.Black, Pixels.At(frame, 25, 25));
        Assert.False(image.IsPlaying);
    }

    [AvaloniaFact]
    public void Shows_the_background_of_the_options()
    {
        using var env = new TestEnvironment(options: o => o with { BackgroundTint = "#1e1e2e60", BackgroundTintSolid = false });
        var image = new AnimatedImage();
        Assert.True(image.ApplyBackgroundOptions(env.Paths, env.Options));
        Assert.Equal(new PixelSize(2164, 727), image.PixelSize);
        Assert.Equal(Color.FromArgb(0x60, 0x1e, 0x1e, 0x2e), image.TintColor);
        var window = env.Show(new RcWindow(env.Services) { Content = image }, 500, 180);
        Shots.Save(window, "header-background");
    }
}
