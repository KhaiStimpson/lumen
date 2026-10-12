using System.Collections.Concurrent;
using System.Threading.Channels;
using Lumen.Contracts;
using Lumen.Domain;

namespace Lumen.Engine.Sessions;

/// <summary>
/// One pull request being analysed. Keeps an append-only event log so any number of clients can attach at any
/// time and receive a full replay followed by live events (TDD §46, §48).
/// </summary>
public sealed class PullRequestSession : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<PullRequestEvent> _log = [];
    private readonly List<Channel<PullRequestEvent>> _subscribers = [];
    private readonly Dictionary<string, Domain.ReviewPoint> _points = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    public PullRequestSession(PullRequestKey key)
    {
        Key = key;
    }

    public PullRequestKey Key { get; }

    public Domain.PullRequestSnapshot? Snapshot { get; private set; }

    public IPullRequestCheckout? Checkout { get; private set; }

    public bool IsFinished { get; private set; }

    public bool HasFailed { get; private set; }

    public CancellationToken Cancellation => _cts.Token;

    internal Task? Completion { get; set; }

    // ---- v2: the review client's view of GitHub ----

    public string ViewerLogin { get; internal set; } = "";

    /// <summary>Description, conversation, commits and checks; null until loaded (best effort, after the snapshot).</summary>
    public Domain.PullRequestDetails? Details { get; private set; }

    public IReadOnlyList<Domain.ReviewThread> Threads { get; private set; } = [];

    public Domain.PendingReview? PendingReview { get; private set; }

    /// <summary>The visit recorded before this session opened the pull request (for "since last review").</summary>
    public PullRequestVisit? PreviousVisit { get; internal set; }

    /// <summary>Serialises pending-review changes, so two comments can't each start a review.</summary>
    internal SemaphoreSlim ReviewGate { get; } = new(1, 1);

    /// <summary>Pending comment id → the review point it answers, so the tray can say which.</summary>
    internal ConcurrentDictionary<string, string> PendingPoints { get; } = new(StringComparer.Ordinal);

    internal ConcurrentDictionary<(string From, string To, bool IgnoreWhitespace), DiffRangeResult> Ranges { get; } = new();

    internal ConcurrentDictionary<string, ChangedFile> WhitespaceDiffs { get; } = new(StringComparer.Ordinal);

    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>Replaces details and publishes what changed (timeline, checks, a new head).</summary>
    internal void SetDetails(Domain.PullRequestDetails details)
    {
        Domain.PullRequestDetails? previous;
        lock (_gate)
        {
            previous = Details;
            Details = details;
        }

        if (previous is null || !previous.Timeline.Select(t => (t.Id, t.Body)).SequenceEqual(details.Timeline.Select(t => (t.Id, t.Body))))
        {
            var timeline = new TimelineUpdated();
            timeline.Timeline.AddRange(details.Timeline.Select(Mapping.ProtoMapper.ToProto));
            Publish(new PullRequestEvent { TimelineUpdated = timeline });
        }

        if (previous is null || !previous.Checks.SequenceEqual(details.Checks))
        {
            var checks = new ChecksUpdated { HeadSha = details.HeadSha };
            checks.Checks.AddRange(details.Checks.Select(Mapping.ProtoMapper.ToProto));
            Publish(new PullRequestEvent { ChecksUpdated = checks });
        }

        if (Snapshot is { } snapshot && details.HeadSha.Length > 0
            && !string.Equals(details.HeadSha, snapshot.HeadSha, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(previous?.HeadSha, details.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            Publish(new PullRequestEvent { HeadChanged = new HeadChanged { OldSha = snapshot.HeadSha, NewSha = details.HeadSha } });
        }
    }

    /// <summary>Replaces threads and the pending review; publishes only what changed.</summary>
    internal void SetReviewState(ReviewThreadsResult state, bool force = false)
    {
        bool threadsChanged, pendingChanged;
        lock (_gate)
        {
            threadsChanged = force || !SameThreads(Threads, state.Threads);
            pendingChanged = force || !SamePending(PendingReview, state.PendingReview);
            Threads = state.Threads;
            PendingReview = state.PendingReview;
        }

        if (threadsChanged)
        {
            // The viewer's own pending threads are included: they show inline, marked pending, as on github.com.
            Publish(new PullRequestEvent { ThreadsUpdated = Mapping.ProtoMapper.ToProto(state.Threads) });
        }

        if (pendingChanged)
        {
            Publish(new PullRequestEvent { PendingReviewUpdated = Mapping.ProtoMapper.ToProto(state.PendingReview, PendingPoints) });
        }
    }

    private static bool SameThreads(IReadOnlyList<Domain.ReviewThread> a, IReadOnlyList<Domain.ReviewThread> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            p.First.Id == p.Second.Id && p.First.IsResolved == p.Second.IsResolved && p.First.IsOutdated == p.Second.IsOutdated
            && p.First.Line == p.Second.Line && p.First.Comments.Select(c => (c.Id, c.Body, c.IsPending)).SequenceEqual(p.Second.Comments.Select(c => (c.Id, c.Body, c.IsPending))));

    private static bool SamePending(Domain.PendingReview? a, Domain.PendingReview? b) =>
        (a is null && b is null) || (a is not null && b is not null && a.Id == b.Id && a.Body == b.Body
            && a.Comments.Select(c => (c.Id, c.Body)).SequenceEqual(b.Comments.Select(c => (c.Id, c.Body))));

    public Domain.ReviewPoint? FindReviewPoint(string id)
    {
        lock (_gate)
        {
            return _points.GetValueOrDefault(id);
        }
    }

    internal void SetSnapshot(Domain.PullRequestSnapshot snapshot, IPullRequestCheckout checkout)
    {
        Snapshot = snapshot;
        Checkout = checkout;
    }

    internal void AddReviewPoint(Domain.ReviewPoint point)
    {
        lock (_gate)
        {
            _points[point.Id] = point;
        }
    }

    internal void MarkFinished(bool failed)
    {
        IsFinished = true;
        HasFailed = failed;
    }

    public void Publish(PullRequestEvent evt)
    {
        lock (_gate)
        {
            _log.Add(evt);
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(evt);
            }
        }
    }

    /// <summary>Replays everything published so far, then streams live events until cancelled.</summary>
    public async IAsyncEnumerable<PullRequestEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<PullRequestEvent>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            foreach (var evt in _log)
            {
                channel.Writer.TryWrite(evt);
            }

            _subscribers.Add(channel);
        }

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }
        }
    }

    public void Cancel() => _cts.Cancel();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        ReviewGate.Dispose();
    }
}
