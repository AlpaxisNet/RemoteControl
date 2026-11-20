namespace RemoteControl.Sync.ImportMissing;

/// <summary>The waits of the Import Missing feature; tests shorten them.</summary>
public sealed class MissingFilesImporterOptions
{
    /// <summary>How long to wait for the server's answer to the request for the missing files; a server that does not answer does not know it (default 60 seconds).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait for the server's message 67 about one uploaded file (default 30 seconds).</summary>
    public TimeSpan UploadAnswerTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the account's folder rights (default 15 seconds).</summary>
    public TimeSpan RightsTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
