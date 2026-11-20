using RemoteControl.Protocol.ListServer;
using RemoteControl.Protocol.Text;

namespace RemoteControl.Protocol.Session;

/// <summary>
/// A Remote Control session: the RC connection to one game server and its optional NC connection, with the caches the server's
/// packets fill. Every event is raised, and every cache changes, on the context the session was given (the UI thread in the
/// program); members may be called from any thread and never block it. A session is used once: after <see cref="Disconnected"/>
/// (or <see cref="IDisposable.Dispose"/>) it raises nothing more and every method returns <c>false</c> (or <c>null</c>) without
/// throwing. A method that sends returns <c>true</c> when its packets were written; a packet too large for a frame, or a text too
/// long for its field, is refused: the method returns <c>false</c> and <see cref="LastError"/> and <see cref="ProtocolError"/> say why.
/// </summary>
public interface IRcSession : IDisposable
{
    // ---- State -----------------------------------------------------------------------------------------------------------

    /// <summary>The account the session logs in with.</summary>
    string Account { get; }

    /// <summary>The server connected to (or being connected to).</summary>
    ServerInfo? Server { get; }

    /// <summary>Whether the RC socket is connected (RClib <c>rc_is_connected</c>).</summary>
    bool IsConnected { get; }

    /// <summary>Whether the server accepted the login (RClib <c>rc_is_authenticated</c>).</summary>
    bool IsAuthenticated { get; }

    /// <summary>Whether the NC socket is connected (RClib <c>rc_is_nc_connected</c>).</summary>
    bool IsNcConnected { get; }

    /// <summary>Whether the NC server answered the NC login (RClib <c>rc_is_nc_authenticated</c>).</summary>
    bool IsNcAuthenticated { get; }

    /// <summary>Whether the server told the address of its NC server (RClib <c>rc_has_nc_server</c>).</summary>
    bool HasNcServer { get; }

    /// <summary>The NC server's <c>"host,port"</c>, or <c>null</c>.</summary>
    string? NcServerAddress { get; }

    /// <summary>Whether ban requests use the lister texts (RClib <c>rc_is_new_protocol</c> / <c>rc_set_new_protocol</c>; on by default).</summary>
    bool UseNewBanProtocol { get; set; }

    /// <summary>The last error (a refused send, a failed connect, the disconnect reason) (RClib <c>rc_last_error</c>).</summary>
    string? LastError { get; }

    // ---- Caches ----------------------------------------------------------------------------------------------------------

    /// <summary>The players of the player list that are not channels (RClib <c>rc_get_players</c>; external players included, see <see cref="RcPlayer.IsExternal"/>).</summary>
    IReadOnlyList<RcPlayer> Players { get; }

    /// <summary>The channel entries of the player list (RClib <c>rc_get_channels</c>).</summary>
    IReadOnlyList<RcChannel> Channels { get; }

    /// <summary>The NC weapon list (RClib <c>rc_get_weapons</c>).</summary>
    IReadOnlyList<RcWeapon> Weapons { get; }

    /// <summary>The NC class list (RClib <c>rc_get_classes</c>).</summary>
    IReadOnlyList<RcClass> Classes { get; }

    /// <summary>The NC NPC list (RClib <c>rc_get_npcs</c>).</summary>
    IReadOnlyList<RcNpc> Npcs { get; }

    /// <summary>The NC level list, from the last <see cref="RequestLevelListAsync"/> (RClib <c>rc_get_levels</c>, which was always empty).</summary>
    IReadOnlyList<RcLevel> Levels { get; }

    /// <summary>The private-message servers (RClib <c>rc_get_pm_servers</c>).</summary>
    IReadOnlyList<string> PmServers { get; }

    /// <summary>The private-message guilds (RClib <c>rc_get_pm_guilds</c>).</summary>
    IReadOnlyList<string> PmGuilds { get; }

    /// <summary>The NPC flags received, by NPC id (RClib <c>rc_get_cached_npc_flags</c>).</summary>
    IReadOnlyDictionary<int, string> NpcFlags { get; }

    /// <summary>The server options last received (RClib <c>rc_get_server_options</c>).</summary>
    string ServerOptions { get; }

    /// <summary>The server flags last received, one per line with a trailing newline (RClib <c>rc_get_server_flags</c>).</summary>
    string ServerFlags { get; }

    /// <summary>The folder configuration last received (RClib <c>rc_get_folder_config</c>).</summary>
    string FolderConfig { get; }

    /// <summary>The upload limit the server announced, in bytes, or 0 (RClib <c>rc_get_max_upload_file_size</c>).</summary>
    long MaxUploadFileSize { get; }

    /// <summary>The list server's simple server list, from the last <see cref="RequestServerListAsync"/>.</summary>
    IReadOnlyList<SimpleServerEntry> GameServers { get; }

    /// <summary>The windows' view of the file browser.</summary>
    IRcFileBrowser FileBrowser { get; }

    /// <summary>The folder synchroniser's view of the file browser (RClib's <c>rc_sync_*</c> functions).</summary>
    IRcFileBrowser SyncFileBrowser { get; }

    /// <summary>The server backup's view of the file browser: its listings and downloads never reach the windows or the synchroniser.</summary>
    IRcFileBrowser BackupFileBrowser { get; }

    // ---- Connection events -------------------------------------------------------------------------------------------------

    /// <summary>The server accepted the login (RClib <c>on_connected</c>).</summary>
    event EventHandler? Connected;

    /// <summary>The session ended; it is the last event the session raises (RClib <c>on_disconnected</c>, <c>on_disconnected_ex</c>).</summary>
    event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <summary>The NC server answered the NC login.</summary>
    event EventHandler? NcConnected;

    /// <summary>The NC connection ended (RClib reported it as the <c>nc_message</c> "[NC] DISCONNECT: …"); the RC connection stays.</summary>
    event EventHandler<DisconnectedEventArgs>? NcDisconnected;

    /// <summary>A send was refused or a packet could not be read; the text says what.</summary>
    event EventHandler<TextEventArgs>? ProtocolError;

    /// <summary>Any packet arrived, on either connection (RClib <c>on_raw_packet</c>, which reported only some).</summary>
    event EventHandler<RawPacketEventArgs>? RawPacketReceived;

    // ---- Chat and messages -----------------------------------------------------------------------------------------------

    /// <summary>An RC chat line (RClib <c>on_message</c>; <c>" of +"</c> is shown as <c>" of "</c>).</summary>
    event EventHandler<TextEventArgs>? RcChatReceived;

    /// <summary>A chat line of the NC connection (RClib <c>on_server_data("nc_message")</c>).</summary>
    event EventHandler<TextEventArgs>? NcChatReceived;

    /// <summary>A private message (RClib <c>on_private_message</c>, <c>on_private_message_ex</c>).</summary>
    event EventHandler<PrivateMessageEventArgs>? PrivateMessageReceived;

    /// <summary>An admin message (RClib <c>on_server_data("admin_message")</c>; 0xA7 is a line break).</summary>
    event EventHandler<TextEventArgs>? AdminMessageReceived;

    /// <summary>A to-all message, <c>"nick: text"</c> (RClib <c>on_server_data("toall")</c>).</summary>
    event EventHandler<TextEventArgs>? ToAllReceived;

    /// <summary>A line for an IRC channel (RClib <c>on_irc_message</c>).</summary>
    event EventHandler<IrcMessageEventArgs>? IrcMessageReceived;

    /// <summary>A server text nothing else handled (RClib <c>on_server_data("server_text")</c>).</summary>
    event EventHandler<TextEventArgs>? ServerTextReceived;

    /// <summary>
    /// The server's answer to <see cref="RequestMissingFilesAsync"/>: raised once, on the session's context, when the last part has
    /// arrived (the list) or when the server refused (<see cref="MissingFilesEventArgs.Error"/>).
    /// </summary>
    event EventHandler<MissingFilesEventArgs>? MissingFilesReceived;

    /// <summary>Other server data RClib reported through <c>on_server_data</c> (bomb add, board packet, status list, staff guilds, deprecated account packets ...).</summary>
    event EventHandler<ServerDataEventArgs>? ServerDataReceived;

    /// <summary>The world time (RClib <c>on_world_time</c>; Alpaxis does not send it to RCs).</summary>
    event EventHandler<ValueEventArgs<int>>? WorldTimeReceived;

    // ---- Players ---------------------------------------------------------------------------------------------------------

    /// <summary>A player joined the list (RClib <c>on_player_joined</c>; channels raise <see cref="ChannelsChanged"/> instead).</summary>
    event EventHandler<PlayerEventArgs>? PlayerJoined;

    /// <summary>A known player was sent again (level change, nickname).</summary>
    event EventHandler<PlayerEventArgs>? PlayerUpdated;

    /// <summary>A player left the list (RClib <c>on_player_left</c>).</summary>
    event EventHandler<PlayerEventArgs>? PlayerLeft;

    /// <summary>The channel list changed.</summary>
    event EventHandler? ChannelsChanged;

    /// <summary>One property of a player changed (RClib <c>on_player_prop_changed</c>).</summary>
    event EventHandler<PlayerPropertyChangedEventArgs>? PlayerPropertyChanged;

    /// <summary>A player's raw property stream arrived (RClib <c>on_player_properties_changed</c>).</summary>
    event EventHandler<PlayerPropertiesChangedEventArgs>? PlayerPropertiesChanged;

    /// <summary>A player's attributes arrived (RClib <c>on_player_attributes</c>).</summary>
    event EventHandler<PlayerAttributesEventArgs>? PlayerAttributesReceived;

    /// <summary>A player's rights arrived (RClib <c>on_player_rights</c>).</summary>
    event EventHandler<PlayerRightsEventArgs>? PlayerRightsReceived;

    /// <summary>A player's comments arrived (RClib <c>on_player_text_data("comments")</c>).</summary>
    event EventHandler<AccountTextEventArgs>? PlayerCommentsReceived;

    /// <summary>An account arrived (RClib <c>on_player_text_data("account")</c>).</summary>
    event EventHandler<AccountDataEventArgs>? AccountReceived;

    /// <summary>A profile arrived (RClib <c>on_player_text_data("profile")</c>).</summary>
    event EventHandler<ProfileEventArgs>? ProfileReceived;

    /// <summary>The account list arrived (RClib <c>on_account_list</c>).</summary>
    event EventHandler<AccountListEventArgs>? AccountListReceived;

    /// <summary>Ban data arrived (RClib <c>on_ban_data</c>).</summary>
    event EventHandler<BanDataEventArgs>? BanDataReceived;

    /// <summary>Ban types, ban history or staff activity arrived (RClib <c>on_ban_list_data</c>).</summary>
    event EventHandler<BanListEventArgs>? BanListReceived;

    // ---- Private-message servers -----------------------------------------------------------------------------------------

    /// <summary>The PM server list changed (RClib <c>on_pm_servers_updated</c>).</summary>
    event EventHandler<ValueEventArgs<int>>? PmServersUpdated;

    /// <summary>The PM guild list changed (RClib <c>on_pm_guilds_updated</c>).</summary>
    event EventHandler<ValueEventArgs<int>>? PmGuildsUpdated;

    /// <summary>The players of a PM server arrived as a server text (RClib <c>on_pm_server_players</c>; Alpaxis adds them to the player list instead).</summary>
    event EventHandler<PmServerPlayersEventArgs>? PmServerPlayersReceived;

    /// <summary>The simple server list arrived (the answer to <see cref="RequestServerListAsync"/>).</summary>
    event EventHandler? GameServersUpdated;

    // ---- Server settings -------------------------------------------------------------------------------------------------

    /// <summary>The server options arrived; both the editor and the synchroniser may listen (RClib <c>"options"</c> to both callbacks).</summary>
    event EventHandler<TextEventArgs>? ServerOptionsReceived;

    /// <summary>The server flags arrived (RClib <c>"flags"</c>).</summary>
    event EventHandler<TextEventArgs>? ServerFlagsReceived;

    /// <summary>The folder configuration arrived (RClib <c>"folder_config"</c>).</summary>
    event EventHandler<TextEventArgs>? FolderConfigReceived;

    /// <summary>The upload limit arrived (RClib <c>on_max_upload_file_size</c>).</summary>
    event EventHandler<ValueEventArgs<long>>? MaxUploadFileSizeReceived;

    /// <summary>A file-browser message (RClib <c>on_filebrowser_message</c>, also for <c>PLO_FILESENDFAILED</c>).</summary>
    event EventHandler<TextEventArgs>? FileBrowserMessageReceived;

    // ---- NC ----------------------------------------------------------------------------------------------------------------

    /// <summary>The weapon list arrived (RClib <c>on_weapon_list_received</c>).</summary>
    event EventHandler<ValueEventArgs<int>>? WeaponListReceived;

    /// <summary>A class was added (RClib <c>on_class_added</c>).</summary>
    event EventHandler<ValueEventArgs<string>>? ClassAdded;

    /// <summary>A class was deleted (RClib <c>on_class_deleted</c>).</summary>
    event EventHandler<ValueEventArgs<string>>? ClassDeleted;

    /// <summary>An NPC was added (RClib <c>on_npc_added</c>).</summary>
    event EventHandler<NpcEventArgs>? NpcAdded;

    /// <summary>An NPC was deleted (RClib <c>on_npc_deleted</c>).</summary>
    event EventHandler<NpcEventArgs>? NpcDeleted;

    /// <summary>NPC attributes arrived (RClib <c>on_npc_attributes</c>).</summary>
    event EventHandler<NpcTextEventArgs>? NpcAttributesReceived;

    /// <summary>NPC flags arrived (RClib <c>on_npc_flags</c>).</summary>
    event EventHandler<NpcTextEventArgs>? NpcFlagsReceived;

    /// <summary>A weapon, class or NPC script arrived (RClib <c>on_script_received</c>).</summary>
    event EventHandler<ScriptReceivedEventArgs>? ScriptReceived;

    /// <summary>A level's local NPCs arrived (RClib <c>on_local_npcs</c>).</summary>
    event EventHandler<LocalNpcsEventArgs>? LocalNpcsReceived;

    /// <summary>The NC level list arrived (RClib <c>on_server_data("nc_levellist")</c>).</summary>
    event EventHandler<TextEventArgs>? LevelListReceived;

    // ---- Connecting --------------------------------------------------------------------------------------------------------

    /// <summary>Connects to a server of the list and logs in (RClib <c>rc_connect_to_server</c>); completes when the server accepted or refused the login.</summary>
    /// <param name="server">The server.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>The result.</returns>
    Task<ConnectResult> ConnectAsync(ServerInfo server, CancellationToken cancellationToken = default);

    /// <summary>Connects to a game server by host and port (RClib <c>rc_connect_to_ip</c>).</summary>
    /// <param name="host">The host.</param>
    /// <param name="port">The port.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>The result.</returns>
    Task<ConnectResult> ConnectAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>Connects the NC (RClib <c>rc_connect_to_nc_server</c>, which blocked the UI thread); completes when the NC server answered.</summary>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>Whether the NC is connected and answered.</returns>
    Task<bool> ConnectNcAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the NC connection (RClib <c>rc_disconnect_nc</c>).</summary>
    /// <returns>Whether anything was open.</returns>
    bool DisconnectNc();

    /// <summary>Closes the session (RClib <c>rc_disconnect</c>); <see cref="Disconnected"/> is raised once with <see cref="DisconnectedEventArgs.Requested"/> set.</summary>
    void Disconnect();

    // ---- RC chat and messages ----------------------------------------------------------------------------------------------

    /// <summary>Sends an RC chat line or command; each line of a multi-line text is its own packet (RClib <c>rc_execute</c>).</summary>
    /// <param name="text">The text.</param>
    /// <param name="alsoOnNc">Also send it on the NC connection, as RClib always did when the NC was connected.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SendRcChatAsync(string text, bool alsoOnNc = false);

    /// <summary>Sends a chat line on the NC connection.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SendNcChatAsync(string text);

    /// <summary>Sends a message to everyone (RClib <c>rc_send_toall_message</c>; at most 223 bytes).</summary>
    /// <param name="message">The message.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SendToAllAsync(string message);

    /// <summary>Sends a private message (RClib <c>rc_send_private_message</c>).</summary>
    /// <param name="playerId">The recipient.</param>
    /// <param name="message">The message (line breaks are kept).</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SendPrivateMessageAsync(int playerId, string message);

    /// <summary>Sends one private message to several players (RClib <c>rc_send_mass_pm</c>).</summary>
    /// <param name="playerIds">The recipients (at most 0x6FFF).</param>
    /// <param name="message">The message.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SendMassPrivateMessageAsync(IReadOnlyList<int> playerIds, string message);

    /// <summary>Sends an admin message to one player (RClib <c>rc_send_admin_message</c>; line breaks are sent as 0xA7).</summary>
    /// <param name="playerId">The player.</param>
    /// <param name="message">The message.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SendAdminMessageAsync(int playerId, string message);

    /// <summary>Sends an admin message to everyone (RClib <c>rc_send_admin_message_all</c>).</summary>
    /// <param name="message">The message.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SendAdminMessageToAllAsync(string message);

    /// <summary>Sets the RC's nickname (RClib <c>rc_set_nickname</c>).</summary>
    /// <param name="nickname">The nickname.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SetNicknameAsync(string nickname);

    // ---- Players -----------------------------------------------------------------------------------------------------------

    /// <summary>Warps a player (RClib <c>rc_warp_player</c>).</summary>
    /// <param name="playerId">The player.</param>
    /// <param name="level">The level.</param>
    /// <param name="x">X in tiles.</param>
    /// <param name="y">Y in tiles.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> WarpPlayerAsync(int playerId, string level, double x, double y);

    /// <summary>Disconnects a player (RClib <c>rc_disconnect_player</c>).</summary>
    /// <param name="playerId">The player.</param>
    /// <param name="reason">The reason shown to the player.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> DisconnectPlayerAsync(int playerId, string reason);

    /// <summary>Requests a player's rights (RClib <c>rc_request_player_rights</c>).</summary>
    /// <param name="account">The account.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestPlayerRightsAsync(string account);

    /// <summary>Sets a player's rights (RClib <c>rc_set_player_rights</c>).</summary>
    /// <param name="account">The account.</param>
    /// <param name="rights">The rights.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SetPlayerRightsAsync(string account, PlayerRights rights);

    /// <summary>Requests a player's attributes by account (RClib <c>rc_request_player_attrs</c>).</summary>
    /// <param name="account">The account.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestPlayerAttributesAsync(string account);

    /// <summary>Requests a player's attributes by player id (RClib <c>rc_request_player_attrs_by_id</c>).</summary>
    /// <param name="playerId">The player.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestPlayerAttributesAsync(int playerId);

    /// <summary>Sets a player's attributes (RClib <c>rc_set_player_attributes</c>).</summary>
    /// <param name="account">The account.</param>
    /// <param name="attributes">The attributes; <c>null</c> properties are not sent.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SetPlayerAttributesAsync(string account, PlayerAttributes attributes);

    /// <summary>Resets a player's attributes (RClib <c>rc_reset_player</c>).</summary>
    /// <param name="account">The account.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> ResetPlayerAsync(string account);

    /// <summary>Requests an account (RClib <c>rc_request_player_account</c>).</summary>
    /// <param name="account">The account.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestAccountAsync(string account);

    /// <summary>Changes an account (RClib <c>rc_set_player_account</c>).</summary>
    /// <param name="account">The account to change.</param>
    /// <param name="data">The new values (its <see cref="AccountData.Account"/> wins when set).</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SetAccountAsync(string account, AccountData data);

    /// <summary>Creates an account (RClib <c>rc_add_player_account</c>).</summary>
    /// <param name="data">The account.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> AddAccountAsync(AccountData data);

    /// <summary>Requests the account list (RClib <c>rc_request_account_list</c>).</summary>
    /// <param name="accountFilter">The account wildcard.</param>
    /// <param name="conditions">The conditions.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestAccountListAsync(string accountFilter, string conditions);

    /// <summary>Requests a player's comments (RClib <c>rc_request_player_comments</c>).</summary>
    /// <param name="account">The account.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestPlayerCommentsAsync(string account);

    /// <summary>Sets a player's comments (RClib <c>rc_set_player_comments</c>).</summary>
    /// <param name="account">The account.</param>
    /// <param name="comments">The comments.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SetPlayerCommentsAsync(string account, string comments);

    /// <summary>Requests a profile (RClib <c>rc_request_player_profile</c>).</summary>
    /// <param name="account">The account.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestProfileAsync(string account);

    /// <summary>Sets a profile (RClib <c>rc_set_player_profile</c>).</summary>
    /// <param name="account">The account.</param>
    /// <param name="fields">Fields 1 to 10 (see <see cref="ProfileText"/>).</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SetProfileAsync(string account, IReadOnlyList<string> fields);

    // ---- Bans --------------------------------------------------------------------------------------------------------------

    /// <summary>Requests ban data with the lister texts or the legacy packet, as <see cref="UseNewBanProtocol"/> says (RClib <c>rc_request_player_ban</c>).</summary>
    /// <param name="account">The account.</param>
    /// <param name="playerId">The player id, or -1.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestPlayerBanAsync(string account, int playerId = -1);

    /// <summary>Requests ban data with the lister text <c>getbanbyid</c> (id ≥ 0) or <c>getban</c> (RClib <c>rc_request_new_player_ban</c>).</summary>
    /// <param name="account">The account.</param>
    /// <param name="playerId">The player id, or -1.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestNewPlayerBanAsync(string account, int playerId = -1);

    /// <summary>Requests ban data with PLI_RC_PLAYERBANGET (RClib <c>rc_request_legacy_player_ban</c>).</summary>
    /// <param name="account">The account.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestLegacyPlayerBanAsync(string account);

    /// <summary>Requests ban data by account with the lister text (RClib <c>rc_request_player_ban_by_account</c>).</summary>
    /// <param name="account">The account.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestPlayerBanByAccountAsync(string account);

    /// <summary>Requests the ban types (RClib <c>rc_request_ban_types</c>).</summary>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestBanTypesAsync();

    /// <summary>
    /// Asks the game server for the files its NPCs, weapons, classes and animations name and it does not have (the search of the chat
    /// command <c>/findmissing</c>, with the same right), each with the folder it belongs in. The answer arrives as one
    /// <see cref="MissingFilesReceived"/>; a server that does not know the request (an older one) never answers it.
    /// </summary>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestMissingFilesAsync();

    /// <summary>Requests an account's ban history (RClib <c>rc_request_ban_history</c>).</summary>
    /// <param name="account">The account.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestBanHistoryAsync(string account);

    /// <summary>Requests a staff member's activity (RClib <c>rc_request_staff_activity</c>).</summary>
    /// <param name="account">The account.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestStaffActivityAsync(string account);

    /// <summary>Sets a ban with the lister text <c>setban</c> (RClib <c>rc_set_ban</c>; fields are now comma-escaped).</summary>
    /// <param name="target">The account or computer id.</param>
    /// <param name="world">The world.</param>
    /// <param name="banned">Ban or unban.</param>
    /// <param name="banType">The ban type.</param>
    /// <param name="releaseTime">
    /// <c>releasetime</c>: the ban's length in seconds from now, as Alpaxis reads it (0 is a ban without an end, 100000000 or more
    /// too), or empty to leave it out: Alpaxis then takes the length of the ban's type, and bans nothing when it does not know the
    /// type (before 2026-11-19 a missing value was a ban without an end).
    /// </param>
    /// <param name="reason">The reason.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SetBanAsync(string target, string world, bool banned, string banType, string releaseTime, string reason);

    /// <summary>Sets a ban with PLI_RC_PLAYERBANSET (RClib <c>rc_set_legacy_player_ban</c>).</summary>
    /// <param name="account">The account.</param>
    /// <param name="banned">Ban or unban.</param>
    /// <param name="reason">The reason.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SetLegacyPlayerBanAsync(string account, bool banned, string reason);

    // ---- Server settings ---------------------------------------------------------------------------------------------------

    /// <summary>Requests the server options (RClib <c>rc_request_server_options</c>).</summary>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestServerOptionsAsync();

    /// <summary>Uploads the server options (RClib <c>rc_upload_server_options</c>).</summary>
    /// <param name="content">The options text.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> UploadServerOptionsAsync(string content);

    /// <summary>Requests the server flags (RClib <c>rc_request_server_flags</c>).</summary>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestServerFlagsAsync();

    /// <summary>Uploads the server flags; blank lines are skipped (RClib <c>rc_upload_server_flags</c>).</summary>
    /// <param name="content">One flag per line.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> UploadServerFlagsAsync(string content);

    /// <summary>Requests the folder configuration (RClib <c>rc_request_folder_config</c>).</summary>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestFolderConfigAsync();

    /// <summary>Uploads the folder configuration (RClib <c>rc_upload_folder_config</c>).</summary>
    /// <param name="content">The text.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> UploadFolderConfigAsync(string content);

    /// <summary>Asks the server to reload a level (RClib <c>rc_update_level</c>).</summary>
    /// <param name="level">The level.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> UpdateLevelAsync(string level);

    /// <summary>Asks the server to reload levels (RClib <c>rc_update_levels</c>).</summary>
    /// <param name="levels">The levels (at most 0x6FFF).</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> UpdateLevelsAsync(IReadOnlyList<string> levels);

    /// <summary>Uploads a level into the folder of its path (RClib <c>rc_upload_level</c>).</summary>
    /// <param name="levelPath"><c>folder/name</c> or a bare name.</param>
    /// <param name="content">The bytes.</param>
    /// <returns>Whether it was written.</returns>
    Task<bool> UploadLevelAsync(string levelPath, ReadOnlyMemory<byte> content);

    /// <summary>Downloads a level through the windows' file-browser view (RClib <c>rc_download_level</c>, which sent the request without waiting for the file).</summary>
    /// <param name="levelPath"><c>folder/name</c> or a bare name.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The bytes, or <c>null</c>.</returns>
    Task<byte[]?> DownloadLevelAsync(string levelPath, CancellationToken cancellationToken = default);

    // ---- Private-message servers, IRC, server list ---------------------------------------------------------------------------

    /// <summary>Requests the simple server list from the list server through the game server (RClib declared <c>rc_request_server_list</c> but never wrote it).</summary>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestServerListAsync();

    /// <summary>Requests the PM server list (RClib <c>rc_request_pm_server_list</c>).</summary>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestPmServerListAsync();

    /// <summary>Requests the PM guild list (RClib <c>rc_request_pm_guild_list</c>).</summary>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestPmGuildListAsync();

    /// <summary>Asks for the players of a PM server (RClib <c>rc_request_pm_server_players</c>).</summary>
    /// <param name="server">The server.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestPmServerPlayersAsync(string server);

    /// <summary>Stops listing a PM server's players (RClib <c>rc_unmap_pm_server</c>).</summary>
    /// <param name="server">The server.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> UnmapPmServerAsync(string server);

    /// <summary>Sends an IRC command (RClib <c>rc_send_irc_text</c>).</summary>
    /// <param name="command">The command (<c>login</c>, <c>join</c>, <c>part</c>, <c>privmsg</c> ...).</param>
    /// <param name="parameters">Up to three parameters.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SendIrcTextAsync(string command, params string[] parameters);

    /// <summary>Logs in to IRC (RClib <c>rc_irc_login</c>).</summary>
    /// <returns>Whether it was sent.</returns>
    Task<bool> IrcLoginAsync();

    /// <summary>Joins an IRC channel (RClib <c>rc_irc_join</c>).</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> IrcJoinAsync(string channel);

    /// <summary>Leaves an IRC channel (RClib <c>rc_irc_part</c>).</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> IrcPartAsync(string channel);

    // ---- NC: weapons, classes, NPCs, levels ---------------------------------------------------------------------------------

    /// <summary>Requests the weapon list (RClib <c>rc_request_weapon_list</c>).</summary>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestWeaponListAsync();

    /// <summary>Requests a weapon's script (RClib <c>rc_request_weapon_script</c>).</summary>
    /// <param name="name">The weapon.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestWeaponScriptAsync(string name);

    /// <summary>Adds a weapon (RClib <c>rc_add_weapon</c>; line breaks are sent as 0xA7).</summary>
    /// <param name="name">The weapon.</param>
    /// <param name="image">The image.</param>
    /// <param name="script">The script.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> AddWeaponAsync(string name, string image, string script);

    /// <summary>Saves a weapon (RClib <c>rc_update_weapon</c>; the same packet as <see cref="AddWeaponAsync"/>).</summary>
    /// <param name="name">The weapon.</param>
    /// <param name="image">The image.</param>
    /// <param name="script">The script.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> UpdateWeaponAsync(string name, string image, string script);

    /// <summary>Deletes a weapon (RClib <c>rc_delete_weapon</c>).</summary>
    /// <param name="name">The weapon.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> DeleteWeaponAsync(string name);

    /// <summary>Requests a class's script (RClib <c>rc_request_class_script</c>).</summary>
    /// <param name="name">The class.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestClassScriptAsync(string name);

    /// <summary>Adds a class (RClib <c>rc_add_class</c>).</summary>
    /// <param name="name">The class.</param>
    /// <param name="script">The script.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> AddClassAsync(string name, string script);

    /// <summary>Saves a class (RClib <c>rc_update_class</c>; the same packet as <see cref="AddClassAsync"/>).</summary>
    /// <param name="name">The class.</param>
    /// <param name="script">The script.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> UpdateClassAsync(string name, string script);

    /// <summary>Deletes a class (RClib <c>rc_delete_class</c>).</summary>
    /// <param name="name">The class.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> DeleteClassAsync(string name);

    /// <summary>Requests an NPC's script (RClib <c>rc_request_npc_script</c>).</summary>
    /// <param name="npcId">The NPC.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestNpcScriptAsync(int npcId);

    /// <summary>Saves an NPC's script (RClib <c>rc_update_npc</c>).</summary>
    /// <param name="npcId">The NPC.</param>
    /// <param name="script">The script.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> UpdateNpcScriptAsync(int npcId, string script);

    /// <summary>Requests an NPC's attributes (RClib <c>rc_request_npc_attributes</c>).</summary>
    /// <param name="npcId">The NPC.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestNpcAttributesAsync(int npcId);

    /// <summary>Requests an NPC's flags (RClib <c>rc_get_npc_flags</c>, which only sent the request).</summary>
    /// <param name="npcId">The NPC.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestNpcFlagsAsync(int npcId);

    /// <summary>Sets an NPC's flags (RClib <c>rc_set_npc_flags</c>).</summary>
    /// <param name="npcId">The NPC.</param>
    /// <param name="flags">One flag per line.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SetNpcFlagsAsync(int npcId, string flags);

    /// <summary>Resets an NPC (RClib <c>rc_reset_npc</c>).</summary>
    /// <param name="npcId">The NPC.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> ResetNpcAsync(int npcId);

    /// <summary>Warps an NPC (RClib <c>rc_warp_npc</c>).</summary>
    /// <param name="npcId">The NPC.</param>
    /// <param name="x">X in tiles.</param>
    /// <param name="y">Y in tiles.</param>
    /// <param name="level">The level.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> WarpNpcAsync(int npcId, double x, double y, string level);

    /// <summary>Deletes an NPC (RClib <c>rc_delete_npc</c>).</summary>
    /// <param name="npcId">The NPC.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> DeleteNpcAsync(int npcId);

    /// <summary>Creates a database NPC (RClib <c>rc_create_npc_on_server</c>).</summary>
    /// <returns>Whether it was sent.</returns>
    Task<bool> CreateNpcAsync(string name, int npcId, string type, string scripter, string level, string x, string y);

    /// <summary>Requests a level's local NPCs (RClib <c>rc_request_local_npcs</c>).</summary>
    /// <param name="level">The level.</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestLocalNpcsAsync(string level);

    /// <summary>Requests the NC level list (RClib sent it only through <c>rc_send_nc_packet</c>).</summary>
    /// <returns>Whether it was sent.</returns>
    Task<bool> RequestLevelListAsync();

    // ---- Raw ---------------------------------------------------------------------------------------------------------------

    /// <summary>Sends any packet on the RC connection (RClib <c>rc_send_raw_packet</c>).</summary>
    /// <param name="packetId">The id.</param>
    /// <param name="payload">The bytes after the id (no newline).</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SendRawPacketAsync(int packetId, ReadOnlyMemory<byte> payload);

    /// <summary>Sends any packet on the NC connection (RClib <c>rc_send_nc_packet</c>).</summary>
    /// <param name="packetId">The id.</param>
    /// <param name="payload">The bytes after the id (no newline).</param>
    /// <returns>Whether it was sent.</returns>
    Task<bool> SendNcPacketAsync(int packetId, ReadOnlyMemory<byte> payload);
}
