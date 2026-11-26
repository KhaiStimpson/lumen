using Google.Protobuf.WellKnownTypes;
using Lumen.Contracts;
using Lumen.Domain;
using D = Lumen.Domain;

namespace Lumen.Engine.Mapping;

/// <summary>Domain ↔ wire mapping. The client only ever sees these contract types.</summary>
public static partial class ProtoMapper
{
    public static PullRequestKey ToKey(PullRequestRef value) =>
        new(new RepositoryRef(value.Owner, value.Name), value.Number);

    public static PullRequestRef ToProto(PullRequestKey key) => new()
    {
        Owner = key.Repository.Owner,
        Name = key.Repository.Name,
        Number = key.Number,
    };

    public static Contracts.PullRequestSnapshot ToProto(D.PullRequestSnapshot snapshot, string viewer, IReadOnlySet<string> viewedFiles)
    {
        var proto = new Contracts.PullRequestSnapshot
        {
            PullRequest = ToProto(snapshot.Key),
            Title = snapshot.Metadata.Title,
            Author = snapshot.Metadata.Author,
            State = snapshot.Metadata.State,
            IsDraft = snapshot.Metadata.IsDraft,
            BaseRef = snapshot.Metadata.BaseRef,
            HeadRef = snapshot.Metadata.HeadRef,
            Url = snapshot.Metadata.Url,
            Body = snapshot.Metadata.Body ?? "",
            BaseSha = snapshot.BaseSha,
            HeadSha = snapshot.HeadSha,
            MergeBaseSha = snapshot.MergeBaseSha,
            UpdatedAt = Timestamp.FromDateTimeOffset(snapshot.Metadata.UpdatedAt),
            ViewerLogin = viewer,
            ReviewThreadCount = snapshot.Threads.Count(t => !t.IsResolved && !t.IsPendingOnly),
            TargetKey = snapshot.Target.Key,
        };
        if (snapshot.Target is LocalBranchTarget local)
        {
            proto.Local = ToProto(local);
        }

        proto.Files.AddRange(snapshot.Files.Select(f => ToSummary(f, viewedFiles.Contains(f.Path))));
        return proto;
    }

    public static LocalBranchRef ToProto(LocalBranchTarget target) => new()
    {
        ClonePath = target.ClonePath,
        BaseRef = target.BaseRef,
        WorkingTree = target.HeadKind == LocalHeadKind.WorkingTree,
        HeadRef = target.HeadRef ?? "",
    };

    public static LocalBranchTarget FromProto(LocalBranchRef value) =>
        new(value.ClonePath, value.BaseRef, value.WorkingTree ? LocalHeadKind.WorkingTree : LocalHeadKind.Branch, value.WorkingTree ? null : value.HeadRef);

    /// <summary>The TriageReady event: every hunk, the groups, a summary, and the files with lines per fold.</summary>
    public static TriageReady ToProto(
        D.TriageResult triage,
        D.PullRequestSnapshot snapshot,
        IReadOnlySet<string> viewedFiles)
    {
        var ready = new TriageReady { Summary = Summarise(triage) };
        ready.Hunks.AddRange(triage.Hunks.Select(ToProto));
        ready.Groups.AddRange(triage.Groups.Select(g => new Contracts.TriageGroup
        {
            Id = g.Id,
            ChangeClass = ToProto(g.Class),
            Title = g.Title,
            MemberCount = g.Members.Count,
            ChangedLines = g.Members.Sum(m => m.ChangedLines),
        }));

        var byPath = triage.Hunks.ToLookup(h => h.Path, StringComparer.Ordinal);
        ready.Files.AddRange(snapshot.Files.Select(f =>
        {
            var summary = ToSummary(f, viewedFiles.Contains(f.Path));
            summary.FoldLines = FoldLinesOf(byPath[f.Path]);
            return summary;
        }));
        return ready;
    }

    /// <summary>Back to the domain: groups are rebuilt from the hunks that carry their id.</summary>
    public static D.TriageResult FromProto(TriageReady ready)
    {
        var hunks = ready.Hunks.Select(FromProto).ToList();
        var groups = ready.Groups.Select(g => new D.TriageGroup
        {
            Id = g.Id,
            Class = FromProto(g.ChangeClass),
            Title = g.Title,
            Members = [.. hunks.Where(h => h.GroupId == g.Id)],
        }).ToList();
        return new D.TriageResult(hunks, groups);
    }

    public static Contracts.HunkTriage ToProto(D.HunkTriage h)
    {
        var proto = new Contracts.HunkTriage
        {
            Path = h.Path,
            OldStart = h.OldStart ?? 0,
            OldEnd = h.OldEnd ?? 0,
            NewStart = h.NewStart ?? 0,
            NewEnd = h.NewEnd ?? 0,
            ChangeClass = ToProto(h.Class),
            Fold = ToProto(h.Fold),
            ChangedLines = h.ChangedLines,
            GroupId = h.GroupId ?? "",
        };
        proto.Reasons.AddRange(h.Reasons);
        return proto;
    }

    public static D.HunkTriage FromProto(Contracts.HunkTriage h) => new()
    {
        Path = h.Path,
        OldStart = h.OldStart == 0 ? null : h.OldStart,
        OldEnd = h.OldEnd == 0 ? null : h.OldEnd,
        NewStart = h.NewStart == 0 ? null : h.NewStart,
        NewEnd = h.NewEnd == 0 ? null : h.NewEnd,
        Class = FromProto(h.ChangeClass),
        Fold = FromProto(h.Fold),
        Reasons = [.. h.Reasons],
        ChangedLines = h.ChangedLines,
        GroupId = h.GroupId.Length == 0 ? null : h.GroupId,
    };

    public static FoldLines FoldLinesOf(IEnumerable<D.HunkTriage> hunks)
    {
        var lines = new FoldLines();
        foreach (var h in hunks)
        {
            switch (h.Fold)
            {
                case D.FoldKind.Mechanical: lines.Mechanical += h.ChangedLines; break;
                case D.FoldKind.Boilerplate: lines.Boilerplate += h.ChangedLines; break;
                default: lines.Open += h.ChangedLines; break;
            }
        }

        return lines;
    }

    public static TriageSummary Summarise(D.TriageResult triage) => new()
    {
        TotalLines = triage.Hunks.Sum(h => h.ChangedLines),
        MechanicalLines = triage.Hunks.Where(h => h.Fold == D.FoldKind.Mechanical).Sum(h => h.ChangedLines),
        BoilerplateLines = triage.Hunks.Where(h => h.Fold == D.FoldKind.Boilerplate).Sum(h => h.ChangedLines),
        OpenLines = triage.Hunks.Where(h => h.Fold == D.FoldKind.Open).Sum(h => h.ChangedLines),
        LikelyMechanicalLines = triage.Hunks.Where(h => h.Class.IsMechanical() && h.Fold == D.FoldKind.Open).Sum(h => h.ChangedLines),
        NewCodeLines = triage.Hunks.Where(h => h.Class == D.ChangeClass.NewCode).Sum(h => h.ChangedLines),
        BehaviourChangeLines = triage.Hunks.Where(h => h.Class == D.ChangeClass.BehaviourChange).Sum(h => h.ChangedLines),
    };

    public static Contracts.ChangeClass ToProto(D.ChangeClass value) => value switch
    {
        D.ChangeClass.NewCode => Contracts.ChangeClass.NewCode,
        D.ChangeClass.Formatting => Contracts.ChangeClass.Formatting,
        D.ChangeClass.CommentsOnly => Contracts.ChangeClass.CommentsOnly,
        D.ChangeClass.ImportsOnly => Contracts.ChangeClass.ImportsOnly,
        D.ChangeClass.Rename => Contracts.ChangeClass.Rename,
        D.ChangeClass.Move => Contracts.ChangeClass.Move,
        D.ChangeClass.Ripple => Contracts.ChangeClass.Ripple,
        D.ChangeClass.Generated => Contracts.ChangeClass.Generated,
        _ => Contracts.ChangeClass.BehaviourChange,
    };

    public static D.ChangeClass FromProto(Contracts.ChangeClass value) => value switch
    {
        Contracts.ChangeClass.NewCode => D.ChangeClass.NewCode,
        Contracts.ChangeClass.Formatting => D.ChangeClass.Formatting,
        Contracts.ChangeClass.CommentsOnly => D.ChangeClass.CommentsOnly,
        Contracts.ChangeClass.ImportsOnly => D.ChangeClass.ImportsOnly,
        Contracts.ChangeClass.Rename => D.ChangeClass.Rename,
        Contracts.ChangeClass.Move => D.ChangeClass.Move,
        Contracts.ChangeClass.Ripple => D.ChangeClass.Ripple,
        Contracts.ChangeClass.Generated => D.ChangeClass.Generated,
        _ => D.ChangeClass.BehaviourChange,
    };

    public static Contracts.FoldKind ToProto(D.FoldKind value) => value switch
    {
        D.FoldKind.Mechanical => Contracts.FoldKind.Mechanical,
        D.FoldKind.Boilerplate => Contracts.FoldKind.Boilerplate,
        _ => Contracts.FoldKind.Open,
    };

    public static D.FoldKind FromProto(Contracts.FoldKind value) => value switch
    {
        Contracts.FoldKind.Mechanical => D.FoldKind.Mechanical,
        Contracts.FoldKind.Boilerplate => D.FoldKind.Boilerplate,
        _ => D.FoldKind.Open,
    };

    public static Contracts.RedFlag ToProto(D.RedFlag flag) => new()
    {
        Id = flag.Id,
        Kind = (Contracts.FlagKind)flag.Kind,
        Path = flag.Path,
        Side = flag.Side == D.DiffSide.Left ? Contracts.DiffSide.Left : Contracts.DiffSide.Right,
        Line = flag.Line,
        Message = flag.Message,
        Member = flag.Member,
    };

    public static FileDiff ToProto(ChangedFile file)
    {
        var diff = new FileDiff { Path = file.Path };
        foreach (var hunk in file.Hunks)
        {
            var h = new Contracts.DiffHunk
            {
                OldStart = hunk.OldStart,
                OldCount = hunk.OldCount,
                NewStart = hunk.NewStart,
                NewCount = hunk.NewCount,
                Header = hunk.Header,
            };
            h.Lines.AddRange(hunk.Lines.Select(l => new Contracts.DiffLine
            {
                Kind = (Contracts.DiffLineKind)l.Kind,
                OldNumber = l.OldNumber ?? 0,
                NewNumber = l.NewNumber ?? 0,
                Text = l.Text,
            }));
            diff.Hunks.Add(h);
        }

        return diff;
    }

}
