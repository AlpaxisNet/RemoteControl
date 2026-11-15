using Avalonia.Controls;
using RemoteControl.Protocol.Session;
using RemoteControl.Scripting;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Scripts;

/// <summary>
/// The script group's part of a session: the open editor windows and their unsaved-change tracking (<see cref="IScriptEditors"/>,
/// offered as long as the session lives), the remembered editor windows (<c>script-windows.txt</c>), and opening them again when the
/// NC connects (C++ <c>restoreOpenEditors</c>). Used on the UI thread.
/// </summary>
internal sealed class ScriptsSession : IScriptEditors
{
    private readonly SessionContext _context;
    private readonly List<EditorWindow> _windows = [];
    private bool _restored;

    private ScriptsSession(SessionContext context)
    {
        _context = context;
        Records = new ScriptWindowRecords(Path.Combine(context.App.Paths.UserDirectory, ScriptWindowRecords.FileName));
    }

    /// <summary>Which editors have changes the server has not confirmed.</summary>
    public ScriptEditorTracker Tracker { get; } = new();

    /// <summary>The remembered editor windows.</summary>
    public ScriptWindowRecords Records { get; }

    /// <summary>The open editor windows.</summary>
    public IReadOnlyList<EditorWindow> Windows => _windows;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>The session's script part, started when there is none yet.</summary>
    public static ScriptsSession For(SessionContext context) => context.Services.Get<IScriptEditors>() as ScriptsSession ?? Start(context);

    /// <summary>Starts the script part of a session (safe to call again: the existing one is returned).</summary>
    public static ScriptsSession Start(SessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Services.Get<IScriptEditors>() is ScriptsSession existing) return existing;
        var session = new ScriptsSession(context);
        IDisposable offer = context.Services.Provide<IScriptEditors>(session);
        IDisposable attachment = context.Attach(connection =>
        {
            EventHandler connected = (_, _) => UiSafe.Run(session.RestoreEditors, "restore script editors");
            connection.NcConnected += connected;
            if (connection.IsNcAuthenticated) Avalonia.Threading.Dispatcher.UIThread.Post(() => UiSafe.Run(session.RestoreEditors, "restore script editors"));
            return new Unsubscriber(() => connection.NcConnected -= connected);
        });
        context.Ended += (_, _) =>
        {
            attachment.Dispose();
            offer.Dispose();
        };
        return session;
    }

    /// <summary>Shows the editor of a target (one window per script).</summary>
    public static Window? Open(SessionContext context, EditorTarget target)
    {
        For(context);
        return context.Windows.ShowSingle(target.Key, () => new EditorWindow(context, target));
    }

    /// <inheritdoc />
    public IReadOnlyList<ScriptEditorSnapshot> Editors => _windows.Select(window => window.Snapshot()).OfType<ScriptEditorSnapshot>().ToArray();

    /// <inheritdoc />
    public IReadOnlyList<string> UnsavedTitles => _windows.Where(window => window.IsDirty).Select(window => window.Title ?? string.Empty).ToArray();

    /// <inheritdoc />
    public bool Update(string title, Func<string, string?> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        EditorWindow? window = Find(title);
        return window is not null && window.Update(edit);
    }

    /// <inheritdoc />
    public Task<bool> SaveAsync(string title) => Find(title) is { } window ? window.SaveAsync() : Task.FromResult(false);

    public void Register(EditorWindow window)
    {
        _windows.Add(window);
        RaiseChanged();
    }

    public void Unregister(EditorWindow window)
    {
        if (_windows.Remove(window)) RaiseChanged();
    }

    public void RaiseChanged() => UiSafe.Run(() => Changed?.Invoke(this, EventArgs.Empty), "script editors changed");

    private EditorWindow? Find(string title) => _windows.FirstOrDefault(window => window.Target.Kind is not null && string.Equals(window.Title, title, StringComparison.Ordinal));

    private void RestoreEditors()
    {
        if (_restored || _context.IsEnded || !_context.Session.IsNcAuthenticated) return;
        _restored = true;
        string key = ScriptWindowRecords.SessionKey(_context.ServerName, _context.Account);
        foreach (ScriptWindowRecord record in Records.Load().Where(item => item.Session == key))
        {
            EditorTarget? target = record.Type switch
            {
                "weapon" => new WeaponTarget(record.Name),
                "class" => new ClassTarget(record.Name),
                _ => null,
            };
            if (target is not null) Open(_context, target);
        }
    }
}
