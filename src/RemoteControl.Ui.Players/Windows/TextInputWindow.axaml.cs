using Avalonia.Input;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Players;

/// <summary>
/// The text prompts of the player list (<c>getMessage</c> / <c>getAdminMessage</c>): Mass PM, Admin Message, Disconnect reason. Not
/// modal and without a nested main loop (defect D6); it belongs to the session, so it closes when the session ends. A multi-line
/// prompt takes Ctrl+Enter as the accepting key; a single-line one Enter. Escape cancels.
/// </summary>
internal sealed partial class TextInputWindow : RcWindow
{
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>For the designer.</summary>
    public TextInputWindow() : this(null, "Message", "Message:", "Send", true)
    {
    }

    /// <summary>Creates the prompt.</summary>
    /// <param name="services">The application services.</param>
    /// <param name="title">The title.</param>
    /// <param name="label">The label above the text.</param>
    /// <param name="accept">The accepting button's text.</param>
    /// <param name="multiline">Whether the text has several lines (120 high, as in the C++).</param>
    public TextInputWindow(AppServices? services, string title, string label, string accept, bool multiline) : base(services)
    {
        InitializeComponent();
        Title = title;
        Label.Text = label;
        AcceptButton.Content = accept;
        Multiline = multiline;
        Input.AcceptsReturn = multiline;
        if (multiline) Input.Height = 120;
        AcceptButton.Click += On.Click(Accept, "TextInput.Accept");
        CancelButton.Click += On.Click(Cancel, "TextInput.Cancel");
        Input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyDown += UiSafe.Handler<KeyEventArgs>((_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Cancel();
            }
        }, "TextInput.Escape");
        Opened += UiSafe.Handler((_, _) => Input.Focus(), "TextInput.Opened");
        OnClosedOnce(() => _result.TrySetResult(null));
    }

    /// <summary>Whether the text has several lines.</summary>
    public bool Multiline { get; }

    /// <summary>The text for the accepting button; null for Cancel or closing.</summary>
    public Task<string?> Result => _result.Task;

    /// <summary>The text typed.</summary>
    public string Text
    {
        get => Input.Text ?? string.Empty;
        set => Input.Text = value;
    }

    /// <summary>Accepts the text (as the accepting button does).</summary>
    public void Accept()
    {
        _result.TrySetResult(Text);
        Close();
    }

    /// <summary>Cancels.</summary>
    public void Cancel()
    {
        _result.TrySetResult(null);
        Close();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        UiSafe.Run(() =>
        {
            if (e.Key is not (Key.Enter or Key.Return)) return;
            if (!Multiline || (e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                e.Handled = true;
                Accept();
            }
        }, "TextInput.Key");
    }
}
