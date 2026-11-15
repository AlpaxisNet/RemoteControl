using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteControl.Core;

namespace RemoteControl.Ui.Common;

/// <summary>
/// Plays a sound file without blocking and without ever throwing. Windows: <c>PlaySound</c> of <c>winmm</c> (asynchronous).
/// Linux: the first of <c>pw-play</c>, <c>paplay</c> and <c>aplay</c> found on <c>PATH</c>, started as a child process that is reaped
/// when it ends. macOS: <c>afplay</c>. Silent when <see cref="Enabled"/> is off, when no player exists, or when a few sounds are already
/// playing (so that a flood of alerts cannot start a flood of processes).
/// </summary>
/// <remarks>
/// The C++ called <c>gdk_beep()</c> for sound alerts (<c>notificationsounds</c>) and shipped <c>sounds/itemget.wav</c> without using it;
/// <see cref="Beep"/> plays that file instead, because Avalonia has no system bell.
/// </remarks>
/// <example>
/// <code>
/// ChatAppendResult result = chat.Append(line);
/// if (result.WantsBeep &amp;&amp; !anyWindowActive) services.Sounds.Beep();
/// </code>
/// </example>
public sealed class SoundPlayer
{
    /// <summary>The sound <see cref="Beep"/> plays.</summary>
    public const string BeepSound = "itemget.wav";

    /// <summary>How many sounds may play at once on Linux and macOS.</summary>
    public const int MaximumConcurrent = 3;

    private static readonly string[] LinuxPlayers = ["pw-play", "paplay", "aplay"];
    private readonly AppPaths _paths;
    private readonly AppLog _log;
    private readonly object _gate = new();
    private string? _player;
    private bool _playerSearched;
    private int _running;

    /// <summary>Creates a player.</summary>
    /// <param name="paths">Where the bundled sounds are (<see cref="AppPaths.SoundsDirectory"/>).</param>
    /// <param name="log">Where problems are logged; <see cref="AppLog.Default"/> when <c>null</c>.</param>
    /// <param name="enabled">Whether sounds are played at all (tests pass <c>false</c>).</param>
    public SoundPlayer(AppPaths paths, AppLog? log = null, bool enabled = true)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? AppLog.Default;
        Enabled = enabled;
    }

    /// <summary>Whether sounds are played. Off in tests.</summary>
    public bool Enabled { get; set; }

    /// <summary>How many sounds were started (for tests and diagnostics).</summary>
    public int StartedCount { get; private set; }

    /// <summary>The external player used on Linux or macOS, or <c>null</c> when there is none (or on Windows).</summary>
    public string? ExternalPlayer
    {
        get
        {
            lock (_gate)
            {
                if (!_playerSearched)
                {
                    _playerSearched = true;
                    _player = FindPlayer();
                }
                return _player;
            }
        }
    }

    /// <summary>Plays the alert sound (<see cref="BeepSound"/>).</summary>
    /// <returns>Whether a sound was started.</returns>
    public bool Beep() => Play(BeepSound);

    /// <summary>Starts playing a sound file and returns at once.</summary>
    /// <param name="file">A file name in the <c>sounds</c> folder, or an absolute path.</param>
    /// <returns>Whether a sound was started; <c>false</c> when disabled, missing, busy or failed (failures are logged).</returns>
    public bool Play(string file)
    {
        try
        {
            if (!Enabled || string.IsNullOrWhiteSpace(file)) return false;
            string path = Path.IsPathRooted(file) ? file : Path.Combine(_paths.SoundsDirectory, file);
            if (!File.Exists(path))
            {
                _log.Warn("The sound " + path + " does not exist.");
                return false;
            }
            if (ExternalPrograms.Disabled)
            {
                // A test run: remember what would have been played, start nothing.
                ExternalPrograms.Suppress("sound " + path);
                return false;
            }
            if (OperatingSystem.IsWindows()) return PlayWindows(path);
            return PlayExternal(path);
        }
        catch (Exception exception)
        {
            _log.Warn("The sound " + file + " could not be played.", exception);
            return false;
        }
    }

    private bool PlayWindows(string path)
    {
        const uint SndAsync = 0x0001;
        const uint SndNoDefault = 0x0002;
        const uint SndFileName = 0x00020000;
        bool started = NativeMethods.PlaySound(path, IntPtr.Zero, SndAsync | SndNoDefault | SndFileName);
        if (started) StartedCount++;
        return started;
    }

    private bool PlayExternal(string path)
    {
        string? player = ExternalPlayer;
        if (player is null) return false;
        if (Interlocked.Increment(ref _running) > MaximumConcurrent)
        {
            Interlocked.Decrement(ref _running);
            return false;
        }
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(player)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            if (Path.GetFileName(player) == "aplay") start.ArgumentList.Add("-q");
            start.ArgumentList.Add(path);
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += (sender, _) =>
            {
                Interlocked.Decrement(ref _running);
                (sender as Process)?.Dispose();
            };
            if (!process.Start())
            {
                Interlocked.Decrement(ref _running);
                process.Dispose();
                return false;
            }
            StartedCount++;
            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Decrement(ref _running);
            process?.Dispose();
            _log.Warn("The sound player " + player + " could not be started.", exception);
            return false;
        }
    }

    private static string? FindPlayer()
    {
        if (OperatingSystem.IsWindows()) return null;
        IEnumerable<string> names = OperatingSystem.IsMacOS() ? ["afplay"] : LinuxPlayers;
        string[] folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string name in names)
        {
            foreach (string folder in folders)
            {
                try
                {
                    string candidate = Path.Combine(folder, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception)
                {
                    // A malformed PATH entry is skipped.
                }
            }
        }
        return null;
    }

    private static class NativeMethods
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "PlaySoundW", SetLastError = false)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PlaySound(string sound, IntPtr module, uint flags);
    }
}
