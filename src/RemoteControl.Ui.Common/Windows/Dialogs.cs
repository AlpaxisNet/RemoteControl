using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace RemoteControl.Ui.Common;

/// <summary>The kinds of <see cref="RcDialog"/>.</summary>
public enum DialogKind
{
    /// <summary>A message with OK.</summary>
    Message,

    /// <summary>The error window of the C++ (<c>createErrorWindow</c>): 400 by 120, a wrapped text and OK.</summary>
    Error,

    /// <summary>A question with OK and Cancel.</summary>
    Confirm,

    /// <summary>A one-line text entry with OK and Cancel.</summary>
    Prompt,

    /// <summary>A list to choose from with OK and Cancel.</summary>
    Choose,
}

/// <summary>
/// The small dialogs of the program, all asynchronous: nothing here runs a nested main loop (defect D6). With an owner the dialog is
/// modal to that window and centred on it; without one it is an ordinary window. Enter activates the default button (OK), Escape and
/// the close button cancel. A dialog never throws: when it cannot be shown, the call returns the cancel answer and logs.
/// </summary>
/// <example>
/// <code>
/// await Dialogs.ShowErrorAsync("Connection Error", reason, this);
/// if (!await Dialogs.ConfirmAsync("Delete", $"Delete {name}?", this)) return;
/// string? nick = await Dialogs.PromptAsync("Nickname", "New nickname:", current, this);
/// string? password = await Dialogs.PromptAsync("Password", "Password:", owner: this, password: true);
/// int? index = await Dialogs.ChooseAsync("Server", "Choose a server:", names, this);
/// </code>
/// </example>
public static class Dialogs
{
    /// <summary>The name of the OK button (for tests).</summary>
    public const string OkButtonName = "DialogOk";

    /// <summary>The name of the Cancel button (for tests).</summary>
    public const string CancelButtonName = "DialogCancel";

    /// <summary>The name of the text of the dialog (for tests).</summary>
    public const string MessageName = "DialogMessage";

    /// <summary>The name of the text box of <see cref="PromptAsync"/> (for tests).</summary>
    public const string InputName = "DialogInput";

    /// <summary>The name of the list of <see cref="ChooseAsync"/> (for tests).</summary>
    public const string ListName = "DialogList";

    /// <summary>Shows a message with an OK button.</summary>
    /// <param name="title">The window title.</param>
    /// <param name="message">The text.</param>
    /// <param name="owner">The owner window (modal), or <c>null</c>.</param>
    /// <returns>Completes when the dialog closed.</returns>
    public static async Task ShowMessageAsync(string title, string message, Window? owner = null) =>
        await ShowAsync(new RcDialog(DialogKind.Message, title, message), owner).ConfigureAwait(true);

    /// <summary>Shows the error window of section 12: 400 by 120 (taller for a long text), a wrapped text and OK.</summary>
    /// <param name="title">The window title, for example <c>Connection Error</c>.</param>
    /// <param name="message">The text.</param>
    /// <param name="owner">The owner window (modal and centred on it), or <c>null</c> (non-modal).</param>
    /// <returns>Completes when the window closed.</returns>
    public static async Task ShowErrorAsync(string title, string message, Window? owner = null) =>
        await ShowAsync(new RcDialog(DialogKind.Error, title, message), owner).ConfigureAwait(true);

    /// <summary>Asks a question with OK and Cancel.</summary>
    /// <param name="title">The window title.</param>
    /// <param name="message">The question.</param>
    /// <param name="owner">The owner window, or <c>null</c>.</param>
    /// <param name="okText">The text of the OK button.</param>
    /// <param name="cancelText">The text of the Cancel button.</param>
    /// <returns><c>true</c> for OK; <c>false</c> for Cancel, Escape or closing the window.</returns>
    public static async Task<bool> ConfirmAsync(string title, string message, Window? owner = null, string okText = "OK", string cancelText = "Cancel")
    {
        var dialog = new RcDialog(DialogKind.Confirm, title, message, okText, cancelText);
        return await ShowAsync(dialog, owner).ConfigureAwait(true);
    }

    /// <summary>Asks for one line of text.</summary>
    /// <param name="title">The window title.</param>
    /// <param name="message">The label above the text box.</param>
    /// <param name="initial">The text the box starts with (selected).</param>
    /// <param name="owner">The owner window, or <c>null</c>.</param>
    /// <param name="password">Whether the text is hidden.</param>
    /// <returns>The text for OK; <c>null</c> for Cancel.</returns>
    public static async Task<string?> PromptAsync(string title, string message, string initial = "", Window? owner = null, bool password = false)
    {
        var dialog = new RcDialog(DialogKind.Prompt, title, message) { InitialText = initial ?? string.Empty, IsPassword = password };
        return await ShowAsync(dialog, owner).ConfigureAwait(true) ? dialog.Text : null;
    }

    /// <summary>Asks to choose one item of a list (double click or Enter chooses).</summary>
    /// <param name="title">The window title.</param>
    /// <param name="message">The label above the list.</param>
    /// <param name="items">The items.</param>
    /// <param name="owner">The owner window, or <c>null</c>.</param>
    /// <param name="selectedIndex">The item selected at first.</param>
    /// <returns>The index of the chosen item; <c>null</c> for Cancel or an empty list.</returns>
    public static async Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> items, Window? owner = null, int selectedIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(items);
        var dialog = new RcDialog(DialogKind.Choose, title, message) { Items = items, InitialIndex = selectedIndex };
        bool accepted = await ShowAsync(dialog, owner).ConfigureAwait(true);
        return accepted && dialog.SelectedIndex >= 0 && dialog.SelectedIndex < items.Count ? dialog.SelectedIndex : null;
    }

    private static async Task<bool> ShowAsync(RcDialog dialog, Window? owner)
    {
        try
        {
            if (!Dispatcher.UIThread.CheckAccess()) return await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(dialog, owner)).ConfigureAwait(true);
            if (owner is { IsVisible: true })
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                await dialog.ShowDialog(owner).ConfigureAwait(true);
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.Show();
                await dialog.Completion.ConfigureAwait(true);
            }
            return dialog.Accepted;
        }
        catch (Exception exception)
        {
            UiSafe.Log.Error("A dialog could not be shown: " + dialog.Title, exception);
            try
            {
                dialog.Close();
            }
            catch (Exception)
            {
                // The dialog was never shown.
            }
            return false;
        }
    }
}

/// <summary>
/// The window of <see cref="Dialogs"/>. Public so that window tests can find an open dialog among <see cref="RcWindow.OpenWindows"/>
/// and answer it (<see cref="Accept"/>, <see cref="Cancel"/>) or click its buttons (<see cref="Dialogs.OkButtonName"/>).
/// </summary>
public sealed class RcDialog : RcWindow
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _message;
    private readonly Button _ok;
    private readonly Button? _cancel;
    private TextBox? _input;
    private ListBox? _list;
    private bool _built;

    internal RcDialog(DialogKind kind, string title, string message, string okText = "OK", string cancelText = "Cancel")
    {
        Kind = kind;
        Title = string.IsNullOrEmpty(title) ? (kind == DialogKind.Error ? "Question" : "Remote Control") : title;
        Message = string.IsNullOrEmpty(message) ? (kind == DialogKind.Error ? "Question?" : string.Empty) : message;
        ShowInTaskbar = kind is DialogKind.Error or DialogKind.Message;
        CanResize = kind is DialogKind.Error or DialogKind.Choose;
        Width = kind switch { DialogKind.Error => 400, DialogKind.Choose => 360, _ => 380 };
        MinHeight = kind == DialogKind.Error ? 120 : 0;
        SizeToContent = kind == DialogKind.Choose ? SizeToContent.Manual : SizeToContent.Height;
        if (kind == DialogKind.Choose) Height = 320;

        _message = new TextBlock { Name = Dialogs.MessageName, Text = Message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _ok = new Button { Name = Dialogs.OkButtonName, Content = okText, MinWidth = 80, MinHeight = 24, HorizontalContentAlignment = HorizontalAlignment.Center };
        _ok.Classes.Add("accent");
        _ok.Click += (_, _) => Finish(true);
        if (kind is DialogKind.Confirm or DialogKind.Prompt or DialogKind.Choose)
        {
            _cancel = new Button { Name = Dialogs.CancelButtonName, Content = cancelText, MinWidth = 80, MinHeight = 24, HorizontalContentAlignment = HorizontalAlignment.Center };
            _cancel.Click += (_, _) => Finish(false);
        }
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>What the dialog asks.</summary>
    public DialogKind Kind { get; }

    /// <summary>The text of the dialog.</summary>
    public string Message { get; }

    /// <summary>Whether the dialog was answered with OK.</summary>
    public bool Accepted { get; private set; }

    /// <summary>The text of a prompt (as typed so far).</summary>
    public string Text => _input?.Text ?? InitialText;

    /// <summary>The selected index of a list, or -1.</summary>
    public int SelectedIndex => _list?.SelectedIndex ?? Math.Clamp(InitialIndex, -1, Items.Count - 1);

    internal string InitialText { get; init; } = string.Empty;

    internal bool IsPassword { get; init; }

    internal IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();

    internal int InitialIndex { get; init; }

    internal Task Completion => _completion.Task;

    /// <summary>Answers OK (as the OK button or Enter does).</summary>
    public void Accept() => Finish(true);

    /// <summary>Answers Cancel (as the Cancel button, Escape or the close button do).</summary>
    public void Cancel() => Finish(false);

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        Build();
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() =>
        {
            if (IsClosed) return;
            if (_input is not null)
            {
                _input.Focus();
                _input.SelectAll();
            }
            else if (_list is not null) _list.Focus();
            else _ok.Focus();
        }, DispatcherPriority.Loaded);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _completion.TrySetResult();
    }

    private void Build()
    {
        if (_built) return;
        _built = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right };
        if (_cancel is not null) buttons.Children.Add(_cancel);
        buttons.Children.Add(_ok);
        DockPanel.SetDock(buttons, Dock.Bottom);
        double margin = Kind == DialogKind.Error ? 5 : 12;
        var root = new DockPanel { Margin = new Thickness(margin), LastChildFill = true, MinHeight = Kind == DialogKind.Error ? 120 - 2 * margin : 0 };
        root.Children.Add(buttons);
        switch (Kind)
        {
            case DialogKind.Prompt:
            {
                _input = new TextBox { Name = Dialogs.InputName, Text = InitialText, Margin = new Thickness(0, 6, 0, 10), AcceptsReturn = false };
                if (IsPassword) _input.PasswordChar = '●';
                _message.Margin = new Thickness(0);
                var body = new StackPanel();
                if (Message.Length > 0) body.Children.Add(_message);
                body.Children.Add(_input);
                root.Children.Add(body);
                break;
            }
            case DialogKind.Choose:
            {
                _list = new ListBox { Name = Dialogs.ListName, ItemsSource = Items, Margin = new Thickness(0, 6, 0, 10) };
                if (Items.Count > 0) _list.SelectedIndex = Math.Clamp(InitialIndex, 0, Items.Count - 1);
                _list.DoubleTapped += (_, _) => { if (_list.SelectedIndex >= 0) Finish(true); };
                var body = new DockPanel();
                if (Message.Length > 0)
                {
                    DockPanel.SetDock(_message, Dock.Top);
                    body.Children.Add(_message);
                }
                body.Children.Add(_list);
                root.Children.Add(body);
                break;
            }
            default:
                _message.Margin = new Thickness(Kind == DialogKind.Error ? 4 : 0, 4, 4, 12);
                if (Kind == DialogKind.Error) _message.TextAlignment = TextAlignment.Center;
                root.Children.Add(_message);
                break;
        }
        Content = root;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Finish(false);
        }
        else if (e.Key is Key.Enter or Key.Return && e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Source is Button button && !ReferenceEquals(button, _ok))
            {
                return;
            }
            if (Kind == DialogKind.Choose && (_list is null || _list.SelectedIndex < 0)) return;
            e.Handled = true;
            Finish(true);
        }
    }

    private void Finish(bool accepted)
    {
        if (IsClosed) return;
        Accepted = accepted;
        Close();
    }
}
