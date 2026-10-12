using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumen.Engine.Tests;

/// <summary>The fast model and the walkthrough through the engine (plan v2 M7, M8), with the model faked.</summary>
public sealed class EngineWalkthroughTests : IAsyncLifetime, IDisposable
{
    private static readonly PullRequestRef Pr = new() { Owner = "acme", Name = "billing", Number = 42 };

    private readonly string _pipe = $"lumen-test-{Guid.NewGuid():N}";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"lumen-test-{Guid.NewGuid():N}");
    private readonly FakeGitHub _gitHub = new();
    private readonly FakeModel _model;
    private readonly EngineConnectionsTests.MemorySecretStore _secrets = new();
    private Microsoft.AspNetCore.Builder.WebApplication? _engine;
    private Grpc.Net.Client.GrpcChannel? _channel;

    private ReviewEngine.ReviewEngineClient Client { get; set; } = null!;

    public EngineWalkthroughTests()
    {
        _model = new FakeModel(_secrets);
        _secrets.Write(SecretNames.CloudflareAccountId, "acct");
        _secrets.Write(SecretNames.CloudflareApiToken, "token");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task StartAsync(bool allowCode = true)
    {
        var checkout = WorkerScenario.CreateCheckout(Path.Combine(_dataDir, "checkout"));
        _engine = EngineHost.Build(
            new EngineOptions { PipeName = _pipe, DataDirectory = _dataDir },
            services =>
            {
                services.RemoveAll<EngineSettings>();
                services.AddSingleton(new EngineSettings { Privacy = new PrivacySettings { AllowCodeToFastModel = allowCode } });
                services.RemoveAll<IGitHubClient>();
                services.AddSingleton<IGitHubClient>(_gitHub);
                services.RemoveAll<IRepositoryWorkspace>();
                services.AddSingleton<IRepositoryWorkspace>(new FakeWorkspace(checkout));
                services.RemoveAll<ISecretStore>();
                services.AddSingleton<ISecretStore>(_secrets);
                services.RemoveAll<IModelProvider>();
                services.AddSingleton<IModelProvider>(_model);
            });
        await _engine.StartAsync();
        _channel = EngineEndpoint.CreateChannel(_pipe);
        Client = new ReviewEngine.ReviewEngineClient(_channel);
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

    [Fact]
    public async Task WalkthroughStreamsAfterTheDiffAndIsCachedForTheNextOpen()
    {
        await StartAsync();

        var events = await CollectUntilAsync(e => e.WalkthroughUpdated?.Walkthrough.Status == Contracts.WalkthroughStatus.Complete);
        var updates = events.Where(e => e.WalkthroughUpdated is not null).Select(e => e.WalkthroughUpdated.Walkthrough).ToList();

        // The diff came first; the walkthrough never holds it up.
        Assert.True(events.FindIndex(e => e.Snapshot is not null) < events.FindIndex(e => e.WalkthroughUpdated is not null));
        Assert.Equal(Contracts.WalkthroughStatus.Running, updates[0].Status);
        var done = updates[^1];
        Assert.Equal("Adds a retry worker.", done.Intent);
        Assert.Equal(WorkerScenario.NewWorkerPath, Assert.Single(done.Groups).Paths.Single());
        var risk = Assert.Single(done.Risks);
        Assert.Equal((WorkerScenario.NewWorkerPath, 5), (risk.Location.Path, risk.Location.StartLine));
        Assert.False(done.FromCache);
        var requests = _model.Requests.Count;

        // Reopening (a fresh analysis) serves it from the cache without a single model request.
        var again = await CollectUntilAsync(e => e.WalkthroughUpdated?.Walkthrough.FromCache == true, refresh: true);
        Assert.Equal("Adds a retry worker.", again.Last().WalkthroughUpdated.Walkthrough.Intent);
        Assert.Equal(requests, _model.Requests.Count);
    }

    [Fact]
    public async Task WithoutPermissionToSendCodeNothingIsSentAndTheWalkthroughSaysWhy()
    {
        await StartAsync(allowCode: false);

        var events = await CollectUntilAsync(e => e.WalkthroughUpdated is not null);

        var walkthrough = events.Last(e => e.WalkthroughUpdated is not null).WalkthroughUpdated.Walkthrough;
        Assert.Equal(Contracts.WalkthroughStatus.Unavailable, walkthrough.Status);
        Assert.Contains("Connections", walkthrough.Detail, StringComparison.Ordinal);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task AFailingModelLeavesTheReviewIntact()
    {
        _model.Fail = true;
        await StartAsync();

        var events = await CollectUntilAsync(e => e.WalkthroughUpdated?.Walkthrough.Status == Contracts.WalkthroughStatus.Failed);

        Assert.Contains(events, e => e.ReviewPointAdded is not null);
        Assert.Contains("works as usual", events.Last(e => e.WalkthroughUpdated is not null).WalkthroughUpdated.Walkthrough.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FastModelConnectionAndLimitsAreEditable()
    {
        _secrets.Delete(SecretNames.CloudflareAccountId);
        _secrets.Delete(SecretNames.CloudflareApiToken);
        await StartAsync(allowCode: false);

        var status = await Client.GetConnectionsAsync(new GetConnectionsRequest());
        Assert.Equal(ConnectionState.NotConnected, status.FastModel.State);
        Assert.Equal("@cf/zai-org/glm-5.3-flash", status.FastModel.Model);

        var bad = await Assert.ThrowsAsync<RpcException>(async () =>
            await Client.SetCloudflareCredentialsAsync(new SetCloudflareCredentialsRequest { AccountId = "acct", ApiToken = " " }));
        Assert.Equal(StatusCode.InvalidArgument, bad.StatusCode);

        status = await Client.SetCloudflareCredentialsAsync(new SetCloudflareCredentialsRequest { AccountId = " acct ", ApiToken = "s3cr3t" });
        Assert.Equal("acct", _secrets.Read(SecretNames.CloudflareAccountId));
        Assert.DoesNotContain("s3cr3t", status.ToString(), StringComparison.Ordinal);

        status = await Client.UpdateConnectionSettingsAsync(new ConnectionSettings
        {
            AllowCloudReasoning = true,
            AllowCodeToFastModel = true,
            FastModelDailyRequestCap = 50,
            FastModelMaxDiffKb = 1,
            FastModelGateway = "gw",
        });
        Assert.True(status.Settings.AllowCodeToFastModel);
        Assert.Equal(50, status.Settings.FastModelDailyRequestCap);
        Assert.Equal(16, status.Settings.FastModelMaxDiffKb);
        Assert.Equal("gw", status.Settings.FastModelGateway);
        Assert.Equal(ConnectionState.Connected, status.FastModel.State);

        status = await Client.RemoveCloudflareCredentialsAsync(new RemoveCloudflareCredentialsRequest());
        Assert.Null(_secrets.Read(SecretNames.CloudflareApiToken));
        Assert.Equal(ConnectionState.NotConnected, status.FastModel.State);
    }

    [Fact]
    public async Task JevIsLegacyAndOffByDefault()
    {
        await StartAsync();

        var status = await Client.GetConnectionsAsync(new GetConnectionsRequest());

        Assert.False(status.Settings.JevEnabled);
        Assert.Equal(ConnectionState.Off, status.Jev.State);
    }

    private async Task<List<PullRequestEvent>> CollectUntilAsync(Func<PullRequestEvent, bool> stop, bool refresh = false)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var call = Client.WatchPullRequest(new WatchPullRequestRequest { PullRequest = Pr, Refresh = refresh }, cancellationToken: cts.Token);
        var events = new List<PullRequestEvent>();
        bool complete = false, matched = false;
        await foreach (var evt in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            events.Add(evt);
            complete |= evt.Complete is not null;
            matched |= stop(evt);
            if (matched && complete)
            {
                break;
            }
        }

        return events;
    }

    /// <summary>Answers each walkthrough step for the worker scenario; credentials always present.</summary>
    private sealed class FakeModel(ISecretStore secrets) : IModelProvider
    {
        public List<ModelRequest> Requests { get; } = [];

        public bool Fail { get; set; }

        public string Name => "fake";

        public string Model => "@cf/test/glm";

        public bool HasCredentials => secrets.Read(SecretNames.CloudflareApiToken) is not null;

        public Task<ModelCheck> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(new ModelCheck(true, "ok"));

        public Task<ModelCompletion> CompleteAsync(ModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            if (Fail)
            {
                throw new ModelException(ModelFailure.Unavailable, "Cloudflare is down.");
            }

            var text = request.Purpose switch
            {
                "walkthrough/intent" => """{"intent":"Adds a retry worker."}""",
                "walkthrough/file" => """{"summary":"New worker.","kind":"logic","risks":[{"line":5,"category":"behaviour-change","reason":"Queries the DbContext directly."}]}""",
                "walkthrough/groups" => $$"""{"groups":[{"title":"Retry worker","summary":"New background worker.","paths":["{{WorkerScenario.NewWorkerPath}}"]}],"descriptionMismatch":""}""",
                _ => "{}",
            };
            return Task.FromResult(new ModelCompletion(text, Model, 1, 1, TimeSpan.Zero));
        }
    }
}
