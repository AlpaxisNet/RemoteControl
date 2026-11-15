using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RemoteControl.Core;

/// <summary>The outcome of one git command.</summary>
/// <param name="Success">Whether git ran and exited with 0.</param>
/// <param name="Output">Standard output, then standard error, as the C++ joined them; or the reason git did not run.</param>
/// <param name="ExitCode">The exit code, or -1 when git did not run or was stopped.</param>
/// <param name="TimedOut">Whether git was stopped because it took longer than <see cref="GitBackup.Timeout"/>.</param>
public sealed record GitResult(bool Success, string Output, int ExitCode, bool TimedOut = false);

/// <summary>
/// The Git backups of the sync workspace (<c>TSyncManager::runGit</c> and its callers): the same commands, run as
/// <c>git -C &lt;workspace&gt; …</c> on a background task with a time limit. Never throws for a missing git or a failing command.
/// </summary>
/// <remarks>
/// Differences from the C++: asynchronous with a timeout (the C++ blocked the UI thread with <c>g_spawn_sync</c>);
/// <c>GIT_TERMINAL_PROMPT=0</c> is always set so git can never wait for a password on a terminal nobody sees; the stored SSH key
/// and access token of the sync manager are not handled here: pass what they need (for example <c>GIT_SSH_COMMAND</c> or
/// <c>GIT_ASKPASS</c>) in <see cref="Environment"/>.
/// </remarks>
public sealed class GitBackup
{
    /// <summary>The paths <see cref="CommitAsync"/> adds, as the C++ did.</summary>
    public static IReadOnlyList<string> SyncPaths { get; } = ["weapons", "classes", "npcs", "files", "config", ".graal-sync/manifest.conf"];

    /// <summary>Creates a runner for one workspace.</summary>
    /// <param name="workspace">The sync workspace (the repository folder).</param>
    /// <param name="gitExecutable">The git program; found with <see cref="FindGit"/> when <c>null</c>.</param>
    public GitBackup(string workspace, string? gitExecutable = null)
    {
        Workspace = workspace ?? string.Empty;
        GitExecutable = gitExecutable ?? FindGit(AppContext.BaseDirectory);
    }

    /// <summary>The sync workspace.</summary>
    public string Workspace { get; }

    /// <summary>The git program, or <c>null</c> when none was found.</summary>
    public string? GitExecutable { get; }

    /// <summary>The upstream URL configured as <c>origin</c> before pull and push; empty for none.</summary>
    public string RemoteUrl { get; init; } = string.Empty;

    /// <summary>How long one git command may run before it is stopped.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Extra environment variables for git (credentials helpers, identity in tests).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Finds git like the C++: on the <c>PATH</c>; on Windows also beside the program, in <c>tools\git\cmd</c> and the usual Git for
    /// Windows folders; elsewhere <c>/usr/bin/git</c> and <c>/usr/local/bin/git</c>.
    /// </summary>
    /// <param name="programDirectory">The folder of the executable.</param>
    /// <returns>The path, or <c>null</c>.</returns>
    public static string? FindGit(string? programDirectory)
    {
        try
        {
            string name = OperatingSystem.IsWindows() ? "git.exe" : "git";
            foreach (string folder in (System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(folder.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            var candidates = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                if (!string.IsNullOrEmpty(programDirectory))
                {
                    candidates.Add(Path.Combine(programDirectory, "git.exe"));
                    candidates.Add(Path.Combine(programDirectory, "tools", "git", "cmd", "git.exe"));
                }
                foreach (string variable in new[] { "ProgramFiles", "ProgramFiles(x86)" })
                {
                    string? root = System.Environment.GetEnvironmentVariable(variable);
                    if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "Git", "cmd", "git.exe"));
                }
                string? local = System.Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (!string.IsNullOrEmpty(local)) candidates.Add(Path.Combine(local, "Programs", "Git", "cmd", "git.exe"));
            }
            else
            {
                candidates.Add("/usr/bin/git");
                candidates.Add("/usr/local/bin/git");
            }
            return candidates.FirstOrDefault(File.Exists);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Runs <c>git -C &lt;workspace&gt; &lt;arguments&gt;</c>.</summary>
    /// <param name="arguments">The git arguments.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result.</returns>
    public async Task<GitResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(Workspace)) return new GitResult(false, "Choose a sync workspace first.", -1);
        if (string.IsNullOrEmpty(GitExecutable)) return new GitResult(false, "Git executable was not found.", -1);
        var start = new ProcessStartInfo(GitExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(Workspace);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (KeyValuePair<string, string> variable in Environment) start.Environment[variable.Key] = variable.Value;

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new GitResult(false, "Git could not be started.", -1);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new GitResult(false, "Git executable was not found.", -1);
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
            }
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        string text = await Collect(output).ConfigureAwait(false);
        string errorText = await Collect(error).ConfigureAwait(false);
        if (errorText.Length != 0) text = text.Length == 0 ? errorText : text + "\n" + errorText;
        if (timedOut) return new GitResult(false, (text.Length == 0 ? string.Empty : text + "\n") + $"Git did not finish within {Timeout.TotalSeconds:0} seconds and was stopped.", -1, true);
        if (cancellationToken.IsCancellationRequested) return new GitResult(false, (text.Length == 0 ? string.Empty : text + "\n") + "Git was cancelled.", -1);
        int exitCode = process.ExitCode;
        return new GitResult(exitCode == 0, text, exitCode);
    }

    /// <summary><c>git init</c>, then the upstream (Git button "Initialize").</summary>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result.</returns>
    public async Task<GitResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        GitResult init = await RunAsync(["init"], cancellationToken).ConfigureAwait(false);
        if (!init.Success) return init;
        GitResult remote = await ConfigureRemoteAsync(cancellationToken).ConfigureAwait(false);
        return remote.Success ? init : remote;
    }

    /// <summary><c>git add -- weapons classes npcs files config .graal-sync/manifest.conf</c>, then <c>git commit -m "[graal-sync] &lt;server&gt;"</c>.</summary>
    /// <param name="serverName">The server name in the commit message.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result of the failing or the last command.</returns>
    public async Task<GitResult> CommitAsync(string serverName, CancellationToken cancellationToken = default)
    {
        var add = new List<string> { "add", "--" };
        add.AddRange(SyncPaths);
        GitResult added = await RunAsync(add, cancellationToken).ConfigureAwait(false);
        if (!added.Success) return added;
        return await RunAsync(["commit", "-m", "[graal-sync] " + serverName], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The upstream, then <c>git pull --ff-only</c>.</summary>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result.</returns>
    public async Task<GitResult> PullAsync(CancellationToken cancellationToken = default)
    {
        GitResult remote = await ConfigureRemoteAsync(cancellationToken).ConfigureAwait(false);
        if (!remote.Success) return remote;
        return await RunAsync(["pull", "--ff-only"], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The upstream, then <c>git push</c> (or <c>git push -u origin HEAD</c> when <see cref="RemoteUrl"/> is set).</summary>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result.</returns>
    public async Task<GitResult> PushAsync(CancellationToken cancellationToken = default)
    {
        GitResult remote = await ConfigureRemoteAsync(cancellationToken).ConfigureAwait(false);
        if (!remote.Success) return remote;
        string[] arguments = RemoteUrl.Length == 0 ? ["push"] : ["push", "-u", "origin", "HEAD"];
        return await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>After a download: commit, then push when <paramref name="autoPush"/> (<c>gitCommitDownloaded</c>).</summary>
    /// <param name="serverName">The server name in the commit message.</param>
    /// <param name="autoPush">Push after a successful commit.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result of the failing or the last command.</returns>
    public async Task<GitResult> CommitDownloadedAsync(string serverName, bool autoPush, CancellationToken cancellationToken = default)
    {
        GitResult commit = await CommitAsync(serverName, cancellationToken).ConfigureAwait(false);
        if (!commit.Success || !autoPush) return commit;
        return await PushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets <c>origin</c> to <see cref="RemoteUrl"/> (<c>remote set-url</c> when it exists, else <c>remote add</c>); nothing to do when it
    /// is empty. A URL with credentials (<c>scheme://user@host</c>) is refused.
    /// </summary>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result.</returns>
    public async Task<GitResult> ConfigureRemoteAsync(CancellationToken cancellationToken = default)
    {
        if (RemoteUrl.Length == 0) return new GitResult(true, string.Empty, 0);
        int scheme = RemoteUrl.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            int at = RemoteUrl.IndexOf('@', scheme + 3);
            int slash = RemoteUrl.IndexOf('/', scheme + 3);
            if (at >= 0 && (slash < 0 || at < slash)) return new GitResult(false, "Credentials must not be embedded in the upstream URL.", -1);
        }
        GitResult current = await RunAsync(["remote", "get-url", "origin"], cancellationToken).ConfigureAwait(false);
        if (current.Success) return await RunAsync(["remote", "set-url", "origin", RemoteUrl], cancellationToken).ConfigureAwait(false);
        if (current.TimedOut) return current;
        return await RunAsync(["remote", "add", "origin", RemoteUrl], cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> Collect(Task<string> reader)
    {
        try
        {
            return (await reader.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).TrimEnd('\n', '\r');
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
