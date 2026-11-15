using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RemoteControl.Core;
using RemoteControl.Protocol.Session;
using RemoteControl.Scripting;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;

namespace RemoteControl.Ui.Scripts;

/// <summary>
/// The editor window of one text (C++ the script dialogs of <c>TScriptList</c> and <c>TNPCList</c>, <c>TServerTextEditor</c>,
/// <c>TLevelList</c>): asks the server for the text and fills in when it arrives, shows it in a <see cref="ScriptEditorControl"/>,
/// and saves it with Apply or Ctrl+S. Fixed per review defects D-01, D-02, D-04 and D-12: it is clean only after the text went out on
/// a live connection; with the NC down it says so and stays dirty; closing it with changes asks; it never keeps a connection (it uses
/// <see cref="SessionContext.Session"/> when it sends); a text the server sends while there are unsaved changes is offered, not applied.
/// </summary>
internal sealed partial class EditorWindow : RcWindow
{
    private readonly SessionContext _context;
    private readonly EditorTarget _target;
    private readonly ScriptsSession _scripts;
    private readonly ScriptEditorControl _editor;
    private readonly TrackedEditor _tracked;
    private readonly EditorBackup? _backup;
    private readonly DispatcherTimer _tintTimer;
    private readonly DispatcherTimer _geometryTimer;
    private readonly TextBlock _lineStatus;
    private readonly TextBlock _status;
    private readonly Border _infoBar;
    private readonly TextBlock _infoText;
    private readonly TextBox _iconBox;
    private readonly Button _applyButton;
    private readonly StackPanel _topActions;
    private readonly StackPanel _bottomActions;
    private ExternalEditor? _external;
    private bool _awaiting;
    private bool _loaded;
    private bool _saving;
    private bool _closeConfirmed;
    private bool _userClosed;
    private bool _asking;
    private bool _firstBind = true;
    private string? _pendingServerText;

    public EditorWindow() : this(null!, new LevelListTarget())
    {
    }

    public EditorWindow(SessionContext context, EditorTarget target) : base(context?.App)
    {
        AvaloniaXamlLoader.Load(this);
        _context = context!;
        _target = target;
        _scripts = ScriptsSession.For(_context);
        Title = target.BaseTitle + (_context.ServerName.Length == 0 ? string.Empty : target.ServerInParentheses ? " (" + _context.ServerName + ")" : " - " + _context.ServerName);
        (Width, Height) = target.Size;
        _lineStatus = this.FindControl<TextBlock>("LineStatus")!;
        _status = this.FindControl<TextBlock>("StatusText")!;
        _infoBar = this.FindControl<Border>("InfoBar")!;
        _infoText = this.FindControl<TextBlock>("InfoText")!;
        _iconBox = this.FindControl<TextBox>("IconBox")!;
        _applyButton = this.FindControl<Button>("ApplyButton")!;
        _topActions = this.FindControl<StackPanel>("TopActions")!;
        _bottomActions = this.FindControl<StackPanel>("BottomActions")!;
        this.FindControl<DockPanel>("IconRow")!.IsVisible = target.HasIcon;
        _applyButton.Content = target.ApplyLabel;
        _applyButton.IsVisible = !target.ReadOnly;
        this.FindControl<Button>("FormatButton")!.IsVisible = target.HasFormat && !target.ReadOnly;
        this.FindControl<Button>("GoToLineButton")!.IsVisible = target.HasFormat;
        if (target.HasIcon) _iconBox.Text = _context.Session.Weapons.FirstOrDefault(weapon => weapon.Name == target.Name)?.Image ?? string.Empty;

        _editor = new ScriptEditorControl(_context.App, target.Mode)
        {
            Name = "ScriptEditor",
            IsReadOnly = true,
            PlayerSource = () => _context.Session.Players.Where(player => !player.IsChannel).Select(player => new OnlinePlayer(player.Account, player.Nickname, player.Community)).ToArray(),
        };
        this.FindControl<ContentControl>("EditorHost")!.Content = _editor;
        _backup = _context.ServerName.Length > 0 && target.BackupType.Length > 0 ? new EditorBackup(_context.App.Paths.BackupDirectory, _context.ServerName) : null;

        _tracked = _scripts.Tracker.Track(Title, string.Empty, _context);
        _tintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _tintTimer.Tick += (_, _) => { _tintTimer.Stop(); UiSafe.Run(() => _editor.ChangedLines = _tracked.ChangedLines, "changed lines"); };
        _geometryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _geometryTimer.Tick += (_, _) => { _geometryTimer.Stop(); UiSafe.Run(SaveGeometry, "editor geometry"); };
        OnClosedOnce(() => { _tintTimer.Stop(); _geometryTimer.Stop(); });

        _editor.TextChanged += UiSafe.Handler((_, _) => OnTextChanged(), "editor text");
        _editor.CaretLineChanged += UiSafe.Handler((_, _) => _lineStatus.Text = "Line: " + _editor.CaretLine, "editor line");
        _editor.SaveRequested += UiSafe.AsyncHandler(() => SaveAsync(), "editor save");
        _scripts.Tracker.DirtyChanged += OnDirtyChanged;
        OnClosedOnce(() => _scripts.Tracker.DirtyChanged -= OnDirtyChanged);

        this.FindControl<Button>("GoToLineButton")!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => _editor.ShowGoToLine(), "go to line");
        this.FindControl<Button>("FormatButton")!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => _editor.Format(), "format");
        Button find = this.FindControl<Button>("FindButton")!;
        find.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => _editor.ShowFind(false), "find");
        find.AddHandler(PointerPressedEvent, UiSafe.Handler<PointerPressedEventArgs>((_, e) =>
        {
            if (!e.GetCurrentPoint(find).Properties.IsRightButtonPressed) return;
            _editor.ShowFind(true);
            e.Handled = true;
        }, "find replace"), RoutingStrategies.Tunnel);
        _applyButton.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => SaveAsync(), "apply");
        this.FindControl<Button>("CloseButton")!.Click += UiSafe.AsyncHandler<RoutedEventArgs>((_, _) => RequestCloseAsync(), "close");
        this.FindControl<Button>("ReloadButton")!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => ReloadPending(), "reload");
        this.FindControl<Button>("KeepButton")!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => KeepMine(), "keep mine");

        Track(_context.Attach(session =>
        {
            var subscriptions = new List<IDisposable>();
            if (target.Subscribe(session, (text, image) => UiSafe.Run(() => OnReceived(text, image), "editor text received")) is { } subscription) subscriptions.Add(subscription);
            EventHandler connected = (_, _) => UiSafe.Run(OnConnectionReady, "editor connected");
            session.NcConnected += connected;
            session.Connected += connected;
            subscriptions.Add(new Unsubscriber(() => { session.NcConnected -= connected; session.Connected -= connected; }));
            if (!_firstBind) Dispatcher.UIThread.Post(() => UiSafe.Run(OnConnectionReady, "editor reconnected"));
            _firstBind = false;
            return new Unsubscriber(() => subscriptions.ForEach(item => item.Dispose()));
        }));

        if (target.HasExtensionActions && _context.Shell.GetService<IExtensionActions>() is { } actions)
        {
            BuildExtensionActions(actions);
            Listen(handler => actions.Changed += handler, handler => actions.Changed -= handler, (_, _) => BuildExtensionActions(actions), "extension actions");
        }

        PositionChanged += (_, _) => ScheduleGeometry();
        SizeChanged += (_, _) => ScheduleGeometry();
        _scripts.Register(this);
        OnClosedOnce(OnWindowClosed);
    }

    /// <summary>What it edits.</summary>
    public EditorTarget Target => _target;

    /// <summary>The editor control.</summary>
    public ScriptEditorControl Editor => _editor;

    /// <summary>Whether the text differs from what the server confirmed.</summary>
    public bool IsDirty => _loaded && _tracked.IsDirty;

    /// <summary>Whether the window waits for the server's text.</summary>
    public bool IsAwaiting => _awaiting;

    /// <summary>Whether the text arrived (or was given).</summary>
    public bool HasText => _loaded;

    /// <summary>The status line.</summary>
    public string StatusMessage => _status.Text ?? string.Empty;

    /// <summary>Whether the bar offering the server's newer text is shown.</summary>
    public bool IsReloadOffered => _infoBar.IsVisible && _pendingServerText is not null;

    /// <summary>The info bar text.</summary>
    public string InfoMessage => _infoBar.IsVisible ? _infoText.Text ?? string.Empty : string.Empty;

    /// <summary>The external editor session, or null.</summary>
    internal ExternalEditor? External => _external;

    /// <summary>The weapon image field.</summary>
    internal TextBox IconField => _iconBox;

    /// <summary>What other groups see of the editor.</summary>
    public ScriptEditorSnapshot? Snapshot() => _target.Kind is { } kind
        ? new ScriptEditorSnapshot(Title ?? string.Empty, kind, _target.Name, IsActive, IsDirty, _editor.Text, _editor.SelectedText)
        : null;

    /// <summary>Replaces the text as one undo step (<see cref="IScriptEditors.Update"/>).</summary>
    public bool Update(Func<string, string?> edit)
    {
        if (!_loaded || _target.ReadOnly) return false;
        string? next = edit(_editor.Text);
        if (next is null) return false;
        _editor.ReplaceText(next);
        return true;
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RestoreGeometry();
        if (_loaded || _awaiting) return;
        if (_target.GivenText is { } given) Load(given, null);
        else UiSafe.Fire(RequestAsync, "request editor text");
        _editor.FocusText();
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        if (!e.IsProgrammatic && !_closeConfirmed)
        {
            e.Cancel = true;
            UiSafe.Fire(RequestCloseAsync, "close editor");
        }
    }

    /// <summary>Asks the server for the text (again).</summary>
    internal async Task RequestAsync()
    {
        _awaiting = true;
        IRcSession session = _context.Session;
        if (!_target.IsConnected(session))
        {
            SetStatus(_target.NotConnectedText);
            return;
        }
        SetStatus("Loading...");
        string? error = await _target.RequestAsync(session).ConfigureAwait(true);
        if (error is not null && _awaiting) SetStatus(error);
    }

    /// <summary>The Apply button and Ctrl+S: sends the text; clean only when it went out on a live connection.</summary>
    /// <returns>Whether it was sent.</returns>
    public async Task<bool> SaveAsync()
    {
        if (!_loaded || _target.ReadOnly || _saving || IsClosed) return false;
        string text = _editor.Text;
        if (_target is ClassTarget && text.Length == 0 && _context.Session.Classes.Any(item => item.Name == _target.Name))
        {
            bool delete = await Dialogs.ConfirmAsync("Empty Class", "Saving an empty class deletes it on the server. Save anyway?", this, "Save", "Cancel").ConfigureAwait(true);
            if (!delete) return false;
        }
        PendingUpload? upload = _tracked.BeginUpload();
        if (upload is null) return false;
        _saving = true;
        _applyButton.IsEnabled = false;
        try
        {
            if (_backup is not null) _ = _backup.WriteAsync(_target.BackupType, _target.BackupTarget, upload.Text, modified: true);
            SaveOutcome outcome = await _target.SaveAsync(_context, upload.Text, _iconBox.Text ?? string.Empty).ConfigureAwait(true);
            if (!outcome.Sent)
            {
                SetStatus(outcome.Message ?? "Not saved.");
                return false;
            }
            _tracked.ConfirmUpload(upload);
            SetStatus(string.Empty);
            _editor.ChangedLines = _tracked.ChangedLines;
            _scripts.RaiseChanged();
            return true;
        }
        finally
        {
            _saving = false;
            _applyButton.IsEnabled = true;
        }
    }

    /// <summary>The Close button and the window's close box: asks first when there are unsaved changes.</summary>
    internal async Task RequestCloseAsync()
    {
        if (IsClosed || _asking) return;
        if (IsDirty)
        {
            _asking = true;
            UnsavedChoice choice;
            try
            {
                choice = await UnsavedChangesDialog.AskAsync(this, _context.App, Title ?? string.Empty).ConfigureAwait(true);
            }
            finally
            {
                _asking = false;
            }
            if (choice == UnsavedChoice.Cancel) return;
            if (choice == UnsavedChoice.Save && !await SaveAsync().ConfigureAwait(true)) return;
        }
        if (IsClosed) return;
        _closeConfirmed = true;
        _userClosed = true;
        Close();
    }

    private void OnConnectionReady()
    {
        if (_awaiting && _target.IsConnected(_context.Session)) UiSafe.Fire(RequestAsync, "request editor text");
        else if (_status.Text == SaveOutcome.NcDown.Message || _status.Text == SaveOutcome.RcDown.Message) SetStatus("Connected again: press " + _target.ApplyLabel + " to save.");
    }

    private void OnReceived(string text, string? image)
    {
        if (IsClosed) return;
        if (_awaiting || !_loaded)
        {
            Load(text, image);
            return;
        }
        if (string.Equals(text, _editor.Text, StringComparison.Ordinal))
        {
            _tracked.SetServerText(text, false);
            HideInfo();
            return;
        }
        if (!_tracked.IsDirty)
        {
            int caret = _editor.Editor.CaretOffset;
            _editor.SetContent(text);
            _editor.SetCaret(caret);
            _tracked.SetServerText(text, true);
            if (image is not null) _iconBox.Text = image;
            _editor.ChangedLines = null;
            return;
        }
        _pendingServerText = text;
        _infoText.Text = "The server sent a new version of this text. Reload it (your changes are lost) or keep yours?";
        _infoBar.IsVisible = true;
    }

    private void Load(string text, string? image)
    {
        bool first = !_loaded;
        _awaiting = false;
        _loaded = true;
        HideInfo();
        _editor.SetContent(text);
        _editor.IsReadOnly = _target.ReadOnly;
        _tracked.SetServerText(text, true);
        _editor.ChangedLines = null;
        if (image is not null && (first || string.IsNullOrEmpty(_iconBox.Text))) _iconBox.Text = image;
        SetStatus(string.Empty);
        if (_backup is not null) _ = _backup.WriteAsync(_target.BackupType, _target.BackupTarget, text, modified: false);
        _scripts.RaiseChanged();
        if (first) UiSafe.Fire(() => OpenExternalAsync(text), "open the external editor");
    }

    private void ReloadPending()
    {
        if (_pendingServerText is { } text) Load(text, null);
    }

    private void KeepMine()
    {
        if (_pendingServerText is { } text) _tracked.SetServerText(text, false);
        HideInfo();
        _editor.ChangedLines = _tracked.ChangedLines;
    }

    private void HideInfo()
    {
        _pendingServerText = null;
        if (_external is null) _infoBar.IsVisible = false;
        else _infoText.Text = "Editing in the external editor: " + _external.Path;
        this.FindControl<Button>("ReloadButton")!.IsVisible = true;
        this.FindControl<Button>("KeepButton")!.IsVisible = true;
    }

    private void OnTextChanged()
    {
        if (!_loaded) return;
        _tracked.UpdateText(_editor.Text);
        _tintTimer.Stop();
        _tintTimer.Start();
    }

    private void OnDirtyChanged(object? sender, TrackedEditor editor)
    {
        if (!ReferenceEquals(editor, _tracked)) return;
        Dispatcher.UIThread.Post(() => { if (!IsClosed) _scripts.RaiseChanged(); });
    }

    private void SetStatus(string text) => _status.Text = text;

    private async Task OpenExternalAsync(string text)
    {
        RcOptions options = _context.App.Options.Current;
        if (_target.ExternalIsScript is not { } isScript || _target.ReadOnly || !ExternalEditor.Covers(options.ExternalEditorScope, isScript)) return;
        (string category, string name) = _target.External;
        string path = ExternalEditor.PathFor(_context.App.Paths.ExternalEditorDirectory(options), _context.ServerName, category, name);
        string? existing = await ExternalEditor.ReadExistingAsync(path).ConfigureAwait(true);
        bool write = true;
        if (existing is { Length: > 0 } && !string.Equals(existing, text, StringComparison.Ordinal))
        {
            bool useFile = await Dialogs.ConfirmAsync("External Editor",
                "The file " + path + " has text that differs from the server's. Use the file's text (it is shown here, not yet saved) or replace the file with the server's text?",
                this, "Use the file", "Use the server's text").ConfigureAwait(true);
            if (IsClosed) return;
            if (useFile)
            {
                write = false;
                _editor.ReplaceText(existing);
            }
        }
        ExternalEditor? external = await ExternalEditor.OpenAsync(path, text, write, OnExternalSaved).ConfigureAwait(true);
        if (external is null)
        {
            SetStatus("The external editor file could not be written: " + path);
            return;
        }
        if (IsClosed)
        {
            external.Dispose();
            return;
        }
        _external = Track(external);
        external.Status += (_, message) => { if (!IsClosed) SetStatus(message); };
        _infoText.Text = "Editing in the external editor: " + path;
        this.FindControl<Button>("ReloadButton")!.IsVisible = false;
        this.FindControl<Button>("KeepButton")!.IsVisible = false;
        _infoBar.IsVisible = true;
        external.Start(options.ExternalEditorCommand);
    }

    private void OnExternalSaved(string text)
    {
        if (IsClosed || !_loaded) return;
        _editor.ReplaceText(text);
        UiSafe.Fire(() => SaveAsync(), "upload the external editor's file");
    }

    private void BuildExtensionActions(IExtensionActions actions)
    {
        _topActions.Children.Clear();
        _bottomActions.Children.Clear();
        foreach (WindowActionButton action in actions.ActionsFor("script-editor"))
        {
            var button = new Button { Content = action.Label, Tag = action.Id };
            ToolTip.SetTip(button, "Run " + action.Label);
            button.Click += UiSafe.Handler<RoutedEventArgs>((_, _) =>
            {
                if (Snapshot() is { } snapshot) actions.Invoke(action.Id, _context, snapshot);
            }, "extension action");
            (action.AtBottom ? _bottomActions : _topActions).Children.Add(button);
        }
        _topActions.IsVisible = _topActions.Children.Count > 0;
        _bottomActions.IsVisible = _bottomActions.Children.Count > 0;
    }

    private string SessionKey => ScriptWindowRecords.SessionKey(_context.ServerName, _context.Account);

    private void RestoreGeometry()
    {
        if (_target.RecordType is not { } type) return;
        if (_scripts.Records.Find(SessionKey, type, _target.Name) is { Width: > 0, Height: > 0 } record)
        {
            Width = record.Width;
            Height = record.Height;
            Position = new PixelPoint(record.X, record.Y);
        }
        SaveGeometry();
    }

    private void ScheduleGeometry()
    {
        if (_target.RecordType is null || IsClosed) return;
        _geometryTimer.Stop();
        _geometryTimer.Start();
    }

    private void SaveGeometry()
    {
        if (_target.RecordType is not { } type || IsClosed || !IsVisible) return;
        int width = (int)Math.Round(Bounds.Width > 0 ? Bounds.Width : Width);
        int height = (int)Math.Round(Bounds.Height > 0 ? Bounds.Height : Height);
        if (width <= 0 || height <= 0) return;
        _scripts.Records.Update(new ScriptWindowRecord(SessionKey, type, _target.Name, Position.X, Position.Y, width, height));
    }

    private void OnWindowClosed()
    {
        _scripts.Tracker.Untrack(_tracked);
        _scripts.Unregister(this);
        if (_userClosed && _target.RecordType is { } type) _scripts.Records.Remove(SessionKey, type, _target.Name);
    }
}
