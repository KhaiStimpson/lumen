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

/// <summary>What part of the system a component plays, used to label it and to place it when nothing links it.</summary>
public enum ComponentLayer
{
    Other,
    Entry,
    Ui,
    Core,
    Integration,
    Data,
    Config,
}

/// <summary>How one changed file uses another, strongest last.</summary>
public enum LinkKind
{
    References,
    DependsOn,
    Implements,
    Tests,
}

/// <summary>A link between two changed files found in the code itself (not a model's reading): <c>From</c> uses <c>To</c>.</summary>
public sealed record FileLink(string From, string To, LinkKind Kind);

/// <summary>A part of the system the pull request builds or changes, e.g. "Ingestion pipeline", and its files.</summary>
public sealed record WalkthroughComponent(string Id, string Title, string Role, ComponentLayer Layer, IReadOnlyList<string> Paths);

/// <summary>
/// <c>From</c> uses <c>To</c> (component ids). Found in the code unless <see cref="Inferred"/>, which marks a link only the
/// model suggested (views, configuration and other files the code links don't reach).
/// </summary>
public sealed record WalkthroughEdge(string From, string To, LinkKind Kind, bool Inferred, string Label);

public sealed record Walkthrough
{
    public WalkthroughStatus Status { get; init; }

    public string Detail { get; init; } = "";

    public string Intent { get; init; } = "";

    public string DescriptionMismatch { get; init; } = "";

    public IReadOnlyList<WalkthroughGroup> Groups { get; init; } = [];

    public IReadOnlyList<WalkthroughRisk> Risks { get; init; } = [];

    /// <summary>The system the pull request builds, for the Map (plan v3): components of changed files and how they connect.</summary>
    public IReadOnlyList<WalkthroughComponent> Components { get; init; } = [];

    public IReadOnlyList<WalkthroughEdge> Edges { get; init; } = [];

    /// <summary>File-to-file links found in the code (including which tests exercise which files).</summary>
    public IReadOnlyList<FileLink> FileLinks { get; init; } = [];

    public string Producer { get; init; } = "";

    public string HeadSha { get; init; } = "";

    /// <summary>Set for a "since last review" walkthrough: the commit the reviewer last saw.</summary>
    public string? SinceSha { get; init; }

    public bool FromCache { get; init; }

    public int FilesDone { get; init; }

    public int FilesTotal { get; init; }
}
