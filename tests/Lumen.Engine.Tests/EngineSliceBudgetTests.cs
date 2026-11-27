using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumen.Engine.Tests;

/// <summary>Slicing past its budget falls back to the file list without holding back the folds or the red flags.</summary>
public sealed class EngineSliceBudgetTests : IAsyncLifetime
{
    private readonly string _pipe = $"lumen-test-{Guid.NewGuid():N}";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"lumen-test-{Guid.NewGuid():N}");
    private readonly FixtureRepository _repo = FixtureRepository.Create();
    private Microsoft.AspNetCore.Builder.WebApplication? _engine;
    private Grpc.Net.Client.GrpcChannel? _channel;

    public async Task InitializeAsync()
    {
        _engine = EngineHost.Build(
            new EngineOptions { PipeName = _pipe, DataDirectory = _dataDir },
            services =>
            {
                services.RemoveAll<IGitHubClient>();
                services.AddSingleton<IGitHubClient>(new FakeGitHub());
                services.RemoveAll<Lumen.Storage.ISecretStore>();
                services.AddSingleton<Lumen.Storage.ISecretStore>(new Lumen.Storage.UnavailableSecretStore());
                services.RemoveAll<Overhaul.OverhaulOptions>();
                services.AddSingleton(new Overhaul.OverhaulOptions { SliceBudget = TimeSpan.Zero });
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
    public async Task OverBudgetFallsBackToTheFileListAndTheFlagsStillArrive()
    {
        var client = new ReviewEngine.ReviewEngineClient(_channel!);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var call = client.WatchPullRequest(
            new WatchPullRequestRequest { Local = new LocalBranchRef { ClonePath = _repo.Root, BaseRef = "main", HeadRef = FixtureRepository.HeadBranch } },
            cancellationToken: cts.Token);
        var events = new List<PullRequestEvent>();
        await foreach (var evt in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            events.Add(evt);
            if (evt.Complete is not null || evt.Failed is not null)
            {
                break;
            }
        }

        var ready = Assert.Single(events, e => e.SlicesReady is not null).SlicesReady;
        Assert.True(ready.FellBack);
        Assert.Equal("Slicing took longer than 0 s, so the change is shown as a file list.", ready.FallbackReason);
        Assert.Single(events, e => e.TriageReady is not null);
        Assert.NotEmpty(Assert.Single(events, e => e.FlagsReady is not null).FlagsReady.Flags);
        Assert.Single(events, e => e.Complete is not null);
    }
}
