using System.Text.Json;
using System.Threading.Channels;
using Lumen.Agents.Investigations;
using Lumen.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Agents.Tests;

public sealed class InvestigationTests : IDisposable
{
    private static readonly PullRequestKey Pr = new(new RepositoryRef("acme", "billing"), 42);

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "lumen-investigation-tests", Guid.NewGuid().ToString("N"));

    public InvestigationTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllLines(Path.Combine(_repo, "src", "ChargeWorker.cs"), Enumerable.Range(1, 30).Select(i => $"// line {i}"));
        File.WriteAllLines(Path.Combine(_repo, "src", "InvoiceRetryWorker.cs"), Enumerable.Range(1, 20).Select(i => $"// line {i}"));
    }

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private static ReviewPoint Point(string id = "pp-1", ReviewPointType type = ReviewPointType.PatternDeviation) => new()
    {
        Id = id,
        ChangeUnitIds = ["cu-1"],
        Type = type,
        Severity = ReviewSeverity.Medium,
        State = ReviewPointState.Visible,
        EvidenceState = EvidenceState.ConflictingEvidence,
        Title = "Missing QueueClaimService",
        Summary = "InvoiceRetryWorker doesn't take QueueClaimService, unlike 7 of 8 queue workers.",
        WhyItMatters = "Claims prevent duplicate processing.",
        SuggestedComment = "Should this use QueueClaimService?",
        Anchor = new CodeLocation("src/InvoiceRetryWorker.cs", 12, 12, "InvoiceRetryWorker"),
        Evidence =
        [
            new RepositoryPrecedentEvidence
            {
                Id = "e1",
                Source = EvidenceSource.RepositoryPrecedent,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Summary = "7 of 8 queue workers take QueueClaimService",
                Producer = "peer-pattern/v1",
                Convention = "Queue workers take QueueClaimService",
                Supporting = 7,
                PeerCount = 8,
                Examples = [new PrecedentExample(new CodeLocation("src/ChargeWorker.cs", 9, 9, "ChargeWorker"), "ChargeWorker", "", 5)],
            },
            new RepositoryPrecedentEvidence
            {
                Id = "e2",
                Source = EvidenceSource.RepositoryPrecedent,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Summary = "1 peer doesn't: LegacyWorker",
                Producer = "peer-pattern/v1",
                IsCounterEvidence = true,
                Convention = "Queue workers take QueueClaimService",
                Supporting = 1,
                PeerCount = 8,
                Examples = [new PrecedentExample(new CodeLocation("src/LegacyWorker.cs", 4, 4, "LegacyWorker"), "LegacyWorker", "", 1)],
            },
        ],
    };

    private static AgentSessionResult Answer(string json) => new()
    {
        Succeeded = true,
        StructuredOutput = JsonDocument.Parse(json).RootElement.Clone(),
        Model = "claude-sonnet-5",
        Billing = AgentBilling.Subscription,
    };

    private InvestigationBrief Brief(ReviewPoint? point = null) => new(point ?? Point(), _repo, InvestigationBudget.Standard);

    [Fact]
    public void PromptNamesTheClaimAndEveryLocationButNoCode()
    {
        var prompt = RepositoryPatternInvestigator.BuildPrompt(Point());

        Assert.Contains("unlike 7 of 8 queue workers", prompt, StringComparison.Ordinal);
        Assert.Contains("Queue workers take QueueClaimService (7 of 8 peers", prompt, StringComparison.Ordinal);
        Assert.Contains("src/InvoiceRetryWorker.cs:12", prompt, StringComparison.Ordinal);
        Assert.Contains("src/ChargeWorker.cs:9 (ChargeWorker)", prompt, StringComparison.Ordinal);
        Assert.Contains("src/LegacyWorker.cs:4 (LegacyWorker)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void GroundedFindingsBecomeEvidenceWithProvenance()
    {
        var result = RepositoryPatternInvestigator.Interpret(Brief(), Answer("""
            {
              "outcome": "supported",
              "summary": "Every other worker claims its queue item first.",
              "dominantPrecedent": "Workers claim items via QueueClaimService",
              "whyPrecedentExists": "Comment on ChargeWorker: claims stop two instances charging twice.",
              "findings": [
                { "claim": "ChargeWorker claims before charging", "path": "src/ChargeWorker.cs", "line": 9, "supportsReviewPoint": true },
                { "claim": "InvoiceRetryWorker reads rows directly", "path": "./src\\InvoiceRetryWorker.cs", "line": 12, "supportsReviewPoint": true },
                { "claim": "Invented file", "path": "src/Nope.cs", "line": 3, "supportsReviewPoint": true },
                { "claim": "Past the end", "path": "src/ChargeWorker.cs", "line": 99, "supportsReviewPoint": true },
                { "claim": "Escapes the repo", "path": "../../etc/passwd", "line": 1, "supportsReviewPoint": true },
                { "claim": "LegacyWorker is scheduled once a day", "path": "src/ChargeWorker.cs", "line": 20, "supportsReviewPoint": false }
              ],
              "recommendReviewPoint": true
            }
            """), "claude-code · pattern-investigator/v1 · claude-sonnet-5", DateTimeOffset.UnixEpoch);

        Assert.Equal(InvestigationOutcome.Supported, result.Outcome);
        Assert.True(result.RecommendReviewPoint);
        Assert.Equal(2, result.Evidence.Count);
        var counter = Assert.Single(result.CounterEvidence);
        Assert.True(counter.IsCounterEvidence);
        var first = Assert.IsType<AgentInvestigationEvidence>(result.Evidence[0]);
        Assert.Equal(EvidenceSource.AgentInvestigation, first.Source);
        Assert.Equal("claude-code · pattern-investigator/v1 · claude-sonnet-5", first.Producer);
        Assert.Equal(new CodeLocation("src/ChargeWorker.cs", 9, 9), first.Location);
        Assert.Equal("src/InvoiceRetryWorker.cs", ((AgentInvestigationEvidence)result.Evidence[1]).Location!.Path);
        Assert.Contains("3 uncited findings discarded", result.Summary, StringComparison.Ordinal);
        Assert.StartsWith("Comment on ChargeWorker", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerdictWithoutCitationsIsInconclusive()
    {
        var result = RepositoryPatternInvestigator.Interpret(Brief(), Answer("""
            { "outcome": "refuted", "summary": "Trust me.", "dominantPrecedent": "", "findings": [], "recommendReviewPoint": false }
            """), "p", DateTimeOffset.UnixEpoch);

        Assert.Equal(InvestigationOutcome.Inconclusive, result.Outcome);
        Assert.False(result.RecommendReviewPoint);
    }

    [Fact]
    public void AFailedSessionIsInconclusiveWithTheReason()
    {
        var result = RepositoryPatternInvestigator.Interpret(Brief(), new AgentSessionResult { Succeeded = false, Error = "Timed out after 240s" }, "p", DateTimeOffset.UnixEpoch);

        Assert.Equal(InvestigationOutcome.Inconclusive, result.Outcome);
        Assert.Equal("Timed out after 240s", result.Summary);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public void OnlyInvestigatesPatternDeviationsWithPrecedent() =>
        Assert.False(new RepositoryPatternInvestigator(TimeProvider.System).CanInvestigate(Point(type: ReviewPointType.CorrectnessRisk)));

    [Fact]
    public async Task RunsReadOnlyThroughTheProvider()
    {
        var provider = new FakeProvider(_ => Answer("""{ "outcome": "inconclusive", "summary": "s", "dominantPrecedent": "d", "findings": [], "recommendReviewPoint": false }"""));

        var result = await new RepositoryPatternInvestigator(TimeProvider.System).InvestigateAsync(Brief(), provider, _repo, CancellationToken.None);

        var request = Assert.Single(provider.Requests);
        Assert.Equal(["Read", "Grep", "Glob"], request.Tools);
        Assert.Equal(_repo, request.WorkingDirectory);
        Assert.False(request.AllowMeteredUsage);
        Assert.NotNull(request.OutputSchema);
        Assert.Equal(InvestigationLimits.For(InvestigationBudget.Standard).Timeout, request.Timeout);
        Assert.Equal("fake · pattern-investigator/v1 · claude-sonnet-5", result.Producer);
    }

    [Fact]
    public async Task SchedulerReusesCachedResultsAndRespectsTheCap()
    {
        var store = new MemoryInvestigationStore();
        var cachedPoint = Point("pp-cached");
        await store.SaveInvestigationAsync(Pr, "head", "pp-cached", Result("pp-cached"), CancellationToken.None);
        var provider = new FakeProvider(_ => Answer("""{ "outcome": "inconclusive", "summary": "s", "dominantPrecedent": "d", "findings": [], "recommendReviewPoint": false }"""));
        using var scheduler = Scheduler(provider, store, new InvestigationSchedulerOptions { MaxPerPullRequest = 2 });

        var results = new List<string>();
        var active = new List<int>();
        var summary = await scheduler.RunAsync(
            new FakeCheckout(_repo),
            Pr,
            [
                new InvestigationJob(cachedPoint, InvestigationType.RepositoryPattern, InvestigationBudget.Standard, 5),
                new InvestigationJob(Point("pp-low"), InvestigationType.RepositoryPattern, InvestigationBudget.Standard, 1),
                new InvestigationJob(Point("pp-high"), InvestigationType.RepositoryPattern, InvestigationBudget.Standard, 9),
                new InvestigationJob(Point("pp-high"), InvestigationType.RepositoryPattern, InvestigationBudget.Standard, 9),
                new InvestigationJob(Point("pp-other"), InvestigationType.Correctness, InvestigationBudget.Standard, 9),
            ],
            (result, point) =>
            {
                results.Add(point.Id);
                return Task.CompletedTask;
            },
            active.Add,
            CancellationToken.None);

        // The cached result counts toward the cap, so only the highest-priority new job runs.
        Assert.Equal(["pp-cached", "pp-high"], results);
        Assert.Equal(new InvestigationRunSummary(1, 1, 1, null), summary);
        Assert.Single(provider.Requests);
        Assert.Equal([1, 0], active);
        Assert.NotNull(await store.FindInvestigationAsync(Pr, "head", "pp-high", InvestigationType.RepositoryPattern, CancellationToken.None));
    }

    [Theory]
    [InlineData(AgentConnectionStatus.SignedOut, AgentBilling.Unknown)]
    [InlineData(AgentConnectionStatus.NotInstalled, AgentBilling.Unknown)]
    [InlineData(AgentConnectionStatus.SignedIn, AgentBilling.Metered)]
    public async Task SchedulerNeverStartsWithoutUsableSubscription(AgentConnectionStatus status, AgentBilling billing)
    {
        var provider = new FakeProvider(_ => throw new InvalidOperationException("must not start")) { State = new AgentAuthenticationState(status, billing, "not available") };
        using var scheduler = Scheduler(provider, new MemoryInvestigationStore());

        var summary = await scheduler.RunAsync(
            new FakeCheckout(_repo),
            Pr,
            [new InvestigationJob(Point(), InvestigationType.RepositoryPattern, InvestigationBudget.Standard, 1)],
            (_, _) => Task.CompletedTask,
            _ => { },
            CancellationToken.None);

        Assert.Empty(provider.Requests);
        Assert.Equal(1, summary.Skipped);
        Assert.NotNull(summary.Unavailable);
    }

    [Fact]
    public async Task SchedulerGivesMutatingRolesAnIsolatedWorktree()
    {
        var worktrees = new FakeWorktrees(_repo);
        var investigator = new RecordingInvestigator();
        using var scheduler = new InvestigationScheduler(
            [investigator],
            new FakeProvider(_ => Answer("{}")),
            new MemoryInvestigationStore(),
            worktrees,
            new InvestigationSchedulerOptions(),
            NullLogger<InvestigationScheduler>.Instance);

        await scheduler.RunAsync(new FakeCheckout(_repo), Pr, [new InvestigationJob(Point(), InvestigationType.Tests, InvestigationBudget.Deep, 1)], (_, _) => Task.CompletedTask, _ => { }, CancellationToken.None);

        Assert.Equal(worktrees.Created.Single(), investigator.WorkingDirectory);
        Assert.True(worktrees.Disposed);
    }

    private static InvestigationScheduler Scheduler(FakeProvider provider, MemoryInvestigationStore store, InvestigationSchedulerOptions? options = null) => new(
        [new RepositoryPatternInvestigator(TimeProvider.System)],
        provider,
        store,
        new FakeWorktrees(Path.GetTempPath()),
        options ?? new InvestigationSchedulerOptions(),
        NullLogger<InvestigationScheduler>.Instance);

    private static InvestigationResult Result(string point) => new()
    {
        InvestigationId = $"{point}-inv",
        Type = InvestigationType.RepositoryPattern,
        Claim = "c",
        Outcome = InvestigationOutcome.Supported,
        Summary = "cached",
        Evidence = [],
        Producer = "fake",
    };

    private sealed class FakeProvider(Func<AgentSessionRequest, AgentSessionResult> answer) : IAgentProvider
    {
        public AgentAuthenticationState State { get; init; } = new(AgentConnectionStatus.SignedIn, AgentBilling.Subscription, "Claude Pro via Claude Code");

        public List<AgentSessionRequest> Requests { get; } = [];

        public string Id => "fake";

        public AgentProviderCapabilities Capabilities => AgentProviderCapabilities.StructuredOutput;

        public Task<AgentAuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken) => Task.FromResult(State);

        public Task<AgentSession> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var events = Channel.CreateUnbounded<AgentEvent>();
            events.Writer.Complete();
            return Task.FromResult(new AgentSession("s", events.Reader, Task.FromResult(answer(request)), () => { }));
        }
    }

    private sealed class RecordingInvestigator : IInvestigator
    {
        public string? WorkingDirectory { get; private set; }

        public InvestigationType Type => InvestigationType.Tests;

        public bool MutatesWorkingTree => true;

        public bool CanInvestigate(ReviewPoint point) => true;

        public Task<InvestigationResult> InvestigateAsync(InvestigationBrief brief, IAgentProvider provider, string workingDirectory, CancellationToken cancellationToken)
        {
            WorkingDirectory = workingDirectory;
            return Task.FromResult(Result(brief.Point.Id));
        }
    }

    private sealed class FakeWorktrees(string root) : IAgentWorktreeFactory
    {
        public List<string> Created { get; } = [];

        public bool Disposed { get; private set; }

        public Task<IAgentWorktree> CreateAsync(IPullRequestCheckout checkout, string name, CancellationToken cancellationToken)
        {
            var path = Path.Combine(root, name);
            Created.Add(path);
            return Task.FromResult<IAgentWorktree>(new Worktree(path, () => Disposed = true));
        }

        private sealed class Worktree(string path, Action onDispose) : IAgentWorktree
        {
            public string Path => path;

            public ValueTask DisposeAsync()
            {
                onDispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeCheckout(string root) : IPullRequestCheckout
    {
        public string RootPath => root;

        public string BaseSha => "base";

        public string HeadSha => "head";

        public string MergeBaseSha => "mb";

        public Task<string> GetDiffAsync(CancellationToken cancellationToken) => Task.FromResult("");

        public Task<string?> ReadBaseFileAsync(string path, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ReadHeadFileAsync(string path, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class MemoryInvestigationStore : IInvestigationStore
    {
        private readonly List<(PullRequestKey Key, string Head, string Candidate, InvestigationResult Result)> _items = [];

        public Task SaveInvestigationAsync(PullRequestKey key, string headSha, string candidateKey, InvestigationResult result, CancellationToken cancellationToken)
        {
            lock (_items)
            {
                _items.Add((key, headSha, candidateKey, result));
            }

            return Task.CompletedTask;
        }

        public Task<InvestigationResult?> FindInvestigationAsync(PullRequestKey key, string headSha, string candidateKey, InvestigationType type, CancellationToken cancellationToken) =>
            Task.FromResult<InvestigationResult?>(_items.LastOrDefault(i => i.Key == key && i.Head == headSha && i.Candidate == candidateKey && i.Result.Type == type).Result);

        public Task<int> CountInvestigationsAsync(PullRequestKey key, string headSha, CancellationToken cancellationToken) =>
            Task.FromResult(_items.Count(i => i.Key == key && i.Head == headSha));
    }
}
