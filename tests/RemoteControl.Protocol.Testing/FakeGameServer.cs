using System.Collections.Concurrent;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Testing;

/// <summary>What a fake game server received as an RC login.</summary>
/// <param name="Key">The Gen-5 key the client announced.</param>
/// <param name="Version">The version string (<c>"GSERV025"</c>).</param>
/// <param name="Account">The account.</param>
/// <param name="Password">The password.</param>
/// <param name="PcIds">The PC-id list.</param>
public sealed record RcLogin(int Key, string Version, string Account, string Password, string PcIds);

/// <summary>
/// A game server on 127.0.0.1 that speaks the RC side of the protocol as the Alpaxis server does. By default it accepts every login
/// with PLO_SIGNATURE, answers the NPC-server query with <see cref="NcAddress"/>, and emulates the file browser over
/// <see cref="Files"/> (start, change of folder, download small and large files, upload, delete, rename). Anything else is recorded
/// and can be answered by the test with the <c>Send</c> helpers.
/// </summary>
public sealed class FakeGameServer : LoopbackServer
{
    /// <summary>The piece size of large downloads (as Alpaxis: 32000 bytes).</summary>
    public const int FileChunkSize = 32000;

    private readonly List<RcLogin> _logins = new();

    /// <summary>When set, a login is refused with this disconnect message.</summary>
    public string? RejectLoginWith { get; set; }

    /// <summary>Whether to answer a login with PLO_SIGNATURE.</summary>
    public bool AcceptLogin { get; set; } = true;

    /// <summary>Called on the connection's reader task after a login was accepted, to send the welcome packets of a test.</summary>
    public Func<FakeConnection, Task>? OnLoggedIn { get; set; }

    /// <summary>The <c>"host,port"</c> sent in answer to PLI_NPCSERVERQUERY, or <c>null</c> to not answer.</summary>
    public string? NcAddress { get; set; }

    /// <summary>Whether to emulate the file browser.</summary>
    public bool EmulateFileBrowser { get; set; } = true;

    /// <summary>The folder rights rows of the file browser (<c>"rw levels/*"</c>).</summary>
    public List<string> FolderRights { get; } = new() { "rw levels/*", "r config/*" };

    /// <summary>The files of the file browser, by <c>folder/name</c>.</summary>
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>The modification time sent for every file.</summary>
    public long FileModified { get; set; } = 1_700_000_000;

    /// <summary>The logins received.</summary>
    public IReadOnlyList<RcLogin> Logins
    {
        get
        {
            lock (_logins) return _logins.ToArray();
        }
    }

    internal override IFrameCodec CreateCodec() => new ServerGen5Codec();

    /// <inheritdoc />
    protected override void OnPacket(FakeConnection connection, ReceivedPacket packet)
    {
        var codec = (ServerGen5Codec)connection.Codec;
        if (!codec.HasKey)
        {
            if (packet.Id == (int)ClientPacketId.ToAll) HandleLogin(connection, codec, packet);
            return;
        }

        switch ((ClientPacketId)packet.Id)
        {
            case ClientPacketId.NpcServerQuery when NcAddress is not null:
                _ = connection.SendAsync(ServerPacketId.NpcServerAddress, new PacketWriter().WriteGShort(2).WriteText(NcAddress));
                break;
            case ClientPacketId.FileBrowserStart when EmulateFileBrowser:
                _ = SendFolderListAsync(connection);
                break;
            case ClientPacketId.FileBrowserCd when EmulateFileBrowser:
            {
                string folder = packet.Text.Trim();
                if (!FolderRights.Any(row => FolderOf(row) == folder)) break;
                connection.State["folder"] = folder;
                _ = SendFolderChangedAsync(connection, folder);
                break;
            }
            case ClientPacketId.FileBrowserDown when EmulateFileBrowser:
                _ = SendFileAsync(connection, CurrentFolder(connection) + packet.Text, packet.Text);
                break;
            case ClientPacketId.FileBrowserDelete when EmulateFileBrowser:
                Files.TryRemove(CurrentFolder(connection) + packet.Text, out _);
                _ = connection.SendAsync(ServerPacketId.FileBrowserMessage, RcText.GetBytes(CommaText.Tokenize("Deleted file " + packet.Text)));
                break;
            case ClientPacketId.FileBrowserRename when EmulateFileBrowser:
            {
                var reader = packet.Reader();
                reader.TryReadGString(out string from);
                reader.TryReadGString(out string to);
                string folder = CurrentFolder(connection);
                if (Files.TryRemove(folder + from, out byte[]? content)) Files[folder + to] = content;
                break;
            }
            case ClientPacketId.FileBrowserUp when EmulateFileBrowser:
                HandleUpload(connection, packet);
                break;
            case ClientPacketId.LargeFileStart when EmulateFileBrowser:
                connection.State["large:" + packet.Text] = new MemoryStream();
                break;
            case ClientPacketId.LargeFileEnd when EmulateFileBrowser:
                if (connection.State.TryRemove("large:" + packet.Text, out object? buffer))
                {
                    Files[CurrentFolder(connection) + packet.Text] = ((MemoryStream)buffer).ToArray();
                    _ = connection.SendAsync(ServerPacketId.FileBrowserMessage, RcText.GetBytes(CommaText.Tokenize("Uploaded large file " + packet.Text)));
                }

                break;
        }
    }

    private void HandleLogin(FakeConnection connection, ServerGen5Codec codec, ReceivedPacket packet)
    {
        var reader = packet.Reader();
        reader.TryReadGByte(out int key);
        reader.TryReadText(8, out string version);
        reader.TryReadGString(out string account);
        reader.TryReadGString(out string password);
        var login = new RcLogin(key, version, account, password, reader.ReadRemainingText());
        lock (_logins) _logins.Add(login);
        codec.SetKey((byte)key);
        connection.State["folder"] = "levels/";
        _ = AnswerLoginAsync(connection);
    }

    private async Task AnswerLoginAsync(FakeConnection connection)
    {
        if (RejectLoginWith is not null)
        {
            await connection.SendTextAsync(ServerPacketId.DisconnectMessage, RejectLoginWith).ConfigureAwait(false);
            connection.Close();
            return;
        }

        if (!AcceptLogin) return;
        await connection.SendAsync(ServerPacketId.Signature, new byte[] { 0x69 }).ConfigureAwait(false);
        if (OnLoggedIn is not null) await OnLoggedIn(connection).ConfigureAwait(false);
    }

    private static string FolderOf(string row)
    {
        int space = row.IndexOf(' ', StringComparison.Ordinal);
        string pattern = space < 0 ? row : row[(space + 1)..];
        int slash = pattern.LastIndexOf('/');
        return slash < 0 ? string.Empty : pattern[..(slash + 1)];
    }

    private static string CurrentFolder(FakeConnection connection) => connection.State.TryGetValue("folder", out object? folder) ? (string)folder : string.Empty;

    private async Task SendFolderListAsync(FakeConnection connection)
    {
        await connection.SendAsync(ServerPacketId.FileBrowserDirList, RcText.GetBytes(CommaText.Tokenize(FolderRights))).ConfigureAwait(false);
        await connection.SendAsync(ServerPacketId.FileBrowserMessage, RcText.GetBytes(CommaText.Tokenize("Welcome to the File Browser."))).ConfigureAwait(false);
        await SendDirectoryAsync(connection, CurrentFolder(connection)).ConfigureAwait(false);
    }

    private async Task SendFolderChangedAsync(FakeConnection connection, string folder)
    {
        await connection.SendAsync(ServerPacketId.FileBrowserMessage, RcText.GetBytes(CommaText.Tokenize("Folder changed to " + folder))).ConfigureAwait(false);
        await SendDirectoryAsync(connection, folder).ConfigureAwait(false);
    }

    /// <summary>Sends the listing of a folder as Alpaxis does: GString folder, then per file <c>0x20, GChar length, GString name, GString rights, GInt5 size, GInt5 time</c>.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="folder">The folder (e.g. <c>levels/</c>).</param>
    /// <returns>Whether it was written.</returns>
    public Task<bool> SendDirectoryAsync(FakeConnection connection, string folder)
    {
        var writer = new PacketWriter().WriteGString(folder);
        string rights = FolderRights.FirstOrDefault(row => FolderOf(row) == folder)?.Split(' ')[0] ?? "r";
        foreach (var file in Files.Where(pair => pair.Key.StartsWith(folder, StringComparison.Ordinal) && pair.Key.IndexOf('/', folder.Length) < 0).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string name = file.Key[folder.Length..];
            var entry = new PacketWriter().WriteGString(name).WriteGString(rights).WriteGInt5(file.Value.Length).WriteGInt5(FileModified);
            writer.WriteByte(0x20).WriteGByte(entry.Length).WriteBytes(entry.WrittenSpan);
        }

        return connection.SendAsync(ServerPacketId.FileBrowserDir, writer);
    }

    /// <summary>Sends a file as Alpaxis does: FILESENDFAILED when missing or empty; RAWDATA+FILE up to 32000 bytes; else LARGEFILESTART, LARGEFILESIZE, pieces, LARGEFILEEND.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="path">The <c>folder/name</c> key in <see cref="Files"/>.</param>
    /// <param name="name">The name sent back.</param>
    /// <returns>A task that completes when everything was written.</returns>
    public async Task SendFileAsync(FakeConnection connection, string path, string name)
    {
        if (!Files.TryGetValue(path, out byte[]? content) || content.Length == 0)
        {
            await connection.SendTextAsync(ServerPacketId.FileSendFailed, name).ConfigureAwait(false);
            return;
        }

        bool large = content.Length > FileChunkSize;
        if (large)
        {
            await connection.SendTextAsync(ServerPacketId.LargeFileStart, name).ConfigureAwait(false);
            await connection.SendAsync(ServerPacketId.LargeFileSize, new PacketWriter().WriteGInt5(content.Length)).ConfigureAwait(false);
        }

        for (int offset = 0; offset < content.Length; offset += FileChunkSize)
        {
            byte[] chunk = content.AsSpan(offset, Math.Min(FileChunkSize, content.Length - offset)).ToArray();
            await connection.SendRawDataAsync(ServerPacketId.File, new PacketWriter().WriteGInt5(FileModified).WriteGString(name).WriteBytes(chunk).ToArray())
                .ConfigureAwait(false);
        }

        if (large) await connection.SendTextAsync(ServerPacketId.LargeFileEnd, name).ConfigureAwait(false);
        await connection.SendAsync(ServerPacketId.FileBrowserMessage, RcText.GetBytes(CommaText.Tokenize("Downloaded file " + name))).ConfigureAwait(false);
    }

    private void HandleUpload(FakeConnection connection, ReceivedPacket packet)
    {
        var reader = packet.Reader();
        if (!reader.TryReadGString(out string name)) return;
        byte[] content = reader.ReadRemainingBytes();
        if (connection.State.TryGetValue("large:" + name, out object? buffer))
        {
            ((MemoryStream)buffer).Write(content);
            return;
        }

        Files[CurrentFolder(connection) + name] = content;
        _ = connection.SendAsync(ServerPacketId.FileBrowserMessage, RcText.GetBytes(CommaText.Tokenize("Uploaded file " + name)));
    }

    // ---- Packet builders for tests ---------------------------------------------------------------------------------------------

    /// <summary>Sends an RC chat line.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="text">The line.</param>
    /// <returns>Whether it was written.</returns>
    public static Task<bool> SendRcChatAsync(FakeConnection connection, string text) => connection.SendTextAsync(ServerPacketId.RcChat, text);

    /// <summary>Sends PLO_ADDPLAYER as Alpaxis does for RCs: id, account, then level (20), status icon (53), nickname (0), community (82), and the category (81) when not 0.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="id">The player id.</param>
    /// <param name="account">The account.</param>
    /// <param name="nickname">The nickname.</param>
    /// <param name="level">The level.</param>
    /// <param name="statusIcon">The status-message index.</param>
    /// <param name="category">The player-list category.</param>
    /// <returns>Whether it was written.</returns>
    public static Task<bool> SendAddPlayerAsync(FakeConnection connection, int id, string account, string nickname, string level = "", int statusIcon = 0,
        int category = 0)
    {
        var writer = new PacketWriter().WriteGShort(id).WriteGString(account)
            .WriteGByte((int)PlayerPropertyId.CurrentLevel).WriteGString(level)
            .WriteGByte((int)PlayerPropertyId.StatusMessage).WriteGByte(statusIcon)
            .WriteGByte((int)PlayerPropertyId.Nickname).WriteGString(nickname)
            .WriteGByte((int)PlayerPropertyId.CommunityName).WriteGString(string.Empty);
        if (category != 0) writer.WriteGByte((int)PlayerPropertyId.PlayerListCategory).WriteGByte(category);
        return connection.SendAsync(ServerPacketId.AddPlayer, writer);
    }

    /// <summary>Sends PLO_DELPLAYER.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="id">The player id.</param>
    /// <returns>Whether it was written.</returns>
    public static Task<bool> SendDeletePlayerAsync(FakeConnection connection, int id) =>
        connection.SendAsync(ServerPacketId.DeletePlayer, new PacketWriter().WriteGShort(id));

    /// <summary>Sends a private message as Alpaxis does: <c>"","Private message:",&lt;comma text&gt;</c>.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="fromId">The sender.</param>
    /// <param name="text">The message.</param>
    /// <returns>Whether it was written.</returns>
    public static Task<bool> SendPrivateMessageAsync(FakeConnection connection, int fromId, string text) =>
        connection.SendAsync(ServerPacketId.PrivateMessage, new PacketWriter().WriteGShort(fromId).WriteText("\"\",\"Private message:\"," + CommaText.Tokenize(text)));

    /// <summary>Sends a server text (comma text of the given fields).</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="fields">The fields.</param>
    /// <returns>Whether it was written.</returns>
    public static Task<bool> SendServerTextAsync(FakeConnection connection, params string[] fields) =>
        connection.SendTextAsync(ServerPacketId.ServerText, CommaText.Tokenize(fields));
}
