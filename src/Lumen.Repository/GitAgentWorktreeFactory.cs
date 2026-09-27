using Lumen.Domain;

namespace Lumen.Repository;

/// <summary>
/// Detached, throwaway worktrees for mutation-capable agents under <c>{dataDir}/agents</c> (TDD §12). They share the
/// review checkout's object store, never touch the user's own working tree, and are removed when disposed.
/// </summary>
public sealed class GitAgentWorktreeFactory(string root) : IAgentWorktreeFactory
{
    private readonly GitRunner _git = new();

    public string Root { get; } = Path.GetFullPath(root);

    public async Task<IAgentWorktree> CreateAsync(IPullRequestCheckout checkout, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkout);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\', '.']) >= 0)
        {
            throw new ArgumentException($"Invalid agent worktree name '{name}'.", nameof(name));
        }

        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Root);
        await _git.RunAsync(checkout.RootPath, ["worktree", "add", "--detach", "--", path, checkout.HeadSha], cancellationToken).ConfigureAwait(false);
        return new Worktree(_git, checkout.RootPath, path);
    }

    private sealed class Worktree(GitRunner git, string owner, string path) : IAgentWorktree
    {
        public string Path { get; } = path;

        public async ValueTask DisposeAsync()
        {
            await git.RunAsync(owner, ["worktree", "remove", "--force", "--", Path], CancellationToken.None, throwOnError: false).ConfigureAwait(false);
            if (Directory.Exists(Path))
            {
                GitRepositoryWorkspace.DeleteDirectory(Path);
            }

            await git.RunAsync(owner, ["worktree", "prune"], CancellationToken.None, throwOnError: false).ConfigureAwait(false);
        }
    }
}
