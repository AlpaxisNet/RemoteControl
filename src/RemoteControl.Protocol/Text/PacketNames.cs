namespace RemoteControl.Protocol.Text;

/// <summary>The direction of a packet.</summary>
public enum PacketDirection
{
    /// <summary>From the server.</summary>
    Incoming,
    /// <summary>To the server.</summary>
    Outgoing,
}

/// <summary>The packet names RClib listed for its packet log (<c>rc_get_packet_names</c>): id and C name, in RClib's order.</summary>
public static class PacketNames
{
    private static readonly (int, string)[] NcOutgoing =
    {
        (3, "PLI_NPCPROPS"), (103, "PLI_NC_NPCGET"), (104, "PLI_NC_NPCDELETE"), (105, "PLI_NC_NPCRESET"), (106, "PLI_NC_NPCSCRIPTGET"),
        (107, "PLI_NC_NPCWARP"), (108, "PLI_NC_NPCFLAGSGET"), (109, "PLI_NC_NPCSCRIPTSET"), (110, "PLI_NC_NPCFLAGSSET"), (111, "PLI_NC_NPCADD"),
        (112, "PLI_NC_CLASSEDIT"), (113, "PLI_NC_CLASSADD"), (114, "PLI_NC_LOCALNPCSGET"), (115, "PLI_NC_WEAPONLISTGET"),
        (116, "PLI_NC_WEAPONGET"), (117, "PLI_NC_WEAPONADD"), (118, "PLI_NC_WEAPONDELETE"), (119, "PLI_NC_CLASSDELETE"),
        (150, "PLI_NC_LEVELLISTGET"), (151, "PLI_NC_LEVELLISTSET"),
    };

    private static readonly (int, string)[] NcIncoming =
    {
        (157, "PLO_NC_NPCATTRIBUTES"), (158, "PLO_NC_NPCADD"), (159, "PLO_NC_NPCDELETE"), (160, "PLO_NC_NPCSCRIPT"), (161, "PLO_NC_NPCFLAGS"),
        (162, "PLO_NC_CLASSGET"), (163, "PLO_NC_CLASSADD"), (164, "PLO_NC_LEVELDUMP"), (167, "PLO_NC_WEAPONLISTGET"), (188, "PLO_NC_CLASSDELETE"),
        (192, "PLO_NC_WEAPONGET"),
    };

    private static readonly (int, string)[] RcOutgoing =
    {
        (6, "PLI_TOALL"), (50, "PLI_RAWDATA"), (51, "PLI_RC_SERVEROPTIONSGET"), (52, "PLI_RC_SERVEROPTIONSSET"), (53, "PLI_RC_FOLDERCONFIGGET"),
        (54, "PLI_RC_FOLDERCONFIGSET"), (55, "PLI_RC_RESPAWNSET"), (56, "PLI_RC_HORSELIFESET"), (57, "PLI_RC_APINCREMENTSET"),
        (58, "PLI_RC_BADDYRESPAWNSET"), (59, "PLI_RC_PLAYERPROPSGET"), (60, "PLI_RC_PLAYERPROPSSET"), (28, "PLI_PRIVATEMESSAGE"),
        (61, "PLI_RC_DISCONNECTPLAYER"), (62, "PLI_RC_UPDATELEVELS"), (63, "PLI_RC_ADMINMESSAGE"), (64, "PLI_RC_PRIVADMINMESSAGE"),
        (65, "PLI_RC_LISTRCS"), (66, "PLI_RC_DISCONNECTRC"), (67, "PLI_RC_APPLYREASON"), (68, "PLI_RC_SERVERFLAGSGET"),
        (69, "PLI_RC_SERVERFLAGSSET"), (70, "PLI_RC_ACCOUNTADD"), (71, "PLI_RC_ACCOUNTDEL"), (72, "PLI_RC_ACCOUNTLISTGET"),
        (73, "PLI_RC_PLAYERPROPSGET2"), (74, "PLI_RC_PLAYERPROPSGET3"), (75, "PLI_RC_PLAYERPROPSRESET"), (76, "PLI_RC_PLAYERPROPSSET2"),
        (77, "PLI_RC_ACCOUNTGET"), (78, "PLI_RC_ACCOUNTSET"), (79, "PLI_RC_CHAT"), (80, "PLI_PROFILEGET"), (81, "PLI_PROFILESET"),
        (82, "PLI_RC_WARPPLAYER"), (83, "PLI_RC_PLAYERRIGHTSGET"), (84, "PLI_RC_PLAYERRIGHTSSET"), (85, "PLI_RC_PLAYERCOMMENTSGET"),
        (86, "PLI_RC_PLAYERCOMMENTSSET"), (87, "PLI_RC_PLAYERBANGET"), (88, "PLI_RC_PLAYERBANSET"), (89, "PLI_RC_FILEBROWSER_START"),
        (90, "PLI_RC_FILEBROWSER_CD"), (91, "PLI_RC_FILEBROWSER_END"), (92, "PLI_RC_FILEBROWSER_DOWN"), (93, "PLI_RC_FILEBROWSER_UP"),
        (94, "PLI_NPCSERVERQUERY"), (96, "PLI_RC_FILEBROWSER_MOVE"), (97, "PLI_RC_FILEBROWSER_DELETE"), (98, "PLI_RC_FILEBROWSER_RENAME"),
        (100, "PLI_NC_LISTNPCS"), (103, "PLI_NC_NPCGET"), (104, "PLI_NC_NPCDELETE"), (105, "PLI_NC_NPCRESET"), (106, "PLI_NC_NPCSCRIPTGET"),
        (107, "PLI_NC_NPCWARP"), (108, "PLI_NC_NPCFLAGSGET"), (109, "PLI_NC_NPCSCRIPTSET"), (110, "PLI_NC_NPCFLAGSSET"), (111, "PLI_NC_NPCADD"),
        (112, "PLI_NC_CLASSEDIT"), (113, "PLI_NC_CLASSADD"), (114, "PLI_NC_LOCALNPCSGET"), (115, "PLI_NC_WEAPONLISTGET"),
        (116, "PLI_NC_WEAPONGET"), (117, "PLI_NC_WEAPONADD"), (118, "PLI_NC_WEAPONDELETE"), (119, "PLI_NC_CLASSDELETE"),
        (130, "PLI_REQUESTUPDATEBOARD"), (150, "PLI_NC_LEVELLISTGET"), (151, "PLI_NC_LEVELLISTSET"), (152, "PLI_REQUESTTEXT"),
        (154, "PLI_SENDTEXT"), (155, "PLI_RC_LARGEFILESTART"), (156, "PLI_RC_LARGEFILEEND"), (157, "PLI_UPDATEGANI"), (158, "PLI_UPDATESCRIPT"),
        (159, "PLI_UPDATEPACKAGEREQUESTFILE"), (160, "PLI_RC_FOLDERDELETE"), (161, "PLI_UPDATECLASS"),
    };

    private static readonly (int, string)[] RcIncoming =
    {
        (8, "PLO_OTHERPLPROPS"), (9, "PLO_PLAYERPROPS"), (11, "PLO_BOMBADD"), (13, "PLO_TOALL"), (30, "PLO_FILESENDFAILED"),
        (42, "PLO_NEWWORLDTIME"), (35, "PLO_RC_ADMINMESSAGE"), (50, "PLO_RC_ACCOUNTADD"), (51, "PLO_RC_ACCOUNTSTATUS"),
        (52, "PLO_RC_ACCOUNTNAME"), (53, "PLO_RC_ACCOUNTDEL"), (54, "PLO_RC_ACCOUNTPROPS"), (55, "PLO_ADDPLAYER"), (56, "PLO_DELPLAYER"),
        (57, "PLO_RC_ACCOUNTPROPSGET"), (58, "PLO_RC_ACCOUNTCHANGE"), (59, "PLO_RC_PLAYERPROPSCHANGE"), (60, "PLO_UNKNOWN60"),
        (61, "PLO_RC_SERVERFLAGSGET"), (62, "PLO_RC_PLAYERRIGHTSGET"), (63, "PLO_RC_PLAYERCOMMENTSGET"), (64, "PLO_RC_PLAYERBANGET"),
        (65, "PLO_RC_FILEBROWSER_DIRLIST"), (66, "PLO_RC_FILEBROWSER_DIR"), (67, "PLO_RC_FILEBROWSER_MESSAGE"), (68, "PLO_LARGEFILESTART"),
        (69, "PLO_LARGEFILEEND"), (70, "PLO_RC_ACCOUNTLISTGET"), (71, "PLO_RC_PLAYERPROPS"), (72, "PLO_RC_PLAYERPROPSGET"),
        (73, "PLO_RC_ACCOUNTGET"), (74, "PLO_RC_CHAT"), (75, "PLO_PROFILE"), (76, "PLO_RC_SERVEROPTIONSGET"), (77, "PLO_RC_FOLDERCONFIGGET"),
        (78, "PLO_NC_CONTROL"), (79, "PLO_NPCSERVERADDR"), (80, "PLO_NC_LEVELLIST"), (82, "PLO_SERVERTEXT"), (16, "PLO_DISCMESSAGE"),
        (25, "PLO_SIGNATURE"), (37, "PLO_PRIVATEMESSAGE"), (47, "PLO_STAFFGUILDS"), (84, "PLO_LARGEFILESIZE"), (100, "PLO_RAWDATA"),
        (101, "PLO_BOARDPACKET"), (102, "PLO_FILE"), (103, "PLO_RC_MAXUPLOADFILESIZE"), (180, "PLO_STATUSLIST"),
        (190, "PLO_SERVERLISTCONNECTED"), (194, "PLO_CLEARWEAPONS"),
    };

    /// <summary>The names for one connection and direction.</summary>
    /// <param name="nc">The NC connection (else the game connection).</param>
    /// <param name="direction">The direction.</param>
    /// <returns>Id and name pairs.</returns>
    public static IReadOnlyList<KeyValuePair<int, string>> Get(bool nc, PacketDirection direction)
    {
        (int, string)[] source = nc
            ? direction == PacketDirection.Outgoing ? NcOutgoing : NcIncoming
            : direction == PacketDirection.Outgoing ? RcOutgoing : RcIncoming;
        return source.Select(pair => new KeyValuePair<int, string>(pair.Item1, pair.Item2)).ToArray();
    }

    /// <summary>The names as RClib's text: <c>"id=NAME"</c> lines.</summary>
    /// <param name="nc">The NC connection (else the game connection).</param>
    /// <param name="direction">The direction.</param>
    /// <returns>The text.</returns>
    public static string AsText(bool nc, PacketDirection direction) =>
        string.Join('\n', Get(nc, direction).Select(pair => $"{pair.Key}={pair.Value}"));

    /// <summary>The name of one packet, or <c>null</c> when it is not listed.</summary>
    /// <param name="nc">The NC connection.</param>
    /// <param name="direction">The direction.</param>
    /// <param name="id">The packet id.</param>
    /// <returns>The name.</returns>
    public static string? NameOf(bool nc, PacketDirection direction, int id)
    {
        foreach (var pair in Get(nc, direction))
            if (pair.Key == id) return pair.Value;
        return null;
    }
}
