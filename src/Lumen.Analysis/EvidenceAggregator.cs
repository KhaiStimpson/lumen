using Lumen.Domain;

namespace Lumen.Analysis;

/// <summary>
/// Merges an investigation into a review point (TDD §14). Evidence is appended with its provenance, never averaged
/// into a score; the evidence state changes only in words. A refuted point stays visible, demoted, because the
/// reviewer — not the agent — has the final say (§2.4).
/// </summary>
public static class EvidenceAggregator
{
    public static ReviewPoint Apply(ReviewPoint point, InvestigationResult result)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(result);

        if (point.Evidence.OfType<AgentInvestigationEvidence>().Any(e => e.InvestigationId == result.InvestigationId))
        {
            return point;
        }

        var verdict = new AgentInvestigationEvidence
        {
            Id = $"{result.InvestigationId}-summary",
            Source = EvidenceSource.AgentInvestigation,
            CreatedAt = result.Evidence.Concat(result.CounterEvidence).Select(e => e.CreatedAt).DefaultIfEmpty(DateTimeOffset.UtcNow).Max(),
            Summary = result.Outcome switch
            {
                InvestigationOutcome.Supported => $"Investigation supports this: {result.Summary}",
                InvestigationOutcome.Refuted => $"Investigation suggests this is justified: {result.Summary}",
                _ => $"Investigation inconclusive: {result.Summary}",
            },
            Producer = result.Producer,
            InvestigationId = result.InvestigationId,
            IsCounterEvidence = result.Outcome == InvestigationOutcome.Refuted,
        };

        var state = result.Outcome switch
        {
            InvestigationOutcome.Supported when point.EvidenceState == EvidenceState.Unverified => EvidenceState.StrongEvidence,
            InvestigationOutcome.Refuted => EvidenceState.ConflictingEvidence,
            _ => point.EvidenceState,
        };

        return point with
        {
            Evidence = [.. point.Evidence, verdict, .. result.Evidence, .. result.CounterEvidence],
            EvidenceState = state,
            Severity = result.Outcome == InvestigationOutcome.Refuted ? ReviewSeverity.Low : point.Severity,
            WhyItMatters = result.Rationale is { Length: > 0 } rationale && result.Outcome != InvestigationOutcome.Refuted
                ? $"{point.WhyItMatters} Why the precedent exists: {rationale}"
                : point.WhyItMatters,
        };
    }
}
