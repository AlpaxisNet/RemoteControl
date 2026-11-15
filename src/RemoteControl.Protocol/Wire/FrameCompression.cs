using System.IO.Compression;
using ICSharpCode.SharpZipLib.BZip2;

namespace RemoteControl.Protocol.Wire;

/// <summary>The compression types of a game-server frame and the zlib and bzip2 helpers, with a cap on what a decompression may produce.</summary>
internal static class FrameCompression
{
    /// <summary>Type byte 0x02: not compressed.</summary>
    public const byte None = 0x02;

    /// <summary>Type byte 0x04: zlib.</summary>
    public const byte Zlib = 0x04;

    /// <summary>Type byte 0x06: bzip2.</summary>
    public const byte Bzip2 = 0x06;

    /// <summary>The most a frame may inflate to (a zip bomb or garbage cannot claim more memory).</summary>
    public const int MaxDecompressedSize = 32 * 1024 * 1024;

    /// <summary>Compresses with zlib (RFC 1950, default level as miniz's <c>mz_compress</c>).</summary>
    public static byte[] ZlibCompress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(data.Length / 2 + 16);
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>Inflates zlib data. Fails (instead of returning a zero-filled buffer) when the data is corrupt or too large.</summary>
    public static bool TryZlibDecompress(ReadOnlySpan<byte> data, out byte[] result, int maxSize = MaxDecompressedSize)
    {
        result = Array.Empty<byte>();
        if (data.IsEmpty) return false;
        try
        {
            using var input = new MemoryStream(data.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            return TryReadAll(zlib, maxSize, out result);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Compresses with bzip2, block size 5 (as RClib).</summary>
    public static byte[] Bzip2Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(data.Length / 2 + 64);
        using (var bzip = new BZip2OutputStream(output, 5) { IsStreamOwner = false }) bzip.Write(data);
        return output.ToArray();
    }

    /// <summary>Decompresses bzip2 data; fails when it is corrupt or too large.</summary>
    public static bool TryBzip2Decompress(ReadOnlySpan<byte> data, out byte[] result, int maxSize = MaxDecompressedSize)
    {
        result = Array.Empty<byte>();
        if (data.Length < 4) return false;
        try
        {
            using var input = new MemoryStream(data.ToArray(), writable: false);
            using var bzip = new BZip2InputStream(input) { IsStreamOwner = false };
            return TryReadAll(bzip, maxSize, out result) && result.Length > 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException
                                              or NullReferenceException or ICSharpCode.SharpZipLib.SharpZipBaseException)
        {
            return false;
        }
    }

    /// <summary>
    /// Chooses the compression of an outgoing game frame as RClib does (bzip2 over 4095 bytes, zlib over 47 bytes, zlib below that
    /// only when it is smaller), except that a compressed body that does not fit in <paramref name="maxBody"/> bytes while the plain
    /// payload does is sent uncompressed instead.
    /// </summary>
    /// <returns>The type and body, or <c>null</c> when nothing fits.</returns>
    public static (byte Type, byte[] Body)? ChooseForGameFrame(byte[] payload, int maxBody)
    {
        (byte, byte[])? chosen = null;
        if (payload.Length > 0xFFF)
        {
            byte[] bzip = Bzip2Compress(payload);
            if (bzip.Length > 0) chosen = (Bzip2, bzip);
        }

        if (chosen is null)
        {
            byte[] zlib = ZlibCompress(payload);
            if (payload.Length > 0x2F || zlib.Length < payload.Length) chosen = (Zlib, zlib);
            else chosen = (None, payload);
        }

        if (chosen.Value.Item2.Length <= maxBody) return chosen;
        if (payload.Length <= maxBody) return (None, payload);
        return null;
    }

    private static bool TryReadAll(Stream stream, int maxSize, out byte[] result)
    {
        result = Array.Empty<byte>();
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maxSize) return false;
            output.Write(buffer, 0, read);
        }

        result = output.ToArray();
        return true;
    }
}
