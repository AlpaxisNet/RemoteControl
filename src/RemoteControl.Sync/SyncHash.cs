using System.Security.Cryptography;

namespace RemoteControl.Sync;

/// <summary>
/// The content hash kept in the manifest: <c>sha256:&lt;64 lower-case hex digits&gt;</c>. The C++ stored <c>std::hash</c> values, which differ
/// between compilers and word sizes (defect D-14); such values are recognised as legacy and never trusted.
/// </summary>
public static class SyncHash
{
    /// <summary>The prefix of a hash this program wrote.</summary>
    public const string Prefix = "sha256:";

    /// <summary>The hash of bytes.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The hash text.</returns>
    public static string Of(ReadOnlySpan<byte> data) => Prefix + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>The hash of a file, read in pieces (the file is never held in memory whole).</summary>
    /// <param name="path">The file.</param>
    /// <returns>The hash text.</returns>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static string OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
        return Prefix + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Whether a manifest value is a hash this program wrote (and may therefore be trusted).</summary>
    /// <param name="value">The value.</param>
    /// <returns><c>true</c> for <c>sha256:</c> and 64 hex digits.</returns>
    public static bool IsTrusted(string? value) =>
        value is not null && value.Length == Prefix.Length + 64 && value.StartsWith(Prefix, StringComparison.Ordinal)
        && value.AsSpan(Prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
}
