namespace RemoteControl.Ui.Session;

/// <summary>One line of the Find Results tab.</summary>
/// <param name="Prefix">The text before the file name (<c>[12:00] </c>), or the whole line when it has no file.</param>
/// <param name="File">The file name shown as a link, or empty.</param>
/// <param name="Rest">The text after the file name (<c>: 1234 bytes,...</c>).</param>
/// <param name="Folder">The folder the file browser opens at for the link, or empty.</param>
internal sealed record FindResultLine(string Prefix, string File, string Rest, string Folder)
{
    /// <summary>Whether the line has a link.</summary>
    public bool HasLink => File.Length > 0;
}

/// <summary>
/// Recognises the server's answer to <c>/find</c> and <c>/finddef</c> (<c>appendFindResult</c>, <c>TRemoteFrame.cpp:2570-2615</c>): a
/// header <c>Game files found (relative to &lt;base&gt;, max ...</c> starts the results, then each <c>[...] &lt;file&gt;: N byte,...</c> line is a
/// result whose file links to its folder.
/// </summary>
internal sealed class FindResultsParser
{
    private const string Header = "Game files found (relative to ";

    /// <summary>The folder the results are relative to.</summary>
    public string Base { get; private set; } = string.Empty;

    /// <summary>Whether results were started (a header was seen or <see cref="Begin"/> called).</summary>
    public bool Active { get; private set; }

    /// <summary>Starts a new result list (the user typed <c>/find</c>).</summary>
    /// <param name="baseFolder">The folder, usually empty until the header arrives.</param>
    public void Begin(string baseFolder)
    {
        Base = baseFolder;
        Active = true;
    }

    /// <summary>Stops collecting (the tab was closed).</summary>
    public void Reset()
    {
        Base = string.Empty;
        Active = false;
    }

    /// <summary>Reads a chat line.</summary>
    /// <param name="message">The line.</param>
    /// <param name="line">The line for the Find Results tab.</param>
    /// <param name="isHeader">Whether the line is a header (a new result list).</param>
    /// <returns>Whether the line belongs to the find results (then it is not shown in the RC chat).</returns>
    public bool TryParse(string message, out FindResultLine? line, out bool isHeader)
    {
        line = null;
        isHeader = false;
        int headerStart = message.IndexOf(Header, StringComparison.Ordinal);
        if (headerStart >= 0)
        {
            int baseStart = headerStart + Header.Length;
            int baseEnd = message.IndexOf(", max", baseStart, StringComparison.Ordinal);
            Base = baseEnd < 0 ? string.Empty : message[baseStart..baseEnd];
            Active = true;
            isHeader = true;
            line = new FindResultLine(message, string.Empty, string.Empty, string.Empty);
            return true;
        }
        if (!Active) return false;
        int close = message.IndexOf("] ", StringComparison.Ordinal);
        int contentStart = message.StartsWith('[') && close >= 0 ? close + 2 : 0;
        int separator = message.IndexOf(':', contentStart);
        int bytes = separator < 0 ? -1 : message.IndexOf(" byte,", separator + 1, StringComparison.Ordinal);
        if (separator < 0 || bytes < 0)
        {
            if (!message.Contains("Also found default files matching this", StringComparison.Ordinal)) return false;
            line = new FindResultLine(message, string.Empty, string.Empty, string.Empty);
            return true;
        }
        string file = message[contentStart..separator];
        if (!file.Contains('/', StringComparison.Ordinal) && !file.Contains('.', StringComparison.Ordinal)) return false;
        string path = Base;
        if (path.Length > 0 && !path.EndsWith('/')) path += "/";
        path += file;
        int slash = path.LastIndexOf('/');
        string folder = slash < 0 ? Base : path[..(slash + 1)];
        line = new FindResultLine(message[..contentStart], file, message[separator..], folder);
        return true;
    }
}
