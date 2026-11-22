using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;
using ProtocolKind = RemoteControl.Protocol.Session.ScriptKind;
using ShellKind = RemoteControl.Ui.Shell.ScriptKind;

namespace RemoteControl.Ui.Scripts.Tests;

public sealed class EditorWindowTests
{
    private static EditorWindow OpenWeapon(TestSession test, string script = "echo(1);")
    {
        EditorWindow window = Helpers.OpenEditor(test, new ScriptRequest(ShellKind.Weapon, "Sword"));
        test.Fake.RaiseScript(ProtocolKind.Weapon, "Sword", script, image: "sword.png");
        TestUi.Settle();
        return window;
    }

    [AvaloniaFact]
    public void A_weapon_is_requested_and_filled_in_when_it_arrives()
    {
        using TestSession test = Helpers.Session();
        EditorWindow window = Helpers.OpenEditor(test, new ScriptRequest(ShellKind.Weapon, "Sword"));
        Assert.Equal("Weapon: Sword (Test Server)", window.Title);
        Assert.Equal("Sword", test.Fake.LastCall(nameof(IRcSession.RequestWeaponScriptAsync))!.Argument<string>(0));
        Assert.True(window.IsAwaiting);
        Assert.True(window.Editor.IsReadOnly);
        test.Fake.RaiseScript(ProtocolKind.Weapon, "Other", "nope");
        Assert.True(window.IsAwaiting);
        test.Fake.RaiseScript(ProtocolKind.Weapon, "Sword", "echo(1);", image: "sword.png");
        TestUi.Settle();
        Assert.False(window.IsAwaiting);
        Assert.Equal("echo(1);", window.Editor.Text);
        Assert.Equal("sword.png", window.IconField.Text);
        Assert.False(window.Editor.IsReadOnly);
        Assert.False(window.IsDirty);
        Assert.Same(window, Helpers.OpenEditor(test, new ScriptRequest(ShellKind.Weapon, "Sword")));
        Assert.Single(Helpers.Editors(test));
    }

    [AvaloniaFact]
    public async Task Saving_is_clean_only_after_the_text_went_out()
    {
        using TestSession test = Helpers.Session();
        EditorWindow window = OpenWeapon(test);
        window.Editor.ReplaceText("echo(2);");
        Assert.True(window.IsDirty);
        Assert.True(await window.SaveAsync());
        RecordedCall call = test.Fake.LastCall(nameof(IRcSession.UpdateWeaponAsync))!;
        Assert.Equal(["Sword", "sword.png", "echo(2);"], call.Arguments.Cast<string>());
        Assert.False(window.IsDirty);
        Assert.Equal(string.Empty, window.StatusMessage);

        test.Fake.SendResult = false;
        window.Editor.ReplaceText("echo(3);");
        Assert.False(await window.SaveAsync());
        Assert.True(window.IsDirty);
        Assert.Contains("refused", window.StatusMessage);
    }

    [AvaloniaFact]
    public async Task With_the_nc_down_the_text_stays_dirty_and_the_user_is_told()
    {
        using TestSession test = Helpers.Session();
        EditorWindow window = OpenWeapon(test);
        window.Editor.ReplaceText("echo(2);");
        test.Fake.RaiseNcDisconnected();
        test.Fake.ClearCalls();
        Assert.False(await window.SaveAsync());
        Assert.False(test.Fake.WasCalled(nameof(IRcSession.UpdateWeaponAsync)));
        Assert.True(window.IsDirty);
        Assert.Contains("not connected", window.StatusMessage);
        Assert.Contains(window.Title!, ScriptsSession.For(test.Context).UnsavedTitles);
    }

    [AvaloniaFact]
    public async Task Ctrl_s_and_the_apply_button_save()
    {
        using TestSession test = Helpers.Session();
        EditorWindow window = OpenWeapon(test);
        window.Editor.ReplaceText("echo(5);");
        window.Editor.FocusText();
        TestUi.PressKey(window, Key.S, RawInputModifiers.Control);
        await TestUi.WaitUntilAsync(() => test.Fake.WasCalled(nameof(IRcSession.UpdateWeaponAsync)));
        window.Editor.ReplaceText("echo(6);");
        TestUi.Click(TestUi.Find<Button>(window, "ApplyButton"));
        await TestUi.WaitUntilAsync(() => test.Fake.CallsTo(nameof(IRcSession.UpdateWeaponAsync)).Count == 2);
        Assert.False(window.IsDirty);
    }

    [AvaloniaFact]
    public async Task Closing_a_dirty_editor_asks_save_discard_or_cancel()
    {
        using TestSession test = Helpers.Session();
        EditorWindow window = OpenWeapon(test);
        window.Editor.ReplaceText("changed");
        TestUi.Click(TestUi.Find<Button>(window, "CloseButton"));
        UnsavedChangesDialog dialog = await Helpers.FindWindowAsync<UnsavedChangesDialog>();
        Shots.Save(dialog, "unsaved-changes-dialog");
        dialog.Finish(UnsavedChoice.Cancel);
        TestUi.Settle();
        Assert.False(window.IsClosed);

        TestUi.Click(TestUi.Find<Button>(window, "CloseButton"));
        dialog = await Helpers.FindWindowAsync<UnsavedChangesDialog>();
        dialog.Finish(UnsavedChoice.Save);
        await TestUi.WaitUntilAsync(() => window.IsClosed);
        Assert.Equal("changed", test.Fake.LastCall(nameof(IRcSession.UpdateWeaponAsync))!.Argument<string>(2));

        EditorWindow second = OpenWeapon(test);
        second.Editor.ReplaceText("lost");
        TestUi.Click(TestUi.Find<Button>(second, "CloseButton"));
        dialog = await Helpers.FindWindowAsync<UnsavedChangesDialog>();
        dialog.Finish(UnsavedChoice.Discard);
        await TestUi.WaitUntilAsync(() => second.IsClosed);
        Assert.Single(test.Fake.CallsTo(nameof(IRcSession.UpdateWeaponAsync)));
    }

    [AvaloniaFact]
    public async Task Save_choice_with_the_nc_down_keeps_the_window_open()
    {
        using TestSession test = Helpers.Session();
        EditorWindow window = OpenWeapon(test);
        window.Editor.ReplaceText("changed");
        test.Fake.IsNcAuthenticated = false;
        TestUi.Click(TestUi.Find<Button>(window, "CloseButton"));
        (await Helpers.FindWindowAsync<UnsavedChangesDialog>()).Finish(UnsavedChoice.Save);
        await TestUi.WaitUntilAsync(() => window.StatusMessage.Contains("not connected", StringComparison.Ordinal));
        Assert.False(window.IsClosed);
    }

    [AvaloniaFact]
    public async Task Script_editors_service_lists_updates_and_saves()
    {
        using TestSession test = Helpers.Session();
        ScriptsFeature.Start(test.Context);
        IScriptEditors? service = test.Context.Services.Get<IScriptEditors>();
        Assert.NotNull(service);
        ScriptsFeature.Start(test.Context);
        Assert.Same(service, test.Context.Services.Get<IScriptEditors>());
        int changes = 0;
        service!.Changed += (_, _) => changes++;
        EditorWindow window = OpenWeapon(test, "a();");
        Assert.True(changes > 0);
        ScriptEditorSnapshot snapshot = Assert.Single(service.Editors);
        Assert.Equal("Weapon: Sword (Test Server)", snapshot.Title);
        Assert.Equal(ShellKind.Weapon, snapshot.Kind);
        Assert.Equal("a();", snapshot.Text);
        Assert.False(snapshot.IsModified);

        Assert.True(service.Update(snapshot.Title, text => text + "\nb();"));
        Assert.Equal("a();\nb();", window.Editor.Text);
        Assert.False(service.Update(snapshot.Title, _ => null));
        Assert.False(service.Update("missing", text => text));
        Assert.Equal([snapshot.Title], service.UnsavedTitles);
        Assert.True(service.Editors[0].IsModified);
        window.Editor.Editor.Undo();
        Assert.Equal("a();", window.Editor.Text);
        Assert.True(service.Update(snapshot.Title, text => "c();"));
        Assert.True(await service.SaveAsync(snapshot.Title));
        Assert.Equal("c();", test.Fake.LastCall(nameof(IRcSession.UpdateWeaponAsync))!.Argument<string>(2));
        Assert.Empty(service.UnsavedTitles);
        Assert.False(await service.SaveAsync("missing"));
        window.Close();
        TestUi.Settle();
        Assert.Empty(service.Editors);
    }

    [AvaloniaFact]
    public async Task A_reconnect_keeps_the_text_and_saves_through_the_new_connection()
    {
        using TestSession test = Helpers.Session();
        EditorWindow window = OpenWeapon(test);
        window.Editor.ReplaceText("mine");
        FakeRcSession old = test.Fake;
        var replacement = new FakeRcSession("tester") { IsNcConnected = true, IsNcAuthenticated = true };
        test.Context.Replace(replacement, test.Login);
        TestUi.Settle();
        old.ClearCalls();
        Assert.Equal("mine", window.Editor.Text);
        Assert.True(window.IsDirty);
        Assert.True(await window.SaveAsync());
        Assert.Equal("mine", replacement.LastCall(nameof(IRcSession.UpdateWeaponAsync))!.Argument<string>(2));
        Assert.False(old.WasCalled(nameof(IRcSession.UpdateWeaponAsync)));
        replacement.RaiseScript(ProtocolKind.Weapon, "Sword", "server text");
        old.RaiseScript(ProtocolKind.Weapon, "Sword", "from the old connection");
        TestUi.Settle();
        Assert.Equal("server text", window.Editor.Text);
    }

    [AvaloniaFact]
    public void An_editor_waiting_without_the_nc_asks_again_when_the_nc_comes()
    {
        using TestSession test = Helpers.Session(nc: false);
        EditorWindow window = Helpers.OpenEditor(test, new ScriptRequest(ShellKind.Class, "Util"));
        Assert.Contains("Not connected", window.StatusMessage);
        Assert.False(test.Fake.WasCalled(nameof(IRcSession.RequestClassScriptAsync)));
        test.Fake.RaiseNcConnected();
        TestUi.Settle();
        Assert.True(test.Fake.WasCalled(nameof(IRcSession.RequestClassScriptAsync)));

        var replacement = new FakeRcSession("tester") { IsNcConnected = true, IsNcAuthenticated = true };
        test.Context.Replace(replacement, test.Login);
        TestUi.Settle();
        Assert.True(replacement.WasCalled(nameof(IRcSession.RequestClassScriptAsync)));
        replacement.RaiseScript(ProtocolKind.Class, "Util", "function f() {}");
        TestUi.Settle();
        Assert.Equal("function f() {}", window.Editor.Text);
    }

    [AvaloniaFact]
    public async Task A_new_class_is_created_on_the_first_save_with_text()
    {
        using TestSession test = Helpers.Session();
        ScriptListWindow list = Helpers.Open<ScriptListWindow>(test, ToolIds.Classes);
        Task adding = list.AddAsync();
        FormDialog form = await Helpers.FindWindowAsync<FormDialog>();
        form.Set("Name", "Fresh");
        form.Accept();
        await adding;
        EditorWindow window = Assert.Single(Helpers.Editors(test));
        Assert.Equal("Class: Fresh (Test Server)", window.Title);
        Assert.False(test.Fake.WasCalled(nameof(IRcSession.RequestClassScriptAsync)));
        Assert.True(await window.SaveAsync());
        Assert.False(test.Fake.WasCalled(nameof(IRcSession.AddClassAsync)));
        window.Editor.ReplaceText("function a() {}");
        Assert.True(await window.SaveAsync());
        Assert.Equal("function a() {}", test.Fake.LastCall(nameof(IRcSession.AddClassAsync))!.Argument<string>(1));
        test.Fake.RaiseClassAdded("Fresh");
        window.Editor.ReplaceText("function b() {}");
        Assert.True(await window.SaveAsync());
        Assert.True(test.Fake.WasCalled(nameof(IRcSession.UpdateClassAsync)));
    }

    [AvaloniaFact]
    public void An_npc_script_is_matched_by_its_id()
    {
        using TestSession test = Helpers.Session();
        EditorWindow window = Helpers.OpenEditor(test, new ScriptRequest(ShellKind.Npc, "Guard", 1002));
        Assert.Equal("Npcscript: Guard (Test Server)", window.Title);
        Assert.Equal(1002, test.Fake.LastCall(nameof(IRcSession.RequestNpcScriptAsync))!.Argument<int>(0));
        test.Fake.RaiseScript(ProtocolKind.Npc, "Guard", "wrong", id: 1003);
        test.Fake.RaiseScript(ProtocolKind.Npc, "Guard", "right", id: 1002);
        TestUi.Settle();
        Assert.Equal("right", window.Editor.Text);
    }

    [AvaloniaFact]
    public async Task A_text_of_another_group_is_saved_by_its_own_function()
    {
        using TestSession test = Helpers.Session(nc: false);
        string? saved = null;
        EditorWindow window = Helpers.OpenEditor(test, new ScriptRequest(ShellKind.Text, "levels/test.ini", Text: "a=1", Save: text => { saved = text; return Task.FromResult(true); }));
        Assert.Equal("levels/test.ini - Test Server", window.Title);
        Assert.Equal(ScriptEditorMode.Configuration, window.Editor.Mode);
        Assert.Equal("Save", TestUi.Find<Button>(window, "ApplyButton").Content);
        window.Editor.ReplaceText("a=2");
        Assert.True(await window.SaveAsync());
        Assert.Equal("a=2", saved);
        Assert.False(window.IsDirty);

        EditorWindow viewer = Helpers.OpenEditor(test, new ScriptRequest(ShellKind.Text, "readonly.txt", Text: "x"));
        Assert.True(viewer.Editor.IsReadOnly);
        Assert.False(TestUi.Find<Button>(viewer, "ApplyButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task Server_options_never_lose_unsaved_text_when_the_server_sends_a_new_one()
    {
        using TestSession test = Helpers.Session();
        EditorWindow window = Helpers.Open<EditorWindow>(test, ToolIds.ServerOptions);
        Assert.Equal("Server Options - Test Server", window.Title);
        Assert.True(test.Fake.WasCalled(nameof(IRcSession.RequestServerOptionsAsync)));
        test.Fake.RaiseServerOptions("a=1");
        TestUi.Settle();
        Assert.Equal("a=1", window.Editor.Text);
        test.Fake.RaiseServerOptions("a=2");
        TestUi.Settle();
        Assert.Equal("a=2", window.Editor.Text);
        window.Editor.ReplaceText("mine=1");
        test.Fake.RaiseServerOptions("a=3");
        TestUi.Settle();
        Assert.Equal("mine=1", window.Editor.Text);
        Assert.True(window.IsReloadOffered);
        Shots.Save(window, "server-options-reload-offered");
        TestUi.Click(TestUi.Find<Button>(window, "KeepButton"));
        Assert.False(window.IsReloadOffered);
        Assert.True(window.IsDirty);
        test.Fake.RaiseServerOptions("a=4");
        TestUi.Settle();
        TestUi.Click(TestUi.Find<Button>(window, "ReloadButton"));
        Assert.Equal("a=4", window.Editor.Text);
        Assert.False(window.IsDirty);
        window.Editor.ReplaceText("b=1");
        Assert.True(await window.SaveAsync());
        Assert.Equal("b=1", test.Fake.LastCall(nameof(IRcSession.UploadServerOptionsAsync))!.Argument<string>(0));
    }

    [AvaloniaFact]
    public async Task Server_flags_and_folder_config_upload_their_texts()
    {
        using TestSession test = Helpers.Session(nc: false);
        EditorWindow flags = Helpers.Open<EditorWindow>(test, ToolIds.ServerFlags);
        test.Fake.RaiseServerFlags("server.x=1\n");
        TestUi.Settle();
        flags.Editor.ReplaceText("server.x=2\n");
        Assert.True(await flags.SaveAsync());
        Assert.Equal("server.x=2\n", test.Fake.LastCall(nameof(IRcSession.UploadServerFlagsAsync))!.Argument<string>(0));
        EditorWindow folders = Helpers.Open<EditorWindow>(test, ToolIds.FolderConfig);
        Assert.Equal("Folder Config - Test Server", folders.Title);
        test.Fake.RaiseFolderConfig("file levels/*.nw");
        TestUi.Settle();
        folders.Editor.ReplaceText("file levels/*.graal");
        Assert.True(await folders.SaveAsync());
        Assert.True(test.Fake.WasCalled(nameof(IRcSession.UploadFolderConfigAsync)));
        test.Fake.IsAuthenticated = false;
        folders.Editor.ReplaceText("x");
        Assert.False(await folders.SaveAsync());
        Assert.Contains("not connected", folders.StatusMessage);
    }

    [AvaloniaFact]
    public async Task The_level_list_is_requested_shown_and_sent()
    {
        using TestSession test = Helpers.Session();
        EditorWindow window = Helpers.Open<EditorWindow>(test, ToolIds.Levels);
        Assert.Equal("Levels - Test Server", window.Title);
        Assert.True(test.Fake.WasCalled(nameof(IRcSession.RequestLevelListAsync)));
        test.Fake.RaiseLevelList("a.nw\r\n\r\nb.nw");
        TestUi.Settle();
        Assert.Equal("a.nw\nb.nw", window.Editor.Text);
        window.Editor.ReplaceText("a.nw\nc.nw");
        Assert.True(await window.SaveAsync());
        RecordedCall call = test.Fake.LastCall(nameof(IRcSession.SendNcPacketAsync))!;
        Assert.Equal(151, call.Argument<int>(0));
        Assert.Equal(Protocol.Wire.CommaText.Tokenize("a.nw\nc.nw"), Protocol.Wire.RcText.GetString(call.Argument<byte[]>(1)));

        // Blank lines are not sent as empty level names, and a list that would read as GStrings gets a line quoted.
        window.Editor.ReplaceText("a.nw\n\nmy level.nw\n");
        Assert.True(await window.SaveAsync());
        Assert.Equal("a.nw,\"my level.nw\"", Protocol.Wire.RcText.GetString(test.Fake.LastCall(nameof(IRcSession.SendNcPacketAsync))!.Argument<byte[]>(1)));
        string first = "~" + new string('a', 94);
        window.Editor.ReplaceText(first + "\n" + new string('b', 12));
        Assert.True(await window.SaveAsync());
        byte[] sent = test.Fake.LastCall(nameof(IRcSession.SendNcPacketAsync))!.Argument<byte[]>(1);
        Assert.Equal(first + ",\"" + new string('b', 12) + "\"", Protocol.Wire.RcText.GetString(sent));
        Assert.False(Protocol.Wire.LineListText.IsExactGStringChain(sent));
    }

    [AvaloniaFact]
    public void Extension_buttons_get_the_editor()
    {
        using TestSession test = Helpers.Session();
        var actions = new FakeActions();
        test.Shell.Provide<IExtensionActions>(actions);
        EditorWindow window = OpenWeapon(test, "x();");
        Button top = TestUi.FindAll<Button>(window).Single(button => Equals(button.Content, "Lint"));
        TestUi.Click(top);
        (string id, ScriptEditorSnapshot editor) = Assert.Single(actions.Invoked);
        Assert.Equal("lint", id);
        Assert.Equal("x();", editor.Text);
        Assert.Contains(TestUi.FindAll<Button>(window), button => Equals(button.Content, "Upload"));
        Shots.Save(window, "weapon-editor-extension-buttons");
    }

    [AvaloniaFact]
    public void Window_places_are_remembered_and_editors_reopen_when_the_nc_connects()
    {
        using TestSession test = Helpers.Session(nc: false);
        string file = Path.Combine(test.Environment.UserDirectory, ScriptWindowRecords.FileName);
        var records = new ScriptWindowRecords(file);
        string key = ScriptWindowRecords.SessionKey("Test Server", "tester");
        records.Update(new ScriptWindowRecord(key, "weapon", "Bow", 40, 50, 640, 480));
        records.Update(new ScriptWindowRecord(key, "class", "Lib\tx", 40, 50, 640, 480));
        records.Update(new ScriptWindowRecord("Other\x1fsomeone", "weapon", "Axe", 1, 2, 300, 300));
        Assert.Contains("Lib%09x", File.ReadAllText(file));
        ScriptsFeature.Start(test.Context);
        Assert.Empty(Helpers.Editors(test));
        test.Fake.RaiseNcConnected();
        TestUi.Settle();
        Assert.Equal(2, Helpers.Editors(test).Count);
        Assert.Equal("Bow", test.Fake.LastCall(nameof(IRcSession.RequestWeaponScriptAsync))!.Argument<string>(0));
        Assert.Equal("Lib\tx", test.Fake.LastCall(nameof(IRcSession.RequestClassScriptAsync))!.Argument<string>(0));
        EditorWindow bow = Helpers.Editors(test).Single(window => window.Target.Name == "Bow");
        Assert.Equal(640, bow.Width);
        test.Fake.RaiseScript(ProtocolKind.Weapon, "Bow", "x");
        TestUi.Settle();
        TestUi.Click(TestUi.Find<Button>(bow, "CloseButton"));
        TestUi.Settle();
        Assert.True(bow.IsClosed);
        Assert.Null(records.Find(key, "weapon", "Bow"));
        Assert.NotNull(records.Find(key, "class", "Lib\tx"));
        Assert.NotNull(records.Find("Other\x1fsomeone", "weapon", "Axe"));
    }

    [AvaloniaFact]
    public void Pictures_of_the_editor_windows()
    {
        foreach (string theme in new[] { "dracula", "light" })
        {
            using TestSession test = Helpers.Session(theme);
            EditorWindow weapon = OpenWeapon(test, "//#CLIENTSIDE\nfunction onCreated() {\n  echo(\"hi\");\n}\n");
            weapon.Editor.ReplaceText("//#CLIENTSIDE\nfunction onCreated() {\n  echo(\"hi\")\n}\n");
            test.Fake.RaiseNcDisconnected();
            _ = weapon.SaveAsync();
            TestUi.Settle();
            Shots.Save(weapon, "weapon-editor-" + theme);
            EditorWindow flags = Helpers.Open<EditorWindow>(test, ToolIds.ServerFlags);
            test.Fake.RaiseServerFlags("server.event=1\nserver.name=\"test\"\n");
            TestUi.Settle();
            Shots.Save(flags, "server-flags-" + theme);
        }
    }

    private sealed class FakeActions : IExtensionActions
    {
        public List<(string, ScriptEditorSnapshot)> Invoked { get; } = [];

        public event EventHandler? Changed;

        public IReadOnlyList<WindowActionButton> ActionsFor(string windowKind) =>
            windowKind == "script-editor" ? [new WindowActionButton("lint", "Lint", false, null), new WindowActionButton("upload", "Upload", true, null)] : [];

        public void Invoke(string actionId, SessionContext session, ScriptEditorSnapshot editor) => Invoked.Add((actionId, editor));

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
