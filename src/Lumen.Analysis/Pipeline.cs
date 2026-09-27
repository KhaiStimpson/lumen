using Lumen.Domain;

namespace Lumen.Analysis;

/// <summary>Everything a detector may look at. Head files are read from the checkout.</summary>
public sealed record AnalysisContext(PullRequestSnapshot Snapshot, IPullRequestCheckout Checkout);

/// <summary>
/// Compact, structured signals describing a candidate — the input an attention policy decides on. This is the
/// shape JEV will receive later (TDD §10.1), so keep it small, numeric and free of source code.
/// </summary>
public sealed record AttentionSignals
{
    public required string ChangeKind { get; init; }

    public required bool IntroducedByChange { get; init; }

    public int PeerCount { get; init; }

    public int Supporting { get; init; }

    /// <summary>Share of peers that follow the convention (0..1).</summary>
    public double Support => PeerCount == 0 ? 0 : (double)Supporting / PeerCount;

    /// <summary>How much more common the convention is in the peer group than across the repository.</summary>
    public double Lift { get; init; }

    public int AffectedLocations { get; init; } = 1;

    public string? Category { get; init; }
}

/// <summary>A possible review point, before the attention policy and explainer have seen it.</summary>
public sealed record Candidate
{
    /// <summary>Deterministic key; becomes the stable review point id.</summary>
    public required string Key { get; init; }

    public required string DetectorId { get; init; }

    public required ReviewPointType Type { get; init; }

    public required IReadOnlyList<ChangeUnit> ChangeUnits { get; init; }

    public required CodeLocation Anchor { get; init; }

    public IReadOnlyList<CodeLocation> OtherLocations { get; init; } = [];

    public required IReadOnlyList<Evidence> Evidence { get; init; }

    public required AttentionSignals Signals { get; init; }

    /// <summary>Detector-specific facts the matching explainer knows how to phrase.</summary>
    public required object Facts { get; init; }
}

public interface IChangeDetector
{
    string Id { get; }

    Task<DetectionResult> DetectAsync(AnalysisContext context, CancellationToken cancellationToken);
}

public sealed record DetectionResult(IReadOnlyList<Candidate> Candidates, IReadOnlyList<RepositoryConvention> Conventions)
{
    public static readonly DetectionResult Empty = new([], []);
}

public enum AttentionAction
{
    Suppress,
    Surface,
    Investigate,
}

/// <summary>An investigation the attention policy asks for (TDD §10.3). The engine decides whether it runs.</summary>
public sealed record InvestigationRequest(InvestigationType Type, InvestigationBudget Budget);

public sealed record AttentionDecision(AttentionAction Action, ReviewSeverity Severity, double Priority, string Reason)
{
    public IReadOnlyList<InvestigationRequest> Investigations { get; init; } = [];
}

/// <summary>Decides whether a candidate deserves the reviewer's attention: rules, or JEV on top of them (TDD §10).</summary>
public interface IAttentionPolicy
{
    ValueTask<AttentionDecision> DecideAsync(Candidate candidate, CancellationToken cancellationToken);

    /// <summary>Decides a detector's candidates together, so a policy can batch per change unit (TDD §38.1).</summary>
    async ValueTask<IReadOnlyList<AttentionDecision>> DecideAllAsync(
        IReadOnlyList<Candidate> candidates,
        AnalysisContext context,
        CancellationToken cancellationToken)
    {
        var decisions = new List<AttentionDecision>(candidates.Count);
        foreach (var candidate in candidates)
        {
            decisions.Add(await DecideAsync(candidate, cancellationToken).ConfigureAwait(false));
        }

        return decisions;
    }
}

public sealed record Explanation(
    string Title,
    string Summary,
    string WhyItMatters,
    string SuggestedComment,
    SurfaceSpec? Surface);

/// <summary>Turns a candidate into reviewer-facing language. Templates now; an agent later.</summary>
public interface IReviewPointExplainer
{
    bool CanExplain(Candidate candidate);

    ValueTask<Explanation> ExplainAsync(Candidate candidate, AnalysisContext context, CancellationToken cancellationToken);
}
