using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumen.Engine.Tests;

/// <summary>The review client over the real pipe (plan v2 M1–M5), with GitHub and git faked.</summary>
public sealed class EngineReviewClientTests : IAsyncLifetime, IDisposable
{
    private static readonly PullRequestRef Pr = new() { Owner = "acme", Name = "billing", Number = 42 };

    private readonly string _pipe = $"lumen-test-{Guid.NewGuid():N}";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"lumen-test-{Guid.NewGuid():N}");
    private readonly FakeGitHub _gitHub = new();
    private Microsoft.AspNetCore.Builder.WebApplication? _engine;
    private GrpcChannelHolder? _channel;

    private ReviewEngine.ReviewEngineClient Client => _channel!.Client;

    public async Task InitializeAsync()
    {
        var checkout = WorkerScenario.CreateCheckout(Path.Combine(_dataDir, "checkout"));
        _engine = EngineHost.Build(
            new EngineOptions { PipeName = _pipe, DataDirectory = _dataDir, FollowInterval = TimeSpan.FromMilliseconds(200) },
            services =>
            {
                services.RemoveAll<IGitHubClient>();
                services.AddSingleton<IGitHubClient>(_gitHub);
                services.RemoveAll<IRepositoryWorkspace>();
                services.AddSingleton<IRepositoryWorkspace>(new FakeWorkspace(checkout));
                services.RemoveAll<ISecretStore>();
                services.AddSingleton<ISecretStore>(new Lumen.Storage.UnavailableSecretStore());
            });
        await _engine.StartAsync();
        _channel = new GrpcChannelHolder(_pipe);
    }

    public void Dispose() => _channel?.Dispose();

    public async Task DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.StopAsync();
            await _engine.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class GrpcChannelHolder : IDisposable
    {
        private readonly Grpc.Net.Client.GrpcChannel _channel;

        public GrpcChannelHolder(string pipe)
        {
            _channel = EngineEndpoint.CreateChannel(pipe);
            Client = new ReviewEngine.ReviewEngineClient(_channel);
        }

        public ReviewEngine.ReviewEngineClient Client { get; }

        public void Dispose() => _channel.Dispose();
    }

    // ---------------------------------------------------------------- M1

    private static Domain.InboxItem Item(string repo, int number, string author, DateTimeOffset updated) => new()
    {
        Key = new PullRequestKey(new RepositoryRef(repo.Split('/')[0], repo.Split('/')[1]), number),
        Title = $"PR {number}",
        Author = author,
        UpdatedAt = updated,
        Additions = 10,
        Deletions = 2,
        ChangedFiles = 3,
        Checks = Domain.InboxCheckState.Success,
        Review = Domain.InboxReviewState.Awaiting,
    };

    [Fact]
    public async Task InboxHasThreeSectionsWithEachPullRequestOnceAndUnreadMarkers()
    {
        var now = DateTimeOffset.UtcNow;
        _gitHub.InboxResults = q => q.Id switch
        {
            "review-requested" => [Item("acme/billing", 42, "sarah-chen", now)],
            "mine" => [Item("acme/web", 7, "reviewer", now)],
            _ => [Item("acme/billing", 42, "sarah-chen", now), Item("acme/billing", 43, "sam", now.AddDays(-1))],
        };
        await Client.UpdateInboxSettingsAsync(new InboxSettings { WatchedRepositories = { "acme/billing" } });

        // Open #42 first, so it is read (until it changes again).
        await CollectUntilAsync(e => e.Complete is not null);
        _gitHub.InboxResults = q => q.Id switch
        {
            "review-requested" => [Item("acme/billing", 42, "sarah-chen", now)],
            "mine" => [Item("acme/web", 7, "reviewer", now)],
            _ => [Item("acme/billing", 42, "sarah-chen", now), Item("acme/billing", 43, "sam", now.AddDays(-1))],
        };

        var inbox = await Client.ListInboxAsync(new ListInboxRequest { Refresh = true });

        Assert.Equal(["review-requested", "mine", "watched"], inbox.Sections.Select(s => s.Id));
        Assert.Equal("reviewer", inbox.ViewerLogin);
        var requested = Assert.Single(inbox.Sections[0].Items);
        Assert.Equal(42, requested.PullRequest.Number);
        Assert.False(requested.IsNew);
        Assert.Equal(Contracts.InboxCheckState.Success, requested.Checks);
        Assert.True(inbox.Sections[1].Items.Single().IsNew);
        var watched = Assert.Single(inbox.Sections[2].Items);
        Assert.Equal(43, watched.PullRequest.Number);
        Assert.True(watched.Unread);

        var last = _gitHub.InboxSearches.Last().Queries;
        Assert.Equal("is:open is:pr archived:false repo:acme/billing", last.Single(q => q.Id == "watched").Search);
    }

    [Fact]
    public async Task InboxWithoutWatchedRepositoriesSaysHowToAddThem()
    {
        var inbox = await Client.ListInboxAsync(new ListInboxRequest { Refresh = true });

        Assert.DoesNotContain(_gitHub.InboxSearches.Last().Queries, q => q.Id == "watched");
        Assert.Contains("Settings", inbox.Sections[2].Detail, StringComparison.Ordinal);
        Assert.Equal("Nothing is waiting for your review.", inbox.Sections[0].Detail);
    }

    [Fact]
    public async Task InboxRejectsBadRepositoriesAndReportsGitHubFailures()
    {
        var bad = await Assert.ThrowsAsync<RpcException>(async () =>
            await Client.UpdateInboxSettingsAsync(new InboxSettings { WatchedRepositories = { "not a repo" } }));
        Assert.Equal(StatusCode.InvalidArgument, bad.StatusCode);

        _gitHub.FailWith = new Lumen.GitHub.GitHubAuthenticationException("Run gh auth login.");
        var inbox = await Client.ListInboxAsync(new ListInboxRequest { Refresh = true });
        Assert.True(inbox.IsAuthenticationError);
        Assert.Contains("gh auth login", inbox.Error, StringComparison.Ordinal);
        Assert.Equal(3, inbox.Sections.Count);
    }

    [Fact]
    public async Task WatchingTheInboxStreamsTheCurrentStateFirst()
    {
        _gitHub.InboxResults = q => q.Id == "mine" ? [Item("acme/web", 7, "reviewer", DateTimeOffset.UtcNow)] : [];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var call = Client.WatchInbox(new WatchInboxRequest(), cancellationToken: cts.Token);

        Assert.True(await call.ResponseStream.MoveNext(cts.Token));
        Assert.Single(call.ResponseStream.Current.Sections[1].Items);
    }

    // ---------------------------------------------------------------- M2

    [Fact]
    public async Task DetailsArriveAsEventsAndOnRequestWithLastOpenAsTheBaseline()
    {
        var events = await CollectUntilAsync(e => e.ChecksUpdated is not null);
        Assert.Contains(events, e => e.TimelineUpdated is not null);
        Assert.Equal("build", events.Last(e => e.ChecksUpdated is not null).ChecksUpdated.Checks.Single().Name);

        var details = await Client.GetPullRequestDetailsAsync(new GetPullRequestDetailsRequest { PullRequest = Pr });
        Assert.Equal("Retries failed invoices.", details.Body);
        Assert.Equal("", details.LastReviewedSha);

        // Reopening (a new analysis) remembers the earlier open as "since last review".
        await CollectUntilAsync(e => e.Complete is not null, refresh: true);
        details = await Client.GetPullRequestDetailsAsync(new GetPullRequestDetailsRequest { PullRequest = Pr });
        Assert.Equal(WorkerScenario.HeadSha, details.LastReviewedSha);
        Assert.True(details.LastReviewedIsOpen);
    }

    [Fact]
    public async Task IssueCommentIsPostedAndJoinsTheTimeline()
    {
        await CollectUntilAsync(e => e.TimelineUpdated is not null);

        var item = await Client.PostIssueCommentAsync(new PostIssueCommentRequest { PullRequest = Pr, Body = "  Thanks!  " });

        Assert.Equal("Thanks!", item.Body);
        var replay = await CollectUntilAsync(e => e.TimelineUpdated?.Timeline.Any(t => t.Body == "Thanks!") == true, refresh: false);
        Assert.NotNull(replay.Last().TimelineUpdated);
    }

    [Fact]
    public async Task NewHeadIsAnnounced()
    {
        await CollectUntilAsync(e => e.ChecksUpdated is not null);
        var newHead = new string('3', 40);
        _gitHub.Details = key => new Domain.PullRequestDetails { Key = key, Title = "t", Author = "sarah-chen", HeadSha = newHead };

        var events = await CollectUntilAsync(e => e.HeadChanged is not null, refresh: false);

        Assert.Equal(new HeadChanged { OldSha = WorkerScenario.HeadSha, NewSha = newHead }, events.Last().HeadChanged);
    }

    // ---------------------------------------------------------------- M3–M5

    [Fact]
    public async Task PendingCommentsStartOneReviewAndSubmitApproves()
    {
        var point = (await CollectUntilAsync(e => e.Complete is not null)).First(e => e.ReviewPointAdded is not null).ReviewPointAdded;

        var pending = await Client.AddPendingCommentAsync(new AddPendingCommentRequest
        {
            PullRequest = Pr,
            Path = WorkerScenario.NewWorkerPath,
            Line = 7,
            StartLine = 5,
            Body = "Range comment",
        });
        pending = await Client.AddPendingCommentAsync(new AddPendingCommentRequest
        {
            PullRequest = Pr,
            Path = point.Anchor.Path,
            Line = point.Anchor.StartLine,
            Body = point.SuggestedComment,
            ReviewPointId = point.Id,
        });

        Assert.Equal(2, pending.Comments.Count);
        Assert.Equal(5, pending.Comments[0].StartLine);
        Assert.Equal(point.Id, pending.Comments[1].ReviewPointId);
        Assert.Empty(_gitHub.Posted);

        var edited = await Client.UpdatePendingCommentAsync(new UpdatePendingCommentRequest { PullRequest = Pr, CommentId = pending.Comments[0].Id, Body = "Edited" });
        Assert.Equal("Edited", edited.Comments[0].Body);
        var deleted = await Client.DeletePendingCommentAsync(new DeletePendingCommentRequest { PullRequest = Pr, CommentId = pending.Comments[0].Id });
        Assert.Single(deleted.Comments);

        var reply = await Client.SubmitReviewAsync(new SubmitReviewRequest { PullRequest = Pr, Event = Contracts.ReviewEvent.Approve, Body = "LGTM" });

        Assert.Equal(WorkerScenario.HeadSha, reply.HeadSha);
        var submitted = Assert.Single(_gitHub.Submitted);
        Assert.Equal((Domain.ReviewEvent.Approve, "LGTM"), (submitted.Event, submitted.Body));
        Assert.Equal("", (await Client.GetPendingReviewAsync(new GetPendingReviewRequest { PullRequest = Pr })).Id);

        // The point it answered is marked commented, and the review becomes the "since last review" baseline.
        var replay = await CollectUntilAsync(e => e.ReviewPointStateChanged?.ReviewPointId == point.Id, refresh: false);
        Assert.Equal(Contracts.ReviewPointState.Commented, replay.Last().ReviewPointStateChanged.State);
        var details = await Client.GetPullRequestDetailsAsync(new GetPullRequestDetailsRequest { PullRequest = Pr });
        Assert.Equal((WorkerScenario.HeadSha, false), (details.LastReviewedSha, details.LastReviewedIsOpen));
    }

    [Fact]
    public async Task PostNowGoesIntoAnExistingPendingReviewAndSaysSo()
    {
        await CollectUntilAsync(e => e.Complete is not null);
        _gitHub.Pending = new Domain.PendingReview { Id = "PRR_web" };

        var reply = await Client.PostReviewCommentAsync(new PostReviewCommentRequest
        {
            PullRequest = Pr,
            Path = WorkerScenario.NewWorkerPath,
            Line = 3,
            Body = "Now please",
        });

        Assert.True(reply.AddedToPendingReview);
        Assert.Equal("PRR_web", reply.PendingReview.Id);
        Assert.Single(reply.PendingReview.Comments);
        Assert.Empty(_gitHub.Posted);
    }

    [Fact]
    public async Task PostNowOnTheBaseSideOfARange()
    {
        await CollectUntilAsync(e => e.Complete is not null);

        var reply = await Client.PostReviewCommentAsync(new PostReviewCommentRequest
        {
            PullRequest = Pr,
            Path = WorkerScenario.NewWorkerPath,
            Line = 4,
            StartLine = 2,
            Body = "Range",
        });

        Assert.False(reply.AddedToPendingReview);
        var posted = Assert.Single(_gitHub.Posted);
        Assert.Equal((2, 4, Domain.DiffSide.Right), (posted.StartLine, posted.Line, posted.Side));

        // Deleted lines comment on the base side; the scenario's file is new, so there are none.
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Client.PostReviewCommentAsync(new PostReviewCommentRequest
        {
            PullRequest = Pr,
            Path = WorkerScenario.NewWorkerPath,
            Line = 4,
            Side = Contracts.DiffSide.Left,
            Body = "Base side",
        }));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task ThreadsStreamAndCanBeRepliedToAndResolved()
    {
        _gitHub.Threads.Add(new Domain.ReviewThread
        {
            Id = "T_x",
            Path = WorkerScenario.NewWorkerPath,
            Line = 5,
            OriginalLine = 5,
            CanResolve = true,
            Comments = [new Domain.ThreadComment { Id = "C_x", Author = "sam", Body = "Why?", CreatedAt = DateTimeOffset.UtcNow }],
        });

        var events = await CollectUntilAsync(e => e.Complete is not null);
        var threads = events.First(e => e.ThreadsUpdated is not null).ThreadsUpdated.Threads;
        Assert.Equal("Why?", Assert.Single(threads).Comments.Single().Body);

        var replied = await Client.ReplyToThreadAsync(new ReplyToThreadRequest { PullRequest = Pr, ThreadId = "T_x", Body = "Because." });
        Assert.Equal(["Why?", "Because."], replied.Comments.Select(c => c.Body));
        Assert.False(replied.Comments[1].IsPending);

        var resolved = await Client.SetThreadResolvedAsync(new SetThreadResolvedRequest { PullRequest = Pr, ThreadId = "T_x", Resolved = true });
        Assert.True(resolved.IsResolved);
        Assert.True(_gitHub.Threads.Single().IsResolved);

        var missing = await Assert.ThrowsAsync<RpcException>(async () =>
            await Client.SetThreadResolvedAsync(new SetThreadResolvedRequest { PullRequest = Pr, ThreadId = "nope", Resolved = true }));
        Assert.Equal(StatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task AuthorsCannotApproveTheirOwnPullRequest()
    {
        _gitHub.Author = "reviewer";
        await CollectUntilAsync(e => e.Complete is not null);

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await Client.SubmitReviewAsync(new SubmitReviewRequest { PullRequest = Pr, Event = Contracts.ReviewEvent.Approve }));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        var empty = await Assert.ThrowsAsync<RpcException>(async () =>
            await Client.SubmitReviewAsync(new SubmitReviewRequest { PullRequest = Pr, Event = Contracts.ReviewEvent.Comment }));
        Assert.Equal(StatusCode.InvalidArgument, empty.StatusCode);
        Assert.Empty(_gitHub.Submitted);

        await Client.SubmitReviewAsync(new SubmitReviewRequest { PullRequest = Pr, Event = Contracts.ReviewEvent.Comment, Body = "Notes to self" });
        Assert.Single(_gitHub.Submitted);
    }

    private async Task<List<PullRequestEvent>> CollectUntilAsync(Func<PullRequestEvent, bool> stop, bool refresh = false)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var call = Client.WatchPullRequest(new WatchPullRequestRequest { PullRequest = Pr, Refresh = refresh }, cancellationToken: cts.Token);
        var events = new List<PullRequestEvent>();
        bool seenComplete = false, matched = false;
        await foreach (var evt in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            events.Add(evt);
            seenComplete |= evt.Complete is not null || evt.Failed is not null;
            matched |= stop(evt);
            if (matched && seenComplete)
            {
                break;
            }
        }

        return events;
    }
}
