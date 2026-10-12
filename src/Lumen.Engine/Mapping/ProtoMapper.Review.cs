using Google.Protobuf.WellKnownTypes;
using Lumen.Contracts;
using Lumen.Domain;
using D = Lumen.Domain;

namespace Lumen.Engine.Mapping;

/// <summary>Mapping for the review client (plan v2): inbox, details, threads and pending reviews.</summary>
public static partial class ProtoMapper
{
    public static Contracts.InboxItem ToProto(D.InboxItem item) => new()
    {
        PullRequest = ToProto(item.Key),
        Title = item.Title,
        Author = item.Author,
        CreatedAt = Timestamp.FromDateTimeOffset(item.CreatedAt),
        UpdatedAt = Timestamp.FromDateTimeOffset(item.UpdatedAt),
        Additions = item.Additions,
        Deletions = item.Deletions,
        ChangedFiles = item.ChangedFiles,
        IsDraft = item.IsDraft,
        Checks = (Contracts.InboxCheckState)item.Checks,
        Review = (Contracts.InboxReviewState)item.Review,
        Url = item.Url,
        HeadSha = item.HeadSha,
    };

    public static Contracts.DiffSide ToProto(D.DiffSide side) => side == D.DiffSide.Left ? Contracts.DiffSide.Left : Contracts.DiffSide.Right;

    public static D.DiffSide FromProto(Contracts.DiffSide side) => side == Contracts.DiffSide.Left ? D.DiffSide.Left : D.DiffSide.Right;

    public static Contracts.ReviewVerdict ToProto(D.ReviewVerdict verdict) => verdict switch
    {
        D.ReviewVerdict.Approved => Contracts.ReviewVerdict.Approved,
        D.ReviewVerdict.ChangesRequested => Contracts.ReviewVerdict.ChangesRequested,
        D.ReviewVerdict.Dismissed => Contracts.ReviewVerdict.Dismissed,
        D.ReviewVerdict.Pending => Contracts.ReviewVerdict.Pending,
        _ => Contracts.ReviewVerdict.Commented,
    };

    public static Contracts.TimelineItem ToProto(D.TimelineItem item) => new()
    {
        Id = item.Id,
        Kind = item.Kind == D.TimelineItemKind.Review ? Contracts.TimelineItemKind.Review : Contracts.TimelineItemKind.Comment,
        Author = item.Author,
        Body = item.Body,
        CreatedAt = Timestamp.FromDateTimeOffset(item.CreatedAt),
        Url = item.Url,
        Verdict = ToProto(item.Verdict),
        ReviewCommentCount = item.ReviewCommentCount,
    };

    public static Contracts.CheckRun ToProto(CheckRunInfo check)
    {
        var proto = new Contracts.CheckRun
        {
            Name = check.Name,
            State = (Contracts.CheckState)check.State,
            Detail = check.Detail,
            Url = check.Url,
            IsStatus = check.IsStatus,
        };
        if (check.StartedAt is { } started)
        {
            proto.StartedAt = Timestamp.FromDateTimeOffset(started);
        }

        if (check.CompletedAt is { } completed)
        {
            proto.CompletedAt = Timestamp.FromDateTimeOffset(completed);
        }

        return proto;
    }

    public static Contracts.PullRequestDetails ToProto(D.PullRequestDetails details, string? lastReviewedSha, bool lastReviewedIsOpen)
    {
        var proto = new Contracts.PullRequestDetails
        {
            PullRequest = ToProto(details.Key),
            Title = details.Title,
            Author = details.Author,
            Body = details.Body,
            State = details.State,
            IsDraft = details.IsDraft,
            BaseRef = details.BaseRef,
            HeadRef = details.HeadRef,
            HeadSha = details.HeadSha,
            Mergeability = (Contracts.Mergeability)details.Mergeability,
            MergeState = details.MergeState,
            ViewerLogin = details.ViewerLogin,
            LastReviewedSha = lastReviewedSha ?? "",
            LastReviewedIsOpen = lastReviewedIsOpen,
            Url = details.Url,
            CreatedAt = Timestamp.FromDateTimeOffset(details.CreatedAt),
        };
        proto.Labels.AddRange(details.Labels.Select(l => new Contracts.Label { Name = l.Name, Color = l.Color }));
        proto.Reviewers.AddRange(details.Reviewers.Select(r => new Contracts.Reviewer
        {
            Login = r.Login,
            IsTeam = r.IsTeam,
            IsRequested = r.IsRequested,
            HasVerdict = r.Verdict is not null,
            Verdict = r.Verdict is { } verdict ? ToProto(verdict) : Contracts.ReviewVerdict.Commented,
        }));
        proto.Timeline.AddRange(details.Timeline.Select(ToProto));
        proto.Commits.AddRange(details.Commits.Select(c => new Contracts.CommitInfo
        {
            Sha = c.Sha,
            Headline = c.Headline,
            Message = c.Message,
            Author = c.Author,
            AuthoredAt = Timestamp.FromDateTimeOffset(c.AuthoredAt),
        }));
        proto.Checks.AddRange(details.Checks.Select(ToProto));
        return proto;
    }

    public static Contracts.ReviewThread ToProto(D.ReviewThread thread)
    {
        var proto = new Contracts.ReviewThread
        {
            Id = thread.Id,
            Path = thread.Path,
            Side = ToProto(thread.Side),
            Line = thread.Line ?? 0,
            StartLine = thread.StartLine ?? 0,
            OriginalLine = thread.OriginalLine ?? 0,
            OriginalStartLine = thread.OriginalStartLine ?? 0,
            OriginalCommitSha = thread.OriginalCommitSha ?? "",
            IsResolved = thread.IsResolved,
            IsOutdated = thread.IsOutdated,
            CanReply = thread.CanReply,
            CanResolve = thread.CanResolve,
            CanUnresolve = thread.CanUnresolve,
            DiffHunk = thread.DiffHunk ?? "",
        };
        proto.Comments.AddRange(thread.Comments.Select(ToProto));
        return proto;
    }

    public static Contracts.ThreadComment ToProto(D.ThreadComment comment) => new()
    {
        Id = comment.Id,
        DatabaseId = comment.DatabaseId,
        Author = comment.Author,
        Body = comment.Body,
        CreatedAt = Timestamp.FromDateTimeOffset(comment.CreatedAt),
        Url = comment.Url,
        IsPending = comment.IsPending,
    };

    public static ThreadsUpdated ToProto(IEnumerable<D.ReviewThread> threads)
    {
        var proto = new ThreadsUpdated();
        proto.Threads.AddRange(threads.Select(ToProto));
        return proto;
    }

    /// <param name="reviewPoints">Pending comment id → the review point it was written for.</param>
    public static Contracts.PendingReview ToProto(D.PendingReview? review, IReadOnlyDictionary<string, string>? reviewPoints = null)
    {
        var proto = new Contracts.PendingReview();
        if (review is null)
        {
            return proto;
        }

        proto.Id = review.Id;
        proto.Body = review.Body;
        proto.Comments.AddRange(review.Comments.Select(c => new Contracts.PendingComment
        {
            Id = c.Id,
            ThreadId = c.ThreadId ?? "",
            Path = c.Path,
            Side = ToProto(c.Side),
            Line = c.Line,
            StartLine = c.StartLine ?? 0,
            Body = c.Body,
            IsReply = c.IsReply,
            ReviewPointId = reviewPoints?.GetValueOrDefault(c.Id) ?? "",
        }));
        return proto;
    }

    public static ChangedFileSummary ToSummary(ChangedFile f, bool viewed) => new()
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
}
