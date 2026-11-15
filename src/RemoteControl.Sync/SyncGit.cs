using System.Collections.Concurrent;
using RemoteControl.Core;

namespace RemoteControl.Sync;

/// <summary>
/// The Git backups of a sync workspace (the "Git" page, <c>runGit</c> and its callers, <c>TSyncManager.cpp:1763-1953</c>), through
/// <see cref="GitBackup"/>: every command runs on a background task with a time limit, one at a time per workspace. The stored SSH key is
/// written for the duration of one command to <c>.graal-sync/.git-ssh-key</c> (mode 0600) and passed with
/// <c>GIT_SSH_COMMAND=ssh -i "…" -o IdentitiesOnly=yes -o BatchMode=yes</c>; the stored token is given to git through an askpass helper
/// (<c>.graal-sync/.git-askpass.sh</c> or <c>.cmd</c>) that reads <c>GSCRIPTRC_GIT_TOKEN</c>; both files are removed afterwards. Outputs
/// never contain the token.
/// </summary>
public sealed class SyncGit
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>Creates the runner.</summary>
    /// <param name="workspace">The workspace (the repository).</param>
    /// <param name="serverName">The server name (commit messages).</param>
    /// <param name="credentials">The stored credentials, or <c>null</c> for none.</param>
    public SyncGit(string workspace, string serverName, GitCredentialStore? credentials)
    {
        Workspace = workspace ?? string.Empty;
        ServerName = serverName ?? string.Empty;
        Credentials = credentials;
    }

    /// <summary>The workspace.</summary>
    public string Workspace { get; }

    /// <summary>The server name.</summary>
    public string ServerName { get; }

    /// <summary>The stored credentials.</summary>
    public GitCredentialStore? Credentials { get; }

    /// <summary>The upstream URL (<c>[git] remoteUrl</c>).</summary>
    public string RemoteUrl { get; init; } = string.Empty;

    /// <summary>The git program; found on the <c>PATH</c> when <c>null</c>.</summary>
    public string? GitExecutable { get; init; }

    /// <summary>How long one command may run.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Extra environment variables (an identity in tests).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>"Initialize": <c>git init</c>, then the upstream.</summary>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result.</returns>
    public Task<GitResult> InitializeAsync(CancellationToken cancellationToken = default) => RunAsync(git => git.InitializeAsync(cancellationToken), cancellationToken);

    /// <summary>"Commit": <c>git add</c> of the sync folders and the manifest, then <c>git commit -m "[graal-sync] &lt;server&gt;"</c>.</summary>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result.</returns>
    public Task<GitResult> CommitAsync(CancellationToken cancellationToken = default) => RunAsync(git => git.CommitAsync(ServerName, cancellationToken), cancellationToken);

    /// <summary>"Pull": the upstream, then <c>git pull --ff-only</c>.</summary>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result.</returns>
    public Task<GitResult> PullAsync(CancellationToken cancellationToken = default) => RunAsync(git => git.PullAsync(cancellationToken), cancellationToken);

    /// <summary>"Push": the upstream, then <c>git push</c> (<c>-u origin HEAD</c> with an upstream URL).</summary>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result.</returns>
    public Task<GitResult> PushAsync(CancellationToken cancellationToken = default) => RunAsync(git => git.PushAsync(cancellationToken), cancellationToken);

    /// <summary>The automatic commit after downloads, then a push when <paramref name="push"/> (<c>gitCommitDownloaded</c>).</summary>
    /// <param name="push">"Push after automatic commits".</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result.</returns>
    public Task<GitResult> CommitDownloadedAsync(bool push, CancellationToken cancellationToken = default) =>
        RunAsync(git => git.CommitDownloadedAsync(ServerName, push, cancellationToken), cancellationToken);

    /// <summary>"Pull before local uploads": <c>git pull --ff-only</c> without changing the upstream (as the C++ did).</summary>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result.</returns>
    public Task<GitResult> PullBeforeUploadAsync(CancellationToken cancellationToken = default) => RunAsync(git => git.RunAsync(["pull", "--ff-only"], cancellationToken), cancellationToken);

    /// <summary>Runs any git command with the stored credentials.</summary>
    /// <param name="arguments">The arguments after <c>git -C &lt;workspace&gt;</c>.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The result.</returns>
    public Task<GitResult> RunCommandAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
        RunAsync(git => git.RunAsync(arguments, cancellationToken), cancellationToken);

    private async Task<GitResult> RunAsync(Func<GitBackup, Task<GitResult>> command, CancellationToken cancellationToken)
    {
        if (Workspace.Length == 0) return new GitResult(false, "Choose a sync workspace first.", -1);
        SemaphoreSlim gate = Gates.GetOrAdd(Path.GetFullPath(Workspace), _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new GitResult(false, "Git was cancelled.", -1);
        }

        string? keyFile = null;
        string? askPass = null;
        string? token = null;
        try
        {
            var environment = new Dictionary<string, string>(Environment, StringComparer.Ordinal);
            string metadata = Path.Combine(Workspace, SyncNames.MetadataFolder);
            if (Credentials is not null)
            {
                string? key = await Credentials.LoadKeyAsync(cancellationToken).ConfigureAwait(false);
                if (key is not null)
                {
                    keyFile = Path.Combine(metadata, ".git-ssh-key");
                    try
                    {
                        await Task.Run(() => AtomicFile.WriteAllText(keyFile, key, userOnly: true), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        return new GitResult(false, "Unable to prepare the stored SSH key.", -1);
                    }

                    environment["GIT_SSH_COMMAND"] = "ssh -i \"" + keyFile.Replace('\\', '/') + "\" -o IdentitiesOnly=yes -o BatchMode=yes";
                }

                token = await Credentials.LoadTokenAsync(cancellationToken).ConfigureAwait(false);
                if (token is not null)
                {
                    bool windows = OperatingSystem.IsWindows();
                    askPass = Path.Combine(metadata, windows ? ".git-askpass.cmd" : ".git-askpass.sh");
                    string helper = windows
                        ? "@echo off\r\nset \"prompt=%~1\"\r\nif /I \"%prompt:~0,8%\"==\"Username\" (echo x-access-token) else (echo %GSCRIPTRC_GIT_TOKEN%)\r\n"
                        : "#!/bin/sh\ncase \"$1\" in *Username*) printf '%s\\n' x-access-token;; *) printf '%s\\n' \"$GSCRIPTRC_GIT_TOKEN\";; esac\n";
                    try
                    {
                        await Task.Run(() =>
                        {
                            AtomicFile.WriteAllText(askPass, helper, userOnly: true);
                            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(askPass, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        return new GitResult(false, "Unable to prepare Git token authentication.", -1);
                    }

                    environment["GIT_ASKPASS"] = askPass;
                    environment["GIT_TERMINAL_PROMPT"] = "0";
                    environment["GSCRIPTRC_GIT_TOKEN"] = token;
                }
            }

            var git = new GitBackup(Workspace, GitExecutable) { RemoteUrl = RemoteUrl, Timeout = Timeout, Environment = environment };
            GitResult result = await command(git).ConfigureAwait(false);
            return token is { Length: > 0 } && result.Output.Contains(token, StringComparison.Ordinal) ? result with { Output = result.Output.Replace(token, "***", StringComparison.Ordinal) } : result;
        }
        catch (OperationCanceledException)
        {
            return new GitResult(false, "Git was cancelled.", -1);
        }
        finally
        {
            TryDelete(keyFile);
            TryDelete(askPass);
            gate.Release();
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Default.Warn($"Temporary Git helper {Path.GetFileName(path)} could not be removed: {exception.Message}");
        }
    }
}
