using Lumen.Contracts;

namespace Lumen.App.Services;

/// <summary>Everything the UI needs from the review engine. Backed by gRPC in the app, by fixtures in tests.</summary>
public interface IReviewSource : IAsyncDisposable
{
    /// <summary>Human-readable description of where data comes from, e.g. "Local engine (pid 1234)".</summary>
    string Description { get; }

    IAsyncEnumerable<PullRequestEvent> WatchAsync(PullRequestRef pullRequest, bool refresh, CancellationToken cancellationToken);

    Task<FileDiff> GetFileDiffAsync(PullRequestRef pullRequest, string path, CancellationToken cancellationToken);

    Task<SourceFile> GetSourceFileAsync(PullRequestRef pullRequest, string path, CancellationToken cancellationToken);

    Task SetReviewPointStateAsync(PullRequestRef pullRequest, string reviewPointId, ReviewPointState state, CancellationToken cancellationToken);

    Task<PostReviewCommentReply> PostCommentAsync(PostReviewCommentRequest request, CancellationToken cancellationToken);
}
