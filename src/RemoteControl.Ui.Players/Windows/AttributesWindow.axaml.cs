using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using RemoteControl.Protocol.Text;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>
/// "Edit Attributes of &lt;acc&gt;" (<c>handlePlayerAttributes</c>, <c>TPlayerList.cpp:850-1037</c>), 400 wide: Stats (read only),
/// Look, Basic Attributes, Chests, Weapons and Script Flags, filled from the editor text. Apply writes the text back in the C++
/// section order, reads it with <see cref="PlayerAttributesText.Parse"/>, keeps a backup and sends it with the connection in use
/// then, and closes. The status bits the dialog has no check box for (paused, hidden, dead, hide sword) are kept as they came.
/// </summary>
internal sealed partial class AttributesWindow : RcWindow
{
    /// <summary>The colour names of the colour fields (<c>TPlayerList.cpp:893</c>).</summary>
    internal static readonly string[] ColorChoices =
    {
        "white", "yellow", "orange", "red", "darkred", "lightgreen", "green", "darkgreen", "lightblue", "blue", "darkblue", "brown", "cynober",
        "purple", "darkpurple", "lightgray", "gray", "black", "transparent",
    };

    internal static readonly string[] StatsKeys = { "Account", "Last IP", "Kills", "Deaths", "Online Seconds", "Rating", "Rating Deviation" };
    internal static readonly string[] LookKeys = { "Head Image", "Body Image", "Animation", "Skin Color", "Coat Color", "Sleeves Color", "Shoes Color", "Belt Color" };
    internal static readonly string[] BasicKeys = { "Level", "X", "Y", "Hearts", "Full Hearts", "AP", "MP", "Gralats", "Glove", "Bombs", "Arrows", "Sword Power", "Sword Image", "Shield Power", "Shield Image" };
    private static readonly string[] HiddenStatus = { "Paused", "Hidden", "Dead", "Hide sword" };

    private readonly PlayersSession? _owner;
    private readonly Dictionary<string, Func<string>> _readers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Control> _fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values;
    private readonly List<string> _chests = new();

    /// <summary>For the designer.</summary>
    public AttributesWindow() : this(null, new PlayerAttributes { Account = "account" }, string.Empty)
    {
    }

    /// <summary>Creates the dialog for the attributes the server sent.</summary>
    /// <param name="owner">The session.</param>
    /// <param name="attributes">The attributes.</param>
    /// <param name="editorText">Their editor text.</param>
    public AttributesWindow(PlayersSession? owner, PlayerAttributes attributes, string editorText) : base(owner?.App)
    {
        InitializeComponent();
        _owner = owner;
        Original = attributes;
        Account = attributes.Account;
        Title = "Edit Attributes of " + Account;
        _values = FieldValues(editorText);
        AddRows(StatsGrid, StatsKeys, editable: false);
        AddRows(LookGrid, LookKeys, editable: true);
        AddBasic();
        MaleCheck.IsChecked = IsTrue("Male");
        WeaponsCheck.IsChecked = IsTrue("Weapons Enabled");
        SpinCheck.IsChecked = IsTrue("Spin Attack");
        LoadLists(editorText);
        ApplyButton.Click += On.ClickAsync(ApplyAsync, "Attributes.Apply");
        CloseButton.Click += On.Click(Close, "Attributes.Close");
    }

    /// <summary>The account the dialog was opened for.</summary>
    public string Account { get; }

    /// <summary>The attributes as the server sent them.</summary>
    public PlayerAttributes Original { get; }

    /// <summary>A field by its editor-text key (for tests).</summary>
    internal Control Field(string key) => _fields[key];

    /// <summary>The editor text of what the dialog shows now (the C++ <c>onAttributeFormApply</c> text).</summary>
    public string EditorText
    {
        get
        {
            var text = new StringBuilder();
            text.Append("[Stats]\n");
            foreach (string key in StatsKeys) text.Append(key).Append(": ").Append(_readers[key]()).Append('\n');
            text.Append("[Look]\n");
            foreach (string key in LookKeys) text.Append(key).Append(": ").Append(_readers[key]()).Append('\n');
            text.Append("[Basic Attributes]\n");
            foreach (string key in BasicKeys) text.Append(key).Append(": ").Append(_readers[key]()).Append('\n');
            text.Append("Male: ").Append(MaleCheck.IsChecked == true ? "true" : "false").Append('\n');
            text.Append("Weapons Enabled: ").Append(WeaponsCheck.IsChecked == true ? "true" : "false").Append('\n');
            text.Append("Spin Attack: ").Append(SpinCheck.IsChecked == true ? "true" : "false").Append('\n');
            foreach (string key in HiddenStatus) text.Append(key).Append(": ").Append(IsTrue(key) ? "true" : "false").Append('\n');
            text.Append("[Chests]\n");
            foreach (string chest in _chests) text.Append(chest).Append('\n');
            text.Append('\n');
            text.Append("[Weapons]\n").Append(Lines(WeaponsBox.Text)).Append('\n');
            text.Append("[Script Flags]\n").Append(Lines(FlagsBox.Text));
            return text.ToString();
        }
    }

    /// <summary>The attributes of what the dialog shows now, as they are sent.</summary>
    public PlayerAttributes Current
    {
        get
        {
            PlayerAttributes attributes = PlayerAttributesText.Parse(EditorText);
            attributes.Account = Account;
            attributes.World = Original.World;
            attributes.PlayerId = Original.PlayerId;
            return attributes;
        }
    }

    /// <summary>Sends the attributes (with backups) and closes.</summary>
    /// <returns>Whether they were sent.</returns>
    public async Task<bool> ApplyAsync()
    {
        if (_owner is null || IsClosed) return false;
        string text = EditorText;
        PlayerAttributes attributes = Current;
        bool sent = await _owner.SendAsync(this, session => session.SetPlayerAttributesAsync(Account, attributes)).ConfigureAwait(true);
        if (!sent) return false;
        _owner.Backup("attributes", Account, text, modified: true);
        Close();
        return true;
    }

    private static Dictionary<string, string> FieldValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in (text ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int colon = line.IndexOf(':');
            if (colon < 0 || line.StartsWith('[')) continue;
            string key = line[..colon];
            if (values.ContainsKey(key)) continue;
            string value = line[(colon + 1)..];
            values[key] = value.StartsWith(' ') ? value[1..] : value;
        }
        return values;
    }

    private bool IsTrue(string key) => _values.TryGetValue(key, out string? value) && value.Contains("true", StringComparison.Ordinal);

    private string Value(string key) => _values.TryGetValue(key, out string? value) ? value : string.Empty;

    private static string CaptionOf(string key)
    {
        if (key == "Account") return "Account name:";
        if (key == "Online Seconds") return "Online seconds:";
        return char.ToLowerInvariant(key[0]) + key[1..] + ":";
    }

    private void AddRows(Grid grid, string[] keys, bool editable)
    {
        for (int row = 0; row < keys.Length; row++)
        {
            string key = keys[row];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var caption = new TextBlock { Text = CaptionOf(key), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(caption, row);
            grid.Children.Add(caption);
            Control editor;
            if (key.EndsWith(" Color", StringComparison.Ordinal))
            {
                var combo = new ComboBox { IsEditable = true, ItemsSource = ColorChoices, Text = Value(key), HorizontalAlignment = HorizontalAlignment.Stretch, Name = "Field" + key.Replace(" ", string.Empty, StringComparison.Ordinal) };
                _readers[key] = () => combo.Text ?? string.Empty;
                editor = combo;
            }
            else
            {
                var box = new TextBox { Text = Value(key), IsReadOnly = !editable, Name = "Field" + key.Replace(" ", string.Empty, StringComparison.Ordinal) };
                _readers[key] = () => box.Text ?? string.Empty;
                editor = box;
            }
            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);
            _fields[key] = editor;
        }
    }

    private void AddBasic()
    {
        (string Key, string Label, string RightKey, string RightLabel)[] rows =
        {
            ("X", "x:", "Y", "y:"), ("Hearts", "Hearts:", "Full Hearts", "Fullhearts:"), ("AP", "AP:", "MP", "MP:"), ("Gralats", "Gralats:", "Glove", "Glove:"),
            ("Bombs", "Bombs:", "Arrows", "Arrows:"), ("Sword Power", "Sword:", "Sword Image", "-Image:"), ("Shield Power", "Shield:", "Shield Image", "-Image:"),
        };
        for (int row = 0; row <= rows.Length; row++) BasicGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AddBasicField("Level", "Level:", 0, 0, 3);
        for (int row = 0; row < rows.Length; row++)
        {
            AddBasicField(rows[row].Key, rows[row].Label, 0, row + 1, 1);
            AddBasicField(rows[row].RightKey, rows[row].RightLabel, 2, row + 1, 1);
        }
    }

    private void AddBasicField(string key, string label, int column, int row, int span)
    {
        var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(caption, row);
        Grid.SetColumn(caption, column);
        BasicGrid.Children.Add(caption);
        var box = new TextBox { Text = Value(key), Name = "Field" + key.Replace(" ", string.Empty, StringComparison.Ordinal), MinWidth = 60 };
        Grid.SetRow(box, row);
        Grid.SetColumn(box, column + 1);
        Grid.SetColumnSpan(box, span);
        BasicGrid.Children.Add(box);
        _readers[key] = () => box.Text ?? string.Empty;
        _fields[key] = box;
    }

    private void LoadLists(string text)
    {
        string section = string.Empty;
        var weapons = new List<string>();
        var flags = new List<string>();
        var shown = new List<string>();
        foreach (string raw in (text ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }
            if (line.Length == 0) continue;
            switch (section)
            {
                case "Chests":
                    _chests.Add(line);
                    int first = line.IndexOf(':');
                    int second = first < 0 ? -1 : line.IndexOf(':', first + 1);
                    shown.Add(second < 0 ? line : line[(second + 1)..]);
                    break;
                case "Weapons":
                    weapons.Add(line);
                    break;
                case "Script Flags":
                    flags.Add(line);
                    break;
            }
        }
        ChestList.ItemsSource = shown;
        WeaponsBox.Text = weapons.Count == 0 ? string.Empty : string.Join('\n', weapons) + "\n";
        FlagsBox.Text = flags.Count == 0 ? string.Empty : string.Join('\n', flags) + "\n";
    }

    private static string Lines(string? text)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(line => line.Trim().Length > 0);
        var result = new StringBuilder();
        foreach (string line in lines) result.Append(line).Append('\n');
        return result.ToString();
    }
}
