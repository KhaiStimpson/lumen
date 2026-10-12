namespace Lumen.Domain;

// The PR walkthrough (plan v2 M8): a fast model's reading of the diff. It is never evidence (TDD §2.3) and never posts.

public enum RiskCategory
{
    Other,
    Concurrency,
    ErrorHandling,
    Security,
    BehaviourChange,
    MissingTests,
}

public enum WalkthroughStatus
{
    /// <summary>Not offered: no fast model, not connected, or not allowed to see code.</summary>
    Unavailable,
    Running,
    Complete,
    Failed,
}

/// <summary>Files that make one logical change, e.g. "EF migration", with a one-line summary.</summary>
public sealed record WalkthroughGroup(string Id, string Title, string Summary, IReadOnlyList<string> Paths, int Order, bool IsMechanical);

/// <summary>A location worth a careful look, with a short reason. A model's reading, labelled as such.</summary>
public sealed record WalkthroughRisk(string Id, CodeLocation Location, RiskCategory Category, string Reason);

public sealed record Walkthrough
{
    public WalkthroughStatus Status { get; init; }

    public string Detail { get; init; } = "";

    public string Intent { get; init; } = "";

    public string DescriptionMismatch { get; init; } = "";

    public IReadOnlyList<WalkthroughGroup> Groups { get; init; } = [];

    public IReadOnlyList<WalkthroughRisk> Risks { get; init; } = [];

    public string Producer { get; init; } = "";

    public string HeadSha { get; init; } = "";

    /// <summary>Set for a "since last review" walkthrough: the commit the reviewer last saw.</summary>
    public string? SinceSha { get; init; }

    public bool FromCache { get; init; }

    public int FilesDone { get; init; }

    public int FilesTotal { get; init; }
}
