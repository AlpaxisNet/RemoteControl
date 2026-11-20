namespace RemoteControl.Protocol.Session;

/// <summary>Bits of a player's player-list category (property 81).</summary>
[Flags]
public enum PlayerListCategory
{
    /// <summary>A player of this server.</summary>
    None = 0,
    /// <summary>A player of another server (private-message list).</summary>
    External = 1,
    /// <summary>A channel entry (IRC).</summary>
    Channel = 2,
    /// <summary>A user of a channel.</summary>
    ChannelUser = 4,
}

/// <summary>A player in the session's player list.</summary>
/// <param name="Id">The player id.</param>
/// <param name="Account">The account.</param>
/// <param name="Nickname">The nickname (may be empty).</param>
/// <param name="Level">The level (may be empty).</param>
/// <param name="Category">The player-list category.</param>
/// <param name="StatusIcon">The status-message index (property 53), or -1 when not sent.</param>
/// <param name="Community">The community name (may be empty).</param>
/// <param name="Properties">Every property received, by id, as text.</param>
public sealed record RcPlayer(int Id, string Account, string Nickname, string Level, PlayerListCategory Category, int StatusIcon, string Community,
    IReadOnlyDictionary<int, string> Properties)
{
    /// <summary>Whether the player is on another server.</summary>
    public bool IsExternal => (Category & PlayerListCategory.External) != 0;

    /// <summary>Whether this entry is a channel or a channel user (RClib: an <c>irc:</c> account or category bits 2 or 4).</summary>
    public bool IsChannel => Account.StartsWith("irc:", StringComparison.Ordinal) || (Category & (PlayerListCategory.Channel | PlayerListCategory.ChannelUser)) != 0;

    /// <summary>Whether this is the NPC-server pseudo player.</summary>
    public bool IsNpcServer => Account.Contains("(npcserver)", StringComparison.Ordinal);

    /// <summary>The nickname, or the account when there is none.</summary>
    public string DisplayName => Nickname.Length > 0 ? Nickname : Account;
}

/// <summary>A channel derived from a channel entry of the player list: the nickname reads <c>"Name (players, id)"</c>.</summary>
/// <param name="PlayerId">The player id of the entry.</param>
/// <param name="Name">The channel name.</param>
/// <param name="Players">The player count as text.</param>
/// <param name="Id">The channel id as text.</param>
public sealed record RcChannel(int PlayerId, string Name, string Players, string Id)
{
    internal static RcChannel FromPlayer(RcPlayer player)
    {
        string name, players = string.Empty, id = string.Empty;
        string nick = player.Nickname;
        int opening = nick.IndexOf('(', StringComparison.Ordinal);
        if (opening < 0)
        {
            name = nick.Trim();
        }
        else
        {
            name = nick[..opening].Trim();
            int closing = nick.IndexOf(')', opening + 1);
            string stats = closing < 0 ? nick[(opening + 1)..] : nick[(opening + 1)..closing];
            int comma = stats.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0)
            {
                players = stats.Trim();
            }
            else
            {
                players = stats[..comma].Trim();
                id = stats[(comma + 1)..].Trim();
            }
        }

        if (name.Length == 0 && player.Account.StartsWith("irc:", StringComparison.Ordinal)) name = player.Account[4..];
        if (name.Length == 0) name = player.Account;
        return new RcChannel(player.Id, name, players, id);
    }
}

/// <summary>A weapon of the NC weapon list.</summary>
/// <param name="Name">The name.</param>
/// <param name="Image">The image (empty until the script was fetched).</param>
/// <param name="Script">The script (empty until fetched).</param>
public sealed record RcWeapon(string Name, string Image, string Script);

/// <summary>A class of the NC class list.</summary>
/// <param name="Name">The name.</param>
/// <param name="Script">The script (empty until fetched).</param>
public sealed record RcClass(string Name, string Script);

/// <summary>A database NPC of the NC NPC list.</summary>
/// <param name="Id">The NPC id.</param>
/// <param name="Name">The name.</param>
/// <param name="Type">The type.</param>
/// <param name="Level">The level it is in.</param>
/// <param name="Script">The script (empty until fetched).</param>
public sealed record RcNpc(int Id, string Name, string Type, string Level, string Script);

/// <summary>A level of the NC level list.</summary>
/// <param name="Name">The level name.</param>
public sealed record RcLevel(string Name);

/// <summary>A folder the RC may browse, from the folder list: <c>"&lt;rights&gt; &lt;pattern&gt;"</c>.</summary>
/// <param name="Rights">The rights (<c>r</c>, <c>rw</c> ...).</param>
/// <param name="Pattern">The folder and wildcard, e.g. <c>levels/*.nw</c>.</param>
public sealed record FileBrowserFolder(string Rights, string Pattern)
{
    /// <summary>The folder part of the pattern, ending in <c>/</c> (what a change of folder sends).</summary>
    public string Folder
    {
        get
        {
            int slash = Pattern.LastIndexOf('/');
            return slash < 0 ? string.Empty : Pattern[..(slash + 1)];
        }
    }

    /// <summary>Whether the rights contain <c>w</c>: files may be uploaded into the folder.</summary>
    public bool AllowsWrite => Rights.Contains('w', StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this rule covers the file <paramref name="name"/> in <paramref name="folder"/>, as the game server decides it: the pattern's
    /// folder part equals the folder without regard to case (slashes at either end do not count), and the pattern's last part matches the
    /// name (a pattern that ends in a slash covers every file of its folder, as the server's <c>FolderRight.TryParse</c> reads it; <c>*</c> for any run of characters, <c>?</c> for one character, without regard to case).
    /// </summary>
    /// <param name="folder">The folder, e.g. <c>world/heads/</c>.</param>
    /// <param name="name">The bare file name.</param>
    /// <returns>Whether the rule covers it.</returns>
    public bool Covers(string folder, string name)
    {
        static string Key(string text) => text.Replace('\\', '/').Trim('/');
        int slash = Pattern.LastIndexOf('/');
        string filePart = slash < 0 ? Pattern : Pattern[(slash + 1)..];
        if (filePart.Length == 0) filePart = "*";
        return string.Equals(Key(Folder), Key(folder), StringComparison.OrdinalIgnoreCase) && WildcardMatches(filePart, name);
    }

    private static bool WildcardMatches(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])) && pattern[p] != '*')
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

/// <summary>A file of a folder listing.</summary>
/// <param name="Name">The file name (a name ending in <c>/</c> is a folder).</param>
/// <param name="Rights">The rights.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Modified">The modification time in Unix seconds.</param>
public sealed record FileBrowserEntry(string Name, string Rights, long Size, long Modified)
{
    /// <summary>Whether the entry is a folder.</summary>
    public bool IsDirectory => Name.EndsWith('/');

    /// <summary>The modification time.</summary>
    public DateTimeOffset ModifiedTime => DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(Modified, 0, 253402300799));
}

/// <summary>An entry of the list server's simple server list (the answer to <see cref="IRcSession.RequestServerListAsync"/>).</summary>
/// <param name="Name">The server's name.</param>
/// <param name="DisplayName">The name as shown (with the type prefix).</param>
/// <param name="Players">The player count.</param>
public sealed record SimpleServerEntry(string Name, string DisplayName, int Players);

/// <summary>Which connection of a session.</summary>
public enum RcConnectionKind
{
    /// <summary>The game-server (RC) connection.</summary>
    Rc,
    /// <summary>The NPC-Control connection.</summary>
    Nc,
}

/// <summary>The kind of a received script.</summary>
public enum ScriptKind
{
    /// <summary>A weapon script.</summary>
    Weapon,
    /// <summary>A class script.</summary>
    Class,
    /// <summary>A database NPC script.</summary>
    Npc,
}

/// <summary>The kind of a private message, from its prefix.</summary>
public enum PrivateMessageKind
{
    /// <summary>A normal private message.</summary>
    Normal,
    /// <summary>A mass message.</summary>
    Mass,
    /// <summary>A guild message.</summary>
    Guild,
    /// <summary>An admin message.</summary>
    Admin,
}

/// <summary>The outcome of a connect.</summary>
/// <param name="Success">Whether the session is connected and logged in.</param>
/// <param name="Error">Why not.</param>
public sealed record ConnectResult(bool Success, string? Error)
{
    /// <summary>A successful result.</summary>
    public static ConnectResult Ok { get; } = new(true, null);

    /// <summary>A failed result.</summary>
    /// <param name="error">Why.</param>
    /// <returns>The result.</returns>
    public static ConnectResult Failed(string error) => new(false, error);
}

/// <summary>How the NC server expects line lists (class scripts, NPC flags, level lists) to be sent.</summary>
public enum NcLineListFormat
{
    /// <summary>One length-prefixed string per line, as the Alpaxis server reads them (lines of at most 223 bytes).</summary>
    LineStrings,
    /// <summary>Comma text, as RClib sent them to classic servers.</summary>
    CommaText,
}
