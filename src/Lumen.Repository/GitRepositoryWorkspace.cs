using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Lumen.Domain;

namespace Lumen.Repository;

/// <summary>
/// Keeps a bare, blobless clone per repository under the cache root and hands out detached
/// worktrees per head commit.
/// </summary>
public sealed partial class GitRepositoryWorkspace : IRepositoryWorkspace
{
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromHours(1);

    private static readonly Dictionary<string, string> NoLazyFetch = new() { ["GIT_NO_LAZY_FETCH"] = "1" };

    private readonly RepositoryWorkspaceOptions options;
    private readonly GitRunner git = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.OrdinalIgnoreCase);

    public GitRepositoryWorkspace()
        : this(new RepositoryWorkspaceOptions())
    {
    }

    public GitRepositoryWorkspace(RepositoryWorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    public GitRepositoryWorkspace(IGitHubTokenSource tokenSource)
        : this(RepositoryWorkspaceOptions.FromTokenSource(tokenSource))
    {
    }

    public async Task<IPullRequestCheckout> CheckoutAsync(
        PullRequestKey key,
        string baseSha,
        string headSha,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var repo = key.Repository;
        ValidateName(repo.Owner, nameof(key));
        ValidateName(repo.Name, nameof(key));
        ValidateSha(baseSha, nameof(baseSha));
        ValidateSha(headSha, nameof(headSha));
        baseSha = baseSha.ToLowerInvariant();
        headSha = headSha.ToLowerInvariant();

        var gate = locks.GetOrAdd(repo.FullName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var gitDir = Path.Combine(options.CacheRoot, "repos", repo.Owner, repo.Name + ".git");
            await EnsureCloneAsync(repo, gitDir, progress, cancellationToken).ConfigureAwait(false);
            await EnsureCommitsAsync(repo, gitDir, key.Number, baseSha, headSha, progress, cancellationToken).ConfigureAwait(false);

            var mergeBase = (await git.RunAsync(gitDir, ["merge-base", baseSha, headSha], cancellationToken).ConfigureAwait(false))
                .StandardOutput.Trim();

            var worktree = Path.Combine(options.CacheRoot, "worktrees", $"{repo.Owner}-{repo.Name}", headSha[..12]);
            await EnsureWorktreeAsync(gitDir, worktree, headSha, progress, cancellationToken).ConfigureAwait(false);

            return new GitPullRequestCheckout(git, gitDir, worktree, baseSha, headSha, mergeBase, GetEnvironmentAsync);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EnsureCloneAsync(RepositoryRef repo, string gitDir, IProgress<string>? progress, CancellationToken ct)
    {
        if (Directory.Exists(gitDir))
        {
            var check = await git.RunAsync(gitDir, ["rev-parse", "--is-bare-repository"], ct, throwOnError: false).ConfigureAwait(false);
            if (check.Succeeded && check.StandardOutput.Trim() == "true")
            {
                return;
            }

            DeleteDirectory(gitDir);
        }

        progress?.Report($"Cloning {repo.FullName}…");
        var parent = Path.GetDirectoryName(gitDir)!;
        Directory.CreateDirectory(parent);
        var staging = gitDir + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        var url = options.RemoteUrl(repo);
        var env = await GetEnvironmentAsync(ct).ConfigureAwait(false);

        try
        {
            var clone = await git.RunAsync(
                parent,
                ["clone", "--bare", "--filter=blob:none", "--", url, staging],
                ct,
                env,
                CloneTimeout,
                throwOnError: false).ConfigureAwait(false);

            if (!clone.Succeeded && clone.StandardError.Contains("filter", StringComparison.OrdinalIgnoreCase))
            {
                DeleteDirectory(staging);
                clone = await git.RunAsync(parent, ["clone", "--bare", "--", url, staging], ct, env, CloneTimeout, throwOnError: false)
                    .ConfigureAwait(false);
            }

            if (!clone.Succeeded)
            {
                throw new GitCommandException($"git clone --bare {url}", clone.ExitCode, clone.StandardError);
            }

            await git.RunAsync(staging, ["config", "core.autocrlf", "false"], ct).ConfigureAwait(false);
            await git.RunAsync(staging, ["config", "core.longpaths", "true"], ct).ConfigureAwait(false);
            await git.RunAsync(staging, ["config", "gc.auto", "0"], ct).ConfigureAwait(false);
            Directory.Move(staging, gitDir);
        }
        finally
        {
            DeleteDirectory(staging);
        }
    }

    private async Task EnsureCommitsAsync(
        RepositoryRef repo,
        string gitDir,
        int number,
        string baseSha,
        string headSha,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (await HasCommitAsync(gitDir, baseSha, ct).ConfigureAwait(false)
            && await HasCommitAsync(gitDir, headSha, ct).ConfigureAwait(false))
        {
            return;
        }

        progress?.Report("Fetching pull request…");
        var env = await GetEnvironmentAsync(ct).ConfigureAwait(false);
        var prefix = $"refs/lumen/pr/{number}";

        // The partial-clone filter is inherited from remote.origin.partialclonefilter; passing
        // --filter explicitly fails when the clone fell back to a full (unfiltered) clone.
        await TryFetchAsync(gitDir, $"+refs/pull/{number}/head:{prefix}/head", env, ct).ConfigureAwait(false);

        if (!await HasCommitAsync(gitDir, headSha, ct).ConfigureAwait(false))
        {
            await TryFetchAsync(gitDir, $"+{headSha}:{prefix}/head-{headSha[..12]}", env, ct).ConfigureAwait(false);
        }

        if (!await HasCommitAsync(gitDir, baseSha, ct).ConfigureAwait(false)
            && !await TryFetchAsync(gitDir, $"+{baseSha}:{prefix}/base", env, ct).ConfigureAwait(false))
        {
            await TryFetchAsync(gitDir, "+refs/heads/*:refs/remotes/origin/*", env, ct).ConfigureAwait(false);
        }

        foreach (var sha in new[] { headSha, baseSha })
        {
            if (!await HasCommitAsync(gitDir, sha, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Commit {sha} of {repo.FullName}#{number} could not be fetched from origin.");
            }
        }
    }

    private async Task<bool> TryFetchAsync(string gitDir, string refspec, IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        var result = await git.RunAsync(
            gitDir,
            ["fetch", "--no-tags", "--no-write-fetch-head", "origin", refspec],
            ct,
            env,
            CloneTimeout,
            throwOnError: false).ConfigureAwait(false);
        return result.Succeeded;
    }

    private async Task<bool> HasCommitAsync(string gitDir, string sha, CancellationToken ct)
    {
        // Without this a partial clone silently lazy-fetches the missing commit (and later its
        // ancestors, one round trip each) from the promisor remote.
        var result = await git.RunAsync(gitDir, ["cat-file", "-e", sha + "^{commit}"], ct, NoLazyFetch, throwOnError: false)
            .ConfigureAwait(false);
        return result.Succeeded;
    }

    private async Task EnsureWorktreeAsync(string gitDir, string worktree, string headSha, IProgress<string>? progress, CancellationToken ct)
    {
        if (Directory.Exists(worktree))
        {
            var head = await git.RunAsync(worktree, ["rev-parse", "HEAD"], ct, throwOnError: false).ConfigureAwait(false);
            var top = await git.RunAsync(worktree, ["rev-parse", "--show-toplevel"], ct, throwOnError: false).ConfigureAwait(false);
            if (head.Succeeded
                && top.Succeeded
                && string.Equals(head.StandardOutput.Trim(), headSha, StringComparison.OrdinalIgnoreCase)
                && SamePath(top.StandardOutput.Trim(), worktree))
            {
                return;
            }

            DeleteDirectory(worktree);
        }

        progress?.Report("Checking out head…");
        await git.RunAsync(gitDir, ["worktree", "prune"], ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
        var env = await GetEnvironmentAsync(ct).ConfigureAwait(false);
        try
        {
            await git.RunAsync(gitDir, ["worktree", "add", "--detach", "--", worktree, headSha], ct, env, CloneTimeout)
                .ConfigureAwait(false);
        }
        catch
        {
            DeleteDirectory(worktree);
            await git.RunAsync(gitDir, ["worktree", "prune"], CancellationToken.None, throwOnError: false).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>?> GetEnvironmentAsync(CancellationToken ct)
    {
        if (options.TokenProvider is null)
        {
            return null;
        }

        var token = await options.TokenProvider(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token.Trim()));
        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader",
            ["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: basic " + credentials,
        };
    }

    private static bool SamePath(string gitPath, string path) =>
        string.Equals(
            Path.GetFullPath(gitPath).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private static void ValidateName(string value, string paramName)
    {
        if (!NamePattern().IsMatch(value) || value is "." or "..")
        {
            throw new ArgumentException($"Invalid repository component '{value}'.", paramName);
        }
    }

    private static void ValidateSha(string value, string paramName)
    {
        if (value is null || !ShaPattern().IsMatch(value))
        {
            throw new ArgumentException($"'{value}' is not a full commit SHA.", paramName);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^([0-9a-fA-F]{40}|[0-9a-fA-F]{64})$")]
    private static partial Regex ShaPattern();
}
