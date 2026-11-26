using System.Text.Json.Serialization;

namespace Lumen.Domain;

/// <summary>What kind of change a hunk is. Mechanical classes must be proven, never guessed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChangeClass>))]
public enum ChangeClass
{
    Formatting,
    CommentsOnly,
    ImportsOnly,
    Rename,
    Move,
    Ripple,
    Generated,
    NewCode,
    BehaviourChange,
}

public static class ChangeClasses
{
    /// <summary>Classes a proof has shown to be mechanical (Ripple call sites included: they only follow another change).</summary>
    public static bool IsMechanical(this ChangeClass value) => value is
        ChangeClass.Formatting or ChangeClass.CommentsOnly or ChangeClass.ImportsOnly or ChangeClass.Rename or
        ChangeClass.Move or ChangeClass.Ripple or ChangeClass.Generated;
}

/// <summary>
/// Whether a hunk is folded behind a one-line reason. Folding is never inferred from a class: only a proof
/// (<see cref="Mechanical"/>) or one of a short list of boilerplate shapes (<see cref="Boilerplate"/>) folds, and an
/// opened fold always shows every line. The zero value is the safe one: unset means open.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FoldKind>))]
public enum FoldKind
{
    /// <summary>Stays visible. Everything that might change behaviour or security, and proven changes worth seeing.</summary>
    Open,

    /// <summary>"Mechanical · Proven": a classifier proved the change is mechanical.</summary>
    Mechanical,

    /// <summary>"Boilerplate · Not verified": new code matching one of the listed shapes.</summary>
    Boilerplate,
}

/// <summary>
/// The triage verdict for one hunk. <see cref="Reasons"/> are words, never a score (TDD §14, §51).
/// Lines are 1-based and inclusive; <see cref="NewStart"/>/<see cref="NewEnd"/> are null for a pure deletion
/// and <see cref="OldStart"/>/<see cref="OldEnd"/> are null for pure additions.
/// </summary>
public sealed record HunkTriage
{
    public required string Path { get; init; }

    public int? OldStart { get; init; }

    public int? OldEnd { get; init; }

    public int? NewStart { get; init; }

    public int? NewEnd { get; init; }

    public required ChangeClass Class { get; init; }

    /// <summary>Whether the hunk is folded, and as what. Proven-but-worth-seeing hunks (ripple call sites) are Open.</summary>
    public required FoldKind Fold { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>Added plus removed lines in the hunk (context excluded); the unit for "lines per tier".</summary>
    public int ChangedLines { get; init; }

    /// <summary>Set when this hunk belongs to a <see cref="TriageGroup"/> (a rename, a move, a ripple).</summary>
    public string? GroupId { get; init; }
}

/// <summary>Hunks that are one idea across the PR, such as a rename applied in many files.</summary>
public sealed record TriageGroup
{
    public required string Id { get; init; }

    public required ChangeClass Class { get; init; }

    /// <summary>For example "Renamed `Foo`→`Bar`".</summary>
    public required string Title { get; init; }

    public required IReadOnlyList<HunkTriage> Members { get; init; }
}

/// <summary>Every hunk's triage for one head commit, and the groups that tie hunks together.</summary>
public sealed record TriageResult(IReadOnlyList<HunkTriage> Hunks, IReadOnlyList<TriageGroup> Groups)
{
    public static readonly TriageResult Empty = new([], []);

    /// <summary>Red flags found with the same sources, cached with the triage (docs/overhaul.md, Red flags).</summary>
    public IReadOnlyList<RedFlag> Flags { get; init; } = [];
}

/// <summary>Triage is computed once per head commit and triage version, then served from here (like investigations).</summary>
public interface ITriageStore
{
    Task<TriageResult?> FindTriageAsync(ReviewTarget target, string headSha, string version, CancellationToken cancellationToken);

    Task SaveTriageAsync(ReviewTarget target, string headSha, string version, TriageResult result, CancellationToken cancellationToken);
}
