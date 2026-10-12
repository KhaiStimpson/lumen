using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using Lumen.Contracts;

namespace Lumen.App.Services;

/// <summary>Talks to the local engine over gRPC; restarts it transparently if it goes away mid-review.</summary>
public sealed class EngineReviewSource : IReviewSource
{
    private readonly EngineLauncher _launcher;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private GrpcChannel? _channel;
    private int _processId;

    public EngineReviewSource(EngineLauncher launcher)
    {
        _launcher = launcher;
    }

    public string Description => _processId == 0 ? "Local engine" : $"Local engine · pid {_processId}";

    public async IAsyncEnumerable<PullRequestEvent> WatchAsync(
        PullRequestRef pullRequest,
        bool refresh,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
            using var call = client.WatchPullRequest(
                new WatchPullRequestRequest { PullRequest = pullRequest, Refresh = refresh || attempt > 0 },
                cancellationToken: cancellationToken);

            var enumerator = call.ResponseStream.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            var lost = false;
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            yield break;
                        }
                    }
                    catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.Internal && attempt < 2)
                    {
                        // The engine died (or was restarted). Reconnect — the launcher starts a fresh one — and resubscribe.
                        await ResetAsync().ConfigureAwait(false);
                        lost = true;
                        break;
                    }

                    yield return enumerator.Current;
                }
            }

            if (!lost)
            {
                yield break;
            }
        }
    }

    public async Task<FileDiff> GetFileDiffAsync(GetFileDiffRequest request, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .GetFileDiffAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<SourceFile> GetSourceFileAsync(PullRequestRef pullRequest, string path, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .GetSourceFileAsync(new GetSourceFileRequest { PullRequest = pullRequest, Path = path }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task SetReviewPointStateAsync(PullRequestRef pullRequest, string reviewPointId, ReviewPointState state, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .SetReviewPointStateAsync(
                new SetReviewPointStateRequest { PullRequest = pullRequest, ReviewPointId = reviewPointId, State = state },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<PostReviewCommentReply> PostCommentAsync(PostReviewCommentRequest request, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .PostReviewCommentAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task SetFileViewedAsync(PullRequestRef pullRequest, string path, bool viewed, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .SetFileViewedAsync(
                new SetFileViewedRequest { PullRequest = pullRequest, Path = path, Viewed = viewed },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<Connections> GetConnectionsAsync(CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .GetConnectionsAsync(new GetConnectionsRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<Connections> SetOpenRouterKeyAsync(string key, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .SetOpenRouterKeyAsync(new SetOpenRouterKeyRequest { Key = key }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<Connections> RemoveOpenRouterKeyAsync(CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .RemoveOpenRouterKeyAsync(new RemoveOpenRouterKeyRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<Connections> UpdateConnectionSettingsAsync(ConnectionSettings settings, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .UpdateConnectionSettingsAsync(settings, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<ReviewSettingsReply> GetReviewSettingsAsync(ReviewSettingsRequest request, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .GetReviewSettingsAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<ReviewSettingsReply> UpdateReviewSettingsAsync(UpdateReviewSettingsRequest request, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .UpdateReviewSettingsAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<Inbox> ListInboxAsync(bool refresh, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .ListInboxAsync(new ListInboxRequest { Refresh = refresh }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async IAsyncEnumerable<Inbox> WatchInboxAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        using var call = client.WatchInbox(new WatchInboxRequest(), cancellationToken: cancellationToken);
        await foreach (var inbox in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return inbox;
        }
    }

    public async Task<InboxSettings> GetInboxSettingsAsync(CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .GetInboxSettingsAsync(new GetInboxSettingsRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<InboxSettings> UpdateInboxSettingsAsync(InboxSettings settings, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .UpdateInboxSettingsAsync(settings, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<PullRequestDetails> GetPullRequestDetailsAsync(PullRequestRef pullRequest, bool refresh, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .GetPullRequestDetailsAsync(new GetPullRequestDetailsRequest { PullRequest = pullRequest, Refresh = refresh }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<DiffRange> GetDiffRangeAsync(GetDiffRangeRequest request, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .GetDiffRangeAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<TimelineItem> PostIssueCommentAsync(PullRequestRef pullRequest, string body, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .PostIssueCommentAsync(new PostIssueCommentRequest { PullRequest = pullRequest, Body = body }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<ReviewThread> ReplyToThreadAsync(PullRequestRef pullRequest, string threadId, string body, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .ReplyToThreadAsync(new ReplyToThreadRequest { PullRequest = pullRequest, ThreadId = threadId, Body = body }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<ReviewThread> SetThreadResolvedAsync(PullRequestRef pullRequest, string threadId, bool resolved, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .SetThreadResolvedAsync(new SetThreadResolvedRequest { PullRequest = pullRequest, ThreadId = threadId, Resolved = resolved }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<PendingReview> GetPendingReviewAsync(PullRequestRef pullRequest, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .GetPendingReviewAsync(new GetPendingReviewRequest { PullRequest = pullRequest }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<PendingReview> AddPendingCommentAsync(AddPendingCommentRequest request, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .AddPendingCommentAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<PendingReview> UpdatePendingCommentAsync(PullRequestRef pullRequest, string commentId, string body, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .UpdatePendingCommentAsync(new UpdatePendingCommentRequest { PullRequest = pullRequest, CommentId = commentId, Body = body }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<PendingReview> DeletePendingCommentAsync(PullRequestRef pullRequest, string commentId, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .DeletePendingCommentAsync(new DeletePendingCommentRequest { PullRequest = pullRequest, CommentId = commentId }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<SubmitReviewReply> SubmitReviewAsync(SubmitReviewRequest request, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .SubmitReviewAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<Connections> SetCloudflareCredentialsAsync(string accountId, string apiToken, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .SetCloudflareCredentialsAsync(new SetCloudflareCredentialsRequest { AccountId = accountId, ApiToken = apiToken }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<Connections> RemoveCloudflareCredentialsAsync(CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .RemoveCloudflareCredentialsAsync(new RemoveCloudflareCredentialsRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task StartWalkthroughAsync(StartWalkthroughRequest request, CancellationToken cancellationToken) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false))
            .StartWalkthroughAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    private async Task<ReviewEngine.ReviewEngineClient> ClientAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_channel is null)
            {
                (_channel, _processId) = await _launcher.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }

            return new ReviewEngine.ReviewEngineClient(_channel);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task ResetAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _channel?.Dispose();
            _channel = null;
            _processId = 0;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel?.Dispose();
        _connectGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
