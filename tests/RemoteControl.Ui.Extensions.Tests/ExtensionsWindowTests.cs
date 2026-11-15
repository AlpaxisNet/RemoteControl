using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using RemoteControl.Extensions;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Extensions.Tests;

public sealed class ExtensionsWindowTests
{
    [AvaloniaTheory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task Window_lists_valid_and_invalid_packages(string theme)
    {
        await using var world = new TestWorld(theme: theme);
        world.WriteEcho();
        world.WriteInvalid(world.UserRoot);
        world.WriteIdle(world.AppRoot, "bundled", "test.bundled", "Bundled");
        ExtensionsWindow window = world.ShowWindow();
        await TestUi.WaitUntilAsync(() => window.Rows.Count == 3, what: "three rows");

        ExtensionRow echo = window.RowOf("test.echo")!;
        Assert.Equal("Echo 1.2.0 (disabled)", echo.Title);
        Assert.Contains("Test Publisher", echo.Subtitle);
        Assert.False(echo.HasError);
        Assert.True(echo.IsValid);
        Assert.True(echo.CanRemove);

        ExtensionRow broken = window.RowOf("test.broken")!;
        Assert.Equal("invalid", broken.State);
        Assert.Contains("(invalid)", broken.Title);
        Assert.StartsWith("Invalid manifest: ", broken.Error);
        Assert.False(broken.IsValid);
        Assert.False(TestWorld.ControlOfRow<CheckBox>(window, broken).IsEnabled);
        Assert.True(TestWorld.ControlOfRow<CheckBox>(window, echo).IsEnabled);

        ExtensionRow bundled = window.RowOf("test.bundled")!;
        Assert.False(bundled.CanRemove);
        Assert.False(TestWorld.ButtonOfRow(window, bundled, "Remove").IsEnabled);
        Assert.False(window.DisabledBanner.IsVisible);
        Assert.False(window.EmptyLabel.IsVisible);

        window.ExtensionList.SelectedItem = broken;
        TestUi.Settle();
        Shots.Save(window, "extensions-" + theme);

        TestUi.Click(TestWorld.ButtonOfRow(window, broken, "Details"));
        ExtensionDetailsWindow details = window.DetailsWindows["test.broken"];
        Assert.True(details.ErrorText.IsVisible);
        Assert.StartsWith("Manifest error: ", details.ErrorText.Text);
        Assert.Contains(world.Host.Find("test.broken")!.Manifest.Error, details.ErrorText.Text);
        Assert.False(details.OpenButton.IsEnabled);
        details.Close();

        TestUi.Click(TestWorld.ButtonOfRow(window, echo, "Details"));
        details = window.DetailsWindows["test.echo"];
        details.Width = 480;
        details.Height = 520;
        TestUi.Settle();
        Assert.Equal("Echo", details.Title);
        string summary = details.SummaryText.Text!;
        Assert.Contains("ID: test.echo", summary);
        Assert.Contains("Runtime: /bin/sh", summary);
        Assert.Contains("Entry: main.sh", summary);
        Assert.Contains("Publisher: Test Publisher", summary);
        Assert.Contains("API revision: 1", summary);
        Assert.Contains("Discovery mode: entry file", summary);
        Assert.Contains("UI slots: output-tab", details.ContributionsText.Text);
        Assert.Contains("Themes: night", details.ContributionsText.Text);
        Assert.Contains("Commands/shortcuts: lint-all", details.ContributionsText.Text);
        Assert.Contains("Read-only views: report", details.ContributionsText.Text);
        Assert.Contains("Lint (lint) in script-editor, bottom bar, icon " + Path.Combine(world.UserRoot, "echo", "images", "lint.png"), details.ActionsText.Text);
        Assert.Contains("Format (fmt) in script-editor, top bar", details.ActionsText.Text);
        Assert.NotNull(details.CapabilityBox("script-read"));
        Assert.NotNull(details.CapabilityBox("file-read"));
        Assert.False(details.ErrorText.IsVisible);
        Shots.Save(details, "extension-details-" + theme);

        ExternalPrograms.ClearSuppressed();
        TestUi.Click(details.OpenButton);
        Assert.Contains("file " + Path.Combine(world.UserRoot, "echo", "main.sh"), ExternalPrograms.Suppressed);
    }

    [AvaloniaFact]
    public async Task Empty_folders_say_no_extensions_installed()
    {
        await using var world = new TestWorld();
        ExtensionsWindow window = world.ShowWindow();
        await TestUi.WaitUntilAsync(() => window.EmptyLabel.IsVisible, what: "the empty label");
        Assert.Empty(window.Rows);
    }

    [AvaloniaFact]
    public async Task Enable_disable_output_tab_and_approval_reach_the_state_file()
    {
        if (!TestWorld.ShellAvailable) return;
        await using var world = new TestWorld();
        world.WriteEcho();
        ExtensionsWindow window = world.ShowWindow();
        await TestUi.WaitUntilAsync(() => window.Rows.Count == 1, what: "the row");
        ExtensionRow row = window.Rows[0];
        window.ExtensionList.SelectedItem = row;

        TestUi.Click(TestWorld.ControlOfRow<CheckBox>(window, row));
        await TestUi.WaitUntilAsync(() => world.StateLine("test.echo") == "test.echo 1 0", what: "enabled in the state file");
        await world.WaitForStatusAsync("test.echo", ExtensionStatus.Running);
        await TestUi.WaitUntilAsync(() => row.Title == "Echo 1.2.0 (running)", what: "the row shows running");
        Assert.Same(row, window.ExtensionList.SelectedItem);
        await TestUi.WaitUntilAsync(() => (window.LogView.Text ?? "").Contains("ready to go"), what: "the log view");
        Assert.Equal("Log of Echo", window.LogTitle.Text);

        TestUi.Click(TestWorld.ControlOfRow<ToggleButton>(window, row, toggle => Equals(toggle.Content, "Output tab")));
        await TestUi.WaitUntilAsync(() => world.StateLine("test.echo") == "test.echo 1 1", what: "output tab in the state file");
        Assert.True(row.OutputToTab);

        TestUi.Click(TestWorld.ButtonOfRow(window, row, "Details"));
        ExtensionDetailsWindow details = window.DetailsWindows["test.echo"];
        TestUi.Click(details.CapabilityBox("script-read")!);
        await TestUi.WaitUntilAsync(() => world.StateLine("test.echo") == "test.echo 1 1 script-read", what: "the approval in the state file");
        TestUi.Click(details.CapabilityBox("file-read")!);
        await TestUi.WaitUntilAsync(() => world.StateLine("test.echo") == "test.echo 1 1 file-read script-read", what: "both approvals");
        TestUi.Click(details.CapabilityBox("script-read")!);
        await TestUi.WaitUntilAsync(() => world.StateLine("test.echo") == "test.echo 1 1 file-read", what: "one approval taken back");
        details.Close();

        TestUi.Click(TestWorld.ControlOfRow<CheckBox>(window, row));
        await TestUi.WaitUntilAsync(() => world.StateLine("test.echo") == "test.echo 0 1 file-read", what: "disabled in the state file");
        await world.WaitForStatusAsync("test.echo", ExtensionStatus.Disabled);
        await TestUi.WaitUntilAsync(() => row.Title == "Echo 1.2.0 (disabled)", what: "the row shows disabled");
        Assert.Same(row, Assert.Single(window.Rows));
    }

    [AvaloniaFact]
    public async Task Row_menu_toggles_enabled_and_output_tab()
    {
        await using var world = new TestWorld();
        world.WriteIdle(world.UserRoot, "idle", "test.idle", "Idle");
        ExtensionsWindow window = world.ShowWindow();
        await TestUi.WaitUntilAsync(() => window.Rows.Count == 1, what: "the row");
        ExtensionRow row = window.Rows[0];
        Assert.Equal("Enable", row.EnableMenuText);
        Assert.Equal("Send output to tab", row.OutputMenuText);
        row.ToggleOutputCommand.Execute(null);
        await TestUi.WaitUntilAsync(() => world.StateLine("test.idle") == "test.idle 0 1", what: "output tab on");
        Assert.Equal("Stop sending output to tab", row.OutputMenuText);
        row.ToggleOutputCommand.Execute(null);
        await TestUi.WaitUntilAsync(() => world.StateLine("test.idle") == "test.idle 0 0", what: "output tab off");
    }

    [AvaloniaFact]
    public async Task Remove_asks_naming_the_folder_and_deletes_only_that_folder()
    {
        await using var world = new TestWorld();
        string first = world.WriteIdle(world.UserRoot, "first", "test.first", "First");
        string second = world.WriteIdle(world.UserRoot, "second", "test.second", "Second");
        ExtensionsWindow window = world.ShowWindow();
        await TestUi.WaitUntilAsync(() => window.Rows.Count == 2, what: "two rows");
        ExtensionRow row = window.RowOf("test.first")!;
        ExtensionRow other = window.RowOf("test.second")!;
        window.ExtensionList.SelectedItem = other;

        TestUi.Click(TestWorld.ButtonOfRow(window, row, "Remove"));
        RcDialog dialog = await TestWorld.WaitForDialogAsync();
        Assert.Contains("Remove extension 'First'?", dialog.Message);
        Assert.Contains(first, dialog.Message);
        Shots.Save(dialog, "extension-remove-confirm");
        dialog.Cancel();
        await TestUi.WaitUntilAsync(() => TestWorld.OpenDialog() is null, what: "the dialog closed");
        Assert.True(Directory.Exists(first));

        TestUi.Click(TestWorld.ButtonOfRow(window, row, "Remove"));
        dialog = await TestWorld.WaitForDialogAsync();
        dialog.Accept();
        await TestUi.WaitUntilAsync(() => !Directory.Exists(first) && window.Rows.Count == 1, what: "the package removed");
        Assert.True(Directory.Exists(second));
        Assert.True(Directory.Exists(world.UserRoot));
        Assert.Same(other, window.ExtensionList.SelectedItem);
        Assert.Same(other, window.Rows[0]);
    }

    [AvaloniaFact]
    public async Task Closing_the_window_while_remove_asks_deletes_nothing()
    {
        await using var world = new TestWorld();
        string first = world.WriteIdle(world.UserRoot, "first", "test.first", "First");
        ExtensionsWindow window = world.ShowWindow();
        await TestUi.WaitUntilAsync(() => window.Rows.Count == 1, what: "the row");
        Task removing = window.RemoveAsync(window.Rows[0]);
        RcDialog dialog = await TestWorld.WaitForDialogAsync();
        window.Close();
        if (!dialog.IsClosed) dialog.Accept();
        await removing;
        Assert.True(Directory.Exists(first));
    }

    [AvaloniaFact]
    public async Task Refresh_finds_new_packages_and_keeps_the_selection()
    {
        await using var world = new TestWorld();
        world.WriteIdle(world.UserRoot, "b", "test.b", "Bee");
        ExtensionsWindow window = world.ShowWindow();
        await TestUi.WaitUntilAsync(() => window.Rows.Count == 1, what: "the row");
        ExtensionRow bee = window.Rows[0];
        window.ExtensionList.SelectedItem = bee;
        world.WriteIdle(world.UserRoot, "a", "test.a", "Ay");
        TestUi.Click(window.RefreshButton);
        await TestUi.WaitUntilAsync(() => window.Rows.Count == 2, what: "the new row");
        Assert.Same(bee, window.RowOf("test.b"));
        Assert.Same(bee, window.ExtensionList.SelectedItem);
    }

    [AvaloniaFact]
    public async Task Open_folder_opens_the_users_extensions_folder()
    {
        await using var world = new TestWorld();
        ExtensionsWindow window = world.ShowWindow();
        ExternalPrograms.ClearSuppressed();
        TestUi.Click(window.OpenFolderButton);
        Assert.Contains("file " + world.UserRoot, ExternalPrograms.Suppressed);
        TestUi.Click(window.CloseButton);
        Assert.False(window.IsVisible);
        Assert.False(window.IsClosed);
        Assert.Same(window, world.Host.ShowWindow(null));
        Assert.True(window.IsVisible);
    }
}
