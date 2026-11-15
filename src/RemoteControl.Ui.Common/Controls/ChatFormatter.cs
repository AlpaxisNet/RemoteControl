using System.Text;
using RemoteControl.Core;

namespace RemoteControl.Ui.Common;

/// <summary>What a part of a chat line is drawn as.</summary>
public enum ChatSpanKind
{
    /// <summary>The time stamp and its space, in the bold colour (not bold).</summary>
    Timestamp,

    /// <summary>The prefix up to and including the first colon, bold in the bold colour.</summary>
    Prefix,

    /// <summary>An <c>#ALERT</c> line, bold in the alert colour.</summary>
    Alert,
}

/// <summary>A coloured part of a chat line (character offsets within <see cref="ChatLine.Text"/>).</summary>
/// <param name="Start">The first character.</param>
/// <param name="Length">The number of characters.</param>
/// <param name="Kind">How it is drawn.</param>
public readonly record struct ChatSpan(int Start, int Length, ChatSpanKind Kind);

/// <summary>A link of a chat line (character offsets within <see cref="ChatLine.Text"/>).</summary>
/// <param name="Start">The first character.</param>
/// <param name="Length">The number of characters.</param>
/// <param name="Url">The URL opened (<c>www.</c> links get <c>https://</c>).</param>
public readonly record struct ChatLink(int Start, int Length, string Url);

/// <summary>An emote of a chat line, replaced by its picture (character offsets within <see cref="ChatLine.Text"/>).</summary>
/// <param name="Start">The first character.</param>
/// <param name="Length">The number of characters.</param>
/// <param name="Emote">The emote.</param>
public readonly record struct ChatEmote(int Start, int Length, Emote Emote);

/// <summary>The alert of a chat line (<c>applyAlertTag</c>, <c>TRemoteFrame.cpp:2653-2664</c>).</summary>
public enum ChatAlertKind
{
    /// <summary>Not an alert line.</summary>
    None,

    /// <summary><c>#ALERT</c>, <c>#ALERTSF</c> or <c>#ALERTFS</c>: urgency (taskbar flash) unless suppressed.</summary>
    Urgent,

    /// <summary><c>#ALERTSP</c>, <c>#ALERTPS</c>, <c>#ALERTF</c> or <c>#ALERTP</c>: urgency and a beep when <c>notificationsounds</c> is on.</summary>
    Sound,
}

/// <summary>One formatted chat line.</summary>
/// <param name="Text">The text shown (time stamp included, alert prefix removed, no line break).</param>
/// <param name="Spans">The coloured parts.</param>
/// <param name="Links">The links.</param>
/// <param name="Emotes">The emotes (empty when not enabled).</param>
/// <param name="Alert">The alert kind.</param>
public sealed record ChatLine(string Text, IReadOnlyList<ChatSpan> Spans, IReadOnlyList<ChatLink> Links, IReadOnlyList<ChatEmote> Emotes, ChatAlertKind Alert)
{
    /// <summary>Whether the line is drawn in the alert colour (it started with <c>#ALERT</c>).</summary>
    public bool IsAlert => Alert != ChatAlertKind.None;
}

/// <summary>How a line is appended to a <see cref="ChatView"/>.</summary>
public sealed record ChatAppendOptions
{
    /// <summary>The default options.</summary>
    public static ChatAppendOptions Default { get; } = new();

    /// <summary>Whether a time stamp is written; <c>null</c> follows the option <c>rctimestamps</c>.</summary>
    public bool? Timestamp { get; init; }

    /// <summary>The time of the time stamp; the current local time when <c>null</c>.</summary>
    public DateTime? Time { get; init; }

    /// <summary>Whether emotes stay text (the C++ help lines).</summary>
    public bool SuppressEmotes { get; init; }

    /// <summary>Whether the urgency of a silent alert is suppressed (the C++ <c>suppressUrgency</c>, used for its own "New PM" alert lines).</summary>
    public bool SuppressUrgency { get; init; }
}

/// <summary>
/// What happened when a line was appended. The view does not flash the taskbar or beep itself: the caller does, when no window of
/// the program is active (as the C++ checked <c>hasActiveRemoteControlWindow</c>).
/// </summary>
/// <param name="Line">The formatted line.</param>
/// <param name="WantsUrgency">Whether the line asks for the taskbar urgency hint.</param>
/// <param name="WantsBeep">Whether the line asks for the alert sound (a sound alert while <c>notificationsounds</c> is on).</param>
/// <param name="Appended">Whether the line was added (false for <c>null</c>).</param>
public sealed record ChatAppendResult(ChatLine Line, bool WantsUrgency, bool WantsBeep, bool Appended = true);

/// <summary>
/// The C++ chat line format (<c>appendChat</c>, <c>applyAlertTag</c>, <c>applyChatUrls</c>, <c>applyEmotes</c>,
/// <c>TRemoteFrame.cpp:1934-1979, 2336-2421, 2653-2664</c>) as pure functions on strings, with character offsets (defect D9).
/// </summary>
public static class ChatFormatter
{
    private static readonly (string Tag, bool Sound)[] AlertTags =
        [("#ALERTSF", false), ("#ALERTFS", false), ("#ALERTSP", true), ("#ALERTPS", true), ("#ALERTF", true), ("#ALERTP", true), ("#ALERT", false)];

    /// <summary>Removes the alert prefix of a message.</summary>
    /// <param name="message">The message as the server sent it.</param>
    /// <param name="display">The message without its alert prefix (the rest, its leading space included, is kept as the C++ kept it).</param>
    /// <returns>The alert kind.</returns>
    public static ChatAlertKind ParseAlert(string message, out string display)
    {
        display = message ?? string.Empty;
        if (!display.StartsWith("#ALERT", StringComparison.Ordinal)) return ChatAlertKind.None;
        foreach ((string tag, bool sound) in AlertTags)
        {
            if (!display.StartsWith(tag, StringComparison.Ordinal)) continue;
            display = display[tag.Length..];
            return sound ? ChatAlertKind.Sound : ChatAlertKind.Urgent;
        }
        return ChatAlertKind.Urgent;
    }

    /// <summary>
    /// Formats a message: the optional time stamp and a space (bold colour), then for an <c>#ALERT</c> line the whole rest in the
    /// alert colour, otherwise the prefix up to and including the first colon (when the colon is not the first character) in bold;
    /// links anywhere after the time stamp; emotes (when <paramref name="emotes"/> is set) in the part after the prefix, never in an alert
    /// line, never overlapping a link.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="timestamp">The time stamp text without its space, or <c>null</c>/empty for none.</param>
    /// <param name="emotes">Whether emotes are found.</param>
    /// <returns>The line.</returns>
    public static ChatLine Format(string? message, string? timestamp, bool emotes)
    {
        string raw = Clean(message ?? string.Empty);
        ChatAlertKind alert = ParseAlert(raw, out string display);
        var text = new StringBuilder(display.Length + 24);
        var spans = new List<ChatSpan>(2);
        if (!string.IsNullOrEmpty(timestamp))
        {
            string stamp = Clean(timestamp) + " ";
            spans.Add(new ChatSpan(0, stamp.Length, ChatSpanKind.Timestamp));
            text.Append(stamp);
        }
        int start = text.Length;
        text.Append(display);
        int emoteStart = start;
        if (alert != ChatAlertKind.None)
        {
            if (display.Length > 0) spans.Add(new ChatSpan(start, display.Length, ChatSpanKind.Alert));
        }
        else
        {
            int colon = display.IndexOf(':');
            if (colon > 0)
            {
                spans.Add(new ChatSpan(start, colon + 1, ChatSpanKind.Prefix));
                emoteStart = start + colon + 1;
            }
        }
        string full = text.ToString();
        IReadOnlyList<ChatLink> links = FindUrls(full, start);
        IReadOnlyList<ChatEmote> found = emotes && alert == ChatAlertKind.None ? FindEmotes(full, emoteStart, links) : Array.Empty<ChatEmote>();
        return new ChatLine(full, spans, links, found, alert);
    }

    /// <summary>
    /// Formats a message that may hold line breaks, as the C++ inserted it into its text buffer: the first line as
    /// <see cref="Format"/> does; each further line plain (in the alert colour for an alert message), with its links and emotes.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="timestamp">The time stamp text without its space, or <c>null</c>/empty for none.</param>
    /// <param name="emotes">Whether emotes are found.</param>
    /// <returns>One line per line of the message (at least one).</returns>
    public static IReadOnlyList<ChatLine> FormatLines(string? message, string? timestamp, bool emotes)
    {
        string value = message ?? string.Empty;
        if (value.IndexOf('\n') < 0) return [Format(value, timestamp, emotes)];
        string[] parts = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lines = new List<ChatLine>(parts.Length) { Format(parts[0], timestamp, emotes) };
        ChatAlertKind alert = lines[0].Alert;
        for (int index = 1; index < parts.Length; index++)
        {
            string text = Clean(parts[index]);
            IReadOnlyList<ChatSpan> spans = alert != ChatAlertKind.None && text.Length > 0 ? [new ChatSpan(0, text.Length, ChatSpanKind.Alert)] : Array.Empty<ChatSpan>();
            IReadOnlyList<ChatLink> links = FindUrls(text);
            IReadOnlyList<ChatEmote> found = emotes && alert == ChatAlertKind.None ? FindEmotes(text, 0, links) : Array.Empty<ChatEmote>();
            lines.Add(new ChatLine(text, spans, links, found, alert));
        }
        return lines;
    }

    /// <summary>
    /// Finds the links of a text as <c>applyChatUrls</c> did: <c>http://</c>, <c>https://</c> or <c>www.</c> not preceded by a letter,
    /// digit or underscore; the link ends at white space, <c>&lt;</c>, <c>&gt;</c>, <c>"</c> or <c>'</c>; trailing <c>.,;:!)</c> are not part of it.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="start">Where to start looking.</param>
    /// <returns>The links in order.</returns>
    public static IReadOnlyList<ChatLink> FindUrls(string text, int start = 0)
    {
        List<ChatLink>? links = null;
        int position = Math.Max(0, start);
        while (position < text.Length)
        {
            if (!StartsUrl(text, position) || position > 0 && (char.IsLetterOrDigit(text[position - 1]) || text[position - 1] == '_'))
            {
                position++;
                continue;
            }
            int finish = position;
            while (finish < text.Length && !char.IsWhiteSpace(text[finish]) && text[finish] is not ('<' or '>' or '"' or '\'')) finish++;
            while (finish > position && text[finish - 1] is '.' or ',' or ';' or ':' or '!' or ')') finish--;
            if (finish == position)
            {
                position++;
                continue;
            }
            string url = text[position..finish];
            string target = url.StartsWith("www.", StringComparison.Ordinal) ? "https://" + url : url;
            (links ??= new List<ChatLink>()).Add(new ChatLink(position, finish - position, target));
            position = finish;
        }
        return links ?? (IReadOnlyList<ChatLink>)Array.Empty<ChatLink>();
    }

    /// <summary>Finds the emotes (<see cref="AssetStore.Emotes"/>) in a text from <paramref name="start"/>, by character offset, skipping links.</summary>
    /// <param name="text">The text.</param>
    /// <param name="start">Where to start looking.</param>
    /// <param name="links">Links the emotes must not overlap.</param>
    /// <returns>The emotes in text order.</returns>
    public static IReadOnlyList<ChatEmote> FindEmotes(string text, int start, IReadOnlyList<ChatLink>? links = null)
    {
        List<ChatEmote>? found = null;
        foreach (Emote emote in AssetStore.Emotes)
        {
            int position = text.IndexOf(emote.Text, Math.Max(0, start), StringComparison.Ordinal);
            while (position >= 0)
            {
                int end = position + emote.Text.Length;
                bool free = true;
                if (links is not null)
                    foreach (ChatLink link in links)
                        if (position < link.Start + link.Length && end > link.Start) { free = false; break; }
                if (free && found is not null)
                    foreach (ChatEmote other in found)
                        if (position < other.Start + other.Length && end > other.Start) { free = false; break; }
                if (free) (found ??= new List<ChatEmote>()).Add(new ChatEmote(position, emote.Text.Length, emote));
                position = text.IndexOf(emote.Text, end, StringComparison.Ordinal);
            }
        }
        if (found is null) return Array.Empty<ChatEmote>();
        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        return found;
    }

    /// <summary>The time stamp of the options at <paramref name="time"/>, or <c>null</c> when <c>rctimestamps</c> is off.</summary>
    /// <param name="options">The options.</param>
    /// <param name="time">The time.</param>
    /// <returns>The time stamp text.</returns>
    public static string? Timestamp(RcOptions options, DateTime time) => options.RcTimestamps ? TimestampFormat.Format(options.TimestampFormat, time) : null;

    private static bool StartsUrl(string text, int index) =>
        string.CompareOrdinal(text, index, "http://", 0, 7) == 0 || string.CompareOrdinal(text, index, "https://", 0, 8) == 0 || string.CompareOrdinal(text, index, "www.", 0, 4) == 0;

    /// <summary>One chat line is one line: line breaks become spaces, other control characters (but tabs) are dropped.</summary>
    private static string Clean(string value)
    {
        bool clean = true;
        foreach (char character in value)
            if (char.IsControl(character) && character != '\t') { clean = false; break; }
        if (clean) return value;
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character is '\r' or '\n') builder.Append(' ');
            else if (!char.IsControl(character) || character == '\t') builder.Append(character);
        }
        return builder.ToString();
    }
}
