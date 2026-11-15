using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using RemoteControl.Core;


namespace RemoteControl.Ui.Files;

/// <summary>Which files get a preview (<c>isPreviewImage</c>, <c>canAutoPreview</c>, <c>TFileBrowserTree.cpp:156-164</c>, <c>:1371-1383</c>).</summary>
internal static class PreviewRules
{
    /// <summary>The largest picture downloaded for a preview.</summary>
    public const long MaximumSize = 512 * 1024;

    /// <summary>The largest tile set downloaded for a preview.</summary>
    public const long MaximumTilesetSize = 128 * 1024;

    /// <summary>The preview popup's largest picture (360 by 260).</summary>
    public static PixelSize PreviewSize { get; } = new(360, 260);

    /// <summary>The modern view's thumbnail size (88).</summary>
    public const int ThumbnailSize = 88;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".mng"];

    /// <summary>Whether the name is a picture the browser can show.</summary>
    /// <param name="path">The name or path.</param>
    /// <returns>Whether it is a picture.</returns>
    public static bool IsImage(string? path)
    {
        string value = (path ?? string.Empty).ToLowerInvariant();
        int dot = value.LastIndexOf('.');
        return dot >= 0 && Extensions.Contains(value[dot..], StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a file is downloaded by itself for a preview: a picture of known size above 0, not animated (<c>.gif</c>, <c>.mng</c>),
    /// at most 512 KiB, or 128 KiB when the name contains <c>tileset</c>, <c>_tiles</c> or <c>-tiles</c>.
    /// </summary>
    /// <param name="path">The name or path.</param>
    /// <param name="size">The size from the listing.</param>
    /// <returns>Whether it is eligible.</returns>
    public static bool CanAutoPreview(string? path, long size)
    {
        if (!IsImage(path) || size <= 0) return false;
        string lowered = (path ?? string.Empty).ToLowerInvariant();
        if (lowered.EndsWith(".gif", StringComparison.Ordinal) || lowered.EndsWith(".mng", StringComparison.Ordinal)) return false;
        bool tileset = lowered.Contains("tileset", StringComparison.Ordinal) || lowered.Contains("_tiles", StringComparison.Ordinal) || lowered.Contains("-tiles", StringComparison.Ordinal);
        return size <= (tileset ? MaximumTilesetSize : MaximumSize);
    }
}

/// <summary>A decoded preview: the popup picture and the thumbnail.</summary>
/// <param name="Preview">At most 360 by 260.</param>
/// <param name="Thumbnail">At most 88 by 88.</param>
internal sealed record PreviewImage(Bitmap Preview, Bitmap Thumbnail);

/// <summary>Decodes previews off the UI thread (<c>previewWorkerLoop</c>): PNG, JPEG, GIF, WebP, BMP and ICO through Skia, MNG through its first PNG frame.</summary>
internal static class PreviewDecoder
{
    /// <summary>Decodes a picture; a broken one gives null.</summary>
    /// <param name="path">The name (for the MNG check).</param>
    /// <param name="content">The bytes.</param>
    /// <returns>The preview, or null.</returns>
    public static PreviewImage? Decode(string path, byte[] content)
    {
        try
        {
            if (content is null || content.Length == 0) return null;
            byte[] data = content;
            if (MngImage.IsMng(content) || path.EndsWith(".mng", StringComparison.OrdinalIgnoreCase))
            {
                data = MngImage.FirstPngFrame(content);
                if (data.Length == 0) return null;
            }

            using var stream = new MemoryStream(data, writable: false);
            using var full = new Bitmap(stream);
            if (full.PixelSize.Width <= 0 || full.PixelSize.Height <= 0) return null;
            Bitmap preview = Scale(full, PreviewRules.PreviewSize.Width, PreviewRules.PreviewSize.Height, allowUp: false);
            Bitmap thumbnail = Scale(full, PreviewRules.ThumbnailSize, PreviewRules.ThumbnailSize, allowUp: true);
            return new PreviewImage(preview, thumbnail);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Decodes on a worker thread.</summary>
    /// <param name="path">The name.</param>
    /// <param name="content">The bytes.</param>
    /// <param name="cancellationToken">Stops before decoding.</param>
    /// <returns>The preview, or null.</returns>
    public static Task<PreviewImage?> DecodeAsync(string path, byte[] content, CancellationToken cancellationToken) =>
        Task.Run(() => cancellationToken.IsCancellationRequested ? null : Decode(path, content), cancellationToken);

    private static Bitmap Scale(Bitmap source, int width, int height, bool allowUp)
    {
        double scale = Math.Min((double)width / Math.Max(1, source.PixelSize.Width), (double)height / Math.Max(1, source.PixelSize.Height));
        if (!allowUp) scale = Math.Min(1.0, scale);
        var size = new PixelSize(Math.Max(1, (int)(source.PixelSize.Width * scale)), Math.Max(1, (int)(source.PixelSize.Height * scale)));
        return source.CreateScaledBitmap(size, BitmapInterpolationMode.MediumQuality);
    }
}

/// <summary>
/// The previews of the file browser: the newest <see cref="MemoryCapacity"/> decoded pictures in memory (the C++ LRU of 50) and the
/// downloaded bytes in <see cref="AppPaths.PreviewCacheDirectory"/>, at most <see cref="DiskCapacity"/> files and
/// <see cref="DiskBytes"/> bytes, so a folder seen before needs no new downloads. A file is keyed by server, path, size and time, so a
/// changed file is downloaded again. Memory is used on the UI thread; the disk on worker threads.
/// </summary>
internal sealed class PreviewCache
{
    /// <summary>Decoded pictures kept in memory.</summary>
    public const int MemoryCapacity = 50;

    /// <summary>Files kept on disk by default.</summary>
    public const int DefaultDiskCapacity = 400;

    /// <summary>Bytes kept on disk by default.</summary>
    public const long DefaultDiskBytes = 64L * 1024 * 1024;

    private readonly Dictionary<string, LinkedListNode<(string Key, PreviewImage Image)>> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, PreviewImage Image)> _order = new();
    private readonly HashSet<string> _broken = new(StringComparer.Ordinal);
    private readonly string? _folder;
    private readonly object _diskGate = new();

    /// <summary>Creates the cache.</summary>
    /// <param name="folder">The disk folder, or null for memory only.</param>
    /// <param name="diskCapacity">The most files kept on disk.</param>
    /// <param name="diskBytes">The most bytes kept on disk.</param>
    public PreviewCache(string? folder, int diskCapacity = DefaultDiskCapacity, long diskBytes = DefaultDiskBytes)
    {
        _folder = folder;
        DiskCapacity = diskCapacity;
        DiskBytes = diskBytes;
    }

    /// <summary>The most files kept on disk.</summary>
    public int DiskCapacity { get; }

    /// <summary>The most bytes kept on disk.</summary>
    public long DiskBytes { get; }

    /// <summary>Pictures in memory.</summary>
    public int Count => _memory.Count;

    /// <summary>The key of a file.</summary>
    /// <param name="server">The server.</param>
    /// <param name="path">The remote path.</param>
    /// <param name="size">Its size.</param>
    /// <param name="modified">Its time.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(string server, string path, long size, long modified) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server + "\n" + path + "\n" + size + "\n" + modified))).ToLowerInvariant();

    /// <summary>The decoded picture of a key, or null; a hit becomes the newest.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The picture.</returns>
    public PreviewImage? Get(string key)
    {
        if (!_memory.TryGetValue(key, out LinkedListNode<(string Key, PreviewImage Image)>? node)) return null;
        _order.Remove(node);
        _order.AddFirst(node);
        return node.Value.Image;
    }

    /// <summary>Whether the key's picture is known to be broken (it is not downloaded again).</summary>
    /// <param name="key">The key.</param>
    /// <returns>Whether it is broken.</returns>
    public bool IsBroken(string key) => _broken.Contains(key);

    /// <summary>Remembers that a picture could not be decoded.</summary>
    /// <param name="key">The key.</param>
    public void MarkBroken(string key)
    {
        if (_broken.Count > 2000) _broken.Clear();
        _broken.Add(key);
    }

    /// <summary>Keeps a decoded picture; the oldest beyond <see cref="MemoryCapacity"/> is dropped.</summary>
    /// <param name="key">The key.</param>
    /// <param name="image">The picture.</param>
    public void Put(string key, PreviewImage image)
    {
        if (_memory.TryGetValue(key, out LinkedListNode<(string Key, PreviewImage Image)>? existing))
        {
            _order.Remove(existing);
            _memory.Remove(key);
        }

        _memory[key] = _order.AddFirst((key, image));
        while (_order.Count > MemoryCapacity && _order.Last is { } last)
        {
            _order.RemoveLast();
            _memory.Remove(last.Value.Key);
        }
    }

    /// <summary>Forgets the pictures in memory (the disk copies stay).</summary>
    public void ClearMemory()
    {
        _memory.Clear();
        _order.Clear();
    }

    /// <summary>The downloaded bytes of a key from the disk, or null.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The bytes.</returns>
    public Task<byte[]?> ReadAsync(string key) => _folder is null ? Task.FromResult<byte[]?>(null) : Task.Run(() =>
    {
        try
        {
            string file = Path.Combine(_folder, key + ".bin");
            if (!File.Exists(file)) return null;
            byte[] bytes = File.ReadAllBytes(file);
            File.SetLastAccessTimeUtc(file, DateTime.UtcNow);
            return bytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    });

    /// <summary>Writes downloaded bytes for a key and trims the folder to its limits.</summary>
    /// <param name="key">The key.</param>
    /// <param name="content">The bytes.</param>
    /// <returns>Completes when written.</returns>
    public Task WriteAsync(string key, byte[] content) => _folder is null ? Task.CompletedTask : Task.Run(() =>
    {
        lock (_diskGate)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                AtomicFile.WriteAllBytes(Path.Combine(_folder, key + ".bin"), content);
                Trim();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The cache is only a cache: a full disk or a locked file costs a download next time.
            }
        }
    });

    /// <summary>The files of the disk cache (for tests).</summary>
    /// <returns>Their paths.</returns>
    public IReadOnlyList<string> DiskFiles() => _folder is null || !Directory.Exists(_folder) ? Array.Empty<string>() : Directory.GetFiles(_folder, "*.bin");

    private void Trim()
    {
        if (_folder is null) return;
        FileInfo[] files = new DirectoryInfo(_folder).GetFiles("*.bin").OrderByDescending(file => file.LastAccessTimeUtc > file.LastWriteTimeUtc ? file.LastAccessTimeUtc : file.LastWriteTimeUtc).ToArray();
        long total = 0;
        for (int index = 0; index < files.Length; index++)
        {
            total += files[index].Length;
            if (index < DiskCapacity && total <= DiskBytes) continue;
            try
            {
                files[index].Delete();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Removed next time.
            }
        }
    }
}
