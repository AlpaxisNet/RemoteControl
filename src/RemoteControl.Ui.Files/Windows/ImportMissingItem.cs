using System.Globalization;
using System.Windows.Input;
using RemoteControl.Protocol.Session;
using RemoteControl.Sync.ImportMissing;

namespace RemoteControl.Ui.Files;

/// <summary>A row of the Import Missing list: one missing file of the server, the local file chosen for it, and what became of it.</summary>
internal sealed class ImportMissingItem : Observable
{
    private readonly string _root;
    private ImportRow _row;
    private bool _ticked;
    private string? _progress;
    private ImportResult? _result;

    /// <summary>Creates a row.</summary>
    /// <param name="row">The planned row.</param>
    /// <param name="root">The folder that was searched; the local file is shown as a path below it.</param>
    /// <param name="chooseOther">What "Other file..." does.</param>
    public ImportMissingItem(ImportRow row, string root, Func<ImportMissingItem, Task> chooseOther)
    {
        _row = row;
        _root = root;
        _ticked = row.TickedByDefault;
        ChooseOther = new AsyncCommand(() => chooseOther(this), "ImportMissing.ChooseOther");
    }

    /// <summary>Raised when the tick box was changed.</summary>
    public event EventHandler? TickedChanged;

    /// <summary>Chooses another local file of the same name.</summary>
    public ICommand ChooseOther { get; }

    /// <summary>The planned row.</summary>
    public ImportRow Row => _row;

    /// <summary>The result of the upload, once there is one.</summary>
    public ImportResult? Result => _result;

    /// <summary>Whether the row will be uploaded.</summary>
    public bool Ticked
    {
        get => _ticked;
        set
        {
            if (value && !CanTick) value = false;
            if (!Set(ref _ticked, value)) return;
            Raise(nameof(Status));
            TickedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether the row may be ticked: it has everything an upload needs and has not been uploaded.</summary>
    public bool CanTick => _row.Problem == ImportProblem.None && _result?.Outcome != ImportOutcome.Uploaded;

    /// <summary>The file's name as the server looks it up.</summary>
    public string Name => _row.Missing.Name;

    /// <summary>What the file is.</summary>
    public string KindText => _row.Missing.Kind.ToString().ToLowerInvariant();

    /// <summary>Where the file is used and how often.</summary>
    public string UsedText
    {
        get
        {
            int uses = _row.Missing.Uses;
            string count = uses == 1 ? "1 use" : uses.ToString(CultureInfo.InvariantCulture) + " uses";
            string by = _row.Missing.UsedBy;
            string shown = uses > CountOf(by) ? by + "; ..." : by;
            return by.Length == 0 ? count : count + ": " + shown;
        }
    }

    /// <summary>The server's folder, or a word that there is none.</summary>
    public string FolderText => _row.Missing.Folder.Length == 0 ? "(no folder on the server)" : _row.Missing.Folder;

    /// <summary>The local file as a path below the searched folder, with its size.</summary>
    public string LocalText => _row.LocalPath is null ? "(not found)" : RelativePath(_row.LocalPath) + " (" + FormatSize(_row.LocalSize) + ")";

    /// <summary>Whether there are other local files of the name to choose from.</summary>
    public bool HasAlternatives => _row.Alternatives.Count > 0;

    /// <summary>The text on the "Other file..." button.</summary>
    public string AlternativesText => "Other file... (" + (_row.Alternatives.Count + 1).ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>What to tell the admin about the row: why it is not ticked, what is happening to it, or its result.</summary>
    public string Status
    {
        get
        {
            if (_result is not null) return _result.Message;
            if (_progress is not null) return _progress;
            if (_row.Problem != ImportProblem.None) return "not ticked: " + _row.Reason;
            return _ticked ? "ready to upload" : "not ticked";
        }
    }

    /// <summary>The paths of the local files that may be chosen, the current one first, as paths below the searched folder.</summary>
    public IReadOnlyList<string> LocalChoices
    {
        get
        {
            var all = new List<string>();
            if (_row.LocalPath is not null) all.Add(_row.LocalPath);
            all.AddRange(_row.Alternatives);
            return all;
        }
    }

    /// <summary>A local path as shown: below the searched folder, with forward slashes.</summary>
    /// <param name="path">The full path.</param>
    /// <returns>The path below the folder.</returns>
    public string RelativePath(string path) => Path.GetRelativePath(_root, path).Replace('\\', '/');

    /// <summary>Takes a re-planned row (another local file was chosen); the result of an earlier upload is forgotten.</summary>
    /// <param name="row">The row.</param>
    public void Replace(ImportRow row)
    {
        _row = row;
        _result = null;
        _progress = null;
        _ticked = row.TickedByDefault;
        Raise(string.Empty);
        TickedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows that the row is being sent.</summary>
    /// <param name="text">What to show.</param>
    public void ShowProgress(string text)
    {
        _progress = text;
        Raise(nameof(Status));
    }

    /// <summary>Takes the result of the upload. An uploaded row is no longer ticked.</summary>
    /// <param name="result">The result.</param>
    public void SetResult(ImportResult result)
    {
        _result = result;
        _progress = null;
        if (result.Outcome == ImportOutcome.Uploaded) _ticked = false;
        Raise(string.Empty);
        TickedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows that the row was not sent because the run was cancelled.</summary>
    public void ShowNotSent()
    {
        _progress = "not sent: cancelled";
        Raise(nameof(Status));
    }

    /// <summary>A size in bytes in words.</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>Such as <c>1.5 KB</c>.</returns>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
    };

    private static int CountOf(string usedBy) => usedBy.Length == 0 ? 0 : usedBy.Split("; ", StringSplitOptions.RemoveEmptyEntries).Length;
}
