using System.Globalization;
using System.Text;
using System.Text.Json;
using RemoteControl.Protocol.Session;
using RemoteControl.Scripting;

namespace RemoteControl.Extensions;

internal static partial class McpServerTools
{
    private const int FetchConcurrency = 8;

    // ---- Kinds and names -------------------------------------------------------------------------------------------------------

    internal static ScriptKind ParseKind(string kind) => (kind ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "weapon" or "weapons" or "gui" => ScriptKind.Weapon,
        "class" or "classes" => ScriptKind.Class,
        "npc" or "npcs" or "dbnpc" => ScriptKind.Npc,
        _ => throw new McpToolException("kind must be weapon, class or npc."),
    };

    internal static string KindName(ScriptKind kind) => kind switch { ScriptKind.Weapon => "weapon", ScriptKind.Class => "class", _ => "npc" };

    /// <summary>The type the saved approvals use: <c>Weapon</c>, <c>Class</c> or <c>NPC</c>.</summary>
    private static string ApprovalType(ScriptKind kind) => kind switch { ScriptKind.Weapon => "Weapon", ScriptKind.Class => "Class", _ => "NPC" };

    private static string Describe(ScriptKind kind, string name, int id) => kind == ScriptKind.Npc ? "NPC " + id + (name.Length > 0 ? " (" + name + ")" : string.Empty) : KindName(kind) + " " + name;

    /// <summary>The name and NPC id of a script that exists on the server (names as the server has them).</summary>
    internal static (string Name, int Id) ResolveScript(McpSessionTarget target, ScriptKind kind, string name, int id)
    {
        IRcSession session = target.Session;
        switch (kind)
        {
            case ScriptKind.Weapon:
            {
                if (name.Length == 0) throw new McpToolException("Give the weapon's name.");
                RcWeapon? weapon = session.Weapons.FirstOrDefault(item => item.Name == name) ?? session.Weapons.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                return weapon is null ? throw new McpToolException("No weapon named \"" + name + "\" on " + target.ServerName + " (rc_list_scripts lists them).") : (weapon.Name, 0);
            }
            case ScriptKind.Class:
            {
                if (name.Length == 0) throw new McpToolException("Give the class's name.");
                RcClass? found = session.Classes.FirstOrDefault(item => item.Name == name) ?? session.Classes.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                return found is null ? throw new McpToolException("No class named \"" + name + "\" on " + target.ServerName + " (rc_list_scripts lists them).") : (found.Name, 0);
            }
            default:
            {
                RcNpc? npc = id > 0 ? session.Npcs.FirstOrDefault(item => item.Id == id)
                    : name.Length > 0 ? session.Npcs.FirstOrDefault(item => item.Name == name) ?? session.Npcs.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) : null;
                if (npc is null) throw new McpToolException((id > 0 ? "No NPC has the id " + id : name.Length > 0 ? "No NPC is named \"" + name + "\"" : "Give the NPC's id or name") + " on " + target.ServerName + ".");
                return (npc.Name, npc.Id);
            }
        }
    }

    private static bool ScriptExists(IRcSession session, ScriptKind kind, string name, int id) => kind switch
    {
        ScriptKind.Weapon => session.Weapons.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)),
        ScriptKind.Class => session.Classes.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)),
        _ => session.Npcs.Any(item => item.Id == id),
    };

    /// <summary>
    /// Whether a script exists on the server. The session's weapon list is the one of the NC login (or the last refresh): it learns
    /// neither of a weapon added since (by this tool or by other staff) nor of one deleted, so a weapon it does not name is looked up in
    /// a fresh list before it counts as new.
    /// </summary>
    /// <exception cref="McpToolException">The weapon list did not come.</exception>
    internal static async Task<bool> ExistsAsync(McpToolContext c, McpSessionTarget target, ScriptKind kind, string name, int id)
    {
        if (ScriptExists(target.Session, kind, name, id)) return true;
        if (kind != ScriptKind.Weapon) return false;
        await RefreshWeaponsAsync(c, target).ConfigureAwait(true);
        return ScriptExists(target.Session, kind, name, id);
    }

    /// <summary><see cref="ResolveScript"/>, after a fresh weapon list when a weapon is not in the session's.</summary>
    internal static async Task<(string Name, int Id)> ResolveScriptAsync(McpToolContext c, McpSessionTarget target, ScriptKind kind, string name, int id)
    {
        if (kind == ScriptKind.Weapon && name.Length > 0 && !ScriptExists(target.Session, kind, name, id)) await RefreshWeaponsAsync(c, target).ConfigureAwait(true);
        return ResolveScript(target, kind, name, id);
    }

    /// <summary>Asks the NC for its weapon list again and waits for it.</summary>
    internal static async Task RefreshWeaponsAsync(McpToolContext c, McpSessionTarget target)
    {
        IRcSession session = target.Session;
        await c.RequestAsync<ValueEventArgs<int>>(session, h => session.WeaponListReceived += h, h => session.WeaponListReceived -= h, _ => true,
            session.RequestWeaponListAsync, "the weapon list of " + target.ServerName).ConfigureAwait(true);
    }

    /// <summary>Asks the server for a script and waits for it.</summary>
    internal static Task<ScriptReceivedEventArgs> FetchScriptAsync(McpToolContext c, McpSessionTarget target, ScriptKind kind, string name, int id)
    {
        IRcSession session = target.Session;
        return c.RequestAsync<ScriptReceivedEventArgs>(session, h => session.ScriptReceived += h, h => session.ScriptReceived -= h,
            e => e.Kind == kind && (kind == ScriptKind.Npc ? e.Id == id : string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)),
            () => kind switch
            {
                ScriptKind.Weapon => session.RequestWeaponScriptAsync(name),
                ScriptKind.Class => session.RequestClassScriptAsync(name),
                _ => session.RequestNpcScriptAsync(id),
            }, "the script of " + Describe(kind, name, id));
    }

    private static string Normalize(string script) => script.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    // ---- Listing and reading ---------------------------------------------------------------------------------------------------

    public static async Task<string> ListScriptsAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireNc();
        string kind = c.Args.String("kind").ToLowerInvariant();
        bool all = kind is "" or "all";
        bool weapons = all || ParseKind(kind) == ScriptKind.Weapon;
        bool classes = all || !weapons && ParseKind(kind) == ScriptKind.Class;
        bool npcs = all || !weapons && !classes;
        if (weapons && c.Args.Bool("refresh", false))
        {
            await c.RequestAsync<ValueEventArgs<int>>(session, h => session.WeaponListReceived += h, h => session.WeaponListReceived -= h, _ => true,
                session.RequestWeaponListAsync, "the weapon list").ConfigureAwait(true);
        }
        Func<string, bool> filter = NameFilter(c.Args.String("query"));
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            if (weapons) McpJson.Strings(writer, "weapons", session.Weapons.Select(item => item.Name).Where(filter).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            if (classes) McpJson.Strings(writer, "classes", session.Classes.Select(item => item.Name).Where(filter).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            if (npcs)
            {
                writer.WriteStartArray("npcs");
                foreach (RcNpc npc in session.Npcs.Where(item => filter(item.Name) || filter(item.Level) || filter(item.Type)).OrderBy(item => item.Id))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", npc.Id);
                    McpJson.Text(writer, "name", npc.Name);
                    McpJson.Text(writer, "type", npc.Type);
                    McpJson.Text(writer, "level", npc.Level);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        });
    }

    public static async Task<string> ReadScriptAsync(McpToolContext c)
    {
        c.RequireNc();
        ScriptKind kind = ParseKind(c.Args.RequiredString("kind"));
        (string name, int id) = await ResolveScriptAsync(c, c.Target, kind, c.Args.String("name"), c.Args.Int("id", 0)).ConfigureAwait(true);
        ScriptReceivedEventArgs script = await FetchScriptAsync(c, c.Target, kind, name, id).ConfigureAwait(true);
        string text = script.Script;
        bool range = c.Args.Has("start") || c.Args.Has("end");
        bool search = c.Args.Has("query");
        string? found = null;
        if (search)
        {
            Func<string, bool> matches = Matcher(c.Args);
            int contextLines = c.Args.Int("contextLines", 0);
            found = await Task.Run(() =>
            {
                int budget = McpProtocol.MaximumLines;
                return SearchText(text, matches, contextLines, ref budget);
            }, c.Token).ConfigureAwait(true);
        }
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", KindName(kind));
            McpJson.Text(writer, "name", script.Name.Length > 0 ? script.Name : name);
            if (kind == ScriptKind.Npc) writer.WriteNumber("id", id);
            if (kind == ScriptKind.Weapon) McpJson.Text(writer, "image", script.Image);
            writer.WriteNumber("lines", text.Length == 0 ? 0 : text.Count(character => character == '\n') + 1);
            writer.WriteNumber("length", text.Length);
            if (found is not null) McpJson.Text(writer, "matches", found);
            else if (range) McpJson.Text(writer, "numbered", McpProtocol.EditorLineRange(text, c.Args.Int("start", 1), c.Args.Int("end", c.Args.Int("start", 1) + McpProtocol.MaximumLines - 1)));
            else McpJson.Text(writer, "script", text);
            writer.WriteEndObject();
        });
    }

    public static async Task<string> SearchScriptsAsync(McpToolContext c)
    {
        if (c.Args.Bool("serverSide", false)) return await ScanScriptsOnServerAsync(c).ConfigureAwait(true);
        IRcSession session = c.Session;
        c.RequireNc();
        Func<string, bool> matches = Matcher(c.Args);
        IReadOnlyList<string> kindNames = c.Args.Strings("kinds").Where(kind => !string.Equals(kind, "all", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (kindNames.Any(kind => kind.StartsWith("level", StringComparison.OrdinalIgnoreCase))) throw new McpToolException("Level scripts are searched by the server: give serverSide: true (/scriptscan).");
        HashSet<ScriptKind> kinds = kindNames.Count == 0 ? new HashSet<ScriptKind> { ScriptKind.Weapon, ScriptKind.Class, ScriptKind.Npc } : kindNames.Select(ParseKind).ToHashSet();
        Func<string, bool> nameFilter = NameFilter(c.Args.String("names"));
        var targets = new List<(ScriptKind Kind, string Name, int Id)>();
        if (kinds.Contains(ScriptKind.Weapon)) targets.AddRange(session.Weapons.Where(item => nameFilter(item.Name)).Select(item => (ScriptKind.Weapon, item.Name, 0)));
        if (kinds.Contains(ScriptKind.Class)) targets.AddRange(session.Classes.Where(item => nameFilter(item.Name)).Select(item => (ScriptKind.Class, item.Name, 0)));
        if (kinds.Contains(ScriptKind.Npc)) targets.AddRange(session.Npcs.Where(item => nameFilter(item.Name)).Select(item => (ScriptKind.Npc, item.Name, item.Id)));
        bool cached = !c.Args.Bool("refresh", true);
        Dictionary<(ScriptKind, string, int), Task<string>> scripts = await FetchManyAsync(c, c.Target, targets, cached).ConfigureAwait(true);
        int maximum = Math.Clamp(c.Args.Int("maxMatches", 500), 1, 5000);
        int context = c.Args.Int("contextLines", 0);
        // The texts were fetched here; the search itself runs off the UI thread (AGENTS.md rule 1).
        var texts = targets.Where(item => scripts[item].IsCompletedSuccessfully).Select(item => (item.Kind, item.Name, item.Id, Text: scripts[item].Result)).ToArray();
        CancellationToken token = c.Token;
        (List<(ScriptKind Kind, string Name, int Id, string Found)> hits, int left) = await Task.Run(() =>
        {
            var found = new List<(ScriptKind, string, int, string)>();
            int budget = maximum;
            foreach ((ScriptKind kind, string name, int id, string text) in texts)
            {
                if (budget <= 0 || token.IsCancellationRequested) break;
                string lines = SearchText(text, matches, context, ref budget);
                if (lines.Length > 0) found.Add((kind, name, id, lines));
            }
            return (found, budget);
        }, token).ConfigureAwait(true);
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("searched", targets.Count);
            writer.WriteStartArray("results");
            foreach ((ScriptKind kind, string name, int id, string found) in hits)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", KindName(kind));
                McpJson.Text(writer, "name", name);
                if (kind == ScriptKind.Npc) writer.WriteNumber("id", id);
                McpJson.Text(writer, "matches", found);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("matchedScripts", hits.Count);
            writer.WriteBoolean("truncated", left <= 0);
            WriteFailures(writer, targets.Where(item => !scripts[item].IsCompletedSuccessfully).Select(item => (Describe(item.Kind, item.Name, item.Id), scripts[item].Exception?.InnerException?.Message ?? "cancelled")));
            writer.WriteEndObject();
        });
    }

    /// <summary>Fetches many scripts, a few at a time; each task holds its text or its failure.</summary>
    internal static async Task<Dictionary<(ScriptKind, string, int), Task<string>>> FetchManyAsync(McpToolContext c, McpSessionTarget target, IReadOnlyList<(ScriptKind Kind, string Name, int Id)> targets, bool cached)
    {
        var results = new Dictionary<(ScriptKind, string, int), Task<string>>();
        using var gate = new SemaphoreSlim(FetchConcurrency);
        foreach (var item in targets.Distinct())
        {
            string? known = cached ? CachedScript(target.Session, item.Kind, item.Name, item.Id) : null;
            results[item] = known is not null ? Task.FromResult(known) : FetchOneAsync(item);
        }
        try
        {
            await Task.WhenAll(results.Values).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is McpToolException or OperationCanceledException)
        {
            // Each task keeps its own failure.
        }
        return results;

        async Task<string> FetchOneAsync((ScriptKind Kind, string Name, int Id) item)
        {
            await gate.WaitAsync(c.Token).ConfigureAwait(true);
            try
            {
                return (await FetchScriptAsync(c, target, item.Kind, item.Name, item.Id).ConfigureAwait(true)).Script;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static string? CachedScript(IRcSession session, ScriptKind kind, string name, int id)
    {
        string? script = kind switch
        {
            ScriptKind.Weapon => session.Weapons.FirstOrDefault(item => item.Name == name)?.Script,
            ScriptKind.Class => session.Classes.FirstOrDefault(item => item.Name == name)?.Script,
            _ => session.Npcs.FirstOrDefault(item => item.Id == id)?.Script,
        };
        return string.IsNullOrEmpty(script) ? null : script;
    }

    private static void WriteFailures(Utf8JsonWriter writer, IEnumerable<(string What, string Why)> failures)
    {
        var list = failures.ToList();
        if (list.Count == 0) return;
        writer.WriteStartArray("failures");
        foreach ((string what, string why) in list)
        {
            writer.WriteStartObject();
            McpJson.Text(writer, "item", what);
            McpJson.Text(writer, "error", why);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    // ---- Writing ---------------------------------------------------------------------------------------------------------------

    public static async Task<string> WriteScriptAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireNc();
        ScriptKind kind = ParseKind(c.Args.RequiredString("kind"));
        string name = c.Args.String("name");
        int id = c.Args.Int("id", 0);
        bool exists = kind == ScriptKind.Npc || await ExistsAsync(c, c.Target, kind, name, id).ConfigureAwait(true);
        if (exists) (name, id) = ResolveScript(c.Target, kind, name, id);
        else if (name.Length == 0) throw new McpToolException("Give the " + KindName(kind) + "'s name.");
        else if (!c.Args.Bool("create", true)) throw new McpToolException("There is no " + KindName(kind) + " named \"" + name + "\" and create is false.");

        IReadOnlyList<McpArguments> edits = EditsOf(c.Args);
        bool hasScript = c.Args.TryString("script", out string script);
        if (hasScript == (edits.Count > 0)) throw new McpToolException("Give either script (the whole text) or edits (expected/replacement pairs).");
        // Edits need the server's text; a whole script needs it only for the backup, which force may go without.
        ScriptReceivedEventArgs? current = !exists ? null : edits.Count > 0 ? await FetchScriptAsync(c, c.Target, kind, name, id).ConfigureAwait(true)
            : await c.ReadForBackupAsync(() => FetchScriptAsync(c, c.Target, kind, name, id), "script of " + Describe(kind, name, id)).ConfigureAwait(true);
        if (edits.Count > 0)
        {
            if (current is null) throw new McpToolException("There is no " + KindName(kind) + " named \"" + name + "\" to edit.");
            script = ApplyEdits(Normalize(current.Script), edits);
        }
        script = Normalize(script);
        string image = c.Args.TryString("image", out string given) ? given : current?.Image ?? string.Empty;
        if (kind == ScriptKind.Class && script.Trim().Length == 0) throw new McpToolException("The server deletes a class whose script is empty; use rc_delete_script to delete it.");
        if (current is not null && Normalize(current.Script) == script && (kind != ScriptKind.Weapon || current.Image == image)) return Status("The script of " + Describe(kind, name, id) + " is already this text; nothing was sent.");

        string change = current is null ? "new, " + LineCount(script) + " lines" : DiffSummary(Normalize(current.Script), script);
        await c.ApproveScriptAsync(McpScriptAction.Write, ApprovalType(kind), (kind == ScriptKind.Npc ? id + " " + name : name) + " on " + c.Server + " (" + change + ")").ConfigureAwait(true);
        if (c.Options.McpApprove && (current is not null || !exists))
        {
            // The question may have been open for a while: what is written must replace the version it was about (and that is backed up).
            bool now = current is not null || await ExistsAsync(c, c.Target, kind, name, id).ConfigureAwait(true);
            ScriptReceivedEventArgs? again = now ? await FetchScriptAsync(c, c.Target, kind, name, id).ConfigureAwait(true) : null;
            if ((again is null) != (current is null) || again is not null && (Normalize(again.Script) != Normalize(current!.Script) || again.Image != current.Image))
                throw new McpToolException(Describe(kind, name, id) + " changed on " + c.Server + " while the question was open; nothing was sent. Read it again and repeat the change.");
        }
        return await PutScriptAsync(c, c.Target, kind, name, id, script, image, current, "rc_write_script").ConfigureAwait(true);
    }

    /// <summary>Writes a script (adds it when it does not exist), after backing up the old one; returns the tool result.</summary>
    internal static async Task<string> PutScriptAsync(McpToolContext c, McpSessionTarget target, ScriptKind kind, string name, int id, string script, string image, ScriptReceivedEventArgs? current, string reason)
    {
        long since = target.Journal.Sequence;
        (bool created, McpBackupEntry? backup) = await SendScriptAsync(c, target, kind, name, id, script, image, current, reason).ConfigureAwait(true);
        bool exists = !created;
        IReadOnlyList<McpJournalEntry> events = await c.CollectAsync(target, since, 1500, AnswerKinds).ConfigureAwait(true);
        string? verified = null;
        if (c.Args.Bool("verify", false))
        {
            ScriptReceivedEventArgs check = await FetchScriptAsync(c, target, kind, name, id).ConfigureAwait(true);
            verified = Normalize(check.Script) == script ? "the server has the new text" : "the server's text differs from what was sent";
        }
        IReadOnlyList<string> open = await OpenEditorsAsync(c, kind, name, id).ConfigureAwait(true);
        return Written(c, events, backup, (exists ? "The script of " : "The new ") + Describe(kind, name, id) + (exists ? " was written to " : " was added to ") + target.ServerName + ".", writer =>
        {
            writer.WriteBoolean("created", !exists);
            writer.WriteNumber("lines", LineCount(script));
            if (verified is not null) writer.WriteString("verified", verified);
            if (open.Count > 0) McpJson.Strings(writer, "openEditorsWithChanges", open);
            writer.WriteNumber("next", target.Journal.Sequence);
        });
    }

    /// <summary>Backs up the script a write replaces (<paramref name="current"/>), then sends the new one; returns whether it was added and the backup.</summary>
    internal static async Task<(bool Created, McpBackupEntry? Backup)> SendScriptAsync(McpToolContext c, McpSessionTarget target, ScriptKind kind, string name, int id, string script, string image, ScriptReceivedEventArgs? current, string reason)
    {
        IRcSession session = target.Session;
        McpBackupEntry? backup = current is null ? null
            : await c.BackupAsync(target.ServerName, KindName(kind), name, id, current.Image, Encoding.UTF8.GetBytes(current.Script), reason).ConfigureAwait(true);
        bool exists = current is not null || ScriptExists(session, kind, name, id);
        bool sent = kind switch
        {
            ScriptKind.Weapon => exists ? await session.UpdateWeaponAsync(name, image, script).ConfigureAwait(true) : await session.AddWeaponAsync(name, image, script).ConfigureAwait(true),
            ScriptKind.Class => exists ? await session.UpdateClassAsync(name, script).ConfigureAwait(true) : await session.AddClassAsync(name, script).ConfigureAwait(true),
            _ => await session.UpdateNpcScriptAsync(id, script).ConfigureAwait(true),
        };
        McpToolContext.Sent(sent, session, "The script of " + Describe(kind, name, id) + " on " + target.ServerName);
        return (!exists, backup);
    }

    public static async Task<string> DeleteScriptAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireNc();
        ScriptKind kind = ParseKind(c.Args.RequiredString("kind"));
        (string name, int id) = await ResolveScriptAsync(c, c.Target, kind, c.Args.String("name"), c.Args.Int("id", 0)).ConfigureAwait(true);
        await c.ApproveScriptAsync(McpScriptAction.Delete, ApprovalType(kind), (kind == ScriptKind.Npc ? id + " " + name : name) + " on " + c.Server).ConfigureAwait(true);
        ScriptReceivedEventArgs? current = null;
        try
        {
            current = await FetchScriptAsync(c, c.Target, kind, name, id).ConfigureAwait(true);
        }
        catch (McpToolException)
        {
            // A script that cannot be read is deleted without a backup only when the caller insists.
            if (!c.Args.Bool("force", false)) throw new McpToolException("The script could not be read for its backup, so it was not deleted; give force: true to delete it anyway.");
        }
        McpBackupEntry? backup = current is null ? null
            : await c.BackupAsync(c.Server, KindName(kind), name, id, current.Image, Encoding.UTF8.GetBytes(current.Script), "rc_delete_script").ConfigureAwait(true);
        if (kind == ScriptKind.Npc)
        {
            // A database NPC goes whole: its flags (restorable onto a new NPC of that id) and its attributes (level, position, type: what
            // rc_npc_action create needs to make it again) are kept too.
            string? flags = (await c.ReadForBackupAsync(() => c.RequestAsync<NpcTextEventArgs>(session, h => session.NpcFlagsReceived += h, h => session.NpcFlagsReceived -= h,
                e => e.NpcId == id, () => session.RequestNpcFlagsAsync(id), "the flags of NPC " + id), "flags of NPC " + id).ConfigureAwait(true))?.Text;
            if (flags is not null) await c.BackupAsync(c.Server, "npcflags", name, id, string.Empty, Encoding.UTF8.GetBytes(flags), "rc_delete_script").ConfigureAwait(true);
            string? attributes = (await c.ReadForBackupAsync(() => c.RequestAsync<NpcTextEventArgs>(session, h => session.NpcAttributesReceived += h, h => session.NpcAttributesReceived -= h,
                e => e.NpcId == id, () => session.RequestNpcAttributesAsync(id), "the attributes of NPC " + id), "attributes of NPC " + id).ConfigureAwait(true))?.Text;
            if (attributes is not null) await c.BackupAsync(c.Server, "npcattributes", name, id, string.Empty, Encoding.UTF8.GetBytes(attributes), "rc_delete_script").ConfigureAwait(true);
        }
        long since = c.Target.Journal.Sequence;
        bool sent = kind switch
        {
            ScriptKind.Weapon => await session.DeleteWeaponAsync(name).ConfigureAwait(true),
            ScriptKind.Class => await session.DeleteClassAsync(name).ConfigureAwait(true),
            _ => await session.DeleteNpcAsync(id).ConfigureAwait(true),
        };
        McpToolContext.Sent(sent, session, "The deletion of " + Describe(kind, name, id));
        if (kind == ScriptKind.Weapon)
        {
            // Nothing the server sends takes a deleted weapon out of the session's list: ask for the list again.
            try { await RefreshWeaponsAsync(c, c.Target).ConfigureAwait(true); }
            catch (McpToolException) { }
        }
        return Written(c, await c.CollectAsync(c.Target, since, 1000, AnswerKinds).ConfigureAwait(true), backup, Describe(kind, name, id) + " was deleted on " + c.Server + ".");
    }

    // ---- NPCs and levels -------------------------------------------------------------------------------------------------------

    public static async Task<string> ReadNpcAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireNc();
        (string name, int id) = ResolveScript(c.Target, ScriptKind.Npc, c.Args.String("name"), c.Args.Int("id", 0));
        RcNpc npc = session.Npcs.First(item => item.Id == id);
        IReadOnlyList<string> parts = c.Args.Strings("parts").Select(part => part.ToLowerInvariant()).ToArray();
        if (parts.Count == 0) parts = new[] { "attributes", "flags" };
        Task<NpcTextEventArgs>? attributes = parts.Contains("attributes")
            ? c.RequestAsync<NpcTextEventArgs>(session, h => session.NpcAttributesReceived += h, h => session.NpcAttributesReceived -= h, e => e.NpcId == id, () => session.RequestNpcAttributesAsync(id), "the attributes of NPC " + id) : null;
        Task<NpcTextEventArgs>? flags = parts.Contains("flags")
            ? c.RequestAsync<NpcTextEventArgs>(session, h => session.NpcFlagsReceived += h, h => session.NpcFlagsReceived -= h, e => e.NpcId == id, () => session.RequestNpcFlagsAsync(id), "the flags of NPC " + id) : null;
        Task<ScriptReceivedEventArgs>? script = parts.Contains("script") ? FetchScriptAsync(c, c.Target, ScriptKind.Npc, name, id) : null;
        try
        {
            await Task.WhenAll(new Task?[] { attributes, flags, script }.OfType<Task>()).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is McpToolException or OperationCanceledException)
        {
            // Reported per part.
        }
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            McpJson.Text(writer, "name", npc.Name);
            McpJson.Text(writer, "type", npc.Type);
            McpJson.Text(writer, "level", npc.Level);
            var failures = new List<(string, string)>();
            if (attributes is not null) WritePart(writer, "attributes", attributes.IsCompletedSuccessfully ? attributes.Result.Text : null, attributes, failures);
            if (flags is not null) WritePart(writer, "flags", flags.IsCompletedSuccessfully ? flags.Result.Text : null, flags, failures);
            if (script is not null) WritePart(writer, "script", script.IsCompletedSuccessfully ? script.Result.Script : null, script, failures);
            WriteFailures(writer, failures);
            writer.WriteEndObject();
        });

        static void WritePart(Utf8JsonWriter writer, string part, string? text, Task task, List<(string, string)> failures)
        {
            if (text is not null) McpJson.Text(writer, part, text);
            else failures.Add((part, task.Exception?.InnerException?.Message ?? "cancelled"));
        }
    }

    public static async Task<string> NpcActionAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireNc();
        string action = c.Args.RequiredString("action").ToLowerInvariant().Replace('-', '_');
        long since = c.Target.Journal.Sequence;
        if (action == "create")
        {
            string name = c.Args.RequiredString("name");
            int id = c.Args.Int("id", 0);
            if (id <= 0)
            {
                id = 1000;
                var used = new HashSet<int>(session.Npcs.Select(npc => npc.Id));
                while (used.Contains(id)) id++;
            }
            else if (session.Npcs.Any(npc => npc.Id == id)) throw new McpToolException("An NPC has the id " + id + " already.");
            string type = c.Args.String("type");
            if (type.Length == 0) type = "OBJECT";
            string scripter = c.Args.String("scripter");
            if (scripter.Length == 0) scripter = session.Account;
            string level = c.Args.String("level");
            if (level.Length == 0) level = "onlinestartlocal.nw";
            string x = c.Args.TryDouble("x", out double xValue) ? xValue.ToString(CultureInfo.InvariantCulture) : "30.5";
            string y = c.Args.TryDouble("y", out double yValue) ? yValue.ToString(CultureInfo.InvariantCulture) : "30.5";
            bool hasScript = c.Args.TryString("script", out string script) && script.Length > 0;
            // The NPC is a game object (mcpserver); its script is a script write like rc_write_script's, with its permission and question.
            if (hasScript && !McpTools.Allowed(McpToolGroup.Write, c.Options))
                throw new McpToolException("A script needs script writes (mcpwrite): create the NPC without one and write it with rc_write_script.");
            if (hasScript) await c.ApproveScriptAsync(McpScriptAction.Write, "NPC", id + " " + name + " on " + c.Server + " (new, " + LineCount(script) + " lines)").ConfigureAwait(true);
            await c.ApproveAsync("Create the database NPC " + name + " (id " + id + ", " + type + ") in " + level + " at " + x + ", " + y + " on " + c.Server + (hasScript ? " with a script of " + LineCount(script) + " lines" : string.Empty)).ConfigureAwait(true);
            var added = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int created = id;
            EventHandler<NpcEventArgs> onAdded = (_, e) =>
            {
                if (e.Id == created) added.TrySetResult(true);
            };
            session.NpcAdded += onAdded;
            try
            {
                McpToolContext.Sent(await session.CreateNpcAsync(name, id, type, scripter, level, x, y).ConfigureAwait(true), session, "The new NPC");
                if (hasScript)
                {
                    if (!session.Npcs.Any(npc => npc.Id == id)) await c.WaitAsync(added.Task, "the new NPC " + id).ConfigureAwait(true);
                    McpToolContext.Sent(await session.UpdateNpcScriptAsync(id, Normalize(script)).ConfigureAwait(true), session, "The new NPC's script");
                }
            }
            finally
            {
                session.NpcAdded -= onAdded;
            }
            return Written(c, await c.CollectAsync(c.Target, since, 1500, AnswerKinds).ConfigureAwait(true), null, "The NPC " + name + " (id " + id + ") was sent to be created.", writer => writer.WriteNumber("id", id));
        }

        (string npcName, int npcId) = ResolveScript(c.Target, ScriptKind.Npc, c.Args.String("name"), c.Args.Int("id", 0));
        string what = "NPC " + npcId + (npcName.Length > 0 ? " (" + npcName + ")" : string.Empty);
        McpBackupEntry? backup = null;
        switch (action)
        {
            case "reset":
                await c.ApproveAsync("Reset " + what + " on " + c.Server).ConfigureAwait(true);
                McpToolContext.Sent(await session.ResetNpcAsync(npcId).ConfigureAwait(true), session, "The reset");
                break;
            case "warp":
            {
                string level = c.Args.RequiredString("level");
                if (!c.Args.TryDouble("x", out double x) || !c.Args.TryDouble("y", out double y)) throw new McpToolException("warp needs x and y (in tiles).");
                await c.ApproveAsync("Warp " + what + " to " + level + " (" + x.ToString(CultureInfo.InvariantCulture) + ", " + y.ToString(CultureInfo.InvariantCulture) + ") on " + c.Server).ConfigureAwait(true);
                McpToolContext.Sent(await session.WarpNpcAsync(npcId, x, y, level).ConfigureAwait(true), session, "The warp");
                break;
            }
            case "write_flags" or "set_flags" or "flags":
            {
                string current = (await c.RequestAsync<NpcTextEventArgs>(session, h => session.NpcFlagsReceived += h, h => session.NpcFlagsReceived -= h, e => e.NpcId == npcId,
                    () => session.RequestNpcFlagsAsync(npcId), "the flags of " + what).ConfigureAwait(true)).Text;
                string flags = MergeSettings(current, c.Args, ignoreCase: false, out List<string> notes);
                if (flags == current) return Status("The flags of " + what + " already are this; nothing was sent.");
                await c.ApproveAsync("Change the flags of " + what + " on " + c.Server + ": " + (notes.Count > 0 ? string.Join("; ", notes) : "\n" + McpToolContext.Whole(flags))).ConfigureAwait(true);
                if (c.Options.McpApprove && (await c.RequestAsync<NpcTextEventArgs>(session, h => session.NpcFlagsReceived += h, h => session.NpcFlagsReceived -= h, e => e.NpcId == npcId,
                    () => session.RequestNpcFlagsAsync(npcId), "the flags of " + what).ConfigureAwait(true)).Text != current)
                    throw new McpToolException("The flags of " + what + " changed while the question was open; nothing was sent. Read them again and repeat the change.");
                backup = await c.BackupAsync(c.Server, "npcflags", npcName, npcId, string.Empty, Encoding.UTF8.GetBytes(current), "rc_npc_action").ConfigureAwait(true);
                McpToolContext.Sent(await session.SetNpcFlagsAsync(npcId, flags).ConfigureAwait(true), session, "The flags");
                break;
            }
            case "delete":
                throw new McpToolException("Delete an NPC with rc_delete_script (kind npc): it keeps a backup of its script.");
            default:
                throw new McpToolException("action must be create, reset, warp or write_flags.");
        }
        return Written(c, await c.CollectAsync(c.Target, since, 1000, AnswerKinds).ConfigureAwait(true), backup, "Done: " + action + " " + what + ".");
    }

    public static async Task<string> ListLevelsAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireNc();
        if (session.Levels.Count == 0 || c.Args.Bool("refresh", false))
        {
            await c.RequestAsync<TextEventArgs>(session, h => session.LevelListReceived += h, h => session.LevelListReceived -= h, _ => true,
                session.RequestLevelListAsync, "the level list").ConfigureAwait(true);
        }
        Func<string, bool> filter = NameFilter(c.Args.String("query"));
        string[] levels = session.Levels.Select(level => level.Name).Where(filter).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("count", levels.Length);
            McpJson.Strings(writer, "levels", levels);
            writer.WriteEndObject();
        });
    }

    public static async Task<string> ReadLocalNpcsAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireNc();
        string level = c.Args.RequiredString("level");
        using IDisposable claim = c.Claim(c.Target, "localnpcs", level);
        LocalNpcsEventArgs npcs = await c.RequestAsync<LocalNpcsEventArgs>(session, h => session.LocalNpcsReceived += h, h => session.LocalNpcsReceived -= h,
            e => string.Equals(e.Level, level, StringComparison.OrdinalIgnoreCase) || e.Level.Length == 0, () => session.RequestLocalNpcsAsync(level), "the NPCs of " + level).ConfigureAwait(true);
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            McpJson.Text(writer, "level", npcs.Level.Length > 0 ? npcs.Level : level);
            McpJson.Text(writer, "text", npcs.Text);
            writer.WriteEndObject();
        });
    }

    public static async Task<string> UpdateLevelsAsync(McpToolContext c)
    {
        IRcSession session = c.Session;
        c.RequireRc();
        string[] levels = c.Args.Strings("levels").Select(level => level.Trim()).Where(level => level.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (levels.Length == 0) throw new McpToolException("Give the levels to reload.");
        await c.ApproveAsync("Reload " + levels.Length + " level(s) on " + c.Server + ": " + string.Join(", ", levels)).ConfigureAwait(true);
        long since = c.Target.Journal.Sequence;
        McpToolContext.Sent(levels.Length == 1 ? await session.UpdateLevelAsync(levels[0]).ConfigureAwait(true) : await session.UpdateLevelsAsync(levels).ConfigureAwait(true), session, "The level update");
        return Written(c, await c.CollectAsync(c.Target, since, 1000, AnswerKinds).ConfigureAwait(true), null, levels.Length + " level(s) were sent to be reloaded.");
    }

    // ---- Checking ----------------------------------------------------------------------------------------------------------------

    public static async Task<string> CheckScriptAsync(McpToolContext c)
    {
        string text;
        string what;
        if (c.Args.TryString("text", out string given))
        {
            text = Normalize(given);
            what = "the given text";
        }
        else
        {
            c.RequireNc();
            ScriptKind kind = ParseKind(c.Args.RequiredString("kind"));
            (string name, int id) = await ResolveScriptAsync(c, c.Target, kind, c.Args.String("name"), c.Args.Int("id", 0)).ConfigureAwait(true);
            text = Normalize((await FetchScriptAsync(c, c.Target, kind, name, id).ConfigureAwait(true)).Script);
            what = Describe(kind, name, id);
        }
        (DiagnosticsOptions options, string api) = await CheckOptionsAsync(c).ConfigureAwait(true);
        IReadOnlyList<Diagnostic> diagnostics = Gs2Diagnostics.Analyze(text, options);
        int[] starts = LineStarts(text);
        bool format = c.Args.Bool("format", false);
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            McpJson.Text(writer, "checked", what);
            McpJson.Text(writer, "api", api);
            writer.WriteNumber("errors", diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error));
            writer.WriteNumber("warnings", diagnostics.Count(item => item.Severity == DiagnosticSeverity.Warning));
            writer.WriteStartArray("diagnostics");
            foreach (Diagnostic diagnostic in diagnostics.Take(500))
            {
                int line = Array.BinarySearch(starts, diagnostic.Start);
                if (line < 0) line = ~line - 1;
                line = Math.Max(0, line);
                writer.WriteStartObject();
                writer.WriteString("severity", diagnostic.Severity.ToString().ToLowerInvariant());
                writer.WriteNumber("line", line + 1);
                writer.WriteNumber("column", diagnostic.Start - starts[line] + 1);
                writer.WriteNumber("length", diagnostic.Length);
                McpJson.Text(writer, "message", diagnostic.Message);
                int end = line + 1 < starts.Length ? starts[line + 1] - 1 : text.Length;
                McpJson.Text(writer, "source", text[starts[line]..Math.Max(starts[line], end)].TrimEnd('\r'));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (format) McpJson.Text(writer, "formatted", Gs2Formatter.Format(text));
            writer.WriteEndObject();
        });
    }

    // ---- Text helpers ------------------------------------------------------------------------------------------------------------

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int index = 0; index < text.Length; index++) if (text[index] == '\n') starts.Add(index + 1);
        return starts.ToArray();
    }

    internal static int LineCount(string text) => text.Length == 0 ? 0 : text.Count(character => character == '\n') + (text.EndsWith('\n') ? 0 : 1);

    /// <summary>The guarded edits of a call: <c>edits</c> (an array of <c>{expected, replacement}</c>), or one <c>expected</c>/<c>replacement</c> pair.</summary>
    internal static IReadOnlyList<McpArguments> EditsOf(McpArguments args)
    {
        IReadOnlyList<McpArguments> edits = args.Objects("edits");
        if (edits.Count > 0) return edits;
        return args.Has("expected") ? new[] { args } : Array.Empty<McpArguments>();
    }

    /// <summary>Applies guarded replacements in order; each expected text must occur exactly once (unless <c>all</c> is true).</summary>
    internal static string ApplyEdits(string text, IReadOnlyList<McpArguments> edits)
    {
        for (int index = 0; index < edits.Count; index++)
        {
            McpArguments edit = edits[index];
            if (!edit.TryString("expected", out string expected) || !edit.TryString("replacement", out string replacement))
                throw new McpToolException("Edit " + (index + 1) + " needs expected and replacement strings.");
            expected = Normalize(expected);
            replacement = Normalize(replacement);
            if (edit.Bool("all", false))
            {
                if (expected.Length == 0 || !text.Contains(expected, StringComparison.Ordinal)) throw new McpToolException("Edit " + (index + 1) + ": the expected text was not found.");
                text = text.Replace(expected, replacement, StringComparison.Ordinal);
                continue;
            }
            if (!McpProtocol.TryGuardedReplace(text, expected, replacement, out string edited, out string error)) throw new McpToolException("Edit " + (index + 1) + ": " + error + ".");
            text = edited;
        }
        return text;
    }

    /// <summary>A short description of how a text changes: lines before and after, lines changed.</summary>
    internal static string DiffSummary(string before, string after)
    {
        string[] old = before.Split('\n');
        string[] now = after.Split('\n');
        int prefix = 0;
        while (prefix < old.Length && prefix < now.Length && old[prefix] == now[prefix]) prefix++;
        int suffix = 0;
        while (suffix < old.Length - prefix && suffix < now.Length - prefix && old[^(suffix + 1)] == now[^(suffix + 1)]) suffix++;
        int removed = old.Length - prefix - suffix;
        int added = now.Length - prefix - suffix;
        return LineCount(before) + " → " + LineCount(after) + " lines; from line " + (prefix + 1) + ", " + removed + " replaced by " + added;
    }

    private static async Task<IReadOnlyList<string>> OpenEditorsAsync(McpToolContext c, ScriptKind kind, string name, int id)
    {
        IReadOnlyList<McpScriptEditorInfo> editors = await c.Host.ListScriptEditorsAsync().ConfigureAwait(true);
        string[] titles = kind switch
        {
            ScriptKind.Weapon => new[] { "Weapon: " + name, "Weapon/GUI Script: " + name },
            ScriptKind.Class => new[] { "Class: " + name },
            _ => new[] { "Npcscript: " + name, "NPC: " + name, "NPC " + id },
        };
        return editors.Where(editor => editor.Modified && titles.Any(title => editor.Name == title || editor.Name.StartsWith(title + " (", StringComparison.Ordinal))).Select(editor => editor.Name).ToArray();
    }
}
