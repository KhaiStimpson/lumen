using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumen.Engine.Tests;

/// <summary>Triage acknowledgement through the real engine: stored per pull request like dismissals.</summary>
public sealed class EngineTriageTests : IAsyncLifetime
{
    private static readonly PullRequestRef Pr = new() { Owner = "acme", Name = "billing", Number = 42 };

    private readonly string _pipe = $"lumen-test-{Guid.NewGuid():N}";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"lumen-test-{Guid.NewGuid():N}");
    private Microsoft.AspNetCore.Builder.WebApplication? _engine;

    private static readonly string GroupId = Analysis.TriagePipeline.IdFor("rename:Foo>Bar");

    public async Task InitializeAsync()
    {
        var checkout = WorkerScenario.CreateCheckout(Path.Combine(_dataDir, "checkout"));
        _engine = EngineHost.Build(
            new EngineOptions { PipeName = _pipe, DataDirectory = _dataDir },
            services =>
            {
                services.RemoveAll<IGitHubClient>();
                services.AddSingleton<IGitHubClient>(new FakeGitHub());
                services.RemoveAll<IRepositoryWorkspace>();
                services.AddSingleton<IRepositoryWorkspace>(new FakeWorkspace(checkout));
                services.RemoveAll<ISecretStore>();
                services.AddSingleton<ISecretStore>(new Lumen.Storage.UnavailableSecretStore());
                services.RemoveAll<ITriageStore>();
                services.AddSingleton<ITriageStore>(new CannedTriage());
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
    public async Task AcknowledgingAGroupIsStreamedAndSurvivesReanalysis()
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);

        var first = await TriageAsync(client, refresh: false);
        Assert.False(Assert.Single(first.Groups).Acknowledged);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var live = client.WatchPullRequest(new WatchPullRequestRequest { PullRequest = Pr }, cancellationToken: cts.Token);
        await client.SetTriageGroupAcknowledgedAsync(new SetTriageGroupAcknowledgedRequest { PullRequest = Pr, GroupId = GroupId, Acknowledged = true });
        TriageGroupAcknowledged? echoed = null;
        await foreach (var evt in live.ResponseStream.ReadAllAsync(cts.Token))
        {
            if (evt.TriageGroupAcknowledged is { } ack)
            {
                echoed = ack;
                break;
            }
        }

        Assert.Equal((GroupId, true), (echoed!.GroupId, echoed.Acknowledged));

        var reopened = await TriageAsync(client, refresh: true);
        Assert.True(Assert.Single(reopened.Groups).Acknowledged);

        await client.SetTriageGroupAcknowledgedAsync(new SetTriageGroupAcknowledgedRequest { PullRequest = Pr, GroupId = GroupId, Acknowledged = false });
        var restored = await TriageAsync(client, refresh: true);
        Assert.False(Assert.Single(restored.Groups).Acknowledged);
    }

    [Fact]
    public async Task UnknownGroupIsNotFound()
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);
        await TriageAsync(client, refresh: false);

        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.SetTriageGroupAcknowledgedAsync(new SetTriageGroupAcknowledgedRequest { PullRequest = Pr, GroupId = "gnope", Acknowledged = true }));

        Assert.Equal(StatusCode.NotFound, error.StatusCode);
    }

    private static async Task<TriageReady> TriageAsync(ReviewEngine.ReviewEngineClient client, bool refresh)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var call = client.WatchPullRequest(new WatchPullRequestRequest { PullRequest = Pr, Refresh = refresh }, cancellationToken: cts.Token);
        TriageReady? ready = null;
        await foreach (var evt in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            ready = evt.TriageReady ?? ready;
            if (evt.Complete is not null || evt.Failed is not null)
            {
                break;
            }
        }

        return ready ?? throw new InvalidOperationException("No TriageReady before completion.");
    }

    /// <summary>Serves one rename group for the scenario's head, as if it had been computed and cached earlier.</summary>
    private sealed class CannedTriage : ITriageStore
    {
        public Task<TriageResult?> FindTriageAsync(PullRequestKey key, string headSha, string version, CancellationToken cancellationToken)
        {
            var hunk = new Domain.HunkTriage
            {
                Path = WorkerScenario.NewWorkerPath,
                NewStart = 1,
                NewEnd = 2,
                Class = Domain.ChangeClass.Rename,
                Tier = Domain.TriageTier.Skip,
                Reasons = ["pure rename `Foo`→`Bar`"],
                ChangedLines = 2,
                GroupId = GroupId,
            };
            return Task.FromResult<TriageResult?>(new TriageResult(
                [hunk],
                [new Domain.TriageGroup { Id = GroupId, Class = Domain.ChangeClass.Rename, Title = "Renamed `Foo`→`Bar`", Members = [hunk] }]));
        }

        public Task SaveTriageAsync(PullRequestKey key, string headSha, string version, TriageResult result, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
