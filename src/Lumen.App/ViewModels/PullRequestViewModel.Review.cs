using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Diff;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

public sealed partial class PendingCommentViewModel(PendingComment comment) : ObservableObject
{
    public PendingComment Model { get; } = comment;

    public string Id => Model.Id;

    public string Path => Model.Path;

    public string FileName => System.IO.Path.GetFileName(Model.Path);

    public LineSide Side => Model.Side == Contracts.DiffSide.Left ? LineSide.Left : LineSide.Right;

    public int Line => Model.Line;

    public string LocationLabel => (Model.StartLine > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{FileName}:{Model.StartLine}–{Model.Line}")
            : string.Create(CultureInfo.InvariantCulture, $"{FileName}:{Model.Line}"))
        + (Model.IsReply ? " · reply" : "");

    public string Body => Model.Body;

    public bool IsForReviewPoint => Model.ReviewPointId.Length > 0;

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditBody { get; set; } = comment.Body;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }
}

/// <summary>Submit a review (plan v2 M5): the pending review on GitHub, its tray, and Comment / Approve / Request changes.</summary>
public sealed partial class PullRequestViewModel
{
    public ObservableCollection<PendingCommentViewModel> PendingComments { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingReview))]
    public partial string PendingReviewId { get; private set; } = "";

    public bool HasPendingReview => PendingReviewId.Length > 0;

    public string TrayLabel => PendingComments.Count switch
    {
        0 => "Review",
        1 => "Review · 1 pending",
        var n => string.Create(CultureInfo.InvariantCulture, $"Review · {n} pending"),
    };

    [ObservableProperty]
    public partial bool IsTrayOpen { get; set; }

    [ObservableProperty]
    public partial string ReviewSummary { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommentCommand), nameof(ApproveCommand), nameof(RequestChangesCommand))]
    public partial bool IsSubmitting { get; private set; }

    [ObservableProperty]
    public partial string? SubmitError { get; private set; }

    /// <summary>As on GitHub, an author can only comment on their own pull request.</summary>
    public bool CanApprove => !ViewerIsAuthor;

    public string ApproveHint => ViewerIsAuthor ? "You can't approve or request changes on your own pull request." : "";

    [RelayCommand]
    public void ToggleTray() => IsTrayOpen = !IsTrayOpen;

    private void ApplyPendingReview(PendingReview review)
    {
        PendingReviewId = review.Id;
        var editing = PendingComments.Where(c => c.IsEditing).ToDictionary(c => c.Id, c => c.EditBody, StringComparer.Ordinal);
        PendingComments.Clear();
        foreach (var comment in review.Comments)
        {
            var item = new PendingCommentViewModel(comment);
            if (editing.TryGetValue(comment.Id, out var body))
            {
                item.IsEditing = true;
                item.EditBody = body;
            }

            PendingComments.Add(item);
        }

        if (review.Body.Length > 0 && ReviewSummary.Length == 0)
        {
            ReviewSummary = review.Body;
        }

        OnPropertyChanged(nameof(TrayLabel));
    }

    [RelayCommand]
    public Task JumpToPendingAsync(PendingCommentViewModel? comment) =>
        comment is null ? Task.CompletedTask : RevealAsync(comment.Path, comment.Side, comment.Line, Threads.FirstOrDefault(t => t.Model.Comments.Any(c => c.Id == comment.Id))?.CardId);

    [RelayCommand]
    public static void EditPending(PendingCommentViewModel? comment)
    {
        if (comment is not null)
        {
            comment.EditBody = comment.Body;
            comment.IsEditing = true;
        }
    }

    [RelayCommand]
    public static void CancelEditPending(PendingCommentViewModel? comment)
    {
        if (comment is not null)
        {
            comment.IsEditing = false;
        }
    }

    [RelayCommand]
    public async Task SavePendingAsync(PendingCommentViewModel? comment)
    {
        if (comment is null || comment.IsBusy || string.IsNullOrWhiteSpace(comment.EditBody))
        {
            return;
        }

        comment.IsBusy = true;
        try
        {
            comment.IsEditing = false;
            ApplyPendingReview(await _source.UpdatePendingCommentAsync(Ref, comment.Id, comment.EditBody.Trim(), _lifetime.Token).ConfigureAwait(true));
        }
        catch (RpcException ex)
        {
            comment.IsEditing = true;
            SubmitError = ex.Status.Detail;
        }
        finally
        {
            comment.IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task DeletePendingAsync(PendingCommentViewModel? comment)
    {
        if (comment is null || comment.IsBusy)
        {
            return;
        }

        comment.IsBusy = true;
        try
        {
            ApplyPendingReview(await _source.DeletePendingCommentAsync(Ref, comment.Id, _lifetime.Token).ConfigureAwait(true));
        }
        catch (RpcException ex)
        {
            SubmitError = ex.Status.Detail;
        }
        finally
        {
            comment.IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    public Task SubmitCommentAsync() => SubmitAsync(ReviewEvent.Comment);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    public Task ApproveAsync() => SubmitAsync(ReviewEvent.Approve);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    public Task RequestChangesAsync() => SubmitAsync(ReviewEvent.RequestChanges);

    private bool CanSubmit() => !IsSubmitting;

    /// <summary>Submits the pending review. Only ever on an explicit click.</summary>
    private async Task SubmitAsync(ReviewEvent reviewEvent)
    {
        if (reviewEvent != ReviewEvent.Comment && ViewerIsAuthor)
        {
            SubmitError = ApproveHint;
            return;
        }

        IsSubmitting = true;
        SubmitError = null;
        try
        {
            await _source.SubmitReviewAsync(
                new SubmitReviewRequest { PullRequest = Ref, Event = reviewEvent, Body = ReviewSummary.Trim() },
                _lifetime.Token).ConfigureAwait(true);
            ReviewSummary = "";
            ApplyPendingReview(new PendingReview());
            IsTrayOpen = false;
            Toast = reviewEvent switch
            {
                ReviewEvent.Approve => "Approved on GitHub",
                ReviewEvent.RequestChanges => "Changes requested on GitHub",
                _ => "Review submitted to GitHub",
            };

            // The review is now "since last review"'s baseline.
            try
            {
                ApplyDetails(await _source.GetPullRequestDetailsAsync(Ref, refresh: true, _lifetime.Token).ConfigureAwait(true));
            }
            catch (RpcException)
            {
            }
        }
        catch (RpcException ex)
        {
            SubmitError = ex.Status.Detail;
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private void ResetReviewClientState()
    {
        Drafts.Clear();
        Threads.Clear();
        CurrentThread = null;
        PendingComments.Clear();
        PendingReviewId = "";
        Walkthrough = null;
        DeltaWalkthrough = null;
        CurrentGroup = null;
        OnPropertyChanged(nameof(TrayLabel));
    }
}
