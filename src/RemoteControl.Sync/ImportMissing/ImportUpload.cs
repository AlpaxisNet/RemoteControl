namespace RemoteControl.Sync.ImportMissing;

/// <summary>How the upload of one row ended.</summary>
public enum ImportOutcome
{
    /// <summary>The server said it has the file.</summary>
    Uploaded,

    /// <summary>The server refused the file; <see cref="ImportResult.Message"/> is its reason.</summary>
    Refused,

    /// <summary>The server did not answer in time.</summary>
    NoAnswer,

    /// <summary>The program did not send it (an unsafe path, a file that is not under the chosen folder or cannot be read, a connection that is gone); <see cref="ImportResult.Message"/> says why.</summary>
    Failed,

    /// <summary>The upload was cancelled while this row waited for the server; the file may have arrived.</summary>
    Cancelled,
}

/// <summary>The result of one row.</summary>
/// <param name="Row">The row.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Message">What to show: <c>uploaded</c>, the server's reason, <c>the server did not answer</c>, or why it was not sent.</param>
public sealed record ImportResult(ImportRow Row, ImportOutcome Outcome, string Message);

/// <summary>What is happening to the file of a row.</summary>
/// <param name="Index">The row's place among the rows being uploaded (from 0).</param>
/// <param name="Count">How many rows are being uploaded.</param>
/// <param name="Row">The row.</param>
/// <param name="Sent">Bytes sent so far.</param>
/// <param name="Total">The file's size.</param>
/// <param name="Result">The result when the row is done; <c>null</c> while it is being sent.</param>
public sealed record ImportProgress(int Index, int Count, ImportRow Row, long Sent, long Total, ImportResult? Result);

/// <summary>The end of an upload run.</summary>
/// <param name="Results">The result of every row that was handled, in order.</param>
/// <param name="Cancelled">Whether the run was cancelled before every row was handled.</param>
/// <param name="StoppedBecause">Why the run ended before every row was handled although nobody cancelled it (the server stopped answering),
/// in a sentence; otherwise <c>null</c>.</param>
public sealed record ImportRun(IReadOnlyList<ImportResult> Results, bool Cancelled, string? StoppedBecause = null);
