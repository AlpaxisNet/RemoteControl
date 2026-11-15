using System.Globalization;
using System.Reflection;

namespace RemoteControl.Ui.Session;

/// <summary>The build date and version the title, the hello line and the debug information show (the C++ <c>REMOTE_CONTROL_BUILD_DATE</c>).</summary>
internal static class BuildInfo
{
    private static readonly Lazy<string> s_date = new(ReadDate);

    /// <summary>The build date as the C++ <c>__DATE__</c> wrote it, for example <c>Nov 15 2026</c>.</summary>
    public static string Date => s_date.Value;

    /// <summary>The program version.</summary>
    public static string Version
    {
        get
        {
            Assembly assembly = typeof(BuildInfo).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                int plus = informational.IndexOf('+', StringComparison.Ordinal);
                return plus > 0 ? informational[..plus] : informational;
            }
            return assembly.GetName().Version?.ToString() ?? "1.0.0";
        }
    }

    /// <summary>The window title of a session's main window (<c>remoteControlTitle</c>, <c>TRemoteFrame.cpp:312-317</c>).</summary>
    /// <param name="server">The server, or empty.</param>
    /// <param name="players">The player text (<c>Players: 12</c>), or empty.</param>
    /// <param name="syncPercent">The sync progress, or null.</param>
    /// <returns>The title.</returns>
    public static string Title(string? server, string? players, int? syncPercent)
    {
        string title = string.IsNullOrEmpty(server) ? "Remote Control" : server;
        if (!string.IsNullOrEmpty(players)) title += " [" + players + "]";
        if (syncPercent is { } percent) title += " [Sync: " + percent.ToString(CultureInfo.InvariantCulture) + "%]";
        return title + " - " + Date;
    }

    /// <summary>
    /// The date the program was built: written into the assembly when it is built (<c>BuildDate</c>, Directory.Build.props), or
    /// the date of the program's file when a build has none. The assembly's own path cannot be used: in the packaged program the
    /// assemblies are inside the executable and have no path.
    /// </summary>
    private static string ReadDate()
    {
        DateTime time = DateTime.Now;
        try
        {
            string? stamped = typeof(BuildInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "BuildDate")?.Value;
            if (DateTime.TryParseExact(stamped, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime built)) time = built;
            else if (Environment.ProcessPath is { Length: > 0 } program && File.Exists(program)) time = File.GetLastWriteTime(program);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The date of today is good enough.
        }
        return time.ToString("MMM d yyyy", CultureInfo.InvariantCulture);
    }
}
