using System.Collections.Immutable;
using System.Diagnostics;
using RemoteControl.Core;

namespace RemoteControl.Ui.Common;

/// <summary>
/// Opens links from the chat and from windows. Only absolute <c>http</c>, <c>https</c> and <c>mailto</c> URLs are opened; anything
/// else (a file, a script, a custom scheme a server could send) is refused and logged. The browsers of the option <c>webbrowsers</c>
/// are tried in order when it is set to something other than its old default (<c>firefox,mozilla,konqueror,netscape</c>, which the
/// C++ never used and which is kept in old option files); otherwise, or when none of them is installed, the system default opens it
/// (<c>xdg-open</c> on Linux, <c>open</c> on macOS, the shell on Windows). Never throws; never blocks.
/// </summary>
/// <example>
/// <code>
/// chat.LinkClicked += (_, e) =&gt; services.Urls.Open(e.Url);
/// </code>
/// </example>
public sealed class UrlOpener
{
    private readonly OptionsStore? _options;
    private readonly AppLog _log;
    private readonly Func<string, IReadOnlyList<string>, bool> _launch;

    /// <summary>Creates an opener.</summary>
    /// <param name="options">The options (for <c>webbrowsers</c>), or <c>null</c> to always use the system default.</param>
    /// <param name="log">Where refusals and failures are logged; <see cref="AppLog.Default"/> when <c>null</c>.</param>
    public UrlOpener(OptionsStore? options, AppLog? log = null) : this(options, log, null)
    {
    }

    /// <summary>Creates an opener with another way to start processes (tests): it receives the program and its arguments and returns whether it started.</summary>
    internal UrlOpener(OptionsStore? options, AppLog? log, Func<string, IReadOnlyList<string>, bool>? launch)
    {
        _options = options;
        _log = log ?? AppLog.Default;
        _launch = launch ?? Launch;
    }

    /// <summary>Whether <paramref name="url"/> is an absolute <c>http</c>, <c>https</c> or <c>mailto</c> URL without control characters.</summary>
    /// <param name="url">The URL.</param>
    /// <returns><c>true</c> when it may be opened.</returns>
    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 8192) return false;
        foreach (char character in url)
            if (char.IsControl(character)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)) return false;
        return uri.Scheme is "http" or "https" or "mailto" && (uri.Scheme == "mailto" || !string.IsNullOrEmpty(uri.Host));
    }

    /// <summary>Opens a URL in a browser (or mail program).</summary>
    /// <param name="url">The URL; <c>www.</c> links of the chat already carry <c>https://</c>.</param>
    /// <returns>Whether a program was started.</returns>
    public bool Open(string? url)
    {
        try
        {
            if (!IsAllowed(url))
            {
                _log.Warn("Refused to open a link that is not http, https or mailto: " + Shorten(url));
                return false;
            }
            string target = new Uri(url!.Trim(), UriKind.Absolute).AbsoluteUri;
            bool mail = target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
            if (!mail)
            {
                foreach (string browser in ConfiguredBrowsers())
                {
                    string? program = FindProgram(browser);
                    if (program is not null && _launch(program, [target])) return true;
                }
            }
            if (OperatingSystem.IsWindows()) return _launch(target, Array.Empty<string>());
            if (OperatingSystem.IsMacOS()) return _launch("open", [target]);
            return _launch(FindProgram("xdg-open") ?? "xdg-open", [target]);
        }
        catch (Exception exception)
        {
            _log.Warn("The link could not be opened: " + Shorten(url), exception);
            return false;
        }
    }

    /// <summary>The browsers to try first: the option <c>webbrowsers</c>, unless it is empty or its old default.</summary>
    /// <returns>The browser commands.</returns>
    public IReadOnlyList<string> ConfiguredBrowsers()
    {
        ImmutableArray<string> browsers = _options?.Current.WebBrowsers ?? ImmutableArray<string>.Empty;
        if (browsers.IsDefaultOrEmpty || browsers.SequenceEqual(RcOptions.Defaults.WebBrowsers, StringComparer.OrdinalIgnoreCase)) return Array.Empty<string>();
        return browsers.Select(browser => browser.Trim()).Where(browser => browser.Length > 0).ToArray();
    }

    private bool Launch(string program, IReadOnlyList<string> arguments)
    {
        if (ExternalPrograms.Disabled)
        {
            // A test run (or a switched-off program): remember what would have been opened, start nothing.
            ExternalPrograms.Suppress("url " + (arguments.Count > 0 ? arguments[^1] : program));
            return true;
        }
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = OperatingSystem.IsWindows() && arguments.Count == 0, CreateNoWindow = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            Process? process = Process.Start(start);
            if (process is null) return OperatingSystem.IsWindows();
            process.EnableRaisingEvents = true;
            process.Exited += (sender, _) => (sender as Process)?.Dispose();
            if (process.HasExited) process.Dispose();
            return true;
        }
        catch (Exception exception)
        {
            _log.Warn("Starting " + program + " failed.", exception);
            return false;
        }
    }

    private static string? FindProgram(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
            string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", string.Empty] : [string.Empty];
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                foreach (string extension in extensions)
                {
                    string candidate = Path.Combine(folder, name + extension);
                    if (File.Exists(candidate)) return candidate;
                }
        }
        catch (Exception)
        {
            // A malformed PATH is treated as "not found".
        }
        return null;
    }

    private static string Shorten(string? url) => url is null ? "(null)" : url.Length > 200 ? url[..200] + "..." : url;
}
