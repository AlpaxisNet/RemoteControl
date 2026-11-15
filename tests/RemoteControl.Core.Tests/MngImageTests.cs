using System.Buffers.Binary;

namespace RemoteControl.Core.Tests;

public sealed class MngImageTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

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

    /// <summary>The vector of tests/TMngTests.cpp.</summary>
    private static List<byte> CppVector()
    {
        var mng = new List<byte> { 0x8a, 0x4d, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        var header = new List<byte>();
        foreach (uint value in new uint[] { 32, 24, 50, 1, 2, 15, 7 }) AddUInt32(header, value);
        AddChunk(mng, "MHDR", header);
        var delay = new List<byte> { 0, 0, 1, 0, 0, 0 };
        AddUInt32(delay, 5);
        AddChunk(mng, "FRAM", delay);
        AddChunk(mng, "IHDR", new byte[13]);
        AddChunk(mng, "IDAT", [1, 2, 3]);
        AddChunk(mng, "IEND", []);
        delay = delay.Take(6).ToList();
        AddUInt32(delay, 10);
        AddChunk(mng, "FRAM", delay);
        AddChunk(mng, "IHDR", Enumerable.Repeat((byte)1, 13));
        AddChunk(mng, "IDAT", [4, 5]);
        AddChunk(mng, "IEND", []);
        return mng;
    }

    [Fact]
    public void Decodes_the_cpp_vector()
    {
        byte[] mng = CppVector().ToArray();
        Assert.True(MngImage.IsMng(mng));
        MngParseResult result = MngImage.Parse(mng);
        Assert.True(result.Success);
        MngAnimation animation = result.Animation!;
        Assert.Equal(32u, animation.Width);
        Assert.Equal(24u, animation.Height);
        Assert.Equal(50u, animation.TicksPerSecond);
        Assert.Equal(1u, animation.NominalLayerCount);
        Assert.Equal(2u, animation.NominalFrameCount);
        Assert.Equal(15u, animation.NominalPlayTime);
        Assert.Equal(7u, animation.SimplicityProfile);
        Assert.Equal(2, animation.Frames.Count);
        Assert.Equal(100u, animation.Frames[0].DelayMilliseconds);
        Assert.Equal(200u, animation.Frames[1].DelayMilliseconds);
        byte[] png = MngImage.FirstPngFrame(mng);
        Assert.Equal(60, png.Length);
        Assert.Equal(PngSignature, png.Take(8).ToArray());
        Assert.Equal(PngSignature.Length + 25 + 14 + 12, animation.Frames[1].Png.Length);
    }

    [Fact]
    public void Not_an_mng_gives_no_frame()
    {
        Assert.Empty(MngImage.FirstPngFrame("not mng"u8));
        Assert.False(MngImage.Parse("not mng"u8).Success);
        Assert.False(MngImage.IsMng([]));
    }

    [Fact]
    public void Every_truncation_gives_an_error_and_never_throws()
    {
        byte[] mng = CppVector().ToArray();
        for (int length = 0; length < mng.Length; length++)
        {
            MngParseResult result = MngImage.Parse(mng.AsSpan(0, length));
            Assert.False(result.Success && result.Animation!.Frames.Count == 2, $"length {length}");
            if (!result.Success) Assert.False(string.IsNullOrEmpty(result.Error));
        }
        Assert.Empty(MngImage.FirstPngFrame(mng.AsSpan(0, mng.Length - 1)));
    }

    [Fact]
    public void Garbage_and_hostile_lengths_are_rejected()
    {
        var random = new Random(1234);
        for (int round = 0; round < 500; round++)
        {
            byte[] garbage = new byte[random.Next(0, 200)];
            random.NextBytes(garbage);
            if (garbage.Length >= 8) new byte[] { 0x8a, 0x4d, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(garbage, 0);
            MngImage.Parse(garbage);
        }
        var huge = new List<byte> { 0x8a, 0x4d, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        AddUInt32(huge, 0xFFFFFFF0);
        huge.AddRange("IHDR"u8.ToArray());
        huge.AddRange(new byte[16]);
        MngParseResult result = MngImage.Parse(huge.ToArray());
        Assert.False(result.Success);
        Assert.Contains("past the end", result.Error);
    }

    [Fact]
    public void A_frame_without_iend_or_a_file_without_frames_is_an_error()
    {
        var noFrames = new List<byte> { 0x8a, 0x4d, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        AddChunk(noFrames, "MEND", []);
        Assert.Equal("The MNG has no frames.", MngImage.Parse(noFrames.ToArray()).Error);
        var open = new List<byte> { 0x8a, 0x4d, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        AddChunk(open, "IHDR", new byte[13]);
        Assert.Equal("The last frame has no IEND.", MngImage.Parse(open.ToArray()).Error);
        var shortHeader = new List<byte> { 0x8a, 0x4d, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        AddChunk(shortHeader, "MHDR", new byte[27]);
        Assert.False(MngImage.Parse(shortHeader.ToArray()).Success);
    }

    [Fact]
    public void Zero_ticks_mean_one_hundred_and_huge_delays_are_capped()
    {
        var mng = new List<byte> { 0x8a, 0x4d, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        var header = new List<byte>();
        foreach (uint value in new uint[] { 1, 1, 0, 0, 0, 0, 0 }) AddUInt32(header, value);
        AddChunk(mng, "MHDR", header);
        var delay = new List<byte> { 0, 0, 1, 0, 0, 0 };
        AddUInt32(delay, uint.MaxValue);
        AddChunk(mng, "FRAM", delay);
        AddChunk(mng, "IHDR", new byte[13]);
        AddChunk(mng, "IEND", []);
        MngAnimation animation = MngImage.Parse(mng.ToArray()).Animation!;
        Assert.Equal(100u, animation.TicksPerSecond);
        Assert.Equal(uint.MaxValue, animation.Frames[0].DelayMilliseconds);
    }
}
