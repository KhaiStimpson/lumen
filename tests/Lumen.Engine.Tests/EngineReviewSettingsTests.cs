using Grpc.Core;
using Lumen.Agents;
using Lumen.Analysis;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Jev;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumen.Engine.Tests;

/// <summary>The Settings overlay's review and Cloud AI limit calls over a real named pipe.</summary>
public sealed class EngineReviewSettingsTests : IAsyncDisposable
{
    private readonly string _pipe = $"lumen-test-{Guid.NewGuid():N}";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"lumen-test-{Guid.NewGuid():N}");
    private Microsoft.AspNetCore.Builder.WebApplication? _engine;
    private Grpc.Net.Client.GrpcChannel? _channel;

    private static Contracts.ReviewSensitivity Quiet => new()
    {
        MinimumPeers = 5,
        MinimumSupport = 0.85,
        MinimumLift = 2.0,
        MaxPointsPerType = 1,
        MaxExamples = 3,
    };

    private static Contracts.ReviewSensitivity Balanced => new()
    {
        MinimumPeers = 3,
        MinimumSupport = 0.75,
        MinimumLift = 1.5,
        MaxPointsPerType = 2,
        MaxExamples = 4,
    };

    private async Task<ReviewEngine.ReviewEngineClient> StartAsync()
    {
        _engine = EngineHost.Build(
            new EngineOptions { PipeName = _pipe, DataDirectory = _dataDir },
            services =>
            {
                services.RemoveAll<EngineSettings>();
                services.AddSingleton(new EngineSettings());
                services.RemoveAll<ISecretStore>();
                services.AddSingleton<ISecretStore>(new EngineConnectionsTests.MemorySecretStore());
                services.RemoveAll<IOpenRouterKeyCheck>();
                services.AddSingleton<IOpenRouterKeyCheck>(new EngineConnectionsTests.FakeKeyCheck());
                services.RemoveAll<IAgentProvider>();
                services.AddSingleton<IAgentProvider>(new EngineConnectionsTests.SignedInAgent());
            });
        await _engine.StartAsync();
        _channel = EngineEndpoint.CreateChannel(_pipe);
        return new ReviewEngine.ReviewEngineClient(_channel);
    }

    public async ValueTask DisposeAsync()
    {
        _channel?.Dispose();
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
    public async Task GlobalDefaultsAreBalancedWithTheBuiltInsListed()
    {
        var client = await StartAsync();

        var reply = await client.GetReviewSettingsAsync(new ReviewSettingsRequest());

        Assert.Equal(3, reply.Global.Sensitivity.MinimumPeers);
        Assert.Equal(0.75, reply.Global.Sensitivity.MinimumSupport);
        Assert.Null(reply.Repository);
        Assert.Contains("CancellationToken", reply.BuiltInIgnoredNames);
        Assert.Contains("*.g.cs", reply.BuiltInMechanicalPaths);
        Assert.EndsWith("settings.json", reply.SettingsPath, StringComparison.Ordinal);
        Assert.Equal(5, reply.Quiet.MinimumPeers);
        Assert.Equal(reply.Global.Sensitivity, reply.Balanced);
        Assert.Equal(0.65, reply.Thorough.MinimumSupport);
    }

    [Fact]
    public async Task GlobalChangesAreSavedToSettingsJson()
    {
        var client = await StartAsync();
        var rules = new ReviewRules { Sensitivity = Quiet };
        rules.IgnoredNames.Add(" IClock ");
        rules.IgnoredNames.Add("IClock");
        rules.MechanicalPaths.Add("src/Generated/**");

        var reply = await client.UpdateReviewSettingsAsync(new UpdateReviewSettingsRequest { Rules = rules });

        Assert.Equal(5, reply.Global.Sensitivity.MinimumPeers);
        Assert.Equal(["IClock"], reply.Global.IgnoredNames);
        var saved = EngineSettings.Load(_dataDir);
        Assert.Equal(SensitivityPreset.Quiet, saved.Review.Sensitivity.Preset);
        Assert.Equal(["src/Generated/**"], saved.Review.MechanicalPaths);
    }

    [Fact]
    public async Task ARepositoryOverridesSensitivityAndAddsToTheLists()
    {
        var client = await StartAsync();
        var global = new ReviewRules { Sensitivity = Balanced };
        global.IgnoredNames.Add("IClock");
        await client.UpdateReviewSettingsAsync(new UpdateReviewSettingsRequest { Rules = global });

        var own = new ReviewRules { Sensitivity = Quiet };
        own.IgnoredNames.Add("OrdersDbContext");
        own.SkippedPaths.Add("src/Legacy/");
        var reply = await client.UpdateReviewSettingsAsync(new UpdateReviewSettingsRequest { Owner = "northwind", Name = "orders-api", Rules = own });

        Assert.Equal(5, reply.Repository.Sensitivity.MinimumPeers);
        Assert.Equal(3, reply.Global.Sensitivity.MinimumPeers);
        var path = Path.Combine(_dataDir, "review", "northwind", "orders-api.json");
        Assert.Equal(path, reply.RepositorySettingsPath);
        Assert.True(File.Exists(path));

        var store = _engine!.Services.GetRequiredService<ReviewSettingsStore>();
        var resolved = store.Resolve(new RepositoryRef("northwind", "orders-api"));
        Assert.Equal(SensitivityPreset.Quiet, resolved.Sensitivity.Preset);
        Assert.Equal(["IClock", "OrdersDbContext"], resolved.IgnoredNames);
        Assert.True(resolved.IsSkipped("src/Legacy/Old.cs"));

        // Another repository still gets only the global settings.
        var other = store.Resolve(new RepositoryRef("northwind", "billing"));
        Assert.Equal(SensitivityPreset.Balanced, other.Sensitivity.Preset);
        Assert.Equal(["IClock"], other.IgnoredNames);
    }

    [Fact]
    public async Task ResettingEverythingRemovesTheRepositoryFile()
    {
        var client = await StartAsync();
        await client.UpdateReviewSettingsAsync(new UpdateReviewSettingsRequest { Owner = "northwind", Name = "orders-api", Rules = new ReviewRules { Sensitivity = Quiet } });

        var reply = await client.UpdateReviewSettingsAsync(new UpdateReviewSettingsRequest { Owner = "northwind", Name = "orders-api", Rules = new ReviewRules() });

        Assert.Null(reply.Repository.Sensitivity);
        Assert.False(File.Exists(reply.RepositorySettingsPath));
    }

    [Theory]
    [InlineData("..", "orders-api")]
    [InlineData("northwind", "a/b")]
    [InlineData("northwind", "")]
    public async Task RepositoryNamesThatCouldEscapeTheDataFolderAreRejected(string owner, string name)
    {
        var client = await StartAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.UpdateReviewSettingsAsync(new UpdateReviewSettingsRequest { Owner = owner, Name = name, Rules = new ReviewRules { Sensitivity = Quiet } }));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task CloudAiLimitsChangeOnlyWhenSet()
    {
        var client = await StartAsync();

        var status = await client.UpdateConnectionSettingsAsync(new ConnectionSettings
        {
            AllowCloudReasoning = true,
            JevEnabled = true,
            JevModel = " ~typesafe/jev-latest ",
            MaxInvestigationsPerPullRequest = 99,
            AllowMeteredUsage = true,
        });

        Assert.Equal("~typesafe/jev-latest", status.Settings.JevModel);
        Assert.Equal(20, status.Settings.MaxInvestigationsPerPullRequest);
        Assert.True(status.Settings.AllowMeteredUsage);
        Assert.Equal(12, status.Settings.JevTimeoutSeconds);
        Assert.True(status.Settings.JevRequireZeroDataRetention);
        Assert.Equal(1, status.Settings.MaxConcurrentInvestigations);
        Assert.Equal("~typesafe/jev-latest", status.Jev.Model);

        var saved = EngineSettings.Load(_dataDir);
        Assert.Equal("~typesafe/jev-latest", saved.Jev.Model);
        Assert.True(saved.Agents.AllowMeteredUsage);
    }

    [Fact]
    public async Task ABlankJevModelIsRejected()
    {
        var client = await StartAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.UpdateConnectionSettingsAsync(new ConnectionSettings { AllowCloudReasoning = true, JevModel = "  " }));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}
