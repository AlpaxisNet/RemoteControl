using System.Security.Cryptography;
using System.Text;

namespace RemoteControl.Core;

/// <summary>
/// Writes a file whole or not at all: the content goes to a temporary file beside the target, is flushed to the disk, and
/// the temporary file is then moved over the target (a rename, atomic on the same volume). If anything fails the target
/// keeps its previous content and the temporary file is removed (AGENTS.md rule 6). The C++ rewrote the options and the
/// profiles in place (defect D-13); only the accounts used this pattern.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Writes bytes to <paramref name="path"/> atomically.</summary>
    /// <param name="path">The target file; its folder is created when missing.</param>
    /// <param name="data">The whole content.</param>
    /// <param name="userOnly">On Unix, create the file readable and writable by the user only (mode 0600).</param>
    /// <exception cref="IOException">The file could not be written; the previous content is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable; the previous content is unchanged.</exception>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data, bool userOnly = false)
    {
        byte[] copy = data.ToArray();
        try
        {
            Write(path, stream => stream.Write(copy, 0, copy.Length), userOnly);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    /// <summary>Writes text as UTF-8 without a byte order mark to <paramref name="path"/> atomically.</summary>
    /// <param name="path">The target file; its folder is created when missing.</param>
    /// <param name="text">The whole content.</param>
    /// <param name="userOnly">On Unix, create the file with mode 0600.</param>
    /// <exception cref="IOException">The file could not be written; the previous content is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable; the previous content is unchanged.</exception>
    public static void WriteAllText(string path, string text, bool userOnly = false)
    {
        byte[] bytes = Utf8NoBom.GetBytes(text ?? string.Empty);
        Write(path, stream => stream.Write(bytes, 0, bytes.Length), userOnly);
    }

    /// <summary>
    /// Writes a file atomically with content produced by <paramref name="writer"/>. If <paramref name="writer"/> throws, the
    /// target is left untouched, the temporary file is removed and the exception is passed on.
    /// </summary>
    /// <param name="path">The target file; its folder is created when missing.</param>
    /// <param name="writer">Writes the whole content to the stream it is given.</param>
    /// <param name="userOnly">On Unix, create the file with mode 0600.</param>
    public static void Write(string path, Action<Stream> writer, bool userOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(writer);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? throw new IOException($"'{path}' has no folder.");
        Directory.CreateDirectory(directory);
        string temporary = TemporaryPath(fullPath);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (userOnly && !OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                writer(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Writes bytes to a file that must not exist yet: the content is written to a temporary file which is then moved to the
    /// target without replacing anything. Used for a new key, so that two instances starting at once cannot both create one.
    /// </summary>
    /// <param name="path">The target file.</param>
    /// <param name="data">The whole content.</param>
    /// <param name="userOnly">On Unix, create the file with mode 0600.</param>
    /// <returns><c>true</c> when the file was created; <c>false</c> when a file of that name already existed (it is not changed).</returns>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static bool TryCreateNew(string path, ReadOnlySpan<byte> data, bool userOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? throw new IOException($"'{path}' has no folder.");
        Directory.CreateDirectory(directory);
        if (File.Exists(fullPath)) return false;
        string temporary = TemporaryPath(fullPath);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (userOnly && !OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(temporary, fullPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                TryDelete(temporary);
                return false;
            }
            return true;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Moves a file aside to <c>&lt;name&gt;.&lt;tag&gt;-&lt;yyyyMMdd-HHmmss&gt;</c> in the same folder (with a counter if that name is taken),
    /// so that a file the program cannot read is kept for the user instead of being overwritten.
    /// </summary>
    /// <param name="path">The file to move.</param>
    /// <param name="tag">A word saying why, for example <c>unreadable</c>.</param>
    /// <param name="now">The time used in the name.</param>
    /// <returns>The new path, or <c>null</c> when the file did not exist.</returns>
    /// <exception cref="IOException">The file could not be moved.</exception>
    public static string? MoveAside(string path, string tag, DateTime now)
    {
        if (!File.Exists(path)) return null;
        string stamp = now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string baseName = $"{path}.{tag}-{stamp}";
        string target = baseName;
        for (int counter = 1; File.Exists(target) || Directory.Exists(target); counter++)
        {
            if (counter > 1000) throw new IOException($"No free name to move '{path}' aside.");
            target = $"{baseName}-{counter}";
        }
        File.Move(path, target, overwrite: false);
        return target;
    }

    private static string TemporaryPath(string fullPath)
    {
        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);
        return fullPath + ".tmp." + Convert.ToHexString(random).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
