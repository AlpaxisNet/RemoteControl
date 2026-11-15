using Avalonia.Controls;
using RemoteControl.App.Hotkeys;
using RemoteControl.App.Mcp;
using RemoteControl.App.Platform;
using RemoteControl.App.Tray;
using RemoteControl.Extensions;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Shell;
using ScriptKind = RemoteControl.Ui.Shell.ScriptKind;

namespace RemoteControl.App.Tests;

/// <summary>A desktop that records what the program asks of it.</summary>
internal sealed class FakeDesktop : IDesktop
{
    public List<Window> OpenWindows { get; } = [];

    public IReadOnlyList<Window> Windows => OpenWindows.Count > 0 ? OpenWindows : RemoteControl.Ui.Common.RcWindow.OpenWindows;

    public bool Active { get; set; }

    public bool AnyWindowActive => Active;

    public List<Window> Flashed { get; } = [];

    public int Beeps { get; private set; }

    public int Shutdowns { get; private set; }

    public void Flash(Window window) => Flashed.Add(window);

    public void Beep() => Beeps++;

    public void Shutdown() => Shutdowns++;
}

/// <summary>Start windows that record the calls.</summary>
internal sealed class FakeStartWindows : IStartWindows
{
    public List<string> Calls { get; } = [];

    public bool IsLoginVisible { get; set; } = true;

    public string SelectedAccount { get; set; } = "staff";

    public string Nickname { get; set; } = "Staff";

    public bool HasPassword { get; set; } = true;

    public List<string> ServerNames { get; } = [];

    public IReadOnlyList<string> Servers => ServerNames;

    public ConnectResult SubmitResult { get; set; } = ConnectResult.Ok;

    public Func<string, Task<ConnectResult>>? Connect { get; set; }

    public SessionContext? LastSwitch { get; private set; }

    public void ShowLogin()
    {
        Calls.Add("ShowLogin");
        IsLoginVisible = true;
    }

    public void HideLogin()
    {
        Calls.Add("HideLogin");
        IsLoginVisible = false;
    }

    public void ShowServerList(SessionContext? switchFor)
    {
        Calls.Add("ShowServerList");
        LastSwitch = switchFor;
    }

    public void ShowListServerSettings(Window? owner) => Calls.Add("ShowListServerSettings");

    public Task<ConnectResult> SubmitLoginAsync(string account, string nickname)
    {
        Calls.Add("Submit:" + account + ":" + nickname);
        return Task.FromResult(SubmitResult);
    }

    public Task<ConnectResult> ConnectAsync(string serverName)
    {
        Calls.Add("Connect:" + serverName);
        return Connect?.Invoke(serverName) ?? Task.FromResult(ConnectResult.Failed("Named server was not found"));
    }
}

/// <summary>Timers that fire when the test says so.</summary>
internal sealed class FakeTimers : IUiTimers
{
    public List<FakeTimer> Timers { get; } = [];

    public IDisposable After(TimeSpan delay, Action action) => Add(delay, action, false);

    public IDisposable Every(TimeSpan interval, Action action) => Add(interval, action, true);

    /// <summary>Fires every live timer with this interval once.</summary>
    public int Fire(TimeSpan interval)
    {
        int fired = 0;
        foreach (FakeTimer timer in Timers.Where(timer => timer.Interval == interval && !timer.Stopped).ToArray())
        {
            if (!timer.Repeat) timer.Stopped = true;
            timer.Action();
            fired++;
        }
        return fired;
    }

    public int Live(TimeSpan interval) => Timers.Count(timer => timer.Interval == interval && !timer.Stopped);

    private FakeTimer Add(TimeSpan interval, Action action, bool repeat)
    {
        var timer = new FakeTimer(interval, action, repeat);
        Timers.Add(timer);
        return timer;
    }

    internal sealed class FakeTimer(TimeSpan interval, Action action, bool repeat) : IDisposable
    {
        public TimeSpan Interval { get; } = interval;

        public Action Action { get; } = action;

        public bool Repeat { get; } = repeat;

        public bool Stopped { get; set; }

        public void Dispose() => Stopped = true;
    }
}

/// <summary>A tray icon that shows nothing and records what it was told.</summary>
internal sealed class FakeTrayIcon : ITrayIcon
{
    public bool Shown { get; set; } = true;

    public bool IsShown => Shown && !Disposed;

    public string ToolTip { get; private set; } = string.Empty;

    public bool Alert { get; private set; }

    public List<bool> AlertChanges { get; } = [];

    public IReadOnlyList<TrayMenuEntry> Menu { get; private set; } = Array.Empty<TrayMenuEntry>();

    public int MenuBuilds { get; private set; }

    public Action<TrayMenuEntry>? Invoke { get; private set; }

    public bool Disposed { get; private set; }

    public event EventHandler? Clicked;

    public void Click() => Clicked?.Invoke(this, EventArgs.Empty);

    public void SetToolTip(string text) => ToolTip = text;

    public void SetAlert(bool alert)
    {
        Alert = alert;
        AlertChanges.Add(alert);
    }

    public void SetMenu(IReadOnlyList<TrayMenuEntry> entries, Action<TrayMenuEntry> invoke)
    {
        Menu = entries;
        Invoke = invoke;
        MenuBuilds++;
    }

    /// <summary>Chooses the entry with this label (in a submenu too).</summary>
    public void Choose(string label)
    {
        TrayMenuEntry entry = Find(Menu, label) ?? throw new InvalidOperationException("No tray entry " + label);
        Invoke!(entry);
    }

    public static TrayMenuEntry? Find(IReadOnlyList<TrayMenuEntry> entries, string label)
    {
        foreach (TrayMenuEntry entry in entries)
        {
            if (entry.Label == label && entry.Command != TrayCommand.Submenu) return entry;
            if (Find(entry.Items, label) is { } inner) return inner;
        }
        return null;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>A hotkey backend that grabs nothing.</summary>
internal sealed class FakeHotkeyBackend : IHotkeyBackend
{
    public string? UnsupportedReason { get; set; }

    public List<Hotkey?> Requests { get; } = [];

    public Func<Hotkey?, HotkeyResult> Result { get; set; } = _ => HotkeyResult.Ok;

    public bool Disposed { get; private set; }

    public event EventHandler? Pressed;

    public void Press() => Pressed?.Invoke(this, EventArgs.Empty);

    public Task<HotkeyResult> SetAsync(Hotkey? hotkey)
    {
        Requests.Add(hotkey);
        return Task.FromResult(Result(hotkey));
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Approvals answered by the test.</summary>
internal sealed class FakeApprovals : IMcpApprovals
{
    public bool Allow { get; set; }

    public McpApproval ScriptAnswer { get; set; } = McpApproval.Deny;

    public List<string> Questions { get; } = [];

    public Task<bool> ApproveAsync(string description)
    {
        Questions.Add(description);
        return Task.FromResult(Allow);
    }

    public Task<McpApproval> ApproveScriptAsync(McpScriptAction action, string editor, string scriptType)
    {
        Questions.Add(action + ":" + editor + ":" + scriptType);
        return Task.FromResult(ScriptAnswer);
    }
}

/// <summary>Script editors of a session, kept in memory.</summary>
internal sealed class FakeEditors : IScriptEditors
{
    public List<ScriptEditorSnapshot> Open { get; } = [];

    public List<string> Saved { get; } = [];

    public bool SaveResult { get; set; } = true;

    public IReadOnlyList<ScriptEditorSnapshot> Editors => Open;

    public event EventHandler? Changed;

    public IReadOnlyList<string> UnsavedTitles => Open.Where(editor => editor.IsModified).Select(editor => editor.Title).ToArray();

    public void Add(string title, string text, bool modified = false, ScriptKind kind = ScriptKind.Weapon) =>
        Open.Add(new ScriptEditorSnapshot(title, kind, title, false, modified, text, string.Empty));

    public bool Update(string title, Func<string, string?> edit)
    {
        int index = Open.FindIndex(editor => editor.Title == title);
        if (index < 0) return false;
        string? next = edit(Open[index].Text);
        if (next is null) return false;
        Open[index] = Open[index] with { Text = next, IsModified = true };
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public Task<bool> SaveAsync(string title)
    {
        Saved.Add(title);
        return Task.FromResult(SaveResult && Open.Any(editor => editor.Title == title));
    }
}
