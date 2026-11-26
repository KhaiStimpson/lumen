using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumen.Engine.Tests;

/// <summary>Local branches end to end over a real named pipe: read-only on the clone, no GitHub, no posting.</summary>
public sealed class EngineLocalBranchTests : IAsyncLifetime
{
    private readonly string _pipe = $"lumen-test-{Guid.NewGuid():N}";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"lumen-test-{Guid.NewGuid():N}");
    private readonly FakeGitHub _gitHub = new();
    private readonly FixtureRepository _repo = FixtureRepository.Create();
    private Microsoft.AspNetCore.Builder.WebApplication? _engine;
    private Grpc.Net.Client.GrpcChannel? _channel;

    private ReviewEngine.ReviewEngineClient Client => new(_channel!);

    public async Task InitializeAsync()
    {
        _engine = EngineHost.Build(
            new EngineOptions { PipeName = _pipe, DataDirectory = _dataDir },
            services =>
            {
                services.RemoveAll<IGitHubClient>();
                services.AddSingleton<IGitHubClient>(_gitHub);
                services.RemoveAll<Lumen.Storage.ISecretStore>();
                services.AddSingleton<Lumen.Storage.ISecretStore>(new Lumen.Storage.UnavailableSecretStore());
            });
        await _engine.StartAsync();
        _channel = EngineEndpoint.CreateChannel(_pipe);
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        if (_engine is not null)
        {
            await _engine.StopAsync();
            await _engine.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _repo.Dispose();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task AWorkingTreeOpensAgainstItsDefaultBaseWithoutGitHub()
    {
        _repo.Write("src/Shop.Web/Pages/Scratch.cshtml", "@page\n<h1>Uncommitted</h1>\n");
        var status = _repo.Git("--no-optional-locks", "status", "--porcelain");

        var events = await WatchAsync(new LocalBranchRef { ClonePath = _repo.Root, WorkingTree = true });

        var snapshot = Assert.Single(events, e => e.Snapshot is not null).Snapshot;
        Assert.Equal("local", snapshot.PullRequest.Owner);
        Assert.Equal(0, snapshot.PullRequest.Number);
        Assert.Equal("main", snapshot.Local.BaseRef);
        Assert.True(snapshot.Local.WorkingTree);
        Assert.StartsWith("local:", snapshot.TargetKey, StringComparison.Ordinal);
        Assert.Equal("local", snapshot.State);
        Assert.Contains(snapshot.Files, f => f.Path == "src/Shop.Web/Pages/Scratch.cshtml");
        Assert.Contains(snapshot.Files, f => f.Path == "src/Shop.Web/Program.cs");
        Assert.Single(events, e => e.TriageReady is not null);
        Assert.Empty(_gitHub.Posted);

        // Reading the diff works through the synthetic ref; nothing in the clone changed.
        var diff = await Client.GetFileDiffAsync(new GetFileDiffRequest { PullRequest = snapshot.PullRequest, Path = "src/Shop.Web/Pages/Scratch.cshtml" });
        Assert.Contains(diff.Hunks.SelectMany(h => h.Lines), l => l.Text.Contains("Uncommitted", StringComparison.Ordinal));
        Assert.Equal(status, _repo.Git("--no-optional-locks", "status", "--porcelain"));
    }

    [Fact]
    public async Task PostingIsRefusedForALocalBranch()
    {
        var events = await WatchAsync(new LocalBranchRef { ClonePath = _repo.Root, BaseRef = "main", HeadRef = FixtureRepository.HeadBranch });
        var snapshot = events.First(e => e.Snapshot is not null).Snapshot;
        var line = (await Client.GetFileDiffAsync(new GetFileDiffRequest { PullRequest = snapshot.PullRequest, Path = "src/Shop.Web/Program.cs" }))
            .Hunks.SelectMany(h => h.Lines).First(l => l.Kind == Contracts.DiffLineKind.Added).NewNumber;

        var post = await Assert.ThrowsAsync<RpcException>(async () => await Client.PostReviewCommentAsync(new PostReviewCommentRequest
        {
            PullRequest = snapshot.PullRequest,
            Path = "src/Shop.Web/Program.cs",
            Line = line,
            Body = "hi",
        }));
        var viewed = await Assert.ThrowsAsync<RpcException>(async () => await Client.SetFileViewedAsync(new SetFileViewedRequest
        {
            PullRequest = snapshot.PullRequest,
            Path = "src/Shop.Web/Program.cs",
            Viewed = true,
        }));

        Assert.Equal(StatusCode.FailedPrecondition, post.StatusCode);
        Assert.Equal(StatusCode.FailedPrecondition, viewed.StatusCode);
        Assert.Empty(_gitHub.Posted);
    }

    [Fact]
    public async Task OpenedLocalBranchesAreListedForTheInbox()
    {
        await WatchAsync(new LocalBranchRef { ClonePath = _repo.Root, BaseRef = "main", HeadRef = FixtureRepository.HeadBranch });

        var listed = Assert.Single((await Client.ListLocalBranchesAsync(new ListLocalBranchesRequest())).Items);

        Assert.Equal(FixtureRepository.HeadBranch, listed.Ref.HeadRef);
        Assert.False(listed.Ref.WorkingTree);
        Assert.Equal("main", listed.Ref.BaseRef);
        Assert.NotNull(listed.LastOpenedAt);
    }

    [Fact]
    public async Task AMissingBaseIsExplained()
    {
        var events = await WatchAsync(new LocalBranchRef { ClonePath = _repo.Root, BaseRef = "nope", WorkingTree = true });

        Assert.Contains("'nope'", Assert.Single(events, e => e.Failed is not null).Failed.Message, StringComparison.Ordinal);
    }

    private async Task<List<PullRequestEvent>> WatchAsync(LocalBranchRef local)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var call = Client.WatchPullRequest(new WatchPullRequestRequest { Local = local }, cancellationToken: cts.Token);
        var events = new List<PullRequestEvent>();
        await foreach (var evt in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            events.Add(evt);
            if (evt.Complete is not null || evt.Failed is not null)
            {
                break;
            }
        }

        return events;
    }
}
