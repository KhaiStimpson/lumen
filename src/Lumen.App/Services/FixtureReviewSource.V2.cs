using System.Globalization;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Lumen.Contracts;

namespace Lumen.App.Services;

/// <summary>
/// The review client against the fixture (plan v2): inbox, details, threads, a pending review and the walkthrough,
/// all in memory. Writes behave as GitHub would (a pending review swallows single comments; submitting publishes it)
/// and are echoed as events; nothing leaves the machine.
/// </summary>
public sealed partial class FixtureReviewSource
{
    private readonly List<ReviewThread> _threads = [];
    private readonly Dictionary<string, List<string>> _commitFiles = new(StringComparer.Ordinal);
    private PendingReview _pending = new();
    private PullRequestDetails _details = new();
    private Inbox _inbox = new();
    private Walkthrough? _walkthrough;
    private InboxSettings _inboxSettings = new();
    private readonly List<System.Threading.Channels.Channel<Inbox>> _inboxWatchers = [];
    private int _ids;

    public List<SubmitReviewRequest> SubmittedReviews { get; } = [];

    public List<StartWalkthroughRequest> WalkthroughRequests { get; } = [];

    public List<string> IssueComments { get; } = [];

    public bool CloudflareStored { get; set; } = true;

    public PendingReview Pending => _pending;

    public IReadOnlyList<ReviewThread> Threads => _threads;

    private void LoadV2(string directory, JsonParser parser, string? viewerLogin)
    {
        var v2 = Path.Combine(directory, "v2-events.jsonl");
        if (File.Exists(v2))
        {
            foreach (var evt in File.ReadAllLines(v2).Where(l => l.Length > 0).Select(parser.Parse<PullRequestEvent>))
            {
                switch (evt.EventCase)
                {
                    case PullRequestEvent.EventOneofCase.ThreadsUpdated:
                        _threads.AddRange(evt.ThreadsUpdated.Threads);
                        break;
                    case PullRequestEvent.EventOneofCase.PendingReviewUpdated:
                        _pending = evt.PendingReviewUpdated;
                        break;
                    case PullRequestEvent.EventOneofCase.WalkthroughUpdated:
                        _walkthrough = evt.WalkthroughUpdated.Walkthrough;
                        break;
                }
            }
        }

        var details = Path.Combine(directory, "details.json");
        if (File.Exists(details))
        {
            _details = parser.Parse<PullRequestDetails>(File.ReadAllText(details));
        }
        else if (_events.FirstOrDefault(e => e.Snapshot is not null)?.Snapshot is { } snapshot)
        {
            _details = new PullRequestDetails
            {
                PullRequest = snapshot.PullRequest,
                Title = snapshot.Title,
                Author = snapshot.Author,
                Body = snapshot.Body,
                State = snapshot.State,
                BaseRef = snapshot.BaseRef,
                HeadRef = snapshot.HeadRef,
                HeadSha = snapshot.HeadSha,
                ViewerLogin = snapshot.ViewerLogin,
                Url = snapshot.Url,
            };
        }

        if (viewerLogin is not null)
        {
            _details.ViewerLogin = viewerLogin;
        }

        var commits = Path.Combine(directory, "commits.json");
        if (File.Exists(commits))
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(commits)) ?? [];
            foreach (var (sha, paths) in map)
            {
                _commitFiles[sha] = paths;
            }
        }

        var inbox = Path.Combine(directory, "..", "inbox", "inbox.json");
        _inbox = File.Exists(inbox) ? parser.Parse<Inbox>(File.ReadAllText(inbox)) : SynthesizedInbox();
    }

    /// <summary>The state as it is now, replayed after the recording to anyone who (re)attaches.</summary>
    private IEnumerable<PullRequestEvent> CurrentState()
    {
        lock (_threads)
        {
            yield return new PullRequestEvent { ThreadsUpdated = ThreadsEvent() };
            yield return new PullRequestEvent { TimelineUpdated = new TimelineUpdated { Timeline = { _details.Timeline } } };
            yield return new PullRequestEvent { ChecksUpdated = new ChecksUpdated { Checks = { _details.Checks }, HeadSha = _details.HeadSha } };
            yield return new PullRequestEvent { PendingReviewUpdated = _pending.Clone() };
            if (_walkthrough is not null)
            {
                yield return new PullRequestEvent
                {
                    WalkthroughUpdated = new WalkthroughUpdated
                    {
                        Walkthrough = ConnectionSettings.AllowCodeToFastModel && CloudflareStored
                            ? _walkthrough
                            : new Walkthrough { Status = WalkthroughStatus.Unavailable, Detail = "Turn on the walkthrough in Connections to send the diff to the fast model." },
                    },
                };
            }
        }
    }

    private ThreadsUpdated ThreadsEvent() => new() { Threads = { _threads.Where(t => !t.Comments.All(c => c.IsPending)).Select(t => t.Clone()) } };

    // ---------------------------------------------------------------- Inbox

    private Inbox SynthesizedInbox()
    {
        var inbox = new Inbox { FetchedAt = Timestamp.FromDateTime(DateTime.UtcNow) };
        foreach (var (id, title) in new[] { ("review-requested", "Review requested"), ("mine", "My open pull requests"), ("watched", "Watched repositories") })
        {
            inbox.Sections.Add(new InboxSection { Id = id, Title = title });
        }

        if (_events.FirstOrDefault(e => e.Snapshot is not null)?.Snapshot is { } snapshot)
        {
            inbox.ViewerLogin = snapshot.ViewerLogin;
            inbox.Sections[1].Items.Add(new InboxItem
            {
                PullRequest = snapshot.PullRequest,
                Title = snapshot.Title,
                Author = snapshot.Author,
                UpdatedAt = snapshot.UpdatedAt,
                Additions = snapshot.Files.Sum(f => f.Additions),
                Deletions = snapshot.Files.Sum(f => f.Deletions),
                ChangedFiles = snapshot.Files.Count,
                Url = snapshot.Url,
            });
        }

        return inbox;
    }

    public List<bool> InboxRequests { get; } = [];

    public Task<Inbox> ListInboxAsync(bool refresh, CancellationToken cancellationToken)
    {
        InboxRequests.Add(refresh);
        return Task.FromResult(_inbox.Clone());
    }

    public async IAsyncEnumerable<Inbox> WatchInboxAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<Inbox>();
        lock (_inboxWatchers)
        {
            _inboxWatchers.Add(channel);
        }

        try
        {
            yield return _inbox.Clone();
            await foreach (var inbox in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return inbox;
            }
        }
        finally
        {
            lock (_inboxWatchers)
            {
                _inboxWatchers.Remove(channel);
            }
        }
    }

    /// <summary>Replaces the inbox and tells watchers, as a poll that found changes would.</summary>
    public void PublishInbox(Inbox inbox)
    {
        _inbox = inbox;
        lock (_inboxWatchers)
        {
            foreach (var watcher in _inboxWatchers)
            {
                watcher.Writer.TryWrite(inbox.Clone());
            }
        }
    }

    public Task<InboxSettings> GetInboxSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(_inboxSettings.Clone());

    public Task<InboxSettings> UpdateInboxSettingsAsync(InboxSettings settings, CancellationToken cancellationToken)
    {
        var bad = settings.WatchedRepositories.FirstOrDefault(r => r.Split('/') is not [{ Length: > 0 }, { Length: > 0 }]);
        if (bad is not null)
        {
            return Task.FromException<InboxSettings>(new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, $"'{bad}' isn't a repository; use owner/name.")));
        }

        _inboxSettings = settings.Clone();
        return Task.FromResult(_inboxSettings.Clone());
    }

    // ---------------------------------------------------------------- Context

    public Task<PullRequestDetails> GetPullRequestDetailsAsync(PullRequestRef pullRequest, bool refresh, CancellationToken cancellationToken) =>
        Task.FromResult(_details.Clone());

    /// <summary>
    /// The recording has no git history: a range returns the files that commits.json says the range's commits
    /// touched, with their whole-PR diffs.
    /// </summary>
    public Task<DiffRange> GetDiffRangeAsync(GetDiffRangeRequest request, CancellationToken cancellationToken)
    {
        var order = _details.Commits.Select(c => c.Sha).ToList();
        var from = order.IndexOf(request.Range.FromSha);
        var to = order.IndexOf(request.Range.ToSha);
        var reply = new DiffRange { Range = request.Range.Clone() };
        var snapshot = _events.First(e => e.Snapshot is not null).Snapshot;
        if (to < 0)
        {
            return Task.FromResult(reply);
        }

        if (from < 0)
        {
            reply.IsForcePush = true;
            reply.Note = "History was rewritten since your last review; comparing file contents.";
            reply.Files.AddRange(snapshot.Files.Select(f => f.Clone()));
            return Task.FromResult(reply);
        }

        var paths = order.Skip(from + 1).Take(to - from)
            .SelectMany(sha => _commitFiles.GetValueOrDefault(sha) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        reply.Files.AddRange(snapshot.Files.Where(f => paths.Contains(f.Path)).Select(f => f.Clone()));
        return Task.FromResult(reply);
    }

    public Task<TimelineItem> PostIssueCommentAsync(PullRequestRef pullRequest, string body, CancellationToken cancellationToken)
    {
        IssueComments.Add(body);
        var item = new TimelineItem
        {
            Id = $"IC_fixture_{++_ids}",
            Kind = TimelineItemKind.Comment,
            Author = _details.ViewerLogin,
            Body = body.Trim(),
            CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            Url = "https://example.invalid/fixture-issue-comment",
        };
        _details.Timeline.Add(item);
        Broadcast(new PullRequestEvent { TimelineUpdated = new TimelineUpdated { Timeline = { _details.Timeline } } });
        return Task.FromResult(item);
    }

    // ---------------------------------------------------------------- Threads

    private ReviewThread AddThread(string path, DiffSide side, int line, int startLine, string body, bool pending)
    {
        var thread = new ReviewThread
        {
            Id = $"PRRT_fixture_{++_ids}",
            Path = path,
            Side = side,
            Line = line,
            StartLine = startLine,
            OriginalLine = line,
            OriginalStartLine = startLine,
            OriginalCommitSha = _details.HeadSha,
            CanReply = true,
            CanResolve = true,
            Comments =
            {
                new ThreadComment
                {
                    Id = $"PRRC_fixture_{++_ids}",
                    Author = _details.ViewerLogin,
                    Body = body.Trim(),
                    CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                    IsPending = pending,
                },
            },
        };
        lock (_threads)
        {
            _threads.Add(thread);
        }

        if (!pending)
        {
            Broadcast(new PullRequestEvent { ThreadsUpdated = ThreadsEvent() });
        }

        return thread;
    }

    public Task<ReviewThread> ReplyToThreadAsync(PullRequestRef pullRequest, string threadId, string body, CancellationToken cancellationToken)
    {
        var thread = _threads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null)
        {
            return Task.FromException<ReviewThread>(new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.NotFound, "That thread isn't on this pull request.")));
        }

        var comment = new ThreadComment
        {
            Id = $"PRRC_fixture_{++_ids}",
            Author = _details.ViewerLogin,
            Body = body.Trim(),
            CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            IsPending = _pending.Id.Length > 0,
        };
        thread.Comments.Add(comment);
        if (comment.IsPending)
        {
            _pending.Comments.Add(new PendingComment { Id = comment.Id, ThreadId = thread.Id, Path = thread.Path, Side = thread.Side, Line = thread.Line, Body = comment.Body, IsReply = true });
            Broadcast(new PullRequestEvent { PendingReviewUpdated = _pending.Clone() });
        }

        Broadcast(new PullRequestEvent { ThreadsUpdated = ThreadsEvent() });
        return Task.FromResult(thread.Clone());
    }

    public Task<ReviewThread> SetThreadResolvedAsync(PullRequestRef pullRequest, string threadId, bool resolved, CancellationToken cancellationToken)
    {
        var thread = _threads.First(t => t.Id == threadId);
        thread.IsResolved = resolved;
        thread.CanResolve = !resolved;
        thread.CanUnresolve = resolved;
        Broadcast(new PullRequestEvent { ThreadsUpdated = ThreadsEvent() });
        return Task.FromResult(thread.Clone());
    }

    // ---------------------------------------------------------------- Pending review

    public Task<PendingReview> GetPendingReviewAsync(PullRequestRef pullRequest, CancellationToken cancellationToken) => Task.FromResult(_pending.Clone());

    private PendingReview AddPending(string path, DiffSide side, int line, int startLine, string body, string? reviewPointId, string? replyTo)
    {
        if (_pending.Id.Length == 0)
        {
            _pending = new PendingReview { Id = $"PRR_fixture_{++_ids}" };
        }

        if (replyTo is not null)
        {
            var thread = _threads.First(t => t.Id == replyTo);
            var comment = new ThreadComment { Id = $"PRRC_fixture_{++_ids}", Author = _details.ViewerLogin, Body = body.Trim(), CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow), IsPending = true };
            thread.Comments.Add(comment);
            _pending.Comments.Add(new PendingComment { Id = comment.Id, ThreadId = thread.Id, Path = thread.Path, Side = thread.Side, Line = thread.Line, Body = comment.Body, IsReply = true });
            Broadcast(new PullRequestEvent { ThreadsUpdated = ThreadsEvent() });
        }
        else
        {
            var thread = AddThread(path, side, line, startLine, body, pending: true);
            _pending.Comments.Add(new PendingComment
            {
                Id = thread.Comments[0].Id,
                ThreadId = thread.Id,
                Path = path,
                Side = side,
                Line = line,
                StartLine = startLine,
                Body = body.Trim(),
                ReviewPointId = reviewPointId ?? "",
            });
        }

        Broadcast(new PullRequestEvent { PendingReviewUpdated = _pending.Clone() });
        return _pending.Clone();
    }

    private void MarkCommented(string? reviewPointId)
    {
        if (!string.IsNullOrEmpty(reviewPointId))
        {
            Broadcast(new PullRequestEvent
            {
                ReviewPointStateChanged = new ReviewPointStateChanged { ReviewPointId = reviewPointId, State = ReviewPointState.Commented },
            });
        }
    }

    public Task<PendingReview> AddPendingCommentAsync(AddPendingCommentRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return Task.FromException<PendingReview>(new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "Comment body is empty.")));
        }

        var pending = AddPending(
            request.Path,
            request.Side,
            request.Line,
            request.StartLine,
            request.Body,
            request.ReviewPointId,
            request.ReplyToThreadId.Length > 0 ? request.ReplyToThreadId : null);
        MarkCommented(request.ReviewPointId);
        return Task.FromResult(pending);
    }

    public Task<PendingReview> UpdatePendingCommentAsync(PullRequestRef pullRequest, string commentId, string body, CancellationToken cancellationToken)
    {
        foreach (var comment in _pending.Comments.Where(c => c.Id == commentId))
        {
            comment.Body = body.Trim();
        }

        foreach (var comment in _threads.SelectMany(t => t.Comments).Where(c => c.Id == commentId))
        {
            comment.Body = body.Trim();
        }

        Broadcast(new PullRequestEvent { PendingReviewUpdated = _pending.Clone() });
        return Task.FromResult(_pending.Clone());
    }

    public Task<PendingReview> DeletePendingCommentAsync(PullRequestRef pullRequest, string commentId, CancellationToken cancellationToken)
    {
        var removed = _pending.Comments.FirstOrDefault(c => c.Id == commentId);
        if (removed is not null)
        {
            _pending.Comments.Remove(removed);
        }

        foreach (var thread in _threads.ToList())
        {
            var comment = thread.Comments.FirstOrDefault(c => c.Id == commentId);
            if (comment is not null)
            {
                thread.Comments.Remove(comment);
                if (thread.Comments.Count == 0)
                {
                    _threads.Remove(thread);
                }
            }
        }

        Broadcast(new PullRequestEvent { PendingReviewUpdated = _pending.Clone() });
        Broadcast(new PullRequestEvent { ThreadsUpdated = ThreadsEvent() });
        return Task.FromResult(_pending.Clone());
    }

    public Task<SubmitReviewReply> SubmitReviewAsync(SubmitReviewRequest request, CancellationToken cancellationToken)
    {
        if (request.Event != ReviewEvent.Comment && string.Equals(_details.ViewerLogin, _details.Author, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromException<SubmitReviewReply>(new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "You can't approve or request changes on your own pull request.")));
        }

        if (request.Event != ReviewEvent.Approve && string.IsNullOrWhiteSpace(request.Body) && _pending.Comments.Count == 0)
        {
            return Task.FromException<SubmitReviewReply>(new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "Write a summary or add a comment first.")));
        }

        SubmittedReviews.Add(request.Clone());
        foreach (var comment in _threads.SelectMany(t => t.Comments))
        {
            comment.IsPending = false;
        }

        _details.Timeline.Add(new TimelineItem
        {
            Id = $"PRR_fixture_{++_ids}",
            Kind = TimelineItemKind.Review,
            Author = _details.ViewerLogin,
            Body = request.Body,
            CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            Verdict = request.Event switch
            {
                ReviewEvent.Approve => ReviewVerdict.Approved,
                ReviewEvent.RequestChanges => ReviewVerdict.ChangesRequested,
                _ => ReviewVerdict.Commented,
            },
            ReviewCommentCount = _pending.Comments.Count,
        });
        _details.LastReviewedSha = _details.HeadSha;
        _details.LastReviewedIsOpen = false;
        _pending = new PendingReview();
        Broadcast(new PullRequestEvent { PendingReviewUpdated = _pending.Clone() });
        Broadcast(new PullRequestEvent { ThreadsUpdated = ThreadsEvent() });
        Broadcast(new PullRequestEvent { TimelineUpdated = new TimelineUpdated { Timeline = { _details.Timeline } } });
        return Task.FromResult(new SubmitReviewReply { Url = "https://example.invalid/fixture-review", HeadSha = _details.HeadSha });
    }

    // ---------------------------------------------------------------- Fast model

    public Task<Connections> SetCloudflareCredentialsAsync(string accountId, string apiToken, CancellationToken cancellationToken)
    {
        CloudflareStored = !string.IsNullOrWhiteSpace(accountId) && !string.IsNullOrWhiteSpace(apiToken);
        return Task.FromResult(FixtureConnections());
    }

    public Task<Connections> RemoveCloudflareCredentialsAsync(CancellationToken cancellationToken)
    {
        CloudflareStored = false;
        return Task.FromResult(FixtureConnections());
    }

    /// <summary>A recorded walkthrough for the whole PR; a short one built from the range for "since last review".</summary>
    public async Task StartWalkthroughAsync(StartWalkthroughRequest request, CancellationToken cancellationToken)
    {
        WalkthroughRequests.Add(request.Clone());
        if (request.Since is not { FromSha.Length: > 0 } since)
        {
            if (_walkthrough is not null)
            {
                Broadcast(new PullRequestEvent { WalkthroughUpdated = new WalkthroughUpdated { Walkthrough = _walkthrough } });
            }

            return;
        }

        var range = await GetDiffRangeAsync(
            new GetDiffRangeRequest { PullRequest = request.PullRequest, Range = new CommitRange { FromSha = since.FromSha, ToSha = _details.HeadSha } },
            cancellationToken).ConfigureAwait(false);
        var delta = new Walkthrough
        {
            Status = WalkthroughStatus.Complete,
            Intent = string.Create(
                CultureInfo.InvariantCulture,
                $"Since you last looked: {range.Files.Count} files changed, mostly signals, the scheduler and the settings page."),
            Producer = _walkthrough?.Producer ?? "fixture",
            HeadSha = _details.HeadSha,
            Since = new CommitRange { FromSha = since.FromSha, ToSha = _details.HeadSha },
            FilesDone = range.Files.Count,
            FilesTotal = range.Files.Count,
        };
        var group = new WalkthroughGroup { Id = "g1", Title = "Changes since your last review", Order = 1 };
        group.Paths.AddRange(range.Files.Where(f => !f.IsMechanical).Select(f => f.Path));
        delta.Groups.Add(group);
        Broadcast(new PullRequestEvent { WalkthroughUpdated = new WalkthroughUpdated { Walkthrough = delta } });
    }
}
