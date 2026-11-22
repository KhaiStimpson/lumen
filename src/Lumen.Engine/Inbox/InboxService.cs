using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Engine.Mapping;
using Lumen.GitHub;

namespace Lumen.Engine.Inbox;

/// <summary>
/// The start screen (plan v2 M1): pull requests awaiting the viewer's review, their own open pull requests, and open
/// pull requests in watched repositories, fetched with one GraphQL round trip. Polled every two minutes while anyone
/// watches, and on demand (the app asks on focus). Backs off when GitHub's rate limit runs low.
/// </summary>
public sealed partial class InboxService(
    IGitHubClient gitHub,
    IPullRequestVisitStore visits,
    EngineSettingsStore settings,
    TimeProvider time,
    ILogger<InboxService> logger) : IDisposable
{
    public const string ReviewRequested = "review-requested";
    public const string Mine = "mine";
    public const string Watched = "watched";

    /// <summary>Below this many requests left, polling waits for the reset (asking on demand still works).</summary>
    private const int RateLimitFloor = 200;

    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly List<Channel<Contracts.Inbox>> _watchers = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Contracts.Inbox? _last;
    private DateTimeOffset _lastAt;
    private Task? _poller;

    public static IReadOnlyList<InboxQuery> QueriesFor(IReadOnlyList<string> watched)
    {
        List<InboxQuery> queries =
        [
            new(ReviewRequested, "is:open is:pr archived:false review-requested:@me"),
            new(Mine, "is:open is:pr archived:false author:@me"),
        ];
        var repos = watched.Where(IsRepository).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (repos.Count > 0)
        {
            queries.Add(new(Watched, "is:open is:pr archived:false " + string.Join(' ', repos.Select(r => $"repo:{r}"))));
        }

        return queries;
    }

    public static bool IsRepository(string value)
    {
        var parts = value.Trim().Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
    }

    public async Task<Contracts.Inbox> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!refresh && _last is not null && time.GetUtcNow() - _lastAt < Fresh)
            {
                return _last;
            }
        }

        return await FetchAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The current inbox, then each refresh, until cancelled.</summary>
    public async IAsyncEnumerable<Contracts.Inbox> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<Contracts.Inbox>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate)
        {
            _watchers.Add(channel);
            _poller ??= Task.Run(() => PollAsync(_lifetime.Token), CancellationToken.None);
        }

        try
        {
            yield return await GetAsync(refresh: false, cancellationToken).ConfigureAwait(false);
            await foreach (var inbox in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return inbox;
            }
        }
        finally
        {
            lock (_gate)
            {
                _watchers.Remove(channel);
            }
        }
    }

    public InboxSettings GetSettings()
    {
        var reply = new InboxSettings();
        reply.WatchedRepositories.AddRange(settings.Current.Inbox.WatchedRepositories);
        return reply;
    }

    /// <exception cref="ArgumentException">An entry isn't "owner/name".</exception>
    public InboxSettings UpdateSettings(InboxSettings changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var repos = changes.WatchedRepositories.Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        if (repos.FirstOrDefault(r => !IsRepository(r)) is { } bad)
        {
            throw new ArgumentException($"'{bad}' isn't a repository; use owner/name.", nameof(changes));
        }

        settings.Update(current => current with
        {
            Inbox = current.Inbox with { WatchedRepositories = [.. repos.Distinct(StringComparer.OrdinalIgnoreCase)] },
        });

        // The watched section changes at once for anyone looking.
        _ = Task.Run(() => FetchAsync(_lifetime.Token), CancellationToken.None);
        return GetSettings();
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(5, settings.Current.Inbox.PollSeconds));
            try
            {
                await Task.Delay(interval, time, cancellationToken).ConfigureAwait(false);
                bool anyone;
                lock (_gate)
                {
                    anyone = _watchers.Count > 0;
                }

                if (!anyone)
                {
                    continue;
                }

                if (gitHub.RateLimit is { } limit && limit.Remaining < RateLimitFloor && limit.ResetAt > time.GetUtcNow())
                {
                    LogRateLimited(logger, limit.Remaining, limit.ResetAt);
                    continue;
                }

                await FetchAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogPollFailed(logger, ex);
            }
        }
    }

    private async Task<Contracts.Inbox> FetchAsync(CancellationToken cancellationToken)
    {
        await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var watched = settings.Current.Inbox.WatchedRepositories;
            Contracts.Inbox inbox;
            try
            {
                var result = await gitHub.SearchInboxAsync(QueriesFor(watched), cancellationToken).ConfigureAwait(false);
                var opened = await visits.GetVisitsAsync(cancellationToken).ConfigureAwait(false);
                inbox = Build(result, opened, watched.Count > 0);
            }
            catch (Exception ex) when (ex is GitHubApiException or GitHubAuthenticationException or HttpRequestException)
            {
                LogFetchFailed(logger, ex);
                inbox = _last?.Clone() ?? EmptyInbox(watched.Count > 0);
                inbox.Error = ex.Message;
                inbox.IsAuthenticationError = ex is GitHubAuthenticationException or GitHubApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized };
            }

            Publish(inbox);
            return inbox;
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private void Publish(Contracts.Inbox inbox)
    {
        lock (_gate)
        {
            if (inbox.Error.Length == 0)
            {
                _last = inbox;
                _lastAt = time.GetUtcNow();
            }

            foreach (var watcher in _watchers)
            {
                watcher.Writer.TryWrite(inbox);
            }
        }
    }

    private Contracts.Inbox EmptyInbox(bool watching)
    {
        var inbox = new Contracts.Inbox { FetchedAt = Timestamp.FromDateTimeOffset(time.GetUtcNow()) };
        inbox.Sections.Add(Section(ReviewRequested, [], new Dictionary<PullRequestKey, PullRequestVisit>(), []));
        inbox.Sections.Add(Section(Mine, [], new Dictionary<PullRequestKey, PullRequestVisit>(), []));
        inbox.Sections.Add(Section(Watched, [], new Dictionary<PullRequestKey, PullRequestVisit>(), [], watching));
        return inbox;
    }

    private Contracts.Inbox Build(InboxSearchResult result, IReadOnlyDictionary<PullRequestKey, PullRequestVisit> opened, bool watching)
    {
        var inbox = new Contracts.Inbox
        {
            ViewerLogin = result.ViewerLogin,
            FetchedAt = Timestamp.FromDateTimeOffset(time.GetUtcNow()),
        };

        // A pull request shows once: review requests first, then the viewer's own, then the rest of the watched repositories.
        var shown = new HashSet<PullRequestKey>();
        foreach (var id in new[] { ReviewRequested, Mine, Watched })
        {
            var items = result.Results.GetValueOrDefault(id) ?? [];
            inbox.Sections.Add(Section(id, items, opened, shown, watching));
        }

        return inbox;
    }

    private static InboxSection Section(
        string id,
        IReadOnlyList<Domain.InboxItem> items,
        IReadOnlyDictionary<PullRequestKey, PullRequestVisit> opened,
        HashSet<PullRequestKey> shown,
        bool watching = true)
    {
        var section = new InboxSection
        {
            Id = id,
            Title = id switch
            {
                ReviewRequested => "Review requested",
                Mine => "My open pull requests",
                _ => "Watched repositories",
            },
        };

        foreach (var item in items.OrderByDescending(i => i.UpdatedAt))
        {
            if (!shown.Add(item.Key))
            {
                continue;
            }

            var visit = opened.GetValueOrDefault(item.Key);
            var proto = ProtoMapper.ToProto(item);
            proto.IsNew = visit?.LastOpenedAt is null;
            proto.Unread = visit?.LastOpenedAt is not { } at || item.UpdatedAt > at;
            section.Items.Add(proto);
        }

        if (section.Items.Count == 0)
        {
            section.Detail = id switch
            {
                ReviewRequested => "Nothing is waiting for your review.",
                Mine => "You have no open pull requests.",
                _ when !watching => "Add repositories to watch in Settings → Inbox.",
                _ => "No other open pull requests in the repositories you watch.",
            };
        }

        return section;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _fetchGate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox: couldn't reach GitHub")]
    private static partial void LogFetchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox: poll failed")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbox: {Remaining} GitHub requests left until {Reset}; skipping the poll")]
    private static partial void LogRateLimited(ILogger logger, int remaining, DateTimeOffset reset);
}
