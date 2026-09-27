using Lumen.Domain;

namespace Lumen.Analysis;

/// <summary>
/// Deterministic thresholds in the spirit of TDD §10.3, and the floor under JEV. During an analysis the thresholds
/// come from the repository's <see cref="ReviewSensitivity"/>; the properties are the defaults outside one.
/// </summary>
public sealed class RuleBasedAttentionPolicy : IAttentionPolicy
{
    public int MinimumPeers { get; init; } = ReviewSensitivity.Balanced.MinimumPeers;

    public double MinimumSupport { get; init; } = ReviewSensitivity.Balanced.MinimumSupport;

    public double MinimumLift { get; init; } = ReviewSensitivity.Balanced.MinimumLift;

    public ValueTask<AttentionDecision> DecideAsync(Candidate candidate, CancellationToken cancellationToken) =>
        DecideAsync(candidate, new ReviewSensitivity { MinimumPeers = MinimumPeers, MinimumSupport = MinimumSupport, MinimumLift = MinimumLift });

    public async ValueTask<IReadOnlyList<AttentionDecision>> DecideAllAsync(
        IReadOnlyList<Candidate> candidates,
        AnalysisContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);
        var decisions = new List<AttentionDecision>(candidates.Count);
        foreach (var candidate in candidates)
        {
            decisions.Add(await DecideAsync(candidate, context.Settings.Sensitivity).ConfigureAwait(false));
        }

        return decisions;
    }

    public static ValueTask<AttentionDecision> DecideAsync(Candidate candidate, ReviewSensitivity sensitivity)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(sensitivity);
        var s = candidate.Signals;

        if (!s.IntroducedByChange)
        {
            return Decide(AttentionAction.Suppress, ReviewSeverity.Low, 0, "Deviation predates this change");
        }

        if (candidate.Type == ReviewPointType.PatternDeviation)
        {
            if (s.PeerCount < sensitivity.MinimumPeers)
            {
                return Decide(AttentionAction.Suppress, ReviewSeverity.Low, 0, $"Only {s.PeerCount} peers; precedent too thin");
            }

            // A slightly weaker majority is still convincing when many peers agree.
            var required = s.Supporting >= 5 ? sensitivity.MinimumSupport - 0.05 : sensitivity.MinimumSupport;
            if (s.Support < required)
            {
                return Decide(AttentionAction.Suppress, ReviewSeverity.Low, 0, $"Support {s.Support:P0} below {sensitivity.MinimumSupport:P0}");
            }

            // Depending on a ubiquitous type (e.g. the DbContext) is weak evidence of a role-specific convention.
            var requiredLift = s.Category == "dependency" ? sensitivity.MinimumLift + 1 : sensitivity.MinimumLift;
            if (s.Lift < requiredLift)
            {
                return Decide(AttentionAction.Suppress, ReviewSeverity.Low, 0, $"Lift {s.Lift:0.0} — convention is not specific to this role");
            }
        }

        var severity = s.Category switch
        {
            "error-handling" or "concurrency" when s.Support >= 0.9 => ReviewSeverity.High,
            "error-handling" or "concurrency" or "dependency" => ReviewSeverity.Medium,
            "framework-dependency" => ReviewSeverity.Low,
            _ when s.Support >= 0.95 && s.PeerCount >= 5 => ReviewSeverity.Medium,
            _ => ReviewSeverity.Low,
        };

        var priority = ((int)severity * 10) + (s.Support * 5) + Math.Min(s.Lift, 5) + Math.Min(s.AffectedLocations, 5);
        return Decide(
            AttentionAction.Surface,
            severity,
            priority,
            $"{s.Supporting}/{s.PeerCount} peers, lift {s.Lift:0.0}, {s.AffectedLocations} location(s)");
    }

    private static ValueTask<AttentionDecision> Decide(AttentionAction action, ReviewSeverity severity, double priority, string reason) =>
        ValueTask.FromResult(new AttentionDecision(action, severity, priority, reason));
}
