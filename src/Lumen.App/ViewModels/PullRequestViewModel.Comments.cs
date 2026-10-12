using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Diff;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

/// <summary>A comment being written on a line or range of the diff (plan v2 M3). Lives until added, posted or cancelled.</summary>
public sealed partial class CommentDraftViewModel : ObservableObject
{
    public CommentDraftViewModel(string id, string path, LineSide side, int line, int? startLine, string? suggestionBase)
    {
        Id = id;
        Path = path;
        Side = side;
        Line = line;
        StartLine = startLine is { } s && s != line ? Math.Min(s, line) : null;
        if (StartLine is not null)
        {
            Line = Math.Max(startLine!.Value, line);
        }

        SuggestionBase = suggestionBase;
    }

    public string Id { get; }

    public string Path { get; }

    public LineSide Side { get; }

    public int Line { get; }

    public int? StartLine { get; }

    /// <summary>The selected lines' text, for "Suggest change" and its preview.</summary>
    public string? SuggestionBase { get; }

    public string RangeLabel => (StartLine is { } start
            ? string.Create(CultureInfo.InvariantCulture, $"Lines {start}–{Line}")
            : string.Create(CultureInfo.InvariantCulture, $"Line {Line}"))
        + (Side == LineSide.Left ? " (before the change)" : "");

    [ObservableProperty]
    public partial string Body { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPreview { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool CanSuggest => Side == LineSide.Right && SuggestionBase is not null;
}

/// <summary>Comment anywhere (plan v2 M3): a line or range, either side, added to the review or posted at once.</summary>
public sealed partial class PullRequestViewModel
{
    private int _draftIds;

    public ObservableCollection<CommentDraftViewModel> Drafts { get; } = [];

    /// <summary>Asks the view to focus a draft's composer once its card exists.</summary>
    public event EventHandler<CommentDraftViewModel>? DraftOpened;

    public CommentDraftViewModel? FindDraft(string cardId) => Drafts.FirstOrDefault(d => d.Id == cardId);

    /// <summary>
    /// Opens a composer under a line (or the end of a range) of the shown file. Lines outside the diff are refused,
    /// as GitHub would; an existing draft at the same place is reopened rather than duplicated.
    /// </summary>
    public CommentDraftViewModel? StartLineComment(LineSide side, int line, int? startLine = null)
    {
        if (SelectedFile is not { } file || CurrentDiff is not { } diff || !ScopeEndsAtHead)
        {
            Toast = !ScopeEndsAtHead ? "Comments attach to the latest commit: show all changes or a range ending at the head first." : null;
            return null;
        }

        if (!diff.Contains(side, line) || (startLine is { } start && !diff.Contains(side, start)))
        {
            Toast = "That line isn't part of the diff, so GitHub can't attach a comment there.";
            return null;
        }

        var existing = Drafts.FirstOrDefault(d => d.Path == file.Path && d.Side == side && d.Line == Math.Max(line, startLine ?? line));
        if (existing is not null)
        {
            DraftOpened?.Invoke(this, existing);
            return existing;
        }

        var draft = new CommentDraftViewModel(
            string.Create(CultureInfo.InvariantCulture, $"draft:{++_draftIds}"),
            file.Path,
            side,
            line,
            startLine,
            side == LineSide.Right ? LinesText(file.Path, Math.Min(line, startLine ?? line), Math.Max(line, startLine ?? line)) : null);
        Drafts.Add(draft);
        RebuildCurrentDiff();
        DraftOpened?.Invoke(this, draft);
        return draft;
    }

    /// <summary>Head-side text of lines <paramref name="from"/>..<paramref name="to"/> from the loaded diff.</summary>
    private string? LinesText(string path, int from, int to)
    {
        if (!_diffCache.TryGetValue(DiffKey(path), out var diff))
        {
            return null;
        }

        var lines = diff.Hunks.SelectMany(h => h.Lines)
            .Where(l => l.NewNumber >= from && l.NewNumber <= to)
            .DistinctBy(l => l.NewNumber)
            .OrderBy(l => l.NewNumber)
            .Select(l => l.Text)
            .ToList();
        return lines.Count == to - from + 1 ? string.Join('\n', lines) : null;
    }

    private IEnumerable<CardAnchor> DraftAnchors(string path) =>
        Drafts.Where(d => d.Path == path).Select(d => new CardAnchor(d.Id, d.Side, d.Line));

    [RelayCommand]
    public void CancelDraft(CommentDraftViewModel? draft)
    {
        if (draft is not null && Drafts.Remove(draft))
        {
            RebuildCurrentDiff();
        }
    }

    [RelayCommand]
    public static void SuggestChange(CommentDraftViewModel? draft)
    {
        if (draft?.SuggestionBase is not { } original)
        {
            return;
        }

        var block = $"```suggestion\n{original}\n```";
        draft.Body = string.IsNullOrWhiteSpace(draft.Body) ? block : $"{draft.Body.TrimEnd()}\n\n{block}";
    }

    [RelayCommand]
    public static void TogglePreview(CommentDraftViewModel? draft)
    {
        if (draft is not null)
        {
            draft.IsPreview = !draft.IsPreview;
        }
    }

    /// <summary>Ctrl+Enter: adds the comment to the pending review on GitHub (started if needed).</summary>
    [RelayCommand]
    public async Task AddDraftToReviewAsync(CommentDraftViewModel? draft)
    {
        if (draft is null || draft.IsBusy || string.IsNullOrWhiteSpace(draft.Body))
        {
            return;
        }

        draft.IsBusy = true;
        draft.Error = null;
        try
        {
            var pending = await _source.AddPendingCommentAsync(
                new AddPendingCommentRequest
                {
                    PullRequest = Ref,
                    Path = draft.Path,
                    Side = ToProto(draft.Side),
                    Line = draft.Line,
                    StartLine = draft.StartLine ?? 0,
                    StartSide = ToProto(draft.Side),
                    Body = draft.Body.Trim(),
                },
                _lifetime.Token).ConfigureAwait(true);
            ApplyPendingReview(pending);
            Drafts.Remove(draft);
            RebuildCurrentDiff();
            Toast = PendingToast;
        }
        catch (RpcException ex)
        {
            draft.Error = ex.Status.Detail;
        }
        finally
        {
            draft.IsBusy = false;
        }
    }

    /// <summary>Ctrl+Shift+Enter: posts the comment on its own, at once.</summary>
    [RelayCommand]
    public async Task PostDraftNowAsync(CommentDraftViewModel? draft)
    {
        if (draft is null || draft.IsBusy || string.IsNullOrWhiteSpace(draft.Body))
        {
            return;
        }

        draft.IsBusy = true;
        draft.Error = null;
        try
        {
            var reply = await _source.PostCommentAsync(
                new PostReviewCommentRequest
                {
                    PullRequest = Ref,
                    Path = draft.Path,
                    Side = ToProto(draft.Side),
                    Line = draft.Line,
                    StartLine = draft.StartLine ?? 0,
                    StartSide = ToProto(draft.Side),
                    Body = draft.Body.Trim(),
                },
                _lifetime.Token).ConfigureAwait(true);
            Drafts.Remove(draft);
            RebuildCurrentDiff();
            Toast = PostedToast(reply);
        }
        catch (RpcException ex)
        {
            draft.Error = ex.Status.Detail;
        }
        finally
        {
            draft.IsBusy = false;
        }
    }

    /// <summary>A review point's suggested comment, added to the pending review (Ctrl+Enter in its composer).</summary>
    [RelayCommand]
    public async Task AddPointCommentToReviewAsync(ReviewPointViewModel? point)
    {
        point ??= CurrentPoint;
        if (point is null || point.IsPosting || string.IsNullOrWhiteSpace(point.CommentDraft))
        {
            return;
        }

        point.IsPosting = true;
        point.PostError = null;
        try
        {
            var pending = await _source.AddPendingCommentAsync(
                new AddPendingCommentRequest
                {
                    PullRequest = Ref,
                    ReviewPointId = point.Id,
                    Path = point.Path,
                    Line = point.Line,
                    Body = point.CommentDraft.Trim(),
                },
                _lifetime.Token).ConfigureAwait(true);
            ApplyPendingReview(pending);
            point.State = ReviewPointState.Commented;
            point.IsComposing = false;
            RefreshPointDerivedState();
            Toast = PendingToast;
        }
        catch (RpcException ex)
        {
            point.PostError = ex.Status.Detail;
        }
        finally
        {
            point.IsPosting = false;
        }
    }

    [RelayCommand]
    public void SuggestChangeForPoint(ReviewPointViewModel? point)
    {
        point ??= CurrentPoint;
        if (point is null || LinesText(point.Path, point.Line, point.Line) is not { } original)
        {
            return;
        }

        var block = $"```suggestion\n{original}\n```";
        point.CommentDraft = string.IsNullOrWhiteSpace(point.CommentDraft) ? block : $"{point.CommentDraft.TrimEnd()}\n\n{block}";
    }

    private string PendingToast => PendingComments.Count == 1
        ? "Added to your review · 1 pending comment (R)"
        : string.Create(CultureInfo.InvariantCulture, $"Added to your review · {PendingComments.Count} pending comments (R)");

    private string PostedToast(PostReviewCommentReply reply)
    {
        if (reply.AddedToPendingReview)
        {
            ApplyPendingReview(reply.PendingReview);
            return "You have a pending review, so GitHub added it there. Submit the review to publish it.";
        }

        return "Comment posted to GitHub";
    }

    private static Contracts.DiffSide ToProto(LineSide side) => side == LineSide.Left ? Contracts.DiffSide.Left : Contracts.DiffSide.Right;

    private static LineSide FromProto(Contracts.DiffSide side) => side == Contracts.DiffSide.Left ? LineSide.Left : LineSide.Right;
}
