using Avalonia.Controls;
using RemoteControl.Protocol.Text;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>
/// "Edit Rights of &lt;acc&gt;" (<c>handlePlayerRights</c>, <c>TPlayerList.cpp:752-849</c>): the account, the IP range, a check box
/// for every right bit in the C++ two-column order (bit 13 included: the C++ had none and cleared it, defect D13; bits above 19
/// are kept as they came), the presets 1 to 4 and "Clear all", and the folder rights. Apply sends with the connection in use then
/// and closes; when it cannot be sent the window stays open.
/// </summary>
internal sealed partial class RightsWindow : RcWindow
{
    /// <summary>The check boxes in the C++ layout: label, bit, column, row.</summary>
    internal static readonly (string Label, int Bit, int Column, int Row)[] Layout =
    {
        ("Warpto XY", 0, 0, 0), ("Set server flags", 15, 1, 0), ("Warpto player", 1, 0, 1), ("Change rights", 10, 1, 1),
        ("Warp players", 2, 0, 2), ("Ban players", 11, 1, 2), ("Update level", 3, 0, 3), ("Change comments", 12, 1, 3),
        ("Disconnect players", 4, 0, 4), ("Change staff accounts", 14, 1, 4), ("View player attributes", 5, 0, 5), ("Change server options", 16, 1, 5),
        ("Set player attributes", 6, 0, 6), ("Edit folder configuration", 17, 1, 6), ("Set the own attributes", 7, 0, 7), ("Edit folder rights", 18, 1, 7),
        ("Reset attributes", 8, 0, 8), ("NPC-Control", 19, 1, 8), ("Admin message", 9, 0, 9), ("Right 13 (unnamed)", 13, 1, 9),
    };

    /// <summary>The presets "1" to "4" (<c>presetRights</c>).</summary>
    internal static readonly int[] PresetRights = { 0b111, 0b1110_1111, (1 << 15) - 1, (1 << 20) - 1 };

    private const int KnownMask = (1 << 20) - 1;
    private readonly PlayersSession? _owner;
    private readonly CheckBox[] _checks = new CheckBox[20];

    /// <summary>For the designer.</summary>
    public RightsWindow() : this(null, "account", new PlayerRights(0, string.Empty, string.Empty))
    {
    }

    /// <summary>Creates the dialog for the rights the server sent.</summary>
    /// <param name="owner">The session.</param>
    /// <param name="account">The account they belong to.</param>
    /// <param name="rights">The rights.</param>
    public RightsWindow(PlayersSession? owner, string account, PlayerRights rights) : base(owner?.App)
    {
        InitializeComponent();
        _owner = owner;
        Account = account;
        Original = rights;
        Title = "Edit Rights of " + account;
        AccountBox.Text = account;
        IpRangeBox.Text = rights.IpRange;
        FoldersBox.Text = rights.Folders;
        for (int row = 0; row < 10; row++) RightsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        foreach ((string label, int bit, int column, int row) in Layout)
        {
            var check = new CheckBox { Content = label, Name = "Right" + bit, IsChecked = rights.Has(bit), MinHeight = 0, Height = 25, Padding = new Avalonia.Thickness(6, 0, 0, 0), VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(check, column);
            Grid.SetRow(check, row);
            RightsGrid.Children.Add(check);
            _checks[bit] = check;
        }
        ClearAllButton.Click += On.Click(() => SetBits(0), "Rights.ClearAll");
        for (int index = 0; index < PresetRights.Length; index++)
        {
            int value = PresetRights[index];
            var preset = new Button { Content = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Name = "Preset" + (index + 1), MinWidth = 30, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            preset.Click += On.Click(() => SetBits(value), "Rights.Preset");
            Presets.Children.Add(preset);
        }
        ApplyButton.Click += On.ClickAsync(ApplyAsync, "Rights.Apply");
        CloseButton.Click += On.Click(Close, "Rights.Close");
    }

    /// <summary>The account the dialog was opened for.</summary>
    public string Account { get; }

    /// <summary>The rights as the server sent them.</summary>
    public PlayerRights Original { get; }

    /// <summary>The rights as the dialog shows them now.</summary>
    public PlayerRights Current
    {
        get
        {
            int bits = Original.Rights & ~KnownMask;
            for (int bit = 0; bit < 20; bit++)
                if (_checks[bit].IsChecked == true) bits |= 1 << bit;
            return new PlayerRights(bits, IpRangeBox.Text ?? string.Empty, (FoldersBox.Text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal));
        }
    }

    /// <summary>The editor text of the current rights (<see cref="PlayerRightsText"/>).</summary>
    public string EditorText => PlayerRightsText.Format(Current);

    /// <summary>The check box of a bit (for tests).</summary>
    internal CheckBox CheckOf(int bit) => _checks[bit];

    /// <summary>Sets every check box from a value (presets, Clear all).</summary>
    /// <param name="value">The bits.</param>
    public void SetBits(int value)
    {
        for (int bit = 0; bit < 20; bit++) _checks[bit].IsChecked = (value & (1 << bit)) != 0;
    }

    /// <summary>Sends the rights and closes.</summary>
    /// <returns>Whether they were sent.</returns>
    public async Task<bool> ApplyAsync()
    {
        if (_owner is null || IsClosed) return false;
        PlayerRights rights = Current;
        bool sent = await _owner.SendAsync(this, session => session.SetPlayerRightsAsync(Account, rights)).ConfigureAwait(true);
        if (sent) Close();
        return sent;
    }
}
