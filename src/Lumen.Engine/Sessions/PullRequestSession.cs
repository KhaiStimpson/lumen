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
    }
}
