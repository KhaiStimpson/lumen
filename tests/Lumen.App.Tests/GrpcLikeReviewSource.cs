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
        if (refresh && RefreshEvents is { } refreshed)
        {
            foreach (var evt in refreshed)
            {
                yield return evt;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            yield break;
        }

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

    public IReadOnlyList<PullRequestEvent>? RefreshEvents { get; set; }

    public string? FailDiffPath { get; set; }

    public Task<FileDiff> GetFileDiffAsync(PullRequestRef pullRequest, string path, CancellationToken cancellationToken) =>
        path == FailDiffPath
            ? Task.FromException<FileDiff>(new RpcException(new Status(StatusCode.Unavailable, "Diff unavailable")))
            : inner.GetFileDiffAsync(pullRequest, path, cancellationToken);

    public List<(string Path, bool FromBase)> SourceRequests { get; } = [];

    public Task<SourceFile> GetSourceFileAsync(PullRequestRef pullRequest, string path, CancellationToken cancellationToken, bool fromBase = false)
    {
        SourceRequests.Add((path, fromBase));
        return inner.GetSourceFileAsync(pullRequest, path, cancellationToken, fromBase);
    }

    public Task SetReviewPointStateAsync(PullRequestRef pullRequest, string reviewPointId, ReviewPointState state, CancellationToken cancellationToken) =>
        inner.SetReviewPointStateAsync(pullRequest, reviewPointId, state, cancellationToken);

    public Task<PostReviewCommentReply> PostCommentAsync(PostReviewCommentRequest request, CancellationToken cancellationToken) =>
        inner.PostCommentAsync(request, cancellationToken);

    public Task SetTriageGroupAcknowledgedAsync(PullRequestRef pullRequest, string groupId, bool acknowledged, CancellationToken cancellationToken) =>
        inner.SetTriageGroupAcknowledgedAsync(pullRequest, groupId, acknowledged, cancellationToken);

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

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
