namespace RemoteControl.Core.Tests;

public sealed class ListServerProfileTests
{
    private static ListServerProfileStore Open(string path)
    {
        var store = new ListServerProfileStore(path);
        store.Load();
        return store;
    }

    // Ported from tests/ListServerProfilesTests.cpp: the built-in profile is now Alpaxis and Retail is never added.
    [Fact]
    public void Missing_file_gives_exactly_the_built_in_profile()
    {
        using var folder = new TempFolder();
        ListServerProfileStore store = Open(folder.Combine("listservers.conf"));
        Assert.Equal([ListServerProfile.Default], store.Profiles);
        Assert.Equal("Alpaxis", store.Profiles[0].Name);
        Assert.Equal("listserver.alpaxis.net", store.Profiles[0].Host);
        Assert.Equal(14922, store.Profiles[0].Port);
        Assert.False(File.Exists(folder.Combine("listservers.conf")));
    }

    [Fact]
    public void Save_and_load_round_trips_and_keeps_the_order()
    {
        using var folder = new TempFolder();
        string path = folder.Combine("listservers.conf");
        ListServerProfileStore store = Open(path);
        Assert.Equal(ProfileChangeStatus.Saved, store.Add(new ListServerProfile("Moreno", "listserver.moreno.land", 14922)));
        Assert.Equal(ProfileChangeStatus.Saved, store.Add(new ListServerProfile("Local", "127.0.0.1", 14900)));
        Assert.Equal("Moreno\tlistserver.moreno.land\t14922\nLocal\t127.0.0.1\t14900\n", File.ReadAllText(path));
        ListServerProfileStore reloaded = Open(path);
        Assert.Equal(["Alpaxis", "Moreno", "Local"], reloaded.Profiles.Select(profile => profile.Name).ToArray());
    }

    [Fact]
    public void A_users_retail_and_official_lines_stay_exactly_as_they_are()
    {
        using var folder = new TempFolder();
        string path = folder.Combine("listservers.conf");
        const string text = "Retail\tlistserver.graalonline.com\t14922\nOfficial\tlistserver.graalonline.com\t14922\nMoreno\tlistserver.moreno.land\t14922\n";
        File.WriteAllText(path, text);
        ListServerProfileStore store = Open(path);
        Assert.Equal(["Alpaxis", "Retail", "Official", "Moreno"], store.Profiles.Select(profile => profile.Name).ToArray());
        Assert.Equal("listserver.graalonline.com", store.Profiles[1].Host);
        store.Add(new ListServerProfile("New", "new.example", 1));
        Assert.Equal(text + "New\tnew.example\t1\n", File.ReadAllText(path));
    }

    [Fact]
    public void A_file_without_the_built_in_profile_gets_it_first_without_writing()
    {
        using var folder = new TempFolder();
        string path = folder.Combine("listservers.conf");
        File.WriteAllText(path, "Moreno\tlistserver.moreno.land\t14922\n");
        ListServerProfileStore store = Open(path);
        Assert.Equal(["Alpaxis", "Moreno"], store.Profiles.Select(profile => profile.Name).ToArray());
        Assert.Equal("Moreno\tlistserver.moreno.land\t14922\n", File.ReadAllText(path));
    }

    [Fact]
    public void A_line_identical_to_the_built_in_profile_is_not_shown_twice()
    {
        using var folder = new TempFolder();
        string path = folder.Combine("listservers.conf");
        File.WriteAllText(path, "Moreno\tlistserver.moreno.land\t14922\nalpaxis\tListserver.Alpaxis.net\t14922\n");
        Assert.Equal(2, Open(path).Profiles.Count);
    }

    [Fact]
    public void Legacy_and_bad_lines_are_read_like_the_cpp()
    {
        List<ListServerProfile> profiles = ListServerProfileStore.Parse("old.example:14922\r\nbad.example:0\nnoport\nName\thost.example\t70000\nPort\thost.example\t14923junk\n:14922\nOne\ttab:1\n");
        Assert.Equal(
            [new ListServerProfile("old.example", "old.example", 14922), new ListServerProfile("Port", "host.example", 14923), new ListServerProfile("One\ttab", "One\ttab", 1)],
            profiles);
    }

    [Fact]
    public void The_built_in_profile_cannot_be_edited_removed_or_shadowed()
    {
        using var folder = new TempFolder();
        ListServerProfileStore store = Open(folder.Combine("listservers.conf"));
        Assert.Equal(ProfileChangeStatus.BuiltIn, store.Remove("Alpaxis"));
        Assert.Equal(ProfileChangeStatus.BuiltIn, store.Update("alpaxis", new ListServerProfile("Alpaxis", "evil.example", 1)));
        Assert.Equal(ProfileChangeStatus.DuplicateName, store.Add(new ListServerProfile("ALPAXIS", "x.example", 1)));
        Assert.Equal([ListServerProfile.Default], store.Profiles);
        Assert.True(store.Profiles[0].IsBuiltIn);
    }

    [Fact]
    public void Users_add_edit_move_and_remove_their_own_profiles()
    {
        using var folder = new TempFolder();
        string path = folder.Combine("listservers.conf");
        ListServerProfileStore store = Open(path);
        int changes = 0;
        store.Changed += (_, _) => changes++;
        store.Add(new ListServerProfile("A", "a.example", 1));
        store.Add(new ListServerProfile("B", "b.example", 2));
        Assert.Equal(ProfileChangeStatus.DuplicateName, store.Add(new ListServerProfile("a", "other", 3)));
        Assert.Equal(ProfileChangeStatus.Invalid, store.Add(new ListServerProfile("Tab\tName", "x", 3)));
        Assert.Equal(ProfileChangeStatus.Invalid, store.Add(new ListServerProfile("Line", "x\ny", 3)));
        Assert.Equal(ProfileChangeStatus.Invalid, store.Add(new ListServerProfile("Port", "x", 0)));
        Assert.Equal(ProfileChangeStatus.Invalid, store.Add(new ListServerProfile("", "x", 1)));
        Assert.Equal(ProfileChangeStatus.Saved, store.Update("a", new ListServerProfile("A2", "a2.example", 11)));
        Assert.Equal(ProfileChangeStatus.DuplicateName, store.Update("A2", new ListServerProfile("B", "a2.example", 11)));
        Assert.Equal(ProfileChangeStatus.NotFound, store.Update("Nope", new ListServerProfile("C", "c", 1)));
        Assert.Equal(ProfileChangeStatus.Saved, store.Move("B", 0));
        Assert.Equal(["Alpaxis", "B", "A2"], store.Profiles.Select(profile => profile.Name).ToArray());
        Assert.Equal(ProfileChangeStatus.Saved, store.Remove("b"));
        Assert.Equal(5, changes);
        Assert.Equal("A2\ta2.example\t11\n", File.ReadAllText(path));
    }

    [Fact]
    public void A_damaged_file_is_moved_aside()
    {
        using var folder = new TempFolder();
        string path = folder.Combine("listservers.conf");
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 0 });
        var store = new ListServerProfileStore(path, () => new DateTime(2026, 1, 1, 0, 0, 0));
        ProfileLoadResult result = store.Load();
        Assert.Equal(ProfileLoadStatus.UnreadableMovedAside, result.Status);
        Assert.True(File.Exists(path + ".unreadable-20260101-000000"));
        Assert.Equal([ListServerProfile.Default], store.Profiles);
    }
}

public sealed class ListServerAssociationTests
{
    private static readonly ListServerProfile Retail = new("Retail", "listserver.graalonline.com", 14922);
    private static readonly ListServerProfile Moreno = new("Moreno", "listserver.moreno.land", 14923);
    private static readonly IReadOnlyList<ListServerProfile> Profiles = [ListServerProfile.Default, Retail, Moreno];

    [Fact]
    public void Association_text_uses_an_em_dash() =>
        Assert.Equal("Retail — listserver.graalonline.com:14922", Retail.Association);

    [Theory]
    [InlineData("Retail — listserver.graalonline.com:14922", "Retail")]
    [InlineData("Retail", "Retail")]
    [InlineData("retail", "Retail")]
    [InlineData("listserver.moreno.land:14923", "Moreno")]
    [InlineData("Old Name — listserver.moreno.land:14923", "Moreno")]
    [InlineData("Moreno — changed.host:1", "Moreno")]
    [InlineData("Alpaxis", "Alpaxis")]
    public void Matches_a_profile(string association, string expected)
    {
        Assert.True(ListServerAssociation.TryResolve(association, Profiles, out ListServerEndpoint? endpoint));
        Assert.Equal(expected, endpoint.Name);
        Assert.NotNull(endpoint.Profile);
        Assert.False(endpoint.IsFallback);
    }

    [Fact]
    public void An_unknown_endpoint_is_used_as_it_is()
    {
        Assert.True(ListServerAssociation.TryResolve("Private — private.example:15000", Profiles, out ListServerEndpoint? labelled));
        Assert.Equal(new ListServerEndpoint("Private", "private.example", 15000, null, false), labelled);
        Assert.True(ListServerAssociation.TryResolve("bare.example:16000", Profiles, out ListServerEndpoint? bare));
        Assert.Equal("bare.example", bare.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Nothing known")]
    [InlineData("host.example:0")]
    [InlineData("host.example:notaport")]
    [InlineData(":14922")]
    [InlineData("listserver.moreno.land:149230")]
    public void Nothing_matched_falls_back_to_the_built_in_profile(string association)
    {
        ListServerEndpoint endpoint = ListServerAssociation.Resolve(association, Profiles);
        Assert.True(endpoint.IsFallback);
        Assert.Equal(ListServerProfile.Default, endpoint.Profile);
        Assert.Equal("listserver.alpaxis.net:14922", endpoint.Endpoint);
    }

    [Fact]
    public void Normalize_rewrites_known_profiles_only()
    {
        Assert.Equal(Moreno.Association, ListServerAssociation.Normalize("listserver.moreno.land:14923", Profiles));
        Assert.Equal(Moreno.Association, ListServerAssociation.Normalize("moreno", Profiles));
        Assert.Equal(Retail.Association, ListServerAssociation.Normalize("Official — listserver.graalonline.com:14922", Profiles));
        Assert.Equal("unknown:1", ListServerAssociation.Normalize("unknown:1", Profiles));
        Assert.Equal("Moreno", ListServerAssociation.BadgeLabel("listserver.moreno.land:14923", Profiles));
        Assert.Equal("Unassigned", ListServerAssociation.BadgeLabel("", Profiles));
    }

    [Fact]
    public void Direct_associations_name_a_game_server()
    {
        Assert.Equal("direct://game.example:14900", ListServerAssociation.ToDirect("game.example:14900"));
        Assert.True(ListServerAssociation.TryParseDirect("direct://game.example:14900", Profiles, out string host, out int port));
        Assert.Equal(("game.example", 14900), (host, port));
        Assert.True(ListServerAssociation.TryParseDirect("direct://moreno", Profiles, out host, out port));
        Assert.Equal(("listserver.moreno.land", 14923), (host, port));
        Assert.False(ListServerAssociation.TryParseDirect("game.example:14900", Profiles, out _, out _));
        Assert.False(ListServerAssociation.TryParseDirect("direct://nohost", Profiles, out _, out _));
    }

    [Fact]
    public void The_store_resolves_with_its_profiles()
    {
        using var folder = new TempFolder();
        string path = folder.Combine("listservers.conf");
        File.WriteAllText(path, "Moreno\tlistserver.moreno.land\t14923\n");
        var store = new ListServerProfileStore(path);
        store.Load();
        Assert.Equal("Moreno", store.Resolve("Moreno — listserver.moreno.land:14923").Name);
        Assert.True(store.Resolve("Gone").IsFallback);
        Assert.Equal("Moreno", store.Find("MORENO")!.Name);
    }
}
