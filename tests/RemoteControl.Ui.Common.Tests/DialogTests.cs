using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Common.Tests;

public sealed class DialogTests
{
    [AvaloniaFact]
    public async Task Message_opens_and_closes_with_ok()
    {
        using var env = new TestEnvironment();
        Task shown = Dialogs.ShowMessageAsync("Hello", "Some text");
        RcDialog dialog = TestUi.FindDialog();
        Assert.Equal(DialogKind.Message, dialog.Kind);
        Assert.Equal("Hello", dialog.Title);
        Assert.Equal("Some text", TestUi.Find<TextBlock>(dialog, Dialogs.MessageName).Text);
        Assert.Null(TestUi.TryFind<Button>(dialog, Dialogs.CancelButtonName));
        Shots.Save(dialog, "dialog-message");
        TestUi.Click(TestUi.Find<Button>(dialog, Dialogs.OkButtonName));
        await shown;
        Assert.True(dialog.IsClosed);
        Assert.True(dialog.Accepted);
    }

    [AvaloniaFact]
    public async Task Unattended_dialogs_are_logged_and_answered_with_cancel_at_once()
    {
        using var env = new TestEnvironment();
        int before = RcWindow.OpenWindows.Count;
        Dialogs.Unattended = true;
        try
        {
            Assert.False(await Dialogs.ConfirmAsync("Quit", "Quit anyway?"));
            Assert.Null(await Dialogs.PromptAsync("Password", "Password of bob:", password: true));
            Assert.Null(await Dialogs.ChooseAsync("Server", "Choose:", ["a", "b"]));
            await Dialogs.ShowErrorAsync("Connection Error", "The server closed\nthe connection.");
        }
        finally
        {
            Dialogs.Unattended = false;
        }
        Assert.Equal(before, RcWindow.OpenWindows.Count);
        Assert.Contains(UiSafe.Log.Snapshot(), line => line.Contains("Not shown (headless): Connection Error: The server closed the connection.", StringComparison.Ordinal));
        Assert.Contains(UiSafe.Log.Snapshot(), line => line.Contains("answered Cancel: Quit: Quit anyway?", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Error_window_is_400_by_120_and_modal_on_its_owner()
    {
        using var env = new TestEnvironment(theme: "nord");
        var owner = env.Show(new RcWindow(env.Services) { Title = "Owner" }, 600, 400);
        Task shown = Dialogs.ShowErrorAsync("Connection Error", "The server closed the connection.", owner);
        RcDialog dialog = TestUi.FindDialog();
        Assert.Equal(DialogKind.Error, dialog.Kind);
        Assert.Same(owner, dialog.Owner);
        Assert.Equal(400, dialog.Bounds.Width, 0);
        Assert.True(dialog.Bounds.Height >= 120, $"height {dialog.Bounds.Height}, client {dialog.ClientSize}");
        Shots.Save(dialog, "dialog-error");
        TestUi.PressKey(dialog, Key.Enter);
        await shown;
        Assert.True(dialog.IsClosed);
    }

    [AvaloniaFact]
    public async Task Error_window_without_text_says_question()
    {
        using var env = new TestEnvironment();
        Task shown = Dialogs.ShowErrorAsync("", "");
        RcDialog dialog = TestUi.FindDialog();
        Assert.Equal("Question", dialog.Title);
        Assert.Equal("Question?", dialog.Message);
        TestUi.PressKey(dialog, Key.Escape);
        await shown;
    }

    [AvaloniaFact]
    public async Task Confirm_answers_with_enter_escape_and_the_buttons()
    {
        using var env = new TestEnvironment();
        Task<bool> yes = Dialogs.ConfirmAsync("Delete", "Delete it?");
        TestUi.PressKey(TestUi.FindDialog(), Key.Enter);
        Assert.True(await yes);

        Task<bool> no = Dialogs.ConfirmAsync("Delete", "Delete it?");
        TestUi.PressKey(TestUi.FindDialog(), Key.Escape);
        Assert.False(await no);

        Task<bool> cancel = Dialogs.ConfirmAsync("Delete", "Delete it?", okText: "Delete", cancelText: "Keep");
        RcDialog dialog = TestUi.FindDialog();
        Button keep = TestUi.Find<Button>(dialog, Dialogs.CancelButtonName);
        Assert.Equal("Keep", keep.Content);
        TestUi.Click(keep);
        Assert.False(await cancel);

        Task<bool> closed = Dialogs.ConfirmAsync("Delete", "Delete it?");
        TestUi.FindDialog().Close();
        Assert.False(await closed);
    }

    [AvaloniaFact]
    public async Task Prompt_returns_the_text_or_null()
    {
        using var env = new TestEnvironment();
        Task<string?> typed = Dialogs.PromptAsync("Nickname", "New nickname:", "old");
        RcDialog dialog = TestUi.FindDialog();
        TextBox box = TestUi.Find<TextBox>(dialog, Dialogs.InputName);
        Assert.Equal("old", box.Text);
        box.Text = string.Empty;
        TestUi.TypeInto(box, "Graal123");
        Shots.Save(dialog, "dialog-prompt");
        TestUi.PressKey(dialog, Key.Enter);
        Assert.Equal("Graal123", await typed);

        Task<string?> cancelled = Dialogs.PromptAsync("Nickname", "New nickname:");
        TestUi.PressKey(TestUi.FindDialog(), Key.Escape);
        Assert.Null(await cancelled);

        Task<string?> secret = Dialogs.PromptAsync("Password", "Password:", password: true);
        RcDialog passwordDialog = TestUi.FindDialog();
        TextBox hidden = TestUi.Find<TextBox>(passwordDialog, Dialogs.InputName);
        Assert.Equal('●', hidden.PasswordChar);
        TestUi.TypeInto(hidden, "s3cret");
        passwordDialog.Accept();
        Assert.Equal("s3cret", await secret);
    }

    [AvaloniaFact]
    public async Task Choose_returns_the_index()
    {
        using var env = new TestEnvironment();
        string[] items = ["Alpha", "Bravo", "Charlie"];
        Task<int?> chosen = Dialogs.ChooseAsync("Server", "Choose a server:", items, selectedIndex: 1);
        RcDialog dialog = TestUi.FindDialog();
        ListBox list = TestUi.Find<ListBox>(dialog, Dialogs.ListName);
        Assert.Equal(1, list.SelectedIndex);
        list.SelectedIndex = 2;
        Shots.Save(dialog, "dialog-choose");
        TestUi.Click(TestUi.Find<Button>(dialog, Dialogs.OkButtonName));
        Assert.Equal(2, await chosen);

        Task<int?> cancelled = Dialogs.ChooseAsync("Server", "Choose:", items);
        TestUi.PressKey(TestUi.FindDialog(), Key.Escape);
        Assert.Null(await cancelled);

        Task<int?> empty = Dialogs.ChooseAsync("Server", "Choose:", Array.Empty<string>());
        TestUi.PressKey(TestUi.FindDialog(), Key.Enter);
        RcDialog still = TestUi.FindDialog();
        Assert.False(still.IsClosed);
        still.Accept();
        Assert.Null(await empty);
    }
}
