using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;
using ProtocolKind = RemoteControl.Protocol.Session.ScriptKind;

namespace RemoteControl.Ui.Scripts.Tests;

public sealed class ListTests
{
    [AvaloniaFact]
    public void The_weapon_list_follows_the_server_and_keeps_its_selection()
    {
        using TestSession test = Helpers.Session();
        ScriptListWindow window = Helpers.Open<ScriptListWindow>(test, ToolIds.Weapons);
        Assert.Equal("Weapon/GUI-Script List - Test Server", window.Title);
        Assert.True(test.Fake.WasCalled(nameof(IRcSession.RequestWeaponListAsync)));
        test.Fake.RaiseWeaponList([new RcWeapon("Sword", "", ""), new RcWeapon("Bow", "", "")]);
        TestUi.Settle();
        Assert.Equal(["Bow", "Sword"], window.Names);
        window.List.SelectedItem = "Sword";
        test.Fake.RaiseWeaponList([new RcWeapon("Sword", "", ""), new RcWeapon("Bow", "", ""), new RcWeapon("Axe", "", "")]);
        TestUi.Settle();
        Assert.Equal(["Axe", "Bow", "Sword"], window.Names);
        Assert.Equal("Sword", window.List.SelectedItem);
        test.Fake.RaiseWeaponList([new RcWeapon("Sword", "", "")]);
        TestUi.Settle();
        Assert.Equal(["Sword"], window.Names);
        Assert.Equal("Sword", window.List.SelectedItem);
        Shots.Save(window, "weapon-list");
    }

    [AvaloniaFact]
    public async Task Edit_opens_the_editor_and_add_and_delete_go_to_the_server()
    {
        using TestSession test = Helpers.Session();
        ScriptListWindow window = Helpers.Open<ScriptListWindow>(test, ToolIds.Weapons);
        test.Fake.RaiseWeaponList([new RcWeapon("Sword", "", "")]);
        TestUi.Settle();
        window.List.SelectedItem = "Sword";
        await window.EditAsync();
        Assert.Equal("Weapon: Sword (Test Server)", Assert.Single(Helpers.Editors(test)).Title);

        Task adding = window.AddAsync();
        FormDialog form = await Helpers.FindWindowAsync<FormDialog>();
        Shots.Save(form, "add-weapon-dialog");
        form.Set("Name", "Staff");
        form.Set("Icon", "staff.png");
        form.Accept();
        await adding;
        Assert.Equal(["Staff", "staff.png", ""], test.Fake.LastCall(nameof(IRcSession.AddWeaponAsync))!.Arguments.Cast<string>());
        test.Fake.RaiseWeaponList([new RcWeapon("Sword", "", ""), new RcWeapon("Staff", "staff.png", "")]);
        TestUi.Settle();
        Assert.Contains(Helpers.Editors(test), editor => editor.Target.Name == "Staff");

        window.List.SelectedItem = "Sword";
        Task deleting = window.DeleteAsync();
        RcDialog confirm = TestUi.FindDialog();
        Assert.Equal("Delete Weapon/GUI Script Sword?", confirm.Message);
        confirm.Cancel();
        await deleting;
        Assert.False(test.Fake.WasCalled(nameof(IRcSession.DeleteWeaponAsync)));
        deleting = window.DeleteAsync();
        TestUi.FindDialog().Accept();
        await deleting;
        Assert.Equal("Sword", test.Fake.LastCall(nameof(IRcSession.DeleteWeaponAsync))!.Argument<string>(0));
    }

    [AvaloniaFact]
    public async Task The_class_list_follows_add_and_delete_events()
    {
        using TestSession test = Helpers.Session();
        test.Fake.ClassList.Add(new RcClass("Base", ""));
        ScriptListWindow window = Helpers.Open<ScriptListWindow>(test, ToolIds.Classes);
        Assert.Equal("Classes - Test Server", window.Title);
        Assert.Equal(["Base"], window.Names);
        window.List.SelectedItem = "Base";
        test.Fake.RaiseClassAdded("Alpha");
        test.Fake.RaiseClassAdded("Zeta");
        Assert.Equal(["Alpha", "Base", "Zeta"], window.Names);
        Assert.Equal("Base", window.List.SelectedItem);
        test.Fake.RaiseClassDeleted("Alpha");
        Assert.Equal(["Base", "Zeta"], window.Names);
        Assert.Equal("Base", window.List.SelectedItem);
        Task deleting = window.DeleteAsync();
        RcDialog confirm = TestUi.FindDialog();
        Assert.Equal("Delete Class Base?", confirm.Message);
        confirm.Accept();
        await deleting;
        Assert.Equal("Base", test.Fake.LastCall(nameof(IRcSession.DeleteClassAsync))!.Argument<string>(0));
    }

    [AvaloniaFact]
    public void Lists_without_the_nc_say_so_and_fill_in_when_it_connects()
    {
        using TestSession test = Helpers.Session(nc: false);
        test.Fake.NpcList.Add(new RcNpc(1000, "Guard", "OBJECT", "start.nw", ""));
        ScriptListWindow weapons = Helpers.Open<ScriptListWindow>(test, ToolIds.Weapons);
        NpcListWindow npcs = Helpers.Open<NpcListWindow>(test, ToolIds.Npcs);
        Assert.True(weapons.ShowsNotConnected);
        Assert.True(npcs.ShowsNotConnected);
        Assert.Empty(npcs.Rows);
        Assert.False(test.Fake.WasCalled(nameof(IRcSession.RequestWeaponListAsync)));
        Shots.Save(npcs, "npc-list-not-connected");
        test.Fake.RaiseNcConnected();
        TestUi.Settle();
        Assert.False(weapons.ShowsNotConnected);
        Assert.False(npcs.ShowsNotConnected);
        Assert.Single(npcs.Rows);
        Assert.True(test.Fake.WasCalled(nameof(IRcSession.RequestWeaponListAsync)));
        test.Fake.RaiseNcDisconnected();
        TestUi.Settle();
        Assert.True(npcs.ShowsNotConnected);
    }

    [AvaloniaFact]
    public void The_npc_list_follows_events_sorts_and_keeps_its_selection()
    {
        using TestSession test = Helpers.Session();
        test.Fake.NpcList.Add(new RcNpc(1001, "Shop", "OBJECT", "town.nw", ""));
        test.Fake.NpcList.Add(new RcNpc(1000, "Guard", "POLICE", "start.nw", ""));
        NpcListWindow window = Helpers.Open<NpcListWindow>(test, ToolIds.Npcs);
        Assert.Equal("NPCs - Test Server", window.Title);
        Assert.Equal([1000, 1001], window.Rows.Select(row => row.Id));
        window.List.SelectedItem = window.Rows[1];
        test.Fake.RaiseNpcAdded(new RcNpc(999, "Banker", "OBJECT", "bank.nw", ""));
        Assert.Equal([999, 1000, 1001], window.Rows.Select(row => row.Id));
        Assert.Equal(1001, ((NpcRow)window.List.SelectedItem!).Id);
        window.SortBy(1);
        Assert.Equal(["Banker", "Guard", "Shop"], window.Rows.Select(row => row.Name));
        window.SortBy(1);
        Assert.Equal(["Shop", "Guard", "Banker"], window.Rows.Select(row => row.Name));
        Assert.Equal(1001, ((NpcRow)window.List.SelectedItem!).Id);
        test.Fake.RaiseNpcDeleted(1000);
        Assert.Equal(2, window.Rows.Count);
        Assert.Equal(1001, ((NpcRow)window.List.SelectedItem!).Id);
        Shots.Save(window, "npc-list");
    }

    [AvaloniaFact]
    public void The_npc_list_opens_flag_editors_only_for_its_own_requests()
    {
        using TestSession test = Helpers.Session();
        test.Fake.NpcList.Add(new RcNpc(1000, "Guard", "OBJECT", "start.nw", ""));
        NpcListWindow window = Helpers.Open<NpcListWindow>(test, ToolIds.Npcs);
        test.Fake.RaiseNpcFlags(1000, "pulled=1");
        test.Fake.RaiseNpcFlags(1001, "pulled=1");
        TestUi.Settle();
        Assert.Empty(Helpers.Editors(test));

        window.List.SelectedItem = window.Rows[0];
        window.EditFlags();
        EditorWindow flags = Assert.Single(Helpers.Editors(test));
        Assert.Equal("NPC Flags: Guard (Test Server)", flags.Title);
        Assert.Equal(1000, test.Fake.LastCall(nameof(IRcSession.RequestNpcFlagsAsync))!.Argument<int>(0));
        test.Fake.RaiseNpcFlags(1001, "other=1");
        TestUi.Settle();
        Assert.Single(Helpers.Editors(test));
        Assert.True(flags.IsAwaiting);
        test.Fake.RaiseNpcFlags(1000, "a=1");
        TestUi.Settle();
        Assert.Equal("a=1", flags.Editor.Text);
        flags.Editor.ReplaceText("a=2");
        test.Fake.RaiseNpcFlags(1000, "pulled=2");
        TestUi.Settle();
        Assert.Equal("a=2", flags.Editor.Text);
        Assert.Single(Helpers.Editors(test));
        Shots.Save(flags, "npc-flags-editor");
    }

    [AvaloniaFact]
    public async Task The_npc_menu_actions_reach_the_server()
    {
        using TestSession test = Helpers.Session();
        test.Fake.NpcList.Add(new RcNpc(1000, "Guard", "OBJECT", "start.nw", ""));
        NpcListWindow window = Helpers.Open<NpcListWindow>(test, ToolIds.Npcs);
        window.List.SelectedItem = window.Rows[0];
        window.EditScript();
        Assert.Equal(1000, test.Fake.LastCall(nameof(IRcSession.RequestNpcScriptAsync))!.Argument<int>(0));
        window.ViewAttributes();
        EditorWindow attributes = Helpers.Editors(test).Single(editor => editor.Target is NpcAttributesTarget);
        test.Fake.RaiseNpcAttributes(1000, "x=1");
        TestUi.Settle();
        Assert.Equal("x=1", attributes.Editor.Text);
        Assert.True(attributes.Editor.IsReadOnly);
        Assert.Equal("Attributes of NPC 1000 - Test Server", attributes.Title);
        await window.ResetAsync();
        Assert.True(test.Fake.WasCalled(nameof(IRcSession.ResetNpcAsync)));

        Task warping = window.WarpAsync();
        FormDialog warp = await Helpers.FindWindowAsync<FormDialog>();
        warp.Set("Level", "jail.nw");
        warp.Set("X", "10.5");
        warp.Set("Y", "20");
        warp.Accept();
        await warping;
        RecordedCall call = test.Fake.LastCall(nameof(IRcSession.WarpNpcAsync))!;
        Assert.Equal([(object?)1000, 10.5, 20.0, "jail.nw"], call.Arguments);

        Task deleting = window.DeleteAsync();
        RcDialog confirm = TestUi.FindDialog();
        Assert.Equal("Delete NPC 1000?", confirm.Message);
        confirm.Accept();
        await deleting;
        Assert.True(test.Fake.WasCalled(nameof(IRcSession.DeleteNpcAsync)));
    }

    [AvaloniaFact]
    public async Task Add_npc_proposes_a_free_id_remembers_values_and_opens_the_new_script()
    {
        using TestSession test = Helpers.Session();
        test.Fake.NpcList.Add(new RcNpc(1000, "Guard", "OBJECT", "start.nw", ""));
        NpcListWindow window = Helpers.Open<NpcListWindow>(test, ToolIds.Npcs);
        Task adding = window.AddAsync();
        FormDialog form = await Helpers.FindWindowAsync<FormDialog>();
        Assert.Equal("1001", form.Get("Id"));
        Assert.Equal("tester", form.Get("Scripter"));
        Assert.Equal("onlinestartlocal.nw", form.Get("Level"));
        Shots.Save(form, "add-npc-dialog");
        form.Set("Name", "Merchant");
        form.Set("Type", "HORSE");
        form.Set("X", "12");
        form.Accept();
        await adding;
        Assert.Equal([(object?)"Merchant", 1001, "HORSE", "tester", "onlinestartlocal.nw", "12", "30"], test.Fake.LastCall(nameof(IRcSession.CreateNpcAsync))!.Arguments);
        test.Fake.RaiseNpcAdded(new RcNpc(1001, "Merchant", "HORSE", "onlinestartlocal.nw", ""));
        TestUi.Settle();
        Assert.Equal(1001, test.Fake.LastCall(nameof(IRcSession.RequestNpcScriptAsync))!.Argument<int>(0));
        adding = window.AddAsync();
        form = await Helpers.FindWindowAsync<FormDialog>();
        Assert.Equal("HORSE", form.Get("Type"));
        Assert.Equal("12", form.Get("X"));
        Assert.Equal("1002", form.Get("Id"));
        form.Cancel();
        await adding;
    }

    [AvaloniaFact]
    public void A_list_survives_a_reconnect()
    {
        using TestSession test = Helpers.Session();
        NpcListWindow window = Helpers.Open<NpcListWindow>(test, ToolIds.Npcs);
        var replacement = new FakeRcSession("tester") { IsNcConnected = true, IsNcAuthenticated = true };
        replacement.NpcList.Add(new RcNpc(5, "New", "OBJECT", "a.nw", ""));
        test.Context.Replace(replacement, test.Login);
        TestUi.Settle();
        Assert.Equal([5], window.Rows.Select(row => row.Id));
        test.Fake.RaiseNpcAdded(new RcNpc(6, "Old", "OBJECT", "a.nw", ""));
        Assert.Single(window.Rows);
        replacement.RaiseNpcAdded(new RcNpc(7, "Another", "OBJECT", "a.nw", ""));
        Assert.Equal(2, window.Rows.Count);
    }

    [AvaloniaFact]
    public void Closing_a_list_hides_it_and_opening_shows_the_same_one()
    {
        using TestSession test = Helpers.Session();
        ScriptListWindow window = Helpers.Open<ScriptListWindow>(test, ToolIds.Weapons);
        TestUi.Click(TestUi.Find<Button>(window, "CloseButton"));
        Assert.False(window.IsVisible);
        Assert.False(window.IsClosed);
        Assert.Same(window, Helpers.Open<ScriptListWindow>(test, ToolIds.Weapons));
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void Pictures_of_the_lists_in_another_theme()
    {
        using TestSession test = Helpers.Session("light");
        test.Fake.NpcList.Add(new RcNpc(1000, "Guard", "POLICE", "start.nw", ""));
        test.Fake.ClassList.Add(new RcClass("util_functions", ""));
        Shots.Save(Helpers.Open<NpcListWindow>(test, ToolIds.Npcs), "npc-list-light");
        Shots.Save(Helpers.Open<ScriptListWindow>(test, ToolIds.Classes), "class-list-light");
        Helpers.Open<EditorWindow>(test, ToolIds.Levels);
        test.Fake.RaiseLevelList("onlinestartlocal.nw\nbank.nw");
        TestUi.Settle();
        Shots.Save(Helpers.Editors(test).Single(), "level-list-light");
    }
}
