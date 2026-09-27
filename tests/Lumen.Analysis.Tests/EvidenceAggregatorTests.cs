using Lumen.Domain;

namespace Lumen.Analysis.Tests;

public sealed class EvidenceAggregatorTests
{
    private static ReviewPoint Point(EvidenceState state = EvidenceState.Unverified) => new()
    {
        Id = "pp-1",
        ChangeUnitIds = ["cu-1"],
        Type = ReviewPointType.PatternDeviation,
        Severity = ReviewSeverity.Medium,
        State = ReviewPointState.Visible,
        EvidenceState = state,
        Title = "t",
        Summary = "s",
        WhyItMatters = "Claims prevent duplicates.",
        SuggestedComment = "c",
        Anchor = new CodeLocation("a.cs", 1, 1),
    };

    private static InvestigationResult Result(InvestigationOutcome outcome, string? rationale = null) => new()
    {
        InvestigationId = "inv-1",
        Type = InvestigationType.RepositoryPattern,
        Claim = "c",
        Outcome = outcome,
        Summary = "Seven workers claim first.",
        Producer = "claude-code · pattern-investigator/v1 · claude-sonnet-5",
        Rationale = rationale,
        Evidence =
        [
            new AgentInvestigationEvidence
            {
                Id = "inv-1-0",
                Source = EvidenceSource.AgentInvestigation,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Summary = "ChargeWorker claims first (src/ChargeWorker.cs:9)",
                Producer = "claude-code · pattern-investigator/v1 · claude-sonnet-5",
                InvestigationId = "inv-1",
                Location = new CodeLocation("src/ChargeWorker.cs", 9, 9),
            },
        ],
    };

    [Fact]
    public void SupportedInvestigationStrengthensAndExplains()
    {
        var updated = EvidenceAggregator.Apply(Point(), Result(InvestigationOutcome.Supported, "Two instances once charged twice."));

        Assert.Equal(EvidenceState.StrongEvidence, updated.EvidenceState);
        Assert.Equal(ReviewSeverity.Medium, updated.Severity);
        Assert.Equal(2, updated.Evidence.Count);
        Assert.StartsWith("Investigation supports this", updated.Evidence[0].Summary, StringComparison.Ordinal);
        Assert.All(updated.Evidence, e => Assert.Equal("claude-code · pattern-investigator/v1 · claude-sonnet-5", e.Producer));
        Assert.EndsWith("Why the precedent exists: Two instances once charged twice.", updated.WhyItMatters, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportedNeverOverridesExistingConflict() =>
        Assert.Equal(EvidenceState.ConflictingEvidence, EvidenceAggregator.Apply(Point(EvidenceState.ConflictingEvidence), Result(InvestigationOutcome.Supported)).EvidenceState);

    [Fact]
    public void RefutedInvestigationDemotesButKeepsThePointVisible()
    {
        var updated = EvidenceAggregator.Apply(Point(EvidenceState.StrongEvidence), Result(InvestigationOutcome.Refuted, "ignored"));

        Assert.Equal(EvidenceState.ConflictingEvidence, updated.EvidenceState);
        Assert.Equal(ReviewSeverity.Low, updated.Severity);
        Assert.Equal(ReviewPointState.Visible, updated.State);
        Assert.True(updated.Evidence[0].IsCounterEvidence);
        Assert.Equal("Claims prevent duplicates.", updated.WhyItMatters);
    }

    [Fact]
    public void InconclusiveOnlyRecordsThatItWasTried()
    {
        var updated = EvidenceAggregator.Apply(Point(), Result(InvestigationOutcome.Inconclusive) with { Evidence = [] });

        Assert.Equal(EvidenceState.Unverified, updated.EvidenceState);
        Assert.StartsWith("Investigation inconclusive", Assert.Single(updated.Evidence).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyingTheSameInvestigationTwiceIsANoOp()
    {
        var once = EvidenceAggregator.Apply(Point(), Result(InvestigationOutcome.Supported));

        Assert.Same(once, EvidenceAggregator.Apply(once, Result(InvestigationOutcome.Supported)));
    }
}
