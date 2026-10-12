using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Lumen.Analysis;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Engine.Mapping;
using Lumen.GitHub;

namespace Lumen.Engine.Sessions;

/// <summary>Starts, caches and replaces pull request sessions; runs the load → checkout → analyse pipeline.</summary>
public sealed partial class PullRequestSessionManager(
    IGitHubClient gitHub,
    IRepositoryWorkspace workspace,
    IReviewStore store,
    ReviewPointPipeline pipeline,
    InvestigationCoordinator investigations,
    ReviewSettingsStore reviewSettings,
    IPullRequestVisitStore visits,
    EngineOptions options,
    TimeProvider time,
    ILogger<PullRequestSessionManager> logger) : IDisposable
{
    private readonly ConcurrentDictionary<PullRequestKey, PullRequestSession> _sessions = new();
    private readonly Lock _startGate = new();

    public PullRequestSession? Find(PullRequestKey key) => _sessions.GetValueOrDefault(key);

    public PullRequestSession GetOrStart(PullRequestKey key, bool refresh)
    {
        lock (_startGate)
        {
            if (_sessions.TryGetValue(key, out var existing) && !(refresh && existing.IsFinished) && !existing.HasFailed)
            {
                return existing;
            }

            existing?.Cancel();
            var session = new PullRequestSession(key);
            _sessions[key] = session;
            session.Completion = Task.Run(() => RunAsync(session));
            return session;
        }
    }

    private async Task RunAsync(PullRequestSession session)
    {
        var key = session.Key;
        var ct = session.Cancellation;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            Progress(session, "github", "Fetching pull request…");
            var (info, viewer, reviewState, viewed) = await LoadFromGitHubAsync(key, ct).ConfigureAwait(false);
            var threads = reviewState.Threads;

            var checkout = await workspace.CheckoutAsync(
                key,
                info.BaseSha,
                info.HeadSha,
                new InlineProgress(message => Progress(session, "repository", message)),
                ct).ConfigureAwait(false);

            // Resolved once, so this run never sees a settings change half-applied; Re-analyse picks up the next one.
            var settings = reviewSettings.Resolve(key.Repository);

            Progress(session, "diff", "Reading changes…");
            var files = ChangedFiles.FromUnifiedDiff(await checkout.GetDiffAsync(ct).ConfigureAwait(false), settings.MechanicalPaths);
            var snapshot = new Domain.PullRequestSnapshot(key, info.BaseSha, info.HeadSha, checkout.MergeBaseSha, info.Metadata, files, threads);
            session.SetSnapshot(snapshot, checkout);
            session.ViewerLogin = viewer;
            session.Publish(new PullRequestEvent { Snapshot = ProtoMapper.ToProto(snapshot, viewer, viewed) });
            session.SetReviewState(reviewState, force: true);
            LogSnapshotReady(logger, key, files.Count, stopwatch.ElapsedMilliseconds);
            await RecordOpenedAsync(session, info.HeadSha, ct).ConfigureAwait(false);

            // The conversation, commits and checks come separately: the diff never waits for them.
            _ = Task.Run(() => FollowAsync(session), CancellationToken.None);
            SessionReady?.Invoke(session);

            Progress(session, "analysis", "Comparing with repository precedent…");
            var states = await store.GetLatestActionsAsync(key, ct).ConfigureAwait(false);

            var result = await pipeline.RunAsync(
                new AnalysisContext(snapshot, checkout) { Settings = settings },
                point =>
                {
                    var withState = states.TryGetValue(point.Id, out var action) ? point with { State = StateFor(action, point.State) } : point;
                    session.AddReviewPoint(withState);
                    session.Publish(new PullRequestEvent { ReviewPointAdded = ProtoMapper.ToProto(withState) });
                    return Task.CompletedTask;
                },
                ct).ConfigureAwait(false);

            session.Publish(new PullRequestEvent { Conventions = ProtoMapper.ToProto(result.Conventions) });
            session.Publish(new PullRequestEvent
            {
                Complete = new AnalysisComplete
                {
                    ReviewPointCount = result.ReviewPoints.Count,
                    SuppressedCount = result.Suppressed.Count,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                },
            });
            LogAnalysisComplete(logger, key, result.ReviewPoints.Count, result.Suppressed.Count, stopwatch.ElapsedMilliseconds);
            session.MarkFinished(failed: false);

            // The review is complete and usable; investigations only add evidence to it from here on.
            await investigations.RunAsync(session, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.MarkFinished(failed: true);
        }
        catch (Exception ex)
        {
            LogAnalysisFailed(logger, ex, key);
            var isAuth = ex is GitHubAuthenticationException or GitHubApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized };
            session.Publish(new PullRequestEvent
            {
                Failed = new AnalysisFailed
                {
                    Message = session.Snapshot is null ? ex.Message : $"Analysis stopped: {ex.Message}. The diff is still available.",
                    IsAuthentication = isAuth,
                },
            });

            // Degrade gracefully (TDD §55): a loaded diff stays usable even if analysis failed.
            session.MarkFinished(failed: session.Snapshot is null);
        }
    }

    /// <summary>Raised once a session's snapshot is published (the walkthrough starts from here).</summary>
    public event Action<PullRequestSession>? SessionReady;

    /// <summary>Remembers the previous visit (for "since last review"), then records this one.</summary>
    private async Task RecordOpenedAsync(PullRequestSession session, string headSha, CancellationToken ct)
    {
        try
        {
            session.PreviousVisit = await visits.GetVisitAsync(session.Key, ct).ConfigureAwait(false);
            await visits.RecordOpenedAsync(session.Key, headSha, time.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogVisitNotRecorded(logger, ex, session.Key);
        }
    }

    /// <summary>
    /// Loads details at once, then keeps details, threads and the pending review fresh while anyone watches, so new
    /// comments, checks and pushes show up without a reload.
    /// </summary>
    private async Task FollowAsync(PullRequestSession session)
    {
        var ct = session.Cancellation;
        await RefreshDetailsAsync(session, ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.FollowInterval, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (session.SubscriberCount == 0)
            {
                continue;
            }

            if (gitHub.RateLimit is { Remaining: < 100 } limit && limit.ResetAt > time.GetUtcNow())
            {
                continue;
            }

            await RefreshDetailsAsync(session, ct).ConfigureAwait(false);
            await RefreshReviewStateAsync(session, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Best effort: a failure leaves the previous details in place.</summary>
    public async Task<bool> RefreshDetailsAsync(PullRequestSession session, CancellationToken ct)
    {
        try
        {
            session.SetDetails(await gitHub.GetPullRequestDetailsAsync(session.Key, ct).ConfigureAwait(false));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDetailsUnavailable(logger, ex, session.Key);
            return false;
        }
    }

    /// <summary>Re-reads threads and the pending review from GitHub; best effort.</summary>
    public async Task RefreshReviewStateAsync(PullRequestSession session, CancellationToken ct)
    {
        try
        {
            session.SetReviewState(await gitHub.GetReviewThreadsAsync(session.Key, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogThreadsUnavailable(logger, ex, session.Key);
        }
    }

    /// <summary>Loads PR data, falling back to the last cached copy when GitHub is unreachable (TDD §55).</summary>
    private async Task<(PullRequestInfo Info, string Viewer, ReviewThreadsResult ReviewState, IReadOnlySet<string> Viewed)> LoadFromGitHubAsync(
        PullRequestKey key,
        CancellationToken ct)
    {
        var cacheKey = $"pr:{key}";
        try
        {
            var infoTask = gitHub.GetPullRequestAsync(key, ct);
            var viewerTask = gitHub.GetViewerLoginAsync(ct);
            var threadsTask = GetReviewStateAsync(key, ct);
            var viewedTask = GetViewedFilesAsync(key, ct);
            await Task.WhenAll(infoTask, viewerTask, threadsTask, viewedTask).ConfigureAwait(false);
            var (info, viewer, state) = (await infoTask.ConfigureAwait(false), await viewerTask.ConfigureAwait(false), await threadsTask.ConfigureAwait(false));
            var cached = new CachedPullRequest(info, viewer, [.. state.Threads]);
            await store.SetCacheAsync(cacheKey, JsonSerializer.Serialize(cached), ct).ConfigureAwait(false);
            return (info, viewer, state, await viewedTask.ConfigureAwait(false));
        }
        catch (HttpRequestException ex)
        {
            var json = await store.GetCacheAsync(cacheKey, ct).ConfigureAwait(false);
            if (json is null || JsonSerializer.Deserialize<CachedPullRequest>(json) is not { } cached)
            {
                throw;
            }

            LogUsingCachedPullRequest(logger, ex, key);

            // Viewed state isn't cached: a stale tick is worse than none. Neither is a pending review.
            return (cached.Info, cached.Viewer, new ReviewThreadsResult(cached.Threads, null), new HashSet<string>());
        }
    }

    /// <summary>Best-effort: the review works without threads (e.g. a token that can't use GraphQL).</summary>
    private async Task<ReviewThreadsResult> GetReviewStateAsync(PullRequestKey key, CancellationToken ct)
    {
        try
        {
            return await gitHub.GetReviewThreadsAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not HttpRequestException)
        {
            LogThreadsUnavailable(logger, ex, key);
            return new ReviewThreadsResult([], null);
        }
    }

    /// <summary>Best-effort: the review works without it (e.g. a token that can't use GraphQL), just with no ticks.</summary>
    private async Task<IReadOnlySet<string>> GetViewedFilesAsync(PullRequestKey key, CancellationToken ct)
    {
        try
        {
            return await gitHub.GetViewedFilesAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogViewedFilesUnavailable(logger, ex, key);
            return new HashSet<string>();
        }
    }

    private static Domain.ReviewPointState StateFor(ReviewAction action, Domain.ReviewPointState fallback) => action switch
    {
        ReviewAction.Dismissed => Domain.ReviewPointState.Dismissed,
        ReviewAction.Commented => Domain.ReviewPointState.Commented,
        ReviewAction.Examined => Domain.ReviewPointState.Examined,
        ReviewAction.Restored => Domain.ReviewPointState.Visible,
        _ => fallback,
    };

    private static void Progress(PullRequestSession session, string stage, string message) =>
        session.Publish(new PullRequestEvent { Progress = new AnalysisProgress { Stage = stage, Message = message } });

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
    }

    /// <summary>Unlike <see cref="Progress{T}"/>, reports synchronously so events keep their order.</summary>
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed record CachedPullRequest(PullRequestInfo Info, string Viewer, List<Domain.ReviewThread> Threads);

    [LoggerMessage(Level = LogLevel.Information, Message = "{PullRequest}: snapshot ready ({Files} files) in {Elapsed} ms")]
    private static partial void LogSnapshotReady(ILogger logger, PullRequestKey pullRequest, int files, long elapsed);

    [LoggerMessage(Level = LogLevel.Information, Message = "{PullRequest}: {Points} review points, {Suppressed} suppressed, {Elapsed} ms")]
    private static partial void LogAnalysisComplete(ILogger logger, PullRequestKey pullRequest, int points, int suppressed, long elapsed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: couldn't read the Viewed checkboxes from GitHub")]
    private static partial void LogViewedFilesUnavailable(ILogger logger, Exception exception, PullRequestKey pullRequest);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: couldn't read review threads from GitHub")]
    private static partial void LogThreadsUnavailable(ILogger logger, Exception exception, PullRequestKey pullRequest);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: couldn't read the conversation, commits or checks from GitHub")]
    private static partial void LogDetailsUnavailable(ILogger logger, Exception exception, PullRequestKey pullRequest);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: couldn't record the visit")]
    private static partial void LogVisitNotRecorded(ILogger logger, Exception exception, PullRequestKey pullRequest);

    [LoggerMessage(Level = LogLevel.Error, Message = "{PullRequest}: analysis failed")]
    private static partial void LogAnalysisFailed(ILogger logger, Exception exception, PullRequestKey pullRequest);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: GitHub unreachable, using cached pull request")]
    private static partial void LogUsingCachedPullRequest(ILogger logger, Exception exception, PullRequestKey pullRequest);
}
