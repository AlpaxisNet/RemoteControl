namespace RemoteControl.Protocol.Wire;

/// <summary>
/// Splits the plaintext of received frames into packets. A packet ends at a newline. A <c>PLO_RAWDATA</c> packet announces that the
/// next N bytes are one packet in their own right, newlines included (a file); its trailing newline is removed. Unlike RClib, a packet
/// or raw block that continues in the next frame is carried over (the Alpaxis server batches packets, so a raw block can start near
/// the end of one frame), and a raw length that is negative or too large is refused instead of crashing.
/// </summary>
internal sealed class PacketSplitter
{
    /// <summary>The longest packet or raw block accepted; anything longer is dropped.</summary>
    public const int MaxPacketSize = 8 * 1024 * 1024;

    private readonly List<byte> _pending = new();
    private readonly byte _rawDataByte;
    private int _rawRemaining = -1;

    /// <summary>Creates a splitter.</summary>
    /// <param name="rawDataId">The id of the raw-data packet: <c>PLO_RAWDATA</c> (100) for what a server sends, <c>PLI_RAWDATA</c> (50) for what a client sends.</param>
    public PacketSplitter(int rawDataId = (int)ServerPacketId.RawData) => _rawDataByte = GEncoding.EncodeGByte(rawDataId);

    /// <summary>Called with a reason when something had to be dropped.</summary>
    public Action<string>? Dropped { get; set; }

    /// <summary>Adds the plaintext of one frame and returns the packets that are complete.</summary>
    public List<byte[]> Push(ReadOnlySpan<byte> plaintext)
    {
        var packets = new List<byte[]>();
        foreach (byte value in plaintext) _pending.Add(value);
        int index = 0;
        while (index < _pending.Count)
        {
            if (_rawRemaining >= 0)
            {
                if (_pending.Count - index < _rawRemaining) break;
                int length = _rawRemaining;
                if (length > 0 && _pending[index + length - 1] == '\n') length--;
                packets.Add(_pending.GetRange(index, length).ToArray());
                index += _rawRemaining;
                _rawRemaining = -1;
                continue;
            }

            int newline = _pending.IndexOf((byte)'\n', index);
            if (newline < 0) break;
            byte[] packet = _pending.GetRange(index, newline - index).ToArray();
            index = newline + 1;
            if (packet.Length >= 1 && packet[0] == _rawDataByte)
            {
                if (packet.Length < 4)
                {
                    Dropped?.Invoke("A raw-data header is too short.");
                    continue;
                }

                int rawLength = GEncoding.DecodeGInt3(packet.AsSpan(1, 3));
                if (rawLength <= 0 || rawLength > MaxPacketSize)
                {
                    Dropped?.Invoke($"A raw-data length of {rawLength} was refused.");
                    continue;
                }

                _rawRemaining = rawLength;
                continue;
            }

            if (packet.Length > 0) packets.Add(packet);
        }

        _pending.RemoveRange(0, index);
        int limit = _rawRemaining >= 0 ? _rawRemaining : MaxPacketSize;
        if (_pending.Count > limit && _rawRemaining < 0)
        {
            Dropped?.Invoke($"{_pending.Count} bytes without a newline were dropped.");
            _pending.Clear();
        }

        return packets;
    }
}
