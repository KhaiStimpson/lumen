namespace Lumen.Domain;

/// <summary>How one changed file uses another, strongest last.</summary>
public enum LinkKind
{
    References,
    DependsOn,
    Implements,
    Tests,
}

/// <summary>A link between two changed files found in the code itself: <c>From</c> uses <c>To</c>.</summary>
public sealed record FileLink(string From, string To, LinkKind Kind);
