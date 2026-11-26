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
                services.RemoveAll<Lumen.Storage.ISecretStore>();
                services.AddSingleton<Lumen.Storage.ISecretStore>(new Lumen.Storage.UnavailableSecretStore());
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
    public async Task CachedTriageIsServedWithItsFoldsAndGroups()
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);

        var ready = await TriageAsync(client, refresh: false);

        var group = Assert.Single(ready.Groups);
        Assert.Equal((GroupId, 1, 2), (group.Id, group.MemberCount, group.ChangedLines));
        var hunk = Assert.Single(ready.Hunks);
        Assert.Equal(Contracts.FoldKind.Mechanical, hunk.Fold);
        Assert.Equal(2, ready.Summary.MechanicalLines);
        Assert.Equal(0, ready.Summary.OpenLines);
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
        public Task<TriageResult?> FindTriageAsync(ReviewTarget key, string headSha, string version, CancellationToken cancellationToken)
        {
            var hunk = new Domain.HunkTriage
            {
                Path = WorkerScenario.NewWorkerPath,
                NewStart = 1,
                NewEnd = 2,
                Class = Domain.ChangeClass.Rename,
                Fold = Domain.FoldKind.Mechanical,
                Reasons = ["pure rename `Foo`→`Bar`"],
                ChangedLines = 2,
                GroupId = GroupId,
            };
            return Task.FromResult<TriageResult?>(new TriageResult(
                [hunk],
                [new Domain.TriageGroup { Id = GroupId, Class = Domain.ChangeClass.Rename, Title = "Renamed `Foo`→`Bar`", Members = [hunk] }]));
        }

        public Task SaveTriageAsync(ReviewTarget key, string headSha, string version, TriageResult result, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
