using System.Runtime.CompilerServices;
using Grpc.Core;
using Lumen.App.Services;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>
/// The fixture, except that cancelling a watch fails the way a real gRPC stream does: with RpcException(Cancelled)
/// rather than OperationCanceledException.
/// </summary>
internal sealed class GrpcLikeReviewSource(FixtureReviewSource inner) : IReviewSource
{
    public string Description => inner.Description;

    public async IAsyncEnumerable<PullRequestEvent> WatchAsync(
        PullRequestRef pullRequest,
        bool refresh,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var events = inner.WatchAsync(pullRequest, refresh, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using (events.ConfigureAwait(false))
        {
            while (true)
            {
                try
                {
                    if (!await events.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }
                }
                catch (OperationCanceledException ex)
                {
                    throw new RpcException(new Status(StatusCode.Cancelled, "Call canceled by the client.", ex));
                }

                yield return events.Current;
            }
        }
    }

    public Task<FileDiff> GetFileDiffAsync(GetFileDiffRequest request, CancellationToken cancellationToken) =>
        inner.GetFileDiffAsync(request, cancellationToken);

    public Task<SourceFile> GetSourceFileAsync(PullRequestRef pullRequest, string path, CancellationToken cancellationToken) =>
        inner.GetSourceFileAsync(pullRequest, path, cancellationToken);

    public Task SetReviewPointStateAsync(PullRequestRef pullRequest, string reviewPointId, ReviewPointState state, CancellationToken cancellationToken) =>
        inner.SetReviewPointStateAsync(pullRequest, reviewPointId, state, cancellationToken);

    public Task<PostReviewCommentReply> PostCommentAsync(PostReviewCommentRequest request, CancellationToken cancellationToken) =>
        inner.PostCommentAsync(request, cancellationToken);

    /// <summary>When set, saving a "Viewed" tick fails the way the engine reports a GitHub refusal.</summary>
    public string? FailViewedWith { get; set; }

    public Task SetFileViewedAsync(PullRequestRef pullRequest, string path, bool viewed, CancellationToken cancellationToken) =>
        FailViewedWith is null
            ? inner.SetFileViewedAsync(pullRequest, path, viewed, cancellationToken)
            : Task.FromException(new RpcException(new Status(StatusCode.Unavailable, FailViewedWith)));

    public Task<Connections> GetConnectionsAsync(CancellationToken cancellationToken) => inner.GetConnectionsAsync(cancellationToken);

    public Task<Connections> SetOpenRouterKeyAsync(string key, CancellationToken cancellationToken) => inner.SetOpenRouterKeyAsync(key, cancellationToken);

    public Task<Connections> RemoveOpenRouterKeyAsync(CancellationToken cancellationToken) => inner.RemoveOpenRouterKeyAsync(cancellationToken);

    public Task<Connections> UpdateConnectionSettingsAsync(ConnectionSettings settings, CancellationToken cancellationToken) =>
        inner.UpdateConnectionSettingsAsync(settings, cancellationToken);

    public Task<ReviewSettingsReply> GetReviewSettingsAsync(ReviewSettingsRequest request, CancellationToken cancellationToken) =>
        inner.GetReviewSettingsAsync(request, cancellationToken);

    public Task<ReviewSettingsReply> UpdateReviewSettingsAsync(UpdateReviewSettingsRequest request, CancellationToken cancellationToken) =>
        inner.UpdateReviewSettingsAsync(request, cancellationToken);

    public Task<Inbox> ListInboxAsync(bool refresh, CancellationToken cancellationToken) => inner.ListInboxAsync(refresh, cancellationToken);

    public IAsyncEnumerable<Inbox> WatchInboxAsync(CancellationToken cancellationToken) => inner.WatchInboxAsync(cancellationToken);

    public Task<InboxSettings> GetInboxSettingsAsync(CancellationToken cancellationToken) => inner.GetInboxSettingsAsync(cancellationToken);

    public Task<InboxSettings> UpdateInboxSettingsAsync(InboxSettings settings, CancellationToken cancellationToken) =>
        inner.UpdateInboxSettingsAsync(settings, cancellationToken);

    public Task<PullRequestDetails> GetPullRequestDetailsAsync(PullRequestRef pullRequest, bool refresh, CancellationToken cancellationToken) =>
        inner.GetPullRequestDetailsAsync(pullRequest, refresh, cancellationToken);

    public Task<DiffRange> GetDiffRangeAsync(GetDiffRangeRequest request, CancellationToken cancellationToken) => inner.GetDiffRangeAsync(request, cancellationToken);

    public Task<TimelineItem> PostIssueCommentAsync(PullRequestRef pullRequest, string body, CancellationToken cancellationToken) =>
        inner.PostIssueCommentAsync(pullRequest, body, cancellationToken);

    public Task<ReviewThread> ReplyToThreadAsync(PullRequestRef pullRequest, string threadId, string body, CancellationToken cancellationToken) =>
        inner.ReplyToThreadAsync(pullRequest, threadId, body, cancellationToken);

    public Task<ReviewThread> SetThreadResolvedAsync(PullRequestRef pullRequest, string threadId, bool resolved, CancellationToken cancellationToken) =>
        inner.SetThreadResolvedAsync(pullRequest, threadId, resolved, cancellationToken);

    public Task<PendingReview> GetPendingReviewAsync(PullRequestRef pullRequest, CancellationToken cancellationToken) =>
        inner.GetPendingReviewAsync(pullRequest, cancellationToken);

    public Task<PendingReview> AddPendingCommentAsync(AddPendingCommentRequest request, CancellationToken cancellationToken) =>
        inner.AddPendingCommentAsync(request, cancellationToken);

    public Task<PendingReview> UpdatePendingCommentAsync(PullRequestRef pullRequest, string commentId, string body, CancellationToken cancellationToken) =>
        inner.UpdatePendingCommentAsync(pullRequest, commentId, body, cancellationToken);

    public Task<PendingReview> DeletePendingCommentAsync(PullRequestRef pullRequest, string commentId, CancellationToken cancellationToken) =>
        inner.DeletePendingCommentAsync(pullRequest, commentId, cancellationToken);

    public Task<SubmitReviewReply> SubmitReviewAsync(SubmitReviewRequest request, CancellationToken cancellationToken) =>
        inner.SubmitReviewAsync(request, cancellationToken);

    public Task<Connections> SetCloudflareCredentialsAsync(string accountId, string apiToken, CancellationToken cancellationToken) =>
        inner.SetCloudflareCredentialsAsync(accountId, apiToken, cancellationToken);

    public Task<Connections> RemoveCloudflareCredentialsAsync(CancellationToken cancellationToken) => inner.RemoveCloudflareCredentialsAsync(cancellationToken);

    public Task StartWalkthroughAsync(StartWalkthroughRequest request, CancellationToken cancellationToken) =>
        inner.StartWalkthroughAsync(request, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
