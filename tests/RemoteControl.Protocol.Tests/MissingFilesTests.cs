using RemoteControl.Protocol.Wire;
using RemoteControl.Protocol.Session;
using RemoteControl.Protocol.Testing;
using static RemoteControl.Protocol.Tests.TestHelpers;

namespace RemoteControl.Protocol.Tests;

/// <summary>The request <c>missingfiles</c> and its answers (the Import Missing specification, sections 1 and 2).</summary>
public sealed class MissingFilesTests
{
    private static string Row(string name, string kind, string folder, string uses, string usedBy) => string.Join('\t', name, kind, folder, uses, usedBy);

    private static async Task<(List<MissingFilesEventArgs> Events, List<string> Log, RcSession Session, FakeConnection Client, FakeGameServer Server)> StartAsync()
    {
        var server = new FakeGameServer();
        var log = new List<string>();
        (RcSession session, FakeConnection client) = await ConnectAsync(server, Options(log: line => { lock (log) log.Add(line); }));
        var events = new List<MissingFilesEventArgs>();
        session.MissingFilesReceived += (_, e) => events.Add(e);
        return (events, log, session, client, server);
    }

    /// <summary>The answers of one connection are handled in order: a server text that no handler knows comes back as an event after them.</summary>
    private static async Task<string> BarrierAsync(RcSession session, FakeConnection client, string marker)
    {
        Task<TextEventArgs> seen = Next<TextEventArgs>(h => session.ServerTextReceived += h, h => session.ServerTextReceived -= h, e => e.Text.Contains(marker, StringComparison.Ordinal));
        await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "barrier", marker);
        return (await seen).Text;
    }

    [Fact]
    public async Task The_request_is_the_text_missingfiles()
    {
        (_, _, RcSession session, FakeConnection client, FakeGameServer server) = await StartAsync();
        await using (server)
        using (session)
        {
            Assert.True(await session.RequestMissingFilesAsync().Within());
            Assert.Equal("GraalEngine,missingfiles", (await client.WaitForPacketAsync(ClientPacketId.RequestText, p => p.Text.Contains("missingfiles", StringComparison.Ordinal))).Text);
        }
    }

    [Fact]
    public async Task The_parts_are_put_together_and_one_event_is_raised_after_the_last()
    {
        (List<MissingFilesEventArgs> events, _, RcSession session, FakeConnection client, FakeGameServer server) = await StartAsync();
        await using (server)
        using (session)
        {
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "1", "3",
                Row("head1.png", "head", "world/heads/", "2", "level a.nw; npc Guard"), Row("door.gani", "gani", "world/ganis/", "1", "class door"));
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "2", "3", Row("x.wav", "sound", "", "7", "weapon -Shop"));
            await BarrierAsync(session, client, "one");
            Assert.Empty(events);

            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "3", "3", Row("map.gmap", "level", "world/", "1", "gani a, b"));
            await BarrierAsync(session, client, "two");

            MissingFilesEventArgs result = Assert.Single(events);
            Assert.Null(result.Error);
            Assert.Equal(
                new[]
                {
                    new MissingFile("head1.png", MissingFileKind.Head, "world/heads/", 2, "level a.nw; npc Guard"),
                    new MissingFile("door.gani", MissingFileKind.Gani, "world/ganis/", 1, "class door"),
                    new MissingFile("x.wav", MissingFileKind.Sound, "", 7, "weapon -Shop"),
                    new MissingFile("map.gmap", MissingFileKind.Level, "world/", 1, "gani a, b"),
                },
                result.Files);
        }
    }

    [Fact]
    public async Task A_result_without_files_is_one_empty_part()
    {
        (List<MissingFilesEventArgs> events, _, RcSession session, FakeConnection client, FakeGameServer server) = await StartAsync();
        await using (server)
        using (session)
        {
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "1", "1");
            await BarrierAsync(session, client, "x");
            MissingFilesEventArgs result = Assert.Single(events);
            Assert.Null(result.Error);
            Assert.Empty(result.Files);
        }
    }

    [Fact]
    public async Task An_error_answer_carries_the_sentence_and_is_not_a_server_text()
    {
        (List<MissingFilesEventArgs> events, _, RcSession session, FakeConnection client, FakeGameServer server) = await StartAsync();
        await using (server)
        using (session)
        {
            var other = new List<string>();
            session.ServerTextReceived += (_, e) => other.Add(e.Text);
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "error", "A search is running already.");
            await BarrierAsync(session, client, "x");
            MissingFilesEventArgs result = Assert.Single(events);
            Assert.Equal("A search is running already.", result.Error);
            Assert.Empty(result.Files);
            Assert.DoesNotContain(other, text => text.Contains("missingfiles", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task An_unknown_kind_is_other_and_values_after_the_fifth_are_ignored()
    {
        (List<MissingFilesEventArgs> events, _, RcSession session, FakeConnection client, FakeGameServer server) = await StartAsync();
        await using (server)
        using (session)
        {
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "1", "1", Row("a.dat", "scroll", "world/", "1", "npc A") + "\textra\tmore");
            await BarrierAsync(session, client, "x");
            Assert.Equal(new MissingFile("a.dat", MissingFileKind.Other, "world/", 1, "npc A"), Assert.Single(Assert.Single(events).Files));
        }
    }

    [Fact]
    public async Task A_part_numbered_beyond_its_count_is_dropped_with_a_log_line()
    {
        (List<MissingFilesEventArgs> events, List<string> log, RcSession session, FakeConnection client, FakeGameServer server) = await StartAsync();
        await using (server)
        using (session)
        {
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "1", "2", Row("a.png", "image", "world/images/", "1", "level a.nw"));
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "1", "3", Row("never.png", "image", "world/images/", "1", "level a.nw"));
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "3", "2", Row("bad.png", "image", "world/images/", "1", "level a.nw"));
            await BarrierAsync(session, client, "x");
            Assert.Empty(events);
            lock (log) Assert.Contains(log, line => line.Contains("missingfiles", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_part_that_skips_one_is_dropped_and_never_completes_a_result()
    {
        (List<MissingFilesEventArgs> events, List<string> log, RcSession session, FakeConnection client, FakeGameServer server) = await StartAsync();
        await using (server)
        using (session)
        {
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "1", "3", Row("a.png", "image", "world/images/", "1", "level a.nw"));
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "3", "3", Row("c.png", "image", "world/images/", "1", "level a.nw"));
            await BarrierAsync(session, client, "x");
            Assert.Empty(events);
            lock (log) Assert.Contains(log, line => line.Contains("part 3 of 3", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_row_with_too_few_values_or_a_bad_number_is_dropped_the_others_stay()
    {
        (List<MissingFilesEventArgs> events, List<string> log, RcSession session, FakeConnection client, FakeGameServer server) = await StartAsync();
        await using (server)
        using (session)
        {
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "1", "1",
                "short.png\timage\tworld/images/", Row("nan.png", "image", "world/images/", "many", "level a.nw"), Row("-1.png", "image", "world/images/", "-1", "x"),
                Row("", "image", "world/", "1", "x"), Row("ok.png", "image", "world/images/", "3", "level a.nw"));
            await BarrierAsync(session, client, "x");
            Assert.Equal("ok.png", Assert.Single(Assert.Single(events).Files).Name);
            lock (log) Assert.Equal(4, log.Count(line => line.Contains("row was dropped", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task Numbers_that_do_not_parse_or_do_not_fit_drop_the_part_with_a_log_line()
    {
        (List<MissingFilesEventArgs> events, List<string> log, RcSession session, FakeConnection client, FakeGameServer server) = await StartAsync();
        await using (server)
        using (session)
        {
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "x", "1");
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "1");
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "0", "1");
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "2", "1");
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "1", "999999999999");
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "weird");
            await BarrierAsync(session, client, "x");
            Assert.Empty(events);
            lock (log) Assert.Equal(6, log.Count(line => line.Contains("missingfiles", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task A_new_part_one_starts_the_result_again_and_a_new_request_forgets_a_half_result()
    {
        (List<MissingFilesEventArgs> events, _, RcSession session, FakeConnection client, FakeGameServer server) = await StartAsync();
        await using (server)
        using (session)
        {
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "1", "2", Row("old.png", "image", "world/images/", "1", "level a.nw"));
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "1", "1", Row("new.png", "image", "world/images/", "1", "level a.nw"));
            await BarrierAsync(session, client, "a");
            Assert.Equal("new.png", Assert.Single(Assert.Single(events).Files).Name);

            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "1", "2", Row("half.png", "image", "world/images/", "1", "level a.nw"));
            Assert.True(await session.RequestMissingFilesAsync().Within());
            await FakeGameServer.SendServerTextAsync(client, "GraalEngine", "missingfiles", "part", "2", "2", Row("late.png", "image", "world/images/", "1", "level a.nw"));
            await BarrierAsync(session, client, "b");
            Assert.Single(events);
        }
    }

    [Fact]
    public void A_rule_covers_a_file_when_the_folder_is_equal_and_the_name_matches()
    {
        var rule = new FileBrowserFolder("rw", "world/heads/head*.png");
        Assert.True(rule.Covers("world/heads/", "head1.png"));
        Assert.True(rule.Covers("WORLD/Heads", "HEAD9.PNG"));
        Assert.False(rule.Covers("world/heads/", "body1.png"));
        Assert.False(rule.Covers("world/bodies/", "head1.png"));
        Assert.False(rule.Covers("world/heads/sub/", "head1.png"));
        Assert.True(new FileBrowserFolder("rw", "world/images/a?c.*").Covers("world/images/", "abc.gif"));
        Assert.False(new FileBrowserFolder("rw", "world/images/a?c.*").Covers("world/images/", "ac.gif"));
        Assert.True(new FileBrowserFolder("r", "world/*").Covers("world/", "x"));
        Assert.False(new FileBrowserFolder("r", "world/*").AllowsWrite);
        Assert.True(new FileBrowserFolder("rw", "world/*").AllowsWrite);
    }

    [Fact]
    public async Task The_fake_session_records_the_call_and_raises_the_result_and_the_server_answers_uploads()
    {
        using var session = new FakeRcSession();
        var events = new List<MissingFilesEventArgs>();
        var messages = new List<string>();
        session.MissingFilesReceived += (_, e) => events.Add(e);
        session.FileBrowserMessageReceived += (_, e) => messages.Add(e.Text);
        Assert.True(await session.RequestMissingFilesAsync());
        Assert.Single(session.CallsTo(nameof(IRcSession.RequestMissingFilesAsync)));
        session.RaiseMissingFiles(new[] { new MissingFile("a.png", MissingFileKind.Image, "world/images/", 1, "level a.nw") });
        session.RaiseMissingFilesError("No right.");
        Assert.Equal("a.png", Assert.Single(events[0].Files).Name);
        Assert.Equal("No right.", events[1].Error);

        FakeFileBrowser files = session.FakeBackupFileBrowser;
        files.ServerAnswersUploads = true;
        files.FolderList.Add(new FileBrowserFolder("rw", "world/images/*.png"));
        await files.UploadAsync("world/images/a.png", new byte[3]);
        await files.UploadAsync("world/images/a.gif", new byte[3]);
        await files.UploadAsync("world/images/big.png", new byte[40000]);
        session.MaxUploadFileSize = 10;
        await files.UploadAsync("world/images/c.png", new byte[11]);
        Assert.Equal(
            new[]
            {
                "Uploaded file a.png",
                "Could not upload world/images/a.gif: not in your folder rights",
                "Uploaded large file big.png",
                "Could not upload world/images/c.png: larger than 20 MiB",
            },
            messages);
        Assert.False(files.RemoteFiles.ContainsKey("world/images/a.gif"));
    }
}
