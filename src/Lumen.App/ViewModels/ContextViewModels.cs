using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

public enum PullRequestTab
{
    Files,
    Conversation,
    Commits,
    Checks,
    Map,
}

/// <summary>Which changes the Files tab shows: a commit, a range, or everything since the last review (plan v2 M2).</summary>
public sealed record DiffScope(string FromSha, string ToSha, string Label, bool IsSinceLastReview)
{
    public string Key => $"{FromSha}..{ToSha}";

    public string? Note { get; init; }
}

public sealed class TimelineItemViewModel(TimelineItem item, DateTimeOffset now)
{
    public TimelineItem Model { get; } = item;

    public string Author => Model.Author;

    public string Initial => Model.Author.Length == 0 ? "?" : Model.Author[..1].ToUpperInvariant();

    public string Body => Model.Body;

    public bool HasBody => Model.Body.Trim().Length > 0;

    public string AgeLabel => InboxItemViewModel.Age(Model.CreatedAt?.ToDateTimeOffset() ?? now, now);

    public bool IsReview => Model.Kind == TimelineItemKind.Review;

    public string ActionLabel => Model.Kind == TimelineItemKind.Comment ? "commented" : Model.Verdict switch
    {
        ReviewVerdict.Approved => "approved",
        ReviewVerdict.ChangesRequested => "requested changes",
        ReviewVerdict.Dismissed => "reviewed (dismissed)",
        _ => "reviewed",
    };

    /// <summary>"approved", "changes" or "" for the verdict pill.</summary>
    public string VerdictKind => !IsReview ? "" : Model.Verdict switch
    {
        ReviewVerdict.Approved => "approved",
        ReviewVerdict.ChangesRequested => "changes",
        _ => "",
    };

    public string CommentCountLabel => Model.ReviewCommentCount switch
    {
        0 => "",
        1 => "1 inline comment",
        var n => string.Create(CultureInfo.InvariantCulture, $"{n} inline comments"),
    };

    public string Url => Model.Url;
}

public sealed partial class CommitViewModel(CommitInfo commit, int index, DateTimeOffset now) : ObservableObject
{
    public CommitInfo Model { get; } = commit;

    public int Index { get; } = index;

    public string ShortSha => Model.Sha.Length > 7 ? Model.Sha[..7] : Model.Sha;

    public string Headline => Model.Headline;

    public string Author => Model.Author;

    public string AgeLabel => InboxItemViewModel.Age(Model.AuthoredAt?.ToDateTimeOffset() ?? now, now);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public sealed class CheckViewModel(CheckRun check)
{
    public CheckRun Model { get; } = check;

    public string Name => Model.Name;

    public string StateKind => Model.State switch
    {
        CheckState.Success => "success",
        CheckState.Failure => "failure",
        CheckState.Pending => "pending",
        _ => "",
    };

    public string StateLabel => Model.State switch
    {
        CheckState.Success => "Passed",
        CheckState.Failure => Model.Detail is { Length: > 0 } d && d != "FAILURE" ? $"Failed · {Pretty(d)}" : "Failed",
        CheckState.Pending => Model.Detail is { Length: > 0 } p ? Pretty(p) : "Pending",
        CheckState.Skipped => "Skipped",
        CheckState.Cancelled => "Cancelled",
        _ => "Neutral",
    };

    public string DurationLabel
    {
        get
        {
            if (Model.StartedAt is not { } start || Model.CompletedAt is not { } end)
            {
                return "";
            }

            var duration = end.ToDateTimeOffset() - start.ToDateTimeOffset();
            return duration.TotalMinutes >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes}m {duration.Seconds}s")
                : string.Create(CultureInfo.InvariantCulture, $"{duration.Seconds}s");
        }
    }

    public string Url => Model.Url;

    public bool HasUrl => Model.Url.Length > 0;

    public string KindLabel => Model.IsStatus ? "Status" : "Check";

    private static string Pretty(string raw) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Replace('_', ' ').ToLowerInvariant());
}

public sealed class ReviewerViewModel(Reviewer reviewer)
{
    public string Login => reviewer.Login;

    public string Initial => reviewer.Login.Length == 0 ? "?" : reviewer.Login[..1].ToUpperInvariant();

    public string StateLabel => !reviewer.HasVerdict ? (reviewer.IsRequested ? "Requested" : "") : reviewer.Verdict switch
    {
        ReviewVerdict.Approved => "Approved",
        ReviewVerdict.ChangesRequested => "Changes requested",
        ReviewVerdict.Dismissed => "Dismissed",
        _ => "Commented",
    };

    public string StateKind => !reviewer.HasVerdict ? "" : reviewer.Verdict switch
    {
        ReviewVerdict.Approved => "approved",
        ReviewVerdict.ChangesRequested => "changes",
        _ => "",
    };
}

public sealed class LabelViewModel(Label label)
{
    public string Name => label.Name;

    public string Color => label.Color.Length == 6 ? $"#{label.Color}" : "#888888";
}
