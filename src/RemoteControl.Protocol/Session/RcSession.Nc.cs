using System.Net.Sockets;
using RemoteControl.Protocol.Net;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Session;

public sealed partial class RcSession
{
    private readonly List<RcWeapon> _weapons = new();
    private readonly List<RcClass> _classes = new();
    private readonly List<RcNpc> _npcs = new();
    private List<RcLevel> _levels = new();
    private readonly Dictionary<int, string> _npcFlags = new();
    private readonly Queue<int> _pendingNpcAttributes = new();
    private readonly Queue<string> _pendingLocalNpcs = new();
    private int _levelListRequests;

    /// <inheritdoc />
    public IReadOnlyList<RcWeapon> Weapons => _weapons.AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<RcClass> Classes => _classes.AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<RcNpc> Npcs => _npcs.AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<RcLevel> Levels => _levels.AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyDictionary<int, string> NpcFlags => _npcFlags.AsReadOnly();

    /// <summary>Runs an action on the context and completes when it ran (immediately when already there).</summary>
    private Task OnContextAsync(Action action)
    {
        if (_poster.IsOnContext)
        {
            action();
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool posted = _poster.Post(() =>
        {
            try
            {
                action();
            }
            finally
            {
                done.TrySetResult();
            }
        });
        if (!posted) done.TrySetResult();
        return done.Task;
    }

    /// <summary>Starts the NC connect on the context. RClib connected synchronously on the UI thread; this never blocks.</summary>
    private async Task<bool> ConnectNcCoreAsync(CancellationToken cancellationToken)
    {
        if (!_authenticated) return false;
        if (_nc is not null && _ncAuthenticated) return true;
        if (_ncConnecting && _ncLogin is { } running) return await running.Task.ConfigureAwait(false);
        string? address = _ncServerAddress;
        if (string.IsNullOrEmpty(address))
        {
            Refuse("NC server address not received");
            return false;
        }

        int comma = address.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            Refuse("Invalid NC server address format");
            return false;
        }

        string host = address[..comma].Trim();
        int port = Text.TextHelpers.ParseInt(address[(comma + 1)..]);
        if (host is "127.0.0.1" or "localhost" or "0.0.0.0" || host.Length == 0) host = _server?.Host ?? host;
        if (port is <= 0 or > 65535)
        {
            Refuse("Invalid NC server address format");
            return false;
        }

        int generation = ++_ncGeneration;
        var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ncLogin = wait;
        _ncConnecting = true;
        TcpClient? client = null;
        try
        {
            client = await FramedConnection.ConnectAsync(host, port, _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or SocketException)
        {
            string reason = exception is OperationCanceledException ? "The NC connect was cancelled." : $"Failed to connect to NC server: {exception.Message}";
            await OnContextAsync(() =>
            {
                if (generation != _ncGeneration) return;
                _ncConnecting = false;
                wait.TrySetResult(false);
                Refuse(reason);
            }).ConfigureAwait(false);
            return false;
        }

        bool started = false;
        await OnContextAsync(() =>
        {
            if (generation != _ncGeneration || _closed || !_ncConnecting) return;
            var connection = new FramedConnection(client, new NcCodec()) { Log = Log };
            connection.PacketReceived = packet => _poster.Post(() => HandleNcPacket(packet, generation));
            connection.Closed = reason => _poster.Post(() => EndNc(reason, requested: false, generation));
            _nc = connection;
            _ncConnected = true;
            connection.Start();
            started = true;
            SendNcNow(ClientPacketId.NpcProps, writer => writer.WriteText(_options.NcVersion).WriteGString(_options.Account).WriteGString(_options.Password),
                requireAuthenticated: false);
            // RClib waited for the first NC packet, slept 300 ms on the NC thread and then asked for the weapon list. Alpaxis sends nothing
            // after an NC login when there are no NPCs or classes, so the weapon list is asked for at once: its answer confirms the login.
            SendNcNow(ClientPacketId.NcWeaponListGet, requireAuthenticated: false);
        }).ConfigureAwait(false);
        if (!started)
        {
            client.Dispose();
            wait.TrySetResult(false);
            return false;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_options.NcLoginTimeout);
        using (limit.Token.Register(() => wait.TrySetResult(false)))
        {
            bool success = await wait.Task.ConfigureAwait(false);
            if (!success)
                await OnContextAsync(() => EndNc(cancellationToken.IsCancellationRequested ? "The NC connect was cancelled." : "The NC server did not answer the login in time.",
                    requested: cancellationToken.IsCancellationRequested, generation)).ConfigureAwait(false);
            return success && _ncAuthenticated;
        }
    }

    /// <summary>Ends the NC connection of one generation; raises <see cref="NcDisconnected"/> once. Runs on the context.</summary>
    private void EndNc(string reason, bool requested, int generation)
    {
        if (generation != _ncGeneration) return;
        FramedConnection? connection = _nc;
        bool wasConnecting = _ncConnecting;
        if (connection is null && !wasConnecting) return;
        _ncGeneration++;
        _nc = null;
        _ncConnected = false;
        _ncAuthenticated = false;
        _ncConnecting = false;
        _ncLogin?.TrySetResult(false);
        if (connection is not null)
        {
            connection.Closed = null;
            connection.PacketReceived = null;
            connection.Close(reason);
            if (!requested) _lastError = reason;
            Raise(NcDisconnected, new DisconnectedEventArgs(reason, requested));
        }
    }

    private void HandleNcPacket(byte[] packet, int generation)
    {
        if (_closed || generation != _ncGeneration || packet.Length == 0) return;
        int id = PacketId(packet[0]);
        byte[] payload = packet.AsSpan(1).ToArray();
        Raise(RawPacketReceived, new RawPacketEventArgs(RcConnectionKind.Nc, id, payload));
        if (id == (int)ServerPacketId.DisconnectMessage)
        {
            string text = RcText.GetString(payload);
            EndNc(text.Length > 0 ? text : "Connection closed", requested: false, generation);
            return;
        }

        if (!_ncAuthenticated)
        {
            _ncAuthenticated = true;
            _ncConnecting = false;
            Raise(NcConnected);
            _ncLogin?.TrySetResult(true);
        }

        try
        {
            DispatchNc((ServerPacketId)id, payload);
        }
        catch (Exception exception)
        {
            string message = $"NC packet {id} ({payload.Length} bytes) could not be read and was dropped: {exception.Message}";
            Log(message);
            Raise(ProtocolError, new TextEventArgs(message));
        }
    }

    private void DispatchNc(ServerPacketId id, byte[] payload)
    {
        var reader = new PacketReader(payload);
        switch (id)
        {
            case ServerPacketId.RcChat:
                Raise(NcChatReceived, new TextEventArgs(FixOfPlus(reader.ReadRemainingText())));
                break;
            case ServerPacketId.NcLevelList:
                HandleLevelList(payload);
                break;
            case ServerPacketId.NcWeaponList:
            {
                _weapons.Clear();
                while (!reader.AtEnd)
                {
                    if (!reader.TryReadGString(out string name)) break;
                    _weapons.Add(new RcWeapon(name, string.Empty, string.Empty));
                }

                Raise(WeaponListReceived, new ValueEventArgs<int>(_weapons.Count));
                break;
            }
            case ServerPacketId.NcClassAdd:
            {
                string name = FirstLine(reader.ReadRemainingText());
                if (name.Length == 0 || _classes.Any(item => item.Name == name)) break;
                _classes.Add(new RcClass(name, string.Empty));
                Raise(ClassAdded, new ValueEventArgs<string>(name));
                break;
            }
            case ServerPacketId.NcClassDelete:
            {
                string name = FirstLine(reader.ReadRemainingText());
                if (_classes.RemoveAll(item => item.Name == name) > 0) Raise(ClassDeleted, new ValueEventArgs<string>(name));
                break;
            }
            case ServerPacketId.NcLevelDump:
            {
                string text = DecodeLineList(payload, 0);
                if (_levelListRequests > 0)
                {
                    _levelListRequests--;
                    _levels = SplitParts(text).Where(line => line.Length > 0).Select(line => new RcLevel(line)).ToList();
                    Raise(LevelListReceived, new TextEventArgs(text));
                }
                else
                {
                    string level = _pendingLocalNpcs.Count > 0 ? _pendingLocalNpcs.Dequeue() : string.Empty;
                    Raise(LocalNpcsReceived, new LocalNpcsEventArgs(level, text));
                }

                break;
            }
            case ServerPacketId.NcWeaponGet:
            {
                if (!reader.TryReadGString(out string name) || !reader.TryReadGString(out string image)) break;
                string script = reader.ReadRemainingText().Replace('§', '\n');
                int index = _weapons.FindIndex(item => item.Name == name);
                var weapon = new RcWeapon(name, image, script);
                if (index >= 0) _weapons[index] = weapon;
                else _weapons.Add(weapon);
                Raise(ScriptReceived, new ScriptReceivedEventArgs(ScriptKind.Weapon, name, 0, script, image));
                break;
            }
            case ServerPacketId.NcNpcAttributes:
            {
                if (reader.AtEnd) break;
                int npcId = _pendingNpcAttributes.Count > 0 ? _pendingNpcAttributes.Dequeue() : -1;
                Raise(NpcAttributesReceived, new NpcTextEventArgs(npcId, reader.ReadRemainingCommaText()));
                break;
            }
            case ServerPacketId.NcNpcAdd:
                HandleNpcAdd(reader);
                break;
            case ServerPacketId.NcNpcDelete:
                if (reader.TryReadGInt3(out int deleted))
                {
                    _npcs.RemoveAll(npc => npc.Id == deleted);
                    Raise(NpcDeleted, new NpcEventArgs(deleted, string.Empty));
                }

                break;
            case ServerPacketId.NcNpcScript:
            {
                if (!reader.TryReadGInt3(out int npcId)) break;
                string script = reader.ReadRemainingCommaText();
                int index = _npcs.FindIndex(npc => npc.Id == npcId);
                string name = string.Empty;
                if (index >= 0)
                {
                    name = _npcs[index].Name;
                    _npcs[index] = _npcs[index] with { Script = script };
                }

                Raise(ScriptReceived, new ScriptReceivedEventArgs(ScriptKind.Npc, name, npcId, script, string.Empty));
                break;
            }
            case ServerPacketId.NcNpcFlags:
            {
                if (!reader.TryReadGInt3(out int npcId)) break;
                string flags = DecodeLineList(payload, reader.Position);
                _npcFlags[npcId] = flags;
                Raise(NpcFlagsReceived, new NpcTextEventArgs(npcId, flags));
                break;
            }
            case ServerPacketId.NcClassGet:
            {
                if (!reader.TryReadGString(out string name)) break;
                string script = DecodeLineList(payload, reader.Position);
                int index = _classes.FindIndex(item => item.Name == name);
                if (index >= 0) _classes[index] = new RcClass(name, script);
                else _classes.Add(new RcClass(name, script));
                Raise(ScriptReceived, new ScriptReceivedEventArgs(ScriptKind.Class, name, 0, script, string.Empty));
                break;
            }
        }
    }

    private void HandleNpcAdd(PacketReader reader)
    {
        if (!reader.TryReadGInt3(out int npcId)) return;
        string name = string.Empty, type = string.Empty, level = string.Empty;
        while (!reader.AtEnd)
        {
            int start = reader.Position;
            reader.TryReadGByte(out int property);
            if (property is < 50 or > 52 || !reader.TryReadGString(out string value))
            {
                reader.Rewind(start);
                break;
            }

            if (property == 50) name = value;
            else if (property == 51) type = value;
            else level = value;
        }

        int index = _npcs.FindIndex(npc => npc.Id == npcId);
        if (index < 0)
        {
            _npcs.Add(new RcNpc(npcId, name, type, level, string.Empty));
        }
        else
        {
            RcNpc old = _npcs[index];
            _npcs[index] = old with
            {
                Name = name.Length > 0 ? name : old.Name, Type = type.Length > 0 ? type : old.Type, Level = level.Length > 0 ? level : old.Level,
            };
        }

        Raise(NpcAdded, new NpcEventArgs(npcId, name));
    }
}
