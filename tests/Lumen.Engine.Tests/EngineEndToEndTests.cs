using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace Lumen.Engine.Tests;

/// <summary>Drives the real engine over a real named pipe, with GitHub and git replaced by in-memory fakes.</summary>
public sealed class EngineEndToEndTests : IAsyncLifetime
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
                services.RemoveAll<Lumen.Storage.ISecretStore>();
                services.AddSingleton<Lumen.Storage.ISecretStore>(new Lumen.Storage.UnavailableSecretStore());
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
    public async Task SourceFileRequestsSelectTheRequestedRevision()
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);
        var events = await CollectUntilCompleteAsync(client);
        Assert.DoesNotContain(events, e => e.Failed is not null);

        var head = await client.GetSourceFileAsync(new GetSourceFileRequest { PullRequest = Pr, Path = WorkerScenario.NewWorkerPath });
        var before = await client.GetSourceFileAsync(new GetSourceFileRequest { PullRequest = Pr, Path = WorkerScenario.NewWorkerPath, FromBase = true });

        Assert.True(head.Exists);
        Assert.Contains("InvoiceRetryWorker", head.Text, StringComparison.Ordinal);
        Assert.False(before.Exists);
        Assert.Empty(before.Text);
    }

    [Fact]
    public async Task StreamsSnapshotTriageAndCompletionWithoutReviewPoints()
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);

        var events = await CollectUntilCompleteAsync(client);

        var snapshot = Assert.Single(events, e => e.EventCase == PullRequestEvent.EventOneofCase.Snapshot).Snapshot;
        Assert.Equal("Add invoice retrying", snapshot.Title);
        Assert.Equal("reviewer", snapshot.ViewerLogin);
        Assert.Equal("pr:acme/billing#42", snapshot.TargetKey);
        Assert.Null(snapshot.Local);
        Assert.Contains(snapshot.Files, f => f.Path == WorkerScenario.NewWorkerPath && f.Kind == Contracts.FileChangeKind.Added);
        Assert.Single(events, e => e.EventCase == PullRequestEvent.EventOneofCase.TriageReady);
        Assert.Single(events, e => e.EventCase == PullRequestEvent.EventOneofCase.Complete);

        var diff = await client.GetFileDiffAsync(new GetFileDiffRequest { PullRequest = Pr, Path = WorkerScenario.NewWorkerPath });
        Assert.Contains(diff.Hunks.SelectMany(h => h.Lines), l => l.Text.Contains("AppDbContext", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PostsCommentOnTheHeadSide()
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);
        await CollectUntilCompleteAsync(client);
        var diff = await client.GetFileDiffAsync(new GetFileDiffRequest { PullRequest = Pr, Path = WorkerScenario.NewWorkerPath });
        var line = diff.Hunks.SelectMany(h => h.Lines).First(l => l.Kind == Contracts.DiffLineKind.Added).NewNumber;

        var reply = await client.PostReviewCommentAsync(new PostReviewCommentRequest
        {
            PullRequest = Pr,
            Path = WorkerScenario.NewWorkerPath,
            Line = line,
            Body = "Should this use QueueClaimService?",
        });

        var posted = Assert.Single(_gitHub.Posted);
        Assert.Equal(WorkerScenario.HeadSha, posted.CommitSha);
        Assert.Equal(line, posted.Line);
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

    [Theory]
    [InlineData("network")]
    [InlineData("server")]
    [InlineData("timeout")]
    public async Task TransientGitHubFailuresUseTheCachedPullRequest(string failure)
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);
        await CollectUntilCompleteAsync(client);
        _gitHub.FailWith = failure switch
        {
            "server" => new Lumen.GitHub.GitHubApiException(System.Net.HttpStatusCode.ServiceUnavailable, "GitHub unavailable"),
            "timeout" => new TaskCanceledException("HTTP request timed out"),
            _ => new HttpRequestException("Network offline"),
        };

        var events = await CollectUntilCompleteAsync(client, refresh: true);

        Assert.Contains(events, e => e.Snapshot is not null);
        Assert.Contains(events, e => e.Complete is not null);
        Assert.DoesNotContain(events, e => e.Failed is not null);
    }

    [Fact]
    public async Task CachedPullRequestDoesNotMaskRevokedAuthentication()
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);
        await CollectUntilCompleteAsync(client);
        _gitHub.FailWith = new Lumen.GitHub.GitHubApiException(System.Net.HttpStatusCode.Unauthorized, "Bad credentials");

        var events = await CollectUntilCompleteAsync(client, refresh: true);

        Assert.True(Assert.Single(events, e => e.Failed is not null).Failed.IsAuthentication);
        Assert.DoesNotContain(events, e => e.Snapshot is not null);
    }

    [Fact]
    public async Task TriagesTheSnapshotAndCachesItPerHead()
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        await CollectUntilCompleteAsync(new ReviewEngine.ReviewEngineClient(channel));

        var key = new PullRequestKey(new RepositoryRef(Pr.Owner, Pr.Name), Pr.Number);
        var session = _engine!.Services.GetRequiredService<Sessions.PullRequestSessionManager>().Find(key);
        var triage = session!.Triage;
        Assert.NotNull(triage);
        Assert.Contains(triage.Hunks, h => h.Path == WorkerScenario.NewWorkerPath && h.Class == Domain.ChangeClass.NewCode);

        using var replay = EngineEndpoint.CreateChannel(_pipe);
        var events = await CollectUntilCompleteAsync(new ReviewEngine.ReviewEngineClient(replay));
        var kinds = events.Select(e => e.EventCase).ToList();
        var ready = Assert.Single(events, e => e.EventCase == PullRequestEvent.EventOneofCase.TriageReady).TriageReady;
        Assert.True(kinds.IndexOf(PullRequestEvent.EventOneofCase.Snapshot) < kinds.IndexOf(PullRequestEvent.EventOneofCase.TriageReady));
        Assert.True(kinds.IndexOf(PullRequestEvent.EventOneofCase.TriageReady) < kinds.IndexOf(PullRequestEvent.EventOneofCase.Complete));
        Assert.Equal(triage.Hunks.Count, ready.Hunks.Count);
        Assert.True(ready.Files.Single(f => f.Path == WorkerScenario.NewWorkerPath).FoldLines.Open > 0);

        var cached = await _engine.Services.GetRequiredService<ITriageStore>()
            .FindTriageAsync(new PullRequestTarget(key), WorkerScenario.HeadSha, Sessions.TriageRunner.VersionFor(Analysis.ReviewSettings.Default, session.Snapshot!.MergeBaseSha), CancellationToken.None);
        Assert.Equal(triage.Hunks.Count, cached!.Hunks.Count);
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
