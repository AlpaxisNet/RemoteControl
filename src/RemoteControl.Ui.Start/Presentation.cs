using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using RemoteControl.Core;
using RemoteControl.Protocol.ListServer;

namespace RemoteControl.Ui.Start;

/// <summary>How saved accounts are shown (<c>TAccountPresentation.cpp</c>): the name and a <c>[list server]</c> badge.</summary>
internal static class AccountPresentation
{
    /// <summary>The title of the login window when no account (or one without list server) is selected.</summary>
    public const string PlainTitle = "RemoteControl";

    /// <summary>The badge label of an account (<c>accountServerBadgeLabels</c>): its list-server profile name, or <c>Unassigned</c>.</summary>
    /// <param name="account">The account.</param>
    /// <param name="profiles">The profiles.</param>
    /// <returns>The label.</returns>
    public static string Badge(SavedAccount account, IEnumerable<ListServerProfile> profiles) =>
        ListServerAssociation.BadgeLabel(account.ListServer, profiles);

    /// <summary>The longer description of an account (<c>accountServerDetail</c>), used as tool tip.</summary>
    /// <param name="account">The account.</param>
    /// <param name="profiles">The profiles.</param>
    /// <returns>For example <c>name — Alpaxis — listserver.alpaxis.net:14922</c>.</returns>
    public static string Detail(SavedAccount account, IEnumerable<ListServerProfile> profiles)
    {
        string detail = account.Name + ListServerAssociation.LabelSeparator;
        if (string.IsNullOrEmpty(account.ListServer)) return detail + "no saved list server";
        string association = ListServerAssociation.Normalize(account.ListServer, profiles);
        return account.DirectMode ? detail + "direct to " + association : detail + association;
    }

    /// <summary>The login window title for an account (<c>updateAccountTitle</c>).</summary>
    /// <param name="account">The selected account, or null.</param>
    /// <param name="profiles">The profiles.</param>
    /// <returns><c>"&lt;badge&gt; - RemoteControl"</c> or <see cref="PlainTitle"/>.</returns>
    public static string Title(SavedAccount? account, IEnumerable<ListServerProfile> profiles)
    {
        if (account is null) return PlainTitle;
        string badge = Badge(account, profiles);
        return badge.Length == 0 || badge == "Unassigned" ? PlainTitle : badge + " - " + PlainTitle;
    }

    /// <summary>How a profile is named in lists: <c>"Name — host:port"</c>, with <c>(built in)</c> for Alpaxis.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The text.</returns>
    public static string ProfileText(ListServerProfile profile) => profile.IsBuiltIn ? profile.Association + " (built in)" : profile.Association;
}

/// <summary>A row of the account lists (login window drop-down, accounts manager).</summary>
/// <param name="Index">The account's index in the store.</param>
/// <param name="Id">The account's id.</param>
/// <param name="Name">The account name (shown bold).</param>
/// <param name="Badge">The list-server label (shown small, in brackets).</param>
/// <param name="Detail">The tool tip.</param>
internal sealed record AccountRow(int Index, ulong Id, string Name, string Badge, string Detail)
{
    /// <summary>The badge with its brackets.</summary>
    public string BadgeText => "[" + Badge + "]";

    /// <summary>Makes the rows of every saved account.</summary>
    /// <param name="accounts">The store.</param>
    /// <param name="profiles">The profiles.</param>
    /// <returns>The rows, in store order.</returns>
    public static List<AccountRow> From(AccountStore accounts, IReadOnlyList<ListServerProfile> profiles)
    {
        var rows = new List<AccountRow>();
        IReadOnlyList<SavedAccount> saved = accounts.Accounts;
        for (int index = 0; index < saved.Count; index++)
        {
            SavedAccount account = saved[index];
            rows.Add(new AccountRow(index, account.Id, account.Name, AccountPresentation.Badge(account, profiles), AccountPresentation.Detail(account, profiles)));
        }
        return rows;
    }
}

/// <summary>An entry of a list-server combo: the text shown and the association it stands for.</summary>
/// <param name="Text">What is shown.</param>
/// <param name="Value">The association saved with an account.</param>
/// <param name="Profile">The profile, or null for a saved association that matches no profile.</param>
internal sealed record ProfileChoice(string Text, string Value, ListServerProfile? Profile)
{
    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>A row of the server list: icon, name, players, and the details shown beside the list.</summary>
internal sealed class ServerRow : INotifyPropertyChanged
{
    private ServerInfo _server;
    private Bitmap? _icon;

    /// <summary>Creates a row.</summary>
    /// <param name="server">The server.</param>
    /// <param name="icon">Its kind's icon.</param>
    public ServerRow(ServerInfo server, Bitmap? icon)
    {
        _server = server;
        _icon = icon;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The server as the list server sent it.</summary>
    public ServerInfo Server => _server;

    /// <summary>The name without its kind prefix.</summary>
    public string Name => _server.DisplayName;

    /// <summary>The player count as shown.</summary>
    public string PlayersText => _server.Players.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The player count.</summary>
    public int Players => _server.Players;

    /// <summary>The icon of the server's kind.</summary>
    public Bitmap? Icon => _icon;

    /// <summary>The picture index of a server kind (<c>getServerListIcon</c>): 0 gold, 1 UC (hidden), 2 bronze (classic and hosted), 3 silver (3D).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The index for <see cref="Common.AssetStore.ServerTypeIcon"/>.</returns>
    public static int IconIndex(ServerKind kind) => kind switch
    {
        ServerKind.Gold => 0,
        ServerKind.Hidden => 1,
        ServerKind.Silver => 3,
        _ => 2,
    };

    /// <summary>Takes the values of a newer copy of the same server.</summary>
    /// <param name="server">The new values.</param>
    /// <param name="icon">The icon.</param>
    public void Update(ServerInfo server, Bitmap? icon)
    {
        if (_server == server && ReferenceEquals(_icon, icon)) return;
        _server = server;
        _icon = icon;
        Raise(nameof(Server));
        Raise(nameof(Name));
        Raise(nameof(PlayersText));
        Raise(nameof(Players));
        Raise(nameof(Icon));
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
