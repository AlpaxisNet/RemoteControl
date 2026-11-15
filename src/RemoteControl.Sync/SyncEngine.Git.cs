using RemoteControl.Core;

namespace RemoteControl.Sync;

public sealed partial class SyncEngine
{
    /// <summary>"Initialize": <c>git init</c> in the workspace and the upstream URL as <c>origin</c>.</summary>
    /// <returns>The result.</returns>
    public Task<GitResult> GitInitializeAsync() => RunGitCommandAsync("Initialize", "Git repository initialized", "Git initialization failed", (git, token) => git.InitializeAsync(token));

    /// <summary>"Commit": adds the sync folders and the manifest and commits them as <c>[graal-sync] &lt;server&gt;</c>.</summary>
    /// <returns>The result.</returns>
    public Task<GitResult> GitCommitAsync() => RunGitCommandAsync("Commit", "Sync changes committed", "Git commit failed", (git, token) => git.CommitAsync(token));

    /// <summary>"Pull": <c>git pull --ff-only</c> from the upstream.</summary>
    /// <returns>The result.</returns>
    public Task<GitResult> GitPullAsync() => RunGitCommandAsync("Pull", "Git pull complete", "Git pull failed", async (git, token) =>
    {
        GitResult result = await git.PullAsync(token);
        if (result.Success) Dispatch(() => _fullRescan = true);
        return result;
    });

    /// <summary>"Push": <c>git push</c> (<c>-u origin HEAD</c> with an upstream URL).</summary>
    /// <returns>The result.</returns>
    public Task<GitResult> GitPushAsync() => RunGitCommandAsync("Push", "Git push complete", "Git push failed", (git, token) => git.PushAsync(token));

    /// <summary>Encrypts and stores an SSH private key for git (the "Credentials" page).</summary>
    /// <param name="keyText">The key file's content.</param>
    /// <returns><c>null</c> when stored, otherwise why not.</returns>
    public Task<string?> ImportGitKeyAsync(string keyText) => InvokeAsync(async () =>
    {
        if (_credentials is null) return "Connect to a server first.";
        string? error = await _credentials.StoreKeyAsync(keyText ?? string.Empty, _lifetime.Token);
        _hasGitKey = _credentials.HasKey;
        Log(error is null ? "Encrypted SSH key imported" : "SSH key import failed");
        Notify();
        return error;
    });

    /// <summary>Removes the stored SSH key.</summary>
    public void ClearGitKey() => Dispatch(() =>
    {
        if (_credentials is null) return;
        _credentials.ClearKey();
        _hasGitKey = _credentials.HasKey;
        Log("Stored SSH key removed");
    });

    /// <summary>Encrypts and stores an HTTPS access token for git.</summary>
    /// <param name="token">The token.</param>
    /// <returns><c>null</c> when stored, otherwise why not.</returns>
    public Task<string?> SaveGitTokenAsync(string token) => InvokeAsync(async () =>
    {
        if (_credentials is null) return "Connect to a server first.";
        string? error = await _credentials.StoreTokenAsync(token ?? string.Empty, _lifetime.Token);
        _hasGitToken = _credentials.HasToken;
        Log(error is null ? "Encrypted Git token saved" : "Git token save failed");
        Notify();
        return error;
    });

    /// <summary>Removes the stored token.</summary>
    public void ClearGitToken() => Dispatch(() =>
    {
        if (_credentials is null) return;
        _credentials.ClearToken();
        _hasGitToken = _credentials.HasToken;
        Log("Stored Git token removed");
    });

    private SyncGit CreateGit() => new(WorkspacePath() ?? string.Empty, _serverName, _credentials)
    {
        RemoteUrl = _settings.GitRemoteUrl,
        GitExecutable = _options.GitExecutable,
        Timeout = _options.GitTimeout,
        Environment = _options.GitEnvironment,
    };

    private Task<GitResult> RunGitCommandAsync(string operation, string success, string failure, Func<SyncGit, CancellationToken, Task<GitResult>> command) => InvokeAsync(async () =>
    {
        if (WorkspacePath() is null)
        {
            var missing = new GitResult(false, "Choose a sync workspace first.", -1);
            RaiseGit(operation, missing);
            return missing;
        }

        string workspace = WorkspacePathOrThrow();
        await Task.Run(() => LocalFiles.PrepareWorkspace(workspace));
        GitResult result = await command(CreateGit(), _lifetime.Token);
        Log(result.Success ? success : failure + ": " + FirstLine(result.Output));
        RaiseGit(operation, result);
        return result;
    });

    private void RaiseGit(string operation, GitResult result)
    {
        try
        {
            GitCompleted?.Invoke(this, new SyncGitEventArgs(operation, result));
        }
        catch (Exception exception)
        {
            ReportInternalError(exception);
        }
    }

    /// <summary>"Commit server downloads automatically" (and push): in the background, one at a time; a second request runs once afterwards.</summary>
    private void StartAutoCommit()
    {
        if (_autoCommitRunning)
        {
            _autoCommitAgain = true;
            return;
        }

        _autoCommitRunning = true;
        _ = AutoCommitAsync();
    }

    private async Task AutoCommitAsync()
    {
        try
        {
            do
            {
                _autoCommitAgain = false;
                SaveManifestNow();
                await _manifestSaveChain;
                SyncGit git = CreateGit();
                bool push = _settings.GitAutoPush;
                GitResult result = await git.CommitDownloadedAsync(push, _lifetime.Token);
                if (_disposed) return;
                if (result.Success) Log(push ? "Committed and pushed server downloads" : "Committed server downloads");
                else if (!result.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)) Log("Automatic Git commit failed: " + FirstLine(result.Output));
                RaiseGit(push ? "Commit and push" : "Commit", result);
            }
            while (_autoCommitAgain && !_disposed);
        }
        catch (Exception exception)
        {
            ReportInternalError(exception);
        }
        finally
        {
            _autoCommitRunning = false;
        }
    }
}
