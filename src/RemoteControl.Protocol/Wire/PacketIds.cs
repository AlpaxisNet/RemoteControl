namespace RemoteControl.Protocol.Wire;

/// <summary>Packets a client sends (RClib <c>PLI_*</c>). On the wire the id is sent as a GByte (id + 32).</summary>
public enum ClientPacketId
{
    /// <summary>PLI_PLAYERPROPS: the client's own properties (the RC nickname).</summary>
    PlayerProps = 2,
    /// <summary>PLI_NPCPROPS: on the NC socket, the NC login.</summary>
    NpcProps = 3,
    /// <summary>PLI_TOALL: a message to everyone; on a new game connection, the RC login.</summary>
    ToAll = 6,
    /// <summary>PLI_PRIVATEMESSAGE.</summary>
    PrivateMessage = 28,
    /// <summary>PLI_RAWDATA: announces a raw block of the given length.</summary>
    RawData = 50,
    /// <summary>PLI_RC_SERVEROPTIONSGET.</summary>
    ServerOptionsGet = 51,
    /// <summary>PLI_RC_SERVEROPTIONSSET.</summary>
    ServerOptionsSet = 52,
    /// <summary>PLI_RC_FOLDERCONFIGGET.</summary>
    FolderConfigGet = 53,
    /// <summary>PLI_RC_FOLDERCONFIGSET.</summary>
    FolderConfigSet = 54,
    /// <summary>PLI_RC_PLAYERPROPSGET (ignored by Alpaxis).</summary>
    PlayerPropsGet = 59,
    /// <summary>PLI_RC_PLAYERPROPSSET.</summary>
    PlayerPropsSet = 60,
    /// <summary>PLI_RC_DISCONNECTPLAYER.</summary>
    DisconnectPlayer = 61,
    /// <summary>PLI_RC_UPDATELEVELS.</summary>
    UpdateLevels = 62,
    /// <summary>PLI_RC_ADMINMESSAGE.</summary>
    AdminMessage = 63,
    /// <summary>PLI_RC_PRIVADMINMESSAGE.</summary>
    PrivateAdminMessage = 64,
    /// <summary>PLI_RC_SERVERFLAGSGET.</summary>
    ServerFlagsGet = 68,
    /// <summary>PLI_RC_SERVERFLAGSSET.</summary>
    ServerFlagsSet = 69,
    /// <summary>PLI_RC_ACCOUNTADD.</summary>
    AccountAdd = 70,
    /// <summary>PLI_RC_ACCOUNTDEL.</summary>
    AccountDelete = 71,
    /// <summary>PLI_RC_ACCOUNTLISTGET.</summary>
    AccountListGet = 72,
    /// <summary>PLI_RC_PLAYERPROPSGET2: attributes by player id.</summary>
    PlayerPropsGetById = 73,
    /// <summary>PLI_RC_PLAYERPROPSGET3: attributes by account.</summary>
    PlayerPropsGetByAccount = 74,
    /// <summary>PLI_RC_PLAYERPROPSRESET.</summary>
    PlayerPropsReset = 75,
    /// <summary>PLI_RC_PLAYERPROPSSET2: attributes by account.</summary>
    PlayerPropsSetByAccount = 76,
    /// <summary>PLI_RC_ACCOUNTGET.</summary>
    AccountGet = 77,
    /// <summary>PLI_RC_ACCOUNTSET.</summary>
    AccountSet = 78,
    /// <summary>PLI_RC_CHAT: an RC chat line or command.</summary>
    RcChat = 79,
    /// <summary>PLI_PROFILEGET.</summary>
    ProfileGet = 80,
    /// <summary>PLI_PROFILESET.</summary>
    ProfileSet = 81,
    /// <summary>PLI_RC_WARPPLAYER.</summary>
    WarpPlayer = 82,
    /// <summary>PLI_RC_PLAYERRIGHTSGET.</summary>
    PlayerRightsGet = 83,
    /// <summary>PLI_RC_PLAYERRIGHTSSET.</summary>
    PlayerRightsSet = 84,
    /// <summary>PLI_RC_PLAYERCOMMENTSGET.</summary>
    PlayerCommentsGet = 85,
    /// <summary>PLI_RC_PLAYERCOMMENTSSET.</summary>
    PlayerCommentsSet = 86,
    /// <summary>PLI_RC_PLAYERBANGET.</summary>
    PlayerBanGet = 87,
    /// <summary>PLI_RC_PLAYERBANSET.</summary>
    PlayerBanSet = 88,
    /// <summary>PLI_RC_FILEBROWSER_START.</summary>
    FileBrowserStart = 89,
    /// <summary>PLI_RC_FILEBROWSER_CD.</summary>
    FileBrowserCd = 90,
    /// <summary>PLI_RC_FILEBROWSER_END.</summary>
    FileBrowserEnd = 91,
    /// <summary>PLI_RC_FILEBROWSER_DOWN.</summary>
    FileBrowserDown = 92,
    /// <summary>PLI_RC_FILEBROWSER_UP.</summary>
    FileBrowserUp = 93,
    /// <summary>PLI_NPCSERVERQUERY.</summary>
    NpcServerQuery = 94,
    /// <summary>PLI_RC_FILEBROWSER_MOVE.</summary>
    FileBrowserMove = 96,
    /// <summary>PLI_RC_FILEBROWSER_DELETE.</summary>
    FileBrowserDelete = 97,
    /// <summary>PLI_RC_FILEBROWSER_RENAME.</summary>
    FileBrowserRename = 98,
    /// <summary>PLI_NC_NPCGET: NPC attributes.</summary>
    NcNpcGet = 103,
    /// <summary>PLI_NC_NPCDELETE.</summary>
    NcNpcDelete = 104,
    /// <summary>PLI_NC_NPCRESET.</summary>
    NcNpcReset = 105,
    /// <summary>PLI_NC_NPCSCRIPTGET.</summary>
    NcNpcScriptGet = 106,
    /// <summary>PLI_NC_NPCWARP.</summary>
    NcNpcWarp = 107,
    /// <summary>PLI_NC_NPCFLAGSGET.</summary>
    NcNpcFlagsGet = 108,
    /// <summary>PLI_NC_NPCSCRIPTSET.</summary>
    NcNpcScriptSet = 109,
    /// <summary>PLI_NC_NPCFLAGSSET.</summary>
    NcNpcFlagsSet = 110,
    /// <summary>PLI_NC_NPCADD.</summary>
    NcNpcAdd = 111,
    /// <summary>PLI_NC_CLASSEDIT: requests a class script.</summary>
    NcClassEdit = 112,
    /// <summary>PLI_NC_CLASSADD: adds or updates a class.</summary>
    NcClassAdd = 113,
    /// <summary>PLI_NC_LOCALNPCSGET.</summary>
    NcLocalNpcsGet = 114,
    /// <summary>PLI_NC_WEAPONLISTGET.</summary>
    NcWeaponListGet = 115,
    /// <summary>PLI_NC_WEAPONGET.</summary>
    NcWeaponGet = 116,
    /// <summary>PLI_NC_WEAPONADD: adds or updates a weapon.</summary>
    NcWeaponAdd = 117,
    /// <summary>PLI_NC_WEAPONDELETE.</summary>
    NcWeaponDelete = 118,
    /// <summary>PLI_NC_CLASSDELETE.</summary>
    NcClassDelete = 119,
    /// <summary>PLI_NC_LEVELLISTGET.</summary>
    NcLevelListGet = 150,
    /// <summary>PLI_NC_LEVELLISTSET.</summary>
    NcLevelListSet = 151,
    /// <summary>PLI_REQUESTTEXT.</summary>
    RequestText = 152,
    /// <summary>PLI_SENDTEXT.</summary>
    SendText = 154,
    /// <summary>PLI_RC_LARGEFILESTART.</summary>
    LargeFileStart = 155,
    /// <summary>PLI_RC_LARGEFILEEND.</summary>
    LargeFileEnd = 156,
    /// <summary>PLI_RC_FOLDERDELETE.</summary>
    FolderDelete = 160,
}

/// <summary>Packets a server sends (RClib <c>PLO_*</c>). On the wire the id is sent as a GByte (id + 32).</summary>
public enum ServerPacketId
{
    /// <summary>PLO_LEVELBOARD.</summary>
    LevelBoard = 0,
    /// <summary>PLO_OTHERPLPROPS: properties of another player.</summary>
    OtherPlayerProps = 8,
    /// <summary>PLO_PLAYERPROPS.</summary>
    PlayerProps = 9,
    /// <summary>PLO_BOMBADD.</summary>
    BombAdd = 11,
    /// <summary>PLO_TOALL.</summary>
    ToAll = 13,
    /// <summary>PLO_DISCMESSAGE: the server closes the connection with this text.</summary>
    DisconnectMessage = 16,
    /// <summary>PLO_SIGNATURE: the login succeeded.</summary>
    Signature = 25,
    /// <summary>PLO_FILESENDFAILED.</summary>
    FileSendFailed = 30,
    /// <summary>PLO_RC_ADMINMESSAGE.</summary>
    AdminMessage = 35,
    /// <summary>PLO_PRIVATEMESSAGE.</summary>
    PrivateMessage = 37,
    /// <summary>PLO_NEWWORLDTIME.</summary>
    NewWorldTime = 42,
    /// <summary>PLO_FILEUPTODATE.</summary>
    FileUpToDate = 45,
    /// <summary>PLO_STAFFGUILDS.</summary>
    StaffGuilds = 47,
    /// <summary>PLO_RC_ACCOUNTADD (deprecated).</summary>
    AccountAdd = 50,
    /// <summary>PLO_RC_ACCOUNTSTATUS (deprecated).</summary>
    AccountStatus = 51,
    /// <summary>PLO_RC_ACCOUNTNAME (deprecated).</summary>
    AccountName = 52,
    /// <summary>PLO_RC_ACCOUNTDEL (deprecated).</summary>
    AccountDelete = 53,
    /// <summary>PLO_RC_ACCOUNTPROPS (deprecated).</summary>
    AccountProps = 54,
    /// <summary>PLO_ADDPLAYER.</summary>
    AddPlayer = 55,
    /// <summary>PLO_DELPLAYER.</summary>
    DeletePlayer = 56,
    /// <summary>PLO_RC_ACCOUNTPROPSGET (deprecated).</summary>
    AccountPropsGet = 57,
    /// <summary>PLO_RC_ACCOUNTCHANGE (deprecated).</summary>
    AccountChange = 58,
    /// <summary>PLO_RC_PLAYERPROPSCHANGE.</summary>
    PlayerPropsChange = 59,
    /// <summary>PLO_UNKNOWN60.</summary>
    Unknown60 = 60,
    /// <summary>PLO_RC_SERVERFLAGSGET.</summary>
    ServerFlags = 61,
    /// <summary>PLO_RC_PLAYERRIGHTSGET.</summary>
    PlayerRights = 62,
    /// <summary>PLO_RC_PLAYERCOMMENTSGET.</summary>
    PlayerComments = 63,
    /// <summary>PLO_RC_PLAYERBANGET.</summary>
    PlayerBan = 64,
    /// <summary>PLO_RC_FILEBROWSER_DIRLIST: the folders the RC may browse.</summary>
    FileBrowserDirList = 65,
    /// <summary>PLO_RC_FILEBROWSER_DIR: the files of one folder.</summary>
    FileBrowserDir = 66,
    /// <summary>PLO_RC_FILEBROWSER_MESSAGE.</summary>
    FileBrowserMessage = 67,
    /// <summary>PLO_LARGEFILESTART.</summary>
    LargeFileStart = 68,
    /// <summary>PLO_LARGEFILEEND.</summary>
    LargeFileEnd = 69,
    /// <summary>PLO_RC_ACCOUNTLISTGET.</summary>
    AccountList = 70,
    /// <summary>PLO_RC_PLAYERPROPS (deprecated).</summary>
    RcPlayerProps = 71,
    /// <summary>PLO_RC_PLAYERPROPSGET: a player's attributes.</summary>
    PlayerAttributes = 72,
    /// <summary>PLO_RC_ACCOUNTGET.</summary>
    Account = 73,
    /// <summary>PLO_RC_CHAT.</summary>
    RcChat = 74,
    /// <summary>PLO_PROFILE.</summary>
    Profile = 75,
    /// <summary>PLO_RC_SERVEROPTIONSGET.</summary>
    ServerOptions = 76,
    /// <summary>PLO_RC_FOLDERCONFIGGET.</summary>
    FolderConfig = 77,
    /// <summary>PLO_NC_CONTROL.</summary>
    NcControl = 78,
    /// <summary>PLO_NPCSERVERADDR: <c>"host,port"</c> of the NC server.</summary>
    NpcServerAddress = 79,
    /// <summary>PLO_NC_LEVELLIST.</summary>
    NcLevelList = 80,
    /// <summary>PLO_SERVERTEXT: the answer to REQUESTTEXT and SENDTEXT.</summary>
    ServerText = 82,
    /// <summary>PLO_LARGEFILESIZE.</summary>
    LargeFileSize = 84,
    /// <summary>PLO_RAWDATA: the next N bytes are one packet.</summary>
    RawData = 100,
    /// <summary>PLO_BOARDPACKET.</summary>
    BoardPacket = 101,
    /// <summary>PLO_FILE: a file or one piece of a large file.</summary>
    File = 102,
    /// <summary>PLO_RC_MAXUPLOADFILESIZE.</summary>
    MaxUploadFileSize = 103,
    /// <summary>PLO_NC_NPCATTRIBUTES.</summary>
    NcNpcAttributes = 157,
    /// <summary>PLO_NC_NPCADD.</summary>
    NcNpcAdd = 158,
    /// <summary>PLO_NC_NPCDELETE.</summary>
    NcNpcDelete = 159,
    /// <summary>PLO_NC_NPCSCRIPT.</summary>
    NcNpcScript = 160,
    /// <summary>PLO_NC_NPCFLAGS.</summary>
    NcNpcFlags = 161,
    /// <summary>PLO_NC_CLASSGET.</summary>
    NcClassGet = 162,
    /// <summary>PLO_NC_CLASSADD.</summary>
    NcClassAdd = 163,
    /// <summary>PLO_NC_LEVELDUMP.</summary>
    NcLevelDump = 164,
    /// <summary>PLO_NC_WEAPONLISTGET.</summary>
    NcWeaponList = 167,
    /// <summary>PLO_STATUSLIST.</summary>
    StatusList = 180,
    /// <summary>PLO_NC_CLASSDELETE.</summary>
    NcClassDelete = 188,
    /// <summary>PLO_SERVERLISTCONNECTED: the game server is connected to the list server.</summary>
    ServerListConnected = 190,
    /// <summary>PLO_NC_WEAPONGET.</summary>
    NcWeaponGet = 192,
    /// <summary>PLO_CLEARWEAPONS.</summary>
    ClearWeapons = 194,
}

/// <summary>Player property ids of the property stream (RClib <c>RC_PLPROP_*</c>).</summary>
public enum PlayerPropertyId
{
    /// <summary>The nickname.</summary>
    Nickname = 0,
    /// <summary>Full hearts.</summary>
    MaxPower = 1,
    /// <summary>Hearts (halves).</summary>
    CurrentPower = 2,
    /// <summary>Gralats.</summary>
    Rupees = 3,
    /// <summary>Arrows.</summary>
    Arrows = 4,
    /// <summary>Bombs.</summary>
    Bombs = 5,
    /// <summary>Glove power.</summary>
    GlovePower = 6,
    /// <summary>Bomb power.</summary>
    BombPower = 7,
    /// <summary>Sword power and image.</summary>
    SwordPower = 8,
    /// <summary>Shield power and image.</summary>
    ShieldPower = 9,
    /// <summary>The animation.</summary>
    Gani = 10,
    /// <summary>The head image.</summary>
    HeadImage = 11,
    /// <summary>The chat text above the player.</summary>
    CurrentChat = 12,
    /// <summary>The five colours.</summary>
    Colors = 13,
    /// <summary>The player id.</summary>
    Id = 14,
    /// <summary>X in tiles (halves).</summary>
    X = 15,
    /// <summary>Y in tiles (halves).</summary>
    Y = 16,
    /// <summary>Direction.</summary>
    Direction = 17,
    /// <summary>Status bits.</summary>
    Status = 18,
    /// <summary>Carried sprite.</summary>
    CarrySprite = 19,
    /// <summary>The current level.</summary>
    CurrentLevel = 20,
    /// <summary>The horse image.</summary>
    HorseImage = 21,
    /// <summary>Horse bushes.</summary>
    HorseBushes = 22,
    /// <summary>Effect colours.</summary>
    EffectColors = 23,
    /// <summary>Carried NPC.</summary>
    CarryNpc = 24,
    /// <summary>AP counter.</summary>
    ApCounter = 25,
    /// <summary>Magic points.</summary>
    MagicPoints = 26,
    /// <summary>Kills.</summary>
    Kills = 27,
    /// <summary>Deaths.</summary>
    Deaths = 28,
    /// <summary>Online seconds.</summary>
    OnlineSeconds = 29,
    /// <summary>The IP address.</summary>
    IpAddress = 30,
    /// <summary>UDP port.</summary>
    UdpPort = 31,
    /// <summary>Alignment (AP).</summary>
    Alignment = 32,
    /// <summary>Additional flags.</summary>
    AdditionalFlags = 33,
    /// <summary>The account name.</summary>
    AccountName = 34,
    /// <summary>The body image.</summary>
    BodyImage = 35,
    /// <summary>Rating and deviation.</summary>
    Rating = 36,
    /// <summary>The first of the gattrib properties (1 to 5 are 37 to 41).</summary>
    GAttrib1 = 37,
    /// <summary>Attached NPC.</summary>
    AttachNpc = 42,
    /// <summary>Gmap level X.</summary>
    GmapLevelX = 43,
    /// <summary>Gmap level Y.</summary>
    GmapLevelY = 44,
    /// <summary>Z.</summary>
    Z = 45,
    /// <summary>gattrib6 (6 to 9 are 46 to 49).</summary>
    GAttrib6 = 46,
    /// <summary>Join/leave level.</summary>
    JoinLeaveLevel = 50,
    /// <summary>Connected.</summary>
    Connected = 51,
    /// <summary>Language.</summary>
    Language = 52,
    /// <summary>The status-message (player-list icon) index.</summary>
    StatusMessage = 53,
    /// <summary>gattrib10 (10 to 30 are 54 to 74).</summary>
    GAttrib10 = 54,
    /// <summary>The operating system.</summary>
    OsType = 75,
    /// <summary>Text code page.</summary>
    TextCodePage = 76,
    /// <summary>Unknown 77 (online seconds 2).</summary>
    Unknown77 = 77,
    /// <summary>Precise X.</summary>
    X2 = 78,
    /// <summary>Precise Y.</summary>
    Y2 = 79,
    /// <summary>Precise Z.</summary>
    Z2 = 80,
    /// <summary>The player-list category: 1 external, 2 channel, 4 channel user.</summary>
    PlayerListCategory = 81,
    /// <summary>The community name.</summary>
    CommunityName = 82,
}
