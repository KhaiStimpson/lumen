namespace Lumen.Domain;

public enum ChangeKind
{
    TypeAdded,
    TypeModified,
}

/// <summary>A span of head-side lines in a file. Lines are 1-based and inclusive.</summary>
public sealed record CodeLocation(string Path, int StartLine, int EndLine, string? Symbol = null);

/// <summary>An idea or behaviour that changed, not merely a diff hunk (TDD §6.2).</summary>
public sealed record ChangeUnit
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required ChangeKind Kind { get; init; }

    public required IReadOnlyList<string> Symbols { get; init; }

    public required IReadOnlyList<CodeLocation> Locations { get; init; }

    public IReadOnlyList<string> Concepts { get; init; } = [];
}

public enum ReviewPointType
{
    PatternDeviation,
    CorrectnessRisk,
    BehaviourChange,
    ArchitectureDrift,
    MissingCoverage,
    PotentialOverengineering,
    Duplication,
    ErrorHandling,
    PerformanceRisk,
    SecurityRisk,
    HumanDecision,
}

public enum ReviewSeverity
{
    Low,
    Medium,
    High,
}

public enum ReviewPointState
{
    Candidate,
    Visible,
    Examined,
    Dismissed,
    Commented,
    Resolved,
}

/// <summary>How well-supported a review point is. Never collapsed into a single confidence number (TDD §14, §51).</summary>
public enum EvidenceState
{
    Verified,
    StrongEvidence,
    ConflictingEvidence,
    Unverified,
    HumanDecision,
}

public enum EvidenceSource
{
    StaticAnalysis,
    RepositoryPrecedent,
    ReviewHistory,
    GeneratedTest,
    Runtime,
    AgentInvestigation,
    GitHistory,
    ArchitectureRule,
    Coverage,
}

public abstract record Evidence
{
    public required string Id { get; init; }

    public required EvidenceSource Source { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>One-line, human-readable statement of what this evidence shows.</summary>
    public required string Summary { get; init; }

    /// <summary>Which component produced this, e.g. "peer-pattern/v1". Provenance for every claim.</summary>
    public required string Producer { get; init; }

    /// <summary>True when this evidence argues against the review point.</summary>
    public bool IsCounterEvidence { get; init; }
}

/// <summary>A code excerpt from the repository that demonstrates (or contradicts) a convention.</summary>
public sealed record PrecedentExample(CodeLocation Location, string TypeName, string Snippet, int SnippetStartLine);

public sealed record RepositoryPrecedentEvidence : Evidence
{
    public required string Convention { get; init; }

    public required int Supporting { get; init; }

    public required int PeerCount { get; init; }

    public required IReadOnlyList<PrecedentExample> Examples { get; init; }
}

public sealed record StaticAnalysisEvidence : Evidence
{
    public required CodeLocation Location { get; init; }

    public string? RuleId { get; init; }
}

/// <summary>A constrained presentation schema chosen for a review point (TDD §23). Never executable UI.</summary>
public abstract record SurfaceSpec;

public sealed record ComparisonSurface(ComparisonSide Current, ComparisonSide Precedent) : SurfaceSpec;

public sealed record ComparisonSide(
    string Title,
    string Caption,
    CodeLocation Source,
    string Snippet,
    int SnippetStartLine,
    IReadOnlyList<int> HighlightLines,
    IReadOnlyList<string> Points);

public sealed record ReviewPoint
{
    /// <summary>Stable across re-analysis of the same PR, so dismissals persist.</summary>
    public required string Id { get; init; }

    public required IReadOnlyList<string> ChangeUnitIds { get; init; }

    public required ReviewPointType Type { get; init; }

    public required ReviewSeverity Severity { get; init; }

    public required ReviewPointState State { get; init; }

    public required EvidenceState EvidenceState { get; init; }

    public required string Title { get; init; }

    public required string Summary { get; init; }

    public required string WhyItMatters { get; init; }

    public required string SuggestedComment { get; init; }

    /// <summary>Primary head-side location, always a line inside the diff so a comment can attach to it.</summary>
    public required CodeLocation Anchor { get; init; }

    public IReadOnlyList<CodeLocation> OtherLocations { get; init; } = [];

    public IReadOnlyList<Evidence> Evidence { get; init; } = [];

    public SurfaceSpec? Surface { get; init; }

    public double Priority { get; init; }

    /// <summary>Why the attention policy surfaced this. Diagnostic only.</summary>
    public string? RoutingReason { get; init; }
}

/// <summary>A repository convention inferred from code (TDD §16), limited here to observed precedent.</summary>
public sealed record RepositoryConvention(
    string Id,
    string Statement,
    string PeerGroup,
    int Supporting,
    int PeerCount,
    IReadOnlyList<CodeLocation> Examples);
