using Lumen.Analysis;
using Lumen.Domain;

namespace Lumen.Engine.Sessions;

/// <summary>Why a review action was refused; becomes an RPC status in the service.</summary>
public sealed class ReviewActionException(ReviewActionError error, string message) : Exception(message)
{
    public ReviewActionError Error { get; } = error;
}

public enum ReviewActionError
{
    Loading,
    Invalid,
    NotFound,
    GitHub,
}

/// <summary>
/// The review client's actions on an open pull request (plan v2 M2–M5): ranges, comments, threads and the pending
/// review. Every write here follows an explicit reviewer action; the engine never posts on its own.
/// </summary>
public sealed partial class ReviewActions(
    IGitHubClient gitHub,
    PullRequestSessionManager sessions,
    ReviewSettingsStore reviewSettings,
    IPullRequestVisitStore visits,
    TimeProvider time,
    ILogger<ReviewActions> logger)
{
    // ---------------------------------------------------------------- M2: context

    /// <summary>The head at the viewer's last review (GitHub, then Lumen), else at their last open, and which it was.</summary>
    public async Task<(string? Sha, bool IsOpen)> LastReviewedAsync(PullRequestSession session, CancellationToken ct)
    {
        if (session.Details?.ViewerLastReviewedSha is { Length: > 0 } fromGitHub)
        {
            return (fromGitHub, false);
        }

        var visit = await visits.GetVisitAsync(session.Key, ct).ConfigureAwait(false);
        if (visit?.LastReviewedHead is { Length: > 0 } reviewed)
        {
            return (reviewed, false);
        }

        return session.PreviousVisit?.LastOpenedHead is { Length: > 0 } opened ? (opened, true) : (null, false);
    }

    public async Task<DiffRangeResult> GetRangeAsync(PullRequestSession session, string fromSha, string toSha, bool ignoreWhitespace, CancellationToken ct)
    {
        var checkout = Checkout(session);
        if (session.Ranges.TryGetValue((fromSha, toSha, ignoreWhitespace), out var cached))
        {
            return cached;
        }

        try
        {
            if (!await checkout.EnsureCommitAsync(toSha, ct).ConfigureAwait(false))
            {
                throw new ReviewActionException(ReviewActionError.NotFound, $"Commit {Short(toSha)} isn't available any more.");
            }

            if (!await checkout.EnsureCommitAsync(fromSha, ct).ConfigureAwait(false))
            {
                throw new ReviewActionException(
                    ReviewActionError.NotFound,
                    $"Commit {Short(fromSha)} is gone from GitHub (history was rewritten), so there's nothing to compare with.");
            }

            var isForcePush = !await checkout.IsAncestorAsync(fromSha, toSha, ct).ConfigureAwait(false);
            var diff = await checkout.GetRangeDiffAsync(fromSha, toSha, ignoreWhitespace, null, ct).ConfigureAwait(false);
            var files = ChangedFiles.FromUnifiedDiff(diff, reviewSettings.Resolve(session.Key.Repository).MechanicalPaths);
            var result = new DiffRangeResult(
                files,
                isForcePush,
                isForcePush ? $"History was rewritten since {Short(fromSha)}; comparing file contents." : null);
            session.Ranges[(fromSha, toSha, ignoreWhitespace)] = result;
            return result;
        }
        catch (ArgumentException ex)
        {
            throw new ReviewActionException(ReviewActionError.Invalid, ex.Message);
        }
    }

    /// <summary>The whole pull request's diff for one file with whitespace changes ignored (<c>git diff -w</c>).</summary>
    public async Task<ChangedFile?> GetWhitespaceInsensitiveFileAsync(PullRequestSession session, string path, CancellationToken ct)
    {
        var checkout = Checkout(session);
        if (session.WhitespaceDiffs.TryGetValue(path, out var cached))
        {
            return cached;
        }

        // A renamed file needs both paths: limited to the new one, git can't see the rename and reports a whole new file.
        var original = session.Snapshot?.FindFile(path);
        string[] paths = original?.OldPath is { Length: > 0 } oldPath && oldPath != path ? [oldPath, path] : [path];
        var diff = await checkout.GetRangeDiffAsync(checkout.MergeBaseSha, checkout.HeadSha, true, paths, ct).ConfigureAwait(false);
        var file = ChangedFiles.FromUnifiedDiff(diff, reviewSettings.Resolve(session.Key.Repository).MechanicalPaths).FirstOrDefault(f => f.Path == path)
            ?? (original is null ? null : original with { Hunks = [], Additions = 0, Deletions = 0 });
        if (file is not null)
        {
            session.WhitespaceDiffs[path] = file;
        }

        return file;
    }

    public async Task<TimelineItem> PostIssueCommentAsync(PullRequestSession session, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ReviewActionException(ReviewActionError.Invalid, "Comment body is empty.");
        }

        var item = await GitHubAsync(() => gitHub.PostIssueCommentAsync(session.Key, body.Trim(), ct)).ConfigureAwait(false);
        if (session.Details is { } details)
        {
            session.SetDetails(details with { Timeline = [.. details.Timeline, item] });
        }

        return item;
    }

    // ---------------------------------------------------------------- M3: comments

    /// <summary>
    /// Posts a comment at once. When the viewer has a pending review GitHub refuses single comments, so the comment is
    /// added to the pending review instead and the caller is told.
    /// </summary>
    public async Task<(PostedComment? Posted, PendingReview? Pending)> PostNowAsync(
        PullRequestSession session,
        NewReviewComment comment,
        string? reviewPointId,
        CancellationToken ct)
    {
        ValidateLines(session, comment);
        if (session.PendingReview is not null)
        {
            return (null, await AddPendingAsync(session, comment, reviewPointId, null, ct).ConfigureAwait(false));
        }

        try
        {
            var posted = await GitHubAsync(() => gitHub.PostReviewCommentAsync(comment, ct)).ConfigureAwait(false);
            _ = Task.Run(() => sessions.RefreshReviewStateAsync(session, session.Cancellation), CancellationToken.None);
            return (posted, null);
        }
        catch (PendingReviewExistsException)
        {
            // One started on github.com since we last looked.
            await sessions.RefreshReviewStateAsync(session, ct).ConfigureAwait(false);
            return (null, await AddPendingAsync(session, comment, reviewPointId, null, ct).ConfigureAwait(false));
        }
    }

    /// <summary>Adds a new thread (or a reply) to the viewer's pending review, starting one if needed.</summary>
    public async Task<PendingReview> AddPendingAsync(
        PullRequestSession session,
        NewReviewComment comment,
        string? reviewPointId,
        string? replyToThreadId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(comment.Body))
        {
            throw new ReviewActionException(ReviewActionError.Invalid, "Comment body is empty.");
        }

        if (replyToThreadId is null)
        {
            ValidateLines(session, comment);
        }

        await session.ReviewGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var reviewId = await EnsurePendingReviewAsync(session, ct).ConfigureAwait(false);
            string commentId;
            if (replyToThreadId is not null)
            {
                commentId = (await GitHubAsync(() => gitHub.ReplyToThreadAsync(replyToThreadId, comment.Body.Trim(), reviewId, ct)).ConfigureAwait(false)).Id;
            }
            else
            {
                commentId = await GitHubAsync(() => gitHub.AddPendingThreadAsync(reviewId, comment with { Body = comment.Body.Trim() }, ct)).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(reviewPointId))
            {
                session.PendingPoints[commentId] = reviewPointId;
            }
        }
        finally
        {
            session.ReviewGate.Release();
        }

        await sessions.RefreshReviewStateAsync(session, ct).ConfigureAwait(false);
        return session.PendingReview ?? throw new ReviewActionException(ReviewActionError.GitHub, "GitHub didn't keep the pending review.");
    }

    private async Task<string> EnsurePendingReviewAsync(PullRequestSession session, CancellationToken ct)
    {
        if (session.PendingReview is { } pending)
        {
            return pending.Id;
        }

        // Pick up one started on github.com before starting another (GitHub allows one per person).
        await sessions.RefreshReviewStateAsync(session, ct).ConfigureAwait(false);
        if (session.PendingReview is { } found)
        {
            return found.Id;
        }

        var head = session.Snapshot?.HeadSha ?? throw new ReviewActionException(ReviewActionError.Loading, "Pull request is still loading.");
        var id = await GitHubAsync(() => gitHub.StartPendingReviewAsync(session.Key, head, ct)).ConfigureAwait(false);
        session.SetReviewState(new ReviewThreadsResult(session.Threads, new PendingReview { Id = id }));
        return id;
    }

    public async Task<PendingReview?> UpdatePendingAsync(PullRequestSession session, string commentId, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ReviewActionException(ReviewActionError.Invalid, "Comment body is empty.");
        }

        RequirePendingComment(session, commentId);
        await GitHubAsync(async () =>
        {
            await gitHub.UpdatePendingCommentAsync(commentId, body.Trim(), ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        await sessions.RefreshReviewStateAsync(session, ct).ConfigureAwait(false);
        return session.PendingReview;
    }

    public async Task<PendingReview?> DeletePendingAsync(PullRequestSession session, string commentId, CancellationToken ct)
    {
        RequirePendingComment(session, commentId);
        await GitHubAsync(async () =>
        {
            await gitHub.DeletePendingCommentAsync(commentId, ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        session.PendingPoints.TryRemove(commentId, out _);
        await sessions.RefreshReviewStateAsync(session, ct).ConfigureAwait(false);
        return session.PendingReview;
    }

    // ---------------------------------------------------------------- M4: threads

    /// <summary>Replies at once, or inside the pending review when there is one (GitHub's rule).</summary>
    public async Task<ReviewThread> ReplyAsync(PullRequestSession session, string threadId, string body, CancellationToken ct)
    {
        var thread = RequireThread(session, threadId);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ReviewActionException(ReviewActionError.Invalid, "Reply is empty.");
        }

        var comment = await GitHubAsync(() => gitHub.ReplyToThreadAsync(threadId, body.Trim(), session.PendingReview?.Id, ct)).ConfigureAwait(false);
        var updated = thread with { Comments = [.. thread.Comments, comment] };
        ReplaceThread(session, updated);
        _ = Task.Run(() => sessions.RefreshReviewStateAsync(session, session.Cancellation), CancellationToken.None);
        return updated;
    }

    public async Task<ReviewThread> SetResolvedAsync(PullRequestSession session, string threadId, bool resolved, CancellationToken ct)
    {
        var thread = RequireThread(session, threadId);
        await GitHubAsync(async () =>
        {
            await gitHub.SetThreadResolvedAsync(threadId, resolved, ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        var updated = thread with { IsResolved = resolved, CanResolve = !resolved, CanUnresolve = resolved };
        ReplaceThread(session, updated);
        return updated;
    }

    private static void ReplaceThread(PullRequestSession session, ReviewThread thread) =>
        session.SetReviewState(new ReviewThreadsResult([.. session.Threads.Select(t => t.Id == thread.Id ? thread : t)], session.PendingReview));

    // ---------------------------------------------------------------- M5: submit

    public async Task<(SubmittedReview Review, string HeadSha)> SubmitAsync(PullRequestSession session, ReviewEvent reviewEvent, string body, CancellationToken ct)
    {
        var snapshot = session.Snapshot ?? throw new ReviewActionException(ReviewActionError.Loading, "Pull request is still loading.");
        if (reviewEvent != ReviewEvent.Comment && string.Equals(session.ViewerLogin, snapshot.Metadata.Author, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReviewActionException(ReviewActionError.Invalid, "You can't approve or request changes on your own pull request.");
        }

        if (reviewEvent != ReviewEvent.Approve && string.IsNullOrWhiteSpace(body) && session.PendingReview is not { Comments.Count: > 0 })
        {
            throw new ReviewActionException(
                ReviewActionError.Invalid,
                reviewEvent == ReviewEvent.RequestChanges ? "Say what needs to change." : "Write a summary or add a comment first.");
        }

        SubmittedReview submitted;
        await session.ReviewGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var reviewId = await EnsurePendingReviewAsync(session, ct).ConfigureAwait(false);
            submitted = await GitHubAsync(() => gitHub.SubmitReviewAsync(reviewId, reviewEvent, body?.Trim() ?? "", ct)).ConfigureAwait(false);
            session.PendingPoints.Clear();
        }
        finally
        {
            session.ReviewGate.Release();
        }

        try
        {
            await visits.RecordReviewedAsync(session.Key, snapshot.HeadSha, time.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReviewNotRecorded(logger, ex, session.Key);
        }

        session.SetReviewState(new ReviewThreadsResult(session.Threads, null));
        await sessions.RefreshReviewStateAsync(session, ct).ConfigureAwait(false);
        await sessions.RefreshDetailsAsync(session, ct).ConfigureAwait(false);
        return (submitted, snapshot.HeadSha);
    }

    // ---------------------------------------------------------------- Helpers

    private static IPullRequestCheckout Checkout(PullRequestSession session) =>
        session.Checkout ?? throw new ReviewActionException(ReviewActionError.Loading, "Pull request is still loading.");

    /// <summary>GitHub attaches comments only to lines in the diff; both ends of a range must be.</summary>
    private static void ValidateLines(PullRequestSession session, NewReviewComment comment)
    {
        var snapshot = session.Snapshot ?? throw new ReviewActionException(ReviewActionError.Loading, "Pull request is still loading.");
        if (string.IsNullOrWhiteSpace(comment.Body))
        {
            throw new ReviewActionException(ReviewActionError.Invalid, "Comment body is empty.");
        }

        var file = snapshot.FindFile(comment.Path);
        bool InDiff(int line, DiffSide side) => file is not null && (side == DiffSide.Left ? file.ContainsOldLine(line) : file.ContainsNewLine(line));
        if (!InDiff(comment.Line, comment.Side))
        {
            throw new ReviewActionException(
                ReviewActionError.Invalid,
                $"{comment.Path}:{comment.Line} is not part of the diff, so GitHub can't attach a comment there.");
        }

        if (comment.StartLine is { } start && !InDiff(start, comment.StartSide ?? comment.Side))
        {
            throw new ReviewActionException(ReviewActionError.Invalid, $"{comment.Path}:{start} is not part of the diff.");
        }
    }

    private static ReviewThread RequireThread(PullRequestSession session, string threadId) =>
        session.Threads.FirstOrDefault(t => t.Id == threadId)
        ?? throw new ReviewActionException(ReviewActionError.NotFound, "That thread isn't on this pull request (it may have been deleted).");

    private static void RequirePendingComment(PullRequestSession session, string commentId)
    {
        if (session.PendingReview?.Comments.Any(c => c.Id == commentId) != true)
        {
            throw new ReviewActionException(ReviewActionError.NotFound, "That comment isn't in your pending review.");
        }
    }

    private static async Task<T> GitHubAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GitHub.GitHubApiException or GitHub.GitHubAuthenticationException or HttpRequestException)
        {
            throw new ReviewActionException(ReviewActionError.GitHub, $"GitHub didn't accept it: {ex.Message}");
        }
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{PullRequest}: couldn't record the review")]
    private static partial void LogReviewNotRecorded(ILogger logger, Exception exception, PullRequestKey pullRequest);
}
