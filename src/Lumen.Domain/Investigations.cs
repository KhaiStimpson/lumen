namespace Lumen.Domain;

/// <summary>Specialist roles (TDD §11.1). There is deliberately no generic "review this PR" agent.</summary>
public enum InvestigationType
{
    RepositoryPattern,
    Correctness,
    Architecture,
    Tests,
    History,
    Security,
}

/// <summary>How much an investigation may spend (TDD §36).</summary>
public enum InvestigationBudget
{
    Tiny,
    Standard,
    Deep,
}

/// <summary>What an investigation concluded about the claim it was given.</summary>
public enum InvestigationOutcome
{
    /// <summary>The investigation found evidence for the claim.</summary>
    Supported,

    /// <summary>The investigation found the claim does not hold (e.g. the deviation is justified).</summary>
    Refuted,

    Inconclusive,
}

public sealed record SourceReference(CodeLocation Location, string Note);

/// <summary>A temporary test or command an agent ran to gather executable evidence. Always ephemeral.</summary>
public sealed record GeneratedExperiment(string Description, string? Command, string? Output);

/// <summary>What an agent returns (TDD §13). Agents never create UI; the engine decides what becomes a review point.</summary>
public sealed record InvestigationResult
{
    public required string InvestigationId { get; init; }

    public required InvestigationType Type { get; init; }

    public required string Claim { get; init; }

    public required InvestigationOutcome Outcome { get; init; }

    /// <summary>One or two sentences, in the agent's words, of what it found.</summary>
    public required string Summary { get; init; }

    public required IReadOnlyList<Evidence> Evidence { get; init; }

    public IReadOnlyList<Evidence> CounterEvidence { get; init; } = [];

    public IReadOnlyList<SourceReference> RelevantSources { get; init; } = [];

    public GeneratedExperiment? Experiment { get; init; }

    public bool RecommendReviewPoint { get; init; }

    /// <summary>Why the precedent exists, when the investigation could tell (e.g. from history).</summary>
    public string? Rationale { get; init; }

    /// <summary>Provider, role and model, e.g. "claude-code · pattern-investigator/v1 · claude-sonnet-5".</summary>
    public required string Producer { get; init; }
}

/// <summary>A claim established by an agent investigation, grounded in a repository location where it has one.</summary>
public sealed record AgentInvestigationEvidence : Evidence
{
    public required string InvestigationId { get; init; }

    public CodeLocation? Location { get; init; }
}
