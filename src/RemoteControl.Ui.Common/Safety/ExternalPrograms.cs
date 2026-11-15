using System.Reflection;
using RemoteControl.Core;

namespace RemoteControl.Ui.Common;

/// <summary>
/// The one switch for everything the windows start outside the program: the web browser (<see cref="UrlOpener"/>) and the sound
/// players (<see cref="SoundPlayer"/>). While <see cref="Disabled"/> is set nothing is started; what would have been started is
/// kept in <see cref="Suppressed"/> so a test can check it. It is set by itself in a test run (a test once opened a link in the
/// user's real browser), and the test kit sets it again.
/// </summary>
public static class ExternalPrograms
{
    private const int MaxSuppressed = 200;
    private static readonly object s_gate = new();
    private static readonly List<string> s_suppressed = [];
    private static volatile bool s_disabled = RunsUnderTestHost();

    /// <summary>Whether starting programs outside the process is switched off (always in a test run).</summary>
    public static bool Disabled
    {
        get => s_disabled;
        set => s_disabled = value;
    }

    /// <summary>What was asked for while <see cref="Disabled"/> was set, oldest first: <c>"url https://…"</c>, <c>"sound …"</c>.</summary>
    public static IReadOnlyList<string> Suppressed
    {
        get
        {
            lock (s_gate) return s_suppressed.ToArray();
        }
    }

    /// <summary>Forgets <see cref="Suppressed"/> (a test calls it before the action it checks).</summary>
    public static void ClearSuppressed()
    {
        lock (s_gate) s_suppressed.Clear();
    }

    /// <summary>
    /// Opens a local file (or folder) with the desktop's default program for it, as a double click in a file manager would:
    /// <c>xdg-open</c> on Linux, <c>open</c> on macOS, the shell on Windows. Never throws and never blocks. While
    /// <see cref="Disabled"/> is set nothing is started and <c>"file &lt;path&gt;"</c> is recorded.
    /// </summary>
    /// <param name="path">The file or folder; it must exist.</param>
    /// <param name="log">Where a failure is logged; <see cref="AppLog.Default"/> when null.</param>
    /// <returns>Whether a program was started (or, while disabled, would have been).</returns>
    public static bool OpenFile(string? path, AppLog? log = null)
    {
        log ??= AppLog.Default;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path)))
            {
                log.Warn("Cannot open " + path + ": it does not exist.");
                return false;
            }
            string full = Path.GetFullPath(path);
            if (Disabled)
            {
                Suppress("file " + full);
                return true;
            }
            var start = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo(full) { UseShellExecute = true }
                : new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false, CreateNoWindow = true };
            if (!OperatingSystem.IsWindows()) start.ArgumentList.Add(full);
            System.Diagnostics.Process? process = System.Diagnostics.Process.Start(start);
            if (process is null) return OperatingSystem.IsWindows();
            process.EnableRaisingEvents = true;
            process.Exited += (sender, _) => (sender as System.Diagnostics.Process)?.Dispose();
            if (process.HasExited) process.Dispose();
            return true;
        }
        catch (Exception exception)
        {
            log.Warn("Opening " + path + " failed.", exception);
            return false;
        }
    }

    /// <summary>Notes something that was not started because of <see cref="Disabled"/>.</summary>
    /// <param name="what">What it was.</param>
    internal static void Suppress(string what)
    {
        lock (s_gate)
        {
            if (s_suppressed.Count >= MaxSuppressed) s_suppressed.RemoveAt(0);
            s_suppressed.Add(what);
        }
    }

    /// <summary>Whether the process is a test runner (<c>testhost</c>, or Avalonia's headless platform is the one in use).</summary>
    private static bool RunsUnderTestHost()
    {
        try
        {
            string? entry = Assembly.GetEntryAssembly()?.GetName().Name;
            if (entry is not null && (entry.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) || entry.Contains("xunit", StringComparison.OrdinalIgnoreCase) || entry.Equals("ReSharperTestRunner", StringComparison.OrdinalIgnoreCase)))
                return true;
            return string.Equals(Environment.GetEnvironmentVariable("RC_NO_EXTERNAL_PROGRAMS"), "1", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
