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

/// <summary>What GitHub last reported about the viewer's request budget.</summary>
public sealed record GitHubRateLimit(int Remaining, DateTimeOffset ResetAt);

public interface IGitHubClient
{
    /// <summary>The most recent rate-limit headers (REST) or <c>rateLimit</c> (GraphQL); null before any request.</summary>
    GitHubRateLimit? RateLimit => null;

    Task<string> GetViewerLoginAsync(CancellationToken cancellationToken);

    Task<PullRequestInfo> GetPullRequestAsync(PullRequestKey key, CancellationToken cancellationToken);

    /// <summary>Review threads with their comments, and the viewer's pending review if there is one.</summary>
    Task<ReviewThreadsResult> GetReviewThreadsAsync(PullRequestKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Posts a review comment at once (not part of a review). Fails with <c>PendingReviewExistsException</c> when the
    /// viewer has a pending review, because GitHub refuses single comments then.
    /// </summary>
    Task<PostedComment> PostReviewCommentAsync(NewReviewComment comment, CancellationToken cancellationToken);

    /// <summary>Paths the viewer has ticked "Viewed" on GitHub. A file changed since it was ticked is not included.</summary>
    Task<IReadOnlySet<string>> GetViewedFilesAsync(PullRequestKey key, CancellationToken cancellationToken);

    /// <summary>Ticks or clears GitHub's "Viewed" checkbox on one file for the viewer.</summary>
    Task SetFileViewedAsync(PullRequestKey key, string path, bool viewed, CancellationToken cancellationToken);

    /// <summary>Runs several pull request searches in one round trip (the inbox).</summary>
    Task<InboxSearchResult> SearchInboxAsync(IReadOnlyList<InboxQuery> queries, CancellationToken cancellationToken);

    Task<PullRequestDetails> GetPullRequestDetailsAsync(PullRequestKey key, CancellationToken cancellationToken);

    /// <summary>A top-level comment on the conversation.</summary>
    Task<TimelineItem> PostIssueCommentAsync(PullRequestKey key, string body, CancellationToken cancellationToken);

    /// <summary>Replies to a thread; inside <paramref name="pendingReviewId"/> when given, otherwise at once.</summary>
    Task<ThreadComment> ReplyToThreadAsync(string threadId, string body, string? pendingReviewId, CancellationToken cancellationToken);

    Task SetThreadResolvedAsync(string threadId, bool resolved, CancellationToken cancellationToken);

    /// <summary>Starts a pending review at <paramref name="commitSha"/> and returns its id.</summary>
    Task<string> StartPendingReviewAsync(PullRequestKey key, string commitSha, CancellationToken cancellationToken);

    /// <summary>Adds a new thread to a pending review; returns the new comment's id.</summary>
    Task<string> AddPendingThreadAsync(string reviewId, NewReviewComment comment, CancellationToken cancellationToken);

    Task UpdatePendingCommentAsync(string commentId, string body, CancellationToken cancellationToken);

    Task DeletePendingCommentAsync(string commentId, CancellationToken cancellationToken);

    Task<SubmittedReview> SubmitReviewAsync(string reviewId, ReviewEvent reviewEvent, string body, CancellationToken cancellationToken);
}

/// <summary>GitHub refused a single comment because the viewer has a pending review on the pull request.</summary>
public sealed class PendingReviewExistsException : Exception
{
    public PendingReviewExistsException()
        : base("You have a pending review on this pull request.")
    {
    }

    public PendingReviewExistsException(string message)
        : base(message)
    {
    }

    public PendingReviewExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
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

    /// <summary>
    /// Unified diff between two commits of the pull request (a commit, a range, "since last review"), optionally
    /// ignoring whitespace and limited to <paramref name="path"/>. When <paramref name="fromSha"/> is not an ancestor
    /// of <paramref name="toSha"/> (a force-push) the file contents are compared directly, which git does anyway.
    /// </summary>
    Task<string> GetRangeDiffAsync(string fromSha, string toSha, bool ignoreWhitespace, string? path, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This checkout can only diff the whole pull request.");

    /// <summary>True when <paramref name="ancestor"/> is reachable from <paramref name="descendant"/>.</summary>
    Task<bool> IsAncestorAsync(string ancestor, string descendant, CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>Makes sure a commit is available locally (fetching it by SHA if needed); false if GitHub no longer has it.</summary>
    Task<bool> EnsureCommitAsync(string sha, CancellationToken cancellationToken) => Task.FromResult(true);
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
