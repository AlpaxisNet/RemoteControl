using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.Scripts;

/// <summary>What the user chose for an editor with unsaved changes.</summary>
internal enum UnsavedChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>Asks before an editor with changes the server has not confirmed is closed (review defect D-02): Save, Close without saving, Cancel.</summary>
internal sealed partial class UnsavedChangesDialog : RcWindow
{
    private readonly TaskCompletionSource<UnsavedChoice> _result = new();

    public UnsavedChangesDialog() : this(null, string.Empty)
    {
    }

    public UnsavedChangesDialog(AppServices? services, string title) : base(services)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("MessageText")!.Text = "\"" + title + "\" has changes the server has not confirmed. Save them before closing?";
        this.FindControl<Button>("SaveButton")!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Finish(UnsavedChoice.Save), "unsaved save");
        this.FindControl<Button>("DiscardButton")!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Finish(UnsavedChoice.Discard), "unsaved discard");
        this.FindControl<Button>("CancelButton")!.Click += UiSafe.Handler<RoutedEventArgs>((_, _) => Finish(UnsavedChoice.Cancel), "unsaved cancel");
        Closed += (_, _) => _result.TrySetResult(UnsavedChoice.Cancel);
    }

    /// <summary>The answer; Cancel when the dialog was closed.</summary>
    public Task<UnsavedChoice> Result => _result.Task;

    /// <summary>Answers as a button does (tests).</summary>
    public void Finish(UnsavedChoice choice)
    {
        _result.TrySetResult(choice);
        if (!IsClosed) Close();
    }

    /// <summary>Shows the dialog for an owner window and waits for the answer.</summary>
    public static async Task<UnsavedChoice> AskAsync(Window owner, AppServices? services, string title)
    {
        var dialog = new UnsavedChangesDialog(services, title);
        if (owner.IsVisible) _ = dialog.ShowDialog(owner);
        else dialog.Show();
        return await dialog.Result.ConfigureAwait(true);
    }
}
