using System.Buffers;
using System.Text;

namespace RemoteControl.Extensions;

/// <summary>One line read by <see cref="BoundedLineReader"/>.</summary>
/// <param name="Text">The line without its line break (and without a trailing carriage return); invalid UTF-8 becomes U+FFFD.</param>
/// <param name="Truncated">Whether the line was longer than the limit and its rest was dropped.</param>
internal readonly record struct ReadLine(string Text, bool Truncated);

/// <summary>
/// Reads <c>\n</c>-separated UTF-8 lines from a stream with a bound on the line length, so that a peer that never sends a line
/// break cannot make the program use unbounded memory. Invalid UTF-8 never stops the reader (the C++ spun on it, D-22).
/// </summary>
internal sealed class BoundedLineReader
{
    private static readonly UTF8Encoding Utf8 = new(false, false);
    private readonly Stream _stream;
    private readonly int _maximumLineBytes;
    private readonly byte[] _buffer;
    private readonly ArrayBufferWriter<byte> _line = new();
    private int _start;
    private int _end;
    private bool _endOfStream;

    /// <summary>Creates a reader.</summary>
    /// <param name="stream">The stream; it is not disposed by the reader.</param>
    /// <param name="maximumLineBytes">The most bytes kept of one line.</param>
    public BoundedLineReader(Stream stream, int maximumLineBytes)
    {
        _stream = stream;
        _maximumLineBytes = Math.Max(1, maximumLineBytes);
        _buffer = new byte[16 * 1024];
    }

    /// <summary>Reads the next line.</summary>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The line, or <c>null</c> at the end of the stream.</returns>
    public async ValueTask<ReadLine?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        _line.Clear();
        bool truncated = false;
        while (true)
        {
            if (_start < _end)
            {
                int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                int stop = newline < 0 ? _end : newline;
                Append(_buffer.AsSpan(_start, stop - _start), ref truncated);
                if (newline >= 0)
                {
                    _start = newline + 1;
                    return Finish(truncated);
                }
                _start = _end = 0;
            }
            if (_endOfStream) return _line.WrittenCount > 0 || truncated ? Finish(truncated) : null;
            int count = await _stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count <= 0) _endOfStream = true;
            else
            {
                _start = 0;
                _end = count;
            }
        }
    }

    private void Append(ReadOnlySpan<byte> data, ref bool truncated)
    {
        int room = _maximumLineBytes - _line.WrittenCount;
        if (data.Length > room)
        {
            truncated = true;
            data = data[..Math.Max(0, room)];
        }
        if (data.Length > 0) _line.Write(data);
    }

    private ReadLine Finish(bool truncated)
    {
        ReadOnlySpan<byte> bytes = _line.WrittenSpan;
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        return new ReadLine(Utf8.GetString(bytes), truncated);
    }
}
