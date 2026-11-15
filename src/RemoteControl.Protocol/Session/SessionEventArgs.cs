using RemoteControl.Protocol.Text;

namespace RemoteControl.Protocol.Session;

/// <summary>An event that carries one value.</summary>
/// <typeparam name="T">The value's type.</typeparam>
public sealed class ValueEventArgs<T> : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="value">The value.</param>
    public ValueEventArgs(T value) => Value = value;

    /// <summary>The value.</summary>
    public T Value { get; }
}

/// <summary>An event that carries a text.</summary>
public sealed class TextEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="text">The text.</param>
    public TextEventArgs(string text) => Text = text;

    /// <summary>The text (lines separated by <c>\n</c>).</summary>
    public string Text { get; }
}

/// <summary>The session or its NC connection ended.</summary>
public sealed class DisconnectedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="requested">Whether the program asked for it.</param>
    public DisconnectedEventArgs(string reason, bool requested)
    {
        Reason = reason;
        Requested = requested;
    }

    /// <summary>The server's disconnect message, or what went wrong.</summary>
    public string Reason { get; }

    /// <summary>Whether <see cref="IRcSession.Disconnect"/> or <see cref="IRcSession.DisconnectNc"/> was called.</summary>
    public bool Requested { get; }
}

/// <summary>A player joined, changed or left.</summary>
public sealed class PlayerEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="player">The player.</param>
    public PlayerEventArgs(RcPlayer player) => Player = player;

    /// <summary>The player (for a player who left: as last known).</summary>
    public RcPlayer Player { get; }
}

/// <summary>A private message arrived.</summary>
public sealed class PrivateMessageEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public PrivateMessageEventArgs(int playerId, string account, string nickname, string text, PrivateMessageKind kind)
    {
        PlayerId = playerId;
        Account = account;
        Nickname = nickname;
        Text = text;
        Kind = kind;
    }

    /// <summary>The sender's player id.</summary>
    public int PlayerId { get; }

    /// <summary>The sender's account, from the player list (empty when unknown).</summary>
    public string Account { get; }

    /// <summary>The sender's nickname, from the player list (empty when unknown).</summary>
    public string Nickname { get; }

    /// <summary>The message.</summary>
    public string Text { get; }

    /// <summary>The kind of message.</summary>
    public PrivateMessageKind Kind { get; }
}

/// <summary>A property of a player changed (from PLO_ADDPLAYER or PLO_OTHERPLPROPS).</summary>
public sealed class PlayerPropertyChangedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public PlayerPropertyChangedEventArgs(int playerId, int propertyId, string name, string value)
    {
        PlayerId = playerId;
        PropertyId = propertyId;
        Name = name;
        Value = value;
    }

    /// <summary>The player id.</summary>
    public int PlayerId { get; }

    /// <summary>The property id.</summary>
    public int PropertyId { get; }

    /// <summary>RClib's name of the property (<c>"nick"</c>, <c>"level"</c>, <c>"attr3"</c> ...).</summary>
    public string Name { get; }

    /// <summary>The value as text.</summary>
    public string Value { get; }
}

/// <summary>The raw property bytes of a player changed.</summary>
public sealed class PlayerPropertiesChangedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public PlayerPropertiesChangedEventArgs(int playerId, byte[] properties)
    {
        PlayerId = playerId;
        Properties = properties;
    }

    /// <summary>The player id.</summary>
    public int PlayerId { get; }

    /// <summary>The property stream as received.</summary>
    public byte[] Properties { get; }
}

/// <summary>An NPC was added or deleted.</summary>
public sealed class NpcEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public NpcEventArgs(int id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>The NPC id.</summary>
    public int Id { get; }

    /// <summary>The name (empty for a deletion).</summary>
    public string Name { get; }
}

/// <summary>A text about one NPC arrived (attributes, flags).</summary>
public sealed class NpcTextEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public NpcTextEventArgs(int npcId, string text)
    {
        NpcId = npcId;
        Text = text;
    }

    /// <summary>The NPC id (for attributes: the id of the matching request, -1 when there was none).</summary>
    public int NpcId { get; }

    /// <summary>The text.</summary>
    public string Text { get; }
}

/// <summary>A script arrived.</summary>
public sealed class ScriptReceivedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public ScriptReceivedEventArgs(ScriptKind kind, string name, int id, string script, string image)
    {
        Kind = kind;
        Name = name;
        Id = id;
        Script = script;
        Image = image;
    }

    /// <summary>What kind of script.</summary>
    public ScriptKind Kind { get; }

    /// <summary>The weapon, class or NPC name.</summary>
    public string Name { get; }

    /// <summary>The NPC id (0 for weapons and classes).</summary>
    public int Id { get; }

    /// <summary>The script with <c>\n</c> line breaks.</summary>
    public string Script { get; }

    /// <summary>The weapon image (empty for classes and NPCs).</summary>
    public string Image { get; }
}

/// <summary>A server text RClib reported through <c>on_server_data</c> that has no event of its own.</summary>
public sealed class ServerDataEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public ServerDataEventArgs(string kind, string content)
    {
        Kind = kind;
        Content = content;
    }

    /// <summary>RClib's data type: <c>"bombadd"</c>, <c>"boardpacket"</c>, <c>"clearweapons"</c>, <c>"PLO_RC_ACCOUNTADD"</c> ...</summary>
    public string Kind { get; }

    /// <summary>The payload as text.</summary>
    public string Content { get; }
}

/// <summary>A player's rights arrived.</summary>
public sealed class PlayerRightsEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public PlayerRightsEventArgs(string account, PlayerRights rights)
    {
        Account = account;
        Rights = rights;
    }

    /// <summary>The account.</summary>
    public string Account { get; }

    /// <summary>The rights.</summary>
    public PlayerRights Rights { get; }
}

/// <summary>A text about an account arrived (comments).</summary>
public sealed class AccountTextEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public AccountTextEventArgs(string account, string text)
    {
        Account = account;
        Text = text;
    }

    /// <summary>The account.</summary>
    public string Account { get; }

    /// <summary>The text.</summary>
    public string Text { get; }
}

/// <summary>An account arrived.</summary>
public sealed class AccountDataEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public AccountDataEventArgs(AccountData account) => Account = account;

    /// <summary>The account.</summary>
    public AccountData Account { get; }
}

/// <summary>A profile arrived.</summary>
public sealed class ProfileEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public ProfileEventArgs(string account, IReadOnlyList<string> fields)
    {
        Account = account;
        Fields = fields;
    }

    /// <summary>The account.</summary>
    public string Account { get; }

    /// <summary>The fields after the account (on Alpaxis: name, age, sex, country, messenger, e-mail, homepage, hangout, quote, online time, then profile variables).</summary>
    public IReadOnlyList<string> Fields { get; }
}

/// <summary>A player's attributes arrived.</summary>
public sealed class PlayerAttributesEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public PlayerAttributesEventArgs(PlayerAttributes attributes, string editorText)
    {
        Attributes = attributes;
        EditorText = editorText;
    }

    /// <summary>The account.</summary>
    public string Account => Attributes.Account;

    /// <summary>The attributes.</summary>
    public PlayerAttributes Attributes { get; }

    /// <summary>The attributes formatted for the editor.</summary>
    public string EditorText { get; }
}

/// <summary>The local NPCs (variable dump) of a level arrived.</summary>
public sealed class LocalNpcsEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public LocalNpcsEventArgs(string level, string text)
    {
        Level = level;
        Text = text;
    }

    /// <summary>The level of the matching request (empty when there was none).</summary>
    public string Level { get; }

    /// <summary>The dump.</summary>
    public string Text { get; }
}

/// <summary>A line for an IRC channel.</summary>
public sealed class IrcMessageEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public IrcMessageEventArgs(string channel, string line)
    {
        Channel = channel;
        Line = line;
    }

    /// <summary>The channel.</summary>
    public string Channel { get; }

    /// <summary>The line: <c>"* Joined #c"</c>, <c>"* Left #c"</c>, <c>"&lt;nick&gt; text"</c> or <c>"* nick: text"</c>.</summary>
    public string Line { get; }
}

/// <summary>Ban data of an account arrived.</summary>
public sealed class BanDataEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public BanDataEventArgs(string account, string computerId, string details)
    {
        Account = account;
        ComputerId = computerId;
        Details = details;
    }

    /// <summary>The account.</summary>
    public string Account { get; }

    /// <summary>The computer id (empty for the legacy packet).</summary>
    public string ComputerId { get; }

    /// <summary>The details, <c>key=value</c> lines (see <see cref="BanText.ParseDetails"/>).</summary>
    public string Details { get; }
}

/// <summary>A lister list arrived (ban types, ban history, staff activity).</summary>
public sealed class BanListEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public BanListEventArgs(string kind, string account, string content)
    {
        Kind = kind;
        Account = account;
        Content = content;
    }

    /// <summary><c>"bantypes"</c>, <c>"banhistory"</c> or <c>"staffactivity"</c>.</summary>
    public string Kind { get; }

    /// <summary>The account (empty for ban types).</summary>
    public string Account { get; }

    /// <summary>The lines.</summary>
    public string Content { get; }
}

/// <summary>The account list arrived.</summary>
public sealed class AccountListEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public AccountListEventArgs(IReadOnlyList<string> accounts) => Accounts = accounts;

    /// <summary>The accounts, sorted without regard to case.</summary>
    public IReadOnlyList<string> Accounts { get; }
}

/// <summary>The players of a private-message server arrived (a <c>pmserverplayers</c> server text).</summary>
public sealed class PmServerPlayersEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public PmServerPlayersEventArgs(string server, string players)
    {
        Server = server;
        Players = players;
    }

    /// <summary>The server.</summary>
    public string Server { get; }

    /// <summary>The player lines.</summary>
    public string Players { get; }
}

/// <summary>A packet arrived (for a packet log).</summary>
public sealed class RawPacketEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public RawPacketEventArgs(RcConnectionKind connection, int packetId, byte[] payload)
    {
        Connection = connection;
        PacketId = packetId;
        Payload = payload;
    }

    /// <summary>The connection.</summary>
    public RcConnectionKind Connection { get; }

    /// <summary>The packet id.</summary>
    public int PacketId { get; }

    /// <summary>The bytes after the id.</summary>
    public byte[] Payload { get; }
}

/// <summary>The files of a folder arrived.</summary>
public sealed class FileBrowserFilesEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public FileBrowserFilesEventArgs(string folder, IReadOnlyList<FileBrowserEntry> files)
    {
        Folder = folder;
        Files = files;
    }

    /// <summary>The folder (empty for an empty listing).</summary>
    public string Folder { get; }

    /// <summary>The files.</summary>
    public IReadOnlyList<FileBrowserEntry> Files { get; }
}

/// <summary>A downloaded file arrived.</summary>
public sealed class FileReceivedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public FileReceivedEventArgs(string path, byte[] content)
    {
        Path = path;
        Content = content;
    }

    /// <summary>The path as requested (<c>folder/name</c>), or the name the server sent when nobody asked.</summary>
    public string Path { get; }

    /// <summary>The bytes.</summary>
    public byte[] Content { get; }
}

/// <summary>A file transfer failed.</summary>
public sealed class FileTransferFailedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public FileTransferFailedEventArgs(string path, string reason)
    {
        Path = path;
        Reason = reason;
    }

    /// <summary>The path.</summary>
    public string Path { get; }

    /// <summary>Why.</summary>
    public string Reason { get; }
}

/// <summary>Progress of a file transfer.</summary>
public sealed class FileTransferProgressEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public FileTransferProgressEventArgs(string path, long transferred, long total, bool upload)
    {
        Path = path;
        Transferred = transferred;
        Total = total;
        Upload = upload;
    }

    /// <summary>The path.</summary>
    public string Path { get; }

    /// <summary>Bytes transferred so far.</summary>
    public long Transferred { get; }

    /// <summary>The size, or 0 while unknown.</summary>
    public long Total { get; }

    /// <summary>Whether it is an upload.</summary>
    public bool Upload { get; }
}

/// <summary>A transfer's progress, as <see cref="IRcFileBrowser.TryGetTransferProgress"/> reports it.</summary>
/// <param name="Transferred">Bytes so far.</param>
/// <param name="Total">The size, or 0 while unknown.</param>
public readonly record struct TransferProgress(long Transferred, long Total);
