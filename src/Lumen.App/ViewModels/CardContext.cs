namespace Lumen.App.ViewModels;

/// <summary>What an inline card needs: the review point, its pull request (for commands), and where it is shown.</summary>
public sealed class CardContext(PullRequestViewModel pullRequest, ReviewPointViewModel point, bool isPrimary)
{
    public PullRequestViewModel PullRequest { get; } = pullRequest;

    public ReviewPointViewModel Point { get; } = point;

    public bool IsPrimary { get; } = isPrimary;

    public bool IsReference => !IsPrimary;
}
