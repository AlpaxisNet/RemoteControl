using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RemoteControl.Core;

namespace RemoteControl.Ui.Common;

/// <summary>The kinds of private message, each with its icon (<c>pmIconFor</c>, <c>TPlayerList.cpp:1472-1477</c>).</summary>
public enum PmKind
{
    /// <summary>A normal private message (<c>pmicon_normal.png</c>).</summary>
    Normal,

    /// <summary>A guild message (<c>pmicon_guild.png</c>).</summary>
    Guild,

    /// <summary>An admin message (<c>pmicon_admin.png</c>).</summary>
    Admin,

    /// <summary>A mass message (<c>pmicon_mass.png</c>).</summary>
    Mass,
}

/// <summary>One picture word of the chat (<c>applyEmotes</c>, <c>TRemoteFrame.cpp:2404-2421</c>).</summary>
/// <param name="Text">The word that is replaced, for example <c>Kappa</c>.</param>
/// <param name="ImageName">The picture in the <c>images</c> folder.</param>
public sealed record Emote(string Text, string ImageName);

/// <summary>
/// The bundled pictures by name, found through <see cref="AppPaths.ResolveImage"/> (or an absolute path), decoded once and kept in a
/// bounded cache (least recently used pictures are dropped first). A missing or broken picture gives <see cref="Placeholder"/> and
/// one log line; nothing here throws. Also the helpers for the pictures the C++ chose by rule: status icons, PM icons, file-type
/// icons, server-type icons, emotes, the graphical-menu buttons and the application icon.
/// </summary>
/// <remarks>
/// Must be used on the UI thread after the Avalonia platform is initialised (decoding needs the renderer). A dropped cache entry is
/// not disposed, because a control may still show it; the garbage collector frees it.
/// </remarks>
/// <example>
/// <code>
/// image.Source = services.Assets.Get("rcicon_gold.png");
/// row.Icon = services.Assets.StatusIcon("Away");          // plisticonaway.png
/// fileIcon.Source = services.Assets.FileTypeIcon("levels/start.nw");
/// </code>
/// </example>
public sealed class AssetStore
{
    /// <summary>The application icon picture.</summary>
    public const string AppIconName = "rcicon.png";

    /// <summary>The status icon used when a status has no picture.</summary>
    public const string OnlineStatusIcon = "plisticononline.png";

    private readonly object _gate = new();
    private readonly AppPaths _paths;
    private readonly AppLog _log;
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _order = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private Bitmap? _placeholder;
    private WindowIcon? _appIcon;

    /// <summary>Creates the store.</summary>
    /// <param name="paths">Where the pictures are.</param>
    /// <param name="log">Where missing pictures are logged; <see cref="AppLog.Default"/> when <c>null</c>.</param>
    /// <param name="capacity">How many decoded pictures are kept (at least 8).</param>
    public AssetStore(AppPaths paths, AppLog? log = null, int capacity = 256)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? AppLog.Default;
        Capacity = Math.Max(8, capacity);
    }

    /// <summary>The emotes of the chat, in the order the C++ applied them.</summary>
    public static IReadOnlyList<Emote> Emotes { get; } = [new("Kappa", "emote_kappa.png"), new("PMNormal", "pmicon_normal.png"), new(":v", "emote_pacman.png")];

    /// <summary>The paths the pictures are found through.</summary>
    public AppPaths Paths => _paths;

    /// <summary>How many decoded pictures are kept.</summary>
    public int Capacity { get; }

    /// <summary>How many decoded pictures are kept now.</summary>
    public int CachedCount
    {
        get { lock (_gate) return _cache.Count; }
    }

    /// <summary>The picture shown for a missing or broken one: 16 by 16 pixels, a grey frame with a red cross.</summary>
    public Bitmap Placeholder => _placeholder ??= CreatePlaceholder();

    /// <summary>The application icon (<c>images/rcicon.png</c>), or <c>null</c> when it cannot be read.</summary>
    public WindowIcon? AppIcon
    {
        get
        {
            if (_appIcon is not null) return _appIcon;
            if (!TryGet(AppIconName, out Bitmap? bitmap)) return null;
            try
            {
                _appIcon = new WindowIcon(bitmap);
            }
            catch (Exception exception)
            {
                _log.Warn("The application icon could not be created.", exception);
            }
            return _appIcon;
        }
    }

    /// <summary>The full path a picture name stands for: an absolute path as it is, otherwise <see cref="AppPaths.ResolveImage"/>.</summary>
    /// <param name="name">A file name in the <c>images</c> folder, or an absolute path.</param>
    /// <returns>The full path.</returns>
    public string Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        try
        {
            return Path.IsPathRooted(name) ? Path.GetFullPath(name) : _paths.ResolveImage(name);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Whether a picture file exists (it may still be broken).</summary>
    /// <param name="name">A file name in the <c>images</c> folder, or an absolute path.</param>
    /// <returns><c>true</c> when the file exists.</returns>
    public bool Exists(string name)
    {
        string path = Resolve(name);
        return path.Length > 0 && File.Exists(path);
    }

    /// <summary>A picture, or <see cref="Placeholder"/> when it is missing or broken (logged once per name).</summary>
    /// <param name="name">A file name in the <c>images</c> folder, or an absolute path.</param>
    /// <returns>The picture.</returns>
    public Bitmap Get(string name) => TryGet(name, out Bitmap? bitmap) ? bitmap : Placeholder;

    /// <summary>A picture, if it can be read.</summary>
    /// <param name="name">A file name in the <c>images</c> folder, or an absolute path.</param>
    /// <param name="bitmap">The picture.</param>
    /// <returns><c>false</c> when the picture is missing or broken (logged once per name).</returns>
    public bool TryGet(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Bitmap? bitmap)
    {
        bitmap = null;
        string path = Resolve(name);
        if (path.Length == 0) return false;
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out LinkedListNode<CacheEntry>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                bitmap = node.Value.Bitmap;
                return true;
            }
        }
        Bitmap? loaded = Load(path);
        if (loaded is null) return false;
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out LinkedListNode<CacheEntry>? existing))
            {
                bitmap = existing.Value.Bitmap;
                return true;
            }
            var node = new LinkedListNode<CacheEntry>(new CacheEntry(path, loaded));
            _order.AddFirst(node);
            _cache[path] = node;
            while (_cache.Count > Capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _cache.Remove(last.Value.Path);
            }
        }
        bitmap = loaded;
        return true;
    }

    /// <summary>Forgets every decoded picture (for example after the pictures folder changed).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
            _order.Clear();
            _reported.Clear();
        }
        _appIcon = null;
    }

    /// <summary>
    /// The picture name of a player status as the server's status list names it (<c>loadStatusIcons</c>): <c>plisticon</c> + the
    /// lower-case name + <c>.png</c>, with <c>rping</c> becoming <c>role-playing</c>.
    /// </summary>
    /// <param name="status">The status name, for example <c>Away</c> or <c>No PMs</c>.</param>
    /// <returns>The file name, for example <c>plisticonaway.png</c>.</returns>
    public static string StatusIconName(string? status)
    {
        string name = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (name == "rping") name = "role-playing";
        return "plisticon" + name + ".png";
    }

    /// <summary>The icon of a player status, or <c>plisticononline.png</c> when the status has no picture (<c>statusIconFor</c>).</summary>
    /// <param name="status">The status name.</param>
    /// <returns>The picture.</returns>
    public Bitmap StatusIcon(string? status)
    {
        string name = StatusIconName(status);
        return !string.IsNullOrWhiteSpace(status) && Exists(name) && TryGet(name, out Bitmap? bitmap) ? bitmap : Get(OnlineStatusIcon);
    }

    /// <summary>The kind of a private message from the type word the C++ used (<c>mass</c>, <c>guild</c>, <c>admin</c>, anything else normal).</summary>
    /// <param name="type">The type word.</param>
    /// <returns>The kind.</returns>
    public static PmKind ParsePmKind(string? type) => type switch
    {
        "mass" => PmKind.Mass,
        "guild" => PmKind.Guild,
        "admin" => PmKind.Admin,
        _ => PmKind.Normal,
    };

    /// <summary>The picture name of a private-message kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>For example <c>pmicon_guild.png</c>.</returns>
    public static string PmIconName(PmKind kind) => kind switch
    {
        PmKind.Mass => "pmicon_mass.png",
        PmKind.Guild => "pmicon_guild.png",
        PmKind.Admin => "pmicon_admin.png",
        _ => "pmicon_normal.png",
    };

    /// <summary>The icon of a private-message kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The picture.</returns>
    public Bitmap PmIcon(PmKind kind) => Get(PmIconName(kind));

    /// <summary>
    /// The picture name of a file in the file browser (<c>fileIcon</c>, <c>TFileBrowserTree.cpp:343-360</c>): a path containing
    /// <c>config</c>, <c>settings</c> or <c>options</c> is a configuration file; otherwise by extension (<c>.bin .gs2bc</c> binary,
    /// <c>.txt</c> text, <c>.ttf .otf</c> font, <c>.tar .zip .rar .7z</c> archive, <c>.conf</c>, <c>.gmap</c>, <c>.graal</c>, <c>.nw</c>), else unknown.
    /// </summary>
    /// <param name="path">The file name or path.</param>
    /// <returns>For example <c>rcfiles_nw.png</c>.</returns>
    public static string FileTypeIconName(string? path)
    {
        string value = (path ?? string.Empty).ToLowerInvariant();
        if (value.Contains("config", StringComparison.Ordinal) || value.Contains("settings", StringComparison.Ordinal) || value.Contains("options", StringComparison.Ordinal)) return "rcfiles_conf.png";
        int dot = value.LastIndexOf('.');
        string suffix = dot < 0 ? string.Empty : value[dot..];
        return suffix switch
        {
            ".bin" or ".gs2bc" => "rcfiles_binary.png",
            ".txt" => "rcfiles_text2.png",
            ".ttf" or ".otf" => "rcfiles_ttf.png",
            ".tar" or ".zip" or ".rar" or ".7z" => "rcfiles_archive.png",
            ".conf" => "rcfiles_conf.png",
            ".gmap" => "rcfiles_gmap.png",
            ".graal" => "rcfiles_graal.png",
            ".nw" => "rcfiles_nw.png",
            _ => "rcfiles_unknown.png",
        };
    }

    /// <summary>The file-browser icon of a file.</summary>
    /// <param name="path">The file name or path.</param>
    /// <returns>The picture.</returns>
    public Bitmap FileTypeIcon(string? path) => Get(FileTypeIconName(path));

    /// <summary>The picture name of a server type in the server list (<c>TServerList.cpp:111-120</c>): 0 gold, 1 UC, 2 bronze, 3 silver; others gold.</summary>
    /// <param name="type">The server type index.</param>
    /// <returns>For example <c>rcicon_bronze.png</c>.</returns>
    public static string ServerTypeIconName(int type) => type switch
    {
        1 => "rcicon_uc.png",
        2 => "rcicon_bronze.png",
        3 => "rcicon_silver.png",
        _ => "rcicon_gold.png",
    };

    /// <summary>The server-list icon of a server type.</summary>
    /// <param name="type">The server type index.</param>
    /// <returns>The picture.</returns>
    public Bitmap ServerTypeIcon(int type) => Get(ServerTypeIconName(type));

    /// <summary>The player-list icon of an IRC channel (<c>rcicon_channelopen.png</c> or <c>rcicon_channelclosed.png</c>).</summary>
    /// <param name="open">Whether the channel is joined.</param>
    /// <returns>The picture.</returns>
    public Bitmap ChannelIcon(bool open) => Get(open ? "rcicon_channelopen.png" : "rcicon_channelclosed.png");

    /// <summary>The ban-window tab icon (<c>rcicon_banned.png</c> or <c>rcicon_unbanned.png</c>).</summary>
    /// <param name="banned">Whether the scope is banned.</param>
    /// <returns>The picture.</returns>
    public Bitmap BanIcon(bool banned) => Get(banned ? "rcicon_banned.png" : "rcicon_unbanned.png");

    /// <summary>The picture of an emote word, or <c>null</c> for a word that is not an emote or whose picture is missing.</summary>
    /// <param name="text">The word, for example <c>Kappa</c>.</param>
    /// <returns>The picture or <c>null</c>.</returns>
    public Bitmap? EmoteImage(string text)
    {
        foreach (Emote emote in Emotes)
            if (emote.Text == text) return TryGet(emote.ImageName, out Bitmap? bitmap) ? bitmap : null;
        return null;
    }

    /// <summary>
    /// The pressed picture of a graphical-menu button (<c>onGraphicalButton</c>, <c>TRemoteFrame.cpp:1242-1249</c>): the option
    /// <c>icon&lt;name&gt;pressed</c>, except when it equals the normal picture and a <c>..._pressed...</c> file exists beside it.
    /// </summary>
    /// <param name="normal">The normal picture name (option <c>icon&lt;name&gt;</c>).</param>
    /// <param name="pressed">The pressed picture name (option <c>icon&lt;name&gt;pressed</c>).</param>
    /// <returns>The picture name to show while the button is pressed.</returns>
    public string PressedImageName(string normal, string pressed)
    {
        if (!string.Equals(normal, pressed, StringComparison.Ordinal)) return pressed;
        int suffix = normal.LastIndexOf("_normal", StringComparison.Ordinal);
        if (suffix < 0) return pressed;
        string candidate = normal[..suffix] + "_pressed" + normal[(suffix + 7)..];
        return Exists(candidate) ? candidate : pressed;
    }

    private Bitmap? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Report(path, "The picture " + path + " does not exist.", null);
                return null;
            }
            byte[] bytes = File.ReadAllBytes(path);
            if (MngImage.IsMng(bytes))
            {
                byte[] png = MngImage.FirstPngFrame(bytes);
                if (png.Length == 0)
                {
                    Report(path, "The picture " + path + " is not a readable MNG.", null);
                    return null;
                }
                bytes = png;
            }
            using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception exception)
        {
            Report(path, "The picture " + path + " could not be read.", exception);
            return null;
        }
    }

    private void Report(string path, string message, Exception? exception)
    {
        lock (_gate)
            if (!_reported.Add(path)) return;
        _log.Warn(message, exception);
    }

    private Bitmap CreatePlaceholder()
    {
        try
        {
            const int size = 16;
            var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (ILockedFramebuffer buffer = bitmap.Lock())
            {
                var pixels = new int[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        bool frame = x == 0 || y == 0 || x == size - 1 || y == size - 1;
                        bool cross = x > 2 && x < size - 3 && (x == y || x == size - 1 - y);
                        pixels[y * size + x] = frame ? unchecked((int)0xFF808080) : cross ? unchecked((int)0xFFD03030) : 0;
                    }
                }
                for (int row = 0; row < size; row++)
                    System.Runtime.InteropServices.Marshal.Copy(pixels, row * size, buffer.Address + row * buffer.RowBytes, size);
            }
            return bitmap;
        }
        catch (Exception exception)
        {
            _log.Error("The placeholder picture could not be created.", exception);
            throw;
        }
    }

    private sealed record CacheEntry(string Path, Bitmap Bitmap);
}
