using System.Globalization;
using System.Text;
using RemoteControl.Core;

namespace RemoteControl.Ui.Scripts;

/// <summary>One open script editor as <c>script-windows.txt</c> remembers it.</summary>
/// <param name="Session">The session key: server, 0x1F, account.</param>
/// <param name="Type"><c>weapon</c> or <c>class</c>.</param>
/// <param name="Name">The script's name.</param>
/// <param name="X">The window's left edge.</param>
/// <param name="Y">The window's top edge.</param>
/// <param name="Width">Its width.</param>
/// <param name="Height">Its height.</param>
internal sealed record ScriptWindowRecord(string Session, string Type, string Name, int X, int Y, int Width, int Height);

/// <summary>
/// The file of open weapon and class editors (C++ <c>TScriptList</c>, <c>&lt;user folder&gt;/script-windows.txt</c>): one line per editor,
/// <c>session \t type \t name \t x \t y \t width \t height</c>, fields percent-encoded (<c>%XX</c> for <c>%</c>, tab, CR, LF and bytes
/// below 0x20). The editors of a session are opened again when its NC connects, at their size and place. Written whole (rule 6).
/// </summary>
internal sealed class ScriptWindowRecords
{
    private readonly string _path;

    public ScriptWindowRecords(string path) => _path = path;

    /// <summary>The file name in the user folder.</summary>
    public const string FileName = "script-windows.txt";

    /// <summary>The session key of a server and account.</summary>
    public static string SessionKey(string server, string account) => server + "\x1f" + account;

    /// <summary>Reads every record; lines that do not parse are skipped.</summary>
    public IReadOnlyList<ScriptWindowRecord> Load()
    {
        var records = new List<ScriptWindowRecord>();
        try
        {
            if (!File.Exists(_path)) return records;
            foreach (string line in File.ReadAllLines(_path, Encoding.UTF8))
            {
                string[] fields = line.Split('\t').Select(Decode).ToArray();
                if (fields.Length != 7 || fields[0].Length == 0 || fields[1].Length == 0 || fields[2].Length == 0) continue;
                if (!int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                    || !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                    || !int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width)
                    || !int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height)) continue;
                records.Add(new ScriptWindowRecord(fields[0], fields[1], fields[2], x, y, width, height));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Default.Warn("The script window file could not be read.", exception);
        }
        return records;
    }

    /// <summary>Finds the record of an editor.</summary>
    public ScriptWindowRecord? Find(string session, string type, string name) =>
        Load().FirstOrDefault(record => record.Session == session && record.Type == type && record.Name == name);

    /// <summary>Adds or replaces the record of an editor.</summary>
    public void Update(ScriptWindowRecord record)
    {
        List<ScriptWindowRecord> records = Load().ToList();
        int index = records.FindIndex(item => item.Session == record.Session && item.Type == record.Type && item.Name == record.Name);
        if (index >= 0 && records[index] == record) return;
        if (index >= 0) records[index] = record;
        else records.Add(record);
        Save(records);
    }

    /// <summary>Removes the record of an editor (the user closed it).</summary>
    public void Remove(string session, string type, string name)
    {
        List<ScriptWindowRecord> records = Load().ToList();
        if (records.RemoveAll(item => item.Session == session && item.Type == type && item.Name == name) > 0) Save(records);
    }

    internal static string Encode(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c == '%' || c < 0x20) builder.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else builder.Append(c);
        }
        return builder.ToString();
    }

    internal static string Decode(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '%' && index + 2 < value.Length &&int.TryParse(value.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
            {
                builder.Append((char)code);
                index += 2;
                continue;
            }
            builder.Append(value[index]);
        }
        return builder.ToString();
    }

    private void Save(IEnumerable<ScriptWindowRecord> records)
    {
        var builder = new StringBuilder();
        foreach (ScriptWindowRecord record in records)
        {
            builder.Append(Encode(record.Session)).Append('\t').Append(Encode(record.Type)).Append('\t').Append(Encode(record.Name)).Append('\t')
                .Append(record.X.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(record.Y.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(record.Width.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(record.Height.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        try
        {
            AtomicFile.WriteAllText(_path, builder.ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Default.Warn("The script window file could not be written.", exception);
        }
    }
}
