using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Services;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

/// <summary>Pull request context (plan v2 M2): conversation, commits, checks, and limiting the diff to a range.</summary>
public sealed partial class PullRequestViewModel
{
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilesTab), nameof(IsConversationTab), nameof(IsCommitsTab), nameof(IsChecksTab))]
    public partial PullRequestTab Tab { get; set; }

    public bool IsFilesTab => Tab == PullRequestTab.Files;

    public bool IsConversationTab => Tab == PullRequestTab.Conversation;

    public bool IsCommitsTab => Tab == PullRequestTab.Commits;

    public bool IsChecksTab => Tab == PullRequestTab.Checks;

    [RelayCommand]
    public void ShowTab(PullRequestTab tab)
    {
        Tab = tab;
        if (tab != PullRequestTab.Files)
        {
            Mode = ReviewMode.Diff;
        }
    }

    // Header ---------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails), nameof(Description), nameof(HasDescription), nameof(MergeabilityLabel),
        nameof(MergeabilityKind), nameof(BaseRef), nameof(HeadRef), nameof(LastReviewedLabel), nameof(CanShowSinceLastReview))]
    public partial PullRequestDetails? Details { get; private set; }

    public bool HasDetails => Details is not null;

    public string Description => Details?.Body is { Length: > 0 } body ? body : _snapshotBody;

    private string _snapshotBody = "";

    public bool HasDescription => Description.Trim().Length > 0;

    public string BaseRef => Details?.BaseRef ?? "";

    public string HeadRef => Details?.HeadRef ?? "";

    public ObservableCollection<LabelViewModel> Labels { get; } = [];

    public ObservableCollection<ReviewerViewModel> Reviewers { get; } = [];

    public string MergeabilityLabel => Details is null ? "" : Details.Mergeability switch
    {
        Mergeability.Conflicting => "Has conflicts",
        Mergeability.Mergeable when Details.MergeState == "BLOCKED" => "Blocked by required reviews or checks",
        Mergeability.Mergeable when Details.MergeState == "BEHIND" => "Behind the base branch",
        Mergeability.Mergeable => "Ready to merge",
        _ => "Checking mergeability…",
    };

    /// <summary>"success", "failure" or "pending" for the status dot.</summary>
    public string MergeabilityKind => Details?.Mergeability switch
    {
        Mergeability.Conflicting => "failure",
        Mergeability.Mergeable when Details.MergeState is "BLOCKED" or "BEHIND" => "pending",
        Mergeability.Mergeable => "success",
        _ => "",
    };

    // Conversation ---------------------------------------------------------------------------------

    public ObservableCollection<TimelineItemViewModel> Timeline { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostIssueCommentCommand))]
    public partial string IssueCommentDraft { get; set; } = "";

    [ObservableProperty]
    public partial bool IsIssueCommentPreview { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostIssueCommentCommand))]
    public partial bool IsPostingIssueComment { get; private set; }

    [ObservableProperty]
    public partial string? IssueCommentError { get; private set; }

    /// <summary>A top-level comment, posted at once. Only ever on an explicit click or Ctrl+Enter.</summary>
    [RelayCommand(CanExecute = nameof(CanPostIssueComment))]
    public async Task PostIssueCommentAsync()
    {
        IsPostingIssueComment = true;
        IssueCommentError = null;
        try
        {
            var item = await _source.PostIssueCommentAsync(Ref, IssueCommentDraft.Trim(), _lifetime.Token).ConfigureAwait(true);
            if (Timeline.All(t => t.Model.Id != item.Id))
            {
                Timeline.Add(new TimelineItemViewModel(item, Now));
            }

            IssueCommentDraft = "";
            IsIssueCommentPreview = false;
            Toast = "Comment posted to GitHub";
        }
        catch (RpcException ex)
        {
            IssueCommentError = ex.Status.Detail;
        }
        finally
        {
            IsPostingIssueComment = false;
        }
    }

    private bool CanPostIssueComment() => !IsPostingIssueComment && IssueCommentDraft.Trim().Length > 0;

    private void ApplyTimeline(IEnumerable<TimelineItem> items)
    {
        Timeline.Clear();
        foreach (var item in items)
        {
            Timeline.Add(new TimelineItemViewModel(item, Now));
        }

        OnPropertyChanged(nameof(ConversationLabel));
    }

    public string ConversationLabel => Timeline.Count == 0 ? "Conversation" : string.Create(CultureInfo.InvariantCulture, $"Conversation · {Timeline.Count}");

    // Commits --------------------------------------------------------------------------------------

    public ObservableCollection<CommitViewModel> Commits { get; } = [];

    public string CommitsLabel => Commits.Count == 0 ? "Commits" : string.Create(CultureInfo.InvariantCulture, $"Commits · {Commits.Count}");

    private int? _rangeAnchor;

    /// <summary>Limits the Files tab to one commit, or with <paramref name="extend"/> to the range from the last pick.</summary>
    public async Task SelectCommitAsync(CommitViewModel commit, bool extend)
    {
        var first = extend && _rangeAnchor is { } anchor ? Math.Min(anchor, commit.Index) : commit.Index;
        var last = extend && _rangeAnchor is { } a ? Math.Max(a, commit.Index) : commit.Index;
        if (!extend)
        {
            _rangeAnchor = commit.Index;
        }

        foreach (var c in Commits)
        {
            c.IsSelected = c.Index >= first && c.Index <= last;
        }

        var from = first == 0 ? _mergeBaseSha : Commits[first - 1].Model.Sha;
        var to = Commits[last].Model.Sha;
        var label = first == last
            ? $"Commit {Commits[first].ShortSha} · {Commits[first].Headline}"
            : string.Create(CultureInfo.InvariantCulture, $"{last - first + 1} commits · {Commits[first].ShortSha}…{Commits[last].ShortSha}");
        ShowSinceLastReview = false;
        await ApplyScopeAsync(new DiffScope(from, to, label, IsSinceLastReview: false)).ConfigureAwait(true);
        ShowTab(PullRequestTab.Files);
    }

    [RelayCommand]
    private Task SelectCommitCommandAsync(CommitViewModel? commit) => commit is null ? Task.CompletedTask : SelectCommitAsync(commit, extend: false);

    // Since last review ----------------------------------------------------------------------------

    public string LastReviewedLabel => Details?.LastReviewedSha is not { Length: > 0 } sha
        ? "You haven't reviewed this pull request yet"
        : Details.LastReviewedIsOpen
            ? $"Since you last opened it ({Short(sha)})"
            : $"Since your last review ({Short(sha)})";

    public bool CanShowSinceLastReview => Details?.LastReviewedSha is { Length: > 0 } sha && sha != _headSha;

    /// <summary>Limits the diff to what changed after the viewer's last review (or last open).</summary>
    [ObservableProperty]
    public partial bool ShowSinceLastReview { get; set; }

    partial void OnShowSinceLastReviewChanged(bool value)
    {
        if (_applyingScope)
        {
            return;
        }

        if (value && Details?.LastReviewedSha is { Length: > 0 } sha)
        {
            _ = ApplyScopeAsync(new DiffScope(sha, _headSha, LastReviewedLabel, IsSinceLastReview: true));
            _ = RequestDeltaWalkthroughAsync(sha);
        }
        else if (!value && Scope?.IsSinceLastReview == true)
        {
            _ = ClearScopeAsync();
        }
    }

    // Scope ----------------------------------------------------------------------------------------

    private bool _applyingScope;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScope), nameof(ScopeLabel), nameof(ScopeNote), nameof(ScopeEndsAtHead))]
    public partial DiffScope? Scope { get; private set; }

    public bool HasScope => Scope is not null;

    public string ScopeLabel => Scope?.Label ?? "";

    public string? ScopeNote => Scope?.Note;

    /// <summary>Review points and threads are anchored at the head, so they show only when the scope ends there.</summary>
    public bool ScopeEndsAtHead => Scope is null || Scope.ToSha == _headSha;

    public ObservableCollection<FileEntryViewModel> ScopeFiles { get; } = [];

    private async Task ApplyScopeAsync(DiffScope scope)
    {
        try
        {
            var range = await _source.GetDiffRangeAsync(
                new GetDiffRangeRequest
                {
                    PullRequest = Ref,
                    Range = new CommitRange { FromSha = scope.FromSha, ToSha = scope.ToSha },
                    IgnoreWhitespace = IgnoreWhitespace,
                },
                _lifetime.Token).ConfigureAwait(true);

            ScopeFiles.Clear();
            foreach (var file in range.Files)
            {
                var entry = new FileEntryViewModel(file) { IsViewed = Files.FirstOrDefault(f => f.Path == file.Path)?.IsViewed ?? false };
                ScopeFiles.Add(entry);
            }

            _applyingScope = true;
            try
            {
                Scope = scope with { Note = range.Note.Length > 0 ? range.Note : null };
                ShowSinceLastReview = scope.IsSinceLastReview;
            }
            finally
            {
                _applyingScope = false;
            }

            RebuildTree();
            var first = ScopeFiles.FirstOrDefault(f => !f.IsMechanical) ?? ScopeFiles.FirstOrDefault();
            if (first is not null)
            {
                await SelectFileAsync(first).ConfigureAwait(true);
            }
            else
            {
                SelectedFile = null;
                CurrentDiff = null;
                Toast = "No changes in that range.";
            }
        }
        catch (RpcException ex)
        {
            Toast = $"Couldn't show those changes: {ex.Status.Detail}";
            _applyingScope = true;
            ShowSinceLastReview = false;
            _applyingScope = false;
        }
    }

    /// <summary>Back to the whole pull request.</summary>
    [RelayCommand]
    public async Task ClearScopeAsync()
    {
        if (Scope is null)
        {
            return;
        }

        _applyingScope = true;
        try
        {
            Scope = null;
            ShowSinceLastReview = false;
        }
        finally
        {
            _applyingScope = false;
        }

        foreach (var commit in Commits)
        {
            commit.IsSelected = false;
        }

        ScopeFiles.Clear();
        RebuildTree();
        var file = SelectedFile is { } current ? Files.FirstOrDefault(f => f.Path == current.Path) : null;
        file ??= Tree.SelectMany(n => n.Descendants().Prepend(n)).FirstOrDefault(n => n.File is not null)?.File;
        if (file is not null)
        {
            await SelectFileAsync(file).ConfigureAwait(true);
        }
    }

    // Checks ---------------------------------------------------------------------------------------

    public ObservableCollection<CheckViewModel> Checks { get; } = [];

    public string ChecksLabel => Checks.Count == 0 ? "Checks" : string.Create(CultureInfo.InvariantCulture, $"Checks · {Checks.Count(c => c.Model.State == CheckState.Success)}/{Checks.Count}");

    /// <summary>"success", "failure" or "pending" across all checks.</summary>
    public string ChecksSummaryKind => Checks.Count == 0 ? ""
        : Checks.Any(c => c.Model.State == CheckState.Failure) ? "failure"
        : Checks.Any(c => c.Model.State == CheckState.Pending) ? "pending"
        : "success";

    private void ApplyChecks(IEnumerable<CheckRun> checks)
    {
        Checks.Clear();
        foreach (var check in checks.OrderBy(c => c.State switch { CheckState.Failure => 0, CheckState.Pending => 1, _ => 2 }).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            Checks.Add(new CheckViewModel(check));
        }

        OnPropertyChanged(nameof(ChecksLabel));
        OnPropertyChanged(nameof(ChecksSummaryKind));
    }

    // New commits ----------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string? NewHeadLabel { get; private set; }

    private void ApplyHeadChanged(HeadChanged changed) =>
        NewHeadLabel = $"New commits were pushed ({Short(changed.OldSha)} → {Short(changed.NewSha)}). Reload to review them.";

    // Loading --------------------------------------------------------------------------------------

    private string _headSha = "";
    private string _mergeBaseSha = "";

    private async Task LoadDetailsAsync()
    {
        try
        {
            ApplyDetails(await _source.GetPullRequestDetailsAsync(Ref, refresh: false, _lifetime.Token).ConfigureAwait(true));
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            // Context is extra: the diff and review work without it.
            Status = $"{Status} · conversation unavailable";
        }
        catch (EngineUnavailableException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal void ApplyDetails(PullRequestDetails details)
    {
        Details = details;
        Labels.Clear();
        foreach (var label in details.Labels)
        {
            Labels.Add(new LabelViewModel(label));
        }

        Reviewers.Clear();
        foreach (var reviewer in details.Reviewers)
        {
            Reviewers.Add(new ReviewerViewModel(reviewer));
        }

        Commits.Clear();
        for (var i = 0; i < details.Commits.Count; i++)
        {
            Commits.Add(new CommitViewModel(details.Commits[i], i, Now));
        }

        OnPropertyChanged(nameof(CommitsLabel));
        if (Timeline.Count == 0)
        {
            ApplyTimeline(details.Timeline);
        }

        if (Checks.Count == 0)
        {
            ApplyChecks(details.Checks);
        }
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
