namespace RemoteControl.Scripting;

/// <summary>The lines that differ from the uploaded text, for the changed-line tint (C++ <c>updateChangedLines</c>).</summary>
/// <param name="FirstLine">First changed line of the current text (0-based).</param>
/// <param name="EndLine">Line after the last changed one; equal to the line count when the range runs to the end.</param>
public readonly record struct ChangedLines(int FirstLine, int EndLine)
{
    /// <summary>
    /// Compares two texts by lines (common prefix and suffix) and returns the middle range of the current text, at least one
    /// line; null when the texts have the same lines.
    /// </summary>
    /// <param name="original">The text last confirmed by the server.</param>
    /// <param name="current">The editor text.</param>
    /// <returns>The range or null.</returns>
    public static ChangedLines? Compute(string? original, string? current)
    {
        string[] before = SplitLines(original ?? string.Empty);
        string[] after = SplitLines(current ?? string.Empty);
        int first = 0;
        while (first < before.Length && first < after.Length && before[first] == after[first]) ++first;
        int beforeLast = before.Length;
        int afterLast = after.Length;
        while (beforeLast > first && afterLast > first && before[beforeLast - 1] == after[afterLast - 1]) { --beforeLast; --afterLast; }
        if (first == beforeLast && first == afterLast) return null;
        int start = Math.Min(first, after.Length - 1);
        int end = Math.Max(start + 1, afterLast);
        return new ChangedLines(start, Math.Min(end, after.Length));
    }

    /// <summary>Splits at '\n' ("\r\n" and '\r' count as one break too); a text ending in a break has an empty last line.</summary>
    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}

/// <summary>
/// One tracked script editor. Its text is clean only when it equals the text the server last confirmed; uploading alone does
/// not make it clean (the C++ marked editors saved even when nothing was sent, review defect D-01).
/// </summary>
public sealed class TrackedEditor
{
    private readonly ScriptEditorTracker owner;
    private string confirmedText;
    private string currentText;
    private object? connection;
    private bool connectionDetached;
    private long uploadSequence;
    private long confirmedSequence;

    internal TrackedEditor(ScriptEditorTracker owner, string name, string originalText, object? connection)
    {
        this.owner = owner;
        Name = name;
        confirmedText = originalText;
        currentText = originalText;
        this.connection = connection;
    }

    /// <summary>The name shown in warnings (the window title).</summary>
    public string Name { get; private set; }

    /// <summary>The text the server last confirmed (or the text as opened).</summary>
    public string ConfirmedText { get { lock (owner.Gate) return confirmedText; } }

    /// <summary>The editor's current text as last reported.</summary>
    public string CurrentText { get { lock (owner.Gate) return currentText; } }

    /// <summary>Whether the editor has changes the server has not confirmed.</summary>
    public bool IsDirty { get { lock (owner.Gate) return !string.Equals(confirmedText, currentText, StringComparison.Ordinal); } }

    /// <summary>The connection uploads go to, or null while it is detached (disconnected).</summary>
    public object? Connection { get { lock (owner.Gate) return connectionDetached ? null : connection; } }

    /// <summary>Whether the editor has been removed from the tracker.</summary>
    public bool IsClosed { get; internal set; }

    /// <summary>The changed lines of the current text against the confirmed text, or null.</summary>
    public ChangedLines? ChangedLines
    {
        get
        {
            string confirmed, current;
            lock (owner.Gate) { confirmed = confirmedText; current = currentText; }
            return Scripting.ChangedLines.Compute(confirmed, current);
        }
    }

    /// <summary>Records the editor's text after a change.</summary>
    /// <param name="text">The new text.</param>
    public void UpdateText(string? text)
    {
        bool changed;
        lock (owner.Gate)
        {
            bool wasDirty = !string.Equals(confirmedText, currentText, StringComparison.Ordinal);
            currentText = text ?? string.Empty;
            changed = wasDirty != !string.Equals(confirmedText, currentText, StringComparison.Ordinal);
        }
        if (changed) owner.RaiseDirtyChanged(this);
    }

    /// <summary>Renames the editor (for example when the window title changes).</summary>
    /// <param name="name">The new name.</param>
    public void Rename(string name)
    {
        lock (owner.Gate) Name = name ?? string.Empty;
    }

    /// <summary>
    /// Starts an upload of the current text. Returns null when the editor has no live connection: then nothing may be sent and
    /// the editor stays dirty.
    /// </summary>
    /// <returns>The pending upload, which carries the exact text sent.</returns>
    public PendingUpload? BeginUpload()
    {
        lock (owner.Gate)
        {
            if (IsClosed || connectionDetached || connection == null) return null;
            return new PendingUpload(this, currentText, connection, ++uploadSequence);
        }
    }

    /// <summary>
    /// Marks the text of <paramref name="upload"/> as confirmed by the server. Text typed since the upload started stays dirty.
    /// A confirmation for an older upload than the last one confirmed is ignored.
    /// </summary>
    /// <param name="upload">The upload the server confirmed.</param>
    /// <returns>True when the confirmation was applied.</returns>
    public bool ConfirmUpload(PendingUpload upload)
    {
        if (upload == null || !ReferenceEquals(upload.Editor, this)) return false;
        bool changed;
        lock (owner.Gate)
        {
            if (IsClosed || upload.Sequence < confirmedSequence) return false;
            confirmedSequence = upload.Sequence;
            bool wasDirty = !string.Equals(confirmedText, currentText, StringComparison.Ordinal);
            confirmedText = upload.Text;
            changed = wasDirty != !string.Equals(confirmedText, currentText, StringComparison.Ordinal);
        }
        if (changed) owner.RaiseDirtyChanged(this);
        return true;
    }

    /// <summary>Replaces the confirmed text with what the server sent (for example when the script is reloaded).</summary>
    /// <param name="serverText">The server's text.</param>
    /// <param name="replaceCurrent">Also set the current text (the editor shows the server text now).</param>
    public void SetServerText(string? serverText, bool replaceCurrent)
    {
        bool changed;
        lock (owner.Gate)
        {
            bool wasDirty = !string.Equals(confirmedText, currentText, StringComparison.Ordinal);
            confirmedText = serverText ?? string.Empty;
            if (replaceCurrent) currentText = confirmedText;
            changed = wasDirty != !string.Equals(confirmedText, currentText, StringComparison.Ordinal);
        }
        if (changed) owner.RaiseDirtyChanged(this);
    }

    internal bool IsBoundTo(object other)
    {
        lock (owner.Gate) return ReferenceEquals(connection, other);
    }

    internal void Detach(object disconnected)
    {
        if (ReferenceEquals(connection, disconnected)) connectionDetached = true;
    }

    internal void Rebind(object? disconnected, object? replacement)
    {
        if (ReferenceEquals(connection, disconnected)) { connection = replacement; connectionDetached = false; }
    }
}

/// <summary>An upload in flight; pass it back to <see cref="TrackedEditor.ConfirmUpload"/> when the server confirms it.</summary>
public sealed class PendingUpload
{
    internal PendingUpload(TrackedEditor editor, string text, object connection, long sequence)
    {
        Editor = editor;
        Text = text;
        Connection = connection;
        Sequence = sequence;
    }

    /// <summary>The editor.</summary>
    public TrackedEditor Editor { get; }

    /// <summary>The exact text to send.</summary>
    public string Text { get; }

    /// <summary>The connection to send it on.</summary>
    public object Connection { get; }

    internal long Sequence { get; }
}

/// <summary>
/// Which script editors have changes the server has not confirmed (port of <c>TScriptEditorTracking</c>, fixed per review
/// defects D-01 and D-02): windows register their editors, report text changes, start uploads and report confirmations; closing a
/// window or quitting asks <see cref="DirtyEditors"/> and warns. Thread-safe.
/// </summary>
public sealed class ScriptEditorTracker
{
    private readonly List<TrackedEditor> editors = new();

    internal object Gate { get; } = new();

    /// <summary>Raised (on the thread that made the change) when an editor turns dirty or clean.</summary>
    public event EventHandler<TrackedEditor>? DirtyChanged;

    /// <summary>Starts tracking an editor.</summary>
    /// <param name="name">The window title.</param>
    /// <param name="originalText">The text as the server sent it.</param>
    /// <param name="connection">The connection uploads go to (any object identifying it), or null.</param>
    /// <returns>The tracked editor.</returns>
    public TrackedEditor Track(string name, string? originalText, object? connection)
    {
        var editor = new TrackedEditor(this, name ?? string.Empty, originalText ?? string.Empty, connection);
        lock (Gate) editors.Add(editor);
        return editor;
    }

    /// <summary>Stops tracking an editor (its window closed).</summary>
    /// <param name="editor">The editor.</param>
    public void Untrack(TrackedEditor editor)
    {
        if (editor == null) return;
        lock (Gate)
        {
            editors.Remove(editor);
            editor.IsClosed = true;
        }
    }

    /// <summary>All tracked editors, sorted by name.</summary>
    public IReadOnlyList<TrackedEditor> Editors
    {
        get { lock (Gate) return editors.OrderBy(editor => editor.Name, StringComparer.Ordinal).ToArray(); }
    }

    /// <summary>The editors with unconfirmed changes, sorted by name.</summary>
    public IReadOnlyList<TrackedEditor> DirtyEditors => Editors.Where(editor => editor.IsDirty).ToArray();

    /// <summary>The dirty editors that upload to <paramref name="connection"/> (also while it is detached), sorted by name.</summary>
    /// <param name="connection">The connection.</param>
    /// <returns>The editors.</returns>
    public IReadOnlyList<TrackedEditor> DirtyEditorsFor(object connection) =>
        Editors.Where(editor => editor.IsDirty && editor.IsBoundTo(connection)).ToArray();

    /// <summary>Marks every editor of a lost connection as detached: uploads are refused until it is rebound.</summary>
    /// <param name="connection">The lost connection.</param>
    public void DetachConnection(object connection)
    {
        if (connection == null) return;
        lock (Gate) foreach (TrackedEditor editor in editors) editor.Detach(connection);
    }

    /// <summary>Moves every editor of a lost connection to its replacement after a reconnect.</summary>
    /// <param name="disconnected">The old connection.</param>
    /// <param name="replacement">The new connection.</param>
    public void RebindConnection(object? disconnected, object? replacement)
    {
        lock (Gate) foreach (TrackedEditor editor in editors) editor.Rebind(disconnected, replacement);
    }

    internal void RaiseDirtyChanged(TrackedEditor editor) => DirtyChanged?.Invoke(this, editor);
}
