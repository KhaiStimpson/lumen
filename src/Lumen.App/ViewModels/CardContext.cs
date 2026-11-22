namespace Lumen.App.ViewModels;

/// <summary>What an inline card needs: the review point, its pull request (for commands), and where it is shown.</summary>
public sealed class CardContext(PullRequestViewModel pullRequest, ReviewPointViewModel point, bool isPrimary)
{
    public PullRequestViewModel PullRequest { get; } = pullRequest;

    public ReviewPointViewModel Point { get; } = point;

    public bool IsPrimary { get; } = isPrimary;

    public bool IsReference => !IsPrimary;
}

/// <summary>An inline comment composer under a line (plan v2 M3).</summary>
public sealed class DraftCardContext(PullRequestViewModel pullRequest, CommentDraftViewModel draft)
{
    public PullRequestViewModel PullRequest { get; } = pullRequest;

    public CommentDraftViewModel Draft { get; } = draft;
}

/// <summary>An existing review thread under its line (plan v2 M4).</summary>
public sealed class ThreadCardContext(PullRequestViewModel pullRequest, ThreadViewModel thread)
{
    public PullRequestViewModel PullRequest { get; } = pullRequest;

    public ThreadViewModel Thread { get; } = thread;
}
