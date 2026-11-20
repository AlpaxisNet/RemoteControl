using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Files;

/// <summary>The files group: the file browser, the "Sync &amp; Git Backups" window, the server backup, Import Missing, and the session's synchronisation behind them.</summary>
public static class FilesFeature
{
    /// <summary>
    /// Registers the group's tools: <see cref="ToolIds.FileBrowser"/> (argument: a folder to go to, as a <see cref="string"/>, or
    /// null; refused with a message while files are being synchronised), <see cref="ToolIds.Sync"/>, <see cref="ToolIds.ServerBackup"/> and
    /// <see cref="ToolIds.ImportMissing"/>.
    /// </summary>
    /// <param name="tools">The program's tool registry.</param>
    public static void Register(ToolRegistry tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        tools.Register(ToolIds.FileBrowser, (context, argument) => FilesSession.For(context)?.ShowFileBrowser(argument as string));
        tools.Register(ToolIds.Sync, (context, _) => FilesSession.For(context)?.ShowSync());
        tools.Register(ToolIds.ServerBackup, (context, _) => FilesSession.For(context)?.ShowServerBackup());
        tools.Register(ToolIds.ImportMissing, (context, _) => FilesSession.For(context)?.ShowImportMissing());
    }

    /// <summary>
    /// Starts the group's part of a session, to be called by the program when a session starts: while the option <c>syncenabled</c> is
    /// on (followed live) the session has a sync engine, attached to the connection now and after every reconnect and disposed when
    /// the session ends, and <see cref="ISyncStatus"/> is offered for the session. Calling it again does nothing; a session that has
    /// ended is ignored.
    /// </summary>
    /// <param name="context">The session.</param>
    public static void Start(SessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = FilesSession.For(context);
    }
}
