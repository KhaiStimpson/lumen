using Lumen.Domain;
using Lumen.Roslyn;
using static Lumen.Analysis.Tests.Support.RepoFixtures;
using static Lumen.Analysis.Tests.Support.TestDiffs;

namespace Lumen.Analysis.Tests;

public sealed class ReviewPointPipelineTests
{
    private sealed class InMemoryCheckout(IReadOnlyDictionary<string, string> baseSources, IReadOnlyDictionary<string, string> headSources, string diff)
        : IPullRequestCheckout
    {
        public string RootPath => "/repo";

        public string BaseSha => "base-sha";

        public string HeadSha => "head-sha";

        public string MergeBaseSha => "merge-base-sha";

        public Task<string> GetDiffAsync(CancellationToken cancellationToken) => Task.FromResult(diff);

        public Task<string?> ReadBaseFileAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(baseSources.TryGetValue(path, out var text) ? text : null);

        public Task<string?> ReadHeadFileAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(headSources.TryGetValue(path, out var text) ? text : null);
    }

    private sealed class FixedDetector(params Candidate[] candidates) : IChangeDetector
    {
        public string Id => "fixed";

        public Task<DetectionResult> DetectAsync(AnalysisContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new DetectionResult(candidates, []));
    }

    private static readonly string NewWorkerPath = WorkerPath("InvoiceRetryWorker");

    private static async Task<PipelineResult> RunWorkerScenario(Func<ReviewPoint, Task>? onReviewPoint = null)
    {
        var head = Infrastructure();
        foreach (var name in ExistingWorkers)
        {
            head[WorkerPath(name)] = QueueWorker(name);
        }

        head[NewWorkerPath] = DbWorker("InvoiceRetryWorker");
        var diff = AddedFile(NewWorkerPath, head[NewWorkerPath]);
        var index = RepositoryTypeIndex.FromSources(head);
        var context = new AnalysisContext(Snapshot(diff), new InMemoryCheckout(new Dictionary<string, string>(), head, diff));

        var pipeline = new ReviewPointPipeline(
            [new PeerPatternDetector((_, _) => Task.FromResult(index))],
            new RuleBasedAttentionPolicy(),
            [new PeerDeviationExplainer()]);

        return await pipeline.RunAsync(context, onReviewPoint, CancellationToken.None);
    }

    [Fact]
    public async Task SurfacesTheMissingQueueClaimServiceEndToEnd()
    {
        var streamed = new List<ReviewPoint>();

        var result = await RunWorkerScenario(p =>
        {
            streamed.Add(p);
            return Task.CompletedTask;
        });

        var point = Assert.Single(result.ReviewPoints);
        Assert.Equal([point], streamed);
        Assert.Empty(result.Suppressed);

        Assert.Equal(ReviewPointType.PatternDeviation, point.Type);
        Assert.Equal(ReviewPointState.Visible, point.State);
        Assert.Equal(ReviewSeverity.Medium, point.Severity);
        Assert.Equal(EvidenceState.StrongEvidence, point.EvidenceState);
        Assert.StartsWith("pp-", point.Id, StringComparison.Ordinal);
        Assert.Equal(NewWorkerPath, point.Anchor.Path);
        Assert.NotNull(point.RoutingReason);

        Assert.Contains("QueueClaimService", point.Title, StringComparison.Ordinal);
        Assert.Contains("QueueClaimService", point.Summary, StringComparison.Ordinal);
        Assert.Contains("InvoiceRetryWorker", point.Summary, StringComparison.Ordinal);
        Assert.Contains("4 of 4", point.Summary, StringComparison.Ordinal);
        Assert.Contains("AppDbContext", point.WhyItMatters, StringComparison.Ordinal);
        Assert.Contains("QueueClaimService", point.SuggestedComment, StringComparison.Ordinal);

        var surface = Assert.IsType<ComparisonSurface>(point.Surface);
        Assert.Equal(NewWorkerPath, surface.Current.Source.Path);
        Assert.Contains("AppDbContext", surface.Current.Snippet, StringComparison.Ordinal);

        var precedentPaths = ExistingWorkers.Select(n => WorkerPath(n)).ToList();
        Assert.Contains(surface.Precedent.Source.Path, precedentPaths);
        Assert.Contains("QueueClaimService", surface.Precedent.Snippet, StringComparison.Ordinal);
        Assert.Equal([surface.Precedent.Source.StartLine], surface.Precedent.HighlightLines);
        Assert.All(surface.Precedent.Points, name => Assert.Contains(name, ExistingWorkers));

        Assert.Contains(result.Conventions, c => c.Statement == "Subclasses of BackgroundService take QueueClaimService");
    }

    [Fact]
    public async Task SuppressedCandidatesAreReportedButNotSurfaced()
    {
        var candidate = PolicyCandidate(introduced: false);
        var pipeline = new ReviewPointPipeline([new FixedDetector(candidate)], new RuleBasedAttentionPolicy(), []);

        var result = await pipeline.RunAsync(EmptyContext(), null, CancellationToken.None);

        Assert.Empty(result.ReviewPoints);
        var (suppressed, decision) = Assert.Single(result.Suppressed);
        Assert.Same(candidate, suppressed);
        Assert.Equal(AttentionAction.Suppress, decision.Action);
    }

    [Fact]
    public async Task ThrowsWhenNoExplainerAcceptsASurfacedCandidate()
    {
        var pipeline = new ReviewPointPipeline([new FixedDetector(PolicyCandidate(introduced: true))], new RuleBasedAttentionPolicy(), [new PeerDeviationExplainer()]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.RunAsync(EmptyContext(), null, CancellationToken.None));
    }

    [Theory]
    [InlineData(4, 4, false, EvidenceState.StrongEvidence)]
    [InlineData(9, 10, false, EvidenceState.StrongEvidence)]
    [InlineData(3, 3, false, EvidenceState.Unverified)]
    [InlineData(3, 4, false, EvidenceState.Unverified)]
    [InlineData(3, 4, true, EvidenceState.ConflictingEvidence)]
    public async Task ClassifiesEvidenceState(int supporting, int peers, bool counterEvidence, EvidenceState expected)
    {
        var candidate = PolicyCandidate(introduced: true, supporting, peers, counterEvidence);
        var explainer = new StubExplainer();
        var pipeline = new ReviewPointPipeline([new FixedDetector(candidate)], new RuleBasedAttentionPolicy(), [explainer]);

        var result = await pipeline.RunAsync(EmptyContext(), null, CancellationToken.None);

        Assert.Equal(expected, Assert.Single(result.ReviewPoints).EvidenceState);
    }

    [Fact]
    public void RanksBySeverityThenPriorityThenPathThenLine()
    {
        ReviewPoint Point(string id, ReviewSeverity severity, double priority, string path, int line) => new()
        {
            Id = id,
            ChangeUnitIds = [],
            Type = ReviewPointType.PatternDeviation,
            Severity = severity,
            State = ReviewPointState.Visible,
            EvidenceState = EvidenceState.Unverified,
            Title = id,
            Summary = "",
            WhyItMatters = "",
            SuggestedComment = "",
            Anchor = new CodeLocation(path, line, line),
            Priority = priority,
        };

        var points = new[]
        {
            Point("low", ReviewSeverity.Low, 99, "a.cs", 1),
            Point("medium-b-5", ReviewSeverity.Medium, 20, "b.cs", 5),
            Point("medium-b-2", ReviewSeverity.Medium, 20, "b.cs", 2),
            Point("high", ReviewSeverity.High, 1, "z.cs", 1),
            Point("medium-a", ReviewSeverity.Medium, 20, "a.cs", 9),
            Point("medium-top", ReviewSeverity.Medium, 25, "z.cs", 1),
            Point("medium-B", ReviewSeverity.Medium, 20, "B.cs", 1),
        };

        var ranked = ReviewPointPipeline.Rank(points);

        Assert.Equal(["high", "medium-top", "medium-B", "medium-a", "medium-b-2", "medium-b-5", "low"], ranked.Select(p => p.Id));
    }

    private sealed class StubExplainer : IReviewPointExplainer
    {
        public bool CanExplain(Candidate candidate) => true;

        public ValueTask<Explanation> ExplainAsync(Candidate candidate, AnalysisContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new Explanation("t", "s", "w", "c", null));
    }

    private static AnalysisContext EmptyContext() =>
        new(Snapshot(""), new InMemoryCheckout(new Dictionary<string, string>(), new Dictionary<string, string>(), ""));

    private static Candidate PolicyCandidate(bool introduced, int supporting = 4, int peers = 4, bool counterEvidence = false) => new()
    {
        Key = "pp-fixed",
        DetectorId = "fixed",
        Type = ReviewPointType.PatternDeviation,
        ChangeUnits = [],
        Anchor = new CodeLocation("src/A.cs", 3, 3),
        Evidence = counterEvidence
            ?
            [
                new RepositoryPrecedentEvidence
                {
                    Id = "e",
                    Source = EvidenceSource.RepositoryPrecedent,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    Summary = "exceptions",
                    Producer = "fixed",
                    IsCounterEvidence = true,
                    Convention = "c",
                    Supporting = 1,
                    PeerCount = peers,
                    Examples = [],
                },
            ]
            : [],
        Facts = new object(),
        Signals = new AttentionSignals
        {
            ChangeKind = "type-added",
            IntroducedByChange = introduced,
            Supporting = supporting,
            PeerCount = peers,
            Lift = 3,
            Category = "usage",
        },
    };
}
