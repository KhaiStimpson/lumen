namespace Lumen.App.ViewModels;

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
