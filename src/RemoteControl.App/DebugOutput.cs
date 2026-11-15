using System.Text;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;

namespace RemoteControl.App;

/// <summary>
/// The <c>--debug</c> and <c>--packetlog</c> output on standard error, in the C++ formats (<c>TDebug.h</c>):
/// <c>[RemoteControl] &lt;message&gt;</c> and <c>[RemoteControl packet] id=&lt;id&gt; length=&lt;n&gt; data=&lt;HEX&gt;</c>. Thread-safe; writing never throws.
/// </summary>
internal sealed class DebugOutput : IDisposable
{
    private readonly object _gate = new();
    private readonly TextWriter _writer;
    private Timer? _tail;
    private AppLog? _log;
    private string? _lastLine;
    private bool _disposed;

    /// <summary>Creates the output.</summary>
    /// <param name="writer">Where lines go (standard error in the program).</param>
    /// <param name="debug">Whether <see cref="Line"/> writes.</param>
    /// <param name="packets">Whether <see cref="Packet"/> writes.</param>
    public DebugOutput(TextWriter writer, bool debug, bool packets)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        Debug = debug || packets;
        Packets = packets;
    }

    /// <summary>An output that writes nothing.</summary>
    public static DebugOutput None { get; } = new(TextWriter.Null, false, false);

    /// <summary>Whether debug lines are written.</summary>
    public bool Debug { get; }

    /// <summary>Whether packet lines are written.</summary>
    public bool Packets { get; }

    /// <summary>The text of a packet line, as <c>remoteControlPacketLogMessage</c> printed it (without the line end).</summary>
    /// <param name="packetId">The packet id.</param>
    /// <param name="payload">The packet's bytes.</param>
    /// <returns>The line.</returns>
    public static string FormatPacket(int packetId, ReadOnlySpan<byte> payload)
    {
        var text = new StringBuilder(48 + payload.Length * 2);
        text.Append("[RemoteControl packet] id=").Append(packetId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(" length=").Append(payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" data=");
        text.Append(Convert.ToHexString(payload));
        return text.ToString();
    }

    /// <summary>The text of a debug line (without the line end).</summary>
    /// <param name="message">The message.</param>
    /// <returns>The line.</returns>
    public static string FormatLine(string message) => "[RemoteControl] " + (message ?? string.Empty);

    /// <summary>Writes a debug line when <c>--debug</c> is on.</summary>
    /// <param name="message">The message.</param>
    public void Line(string message)
    {
        if (Debug) Write(FormatLine(message));
    }

    /// <summary>Writes a packet line when <c>--packetlog</c> is on.</summary>
    /// <param name="packetId">The packet id.</param>
    /// <param name="payload">The bytes.</param>
    public void Packet(int packetId, ReadOnlySpan<byte> payload)
    {
        if (Packets) Write(FormatPacket(packetId, payload));
    }

    /// <summary>Writes the packet lines of a connection for as long as the returned object lives (nothing without <c>--packetlog</c>).</summary>
    /// <param name="session">The connection.</param>
    /// <returns>Dispose it to stop.</returns>
    public IDisposable? WatchPackets(IRcSession session)
    {
        if (!Packets || session is null) return null;
        EventHandler<RawPacketEventArgs> handler = (_, e) => Packet(e.PacketId, e.Payload);
        session.RawPacketReceived += handler;
        return new RemoteControl.Ui.Shell.Unsubscriber(() => session.RawPacketReceived -= handler);
    }

    /// <summary>
    /// Copies the program log's new lines to standard error while <c>--debug</c> is on (the log has no listener: its lines are read
    /// four times a second).
    /// </summary>
    /// <param name="log">The program log.</param>
    public void Follow(AppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!Debug) return;
        lock (_gate)
        {
            if (_disposed || _tail is not null) return;
            _log = log;
            IReadOnlyList<string> existing = log.Snapshot();
            _lastLine = existing.Count > 0 ? existing[^1] : null;
            _tail = new Timer(_ => Pump(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        }
    }

    /// <summary>Writes the log lines that are new since the last call.</summary>
    internal void Pump()
    {
        try
        {
            AppLog? log;
            lock (_gate) log = _log;
            if (log is null) return;
            IReadOnlyList<string> lines = log.Snapshot();
            int start = 0;
            string? last;
            lock (_gate) last = _lastLine;
            if (last is not null)
            {
                for (int index = lines.Count - 1; index >= 0; index--)
                {
                    if (!string.Equals(lines[index], last, StringComparison.Ordinal)) continue;
                    start = index + 1;
                    break;
                }
            }
            for (int index = start; index < lines.Count; index++) Write(FormatLine(lines[index]));
            if (lines.Count > 0) lock (_gate) _lastLine = lines[^1];
        }
        catch (Exception)
        {
            // Debug output must never fail the program.
        }
    }

    /// <summary>Stops following the log.</summary>
    public void Dispose()
    {
        Timer? tail;
        lock (_gate)
        {
            _disposed = true;
            tail = _tail;
            _tail = null;
        }
        tail?.Dispose();
        if (tail is not null) Pump();
    }

    private void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                _writer.Write(line);
                _writer.Write('\n');
                _writer.Flush();
            }
        }
        catch (Exception)
        {
            // Standard error may be closed.
        }
    }
}
