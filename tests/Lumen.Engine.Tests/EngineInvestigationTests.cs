using System.Text.Json;
using Grpc.Core;
using System.Threading.Channels;
using Lumen.Agents;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Jev;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumen.Engine.Tests;

/// <summary>
/// The full Phase 3/4 path over a real named pipe, with JEV and the coding agent faked: JEV routes the deviation to
/// the pattern investigator, the investigation's grounded evidence reaches the client as a ReviewPointUpdated.
/// </summary>
public sealed class EngineInvestigationTests : IAsyncDisposable
{
    private static readonly PullRequestRef Pr = new() { Owner = "acme", Name = "billing", Number = 42 };

    private readonly string _pipe = $"lumen-test-{Guid.NewGuid():N}";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"lumen-test-{Guid.NewGuid():N}");
    private Microsoft.AspNetCore.Builder.WebApplication? _engine;

    private async Task StartAsync(EngineSettings settings, IAgentProvider provider, ISystemOneEvaluator evaluator)
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
                services.RemoveAll<EngineSettings>();
                services.AddSingleton(settings);
                services.RemoveAll<ISystemOneEvaluator>();
                services.AddSingleton(evaluator);
                services.RemoveAll<IAgentProvider>();
                services.AddSingleton(provider);
            });
        await _engine.StartAsync();
    }

    public async ValueTask DisposeAsync()
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

    private static EngineSettings AgentsOn => new()
    {
        Privacy = new PrivacySettings { AllowCodeToAgents = true },
        Agents = new AgentSettings { Enabled = true },
    };

    [Fact]
    public async Task JevRoutesAnInvestigationWhoseEvidenceUpdatesTheReviewPoint()
    {
        var provider = new FakeAgentProvider($$"""
            {
              "outcome": "supported",
              "summary": "All four existing workers claim their queue items first.",
              "dominantPrecedent": "Workers take QueueClaimService",
              "whyPrecedentExists": "Claiming prevents two instances processing the same item.",
              "findings": [
                { "claim": "EmailWorker claims before processing", "path": "{{WorkerScenario.ExistingWorkerPath}}", "line": 14, "supportsReviewPoint": true },
                { "claim": "Made-up file", "path": "src/Nope.cs", "line": 1, "supportsReviewPoint": true }
              ],
              "recommendReviewPoint": true
            }
            """);
        await StartAsync(AgentsOn, provider, new RoutingEvaluator());

        var events = await CollectAsync(e => e.ReviewPointUpdated is not null);

        var added = Assert.Single(events, e => e.ReviewPointAdded is not null).ReviewPointAdded;
        Assert.Contains("JEV typesafe/jev-1.13", added.RoutingReason, StringComparison.Ordinal);

        var statuses = events.Where(e => e.InvestigationStatus is not null).Select(e => e.InvestigationStatus.Active).ToList();
        Assert.Equal(1, statuses.First());

        var updated = events.Last().ReviewPointUpdated;
        Assert.Equal(added.Id, updated.Id);
        var agentEvidence = updated.Evidence.Where(e => e.Source == "AgentInvestigation").ToList();
        Assert.Equal(2, agentEvidence.Count);
        Assert.All(agentEvidence, e => Assert.Equal("fake-agent · pattern-investigator/v1 · fake-model", e.Producer));
        Assert.Contains(agentEvidence, e => e.Location?.Path == WorkerScenario.ExistingWorkerPath && e.Location.StartLine == 14);
        Assert.DoesNotContain(agentEvidence, e => e.Summary.Contains("Made-up", StringComparison.Ordinal));
        Assert.Contains("Claiming prevents two instances", updated.WhyItMatters, StringComparison.Ordinal);

        var request = Assert.Single(provider.Requests);
        Assert.Equal(Path.Combine(_dataDir, "checkout"), request.WorkingDirectory);
        Assert.Equal(["Read", "Grep", "Glob"], request.Tools);
    }

    [Fact]
    public async Task AgentsStayOffUntilTheUserAllowsSendingCode()
    {
        var provider = new FakeAgentProvider("{}");
        await StartAsync(new EngineSettings { Agents = new AgentSettings { Enabled = true } }, provider, new RoutingEvaluator());

        var events = await CollectAsync(e => e.Complete is not null);
        await Task.Delay(200);

        Assert.Empty(provider.Requests);
        Assert.DoesNotContain(events, e => e.InvestigationStatus is not null);
    }

    [Fact]
    public async Task ReviewCompletesWhenTheAgentIsSignedOut()
    {
        var provider = new FakeAgentProvider("{}") { State = new AgentAuthenticationState(AgentConnectionStatus.SignedOut, AgentBilling.Unknown, "Not signed in — run `claude` and sign in") };
        await StartAsync(AgentsOn, provider, new RoutingEvaluator());

        var events = await CollectAsync(e => e.InvestigationStatus is { Detail.Length: > 0 });

        Assert.Single(events, e => e.ReviewPointAdded is not null);
        Assert.Contains(events, e => e.Complete is not null);
        Assert.StartsWith("Not signed in", events.Last().InvestigationStatus.Detail, StringComparison.Ordinal);
        Assert.Empty(provider.Requests);
    }

    private async Task<List<PullRequestEvent>> CollectAsync(Func<PullRequestEvent, bool> stop)
    {
        using var channel = EngineEndpoint.CreateChannel(_pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var call = client.WatchPullRequest(new WatchPullRequestRequest { PullRequest = Pr }, cancellationToken: cts.Token);
        var events = new List<PullRequestEvent>();
        await foreach (var evt in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            events.Add(evt);
            if (stop(evt))
            {
                break;
            }
        }

        return events;
    }

    /// <summary>Says every candidate deviates from precedent and history would help: route to the pattern investigator.</summary>
    private sealed class RoutingEvaluator : ISystemOneEvaluator
    {
        public bool IsConfigured => true;

        public string Model => "typesafe/jev-1.13";

        public Task<SystemOneResult> EvaluateAsync(SystemOneState state, IReadOnlyList<SystemOneQuestion> questions, CancellationToken cancellationToken)
        {
            var answers = questions.ToDictionary(q => q.Id, q =>
            {
                var p = q.Id.EndsWith(AttentionQuestions.PrecedentDeviation, StringComparison.Ordinal) || q.Id.EndsWith(AttentionQuestions.HistoryUseful, StringComparison.Ordinal) || q.Id.EndsWith(AttentionQuestions.HumanJudgement, StringComparison.Ordinal)
                    ? 0.9
                    : 0.1;
                return new SystemOneAnswer(q.Id, p >= 0.5 ? "true" : "false", new Dictionary<string, double> { ["true"] = p, ["false"] = 1 - p });
            });
            return Task.FromResult(new SystemOneResult(answers, Model, Model, "TypeSafe", 0, TimeSpan.FromMilliseconds(5)));
        }
    }

    private sealed class FakeAgentProvider(string structuredOutput) : IAgentProvider
    {
        public AgentAuthenticationState State { get; init; } = new(AgentConnectionStatus.SignedIn, AgentBilling.Subscription, "Claude Pro via Claude Code");

        public List<AgentSessionRequest> Requests { get; } = [];

        public string Id => "fake-agent";

        public AgentProviderCapabilities Capabilities => AgentProviderCapabilities.StructuredOutput;

        public Task<AgentAuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken) => Task.FromResult(State);

        public Task<AgentSession> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            var events = Channel.CreateUnbounded<AgentEvent>();
            events.Writer.Complete();
            var result = new AgentSessionResult
            {
                Succeeded = true,
                StructuredOutput = JsonDocument.Parse(structuredOutput).RootElement.Clone(),
                Model = "fake-model",
                Billing = AgentBilling.Subscription,
            };
            return Task.FromResult(new AgentSession("s1", events.Reader, Task.FromResult(result), () => { }));
        }
    }
}
