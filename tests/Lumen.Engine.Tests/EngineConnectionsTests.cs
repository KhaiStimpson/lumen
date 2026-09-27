using Grpc.Core;
using Lumen.Agents;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Jev;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumen.Engine.Tests;

/// <summary>The Connections panel's calls over a real named pipe, with the credential store and OpenRouter faked.</summary>
public sealed class EngineConnectionsTests : IAsyncDisposable
{
    private const string Key = "sk-or-v1-test-0123456789";

    private readonly string _pipe = $"lumen-test-{Guid.NewGuid():N}";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"lumen-test-{Guid.NewGuid():N}");
    private readonly MemorySecretStore _secrets = new();
    private readonly FakeKeyCheck _keyCheck = new();
    private Microsoft.AspNetCore.Builder.WebApplication? _engine;
    private Grpc.Net.Client.GrpcChannel? _channel;

    private async Task<ReviewEngine.ReviewEngineClient> StartAsync(EngineSettings? settings = null, ISecretStore? secrets = null)
    {
        _engine = EngineHost.Build(
            new EngineOptions { PipeName = _pipe, DataDirectory = _dataDir },
            services =>
            {
                services.RemoveAll<EngineSettings>();
                services.AddSingleton(settings ?? new EngineSettings());
                services.RemoveAll<ISecretStore>();
                services.AddSingleton(secrets ?? _secrets);
                services.RemoveAll<IOpenRouterKeyCheck>();
                services.AddSingleton<IOpenRouterKeyCheck>(_keyCheck);
                services.RemoveAll<IAgentProvider>();
                services.AddSingleton<IAgentProvider>(new SignedInAgent());
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
    public async Task WithoutAKeyJevIsNotConnected()
    {
        var client = await StartAsync();

        var status = await client.GetConnectionsAsync(new GetConnectionsRequest());

        Assert.Equal(ConnectionState.NotConnected, status.Jev.State);
        Assert.False(status.Jev.KeyStored);
        Assert.True(status.Jev.CanStoreKey);
        Assert.Equal("typesafe/jev-1.13", status.Jev.Model);
        Assert.Equal(0, _keyCheck.Calls);
        Assert.Equal(ConnectionState.Connected, status.Claude.State);
        Assert.Equal(ConnectionBilling.Subscription, status.Claude.Billing);
        Assert.EndsWith("settings.json", status.SettingsPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingAKeyStoresItAndReportsConnectedWithoutEchoingIt()
    {
        var client = await StartAsync();

        var status = await client.SetOpenRouterKeyAsync(new SetOpenRouterKeyRequest { Key = $"  {Key}\n" });

        Assert.Equal(Key, _secrets.Read(SecretNames.OpenRouterApiKey));
        Assert.Equal(ConnectionState.Connected, status.Jev.State);
        Assert.True(status.Jev.KeyStored);
        Assert.Equal("Key valid", status.Jev.Detail);
        Assert.DoesNotContain("sk-or", status.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedKeyIsReportedAsAnError()
    {
        _keyCheck.Result = new OpenRouterKeyStatus(false, "OpenRouter rejected the key (401)");
        var client = await StartAsync();

        var status = await client.SetOpenRouterKeyAsync(new SetOpenRouterKeyRequest { Key = Key });

        Assert.Equal(ConnectionState.Error, status.Jev.State);
        Assert.True(status.Jev.KeyStored);
        Assert.Equal("OpenRouter rejected the key (401)", status.Jev.Detail);
    }

    [Fact]
    public async Task RemovingTheKeyDeletesItFromTheCredentialStore()
    {
        _secrets.Write(SecretNames.OpenRouterApiKey, Key);
        var client = await StartAsync();

        var status = await client.RemoveOpenRouterKeyAsync(new RemoveOpenRouterKeyRequest());

        Assert.Null(_secrets.Read(SecretNames.OpenRouterApiKey));
        Assert.Equal(ConnectionState.NotConnected, status.Jev.State);
        Assert.False(status.Jev.KeyStored);
    }

    [Fact]
    public async Task ABlankKeyIsRefusedAndNothingIsStored()
    {
        var client = await StartAsync();

        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.SetOpenRouterKeyAsync(new SetOpenRouterKeyRequest { Key = "   " }));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Null(_secrets.Read(SecretNames.OpenRouterApiKey));
    }

    [Fact]
    public async Task JevTurnedOffInSettingsIsOffEvenWithAKey()
    {
        _secrets.Write(SecretNames.OpenRouterApiKey, Key);
        var client = await StartAsync(new EngineSettings { Privacy = new PrivacySettings { AllowCloudReasoning = false } });

        var status = await client.GetConnectionsAsync(new GetConnectionsRequest());

        Assert.Equal(ConnectionState.Off, status.Jev.State);
        Assert.True(status.Jev.KeyStored);
        Assert.Equal(0, _keyCheck.Calls);
    }

    [Fact]
    public async Task WithoutACredentialStoreKeysCannotBeSet()
    {
        var client = await StartAsync(secrets: new Lumen.Storage.UnavailableSecretStore());

        var status = await client.GetConnectionsAsync(new GetConnectionsRequest());
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.SetOpenRouterKeyAsync(new SetOpenRouterKeyRequest { Key = Key }));

        Assert.False(status.Jev.CanStoreKey);
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
    }

    [Fact]
    public async Task SettingsChangesAreSavedAndApplyWithoutARestart()
    {
        _secrets.Write(SecretNames.OpenRouterApiKey, Key);
        var client = await StartAsync(new EngineSettings
        {
            Jev = new JevSettings { Model = "typesafe/jev-custom" },
            Agents = new AgentSettings { MaxInvestigationsPerPullRequest = 7 },
        });

        var status = await client.UpdateConnectionSettingsAsync(new ConnectionSettings
        {
            AllowCloudReasoning = true,
            JevEnabled = false,
            AllowCodeSnippetsToJev = true,
            InvestigationsEnabled = true,
        });

        Assert.Equal(ConnectionState.Off, status.Jev.State);
        Assert.Equal("Turned off", status.Jev.Detail);
        Assert.True(status.Settings.InvestigationsEnabled);
        Assert.True(status.Claude.InvestigationsEnabled);

        var live = _engine!.Services.GetRequiredService<EngineSettingsStore>().Current;
        Assert.False(live.Jev.Enabled);
        Assert.True(live.AgentsAllowed);

        var saved = EngineSettings.Load(_dataDir);
        Assert.False(saved.Jev.Enabled);
        Assert.True(saved.Privacy.AllowCodeSnippetsToJev);
        Assert.True(saved.Privacy.AllowCodeToAgents);
        Assert.True(saved.Agents.Enabled);
        Assert.Equal("typesafe/jev-custom", saved.Jev.Model);
        Assert.Equal(7, saved.Agents.MaxInvestigationsPerPullRequest);
    }

    [Fact]
    public async Task TurningOffCloudAiTurnsOffJevAndInvestigations()
    {
        _secrets.Write(SecretNames.OpenRouterApiKey, Key);
        var client = await StartAsync(new EngineSettings
        {
            Privacy = new PrivacySettings { AllowCodeToAgents = true },
            Agents = new AgentSettings { Enabled = true },
        });

        var status = await client.UpdateConnectionSettingsAsync(new ConnectionSettings
        {
            AllowCloudReasoning = false,
            JevEnabled = true,
            InvestigationsEnabled = true,
        });

        Assert.Equal(ConnectionState.Off, status.Jev.State);
        Assert.Equal("Off: cloud AI is turned off", status.Jev.Detail);
        Assert.False(status.Claude.InvestigationsEnabled);
        Assert.Equal(0, _keyCheck.Calls);
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public bool IsAvailable => true;

        public string? Read(string name) => _values.GetValueOrDefault(name);

        public void Write(string name, string secret) => _values[name] = secret;

        public bool Delete(string name) => _values.Remove(name);
    }

    private sealed class FakeKeyCheck : IOpenRouterKeyCheck
    {
        public OpenRouterKeyStatus Result { get; set; } = new(true, "Key valid");

        public int Calls { get; private set; }

        public Task<OpenRouterKeyStatus> CheckKeyAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class SignedInAgent : IAgentProvider
    {
        public string Id => "fake-agent";

        public AgentProviderCapabilities Capabilities => AgentProviderCapabilities.None;

        public Task<AgentAuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgentAuthenticationState(AgentConnectionStatus.SignedIn, AgentBilling.Subscription, "Claude Pro via Claude Code", "2.1.0"));

        public Task<AgentSession> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
