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

    Task<Connections> GetConnectionsAsync(CancellationToken cancellationToken);

    /// <summary>Hands the key to the engine, which puts it in the platform credential store; returns fresh status.</summary>
    Task<Connections> SetOpenRouterKeyAsync(string key, CancellationToken cancellationToken);

    Task<Connections> RemoveOpenRouterKeyAsync(CancellationToken cancellationToken);

    /// <summary>Saves the panel's switches to the engine's settings.json; they apply without a restart.</summary>
    Task<Connections> UpdateConnectionSettingsAsync(ConnectionSettings settings, CancellationToken cancellationToken);
}
