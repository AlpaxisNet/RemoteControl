using System.Globalization;
using System.Text;
using RemoteControl.Protocol.Session;

namespace RemoteControl.Sync;

/// <summary>The header of a weapon or NPC document, as <see cref="ScriptDocument.TryParse"/> read it.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Name">The weapon name (<c>REALNAME</c>) or NPC name (<c>NAME</c>).</param>
/// <param name="Image">The image (<c>IMAGE</c>), or empty.</param>
/// <param name="NpcId">The NPC id (<c>ID</c>), or 0.</param>
/// <param name="NpcType">The NPC type (<c>TYPE</c>), or empty.</param>
/// <param name="Level">The NPC level (<c>LEVEL</c>), or empty.</param>
/// <param name="Flags">The NPC flags (<c>FLAG</c> lines).</param>
/// <param name="Script">The script between the markers.</param>
public sealed record ParsedScriptDocument(ScriptKind Kind, string Name, string Image, int NpcId, string NpcType, string Level, IReadOnlyList<string> Flags, string Script);

/// <summary>
/// The files a script is kept in (<c>syncEntry</c>, <c>TSyncManager.cpp:649-663</c>): a weapon is
/// <c>GRAWP001\nREALNAME &lt;name&gt;\nIMAGE &lt;image&gt;\nSCRIPT\n&lt;script&gt;\nSCRIPTEND\n</c>; an NPC is
/// <c>GRNPC001\nNAME …\nID …\nTYPE …\n[IMAGE …\n][LEVEL …\n](FLAG …\n)*NPCSCRIPT\n&lt;script&gt;\nNPCSCRIPTEND\n</c>; a class is its script.
/// </summary>
public static class ScriptDocument
{
    /// <summary>The first line of a weapon document.</summary>
    public const string WeaponMagic = "GRAWP001";

    /// <summary>The first line of an NPC document.</summary>
    public const string NpcMagic = "GRNPC001";

    /// <summary>A weapon document.</summary>
    /// <param name="name">The weapon name.</param>
    /// <param name="image">The image.</param>
    /// <param name="script">The script.</param>
    /// <returns>The document.</returns>
    public static string BuildWeapon(string name, string image, string script) =>
        WeaponMagic + "\nREALNAME " + name + "\nIMAGE " + image + "\nSCRIPT\n" + script + "\nSCRIPTEND\n";

    /// <summary>An NPC document; the type defaults to <c>OBJECT</c>, empty flag lines are left out.</summary>
    /// <param name="name">The NPC name.</param>
    /// <param name="id">The NPC id.</param>
    /// <param name="npcType">The type.</param>
    /// <param name="image">The image (left out when empty).</param>
    /// <param name="level">The level (left out when empty).</param>
    /// <param name="flags">The flags, one per line, or <c>null</c>.</param>
    /// <param name="script">The script.</param>
    /// <returns>The document.</returns>
    public static string BuildNpc(string name, int id, string npcType, string image, string level, string? flags, string script)
    {
        var document = new StringBuilder();
        document.Append(NpcMagic).Append("\nNAME ").Append(name).Append("\nID ").Append(id.ToString(CultureInfo.InvariantCulture))
            .Append("\nTYPE ").Append(string.IsNullOrEmpty(npcType) ? "OBJECT" : npcType).Append('\n');
        if (!string.IsNullOrEmpty(image)) document.Append("IMAGE ").Append(image).Append('\n');
        if (!string.IsNullOrEmpty(level)) document.Append("LEVEL ").Append(level).Append('\n');
        if (!string.IsNullOrEmpty(flags))
        {
            foreach (string line in flags.Split('\n'))
            {
                if (line.Length != 0) document.Append("FLAG ").Append(line).Append('\n');
            }
        }

        document.Append("NPCSCRIPT\n").Append(script).Append("\nNPCSCRIPTEND\n");
        return document.ToString();
    }

    /// <summary>
    /// The script inside a document (<c>extractWrappedScript</c>): a class file is the script; a weapon or NPC file is the text between
    /// the <c>SCRIPT</c>/<c>NPCSCRIPT</c> line and the last <c>SCRIPTEND</c>/<c>NPCSCRIPTEND</c> line. The markers must be whole lines
    /// (a <c>\r\n</c> line end is accepted), so a name or image containing <c>SCRIPT</c> cannot confuse it.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="document">The file content.</param>
    /// <returns>The script, or <c>null</c> when the markers are missing (the C++ then uploaded the whole file, header included).</returns>
    public static string? ExtractScript(ScriptKind kind, string document)
    {
        if (kind == ScriptKind.Class) return document;
        string start = kind == ScriptKind.Npc ? "NPCSCRIPT" : "SCRIPT";
        string end = start + "END";
        int body = FindLine(document, start, 0, fromEnd: false);
        if (body < 0) return null;
        int lineEnd = document.IndexOf('\n', body);
        if (lineEnd < 0) return null;
        int bodyStart = lineEnd + 1;
        int endLine = FindLine(document, end, bodyStart, fromEnd: true);
        if (endLine < 0) return null;
        int bodyEnd = endLine - 1;
        if (bodyEnd > bodyStart && document[bodyEnd - 1] == '\r') bodyEnd--;
        return bodyEnd <= bodyStart ? string.Empty : document[bodyStart..bodyEnd];
    }

    /// <summary>Reads a weapon or NPC document's header and script; <c>null</c> when it is not one.</summary>
    /// <param name="document">The file content.</param>
    /// <returns>The parsed document.</returns>
    public static ParsedScriptDocument? TryParse(string document)
    {
        ScriptKind kind;
        if (document.StartsWith(WeaponMagic + "\n", StringComparison.Ordinal) || document.StartsWith(WeaponMagic + "\r\n", StringComparison.Ordinal)) kind = ScriptKind.Weapon;
        else if (document.StartsWith(NpcMagic + "\n", StringComparison.Ordinal) || document.StartsWith(NpcMagic + "\r\n", StringComparison.Ordinal)) kind = ScriptKind.Npc;
        else return null;
        string? script = ExtractScript(kind, document);
        if (script is null) return null;
        string marker = kind == ScriptKind.Npc ? "NPCSCRIPT" : "SCRIPT";
        string name = string.Empty, image = string.Empty, npcType = string.Empty, level = string.Empty;
        int id = 0;
        var flags = new List<string>();
        foreach (string raw in document.Split('\n').Skip(1))
        {
            string line = raw.TrimEnd('\r');
            if (line == marker) break;
            if (Value(line, kind == ScriptKind.Npc ? "NAME" : "REALNAME") is { } nameValue) name = nameValue;
            else if (Value(line, "IMAGE") is { } imageValue) image = imageValue;
            else if (Value(line, "TYPE") is { } typeValue) npcType = typeValue;
            else if (Value(line, "LEVEL") is { } levelValue) level = levelValue;
            else if (Value(line, "ID") is { } idValue) _ = int.TryParse(idValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id);
            else if (Value(line, "FLAG") is { } flag) flags.Add(flag);
        }

        return new ParsedScriptDocument(kind, name, image, id, npcType, level, flags, script);
    }

    private static string? Value(string line, string key) =>
        line.StartsWith(key + " ", StringComparison.Ordinal) ? line[(key.Length + 1)..] : line == key ? string.Empty : null;

    /// <summary>The index of the first character of a line equal to <paramref name="text"/> (optionally ending in <c>\r</c>).</summary>
    private static int FindLine(string document, string text, int from, bool fromEnd)
    {
        int index = fromEnd ? document.Length : from;
        while (true)
        {
            index = fromEnd ? (index <= from ? -1 : document.LastIndexOf(text, index - 1, index - from, StringComparison.Ordinal))
                : document.IndexOf(text, index, StringComparison.Ordinal);
            if (index < 0) return -1;
            bool atStart = index == 0 || document[index - 1] == '\n';
            int after = index + text.Length;
            bool atEnd = after == document.Length || document[after] == '\n' || (document[after] == '\r' && (after + 1 == document.Length || document[after + 1] == '\n'));
            if (atStart && atEnd && (index >= from)) return index;
            if (!fromEnd) index++;
        }
    }
}
