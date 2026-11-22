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

/// <summary>Where a reviewer should spend attention. The names read as instructions to a reviewer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TriageTier>))]
public enum TriageTier
{
    Critical,
    WorthALook,
    Skim,
    Skip,
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

    public required TriageTier Tier { get; init; }

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
}

/// <summary>Triage is computed once per head commit and triage version, then served from here (like investigations).</summary>
public interface ITriageStore
{
    Task<TriageResult?> FindTriageAsync(PullRequestKey key, string headSha, string version, CancellationToken cancellationToken);

    Task SaveTriageAsync(PullRequestKey key, string headSha, string version, TriageResult result, CancellationToken cancellationToken);
}

/// <summary>Acknowledging a group is stored like a dismissal: an interaction on a per-PR id, latest action wins.</summary>
public static class TriageAcknowledgement
{
    private const string Prefix = "triage-group:";

    public static string InteractionId(string groupId) => Prefix + groupId;

    /// <summary>Group ids whose latest recorded action is Acknowledged.</summary>
    public static IReadOnlySet<string> AcknowledgedGroups(IReadOnlyDictionary<string, ReviewAction> latestActions) =>
        latestActions
            .Where(kv => kv.Key.StartsWith(Prefix, StringComparison.Ordinal) && kv.Value == ReviewAction.Acknowledged)
            .Select(kv => kv.Key[Prefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
}
