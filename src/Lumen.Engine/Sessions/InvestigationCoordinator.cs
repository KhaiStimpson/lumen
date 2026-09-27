using Lumen.Agents.Investigations;
using Lumen.Analysis;
using Lumen.Contracts;
using Lumen.Domain;
using ReviewPoint = Lumen.Domain.ReviewPoint;
using EvidenceState = Lumen.Domain.EvidenceState;
using ReviewPointState = Lumen.Domain.ReviewPointState;
using Lumen.Engine.Mapping;

namespace Lumen.Engine.Sessions;

/// <summary>
/// After a review's deterministic analysis is visible, decides which review points deserve an investigation, runs
/// them in the background and folds the results back in (TDD §13–14, §35). Never blocks or fails the review: if
/// agents are off, unavailable or failing, the reviewer simply keeps the deterministic evidence (§55).
/// </summary>
public sealed partial class InvestigationCoordinator(
    EngineSettings settings,
    InvestigationScheduler scheduler,
    ILogger<InvestigationCoordinator> logger)
{
    /// <summary>Investigations the attention policy asked for; without JEV, pattern deviations with exceptions.</summary>
    public static IReadOnlyList<InvestigationJob> Plan(PipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var jobs = new List<InvestigationJob>();
        foreach (var point in result.ReviewPoints)
        {
            if (!result.Decisions.TryGetValue(point.Id, out var decision))
            {
                continue;
            }

            var requests = decision.Source == "jev" ? decision.Investigations : FallbackRouting(point);
            jobs.AddRange(requests.Select(r => new InvestigationJob(point, r.Type, r.Budget, decision.Priority)));
        }

        return jobs;
    }

    private static IReadOnlyList<InvestigationRequest> FallbackRouting(ReviewPoint point) =>
        point.Type == ReviewPointType.PatternDeviation && point.EvidenceState == EvidenceState.ConflictingEvidence
            ? [new InvestigationRequest(InvestigationType.RepositoryPattern, InvestigationBudget.Standard)]
            : [];

    public async Task RunAsync(PullRequestSession session, PipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);

        if (!settings.AgentsAllowed || session.Checkout is not { } checkout)
        {
            return;
        }

        var jobs = Plan(result)
            .Where(j => session.FindReviewPoint(j.Point.Id)?.State is not (ReviewPointState.Dismissed or ReviewPointState.Resolved))
            .ToList();
        if (jobs.Count == 0)
        {
            return;
        }

        try
        {
            var summary = await scheduler.RunAsync(
                checkout,
                session.Key,
                jobs,
                (investigation, point) =>
                {
                    var current = session.FindReviewPoint(point.Id) ?? point;
                    var updated = EvidenceAggregator.Apply(current, investigation);
                    if (!ReferenceEquals(updated, current))
                    {
                        session.AddReviewPoint(updated);
                        session.Publish(new PullRequestEvent { ReviewPointUpdated = ProtoMapper.ToProto(updated) });
                    }

                    return Task.CompletedTask;
                },
                active => session.Publish(new PullRequestEvent { InvestigationStatus = new InvestigationStatus { Active = active } }),
                session.Cancellation).ConfigureAwait(false);

            if (summary.Unavailable is { } reason)
            {
                session.Publish(new PullRequestEvent { InvestigationStatus = new InvestigationStatus { Active = 0, Detail = reason } });
            }

            LogInvestigationsDone(logger, session.Key, summary.Cached, summary.Completed, summary.Skipped);
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogInvestigationsFailed(logger, ex, session.Key);
            session.Publish(new PullRequestEvent { InvestigationStatus = new InvestigationStatus { Active = 0, Detail = "Investigations stopped" } });
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{PullRequest}: investigations — {Cached} cached, {Completed} run, {Skipped} skipped")]
    private static partial void LogInvestigationsDone(ILogger logger, PullRequestKey pullRequest, int cached, int completed, int skipped);

    [LoggerMessage(Level = LogLevel.Error, Message = "{PullRequest}: investigations failed; deterministic evidence stands")]
    private static partial void LogInvestigationsFailed(ILogger logger, Exception exception, PullRequestKey pullRequest);
}
