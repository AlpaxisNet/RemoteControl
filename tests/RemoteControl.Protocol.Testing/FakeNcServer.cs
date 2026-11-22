using System.Collections.Concurrent;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Testing;

/// <summary>What a fake NC server received as an NC login.</summary>
/// <param name="Version">The version string (<c>"NCL21075"</c>).</param>
/// <param name="Account">The account.</param>
/// <param name="Password">The password.</param>
public sealed record NcLogin(string Version, string Account, string Password);

/// <summary>
/// An NC server on 127.0.0.1 (zlib frames, no encryption) that answers as the Alpaxis server does: the weapon list, weapon scripts
/// (0xA7 line breaks), class scripts (one GString per line), and stores what is saved: a class script or NPC flags as one
/// line per GString when they are an exact GString chain, otherwise as comma text (<see cref="ReadLineList"/>). After the login it sends one PLO_NC_CLASSADD
/// per class and one PLO_NC_NPCADD per NPC.
/// </summary>
public sealed class FakeNcServer : LoopbackServer
{
    private readonly List<NcLogin> _logins = new();

    /// <summary>When set, an NC login is refused with this disconnect message.</summary>
    public string? RejectLoginWith { get; set; }

    /// <summary>The weapons: name to (image, script).</summary>
    public ConcurrentDictionary<string, (string Image, string Script)> Weapons { get; } = new(StringComparer.Ordinal);

    /// <summary>The classes: name to script.</summary>
    public ConcurrentDictionary<string, string> Classes { get; } = new(StringComparer.Ordinal);

    /// <summary>The NPC flags saved with PLI_NC_NPCFLAGSSET, by NPC id.</summary>
    public ConcurrentDictionary<int, string> NpcFlags { get; } = new();

    /// <summary>The bytes of the last class script or NPC flags received, after the class name or the NPC id.</summary>
    public byte[] LastLineList { get; private set; } = Array.Empty<byte>();

    /// <summary>The database NPCs: id to (name, type, level).</summary>
    public ConcurrentDictionary<int, (string Name, string Type, string Level)> Npcs { get; } = new();

    /// <summary>The logins received.</summary>
    public IReadOnlyList<NcLogin> Logins
    {
        get
        {
            lock (_logins) return _logins.ToArray();
        }
    }

    internal override IFrameCodec CreateCodec() => new ServerNcCodec();

    /// <inheritdoc />
    protected override void OnPacket(FakeConnection connection, ReceivedPacket packet)
    {
        if (!connection.State.ContainsKey("login"))
        {
            if (packet.Id != (int)ClientPacketId.NpcProps) return;
            var reader = packet.Reader();
            reader.TryReadText(8, out string version);
            reader.TryReadGString(out string account);
            reader.TryReadGString(out string password);
            lock (_logins) _logins.Add(new NcLogin(version, account, password));
            connection.State["login"] = true;
            if (RejectLoginWith is not null) connection.State["rejected"] = true;
            _ = AnswerLoginAsync(connection);
            return;
        }

        if (connection.State.ContainsKey("rejected")) return;
        switch ((ClientPacketId)packet.Id)
        {
            case ClientPacketId.NcWeaponListGet:
            {
                var writer = new PacketWriter();
                foreach (string name in Weapons.Keys.OrderBy(name => name, StringComparer.Ordinal)) writer.WriteGString(name);
                _ = connection.SendAsync(ServerPacketId.NcWeaponList, writer);
                break;
            }
            case ClientPacketId.NcWeaponGet:
                if (Weapons.TryGetValue(packet.Text, out var weapon))
                    _ = connection.SendAsync(ServerPacketId.NcWeaponGet,
                        new PacketWriter().WriteGString(packet.Text).WriteGString(weapon.Image).WriteText(weapon.Script.Replace('\n', '§')));
                break;
            case ClientPacketId.NcWeaponAdd:
            {
                var reader = packet.Reader();
                reader.TryReadGString(out string name);
                reader.TryReadGString(out string image);
                Weapons[name] = (image, reader.ReadRemainingText().Replace('§', '\n'));
                _ = connection.SendTextAsync(ServerPacketId.RcChat, "Weapon/GUI-script " + name + " added/updated");
                break;
            }
            case ClientPacketId.NcWeaponDelete:
                Weapons.TryRemove(packet.Text, out _);
                break;
            case ClientPacketId.NcClassEdit:
                if (Classes.TryGetValue(packet.Text, out string? script))
                {
                    var writer = new PacketWriter().WriteGString(packet.Text);
                    foreach (string line in script.Split('\n')) writer.WriteGString(line);
                    _ = connection.SendAsync(ServerPacketId.NcClassGet, writer);
                }

                break;
            case ClientPacketId.NcClassAdd:
            {
                var reader = packet.Reader();
                reader.TryReadGString(out string name);
                bool added = !Classes.ContainsKey(name);
                LastLineList = reader.ReadRemainingBytes();
                Classes[name] = ReadLineList(LastLineList);
                if (added) _ = connection.SendTextAsync(ServerPacketId.NcClassAdd, name);
                break;
            }
            case ClientPacketId.NcNpcFlagsSet:
            {
                var reader = packet.Reader();
                if (!reader.TryReadGInt3(out int id)) break;
                LastLineList = reader.ReadRemainingBytes();
                NpcFlags[id] = ReadLineList(LastLineList);
                break;
            }
        }
    }

    /// <summary>
    /// A line list as the Alpaxis server reads it after the fixed fields: one line per GString when the bytes are an exact GString chain,
    /// otherwise comma text, read here with a reader of its own (the Graal rules: commas outside quotes part the lines; inside quotes
    /// <c>""</c> is a quote and <c>\\</c> a backslash), so that the RC's writer is not checked against the RC's reader.
    /// </summary>
    public static string ReadLineList(byte[] bytes)
    {
        if (LineListText.IsExactGStringChain(bytes))
        {
            var reader = new PacketReader(bytes);
            var lines = new List<string>();
            while (reader.TryReadGString(out string line)) lines.Add(line);
            return string.Join('\n', lines);
        }
        string text = RcText.GetString(bytes);
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];
            if (quoted)
            {
                if (c == '"' && index + 1 < text.Length && text[index + 1] == '"') { field.Append('"'); index++; }
                else if (c == '\\' && index + 1 < text.Length && text[index + 1] == '\\') { field.Append('\\'); index++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        fields.Add(field.ToString());
        return string.Join('\n', fields);
    }

    private async Task AnswerLoginAsync(FakeConnection connection)
    {
        if (RejectLoginWith is not null)
        {
            await connection.SendTextAsync(ServerPacketId.DisconnectMessage, RejectLoginWith).ConfigureAwait(false);
            connection.Close();
            return;
        }

        foreach (var npc in Npcs.OrderBy(pair => pair.Key))
        {
            await connection.SendAsync(ServerPacketId.NcNpcAdd, new PacketWriter().WriteGInt3(npc.Key).WriteGByte(50).WriteGString(npc.Value.Name)
                .WriteGByte(51).WriteGString(npc.Value.Type).WriteGByte(52).WriteGString(npc.Value.Level)).ConfigureAwait(false);
        }

        foreach (string name in Classes.Keys.OrderBy(name => name, StringComparer.Ordinal))
            await connection.SendTextAsync(ServerPacketId.NcClassAdd, name).ConfigureAwait(false);
    }
}
