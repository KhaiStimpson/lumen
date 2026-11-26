using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumen.Engine.Tests;

/// <summary>Settings → Files over a real named pipe, and what the engine drops on start (removed AI settings and keys).</summary>
public sealed class EngineReviewSettingsTests : IAsyncDisposable
{
    private readonly string _pipe = $"lumen-test-{Guid.NewGuid():N}";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"lumen-test-{Guid.NewGuid():N}");
    private readonly MemorySecretStore _secrets = new();
    private Microsoft.AspNetCore.Builder.WebApplication? _engine;
    private Grpc.Net.Client.GrpcChannel? _channel;

    private async Task<ReviewEngine.ReviewEngineClient> StartAsync(bool keepSettings = false)
    {
        _engine = EngineHost.Build(
            new EngineOptions { PipeName = _pipe, DataDirectory = _dataDir },
            services =>
            {
                if (!keepSettings)
                {
                    services.RemoveAll<EngineSettings>();
                    services.AddSingleton(new EngineSettings());
                }

                services.RemoveAll<Lumen.Storage.ISecretStore>();
                services.AddSingleton<ISecretStore>(_secrets);
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
    public async Task GlobalDefaultsListTheBuiltInMechanicalRules()
    {
        var client = await StartAsync();

        var reply = await client.GetReviewSettingsAsync(new ReviewSettingsRequest());

        Assert.Empty(reply.Global.MechanicalPaths);
        Assert.Null(reply.Repository);
        Assert.Contains("*.g.cs", reply.BuiltInMechanicalPaths);
        Assert.EndsWith("settings.json", reply.SettingsPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlobalChangesAreSavedToSettingsJson()
    {
        var client = await StartAsync();
        var rules = new ReviewRules();
        rules.MechanicalPaths.Add(" src/Generated/** ");
        rules.MechanicalPaths.Add("src/Generated/**");
        rules.SkippedPaths.Add("src/Legacy/");

        var reply = await client.UpdateReviewSettingsAsync(new UpdateReviewSettingsRequest { Rules = rules });

        Assert.Equal(["src/Generated/**"], reply.Global.MechanicalPaths);
        var saved = EngineSettings.Load(_dataDir);
        Assert.Equal(["src/Generated/**"], saved.Review.MechanicalPaths);
        Assert.Equal(["src/Legacy/"], saved.Review.SkippedPaths);
    }

    [Fact]
    public async Task ARepositoryAddsToTheGlobalLists()
    {
        var client = await StartAsync();
        var global = new ReviewRules();
        global.MechanicalPaths.Add("src/Generated/**");
        await client.UpdateReviewSettingsAsync(new UpdateReviewSettingsRequest { Rules = global });

        var own = new ReviewRules();
        own.SkippedPaths.Add("src/Legacy/");
        var reply = await client.UpdateReviewSettingsAsync(new UpdateReviewSettingsRequest { Owner = "northwind", Name = "orders-api", Rules = own });

        var path = Path.Combine(_dataDir, "review", "northwind", "orders-api.json");
        Assert.Equal(path, reply.RepositorySettingsPath);
        Assert.True(File.Exists(path));

        var store = _engine!.Services.GetRequiredService<ReviewSettingsStore>();
        var resolved = store.Resolve(new RepositoryRef("northwind", "orders-api"));
        Assert.Equal(["src/Generated/**"], resolved.MechanicalPaths);
        Assert.True(resolved.IsSkipped("src/Legacy/Old.cs"));
        Assert.False(store.Resolve(new RepositoryRef("northwind", "billing")).IsSkipped("src/Legacy/Old.cs"));
    }

    [Fact]
    public async Task LocalClonesRunWithTheGlobalSettings()
    {
        var client = await StartAsync();
        var global = new ReviewRules();
        global.MechanicalPaths.Add("src/Generated/**");
        await client.UpdateReviewSettingsAsync(new UpdateReviewSettingsRequest { Rules = global });

        var store = _engine!.Services.GetRequiredService<ReviewSettingsStore>();
        var local = new LocalBranchTarget(@"C:\src\my app", "main", LocalHeadKind.WorkingTree);

        Assert.Equal(["src/Generated/**"], store.Resolve(local.SyntheticKey.Repository).MechanicalPaths);
    }

    [Fact]
    public async Task ResettingEverythingRemovesTheRepositoryFile()
    {
        var client = await StartAsync();
        var own = new ReviewRules();
        own.SkippedPaths.Add("src/Legacy/");
        await client.UpdateReviewSettingsAsync(new UpdateReviewSettingsRequest { Owner = "northwind", Name = "orders-api", Rules = own });

        var reply = await client.UpdateReviewSettingsAsync(new UpdateReviewSettingsRequest { Owner = "northwind", Name = "orders-api", Rules = new ReviewRules() });

        Assert.False(File.Exists(reply.RepositorySettingsPath));
    }

    [Theory]
    [InlineData("..", "orders-api")]
    [InlineData("northwind", "a/b")]
    [InlineData("northwind", "")]
    public async Task RepositoryNamesThatCouldEscapeTheDataFolderAreRejected(string owner, string name)
    {
        var client = await StartAsync();
        var rules = new ReviewRules();
        rules.SkippedPaths.Add("x/");

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.UpdateReviewSettingsAsync(new UpdateReviewSettingsRequest { Owner = owner, Name = name, Rules = rules }));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task RemovedAiSettingsAreDroppedFromSettingsJsonOnStart()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(EngineSettings.PathIn(_dataDir), """
            {
              "privacy": { "allowCodeToAgents": true, "allowCodeToFastModel": true },
              "jev": { "enabled": true, "model": "typesafe/jev-1.13" },
              "agents": { "enabled": true },
              "fastModel": { "model": "@cf/zai-org/glm-5.3-flash" },
              "inbox": { "watchedRepositories": ["acme/shop"] },
              "review": { "sensitivity": { "minimumPeers": 5 }, "ignoredNames": ["IClock"], "mechanicalPaths": ["src/Generated/**"] }
            }
            """);

        await StartAsync(keepSettings: true);
        _ = _engine!.Services.GetRequiredService<EngineSettings>();

        var json = File.ReadAllText(EngineSettings.PathIn(_dataDir));
        Assert.False(EngineSettings.HasUnknownKeys(json));
        Assert.DoesNotContain("jev", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitivity", json, StringComparison.OrdinalIgnoreCase);
        var saved = EngineSettings.Load(_dataDir);
        Assert.Equal(["acme/shop"], saved.Inbox.WatchedRepositories);
        Assert.Equal(["src/Generated/**"], saved.Review.MechanicalPaths);
    }

    [Fact]
    public void KnownSettingsAreLeftAlone()
    {
        Assert.False(EngineSettings.HasUnknownKeys("""{ "inbox": { "pollSeconds": 60 }, "review": { "skippedPaths": [] } }"""));
        Assert.True(EngineSettings.HasUnknownKeys("""{ "privacy": {} }"""));
        Assert.True(EngineSettings.HasUnknownKeys("""{ "review": { "ignoredNames": [] } }"""));
    }

    [Fact]
    public async Task CredentialsForRemovedServicesAreDeletedOnStart()
    {
        _secrets.Write("Lumen/OpenRouter", "sk-or-test");
        _secrets.Write("Lumen/Cloudflare/ApiToken", "cf-test");
        _secrets.Write("SomethingElse", "keep");

        await StartAsync();

        Assert.Null(_secrets.Read("Lumen/OpenRouter"));
        Assert.Null(_secrets.Read("Lumen/Cloudflare/ApiToken"));
        Assert.Equal("keep", _secrets.Read("SomethingElse"));
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public bool IsAvailable => true;

        public string? Read(string name) => _values.GetValueOrDefault(name);

        public void Write(string name, string secret) => _values[name] = secret;

        public bool Delete(string name) => _values.Remove(name);
    }
}
