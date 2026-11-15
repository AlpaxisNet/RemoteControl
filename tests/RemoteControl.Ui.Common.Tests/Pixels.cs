using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RemoteControl.Ui.Common.Tests;

/// <summary>Reads pixels of captured pictures.</summary>
internal static class Pixels
{
    public static Color At(WriteableBitmap bitmap, int x, int y)
    {
        using ILockedFramebuffer buffer = bitmap.Lock();
        var bytes = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + y * buffer.RowBytes + x * 4, bytes, 0, 4);
        bool rgba = buffer.Format == PixelFormat.Rgba8888;
        return rgba ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2]) : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }

    public static void AssertClose(Color expected, Color actual, int tolerance = 6)
    {
        bool close = Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance;
        Assert.True(close, $"Expected {expected} but the pixel is {actual}.");
    }

    public static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
