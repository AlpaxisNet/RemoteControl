using System.Windows.Input;
using RemoteControl.Sync;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Files;

/// <summary>A command that runs an asynchronous action through <see cref="UiSafe"/>.</summary>
internal sealed class AsyncCommand(Func<Task> action, string context) : ICommand
{
    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => true;

    /// <inheritdoc />
    public void Execute(object? parameter) => UiSafe.Fire(action, context);
}

/// <summary>An item of the sync window's "Changes" page: a conflict, a local edit not uploaded, a pending deletion or a failure.</summary>
internal sealed class SyncItemRow : Observable
{
    private SyncItemStatus _status;

    /// <summary>Creates a row.</summary>
    /// <param name="status">The item's status.</param>
    /// <param name="resolve">Resolves its conflict.</param>
    public SyncItemRow(SyncItemStatus status, Func<string, ConflictResolution, Task> resolve)
    {
        _status = status;
        KeepLocal = new AsyncCommand(() => resolve(Key, ConflictResolution.KeepLocal), "Sync.KeepLocal");
        TakeServer = new AsyncCommand(() => resolve(Key, ConflictResolution.UseServer), "Sync.TakeServer");
    }

    /// <summary>The item key.</summary>
    public string Key => _status.Key;

    /// <summary>The status.</summary>
    public SyncItemStatus Status => _status;

    /// <summary>"Weapon: Name".</summary>
    public string Title => KindText(_status.Kind) + ": " + _status.Name;

    /// <summary>The state and detail.</summary>
    public string Description
    {
        get
        {
            string text = StateText(_status.State);
            if (!string.IsNullOrEmpty(_status.Detail)) text += " - " + _status.Detail;
            if (!string.IsNullOrEmpty(_status.ConflictCopy)) text += " (server copy: " + _status.ConflictCopy + ")";
            return text;
        }
    }

    /// <summary>Whether it is a conflict (the two buttons show).</summary>
    public bool IsConflict => _status.State == SyncItemState.Conflict;

    /// <summary>"Keep local".</summary>
    public ICommand KeepLocal { get; }

    /// <summary>"Take server".</summary>
    public ICommand TakeServer { get; }

    /// <summary>Takes a newer status of the same item.</summary>
    /// <param name="status">The status.</param>
    public void Update(SyncItemStatus status)
    {
        if (status == _status) return;
        _status = status;
        Raise(nameof(Title));
        Raise(nameof(Description));
        Raise(nameof(IsConflict));
    }

    /// <summary>The kind as shown.</summary>
    public static string KindText(SyncItemKind kind) => kind switch
    {
        SyncItemKind.Weapon => "Weapon",
        SyncItemKind.Class => "Class",
        SyncItemKind.Npc => "NPC",
        SyncItemKind.File => "File",
        _ => "Configuration",
    };

    /// <summary>The state as shown.</summary>
    public static string StateText(SyncItemState state) => state switch
    {
        SyncItemState.LocallyModified => "Changed locally, not uploaded",
        SyncItemState.Conflict => "Conflict: changed locally and on the server",
        SyncItemState.PendingDeletion => "Deleted locally; waits for confirmation to delete on the server",
        SyncItemState.MissingLocally => "Missing locally; the server copy is kept",
        SyncItemState.Failed => "Failed",
        _ => "In sync",
    };
}
