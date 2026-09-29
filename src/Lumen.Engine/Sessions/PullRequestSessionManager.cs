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
    TriageRunner triage,
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
            var (info, viewer, threads, viewed) = await LoadFromGitHubAsync(key, ct).ConfigureAwait(false);

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
            session.Publish(new PullRequestEvent { Snapshot = ProtoMapper.ToProto(snapshot, viewer, viewed) });
            LogSnapshotReady(logger, key, files.Count, stopwatch.ElapsedMilliseconds);

            // Triage runs before detectors (it needs only the snapshot) and never blocks the review: null means unavailable.
            Progress(session, "triage", "Separating mechanical changes from real ones…");
            var triageResult = await triage.RunAsync(snapshot, checkout, settings, ct).ConfigureAwait(false);
            session.SetTriage(triageResult);
            var states = await store.GetLatestActionsAsync(key, ct).ConfigureAwait(false);
            if (triageResult is not null)
            {
                var acknowledged = TriageAcknowledgement.AcknowledgedGroups(states);
                session.Publish(new PullRequestEvent { TriageReady = ProtoMapper.ToProto(triageResult, snapshot, viewed, acknowledged) });
            }

            Progress(session, "analysis", "Comparing with repository precedent…");

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

    /// <summary>Loads PR data, falling back to the last cached copy when GitHub is unreachable (TDD §55).</summary>
    private async Task<(PullRequestInfo Info, string Viewer, IReadOnlyList<ReviewThread> Threads, IReadOnlySet<string> Viewed)> LoadFromGitHubAsync(
        PullRequestKey key,
        CancellationToken ct)
    {
        var cacheKey = $"pr:{key}";
        try
        {
            var infoTask = gitHub.GetPullRequestAsync(key, ct);
            var viewerTask = gitHub.GetViewerLoginAsync(ct);
            var threadsTask = gitHub.GetReviewCommentsAsync(key, ct);
            var viewedTask = GetViewedFilesAsync(key, ct);
            await Task.WhenAll(infoTask, viewerTask, threadsTask, viewedTask).ConfigureAwait(false);
            var (info, viewer, threads) = (await infoTask.ConfigureAwait(false), await viewerTask.ConfigureAwait(false), await threadsTask.ConfigureAwait(false));
            var cached = new CachedPullRequest(info, viewer, [.. threads]);
            await store.SetCacheAsync(cacheKey, JsonSerializer.Serialize(cached), ct).ConfigureAwait(false);
            return (info, viewer, threads, await viewedTask.ConfigureAwait(false));
        }
        catch (HttpRequestException ex)
        {
            var json = await store.GetCacheAsync(cacheKey, ct).ConfigureAwait(false);
            if (json is null || JsonSerializer.Deserialize<CachedPullRequest>(json) is not { } cached)
            {
                throw;
            }

            LogUsingCachedPullRequest(logger, ex, key);

            // Viewed state isn't cached: a stale tick is worse than none.
            return (cached.Info, cached.Viewer, cached.Threads, new HashSet<string>());
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

    private sealed record CachedPullRequest(PullRequestInfo Info, string Viewer, List<ReviewThread> Threads);

    [LoggerMessage(Level = LogLevel.Information, Message = "{PullRequest}: snapshot ready ({Files} files) in {Elapsed} ms")]
    private static partial void LogSnapshotReady(ILogger logger, PullRequestKey pullRequest, int files, long elapsed);

    [LoggerMessage(Level = LogLevel.Information, Message = "{PullRequest}: {Points} review points, {Suppressed} suppressed, {Elapsed} ms")]
    private static partial void LogAnalysisComplete(ILogger logger, PullRequestKey pullRequest, int points, int suppressed, long elapsed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: couldn't read the Viewed checkboxes from GitHub")]
    private static partial void LogViewedFilesUnavailable(ILogger logger, Exception exception, PullRequestKey pullRequest);

    [LoggerMessage(Level = LogLevel.Error, Message = "{PullRequest}: analysis failed")]
    private static partial void LogAnalysisFailed(ILogger logger, Exception exception, PullRequestKey pullRequest);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: GitHub unreachable, using cached pull request")]
    private static partial void LogUsingCachedPullRequest(ILogger logger, Exception exception, PullRequestKey pullRequest);
}
