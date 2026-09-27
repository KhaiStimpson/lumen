using Lumen.Domain;

namespace Lumen.Analysis;

public sealed record PipelineResult(
    IReadOnlyList<ReviewPoint> ReviewPoints,
    IReadOnlyList<RepositoryConvention> Conventions,
    IReadOnlyList<(Candidate Candidate, AttentionDecision Decision)> Suppressed)
{
    /// <summary>The decision behind each surfaced review point, by review point id; drives investigation planning.</summary>
    public IReadOnlyDictionary<string, AttentionDecision> Decisions { get; init; } = new Dictionary<string, AttentionDecision>();
}

/// <summary>Detectors → attention policy → explainer → ranked review points.</summary>
public sealed class ReviewPointPipeline(
    IEnumerable<IChangeDetector> detectors,
    IAttentionPolicy policy,
    IEnumerable<IReviewPointExplainer> explainers)
{
    private readonly IReadOnlyList<IChangeDetector> _detectors = [.. detectors];
    private readonly IReadOnlyList<IReviewPointExplainer> _explainers = [.. explainers];

    public async Task<PipelineResult> RunAsync(
        AnalysisContext context,
        Func<ReviewPoint, Task>? onReviewPoint,
        CancellationToken cancellationToken)
    {
        var points = new List<ReviewPoint>();
        var conventions = new List<RepositoryConvention>();
        var suppressed = new List<(Candidate, AttentionDecision)>();
        var decisions = new Dictionary<string, AttentionDecision>(StringComparer.Ordinal);

        foreach (var detector in _detectors)
        {
            var detection = await detector.DetectAsync(context, cancellationToken).ConfigureAwait(false);
            conventions.AddRange(detection.Conventions);

            var decided = await policy.DecideAllAsync(detection.Candidates, context, cancellationToken).ConfigureAwait(false);
            foreach (var (candidate, decision) in detection.Candidates.Zip(decided))
            {
                if (decision.Action == AttentionAction.Suppress)
                {
                    suppressed.Add((candidate, decision));
                    continue;
                }

                var explainer = _explainers.FirstOrDefault(e => e.CanExplain(candidate))
                    ?? throw new InvalidOperationException($"No explainer for candidate from '{candidate.DetectorId}'.");
                var explanation = await explainer.ExplainAsync(candidate, context, cancellationToken).ConfigureAwait(false);

                var point = new ReviewPoint
                {
                    Id = candidate.Key,
                    ChangeUnitIds = [.. candidate.ChangeUnits.Select(u => u.Id)],
                    Type = candidate.Type,
                    Severity = decision.Severity,
                    State = ReviewPointState.Visible,
                    EvidenceState = ClassifyEvidence(candidate),
                    Title = explanation.Title,
                    Summary = explanation.Summary,
                    WhyItMatters = explanation.WhyItMatters,
                    SuggestedComment = explanation.SuggestedComment,
                    Anchor = candidate.Anchor,
                    OtherLocations = candidate.OtherLocations,
                    Evidence = candidate.Evidence,
                    Surface = explanation.Surface,
                    Priority = decision.Priority,
                    RoutingReason = decision.Reason,
                };

                points.Add(point);
                decisions[point.Id] = decision;
                if (onReviewPoint is not null)
                {
                    await onReviewPoint(point).ConfigureAwait(false);
                }
            }
        }

        return new PipelineResult(Rank(points), conventions, suppressed) { Decisions = decisions };
    }

    /// <summary>Highest priority first; ties broken by file and line so the order is stable and readable.</summary>
    public static IReadOnlyList<ReviewPoint> Rank(IEnumerable<ReviewPoint> points) =>
    [
        .. points
            .OrderByDescending(p => p.Severity)
            .ThenByDescending(p => p.Priority)
            .ThenBy(p => p.Anchor.Path, StringComparer.Ordinal)
            .ThenBy(p => p.Anchor.StartLine),
    ];

    private static EvidenceState ClassifyEvidence(Candidate candidate)
    {
        var s = candidate.Signals;
        if (s.Support >= 0.9 && s.PeerCount >= 4)
        {
            return EvidenceState.StrongEvidence;
        }

        return candidate.Evidence.Any(e => e.IsCounterEvidence) ? EvidenceState.ConflictingEvidence : EvidenceState.Unverified;
    }
}
