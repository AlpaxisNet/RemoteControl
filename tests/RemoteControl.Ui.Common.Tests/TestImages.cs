using System.Buffers.Binary;
using SkiaSharp;

namespace RemoteControl.Ui.Common.Tests;

/// <summary>Pictures made by the tests: PNG through Skia, a minimal animated GIF and an MNG written byte by byte.</summary>
internal static class TestImages
{
    public static byte[] Png(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(color);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>An animated GIF: one solid colour per frame, the given delays in hundredths of a second, looping.</summary>
    public static byte[] Gif(int width, int height, SKColor[] frameColors, int[] delays)
    {
        var palette = frameColors.Distinct().Take(4).ToList();
        while (palette.Count < 4) palette.Add(SKColors.Black);
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        AddShort(gif, width);
        AddShort(gif, height);
        gif.Add(0xF1);
        gif.Add(0);
        gif.Add(0);
        foreach (SKColor color in palette) { gif.Add(color.Red); gif.Add(color.Green); gif.Add(color.Blue); }
        gif.AddRange(new byte[] { 0x21, 0xFF, 0x0B });
        gif.AddRange("NETSCAPE2.0"u8.ToArray());
        gif.AddRange(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
        for (int frame = 0; frame < frameColors.Length; frame++)
        {
            gif.AddRange(new byte[] { 0x21, 0xF9, 0x04, 0x04 });
            AddShort(gif, delays[frame]);
            gif.Add(0);
            gif.Add(0);
            gif.Add(0x2C);
            AddShort(gif, 0);
            AddShort(gif, 0);
            AddShort(gif, width);
            AddShort(gif, height);
            gif.Add(0);
            gif.Add(2);
            int index = palette.IndexOf(frameColors[frame]);
            var codes = new List<int>();
            for (int pixel = 0; pixel < width * height; pixel++) { codes.Add(4); codes.Add(index); }
            codes.Add(5);
            byte[] packed = Pack(codes, 3);
            for (int offset = 0; offset < packed.Length; offset += 255)
            {
                int length = Math.Min(255, packed.Length - offset);
                gif.Add((byte)length);
                gif.AddRange(packed.Skip(offset).Take(length));
            }
            gif.Add(0);
        }
        gif.Add(0x3B);
        return gif.ToArray();
    }

    /// <summary>An MNG of PNG frames, 100 ticks per second, each frame <paramref name="delayTicks"/> ticks.</summary>
    public static byte[] Mng(int width, int height, SKColor[] frameColors, uint delayTicks)
    {
        var mng = new List<byte> { 0x8a, 0x4d, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        var header = new List<byte>();
        foreach (uint value in new uint[] { (uint)width, (uint)height, 100, 1, (uint)frameColors.Length, 0, 1 }) AddUInt32(header, value);
        AddChunk(mng, "MHDR", header);
        foreach (SKColor color in frameColors)
        {
            var delay = new List<byte> { 0, 0, 1, 0, 0, 0 };
            AddUInt32(delay, delayTicks);
            AddChunk(mng, "FRAM", delay);
            byte[] png = Png(width, height, color);
            mng.AddRange(png.Skip(8));
        }
        AddChunk(mng, "MEND", Array.Empty<byte>());
        return mng.ToArray();
    }

    private static byte[] Pack(List<int> codes, int width)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (int code in codes)
        {
            buffer |= code << bits;
            bits += width;
            while (bits >= 8) { bytes.Add((byte)(buffer & 0xFF)); buffer >>= 8; bits -= 8; }
        }
        if (bits > 0) bytes.Add((byte)(buffer & 0xFF));
        return bytes.ToArray();
    }

    private static void AddShort(List<byte> data, int value) { data.Add((byte)(value & 0xFF)); data.Add((byte)(value >> 8 & 0xFF)); }

    private static void AddUInt32(List<byte> data, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        data.AddRange(buffer.ToArray());
    }

    private static void AddChunk(List<byte> data, string type, IEnumerable<byte> content)
    {
        byte[] bytes = content.ToArray();
        AddUInt32(data, (uint)bytes.Length);
        data.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        data.AddRange(bytes);
        data.AddRange(new byte[4]);
    }
}
