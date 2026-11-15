using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace RemoteControl.Ui.Players;

/// <summary>Raises <see cref="PropertyChanged"/> for the rows of the lists.</summary>
internal abstract class RowBase : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets a field and raises the event when it changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    /// <summary>Raises the event for a computed property.</summary>
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A row of "This server": a group row ("Admins", "Players") or a player, updated in place as the server sends changes.</summary>
internal sealed class PlayerRow : RowBase
{
    private IImage? _icon;
    private string _nick = string.Empty;
    private string _account = string.Empty;
    private string _level = string.Empty;
    private int _status = -1;
    private bool _expanded = true;

    /// <summary>Creates a group row.</summary>
    /// <param name="name">"Admins" or "Players".</param>
    /// <param name="admins">Whether it is the admins group.</param>
    public PlayerRow(string name, bool admins)
    {
        IsGroup = true;
        IsAdminGroup = admins;
        _nick = name;
    }

    /// <summary>Creates a player row.</summary>
    /// <param name="id">The player id.</param>
    /// <param name="order">The order the player joined in (the default sort).</param>
    /// <param name="offline">Whether it is a player who sent a PM and is no longer online.</param>
    public PlayerRow(int id, long order, bool offline)
    {
        Id = id;
        Order = order;
        IsOffline = offline;
    }

    /// <summary>Whether this is a group row.</summary>
    public bool IsGroup { get; }

    /// <summary>Whether this is the admins group row.</summary>
    public bool IsAdminGroup { get; }

    /// <summary>The player id (0 for a group).</summary>
    public int Id { get; }

    /// <summary>The join order.</summary>
    public long Order { get; }

    /// <summary>Whether the player is not online any more (level "Offline").</summary>
    public bool IsOffline { get; }

    /// <summary>The icon: the status (or a PM icon while unread) for a player, open/closed for a group.</summary>
    public IImage? Icon { get => _icon; set => Set(ref _icon, value); }

    /// <summary>The nickname (the group's name for a group row).</summary>
    public string Nick { get => _nick; set => Set(ref _nick, value ?? string.Empty); }

    /// <summary>The account.</summary>
    public string Account { get => _account; set => Set(ref _account, value ?? string.Empty); }

    /// <summary>The level ("Offline" for an offline row).</summary>
    public string Level { get => _level; set => Set(ref _level, value ?? string.Empty); }

    /// <summary>The status index (property 53), or -1.</summary>
    public int Status { get => _status; set => Set(ref _status, value); }

    /// <summary>The id as shown.</summary>
    public string IdText => IsGroup ? string.Empty : Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Bold for group rows.</summary>
    public FontWeight Weight => IsGroup ? FontWeight.Bold : FontWeight.Normal;

    /// <summary>Whether a group is expanded.</summary>
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }

    /// <summary>Whether the player belongs to "Admins": RC and staff clients have no level (<c>isAdminPlayer</c>).</summary>
    public bool InAdmins => !IsOffline && string.IsNullOrWhiteSpace(Level);

    /// <summary>What the actions work on.</summary>
    public PlayerTarget Target => new(Id, Account, Nick, Level);

    /// <inheritdoc />
    public override string ToString() => IsGroup ? Nick : Nick + " (" + Account + ")";
}

/// <summary>A row of the Guilds, Servers or Channels tab.</summary>
internal sealed class SimpleRow : RowBase
{
    private IImage? _icon;
    private string _text1 = string.Empty;
    private string _text2 = string.Empty;
    private string _text3 = string.Empty;

    /// <summary>The icon.</summary>
    public IImage? Icon { get => _icon; set => Set(ref _icon, value); }

    /// <summary>The first text column (the name).</summary>
    public string Text1 { get => _text1; set => Set(ref _text1, value ?? string.Empty); }

    /// <summary>The second text column.</summary>
    public string Text2 { get => _text2; set => Set(ref _text2, value ?? string.Empty); }

    /// <summary>The third text column.</summary>
    public string Text3 { get => _text3; set => Set(ref _text3, value ?? string.Empty); }

    /// <summary>The indentation of the first text (players under a server).</summary>
    public Thickness Indent { get; init; }

    /// <summary>The server of a Servers row (its own name, or the server of a player row).</summary>
    public string Server { get; init; } = string.Empty;

    /// <summary>The player of a Servers row, or null for a server row.</summary>
    public RemotePlayer? Player { get; init; }

    /// <inheritdoc />
    public override string ToString() => Text1;
}

/// <summary>The row templates of the lists: one cell per column, laid out by the inherited <see cref="ColumnLayout"/>.</summary>
internal static class RowTemplates
{
    /// <summary>A template with an icon cell and text cells bound to properties of the row.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="layout">The columns (for alignment).</param>
    /// <param name="iconProperty">The icon property.</param>
    /// <param name="weightProperty">A font weight property, or null.</param>
    /// <param name="indentProperty">A margin property for the first text, or null.</param>
    /// <param name="textProperties">The text properties, one per further column.</param>
    /// <returns>The template.</returns>
    public static FuncDataTemplate<T> Make<T>(ColumnLayout layout, string iconProperty, string? weightProperty, string? indentProperty, params string[] textProperties) where T : class =>
        new((_, _) =>
        {
            var panel = new ColumnsPanel { Background = Brushes.Transparent };
            var image = new Image { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 1) };
            image.Bind(Image.SourceProperty, new Binding(iconProperty));
            panel.Children.Add(image);
            for (int index = 0; index < textProperties.Length; index++)
            {
                var text = new TextBlock
                {
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(4, 1),
                    TextAlignment = layout.RightAligned.Count > index + 1 && layout.RightAligned[index + 1] ? TextAlignment.Right : TextAlignment.Left,
                };
                text.Bind(TextBlock.TextProperty, new Binding(textProperties[index]));
                if (weightProperty is not null) text.Bind(TextBlock.FontWeightProperty, new Binding(weightProperty));
                if (index == 0 && indentProperty is not null) text.Bind(Layoutable.MarginProperty, new Binding(indentProperty));
                panel.Children.Add(text);
            }
            return panel;
        }, supportsRecycling: true);
}
