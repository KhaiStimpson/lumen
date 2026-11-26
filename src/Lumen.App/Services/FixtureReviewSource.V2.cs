using System.Globalization;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Lumen.Contracts;

namespace Lumen.App.Services;

/// <summary>
/// The review client against the fixture (plan v2): inbox, details, threads, a pending review, marks and since-my-last-look,
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
    private SinceLastLook? _sinceLastLook;
    private LocalBranches _localBranches = new();
    private bool _localBranchMode;
    private InboxSettings _inboxSettings = new();
    private readonly List<System.Threading.Channels.Channel<Inbox>> _inboxWatchers = [];
    private int _ids;

    public List<SubmitReviewRequest> SubmittedReviews { get; } = [];

    public List<string> IssueComments { get; } = [];

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

        var since = Path.Combine(directory, "since.json");
        if (File.Exists(since))
        {
            _sinceLastLook = parser.Parse<SinceLastLook>(File.ReadAllText(since));
        }

        var local = Path.Combine(directory, "..", "inbox", "local-branches.json");
        if (File.Exists(local))
        {
            _localBranches = parser.Parse<LocalBranches>(File.ReadAllText(local));
        }

        _localBranchMode = _events.Any(e => e.Snapshot is { Local: not null });

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
            if (_marks.Count > 0 || _events.Any(e => e.SlicesReady is not null))
            {
                yield return new PullRequestEvent { MarksUpdated = MarksEvent() };
            }
        }
    }

    private ThreadsUpdated ThreadsEvent() => new() { Threads = { _threads.Select(t => t.Clone()) } };

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
            CanResolve = !pending,
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

        Broadcast(new PullRequestEvent { ThreadsUpdated = ThreadsEvent() });
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

    private PendingReview AddPending(string path, DiffSide side, int line, int startLine, string body, string? replyTo)
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
            });
        }

        Broadcast(new PullRequestEvent { PendingReviewUpdated = _pending.Clone() });
        return _pending.Clone();
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
            request.ReplyToThreadId.Length > 0 ? request.ReplyToThreadId : null);
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
        foreach (var thread in _threads)
        {
            foreach (var comment in thread.Comments)
            {
                comment.IsPending = false;
            }

            thread.CanResolve = !thread.IsResolved;
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

    // ---------------------------------------------------------------- Overhaul: local branches and tracking

    private readonly Dictionary<string, string> _marks = new(StringComparer.Ordinal);

    /// <summary>Every SetMarked request, so tests can see what was marked.</summary>
    public List<SetMarkedRequest> MarkRequests { get; } = [];

    /// <summary>Paths whose Viewed box the fixture "ticked" because every slice holding them is reviewed.</summary>
    public HashSet<string> ViewedTicks { get; } = new(StringComparer.Ordinal);

    public IAsyncEnumerable<PullRequestEvent> WatchAsync(WatchPullRequestRequest request, CancellationToken cancellationToken) =>
        WatchAsync(request.PullRequest ?? new PullRequestRef(), request.Refresh, cancellationToken);

    public Task<LocalBranches> ListLocalBranchesAsync(CancellationToken cancellationToken) => Task.FromResult(_localBranches.Clone());

    public Task<MarksUpdated> SetMarkedAsync(SetMarkedRequest request, CancellationToken cancellationToken)
    {
        MarkRequests.Add(request.Clone());
        var fingerprints = _events.Where(e => e.SlicesReady is not null).SelectMany(e => e.SlicesReady.Units)
            .GroupBy(u => u.Unit, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Fingerprint, StringComparer.Ordinal);
        foreach (var unit in request.Units)
        {
            if (request.Marked)
            {
                _marks[unit] = fingerprints.GetValueOrDefault(unit, "");
            }
            else
            {
                _marks.Remove(unit);
            }
        }

        var reply = MarksEvent();
        reply.ViewedPaths.AddRange(ViewedNow(fingerprints));
        Broadcast(new PullRequestEvent { MarksUpdated = reply.Clone() });
        return Task.FromResult(reply);
    }

    /// <summary>Like the engine: a file is Viewed once every slice holding it has all its units marked.</summary>
    private List<string> ViewedNow(Dictionary<string, string> fingerprints)
    {
        var slices = _events.LastOrDefault(e => e.SlicesReady is not null)?.SlicesReady;
        if (slices is null || _localBranchMode)
        {
            return [];
        }

        bool Reviewed(Slice slice) => slice.Files.All(f => fingerprints.Keys.Where(u => u == f.Path || u.StartsWith(f.Path + "::", StringComparison.Ordinal))
            .All(u => _marks.TryGetValue(u, out var fp) && fp == fingerprints[u]));
        var newly = new List<string>();
        foreach (var path in slices.Slices.SelectMany(s => s.Files).Select(f => f.Path).Distinct(StringComparer.Ordinal))
        {
            if (slices.Slices.Where(s => s.Files.Any(f => f.Path == path)).All(Reviewed) && ViewedTicks.Add(path))
            {
                newly.Add(path);
            }
        }

        return newly;
    }

    private MarksUpdated MarksEvent()
    {
        var reply = new MarksUpdated();
        reply.Marks.AddRange(_marks.Select(kv => new ReviewMark { Unit = kv.Key, Fingerprint = kv.Value }));
        return reply;
    }

    /// <summary>Adds an event as if the engine had sent it (tests and capture scripts): replayed to later watchers too.</summary>
    public void Publish(PullRequestEvent evt)
    {
        _events.Add(evt);
        Broadcast(evt);
    }

    /// <summary>Since-my-last-look for this fixture (tests and capture scripts).</summary>
    public void SetSinceLastLook(SinceLastLook since) => _sinceLastLook = since;

    public Task<SinceLastLook> GetSinceLastLookAsync(PullRequestRef pullRequest, CancellationToken cancellationToken) =>
        Task.FromResult(_sinceLastLook?.Clone() ?? new SinceLastLook { NewHeadSha = _details.HeadSha });
}
