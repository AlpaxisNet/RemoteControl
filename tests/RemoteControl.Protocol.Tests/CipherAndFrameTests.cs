using RemoteControl.Protocol.Testing;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Protocol.Tests;

public sealed class Gen5CipherTests
{
    // The iterator follows RClib's GRCProtocol::encrypt: it = it * 0x8088405 + key (mod 2^32), starting at 0x4A80B38, once per dword,
    // and each dword is XORed low byte first. With key 0x56 the first iterators are 0xD6D1186E, 0x361E327C, 0x147EECC2, 0x4E9EA820,
    // 0x98C9C8F6, 0xFF3FC524 ... (worked out with that recurrence), so zero bytes encrypt to those values' bytes in little-endian order.
    [Fact]
    public void First_frame_xors_the_first_iterators_low_byte_first()
    {
        var cipher = new Gen5Cipher(0x56);
        byte[] data = new byte[8];
        cipher.Apply(data, FrameCompression.None);
        Assert.Equal(Convert.FromHexString("6E18D1D67C321E36"), data);
        Assert.Equal(0x361E327Cu, cipher.Iterator);
    }

    [Fact]
    public void Iterator_persists_across_frames_and_a_compressed_frame_gets_four_dwords()
    {
        var cipher = new Gen5Cipher(0x56);
        cipher.Apply(new byte[8], FrameCompression.None);
        byte[] second = new byte[20];
        cipher.Apply(second, FrameCompression.Zlib);
        // Iterators 3 to 6 (0x147EECC2, 0x4E9EA820, 0x98C9C8F6, 0xFF3FC524), then the rest is not touched.
        Assert.Equal(Convert.FromHexString("C2EC7E1420A89E4EF6C8C99824C53FFF00000000"), second);
        Assert.Equal(0xFF3FC524u, cipher.Iterator);
    }

    [Fact]
    public void An_uncompressed_frame_gets_twelve_dwords()
    {
        var cipher = new Gen5Cipher(0x56);
        byte[] data = new byte[52];
        cipher.Apply(data, FrameCompression.None);
        Assert.Equal(new byte[4], data[48..]);
        Assert.NotEqual(new byte[4], data[44..48]);
    }

    [Fact]
    public void List_server_key_four()
    {
        // Key 4: the first iterator is 0x4A80B38 * 0x8088405 + 4 = 0xD6D1181C, so '!' (0x21) becomes 0x3D.
        var cipher = new Gen5Cipher(ListServerClientCodec.Key);
        byte[] data = "!$abcd$pass\n"u8.ToArray();
        cipher.Apply(data, FrameCompression.None);
        Assert.Equal(Convert.FromHexString("3D3CB0B4F38C47D3B5B92E73"), data);
    }

    [Fact]
    public void Encrypt_and_decrypt_are_the_same_operation()
    {
        var sender = new Gen5Cipher(0x56);
        var receiver = new Gen5Cipher(0x56);
        var random = new Random(7);
        for (int frame = 0; frame < 20; frame++)
        {
            byte[] original = new byte[random.Next(1, 100)];
            random.NextBytes(original);
            byte[] copy = (byte[])original.Clone();
            byte type = frame % 2 == 0 ? FrameCompression.None : FrameCompression.Bzip2;
            sender.Apply(copy, type);
            receiver.Apply(copy, type);
            Assert.Equal(original, copy);
        }
    }
}

public sealed class FrameTests
{
    [Fact]
    public void Client_frames_decode_on_the_server_side_and_back()
    {
        var client = new GameClientCodec(0x56);
        var server = new ServerGen5Codec();
        byte[] login = Frames.Packet(6, "vGSERV025"u8);
        byte[] loginFrame = client.Seal(client.Prepare(login, plain: true));
        Assert.True(server.TryDecode(loginFrame[2..], out byte[] loginPlain));
        Assert.Equal(login, loginPlain);
        server.SetKey(0x56);

        var random = new Random(3);
        foreach (int size in new[] { 10, 47, 48, 200, 4095, 4096, 20000 })
        {
            byte[] payload = new byte[size];
            random.NextBytes(payload);
            byte[] packet = Frames.Packet(79, payload.Select(value => (byte)(value | 0x80)).ToArray());
            byte[] frame = client.Seal(client.Prepare(packet, plain: false));
            Assert.Equal(frame.Length - 2, (frame[0] << 8) | frame[1]);
            Assert.True(server.TryDecode(frame[2..], out byte[] plain));
            Assert.Equal(packet, plain);

            byte[] back = server.Seal(server.Prepare(packet, plain: false));
            Assert.True(client.TryDecode(back[2..], out byte[] received));
            Assert.Equal(packet, received);
        }
    }

    [Fact]
    public void Compression_choice_follows_rclib()
    {
        byte[] small = Frames.Packet(79, "hi"u8);
        Assert.Equal(FrameCompression.None, FrameCompression.ChooseForGameFrame(small, 0x7FFE)!.Value.Type);
        byte[] medium = Frames.Packet(79, new byte[100]);
        Assert.Equal(FrameCompression.Zlib, FrameCompression.ChooseForGameFrame(medium, 0x7FFE)!.Value.Type);
        byte[] large = Frames.Packet(79, new byte[5000]);
        Assert.Equal(FrameCompression.Bzip2, FrameCompression.ChooseForGameFrame(large, 0x7FFE)!.Value.Type);
    }

    [Fact]
    public void A_frame_too_large_is_refused_and_the_cipher_does_not_move()
    {
        var client = new GameClientCodec(0x56);
        client.Seal(client.Prepare(Frames.Packet(6, "v"u8), plain: true));
        var random = new Random(5);
        byte[] noise = new byte[100_000];
        random.NextBytes(noise);
        Assert.Throws<PacketTooLongException>(() => client.Prepare(Frames.Packet(79, noise), plain: false));

        // The next frame still decodes: Prepare did not touch the cipher.
        var server = new ServerGen5Codec();
        server.SetKey(0x56);
        byte[] frame = client.Seal(client.Prepare(Frames.Packet(79, "ok"u8), plain: false));
        Assert.True(server.TryDecode(frame[2..], out byte[] plain));
        Assert.Equal(Frames.Packet(79, "ok"u8), plain);
    }

    [Fact]
    public void Incompressible_payload_up_to_the_limit_goes_uncompressed()
    {
        byte[] noise = new byte[32000];
        new Random(9).NextBytes(noise);
        var choice = FrameCompression.ChooseForGameFrame(noise, 0x7FFE);
        Assert.NotNull(choice);
        Assert.True(choice!.Value.Body.Length <= 0x7FFE);
    }

    [Fact]
    public void Corrupt_compressed_frames_fail_instead_of_returning_zeros()
    {
        Assert.False(FrameCompression.TryZlibDecompress(new byte[] { 1, 2, 3, 4, 5 }, out _));
        Assert.False(FrameCompression.TryBzip2Decompress("BZh9garbage"u8.ToArray(), out _));
        var codec = new NcCodec();
        Assert.False(codec.TryDecode(new byte[] { 0x78, 0x9C, 0xFF, 0xFF }, out _));
    }

    [Fact]
    public void A_zip_bomb_is_refused()
    {
        byte[] bomb = FrameCompression.ZlibCompress(new byte[FrameCompression.MaxDecompressedSize + 1024]);
        Assert.False(FrameCompression.TryZlibDecompress(bomb, out _));
    }
}

public sealed class PacketSplitterTests
{
    private static byte[] RawHeader(int length)
    {
        byte[] header = new byte[3];
        GEncoding.EncodeGInt3(length, header);
        return Frames.Packet(100, header);
    }

    [Fact]
    public void Packets_end_at_newlines_and_a_fragment_carries_to_the_next_frame()
    {
        var splitter = new PacketSplitter();
        var first = splitter.Push("Jone\nJtw"u8);
        Assert.Single(first);
        var second = splitter.Push("o\n"u8);
        Assert.Equal("Jtwo"u8.ToArray(), Assert.Single(second));
    }

    [Fact]
    public void Raw_data_keeps_newlines_and_may_span_frames()
    {
        var splitter = new PacketSplitter();
        byte[] inner = Frames.Packet(102, "a\nb\nc"u8);
        byte[] all = RawHeader(inner.Length).Concat(inner).Concat("Jafter\n"u8.ToArray()).ToArray();
        var packets = splitter.Push(all.AsSpan(0, 7));
        packets.AddRange(splitter.Push(all.AsSpan(7)));
        Assert.Equal(2, packets.Count);
        Assert.Equal(inner[..^1], packets[0]);
        Assert.Equal("Jafter"u8.ToArray(), packets[1]);
    }

    [Fact]
    public void Negative_or_huge_raw_lengths_are_dropped()
    {
        var splitter = new PacketSplitter();
        var dropped = new List<string>();
        splitter.Dropped = dropped.Add;
        // A raw header whose GInt3 decodes negative (bytes below 32), then a normal packet.
        byte[] negative = { 0x84, 0x10, 0x10, 0x10, (byte)'\n' };
        var packets = splitter.Push(negative.Concat("Jok\n"u8.ToArray()).ToArray());
        Assert.Equal("Jok"u8.ToArray(), Assert.Single(packets));
        Assert.Single(dropped);
    }
}
