using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using RemoteControl.Protocol.Text;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>
/// "Profile of &lt;acc&gt;" (<c>TPlayerList.cpp:1043-1163</c>): the player information (editable), the favourite quote and the
/// in-game stats (read only). Apply writes the <c>Label: value</c> text of the C++, reads it with <see cref="ProfileText.Parse"/>,
/// keeps a backup, sends it with the connection in use then and closes.
/// </summary>
internal sealed partial class ProfileWindow : RcWindow
{
    internal static readonly string[] InfoLabels = { "Real name", "Age", "Sex", "Country", "Messenger", "E-mail", "Homepage", "Fav. hangout" };
    private static readonly string[] TextLabels = { "Real Name", "Age", "Sex", "Country", "Messenger", "E-Mail", "Homepage", "Fav. Hangout" };
    private static readonly string[] StatNames = { "Kills", "Deaths", "Maxpower", "Rating", "Alignment", "Gralat", "Swordpower", "Spin" };

    private readonly PlayersSession? _owner;
    private readonly Func<string>[] _readers = new Func<string>[8];
    private readonly string _onlineTime;

    /// <summary>For the designer.</summary>
    public ProfileWindow() : this(null, "account", Array.Empty<string>())
    {
    }

    /// <summary>Creates the dialog for the profile the server sent.</summary>
    /// <param name="owner">The session.</param>
    /// <param name="account">The account.</param>
    /// <param name="fields">The fields after the account: 1 to 9 the information, then online time, level, the stats.</param>
    public ProfileWindow(PlayersSession? owner, string account, IReadOnlyList<string> fields) : base(owner?.App)
    {
        InitializeComponent();
        _owner = owner;
        Account = account;
        Title = "Profile of " + account;
        string Field(int index) => index < fields.Count ? fields[index] ?? string.Empty : string.Empty;
        HeaderText.Text = account + ": " + Field(0);
        for (int index = 0; index < 8; index++)
        {
            InfoGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var caption = new TextBlock { Text = InfoLabels[index] + ":", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(caption, index);
            InfoGrid.Children.Add(caption);
            Control editor;
            string name = "Profile" + index;
            if (index == 2)
            {
                var combo = new ComboBox { IsEditable = true, ItemsSource = new[] { "unknown", "male", "female" }, Text = Field(index), Name = name, HorizontalAlignment = HorizontalAlignment.Stretch };
                _readers[index] = () => combo.Text ?? string.Empty;
                editor = combo;
            }
            else
            {
                var box = new TextBox { Text = Field(index), Name = name };
                _readers[index] = () => box.Text ?? string.Empty;
                editor = box;
            }
            Grid.SetRow(editor, index);
            Grid.SetColumn(editor, 1);
            InfoGrid.Children.Add(editor);
        }
        QuoteBox.Text = Field(8);
        _onlineTime = Field(9);
        OnlineBox.Text = _onlineTime;
        LevelBox.Text = Field(10);
        var stats = new List<string>();
        for (int index = 11; index < fields.Count && index - 11 < StatNames.Length; index++) stats.Add(StatNames[index - 11] + ":  " + fields[index]);
        StatsList.ItemsSource = stats;
        ApplyButton.Click += On.ClickAsync(ApplyAsync, "Profile.Apply");
        CloseButton.Click += On.Click(Close, "Profile.Close");
    }

    /// <summary>The account the dialog was opened for.</summary>
    public string Account { get; }

    /// <summary>The profile text of what the dialog shows now.</summary>
    public string EditorText
    {
        get
        {
            var text = new StringBuilder();
            for (int index = 0; index < 8; index++) text.Append(TextLabels[index]).Append(": ").Append(OneLine(_readers[index]())).Append('\n');
            text.Append("Favourite Quote: ").Append(OneLine(QuoteBox.Text)).Append('\n');
            text.Append("Online Time: ").Append(_onlineTime).Append('\n');
            return text.ToString();
        }
    }

    /// <summary>Fields 1 to 10 as they are sent.</summary>
    public IReadOnlyList<string> Current => ProfileText.Parse(EditorText);

    /// <summary>Sends the profile (with a backup) and closes.</summary>
    /// <returns>Whether it was sent.</returns>
    public async Task<bool> ApplyAsync()
    {
        if (_owner is null || IsClosed) return false;
        string text = EditorText;
        IReadOnlyList<string> fields = Current;
        bool sent = await _owner.SendAsync(this, session => session.SetProfileAsync(Account, fields)).ConfigureAwait(true);
        if (!sent) return false;
        _owner.Backup("profile", Account, text, modified: true);
        Close();
        return true;
    }

    private static string OneLine(string? value) =>
        (value ?? string.Empty).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();
}
