using Lumen.Domain;

namespace Lumen.Analysis;

/// <summary>Deterministic thresholds in the spirit of TDD §10.3. Replaced by a JEV-backed policy in Phase 3.</summary>
public sealed class RuleBasedAttentionPolicy : IAttentionPolicy
{
    public int MinimumPeers { get; init; } = 3;

    public double MinimumSupport { get; init; } = 0.75;

    public double MinimumLift { get; init; } = 1.5;

    public ValueTask<AttentionDecision> DecideAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        var s = candidate.Signals;

        if (!s.IntroducedByChange)
        {
            return Decide(AttentionAction.Suppress, ReviewSeverity.Low, 0, "Deviation predates this change");
        }

        if (candidate.Type == ReviewPointType.PatternDeviation)
        {
            if (s.PeerCount < MinimumPeers)
            {
                return Decide(AttentionAction.Suppress, ReviewSeverity.Low, 0, $"Only {s.PeerCount} peers; precedent too thin");
            }

            // A slightly weaker majority is still convincing when many peers agree.
            var required = s.Supporting >= 5 ? MinimumSupport - 0.05 : MinimumSupport;
            if (s.Support < required)
            {
                return Decide(AttentionAction.Suppress, ReviewSeverity.Low, 0, $"Support {s.Support:P0} below {MinimumSupport:P0}");
            }

            // Depending on a ubiquitous type (e.g. the DbContext) is weak evidence of a role-specific convention.
            var requiredLift = s.Category == "dependency" ? MinimumLift + 1 : MinimumLift;
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
