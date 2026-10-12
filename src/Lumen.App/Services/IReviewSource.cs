using Lumen.Contracts;

namespace Lumen.App.Services;

/// <summary>Everything the UI needs from the review engine. Backed by gRPC in the app, by fixtures in tests.</summary>
public interface IReviewSource : IAsyncDisposable
{
    /// <summary>Human-readable description of where data comes from, e.g. "Local engine (pid 1234)".</summary>
    string Description { get; }

    IAsyncEnumerable<PullRequestEvent> WatchAsync(PullRequestRef pullRequest, bool refresh, CancellationToken cancellationToken);

    Task<FileDiff> GetFileDiffAsync(PullRequestRef pullRequest, string path, CancellationToken cancellationToken) =>
        GetFileDiffAsync(new GetFileDiffRequest { PullRequest = pullRequest, Path = path }, cancellationToken);

    /// <summary>A file's diff, optionally limited to a commit range or ignoring whitespace.</summary>
    Task<FileDiff> GetFileDiffAsync(GetFileDiffRequest request, CancellationToken cancellationToken);

    Task<SourceFile> GetSourceFileAsync(PullRequestRef pullRequest, string path, CancellationToken cancellationToken);

    Task SetReviewPointStateAsync(PullRequestRef pullRequest, string reviewPointId, ReviewPointState state, CancellationToken cancellationToken);

    Task<PostReviewCommentReply> PostCommentAsync(PostReviewCommentRequest request, CancellationToken cancellationToken);

    /// <summary>Ticks or clears the file's "Viewed" checkbox on GitHub.</summary>
    Task SetFileViewedAsync(PullRequestRef pullRequest, string path, bool viewed, CancellationToken cancellationToken);

    Task<Connections> GetConnectionsAsync(CancellationToken cancellationToken);

    /// <summary>Hands the key to the engine, which puts it in the platform credential store; returns fresh status.</summary>
    Task<Connections> SetOpenRouterKeyAsync(string key, CancellationToken cancellationToken);

    Task<Connections> RemoveOpenRouterKeyAsync(CancellationToken cancellationToken);

    /// <summary>Saves the panel's switches to the engine's settings.json; they apply without a restart.</summary>
    Task<Connections> UpdateConnectionSettingsAsync(ConnectionSettings settings, CancellationToken cancellationToken);

    /// <summary>The global review settings, and the repository's own when the request names one.</summary>
    Task<ReviewSettingsReply> GetReviewSettingsAsync(ReviewSettingsRequest request, CancellationToken cancellationToken);

    /// <summary>Replaces one layer: the global settings, or the named repository's overrides.</summary>
    Task<ReviewSettingsReply> UpdateReviewSettingsAsync(UpdateReviewSettingsRequest request, CancellationToken cancellationToken);

    // ---- v2 ----

    Task<Inbox> ListInboxAsync(bool refresh, CancellationToken cancellationToken);

    /// <summary>The inbox now, then each refresh, until cancelled.</summary>
    IAsyncEnumerable<Inbox> WatchInboxAsync(CancellationToken cancellationToken);

    Task<InboxSettings> GetInboxSettingsAsync(CancellationToken cancellationToken);

    Task<InboxSettings> UpdateInboxSettingsAsync(InboxSettings settings, CancellationToken cancellationToken);

    Task<PullRequestDetails> GetPullRequestDetailsAsync(PullRequestRef pullRequest, bool refresh, CancellationToken cancellationToken);

    Task<DiffRange> GetDiffRangeAsync(GetDiffRangeRequest request, CancellationToken cancellationToken);

    Task<TimelineItem> PostIssueCommentAsync(PullRequestRef pullRequest, string body, CancellationToken cancellationToken);

    Task<ReviewThread> ReplyToThreadAsync(PullRequestRef pullRequest, string threadId, string body, CancellationToken cancellationToken);

    Task<ReviewThread> SetThreadResolvedAsync(PullRequestRef pullRequest, string threadId, bool resolved, CancellationToken cancellationToken);

    Task<PendingReview> GetPendingReviewAsync(PullRequestRef pullRequest, CancellationToken cancellationToken);

    Task<PendingReview> AddPendingCommentAsync(AddPendingCommentRequest request, CancellationToken cancellationToken);

    Task<PendingReview> UpdatePendingCommentAsync(PullRequestRef pullRequest, string commentId, string body, CancellationToken cancellationToken);

    Task<PendingReview> DeletePendingCommentAsync(PullRequestRef pullRequest, string commentId, CancellationToken cancellationToken);

    Task<SubmitReviewReply> SubmitReviewAsync(SubmitReviewRequest request, CancellationToken cancellationToken);

    Task<Connections> SetCloudflareCredentialsAsync(string accountId, string apiToken, CancellationToken cancellationToken);

    Task<Connections> RemoveCloudflareCredentialsAsync(CancellationToken cancellationToken);

    /// <summary>Results arrive on the pull request's stream as WalkthroughUpdated.</summary>
    Task StartWalkthroughAsync(StartWalkthroughRequest request, CancellationToken cancellationToken);
}
