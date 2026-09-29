namespace Lumen.Domain;

/// <summary>Supplies a GitHub token. Today: <c>gh auth token</c>. Later: OAuth Device Flow (TDD §39A).</summary>
public interface IGitHubTokenSource
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
}

public sealed record PullRequestInfo(
    PullRequestKey Key,
    PullRequestMetadata Metadata,
    string BaseSha,
    string HeadSha);

public sealed record PostedComment(long Id, string Url);

public sealed record NewReviewComment(
    PullRequestKey Key,
    string CommitSha,
    string Path,
    int Line,
    string Body);

public interface IGitHubClient
{
    Task<string> GetViewerLoginAsync(CancellationToken cancellationToken);

    Task<PullRequestInfo> GetPullRequestAsync(PullRequestKey key, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewThread>> GetReviewCommentsAsync(PullRequestKey key, CancellationToken cancellationToken);

    /// <summary>Posts a single-line review comment on the head (RIGHT) side of the diff.</summary>
    Task<PostedComment> PostReviewCommentAsync(NewReviewComment comment, CancellationToken cancellationToken);

    /// <summary>Paths the viewer has ticked "Viewed" on GitHub. A file changed since it was ticked is not included.</summary>
    Task<IReadOnlySet<string>> GetViewedFilesAsync(PullRequestKey key, CancellationToken cancellationToken);

    /// <summary>Ticks or clears GitHub's "Viewed" checkbox on one file for the viewer.</summary>
    Task SetFileViewedAsync(PullRequestKey key, string path, bool viewed, CancellationToken cancellationToken);
}

/// <summary>A local checkout of a pull request at its head commit.</summary>
public interface IPullRequestCheckout
{
    string RootPath { get; }

    string BaseSha { get; }

    string HeadSha { get; }

    string MergeBaseSha { get; }

    /// <summary>Unified diff between merge-base and head (what GitHub shows for the PR).</summary>
    Task<string> GetDiffAsync(CancellationToken cancellationToken);

    /// <summary>Reads a file at the merge-base commit; null when it did not exist.</summary>
    Task<string?> ReadBaseFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Reads a file at head from the working tree; null when absent.</summary>
    Task<string?> ReadHeadFileAsync(string path, CancellationToken cancellationToken);
}

public interface IRepositoryWorkspace
{
    /// <summary>Clones or updates the repository and returns a detached checkout at <paramref name="headSha"/>.</summary>
    Task<IPullRequestCheckout> CheckoutAsync(
        PullRequestKey key,
        string baseSha,
        string headSha,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

/// <summary>A throwaway worktree for an agent that may modify files (TDD §12). Removed on dispose.</summary>
public interface IAgentWorktree : IAsyncDisposable
{
    string Path { get; }
}

public interface IAgentWorktreeFactory
{
    /// <summary>A detached worktree of <paramref name="checkout"/> at its head, never the user's working tree.</summary>
    Task<IAgentWorktree> CreateAsync(IPullRequestCheckout checkout, string name, CancellationToken cancellationToken);
}

public enum ReviewAction
{
    Dismissed,
    Commented,
    Examined,
    Restored,

    /// <summary>A triage group the reviewer has cleared (recorded against <see cref="TriageAcknowledgement.InteractionId"/>).</summary>
    Acknowledged,
    Unacknowledged,
}

public sealed record ReviewInteraction(
    PullRequestKey Key,
    string HeadSha,
    string ReviewPointId,
    ReviewAction Action,
    DateTimeOffset At,
    string? Detail = null);

public interface IReviewStore
{
    Task RecordInteractionAsync(ReviewInteraction interaction, CancellationToken cancellationToken);

    /// <summary>Latest action per review point for a PR, across head SHAs (review point IDs are stable).</summary>
    Task<IReadOnlyDictionary<string, ReviewAction>> GetLatestActionsAsync(PullRequestKey key, CancellationToken cancellationToken);

    Task<string?> GetCacheAsync(string key, CancellationToken cancellationToken);

    Task SetCacheAsync(string key, string value, CancellationToken cancellationToken);
}
