using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using RemoteControl.Ui.TestKit;
using Xunit.Abstractions;

namespace RemoteControl.Ui.Common.Tests;

public sealed class ChatViewTests(ITestOutputHelper output)
{
    private static readonly DateTime Noon = new(2024, 3, 5, 13, 7, 9, DateTimeKind.Local);

    private static (TestEnvironment Env, RcWindow Window, ChatView Chat) Open(double width = 420, double height = 240, Func<Core.RcOptions, Core.RcOptions>? options = null)
    {
        var env = new TestEnvironment(theme: "catppuccin", options: options);
        var chat = new ChatView { Name = "Chat", Services = env.Services };
        var window = env.Show(new RcWindow(env.Services) { Content = chat }, width, height);
        return (env, window, chat);
    }

    [AvaloniaFact]
    public void Formats_lines_with_the_time_stamp_of_the_options()
    {
        var (env, _, chat) = Open();
        using (env)
        {
            chat.Append("Bob: hi", new ChatAppendOptions { Time = Noon });
            env.SetOptions(o => o with { TimestampFormat = "%H:%M:%S" });
            chat.Append("Ann: yo", new ChatAppendOptions { Time = Noon });
            env.SetOptions(o => o with { RcTimestamps = false });
            chat.Append("plain", new ChatAppendOptions { Time = Noon });
            chat.Append("forced", new ChatAppendOptions { Time = Noon, Timestamp = true });
            Assert.Equal("[01:07 PM] Bob: hi\n13:07:09 Ann: yo\nplain\n13:07:09 forced", chat.Text);
            Assert.Equal(4, chat.LineCount);
            Assert.Equal(new ChatSpan(11, 4, ChatSpanKind.Prefix), chat.Lines[0].Spans[1]);
        }
    }

    [AvaloniaFact]
    public void Alert_flags_are_reported_not_acted_on()
    {
        var (env, _, chat) = Open();
        using (env)
        {
            ChatAppendResult plain = chat.Append("hello");
            Assert.False(plain.WantsUrgency);
            Assert.False(plain.WantsBeep);

            ChatAppendResult urgent = chat.Append("#ALERT New PM from Bob");
            Assert.True(urgent.WantsUrgency);
            Assert.False(urgent.WantsBeep);
            Assert.False(chat.Append("#ALERTSF x", new ChatAppendOptions { SuppressUrgency = true }).WantsUrgency);

            ChatAppendResult sound = chat.Append("#ALERTSP wake up");
            Assert.True(sound.WantsUrgency);
            Assert.True(sound.WantsBeep);
            Assert.True(chat.Append("#ALERTP x", new ChatAppendOptions { SuppressUrgency = true }).WantsUrgency);

            env.SetOptions(o => o with { NotificationSounds = false });
            ChatAppendResult silent = chat.Append("#ALERTF quiet");
            Assert.True(silent.WantsUrgency);
            Assert.False(silent.WantsBeep);
            Assert.EndsWith(" quiet", chat.Text);
            Assert.DoesNotContain("#ALERT", chat.Text);
        }
    }

    [AvaloniaFact]
    public void Keeps_at_most_the_line_limit_dropping_the_oldest()
    {
        var (env, _, chat) = Open(options: o => o with { RcTimestamps = false });
        using (env)
        {
            chat.MaximumLines = 100;
            for (int index = 0; index < 250; index++) chat.Append("line " + index);
            Assert.Equal(100, chat.LineCount);
            Assert.Equal(100, chat.Editor.Document.LineCount);
            Assert.StartsWith("line 150\n", chat.Text);
            Assert.EndsWith("line 249", chat.Text);
            Assert.Equal("line 150", chat.Lines[0].Text);

            chat.Append("a\nb\nc");
            Assert.Equal(100, chat.LineCount);
            Assert.Equal("line 153", chat.Lines[0].Text);

            chat.MaximumCharacters = 2000;
            Assert.True(chat.Text.Length <= 2000);
            Assert.Equal(chat.LineCount, chat.Editor.Document.LineCount);
            Assert.Equal(chat.Lines[0].Text, chat.Editor.Document.GetText(chat.Editor.Document.GetLineByNumber(1)));

            string huge = new('x', 3000);
            chat.Append(huge);
            Assert.Equal(1, chat.LineCount);
            Assert.Equal(huge, chat.Text);
        }
    }

    [AvaloniaFact]
    public void Clear_and_the_text_for_find()
    {
        var (env, _, chat) = Open(options: o => o with { RcTimestamps = false });
        using (env)
        {
            chat.Append("one");
            chat.Append("two Kappa");
            Assert.Equal("one\ntwo Kappa", chat.Text);
            chat.SelectAll();
            Assert.Equal("one\ntwo Kappa", chat.SelectedText);
            chat.Clear();
            Assert.Equal(0, chat.LineCount);
            Assert.Equal(string.Empty, chat.Text);
            chat.Append("three");
            Assert.Equal("three", chat.Text);
        }
    }

    [AvaloniaFact]
    public void Scrolls_to_the_end_only_while_at_the_bottom()
    {
        var (env, _, chat) = Open(420, 200, o => o with { RcTimestamps = false });
        using (env)
        {
            for (int index = 0; index < 200; index++) chat.Append("line " + index);
            TestUi.Settle();
            ScrollViewer scroller = TestUi.First<ScrollViewer>(chat);
            Assert.True(scroller.Extent.Height > scroller.Viewport.Height * 5);
            Assert.True(chat.IsAtBottom);

            scroller.Offset = new Vector(0, 0);
            TestUi.Settle();
            Assert.False(chat.IsAtBottom);
            chat.Append("while reading old lines");
            TestUi.Settle();
            Assert.Equal(0, scroller.Offset.Y);

            chat.AlwaysScrollToEnd = true;
            chat.Append("main chat always follows");
            TestUi.Settle();
            Assert.True(chat.IsAtBottom);

            chat.AlwaysScrollToEnd = false;
            chat.Append("still at the bottom");
            TestUi.Settle();
            Assert.True(chat.IsAtBottom);
        }
    }

    [AvaloniaFact]
    public void Links_raise_an_event_when_clicked()
    {
        var (env, window, chat) = Open(options: o => o with { RcTimestamps = false });
        using (env)
        {
            var clicked = new List<string>();
            chat.LinkClicked += (_, e) => clicked.Add(e.Url);
            chat.Append("Bob: see www.example.com/page now");
            TestUi.Settle();
            int offset = chat.Text.IndexOf("example", StringComparison.Ordinal);
            Assert.Equal("https://www.example.com/page", chat.LinkAt(offset));
            Assert.Null(chat.LinkAt(0));

            TextView view = chat.Editor.TextArea.TextView;
            view.EnsureVisualLines();
            Point visual = view.GetVisualPosition(new TextViewPosition(1, offset + 1), VisualYPosition.LineMiddle) - view.ScrollOffset;
            Point inWindow = view.TranslatePoint(visual + new Point(3, 0), window)!.Value;
            window.MouseMove(inWindow);
            Assert.True(chat.IsPointerOverLink);
            window.MouseDown(inWindow, MouseButton.Left);
            window.MouseUp(inWindow, MouseButton.Left);
            TestUi.Settle();
            Assert.Equal(["https://www.example.com/page"], clicked);

            Point plain = view.TranslatePoint(view.GetVisualPosition(new TextViewPosition(1, 2), VisualYPosition.LineMiddle) - view.ScrollOffset, window)!.Value;
            window.MouseMove(plain);
            Assert.False(chat.IsPointerOverLink);
            window.MouseDown(plain, MouseButton.Left);
            window.MouseUp(plain, MouseButton.Left);
            TestUi.Settle();
            Assert.Single(clicked);
        }
    }

    [AvaloniaFact]
    public void Emotes_are_shown_as_pictures_and_copied_as_words()
    {
        var (env, _, chat) = Open(options: o => o with { RcTimestamps = false });
        using (env)
        {
            chat.Append("Ünïcode 😀: hi Kappa and :v");
            chat.Append("no pictures Kappa", new ChatAppendOptions { SuppressEmotes = true });
            TestUi.Settle();
            IReadOnlyList<Image> pictures = TestUi.FindAll<Image>(chat.Editor.TextArea.TextView);
            Assert.Equal(2, pictures.Count);
            Assert.Equal("Ünïcode 😀: hi Kappa and :v\nno pictures Kappa", chat.Text);
            chat.EmotesEnabled = false;
            chat.Append("Kappa off");
            Assert.Empty(chat.Lines[^1].Emotes);
        }
    }

    [AvaloniaFact]
    public void Font_and_colours_follow_the_options_live()
    {
        var (env, _, chat) = Open();
        using (env)
        {
            Assert.Equal(9 * 96.0 / 72.0, chat.Editor.FontSize, 3);
            env.SetOptions(o => o with { ChatFontSize = 15, ColorChatBack = "#102030", ColorChat = "#a0b0c0", ColorChatBold = "#ff00ff" });
            Assert.Equal(20.0, chat.Editor.FontSize, 3);
            Assert.Equal(Avalonia.Media.Color.Parse("#102030"), ((Avalonia.Media.ISolidColorBrush)chat.Editor.Background!).Color);
            Assert.Equal(Avalonia.Media.Color.Parse("#a0b0c0"), ((Avalonia.Media.ISolidColorBrush)chat.Editor.Foreground!).Color);
            Assert.Equal(Avalonia.Media.Color.Parse("#ff00ff"), ((Avalonia.Media.ISolidColorBrush)chat.Brushes.Bold).Color);
        }
    }

    [AvaloniaFact]
    public void Appending_5000_lines_stays_cheap()
    {
        var (env, _, chat) = Open(600, 400);
        using (env)
        {
            var watch = Stopwatch.StartNew();
            for (int index = 0; index < 5000; index++)
                chat.Append($"Player{index % 50}: message number {index} with a link www.example{index}.com and Kappa", new ChatAppendOptions { Time = Noon });
            TestUi.Settle();
            watch.Stop();
            output.WriteLine($"5000 appends and a render: {watch.ElapsedMilliseconds} ms");
            Assert.Equal(5000, chat.LineCount);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"5000 appends took {watch.ElapsedMilliseconds} ms");

            watch.Restart();
            for (int index = 0; index < 500; index++) chat.Append("more " + index);
            TestUi.Settle();
            output.WriteLine($"500 more appends with 5000 lines: {watch.ElapsedMilliseconds} ms");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"500 appends took {watch.ElapsedMilliseconds} ms");
            Assert.True(chat.IsAtBottom, "at the bottom after the appends");
        }
    }

    [AvaloniaFact]
    public void Works_without_services_and_outside_a_window()
    {
        var chat = new ChatView();
        ChatAppendResult result = chat.Append("Kappa: www.a.org");
        Assert.True(result.Appended);
        Assert.Equal(1, chat.LineCount);
        Assert.Empty(chat.Lines[0].Emotes);
        chat.Append(null);
        Assert.Equal(2, chat.LineCount);
    }
}
