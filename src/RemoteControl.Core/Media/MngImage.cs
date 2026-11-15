using System.Buffers.Binary;

namespace RemoteControl.Core;

/// <summary>One frame of an MNG animation: a complete PNG file and how long it is shown.</summary>
/// <param name="Png">The PNG file (signature, then the chunks from <c>IHDR</c> to <c>IEND</c> as they were in the MNG).</param>
/// <param name="DelayMilliseconds">How long the frame is shown.</param>
public sealed record MngFrame(byte[] Png, uint DelayMilliseconds);

/// <summary>A decoded MNG animation with the values of its <c>MHDR</c> chunk.</summary>
/// <param name="Width">The frame width.</param>
/// <param name="Height">The frame height.</param>
/// <param name="TicksPerSecond">The tick rate (100 when the file says 0).</param>
/// <param name="NominalLayerCount">The nominal layer count.</param>
/// <param name="NominalFrameCount">The nominal frame count.</param>
/// <param name="NominalPlayTime">The nominal play time in ticks.</param>
/// <param name="SimplicityProfile">The simplicity profile.</param>
/// <param name="Frames">The frames, at least one.</param>
public sealed record MngAnimation(uint Width, uint Height, uint TicksPerSecond, uint NominalLayerCount, uint NominalFrameCount, uint NominalPlayTime, uint SimplicityProfile, IReadOnlyList<MngFrame> Frames);

/// <summary>The outcome of <see cref="MngImage.Parse"/>: an animation or an error.</summary>
/// <param name="Animation">The animation, or <c>null</c> on failure.</param>
/// <param name="Error">Why it failed, or <c>null</c>.</param>
public sealed record MngParseResult(MngAnimation? Animation, string? Error)
{
    /// <summary>Whether an animation was decoded.</summary>
    public bool Success => Animation is not null;
}

/// <summary>
/// The MNG reader of <c>TMng.cpp</c>: it does not render MNG, it cuts the embedded PNG streams out. Each run of chunks from
/// <c>IHDR</c> to <c>IEND</c> becomes one PNG (the PNG signature followed by those chunks, CRCs as they are); the delay of a frame
/// comes from the last <c>FRAM</c> chunk before it (<c>delay × 1000 / ticks per second</c>, rounded), 100 ms when there is none.
/// </summary>
/// <remarks>
/// Hardened: no exception escapes for any input; a truncated chunk, a chunk length past the end, an unterminated PNG or a file
/// without frames gives an error result. Input above <see cref="MaximumInputSize"/> bytes or with more than
/// <see cref="MaximumFrames"/> frames is refused, so the output is bounded by the input size.
/// </remarks>
public static class MngImage
{
    /// <summary>The largest MNG accepted.</summary>
    public const int MaximumInputSize = 64 * 1024 * 1024;

    /// <summary>The most frames accepted.</summary>
    public const int MaximumFrames = 10_000;

    private static ReadOnlySpan<byte> MngSignature => [0x8a, 0x4d, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>Whether the content starts with the MNG signature.</summary>
    /// <param name="content">The file content.</param>
    /// <returns><c>true</c> for an MNG.</returns>
    public static bool IsMng(ReadOnlySpan<byte> content) => content.Length >= MngSignature.Length && content[..MngSignature.Length].SequenceEqual(MngSignature);

    /// <summary>Decodes an MNG.</summary>
    /// <param name="content">The file content.</param>
    /// <returns>The animation or the reason it could not be read.</returns>
    public static MngParseResult Parse(ReadOnlySpan<byte> content)
    {
        try
        {
            return ParseCore(content);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or IndexOutOfRangeException or OutOfMemoryException)
        {
            return new MngParseResult(null, "The MNG could not be read: " + exception.Message);
        }
    }

    /// <summary>The first frame as a PNG, or an empty array when the content is not a readable MNG (<c>firstPngFrame</c>).</summary>
    /// <param name="content">The file content.</param>
    /// <returns>The PNG bytes or an empty array.</returns>
    public static byte[] FirstPngFrame(ReadOnlySpan<byte> content)
    {
        MngParseResult result = Parse(content);
        return result.Animation is { Frames.Count: > 0 } animation ? animation.Frames[0].Png : Array.Empty<byte>();
    }

    private static MngParseResult ParseCore(ReadOnlySpan<byte> bytes)
    {
        if (!IsMng(bytes)) return new MngParseResult(null, "Not an MNG file.");
        if (bytes.Length > MaximumInputSize) return new MngParseResult(null, "The MNG is too large.");
        uint width = 0, height = 0, ticks = 100, layers = 0, frameCount = 0, playTime = 0, profile = 0;
        var frames = new List<MngFrame>();
        using var frame = new MemoryStream();
        uint? pendingDelay = null;
        bool inPng = false;
        int offset = MngSignature.Length;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 12) return new MngParseResult(null, "Truncated chunk header.");
            uint dataLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            long chunkLength = (long)dataLength + 12;
            if (chunkLength > bytes.Length - offset) return new MngParseResult(null, "A chunk runs past the end of the file.");
            ReadOnlySpan<byte> type = bytes.Slice(offset + 4, 4);
            ReadOnlySpan<byte> data = bytes.Slice(offset + 8, (int)dataLength);
            if (type.SequenceEqual("MHDR"u8))
            {
                if (dataLength < 28) return new MngParseResult(null, "MHDR is too short.");
                width = BinaryPrimitives.ReadUInt32BigEndian(data);
                height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                uint tickValue = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
                if (tickValue > 0) ticks = tickValue;
                layers = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
                frameCount = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
                playTime = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
                profile = BinaryPrimitives.ReadUInt32BigEndian(data[24..]);
            }
            else if (type.SequenceEqual("FRAM"u8)) pendingDelay = FrameDelay(data);
            if (!inPng && type.SequenceEqual("IHDR"u8))
            {
                frame.SetLength(0);
                frame.Write(PngSignature);
                inPng = true;
            }
            if (inPng)
            {
                frame.Write(bytes.Slice(offset, (int)chunkLength));
                if (type.SequenceEqual("IEND"u8))
                {
                    if (frames.Count >= MaximumFrames) return new MngParseResult(null, "The MNG has too many frames.");
                    double milliseconds = pendingDelay.HasValue ? Math.Round(pendingDelay.Value * 1000.0 / ticks) : 100.0;
                    uint delay = milliseconds >= uint.MaxValue ? uint.MaxValue : (uint)Math.Max(0.0, milliseconds);
                    frames.Add(new MngFrame(frame.ToArray(), delay));
                    frame.SetLength(0);
                    pendingDelay = null;
                    inPng = false;
                }
            }
            offset += (int)chunkLength;
        }
        if (inPng) return new MngParseResult(null, "The last frame has no IEND.");
        if (frames.Count == 0) return new MngParseResult(null, "The MNG has no frames.");
        return new MngParseResult(new MngAnimation(width, height, ticks, layers, frameCount, playTime, profile, frames), null);
    }

    /// <summary>
    /// The delay of a <c>FRAM</c> chunk (<c>frameDelay</c> in <c>TMng.cpp</c>): skip the framing mode byte and the
    /// NUL-terminated name, read the change-delay flag, skip the three other flags, then the 4-byte delay when the flag is set.
    /// </summary>
    private static uint? FrameDelay(ReadOnlySpan<byte> data)
    {
        int length = data.Length;
        if (length == 0) return null;
        int offset = 1;
        while (offset < length && data[offset] != 0) offset++;
        if (offset == length || ++offset >= length) return null;
        bool hasDelay = data[offset++] != 0;
        if (length - offset < 3) return null;
        offset += 3;
        if (!hasDelay || length - offset < 4) return null;
        return BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
    }
}
