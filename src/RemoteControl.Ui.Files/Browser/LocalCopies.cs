using System.Text;
using RemoteControl.Core;
using RemoteControl.Protocol.Wire;

namespace RemoteControl.Ui.Files;

/// <summary>The result of writing a downloaded file.</summary>
/// <param name="Path">The file written, or null when it was refused.</param>
/// <param name="Error">Why not, or null.</param>
/// <param name="KeptAside">Where an earlier, different local copy was moved, or null.</param>
internal sealed record LocalWrite(string? Path, string? Error, string? KeptAside);

/// <summary>Where downloads go and how they are written (rule 7: a server's name never leaves the download folder).</summary>
internal static class LocalCopies
{
    /// <summary>The largest file read for an upload (<c>maximumLocalTransferBytes</c>, 256 MiB).</summary>
    public const long MaximumTransferBytes = 256L * 1024 * 1024;

    /// <summary>The largest file opened in the text editor (<c>maximumEditorBytes</c>, 64 MiB).</summary>
    public const int MaximumEditorBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Extensions a double click opens in the text editor instead of the desktop's program: the C++ <c>.exe .bat .sh</c> and everything
    /// else a desktop would run (defect D-29).
    /// </summary>
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".sh", ".cmd", ".com", ".scr", ".pif", ".cpl", ".msi", ".msp", ".msc", ".hta", ".lnk", ".url", ".reg",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ws", ".ps1", ".psm1", ".psd1", ".jar", ".py", ".pyw", ".pl", ".rb", ".php",
        ".desktop", ".appimage", ".run", ".command", ".app", ".scpt", ".bash", ".zsh", ".csh", ".ksh", ".fish", ".elf", ".so", ".dll",
        ".application", ".gadget", ".inf", ".ins", ".isp", ".job", ".sct", ".shb", ".shs", ".settingcontent-ms", ".appref-ms",
    };

    /// <summary>Whether a double click must not hand the file to the desktop (it would run it).</summary>
    /// <param name="name">The file name.</param>
    /// <returns>Whether it opens in the text editor.</returns>
    public static bool IsRunnable(string name)
    {
        string extension = Path.GetExtension(name ?? string.Empty);
        return extension.Length != 0 && RunnableExtensions.Contains(extension);
    }

    /// <summary>A server or folder name as one safe folder name (<c>safeDownloadComponent</c>).</summary>
    /// <param name="value">The name.</param>
    /// <param name="fallback">What an empty result becomes.</param>
    /// <returns>The folder name.</returns>
    public static string SafeComponent(string? value, string fallback)
    {
        var result = new StringBuilder();
        foreach (char character in value ?? string.Empty)
        {
            if (character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') result.Append('_');
            else if (character >= ' ' && character != '\u007f') result.Append(character);
        }

        string text = result.ToString().TrimEnd(' ', '.');
        if (text.Length == 0 || text == "." || text == ".." || !SafePath.IsSafeSegment(text, out _)) return fallback;
        return text;
    }

    /// <summary>
    /// The local file of a remote file: <c>&lt;download folder&gt;/&lt;server&gt;/&lt;remote folders&gt;/&lt;name&gt;</c>. A remote path
    /// that would leave that folder (<c>..</c>, an absolute path, a drive letter, a stream name) is refused.
    /// </summary>
    /// <param name="downloadFolder">The download folder (<c>AppPaths.DownloadDirectory</c>).</param>
    /// <param name="server">The server's name.</param>
    /// <param name="remotePath">The remote path.</param>
    /// <param name="path">The local file.</param>
    /// <param name="reason">Why it was refused.</param>
    /// <returns>Whether it is acceptable.</returns>
    public static bool TryTarget(string downloadFolder, string server, string remotePath, out string path, out string reason)
    {
        path = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(downloadFolder))
        {
            reason = "Specify a download folder in RC Options first.";
            return false;
        }

        string root = Path.Combine(downloadFolder, SafeComponent(server, "Server"));
        if (!SafePath.TryCombine(root, remotePath, out string? combined, out string? refused))
        {
            reason = "Refused to save " + remotePath + ": " + refused;
            return false;
        }

        path = combined;
        return true;
    }

    /// <summary>
    /// Writes a downloaded file whole and gives it the server's time. A different local copy that is already there is moved aside
    /// first, never overwritten (defect D-28). Runs on a worker thread.
    /// </summary>
    /// <param name="path">The local file (from <see cref="TryTarget"/>).</param>
    /// <param name="content">The bytes.</param>
    /// <param name="modified">The server's time in Unix seconds, or 0.</param>
    /// <returns>What happened.</returns>
    public static Task<LocalWrite> WriteAsync(string path, byte[] content, long modified) => Task.Run(() =>
    {
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            string? aside = null;
            if (File.Exists(path))
            {
                bool same = new FileInfo(path).Length == content.LongLength && File.ReadAllBytes(path).AsSpan().SequenceEqual(content);
                if (!same) aside = AtomicFile.MoveAside(path, "local", DateTime.Now);
            }

            AtomicFile.WriteAllBytes(path, content);
            SetTime(path, modified);
            return new LocalWrite(path, null, aside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new LocalWrite(null, "Could not save downloaded file: " + exception.Message, null);
        }
    });

    /// <summary>Gives a file the server's time (<c>applyModifiedTime</c>).</summary>
    /// <param name="path">The file.</param>
    /// <param name="modified">Unix seconds; 0 or less leaves it.</param>
    public static void SetTime(string path, long modified)
    {
        if (modified <= 0) return;
        try
        {
            File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(Math.Min(modified, 253402300799)).UtcDateTime);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The time is a courtesy; the content is what matters.
        }
    }

    /// <summary>Reads a local file for an upload, refusing files over the limits.</summary>
    /// <param name="path">The file.</param>
    /// <param name="serverLimit">The server's announced limit, or 0.</param>
    /// <returns>The bytes, or the reason.</returns>
    public static Task<(byte[]? Content, string? Error)> ReadForUploadAsync(string path, long serverLimit) => Task.Run<(byte[]?, string?)>(() =>
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (null, "Unable to read " + path + ": it does not exist.");
            if (info.Length > MaximumTransferBytes) return (null, "Selected file exceeds the 256 MiB transfer limit.");
            if (serverLimit > 0 && info.Length > serverLimit) return (null, $"{info.Name} is larger than the server accepts ({serverLimit} bytes).");
            byte[] content = File.ReadAllBytes(path);
            if (content.LongLength > MaximumTransferBytes) return (null, "Selected file exceeds the 256 MiB transfer limit.");
            return (content, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, "Unable to read selected file: " + exception.Message);
        }
    });

    /// <summary>The time and size of a local file, or null.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its stamp.</returns>
    public static (DateTime Modified, long Length)? StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>How a file's bytes become editor text and back, so that saving writes what the editor showed (defect D-27).</summary>
/// <param name="Text">The text.</param>
/// <param name="Utf8">Whether it was UTF-8 (otherwise Windows-1252, the servers' encoding).</param>
/// <param name="Bom">Whether it began with a UTF-8 byte-order mark.</param>
internal sealed record EditableText(string Text, bool Utf8, bool Bom)
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Decodes a file for the editor; a binary file (a NUL byte) gives null.</summary>
    /// <param name="content">The bytes.</param>
    /// <returns>The text, or null.</returns>
    public static EditableText? Decode(byte[] content)
    {
        if (content.AsSpan().IndexOf((byte)0) >= 0) return null;
        bool bom = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF;
        ReadOnlySpan<byte> body = bom ? content.AsSpan(3) : content;
        try
        {
            return new EditableText(StrictUtf8.GetString(body), true, bom);
        }
        catch (DecoderFallbackException)
        {
            return new EditableText(RcText.GetString(content), false, false);
        }
    }

    /// <summary>Encodes edited text the way the file was read.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The bytes.</returns>
    public byte[] Encode(string text)
    {
        if (!Utf8) return RcText.GetBytes(text);
        byte[] body = new UTF8Encoding(false).GetBytes(text ?? string.Empty);
        if (!Bom) return body;
        byte[] result = new byte[body.Length + 3];
        result[0] = 0xEF;
        result[1] = 0xBB;
        result[2] = 0xBF;
        body.CopyTo(result, 3);
        return result;
    }
}
