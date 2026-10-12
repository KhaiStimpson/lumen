namespace Lumen.Domain;

// The review client (plan v2): inbox, conversation, threads and pending reviews as GitHub holds them.

/// <summary>Which side of a diff a comment sits on. Right is the head (added and unchanged lines).</summary>
public enum DiffSide
{
    Right,
    Left,
}

public sealed record ThreadComment
{
    /// <summary>GraphQL node id.</summary>
    public required string Id { get; init; }

    public long DatabaseId { get; init; }

    public required string Author { get; init; }

    public required string Body { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string Url { get; init; } = "";

    /// <summary>The review this comment belongs to (GraphQL id).</summary>
    public string? ReviewId { get; init; }

    /// <summary>Part of the viewer's pending review: not visible to anyone else yet.</summary>
    public bool IsPending { get; init; }
}

/// <summary>A review comment thread anchored to a line (or range) of one file (GitHub's <c>PullRequestReviewThread</c>).</summary>
public sealed record ReviewThread
{
    public required string Id { get; init; }

    public required string Path { get; init; }

    public DiffSide Side { get; init; }

    /// <summary>Line at the head; null when the thread is outdated (its line no longer exists).</summary>
    public int? Line { get; init; }

    public int? StartLine { get; init; }

    /// <summary>Line in the commit the thread was written against.</summary>
    public int? OriginalLine { get; init; }

    public int? OriginalStartLine { get; init; }

    public string? OriginalCommitSha { get; init; }

    public bool IsResolved { get; init; }

    public bool IsOutdated { get; init; }

    public required IReadOnlyList<ThreadComment> Comments { get; init; }

    public bool CanReply { get; init; } = true;

    public bool CanResolve { get; init; }

    public bool CanUnresolve { get; init; }

    public string? DiffHunk { get; init; }

    /// <summary>Every comment belongs to the viewer's pending review: the thread is a draft.</summary>
    public bool IsPendingOnly => Comments.Count > 0 && Comments.All(c => c.IsPending);
}

public sealed record PendingComment
{
    public required string Id { get; init; }

    public string? ThreadId { get; init; }

    public required string Path { get; init; }

    public DiffSide Side { get; init; }

    public int Line { get; init; }

    public int? StartLine { get; init; }

    public required string Body { get; init; }

    public bool IsReply { get; init; }
}

/// <summary>The viewer's pending (draft) review. It lives on GitHub, so it survives restarts and matches github.com.</summary>
public sealed record PendingReview
{
    public required string Id { get; init; }

    public string Body { get; init; } = "";

    public IReadOnlyList<PendingComment> Comments { get; init; } = [];
}

/// <summary>Review threads and the viewer's pending review, read together in one request.</summary>
public sealed record ReviewThreadsResult(IReadOnlyList<ReviewThread> Threads, PendingReview? PendingReview);

/// <summary>A new review comment. <see cref="StartLine"/> makes it a range (GitHub's multi-line comments).</summary>
public sealed record NewReviewComment(
    PullRequestKey Key,
    string CommitSha,
    string Path,
    int Line,
    string Body,
    DiffSide Side = DiffSide.Right,
    int? StartLine = null,
    DiffSide? StartSide = null);

public enum ReviewEvent
{
    Comment,
    Approve,
    RequestChanges,
}

public enum ReviewVerdict
{
    Commented,
    Approved,
    ChangesRequested,
    Dismissed,
    Pending,
}

public sealed record SubmittedReview(string Id, string Url);

// ---- Inbox ----

public enum InboxCheckState
{
    None,
    Pending,
    Success,
    Failure,
}

public enum InboxReviewState
{
    Awaiting,
    Approved,
    ChangesRequested,
    None,
}

public sealed record InboxItem
{
    public required PullRequestKey Key { get; init; }

    public required string Title { get; init; }

    public required string Author { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public int Additions { get; init; }

    public int Deletions { get; init; }

    public int ChangedFiles { get; init; }

    public bool IsDraft { get; init; }

    public InboxCheckState Checks { get; init; }

    public InboxReviewState Review { get; init; }

    public string Url { get; init; } = "";

    public string HeadSha { get; init; } = "";
}

/// <summary>One GitHub search, e.g. <c>is:open is:pr review-requested:@me</c>.</summary>
public sealed record InboxQuery(string Id, string Search);

public sealed record InboxSearchResult(string ViewerLogin, IReadOnlyDictionary<string, IReadOnlyList<InboxItem>> Results);

// ---- Pull request details ----

public sealed record PullRequestLabel(string Name, string Color);

public sealed record PullRequestReviewer(string Login, bool IsTeam, bool IsRequested, ReviewVerdict? Verdict, string? ReviewedAtSha = null);

public enum TimelineItemKind
{
    Comment,
    Review,
}

public sealed record TimelineItem
{
    public required string Id { get; init; }

    public required TimelineItemKind Kind { get; init; }

    public required string Author { get; init; }

    public required string Body { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string Url { get; init; } = "";

    public ReviewVerdict Verdict { get; init; }

    public int ReviewCommentCount { get; init; }

    /// <summary>The head commit a review was submitted at.</summary>
    public string? CommitSha { get; init; }
}

public sealed record CommitInfo(string Sha, string Headline, string Message, string Author, DateTimeOffset AuthoredAt);

public enum CheckState
{
    Pending,
    Success,
    Failure,
    Neutral,
    Skipped,
    Cancelled,
}

public sealed record CheckRunInfo(
    string Name,
    CheckState State,
    string Detail,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string Url,
    bool IsStatus);

public enum Mergeability
{
    Unknown,
    Mergeable,
    Conflicting,
}

/// <summary>Everything around the diff: description, conversation, commits, checks, reviewers and labels.</summary>
public sealed record PullRequestDetails
{
    public required PullRequestKey Key { get; init; }

    public required string Title { get; init; }

    public required string Author { get; init; }

    public string Body { get; init; } = "";

    public string State { get; init; } = "open";

    public bool IsDraft { get; init; }

    public string BaseRef { get; init; } = "";

    public string HeadRef { get; init; } = "";

    public string HeadSha { get; init; } = "";

    public string Url { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public Mergeability Mergeability { get; init; }

    public string MergeState { get; init; } = "";

    public IReadOnlyList<PullRequestLabel> Labels { get; init; } = [];

    public IReadOnlyList<PullRequestReviewer> Reviewers { get; init; } = [];

    public IReadOnlyList<TimelineItem> Timeline { get; init; } = [];

    public IReadOnlyList<CommitInfo> Commits { get; init; } = [];

    public IReadOnlyList<CheckRunInfo> Checks { get; init; } = [];

    public string ViewerLogin { get; init; } = "";

    /// <summary>The head at the viewer's latest submitted review on GitHub, if they have reviewed.</summary>
    public string? ViewerLastReviewedSha { get; init; }
}

/// <summary>When the viewer last opened and last reviewed a pull request in Lumen (TDD §32; plan v2 M1, M2, M5).</summary>
public sealed record PullRequestVisit(
    PullRequestKey Key,
    DateTimeOffset? LastOpenedAt,
    string? LastOpenedHead,
    DateTimeOffset? LastReviewedAt,
    string? LastReviewedHead);

public interface IPullRequestVisitStore
{
    Task RecordOpenedAsync(PullRequestKey key, string headSha, DateTimeOffset at, CancellationToken cancellationToken);

    Task RecordReviewedAsync(PullRequestKey key, string headSha, DateTimeOffset at, CancellationToken cancellationToken);

    Task<PullRequestVisit?> GetVisitAsync(PullRequestKey key, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<PullRequestKey, PullRequestVisit>> GetVisitsAsync(CancellationToken cancellationToken);
}

/// <summary>A changed file between two commits of a pull request, plus whether history was rewritten in between.</summary>
public sealed record DiffRangeResult(IReadOnlyList<ChangedFile> Files, bool IsForcePush, string? Note);

/// <summary>
/// Walkthroughs cached per head commit, scope ("full" or "since:{sha}") and settings hash, so reopening a pull request
/// costs nothing (plan v2 M8). The result is stored as the engine serialised it.
/// </summary>
public interface IWalkthroughStore
{
    Task<string?> FindWalkthroughAsync(PullRequestKey key, string headSha, string scope, string settingsHash, CancellationToken cancellationToken);

    Task SaveWalkthroughAsync(PullRequestKey key, string headSha, string scope, string settingsHash, string result, CancellationToken cancellationToken);
}
