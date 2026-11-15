using System.Globalization;

namespace RemoteControl.App.Hotkeys;

/// <summary>The modifier keys of a hotkey.</summary>
[Flags]
internal enum HotkeyModifiers
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary><c>ctrl</c> or <c>control</c>.</summary>
    Control = 1,

    /// <summary><c>alt</c>.</summary>
    Alt = 2,

    /// <summary><c>shift</c>.</summary>
    Shift = 4,

    /// <summary><c>super</c>, <c>meta</c> or <c>win</c>.</summary>
    Super = 8,
}

/// <summary>The kind of the key of a hotkey.</summary>
internal enum HotkeyKeyKind
{
    /// <summary>One character (<c>r</c>, <c>1</c>, <c>,</c>).</summary>
    Character,

    /// <summary>A function key <c>f1</c> to <c>f24</c>.</summary>
    Function,

    /// <summary>One of the named keys of the C++ (<see cref="HotkeyNamedKey"/>).</summary>
    Named,

    /// <summary>Any other name: an X11 key symbol name (the C++ passed it to <c>gdk_keyval_from_name</c>); not usable on Windows.</summary>
    Other,
}

/// <summary>The named keys the C++ knew on every system (<c>TMain.cpp:587-596</c>).</summary>
internal enum HotkeyNamedKey
{
    /// <summary><c>space</c>.</summary>
    Space,

    /// <summary><c>tab</c>.</summary>
    Tab,

    /// <summary><c>escape</c> or <c>esc</c>.</summary>
    Escape,

    /// <summary><c>return</c> or <c>enter</c>.</summary>
    Return,

    /// <summary><c>home</c>.</summary>
    Home,

    /// <summary><c>end</c>.</summary>
    End,

    /// <summary><c>page_up</c>.</summary>
    PageUp,

    /// <summary><c>page_down</c>.</summary>
    PageDown,

    /// <summary><c>insert</c>.</summary>
    Insert,

    /// <summary><c>delete</c>.</summary>
    Delete,
}

/// <summary>
/// A global hotkey in the text format of the option <c>globalhotkey</c> (<c>refreshGlobalVisibilityHotkey</c>, <c>TMain.cpp:566-636</c>):
/// tokens separated by <c>+</c>, case ignored; modifiers <c>ctrl|control</c>, <c>alt</c>, <c>shift</c>, <c>super|meta|win</c>; the key a single
/// character, <c>f1</c>..<c>f24</c>, <c>space</c>, <c>tab</c>, <c>escape|esc</c>, <c>return|enter</c>, <c>home</c>, <c>end</c>, <c>page_up</c>,
/// <c>page_down</c>, <c>insert</c>, <c>delete</c>, or (X11 only) any key symbol name. When several keys are given the last one counts, as in the C++.
/// </summary>
/// <param name="Modifiers">The modifiers.</param>
/// <param name="Kind">The kind of key.</param>
/// <param name="Character">The character (lower case) for <see cref="HotkeyKeyKind.Character"/>.</param>
/// <param name="FunctionNumber">1 to 24 for <see cref="HotkeyKeyKind.Function"/>.</param>
/// <param name="NamedKey">The key for <see cref="HotkeyKeyKind.Named"/>.</param>
/// <param name="Name">The key symbol name for <see cref="HotkeyKeyKind.Other"/>.</param>
internal sealed record Hotkey(HotkeyModifiers Modifiers, HotkeyKeyKind Kind, char Character = '\0', int FunctionNumber = 0, HotkeyNamedKey NamedKey = HotkeyNamedKey.Space, string Name = "")
{
    /// <summary>Reads a hotkey text.</summary>
    /// <param name="text">The option's value.</param>
    /// <returns>The hotkey, or null when the text is empty or names no key.</returns>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        Hotkey? key = null;
        foreach (string raw in text.Split('+'))
        {
            string token = raw.Trim().ToLowerInvariant();
            switch (token)
            {
                case "":
                    continue;
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "super" or "meta" or "win":
                    modifiers |= HotkeyModifiers.Super;
                    continue;
            }
            key = KeyOf(token);
        }
        // A lone "+" is the plus key ("ctrl++" splits into empty tokens).
        if (key is null && text.Trim().EndsWith('+') && text.Contains("++", StringComparison.Ordinal)) key = new Hotkey(HotkeyModifiers.None, HotkeyKeyKind.Character, '+');
        return key is null ? null : key with { Modifiers = modifiers };
    }

    private static Hotkey KeyOf(string token)
    {
        if (token.Length == 1) return new Hotkey(HotkeyModifiers.None, HotkeyKeyKind.Character, token[0]);
        if (token.Length > 1 && token[0] == 'f' && token.Skip(1).All(char.IsAsciiDigit)
            && int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number is >= 1 and <= 24)
            return new Hotkey(HotkeyModifiers.None, HotkeyKeyKind.Function, FunctionNumber: number);
        HotkeyNamedKey? named = token switch
        {
            "space" => HotkeyNamedKey.Space,
            "tab" => HotkeyNamedKey.Tab,
            "escape" or "esc" => HotkeyNamedKey.Escape,
            "return" or "enter" => HotkeyNamedKey.Return,
            "home" => HotkeyNamedKey.Home,
            "end" => HotkeyNamedKey.End,
            "page_up" => HotkeyNamedKey.PageUp,
            "page_down" => HotkeyNamedKey.PageDown,
            "insert" => HotkeyNamedKey.Insert,
            "delete" => HotkeyNamedKey.Delete,
            _ => null,
        };
        if (named is { } value) return new Hotkey(HotkeyModifiers.None, HotkeyKeyKind.Named, NamedKey: value);
        return new Hotkey(HotkeyModifiers.None, HotkeyKeyKind.Other, Name: token);
    }

    /// <summary>
    /// The X11 key symbol name (for <c>XStringToKeysym</c>), or null for a character (whose key symbol is its Latin-1 code). The C++
    /// passed <c>tab</c>, <c>home</c>, <c>end</c>, <c>insert</c>, <c>delete</c> and <c>return</c> in lower case, which X11 does not know; here they work.
    /// </summary>
    public string? X11KeysymName => Kind switch
    {
        HotkeyKeyKind.Function => "F" + FunctionNumber.ToString(CultureInfo.InvariantCulture),
        HotkeyKeyKind.Named => NamedKey switch
        {
            HotkeyNamedKey.Space => "space",
            HotkeyNamedKey.Tab => "Tab",
            HotkeyNamedKey.Escape => "Escape",
            HotkeyNamedKey.Return => "Return",
            HotkeyNamedKey.Home => "Home",
            HotkeyNamedKey.End => "End",
            HotkeyNamedKey.PageUp => "Page_Up",
            HotkeyNamedKey.PageDown => "Page_Down",
            HotkeyNamedKey.Insert => "Insert",
            _ => "Delete",
        },
        HotkeyKeyKind.Other => Name,
        _ => null,
    };

    /// <summary>The X11 key symbol of a character key (its Latin-1 code), or 0.</summary>
    public long X11CharacterKeysym => Kind == HotkeyKeyKind.Character && Character is >= ' ' and <= 'ÿ' ? Character : 0;

    /// <summary>The Windows virtual key of a function or named key, or 0 (a character is looked up with <c>VkKeyScanW</c>; another name is not supported).</summary>
    public int WindowsVirtualKey => Kind switch
    {
        HotkeyKeyKind.Function => 0x70 + FunctionNumber - 1,
        HotkeyKeyKind.Named => NamedKey switch
        {
            HotkeyNamedKey.Space => 0x20,
            HotkeyNamedKey.Tab => 0x09,
            HotkeyNamedKey.Escape => 0x1B,
            HotkeyNamedKey.Return => 0x0D,
            HotkeyNamedKey.Home => 0x24,
            HotkeyNamedKey.End => 0x23,
            HotkeyNamedKey.PageUp => 0x21,
            HotkeyNamedKey.PageDown => 0x22,
            HotkeyNamedKey.Insert => 0x2D,
            _ => 0x2E,
        },
        _ => 0,
    };

    /// <summary>The hotkey as text in the option's format (modifiers first).</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Super)) parts.Add("super");
        parts.Add(Kind switch
        {
            HotkeyKeyKind.Character => Character.ToString(),
            HotkeyKeyKind.Function => "f" + FunctionNumber.ToString(CultureInfo.InvariantCulture),
            HotkeyKeyKind.Named => NamedKey switch
            {
                HotkeyNamedKey.PageUp => "page_up",
                HotkeyNamedKey.PageDown => "page_down",
                _ => NamedKey.ToString().ToLowerInvariant(),
            },
            _ => Name,
        });
        return string.Join("+", parts);
    }
}
