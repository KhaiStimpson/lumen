using Lumen.Domain;

namespace Lumen.Analysis;

/// <summary>What a classifier sees: the whole pull request, plus the one hunk it is asked about.</summary>
public sealed record HunkContext(PullRequestSnapshot Snapshot, ChangedFile File, DiffHunk Hunk);

/// <summary>Ties a hunk to others that are one idea across the pull request (a rename, a move, a ripple).</summary>
public sealed record GroupClaim(string Key, string Title);

/// <summary>A classifier's proof that a hunk is of a given class. <see cref="Reasons"/> are words, never a score.</summary>
public sealed record HunkVerdict(ChangeClass Class, TriageTier Tier, IReadOnlyList<string> Reasons, GroupClaim? Group = null);

/// <summary>
/// One proof about a hunk. Return null when the hunk is not provably of this class: any doubt, parse error or
/// partial match is a null, because a false "mechanical" is the worst bug triage can ship.
/// </summary>
public interface IHunkClassifier
{
    HunkVerdict? Classify(HunkContext context);
}

public sealed record TriageResult(IReadOnlyList<HunkTriage> Hunks, IReadOnlyList<TriageGroup> Groups)
{
    public static readonly TriageResult Empty = new([], []);
}

/// <summary>Claims hunks in files the built-in mechanical rules recognise (generated code, lock files, migrations).</summary>
public sealed class MechanicalFileClassifier : IHunkClassifier
{
    public HunkVerdict? Classify(HunkContext context) =>
        context.File.Mechanical.IsMechanical
            ? new(ChangeClass.Generated, TriageTier.Skip, [context.File.Mechanical.Reason ?? "Mechanical file"])
            : null;
}

/// <summary>
/// Runs classifiers in order over every hunk. The first proof wins; a hunk nobody claims is new code (added file)
/// or a behaviour change, and is tiered provisionally until risk signals re-rank it.
/// </summary>
public sealed class TriagePipeline(IReadOnlyList<IHunkClassifier> classifiers)
{
    public static TriagePipeline Default { get; } = new([new MechanicalFileClassifier()]);

    public TriageResult Run(PullRequestSnapshot snapshot)
    {
        var hunks = new List<HunkTriage>();
        var groupKeys = new List<string>();
        var groupInfo = new Dictionary<string, (ChangeClass Class, string Title, List<HunkTriage> Members)>(StringComparer.Ordinal);

        foreach (var file in snapshot.Files)
        {
            foreach (var hunk in file.Hunks)
            {
                if (SpanOf(hunk) is not { } span)
                {
                    continue;
                }

                var verdict = ClassifyHunk(new HunkContext(snapshot, file, hunk)) ?? Fallback(file);
                var groupId = verdict.Group is { } claim ? $"g{GroupIndex(claim.Key, groupKeys)}" : null;
                var triage = new HunkTriage
                {
                    Path = file.Path,
                    OldStart = span.OldStart,
                    OldEnd = span.OldEnd,
                    NewStart = span.NewStart,
                    NewEnd = span.NewEnd,
                    Class = verdict.Class,
                    Tier = verdict.Tier,
                    Reasons = verdict.Reasons,
                    GroupId = groupId,
                };
                hunks.Add(triage);

                if (groupId is not null)
                {
                    if (!groupInfo.TryGetValue(groupId, out var info))
                    {
                        info = (verdict.Class, verdict.Group!.Title, []);
                        groupInfo[groupId] = info;
                    }

                    info.Members.Add(triage);
                }
            }
        }

        var groups = groupKeys
            .Select((_, i) => $"g{i}")
            .Select(id => new TriageGroup { Id = id, Class = groupInfo[id].Class, Title = groupInfo[id].Title, Members = groupInfo[id].Members })
            .ToList();

        return new TriageResult(hunks, groups);
    }

    private HunkVerdict? ClassifyHunk(HunkContext context)
    {
        foreach (var classifier in classifiers)
        {
            try
            {
                if (classifier.Classify(context) is { } verdict)
                {
                    return verdict;
                }
            }
            catch (Exception)
            {
                // A classifier that cannot decide has not proven anything. Fall through to the next one.
            }
        }

        return null;
    }

    private static HunkVerdict Fallback(ChangedFile file) =>
        file.Kind == FileChangeKind.Added
            ? new(ChangeClass.NewCode, TriageTier.Skim, ["new file"])
            : new(ChangeClass.BehaviourChange, TriageTier.WorthALook, ["changes existing code"]);

    private static int GroupIndex(string key, List<string> keys)
    {
        var index = keys.IndexOf(key);
        if (index >= 0)
        {
            return index;
        }

        keys.Add(key);
        return keys.Count - 1;
    }

    /// <summary>The tightest old/new line spans covering the hunk's changed lines; context is not part of the span.</summary>
    private static (int? OldStart, int? OldEnd, int? NewStart, int? NewEnd)? SpanOf(DiffHunk hunk)
    {
        int? oldStart = null, oldEnd = null, newStart = null, newEnd = null;
        foreach (var line in hunk.Lines)
        {
            if (line.Kind == DiffLineKind.Removed && line.OldNumber is { } o)
            {
                oldStart = oldStart is null ? o : Math.Min(oldStart.Value, o);
                oldEnd = oldEnd is null ? o : Math.Max(oldEnd.Value, o);
            }
            else if (line.Kind == DiffLineKind.Added && line.NewNumber is { } n)
            {
                newStart = newStart is null ? n : Math.Min(newStart.Value, n);
                newEnd = newEnd is null ? n : Math.Max(newEnd.Value, n);
            }
        }

        return oldStart is null && newStart is null ? null : (oldStart, oldEnd, newStart, newEnd);
    }
}
