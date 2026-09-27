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

    public static Contracts.PullRequestSnapshot ToProto(D.PullRequestSnapshot snapshot, string viewer)
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

        proto.Files.AddRange(snapshot.Files.Select(f => new ChangedFileSummary
        {
            Path = f.Path,
            OldPath = f.OldPath ?? "",
            Kind = (Contracts.FileChangeKind)f.Kind,
            Additions = f.Additions,
            Deletions = f.Deletions,
            IsBinary = f.IsBinary,
            IsMechanical = f.Mechanical.IsMechanical,
            MechanicalReason = f.Mechanical.Reason ?? "",
        }));
        return proto;
    }

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
