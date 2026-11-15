using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteControl.Ui.Players;

/// <summary>The texts of private messages: the label lines the server adds, HTML shown as safe text, how a player is named.</summary>
internal static partial class PmText
{
    /// <summary>
    /// Removes the lines that are only <c>Private message:</c> (any case, surrounded by spaces) and the trailing spaces of every line
    /// (<c>stripPMLabel</c>, <c>TPlayerList.cpp:198-220</c>).
    /// </summary>
    /// <param name="value">The message.</param>
    /// <returns>The message without the label lines.</returns>
    public static string StripLabel(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var output = new StringBuilder(value.Length);
        bool first = true;
        foreach (string raw in value.Split('\n'))
        {
            string line = raw.TrimEnd('\r', ' ', '\t');
            if (string.Equals(line.TrimStart(' ', '\t'), "private message:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!first) output.Append('\n');
            output.Append(line);
            first = false;
        }
        return output.ToString();
    }

    /// <summary>
    /// The text a PM window shows for a message. With <paramref name="noHtml"/> (option <c>nohtmlinpms</c>) the message is shown
    /// exactly as it was sent, tags included. Otherwise simple HTML is read as text: <c>&lt;br&gt;</c> and <c>&lt;p&gt;</c> break the
    /// line, other tags are dropped, entities are decoded, and an image becomes <c>[image: name]</c> (or nothing with
    /// <paramref name="noImages"/>, option <c>nohtmlimages</c>). Nothing is ever loaded: the result is plain text.
    /// </summary>
    /// <param name="message">The message (label lines already removed).</param>
    /// <param name="noHtml">Whether HTML is shown as typed.</param>
    /// <param name="noImages">Whether images are left out.</param>
    /// <returns>The text.</returns>
    public static string ToDisplay(string? message, bool noHtml, bool noImages)
    {
        string text = message ?? string.Empty;
        if (noHtml || text.IndexOf('<') < 0 && text.IndexOf('&') < 0) return text;
        text = ImageTag().Replace(text, match =>
        {
            if (noImages) return string.Empty;
            string source = match.Groups["src"].Success ? match.Groups["src"].Value : string.Empty;
            string name = source.Length == 0 ? string.Empty : source[(source.LastIndexOfAny(['/', '\\']) + 1)..];
            return name.Length == 0 ? "[image]" : "[image: " + name + "]";
        });
        text = BreakTag().Replace(text, "\n");
        text = AnyTag().Replace(text, string.Empty);
        return WebUtility.HtmlDecode(text);
    }

    /// <summary>How a PM partner is named in titles and alerts: the account, or <c>nick (account)</c> when the nickname differs.</summary>
    /// <param name="account">The account.</param>
    /// <param name="nick">The nickname.</param>
    /// <returns>The text.</returns>
    public static string Identity(string account, string nick)
    {
        string display = string.IsNullOrEmpty(nick) ? account : nick;
        return string.IsNullOrEmpty(nick) || nick == account ? display : display + " (" + account + ")";
    }

    [GeneratedRegex("<img\\b(?:[^>]*?\\bsrc\\s*=\\s*[\"']?(?<src>[^\"'\\s>]*))?[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImageTag();

    [GeneratedRegex("<\\s*(?:br|/?p|/div)\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex("</?[a-zA-Z][^<>]*>")]
    private static partial Regex AnyTag();
}
