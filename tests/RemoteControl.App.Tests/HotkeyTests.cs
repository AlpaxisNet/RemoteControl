using Avalonia.Headless.XUnit;
using RemoteControl.App.Hotkeys;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.App.Tests;

public sealed class HotkeyParsingTests
{
    [Theory]
    [InlineData("ctrl+alt+r", 3, 'r')]
    [InlineData("Control+Shift+X", 5, 'x')]
    [InlineData("super+1", 8, '1')]
    [InlineData("meta+a", 8, 'a')]
    [InlineData("WIN+b", 8, 'b')]
    [InlineData("alt + ,", 2, ',')]
    [InlineData("q", 0, 'q')]
    [InlineData("ctrl++", 1, '+')]
    public void Characters_with_every_modifier(string text, int modifiers, char key)
    {
        Hotkey hotkey = Hotkey.Parse(text)!;

        Assert.Equal(HotkeyKeyKind.Character, hotkey.Kind);
        Assert.Equal((HotkeyModifiers)modifiers, hotkey.Modifiers);
        Assert.Equal(key, hotkey.Character);
        Assert.Equal(key, (char)hotkey.X11CharacterKeysym);
        Assert.Null(hotkey.X11KeysymName);
    }

    [Theory]
    [InlineData("ctrl+f1", 1, 0x70, "F1")]
    [InlineData("shift+F12", 12, 0x7B, "F12")]
    [InlineData("f24", 24, 0x87, "F24")]
    public void Function_keys(string text, int number, int virtualKey, string keysym)
    {
        Hotkey hotkey = Hotkey.Parse(text)!;

        Assert.Equal(HotkeyKeyKind.Function, hotkey.Kind);
        Assert.Equal(number, hotkey.FunctionNumber);
        Assert.Equal(virtualKey, hotkey.WindowsVirtualKey);
        Assert.Equal(keysym, hotkey.X11KeysymName);
    }

    [Theory]
    [InlineData("ctrl+space", 0, 0x20, "space")]
    [InlineData("alt+tab", 1, 0x09, "Tab")]
    [InlineData("ctrl+escape", 2, 0x1B, "Escape")]
    [InlineData("ctrl+esc", 2, 0x1B, "Escape")]
    [InlineData("ctrl+return", 3, 0x0D, "Return")]
    [InlineData("ctrl+enter", 3, 0x0D, "Return")]
    [InlineData("ctrl+home", 4, 0x24, "Home")]
    [InlineData("ctrl+end", 5, 0x23, "End")]
    [InlineData("ctrl+page_up", 6, 0x21, "Page_Up")]
    [InlineData("ctrl+page_down", 7, 0x22, "Page_Down")]
    [InlineData("ctrl+insert", 8, 0x2D, "Insert")]
    [InlineData("ctrl+delete", 9, 0x2E, "Delete")]
    public void Named_keys(string text, int key, int virtualKey, string keysym)
    {
        Hotkey hotkey = Hotkey.Parse(text)!;

        Assert.Equal(HotkeyKeyKind.Named, hotkey.Kind);
        Assert.Equal((HotkeyNamedKey)key, hotkey.NamedKey);
        Assert.Equal(virtualKey, hotkey.WindowsVirtualKey);
        Assert.Equal(keysym, hotkey.X11KeysymName);
        Assert.Equal(text.Replace("esc", "escape").Replace("escapeape", "escape").Replace("enter", "return"), hotkey.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ctrl+alt")]
    [InlineData("shift")]
    [InlineData("+")]
    public void Texts_without_a_key_are_no_hotkey(string? text) => Assert.Null(Hotkey.Parse(text));

    [Fact]
    public void Another_name_is_an_x11_key_symbol_only_and_the_last_key_counts()
    {
        Hotkey other = Hotkey.Parse("ctrl+KP_Add")!;
        Assert.Equal(HotkeyKeyKind.Other, other.Kind);
        Assert.Equal("kp_add", other.X11KeysymName);
        Assert.Equal(0, other.WindowsVirtualKey);

        Hotkey f25 = Hotkey.Parse("f25")!;
        Assert.Equal(HotkeyKeyKind.Other, f25.Kind);

        Hotkey last = Hotkey.Parse("a+b+alt")!;
        Assert.Equal('b', last.Character);
        Assert.Equal(HotkeyModifiers.Alt, last.Modifiers);
    }

    [Fact]
    public void Every_modifier_together_round_trips()
    {
        Hotkey hotkey = Hotkey.Parse("win+shift+alt+control+z")!;

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Super, hotkey.Modifiers);
        Assert.Equal("ctrl+alt+shift+super+z", hotkey.ToString());
        Assert.Equal(hotkey, Hotkey.Parse(hotkey.ToString()));
    }
}

public sealed class HotkeyControllerTests
{
    [AvaloniaFact]
    public async Task The_option_is_registered_at_start_and_followed_live()
    {
        using var env = new TestEnvironment(options: options => options with { GlobalHotkey = "ctrl+alt+r" });
        var backend = new FakeHotkeyBackend();
        int toggles = 0;
        using var controller = new HotkeyController(env.Services, backend, () => toggles++, _ => Task.CompletedTask);
        await controller.Current;
        Assert.Equal(Hotkey.Parse("ctrl+alt+r"), Assert.Single(backend.Requests));

        env.SetOptions(options => options with { GlobalHotkey = "ctrl+f5" });
        await TestUi.WaitUntilAsync(() => backend.Requests.Count == 2);
        Assert.Equal(Hotkey.Parse("ctrl+f5"), backend.Requests[1]);

        env.SetOptions(options => options with { GlobalHotkey = string.Empty });
        await TestUi.WaitUntilAsync(() => backend.Requests.Count == 3);
        Assert.Null(backend.Requests[2]);

        backend.Press();
        await TestUi.WaitUntilAsync(() => toggles == 1);

        controller.Dispose();
        Assert.True(backend.Disposed);
        env.SetOptions(options => options with { GlobalHotkey = "ctrl+q" });
        TestUi.Settle();
        Assert.Equal(3, backend.Requests.Count);
    }

    [AvaloniaFact]
    public async Task A_refused_hotkey_is_logged_and_shown_once()
    {
        using var env = new TestEnvironment(options: options => options with { GlobalHotkey = "ctrl+alt+r" });
        var backend = new FakeHotkeyBackend { Result = key => key is null ? HotkeyResult.Ok : HotkeyResult.Fail("Another program already uses this key combination.") };
        var shown = new List<string>();
        using var controller = new HotkeyController(env.Services, backend, () => { }, message =>
        {
            shown.Add(message);
            return Task.CompletedTask;
        });
        HotkeyResult result = await controller.Current;

        Assert.False(result.Success);
        string message = Assert.Single(shown);
        Assert.Contains("ctrl+alt+r", message);
        Assert.Contains("Another program", message);
        Assert.Contains(env.Log.Snapshot(), line => line.Contains("could not be registered", StringComparison.Ordinal));

        controller.Apply();
        await controller.Current;
        Assert.Single(shown);

        env.SetOptions(options => options with { GlobalHotkey = "ctrl+alt" });
        await TestUi.WaitUntilAsync(() => shown.Count == 2);
        Assert.Contains("names no key", shown[1]);
    }

    [AvaloniaFact]
    public async Task An_unsupported_desktop_grabs_nothing_and_shows_nothing()
    {
        using var env = new TestEnvironment(options: options => options with { GlobalHotkey = "ctrl+alt+r" });
        var backend = new FakeHotkeyBackend { UnsupportedReason = "Global hotkeys are not supported on Wayland." };
        var shown = new List<string>();
        using var controller = new HotkeyController(env.Services, backend, () => { }, message =>
        {
            shown.Add(message);
            return Task.CompletedTask;
        });

        HotkeyResult result = await controller.Current;

        Assert.False(result.Success);
        Assert.Empty(backend.Requests);
        Assert.Empty(shown);
        Assert.Equal("Global hotkeys are not supported on Wayland.", controller.UnsupportedReason);
    }
}
