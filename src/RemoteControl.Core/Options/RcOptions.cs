using System.Collections.Immutable;
using System.Globalization;

namespace RemoteControl.Core;

/// <summary>
/// Every option of <c>control2config.txt</c>, as one immutable snapshot. The defaults are those of the C++ <c>RCOptions</c>
/// struct; integers are clamped and empty font families and timestamp patterns fall back to their defaults as soon as they
/// are set, so a snapshot is always valid. Change options through <see cref="OptionsStore.Update"/>, for example
/// <c>store.Update(o =&gt; o with { AfkTimeout = 30 }, this)</c>; never keep a copy to save later.
/// </summary>
public sealed partial record RcOptions
{
    /// <summary>The options a new installation starts with.</summary>
    public static RcOptions Defaults => Table.Defaults;

    private static ImmutableArray<OptionField> FieldTable => Table.Fields;
    private static Dictionary<string, OptionField> FieldsByKey => Table.ByKey;

    /// <summary>
    /// The normal picture of each graphical-menu button (keys <c>icon&lt;name&gt;</c>), in the order of <see cref="ButtonNames"/>.
    /// Always 15 entries: a shorter array is completed with the defaults, a longer one is cut.
    /// </summary>
    public ImmutableArray<string> ButtonImages
    {
        get => _buttonImages;
        init => _buttonImages = NormalizeButtons(value, DefaultButtonImages);
    }
    private readonly ImmutableArray<string> _buttonImages = DefaultButtonImages;

    /// <summary>
    /// The pressed picture of each graphical-menu button (keys <c>icon&lt;name&gt;pressed</c>), in the order of <see cref="ButtonNames"/>.
    /// Always 15 entries.
    /// </summary>
    public ImmutableArray<string> ButtonImagesPressed
    {
        get => _buttonImagesPressed;
        init => _buttonImagesPressed = NormalizeButtons(value, DefaultButtonImagesPressed);
    }
    private readonly ImmutableArray<string> _buttonImagesPressed = DefaultButtonImagesPressed;

    /// <summary>Every key of the options file, in the order the file is written.</summary>
    public static IReadOnlyList<string> Keys => Table.Keys;

    /// <summary>Whether <paramref name="key"/> is one of <see cref="Keys"/>.</summary>
    /// <param name="key">A key of the options file.</param>
    /// <returns><c>true</c> for a known key.</returns>
    public static bool IsKnownKey(string key) => key is not null && FieldsByKey.ContainsKey(key);

    /// <summary>The value of one key as it is written to the file (booleans as <c>true</c>/<c>false</c>, lists comma separated).</summary>
    /// <param name="key">One of <see cref="Keys"/>.</param>
    /// <returns>The value text.</returns>
    /// <exception cref="ArgumentException">The key is unknown.</exception>
    public string GetValue(string key)
    {
        if (key is null || !FieldsByKey.TryGetValue(key, out OptionField? field)) throw new ArgumentException($"Unknown option key '{key}'.", nameof(key));
        return field.Format(this);
    }

    /// <summary>
    /// A copy with one key set from its file text, read the way the C++ read it: a boolean is true only for exactly <c>true</c>,
    /// an integer that does not parse leaves the value unchanged, an integer out of range is clamped.
    /// </summary>
    /// <param name="key">One of <see cref="Keys"/>.</param>
    /// <param name="value">The value text.</param>
    /// <returns>The changed copy.</returns>
    /// <exception cref="ArgumentException">The key is unknown.</exception>
    public RcOptions WithValue(string key, string value)
    {
        if (key is null || !FieldsByKey.TryGetValue(key, out OptionField? field)) throw new ArgumentException($"Unknown option key '{key}'.", nameof(key));
        return field.Parse(this, value ?? string.Empty);
    }

    /// <summary>Every key with its file text, in the order the file is written.</summary>
    /// <returns>The key/value pairs.</returns>
    public IReadOnlyList<KeyValuePair<string, string>> ToEntries()
    {
        var entries = new List<KeyValuePair<string, string>>(FieldTable.Length);
        foreach (OptionField field in FieldTable) entries.Add(new KeyValuePair<string, string>(field.Key, field.Format(this)));
        return entries;
    }

    /// <summary>The keys whose file text differs between two snapshots.</summary>
    /// <param name="before">The earlier snapshot.</param>
    /// <param name="after">The later snapshot.</param>
    /// <returns>The changed keys, in file order.</returns>
    public static IReadOnlyList<string> ChangedKeys(RcOptions before, RcOptions after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var keys = new List<string>();
        foreach (OptionField field in FieldTable)
            if (!string.Equals(field.Format(before), field.Format(after), StringComparison.Ordinal)) keys.Add(field.Key);
        return keys;
    }

    /// <summary>Two snapshots are equal when every key has the same file text (lists are compared by content).</summary>
    /// <param name="other">The other snapshot.</param>
    /// <returns><c>true</c> when equal.</returns>
    public bool Equals(RcOptions? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        foreach (OptionField field in FieldTable)
            if (!string.Equals(field.Format(this), field.Format(other), StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>A hash of every key's file text.</summary>
    /// <returns>The hash.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (OptionField field in FieldTable) hash.Add(field.Format(this), StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    private static ImmutableArray<string> NormalizeButtons(ImmutableArray<string> value, ImmutableArray<string> defaults)
    {
        if (value.IsDefault) return defaults;
        var builder = ImmutableArray.CreateBuilder<string>(defaults.Length);
        for (int index = 0; index < defaults.Length; index++) builder.Add(index < value.Length ? value[index] ?? string.Empty : defaults[index]);
        return builder.MoveToImmutable();
    }

    /// <summary>The key table, built on first use (after every static member of <see cref="RcOptions"/> is set).</summary>
    private static class Table
    {
        public static readonly ImmutableArray<OptionField> Fields = BuildFields();
        public static readonly Dictionary<string, OptionField> ByKey = Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        public static readonly ImmutableArray<string> Keys = Fields.Select(field => field.Key).ToImmutableArray();
        public static readonly RcOptions Defaults = new();
    }

    /// <summary>One key: how it is written and how it is read.</summary>
    private sealed class OptionField
    {
        private OptionField(string key, Func<RcOptions, string> format, Func<RcOptions, string, RcOptions> parse)
        {
            Key = key;
            Format = format;
            Parse = parse;
        }

        public string Key { get; }
        public Func<RcOptions, string> Format { get; }
        public Func<RcOptions, string, RcOptions> Parse { get; }

        public static OptionField Flag(string key, Func<RcOptions, bool> get, Func<RcOptions, bool, RcOptions> set) =>
            new(key, o => get(o) ? "true" : "false", (o, text) => set(o, text == "true"));

        public static OptionField Text(string key, Func<RcOptions, string> get, Func<RcOptions, string, RcOptions> set) =>
            new(key, get, set);

        public static OptionField Number(string key, Func<RcOptions, int> get, Func<RcOptions, int, RcOptions> set) =>
            new(key, o => get(o).ToString(CultureInfo.InvariantCulture), (o, text) => TryParseInt(text, out int parsed) ? set(o, parsed) : o);

        public static OptionField List(string key, Func<RcOptions, ImmutableArray<string>> get, Func<RcOptions, ImmutableArray<string>, RcOptions> set) =>
            new(key, o => string.Join(',', get(o)), (o, text) => set(o, SplitComma(text)));

        /// <summary>Like <c>std::stoi</c> that must consume the whole text: optional sign, decimal digits, no overflow.</summary>
        private static bool TryParseInt(string text, out int value) =>
            int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

        /// <summary>Like the C++ <c>getline(',')</c> loop: an empty text gives no item, a trailing comma gives no empty last item.</summary>
        private static ImmutableArray<string> SplitComma(string text)
        {
            if (text.Length == 0) return ImmutableArray<string>.Empty;
            string[] parts = text.Split(',');
            int count = parts.Length;
            if (count > 0 && parts[count - 1].Length == 0) count--;
            return ImmutableArray.Create(parts, 0, count);
        }
    }
}
