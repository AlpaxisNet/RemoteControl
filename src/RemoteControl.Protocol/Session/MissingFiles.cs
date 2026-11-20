namespace RemoteControl.Protocol.Session;

/// <summary>What a missing file is, as the game server names it (the <c>kind</c> value of an answer to <c>missingfiles</c>).</summary>
public enum MissingFileKind
{
    /// <summary>Any other file, or a kind this program does not know.</summary>
    Other,

    /// <summary>A picture.</summary>
    Image,

    /// <summary>A head picture.</summary>
    Head,

    /// <summary>A body picture.</summary>
    Body,

    /// <summary>A sword picture.</summary>
    Sword,

    /// <summary>A shield picture.</summary>
    Shield,

    /// <summary>A hat picture.</summary>
    Hat,

    /// <summary>An animation (<c>.gani</c>).</summary>
    Gani,

    /// <summary>A sound.</summary>
    Sound,

    /// <summary>A level or map.</summary>
    Level,
}

/// <summary>A file the server's scripts and NPCs name and the server does not have (one row of the answer to <c>missingfiles</c>).</summary>
/// <param name="Name">The file's name as the server looks it up, without a folder.</param>
/// <param name="Kind">What the file is.</param>
/// <param name="Folder">The server folder the file belongs in, as the file browser names folders, with a trailing slash
/// (<c>world/heads/</c>); empty when the server's folder configuration has no line for a file of this name.</param>
/// <param name="Uses">In how many places the file is named.</param>
/// <param name="UsedBy">Up to three of those places, separated by <c>"; "</c> (<c>level a.nw; weapon -Shop</c>).</param>
public sealed record MissingFile(string Name, MissingFileKind Kind, string Folder, int Uses, string UsedBy);

/// <summary>The server's answer to <see cref="IRcSession.RequestMissingFilesAsync"/>: the whole list, or why there is none.</summary>
public sealed class MissingFilesEventArgs : EventArgs
{
    /// <summary>Creates the arguments of a list.</summary>
    /// <param name="files">The files, in the server's order.</param>
    public MissingFilesEventArgs(IReadOnlyList<MissingFile> files)
    {
        Files = files;
        Error = null;
    }

    /// <summary>Creates the arguments of a refusal.</summary>
    /// <param name="error">The server's sentence.</param>
    public MissingFilesEventArgs(string error)
    {
        Files = Array.Empty<MissingFile>();
        Error = error;
    }

    /// <summary>The missing files (empty when <see cref="Error"/> is set, or when nothing is missing).</summary>
    public IReadOnlyList<MissingFile> Files { get; }

    /// <summary>The server's sentence when the request was refused or the search failed (no right, a search is running already); otherwise <c>null</c>.</summary>
    public string? Error { get; }
}

/// <summary>
/// A sign of life of the server in answer to <see cref="IRcSession.RequestMissingFilesAsync"/>, before the result is complete: the
/// server says it is searching, or a part of the result arrived. The first of these ends the wait for "a server that knows the request".
/// </summary>
public sealed class MissingFilesProgressEventArgs : EventArgs
{
    /// <summary>Creates the arguments of <c>missingfiles,searching</c>.</summary>
    public MissingFilesProgressEventArgs()
    {
    }

    /// <summary>Creates the arguments of a part of the result.</summary>
    /// <param name="part">The part's number (from 1).</param>
    /// <param name="parts">How many parts the result has.</param>
    public MissingFilesProgressEventArgs(int part, int parts)
    {
        Part = part;
        Parts = parts;
    }

    /// <summary>Whether this is the server's <c>searching</c> (no part yet).</summary>
    public bool Searching => Parts == 0;

    /// <summary>The number of the part that arrived (from 1); 0 for <c>searching</c>.</summary>
    public int Part { get; }

    /// <summary>How many parts the result has; 0 for <c>searching</c>.</summary>
    public int Parts { get; }
}
