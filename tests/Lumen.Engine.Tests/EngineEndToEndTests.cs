using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace Lumen.Engine.Tests;

/// <summary>Drives the real engine over a real named pipe, with GitHub and git replaced by in-memory fakes.</summary>
public sealed class EngineEndToEndTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly PullRequestRef Pr = new() { Owner = "acme", Name = "billing", Number = 42 };

    private readonly string _pipe = $"lumen-test-{Guid.NewGuid():N}";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"lumen-test-{Guid.NewGuid():N}");
    private readonly FakeGitHub _gitHub = new();
    private Microsoft.AspNetCore.Builder.WebApplication? _engine;

    public async Task InitializeAsync()
    {
        var checkout = WorkerScenario.CreateCheckout(Path.Combine(_dataDir, "checkout"));
        _engine = EngineHost.Build(
            new EngineOptions { PipeName = _pipe, DataDirectory = _dataDir },
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
    }

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

    [Fact]
    public async Task PingReturnsProcessId()
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var reply = await new ReviewEngine.ReviewEngineClient(channel).PingAsync(new PingRequest());
        Assert.Equal(Environment.ProcessId, reply.ProcessId);
    }

    [Fact]
    public async Task StreamsSnapshotReviewPointAndCompletion()
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);

        var events = await CollectUntilCompleteAsync(client);

        var snapshot = Assert.Single(events, e => e.EventCase == PullRequestEvent.EventOneofCase.Snapshot).Snapshot;
        Assert.Equal("Add invoice retrying", snapshot.Title);
        Assert.Equal("reviewer", snapshot.ViewerLogin);
        Assert.Contains(snapshot.Files, f => f.Path == WorkerScenario.NewWorkerPath && f.Kind == Contracts.FileChangeKind.Added);

        var point = Assert.Single(events, e => e.EventCase == PullRequestEvent.EventOneofCase.ReviewPointAdded).ReviewPointAdded;
        output.WriteLine($"{point.Title}: {point.Summary}");
        Assert.Contains("QueueClaimService", point.Title, StringComparison.Ordinal);
        Assert.Equal(Contracts.ReviewPointState.Visible, point.State);
        Assert.Equal(WorkerScenario.NewWorkerPath, point.Anchor.Path);
        Assert.NotNull(point.Comparison);
        Assert.Contains(point.Evidence, e => e.Producer == "peer-pattern/v1" && e.Supporting == 4);

        var diff = await client.GetFileDiffAsync(new GetFileDiffRequest { PullRequest = Pr, Path = WorkerScenario.NewWorkerPath });
        Assert.Contains(diff.Hunks.SelectMany(h => h.Lines), l => l.Text.Contains("AppDbContext", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DismissalIsPersistedAndReplayedToNewSubscribers()
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);
        var point = (await CollectUntilCompleteAsync(client)).First(e => e.ReviewPointAdded is not null).ReviewPointAdded;

        await client.SetReviewPointStateAsync(new SetReviewPointStateRequest
        {
            PullRequest = Pr,
            ReviewPointId = point.Id,
            State = Contracts.ReviewPointState.Dismissed,
        });

        // A second client attaching later sees the dismissal in its replay.
        var replay = await CollectUntilAsync(client, e => e.ReviewPointStateChanged is not null, refresh: false);
        Assert.Equal(Contracts.ReviewPointState.Dismissed, replay.Last().ReviewPointStateChanged.State);

        // After a full refresh (new analysis), the stored dismissal is applied to the regenerated point.
        var refreshed = await CollectUntilCompleteAsync(client, refresh: true);
        Assert.Equal(Contracts.ReviewPointState.Dismissed, refreshed.First(e => e.ReviewPointAdded is not null).ReviewPointAdded.State);
    }

    [Fact]
    public async Task PostsCommentOnHeadSideAndMarksPointCommented()
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);
        var point = (await CollectUntilCompleteAsync(client)).First(e => e.ReviewPointAdded is not null).ReviewPointAdded;

        var reply = await client.PostReviewCommentAsync(new PostReviewCommentRequest
        {
            PullRequest = Pr,
            ReviewPointId = point.Id,
            Path = point.Anchor.Path,
            Line = point.Anchor.StartLine,
            Body = "Should this use QueueClaimService?",
        });

        var posted = Assert.Single(_gitHub.Posted);
        Assert.Equal(WorkerScenario.HeadSha, posted.CommitSha);
        Assert.Equal(point.Anchor.StartLine, posted.Line);
        Assert.Equal("Should this use QueueClaimService?", posted.Body);
        Assert.Equal(1001, reply.CommentId);
    }

    [Fact]
    public async Task RejectsCommentsOutsideTheDiff()
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);
        await CollectUntilCompleteAsync(client);

        var ex = await Assert.ThrowsAsync<RpcException>(async () => await client.PostReviewCommentAsync(new PostReviewCommentRequest
        {
            PullRequest = Pr,
            Path = WorkerScenario.ExistingWorkerPath,
            Line = 1,
            Body = "hi",
        }));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Empty(_gitHub.Posted);
    }

    [Fact]
    public async Task SnapshotCarriesGitHubViewedStateAndTicksAreSavedAndReplayed()
    {
        _gitHub.Viewed[WorkerScenario.NewWorkerPath] = true;
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);

        var snapshot = (await CollectUntilCompleteAsync(client)).First(e => e.Snapshot is not null).Snapshot;
        Assert.True(snapshot.Files.Single(f => f.Path == WorkerScenario.NewWorkerPath).IsViewed);

        await client.SetFileViewedAsync(new SetFileViewedRequest { PullRequest = Pr, Path = WorkerScenario.NewWorkerPath, Viewed = false });

        Assert.False(_gitHub.Viewed[WorkerScenario.NewWorkerPath]);
        var replay = await CollectUntilAsync(client, e => e.FileViewed is not null, refresh: false);
        Assert.Equal(new FileViewed { Path = WorkerScenario.NewWorkerPath, Viewed = false }, replay.Last().FileViewed);
    }

    [Fact]
    public async Task ViewedStateIsBestEffortWhenLoadingButReportedWhenSaving()
    {
        _gitHub.FailViewedWith = new Lumen.GitHub.GitHubApiException(System.Net.HttpStatusCode.OK, "GraphQL failed", "Resource not accessible");
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);

        var events = await CollectUntilCompleteAsync(client);
        Assert.All(events.First(e => e.Snapshot is not null).Snapshot.Files, f => Assert.False(f.IsViewed));

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.SetFileViewedAsync(new SetFileViewedRequest { PullRequest = Pr, Path = WorkerScenario.NewWorkerPath, Viewed = true }));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.Contains("GraphQL failed", ex.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsGitHubFailureWithoutCrashing()
    {
        _gitHub.FailWith = new Lumen.GitHub.GitHubAuthenticationException("Run gh auth login.");
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);

        var events = await CollectUntilAsync(client, e => e.Failed is not null, refresh: false);
        Assert.True(events.Last().Failed.IsAuthentication);

        var ping = await client.PingAsync(new PingRequest());
        Assert.Equal(Environment.ProcessId, ping.ProcessId);
    }

    private static Task<List<PullRequestEvent>> CollectUntilCompleteAsync(ReviewEngine.ReviewEngineClient client, bool refresh = false) =>
        CollectUntilAsync(client, e => e.Complete is not null || e.Failed is not null, refresh);

    private static async Task<List<PullRequestEvent>> CollectUntilAsync(
        ReviewEngine.ReviewEngineClient client,
        Func<PullRequestEvent, bool> stop,
        bool refresh)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var call = client.WatchPullRequest(new WatchPullRequestRequest { PullRequest = Pr, Refresh = refresh }, cancellationToken: cts.Token);
        var events = new List<PullRequestEvent>();
        var seenComplete = false;
        await foreach (var evt in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            events.Add(evt);
            seenComplete |= evt.Complete is not null || evt.Failed is not null;
            if (stop(evt) && seenComplete)
            {
                break;
            }
        }

        return events;
    }
}
