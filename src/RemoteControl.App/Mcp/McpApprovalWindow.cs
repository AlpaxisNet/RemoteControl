using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RemoteControl.Extensions;
using RemoteControl.Ui.Common;

namespace RemoteControl.App.Mcp;

/// <summary>
/// The approval question of the MCP server (<c>approve</c> and <c>approveScript</c>, <c>TMcpServer.cpp:60-90</c>): "RC MCP Approval"
/// with Deny / Allow once, or "RC MCP Script Approval" with Deny / Allow once / Always allow &lt;type&gt; scripts. Not a nested main loop
/// (defect D-19): the answer is awaited. Escape and the close box deny.
/// </summary>
internal sealed class McpApprovalWindow : RcWindow
{
    /// <summary>The name of the Deny button.</summary>
    public const string DenyName = "ApprovalDeny";

    /// <summary>The name of the Allow once button.</summary>
    public const string AllowName = "ApprovalAllow";

    /// <summary>The name of the Always allow button.</summary>
    public const string AlwaysName = "ApprovalAlways";

    private readonly TaskCompletionSource<McpApproval> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Makes the question.</summary>
    /// <param name="services">The program's services (theme).</param>
    /// <param name="title">The title.</param>
    /// <param name="message">The question.</param>
    /// <param name="alwaysText">The text of the third button, or null for none.</param>
    public McpApprovalWindow(AppServices? services, string title, string message, string? alwaysText) : base(services)
    {
        Title = title;
        Message = message;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var text = new TextBlock { Name = Dialogs.MessageName, Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(MakeButton(DenyName, "Deny", McpApproval.Deny));
        if (alwaysText is not null) buttons.Children.Add(MakeButton(AlwaysName, alwaysText, McpApproval.AlwaysAllow));
        Button allow = MakeButton(AllowName, "Allow once", McpApproval.AllowOnce);
        allow.Classes.Add("accent");
        buttons.Children.Add(allow);
        DockPanel.SetDock(buttons, Dock.Bottom);
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(buttons);
        // The whole text of a change is shown (never cut), so a long one scrolls.
        root.Children.Add(new ScrollViewer { Content = text, MaxHeight = 440, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = root;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The question.</summary>
    public string Message { get; }

    /// <summary>The answer (Deny when the window was closed).</summary>
    public Task<McpApproval> Answer => _answer.Task;

    /// <summary>Answers (as a button does).</summary>
    /// <param name="answer">The answer.</param>
    public void Choose(McpApproval answer)
    {
        _answer.TrySetResult(answer);
        if (!IsClosed) Close();
    }

    /// <summary>Shows a question and waits for the answer.</summary>
    /// <param name="services">The services.</param>
    /// <param name="title">The title.</param>
    /// <param name="message">The question.</param>
    /// <param name="alwaysText">The third button, or null.</param>
    /// <returns>The answer.</returns>
    public static async Task<McpApproval> AskAsync(AppServices? services, string title, string message, string? alwaysText)
    {
        if (!Dispatcher.UIThread.CheckAccess()) return await Dispatcher.UIThread.InvokeAsync(() => AskAsync(services, title, message, alwaysText)).ConfigureAwait(true);
        var window = new McpApprovalWindow(services, title, message, alwaysText);
        try
        {
            window.ShowAndActivate();
        }
        catch (Exception exception)
        {
            UiSafe.Log.Error("The MCP approval could not be shown.", exception);
            return McpApproval.Deny;
        }
        return await window.Answer.ConfigureAwait(true);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _answer.TrySetResult(McpApproval.Deny);
    }

    private Button MakeButton(string name, string text, McpApproval answer)
    {
        var button = new Button { Name = name, Content = text, MinWidth = 80, MinHeight = 24, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => UiSafe.Run(() => Choose(answer), "MCP approval");
        return button;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Choose(McpApproval.Deny);
    }
}

/// <summary>The approval questions as windows.</summary>
/// <param name="services">The program's services.</param>
internal sealed class DialogApprovals(AppServices services) : IMcpApprovals
{
    public async Task<bool> ApproveAsync(string description) =>
        await McpApprovalWindow.AskAsync(services, "RC MCP Approval", "Allow this MCP action?\n\n" + description, null).ConfigureAwait(true) == McpApproval.AllowOnce;

    public async Task<McpApproval> ApproveScriptAsync(McpScriptAction action, string editor, string scriptType)
    {
        string question = action switch
        {
            McpScriptAction.Edit => "Edit the open script buffer without saving or applying it?",
            McpScriptAction.SaveApply => "Save/Apply the current open script buffer to the server?",
            McpScriptAction.Write => "Write this script to the server?",
            _ => "Delete this script on the server?",
        };
        string? always = string.IsNullOrEmpty(scriptType) || action == McpScriptAction.Delete ? null : "Always allow " + scriptType + " scripts";
        McpApproval answer = await McpApprovalWindow.AskAsync(services, "RC MCP Script Approval", question + "\n\n" + editor, always).ConfigureAwait(true);
        return always is null && answer == McpApproval.AlwaysAllow ? McpApproval.AllowOnce : answer;
    }
}

/// <summary>
/// The approval questions of a headless program (<c>--headless</c>): no one can answer them, so each is denied at once and logged. The
/// option <c>mcpbypass</c> (or <c>mcpapprove</c> off) lets MCP clients change things without a question.
/// </summary>
/// <param name="log">Where a denial is logged (standard error too, in a headless run).</param>
internal sealed class UnattendedApprovals(Core.AppLog log) : IMcpApprovals
{
    /// <summary>What the MCP client reads with each denial.</summary>
    public const string Note = "the program runs headless (--headless), where no one can answer approval questions; switch on mcpbypass in the options (Bypass all MCP permissions) to let MCP clients go ahead";

    public Task<bool> ApproveAsync(string description)
    {
        Deny(description);
        return Task.FromResult(false);
    }

    public Task<McpApproval> ApproveScriptAsync(McpScriptAction action, string editor, string scriptType)
    {
        Deny(action + " " + editor);
        return Task.FromResult(McpApproval.Deny);
    }

    private void Deny(string what)
    {
        string first = (what ?? string.Empty).Split('\n', 2)[0];
        log.Warn("An MCP action that needs approval was denied, as " + Note + ": " + first);
    }
}
