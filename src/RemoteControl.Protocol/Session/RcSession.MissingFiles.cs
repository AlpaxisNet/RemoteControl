using System.Globalization;

namespace RemoteControl.Protocol.Session;

public sealed partial class RcSession
{
    /// <summary>A result is at most this many parts (the server sends 100 rows to a part); more is not an answer of a server that follows the text.</summary>
    internal const int MaxMissingFilesParts = 10000;

    private readonly List<MissingFile> _missingFiles = new();
    private int _missingFilesParts;
    private int _missingFilesNext = 1;

    /// <summary>Forgets a half-received result (a new request, a refusal, a finished result). On the context.</summary>
    private void ResetMissingFiles()
    {
        _missingFiles.Clear();
        _missingFilesParts = 0;
        _missingFilesNext = 1;
    }

    /// <summary>
    /// The <c>missingfiles</c> answers: <c>error,&lt;sentence&gt;</c>, or <c>part,&lt;n&gt;,&lt;of&gt;,&lt;row&gt;...</c>. The parts are put together
    /// here and one event is raised after the last. Whatever does not fit (a part out of order, a count that is no number, a row with too
    /// few values) is dropped with a log line.
    /// </summary>
    private void HandleMissingFilesText(List<string> parts)
    {
        if (parts.Count >= 3 && parts[2] == "error")
        {
            ResetMissingFiles();
            Raise(MissingFilesReceived, new MissingFilesEventArgs(string.Join(", ", parts.Skip(3))));
            return;
        }

        if (parts.Count < 5 || parts[2] != "part"
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            || !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out int total))
        {
            Log("A missingfiles answer was dropped: it is neither an error nor a part with a number and a count.");
            return;
        }

        if (total < 1 || total > MaxMissingFilesParts || number < 1 || number > total)
        {
            Log($"A missingfiles part {number} of {total} was dropped: the numbers are out of range.");
            return;
        }

        if (number == 1) ResetMissingFiles();
        if (number != _missingFilesNext || (_missingFilesParts != 0 && total != _missingFilesParts))
        {
            Log($"A missingfiles part {number} of {total} was dropped: part {_missingFilesNext} of {_missingFilesParts} was expected.");
            return;
        }

        _missingFilesParts = total;
        for (int index = 5; index < parts.Count; index++)
        {
            if (TryParseMissingFileRow(parts[index], out MissingFile? file)) _missingFiles.Add(file);
            else Log("A missingfiles row was dropped: it has too few values, an empty name or a count that is no number.");
        }

        _missingFilesNext = number + 1;
        if (number < total) return;
        var files = new List<MissingFile>(_missingFiles);
        ResetMissingFiles();
        Raise(MissingFilesReceived, new MissingFilesEventArgs(files.AsReadOnly()));
    }

    /// <summary>Reads <c>name TAB kind TAB folder TAB uses TAB usedby</c>; values after the fifth are ignored.</summary>
    internal static bool TryParseMissingFileRow(string row, out MissingFile file)
    {
        file = null!;
        string[] values = row.Split('\t');
        if (values.Length < 5 || values[0].Length == 0) return false;
        if (!int.TryParse(values[3], NumberStyles.None, CultureInfo.InvariantCulture, out int uses)) return false;
        file = new MissingFile(values[0], ParseMissingFileKind(values[1]), values[2], uses, values[4]);
        return true;
    }

    private static MissingFileKind ParseMissingFileKind(string word) => word.Trim().ToLowerInvariant() switch
    {
        "image" => MissingFileKind.Image,
        "head" => MissingFileKind.Head,
        "body" => MissingFileKind.Body,
        "sword" => MissingFileKind.Sword,
        "shield" => MissingFileKind.Shield,
        "hat" => MissingFileKind.Hat,
        "gani" => MissingFileKind.Gani,
        "sound" => MissingFileKind.Sound,
        "level" => MissingFileKind.Level,
        _ => MissingFileKind.Other,
    };
}
