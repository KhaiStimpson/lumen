using Google.Protobuf.WellKnownTypes;
using Lumen.Contracts;
using Lumen.Domain;
using D = Lumen.Domain;

namespace Lumen.Engine.Mapping;

/// <summary>Domain ↔ wire mapping. The client only ever sees these contract types.</summary>
public static class ProtoMapper
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
            ReviewThreadCount = snapshot.Threads.Count,
        };

        proto.Files.AddRange(snapshot.Files.Select(f => ToProto(f, viewedFiles.Contains(f.Path))));
        return proto;
    }

    public static ChangedFileSummary ToProto(ChangedFile f, bool viewed) => new()
    {
        Path = f.Path,
        OldPath = f.OldPath ?? "",
        Kind = (Contracts.FileChangeKind)f.Kind,
        Additions = f.Additions,
        Deletions = f.Deletions,
        IsBinary = f.IsBinary,
        IsMechanical = f.Mechanical.IsMechanical,
        MechanicalReason = f.Mechanical.Reason ?? "",
        IsViewed = viewed,
    };

    /// <summary>The TriageReady event: every hunk, the groups, a summary, and the files with lines per tier.</summary>
    public static TriageReady ToProto(
        D.TriageResult triage,
        D.PullRequestSnapshot snapshot,
        IReadOnlySet<string> viewedFiles,
        IReadOnlySet<string>? acknowledgedGroups = null)
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
            Acknowledged = acknowledgedGroups?.Contains(g.Id) ?? false,
        }));

        var byPath = triage.Hunks.ToLookup(h => h.Path, StringComparer.Ordinal);
        ready.Files.AddRange(snapshot.Files.Select(f =>
        {
            var summary = ToProto(f, viewedFiles.Contains(f.Path));
            summary.TierLines = TierLinesOf(byPath[f.Path]);
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
            Tier = ToProto(h.Tier),
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
        Tier = FromProto(h.Tier),
        Reasons = [.. h.Reasons],
        ChangedLines = h.ChangedLines,
        GroupId = h.GroupId.Length == 0 ? null : h.GroupId,
    };

    public static TierLines TierLinesOf(IEnumerable<D.HunkTriage> hunks)
    {
        var lines = new TierLines();
        foreach (var h in hunks)
        {
            switch (h.Tier)
            {
                case D.TriageTier.Critical: lines.Critical += h.ChangedLines; break;
                case D.TriageTier.WorthALook: lines.WorthALook += h.ChangedLines; break;
                case D.TriageTier.Skim: lines.Skim += h.ChangedLines; break;
                case D.TriageTier.Skip: lines.Skip += h.ChangedLines; break;
            }
        }

        return lines;
    }

    public static TriageSummary Summarise(D.TriageResult triage) => new()
    {
        Text = Analysis.TriageSummaryText.Describe(triage),
        TotalLines = triage.Hunks.Sum(h => h.ChangedLines),
        MechanicalLines = Analysis.TriageSummaryText.ProvenMechanical(triage),
        LikelyMechanicalLines = Analysis.TriageSummaryText.LikelyMechanical(triage),
        NewCodeLines = triage.Hunks.Where(h => h.Class == D.ChangeClass.NewCode).Sum(h => h.ChangedLines),
        BehaviourChangeLines = triage.Hunks.Where(h => h.Class == D.ChangeClass.BehaviourChange).Sum(h => h.ChangedLines),
        TierLines = TierLinesOf(triage.Hunks),
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

    public static Contracts.TriageTier ToProto(D.TriageTier value) => value switch
    {
        D.TriageTier.Critical => Contracts.TriageTier.Critical,
        D.TriageTier.Skim => Contracts.TriageTier.Skim,
        D.TriageTier.Skip => Contracts.TriageTier.Skip,
        _ => Contracts.TriageTier.WorthALook,
    };

    public static D.TriageTier FromProto(Contracts.TriageTier value) => value switch
    {
        Contracts.TriageTier.Critical => D.TriageTier.Critical,
        Contracts.TriageTier.Skim => D.TriageTier.Skim,
        Contracts.TriageTier.Skip => D.TriageTier.Skip,
        _ => D.TriageTier.WorthALook,
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

    public static Contracts.ReviewPoint ToProto(D.ReviewPoint point)
    {
        var proto = new Contracts.ReviewPoint
        {
            Id = point.Id,
            Type = point.Type.ToString(),
            Severity = (Contracts.ReviewSeverity)point.Severity,
            State = ToProto(point.State),
            EvidenceState = point.EvidenceState switch
            {
                D.EvidenceState.Verified => Contracts.EvidenceState.Verified,
                D.EvidenceState.StrongEvidence => Contracts.EvidenceState.Strong,
                D.EvidenceState.ConflictingEvidence => Contracts.EvidenceState.Conflicting,
                D.EvidenceState.HumanDecision => Contracts.EvidenceState.HumanDecision,
                _ => Contracts.EvidenceState.Unverified,
            },
            Title = point.Title,
            Summary = point.Summary,
            WhyItMatters = point.WhyItMatters,
            SuggestedComment = point.SuggestedComment,
            Anchor = ToProto(point.Anchor),
            Priority = point.Priority,
            RoutingReason = point.RoutingReason ?? "",
        };

        proto.OtherLocations.AddRange(point.OtherLocations.Select(ToProto));
        proto.Evidence.AddRange(point.Evidence.Select(ToProto));

        if (point.Surface is D.ComparisonSurface comparison)
        {
            proto.Comparison = new Contracts.ComparisonSurface
            {
                Current = ToProto(comparison.Current),
                Precedent = ToProto(comparison.Precedent),
            };
        }

        return proto;
    }

    public static Contracts.ReviewPointState ToProto(D.ReviewPointState state) => state switch
    {
        D.ReviewPointState.Examined => Contracts.ReviewPointState.Examined,
        D.ReviewPointState.Dismissed => Contracts.ReviewPointState.Dismissed,
        D.ReviewPointState.Commented => Contracts.ReviewPointState.Commented,
        D.ReviewPointState.Resolved => Contracts.ReviewPointState.Resolved,
        _ => Contracts.ReviewPointState.Visible,
    };

    public static ReviewAction ToAction(Contracts.ReviewPointState state) => state switch
    {
        Contracts.ReviewPointState.Dismissed => ReviewAction.Dismissed,
        Contracts.ReviewPointState.Commented => ReviewAction.Commented,
        Contracts.ReviewPointState.Examined => ReviewAction.Examined,
        _ => ReviewAction.Restored,
    };

    public static ConventionsFound ToProto(IEnumerable<RepositoryConvention> conventions)
    {
        var proto = new ConventionsFound();
        proto.Conventions.AddRange(conventions.Select(c =>
        {
            var convention = new Convention
            {
                Id = c.Id,
                Statement = c.Statement,
                PeerGroup = c.PeerGroup,
                Supporting = c.Supporting,
                PeerCount = c.PeerCount,
            };
            convention.Examples.AddRange(c.Examples.Select(ToProto));
            return convention;
        }));
        return proto;
    }

    private static Contracts.Evidence ToProto(D.Evidence evidence)
    {
        var proto = new Contracts.Evidence
        {
            Id = evidence.Id,
            Source = evidence.Source.ToString(),
            Producer = evidence.Producer,
            Summary = evidence.Summary,
            IsCounterEvidence = evidence.IsCounterEvidence,
            CreatedAt = Timestamp.FromDateTimeOffset(evidence.CreatedAt),
        };

        switch (evidence)
        {
            case RepositoryPrecedentEvidence precedent:
                proto.Convention = precedent.Convention;
                proto.Supporting = precedent.Supporting;
                proto.PeerCount = precedent.PeerCount;
                proto.Examples.AddRange(precedent.Examples.Select(e => new Contracts.PrecedentExample
                {
                    Location = ToProto(e.Location),
                    TypeName = e.TypeName,
                    Snippet = e.Snippet,
                    SnippetStartLine = e.SnippetStartLine,
                }));
                break;
            case StaticAnalysisEvidence analysis:
                proto.Location = ToProto(analysis.Location);
                break;
            case AgentInvestigationEvidence { Location: { } location }:
                proto.Location = ToProto(location);
                break;
        }

        return proto;
    }

    private static Contracts.ComparisonSide ToProto(D.ComparisonSide side)
    {
        var proto = new Contracts.ComparisonSide
        {
            Title = side.Title,
            Caption = side.Caption,
            Source = ToProto(side.Source),
            Snippet = side.Snippet,
            SnippetStartLine = side.SnippetStartLine,
        };
        proto.HighlightLines.AddRange(side.HighlightLines);
        proto.Points.AddRange(side.Points);
        return proto;
    }

    private static Contracts.CodeLocation ToProto(D.CodeLocation location) => new()
    {
        Path = location.Path,
        StartLine = location.StartLine,
        EndLine = location.EndLine,
        Symbol = location.Symbol ?? "",
    };
}
