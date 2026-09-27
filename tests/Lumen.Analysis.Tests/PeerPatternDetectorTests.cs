using Lumen.Domain;
using Lumen.Roslyn;
using static Lumen.Analysis.Tests.Support.RepoFixtures;
using static Lumen.Analysis.Tests.Support.TestDiffs;

namespace Lumen.Analysis.Tests;

public sealed class PeerPatternDetectorTests
{
    private const string NewWorker = "InvoiceRetryWorker";
    private static readonly Dictionary<string, string> NoBase = [];

    private static Dictionary<string, string> WorkerRepo(IEnumerable<string>? workers = null, string root = "src/Acme/Workers")
    {
        var sources = Infrastructure();
        foreach (var name in workers ?? ExistingWorkers)
        {
            sources[WorkerPath(name, root)] = QueueWorker(name);
        }

        return sources;
    }

    private static Task<DetectionResult> Detect(
        Dictionary<string, string> headSources,
        string diff,
        IReadOnlyDictionary<string, string>? baseSources = null) =>
        PeerPatternDetector.DetectAsync(
            RepositoryTypeIndex.FromSources(headSources),
            Snapshot(diff),
            BaseReader(baseSources ?? NoBase),
            CancellationToken.None);

    private static (Dictionary<string, string> Head, string Diff) NewDbWorkerScenario()
    {
        var head = WorkerRepo();
        var text = DbWorker(NewWorker);
        head[WorkerPath(NewWorker)] = text;
        return (head, AddedFile(WorkerPath(NewWorker), text));
    }

    private static PeerDeviationFacts FactsOf(Candidate candidate) => Assert.IsType<PeerDeviationFacts>(candidate.Facts);

    private static RepositoryPrecedentEvidence Precedent(Candidate candidate) =>
        Assert.Single(candidate.Evidence.OfType<RepositoryPrecedentEvidence>(), e => !e.IsCounterEvidence);

    [Fact]
    public async Task FlagsNewWorkerThatSkipsTheQueueClaimService()
    {
        var (head, diff) = NewDbWorkerScenario();

        var result = await Detect(head, diff);

        var candidate = Assert.Single(result.Candidates);
        var facts = FactsOf(candidate);
        Assert.Equal(new Trait(TraitKind.Dependency, "QueueClaimService"), facts.Missing);
        Assert.Equal(PeerPatternDetector.DetectorId, candidate.DetectorId);
        Assert.Equal(ReviewPointType.PatternDeviation, candidate.Type);

        var text = head[WorkerPath(NewWorker)];
        Assert.Equal(new CodeLocation(WorkerPath(NewWorker), LineOf(text, "(AppDbContext db)"), LineOf(text, "(AppDbContext db)"), NewWorker), candidate.Anchor);
        Assert.Empty(candidate.OtherLocations);

        var affected = Assert.Single(facts.Affected);
        Assert.Equal(NewWorker, affected.Type.Name);
        Assert.Equal("Dependency:AppDbContext", Assert.Single(affected.Instead).Trait.Key);

        var precedent = Precedent(candidate);
        Assert.Equal(4, precedent.Supporting);
        Assert.Equal(4, precedent.PeerCount);
        Assert.Equal(4, precedent.Examples.Count);
        Assert.All(precedent.Examples, e => Assert.Contains(e.TypeName, ExistingWorkers));
        Assert.DoesNotContain(candidate.Evidence, e => e.IsCounterEvidence);
        Assert.Equal(ExistingWorkers.Order(StringComparer.Ordinal), facts.Following.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Empty(facts.NotFollowing);

        var signals = candidate.Signals;
        Assert.True(signals.IntroducedByChange);
        Assert.Equal("type-added", signals.ChangeKind);
        Assert.Equal((4, 4), (signals.Supporting, signals.PeerCount));
        Assert.Equal("dependency", signals.Category);
        Assert.Equal(1, signals.AffectedLocations);

        // 15 pool types (4 workers, 3 repo concepts, 8 fillers), 4 take QueueClaimService: lift = 1.0 / (4/15).
        Assert.Equal(3.75, signals.Lift);

        var unit = Assert.Single(candidate.ChangeUnits);
        Assert.Equal(ChangeKind.TypeAdded, unit.Kind);
        Assert.Equal(["Acme.Workers.InvoiceRetryWorker"], unit.Symbols);
    }

    [Fact]
    public async Task DoesNotReportCallsImpliedByTheMissingDependency()
    {
        var (head, diff) = NewDbWorkerScenario();

        var result = await Detect(head, diff);

        Assert.DoesNotContain(result.Candidates, c => FactsOf(c).Missing.Kind == TraitKind.Calls);
    }

    [Fact]
    public async Task ReportsTheWorkerConvention()
    {
        var (head, diff) = NewDbWorkerScenario();

        var result = await Detect(head, diff);

        var convention = Assert.Single(result.Conventions, c => c.Statement == "Subclasses of BackgroundService take QueueClaimService");
        Assert.Equal((4, 4), (convention.Supporting, convention.PeerCount));
        Assert.Equal(4, convention.Examples.Count);
    }

    [Fact]
    public async Task CandidateKeysAreStableAcrossRuns()
    {
        var (head, diff) = NewDbWorkerScenario();

        var first = await Detect(head, diff);
        var second = await Detect(head, diff);

        var key = Assert.Single(first.Candidates).Key;
        Assert.StartsWith("pp-", key, StringComparison.Ordinal);
        Assert.Equal(key, Assert.Single(second.Candidates).Key);
        Assert.Equal(first.Candidates[0].ChangeUnits[0].Id, second.Candidates[0].ChangeUnits[0].Id);
        Assert.Equal(first.Candidates[0].Evidence.Select(e => e.Id), second.Candidates[0].Evidence.Select(e => e.Id));
    }

    [Fact]
    public async Task FlagsNewClientThatSwallowsInsteadOfThrowing()
    {
        var head = Infrastructure();
        foreach (var name in ExistingClients)
        {
            head[ClientPath(name)] = ThrowingClient(name);
        }

        var path = ClientPath("StripeClient");
        head[path] = SwallowingClient;

        var result = await Detect(head, AddedFile(path, SwallowingClient));

        var candidate = Assert.Single(result.Candidates);
        var facts = FactsOf(candidate);
        Assert.Equal("Throws:ProviderOperationException", facts.Missing.Key);
        Assert.Equal("error-handling", candidate.Signals.Category);

        var affected = Assert.Single(facts.Affected);
        Assert.Contains(affected.Instead, o => o.Trait == new Trait(TraitKind.SwallowsExceptions, "Exception"));
        Assert.Equal(LineOf(SwallowingClient, "catch (Exception ex)"), candidate.Anchor.StartLine);
        Assert.Equal(path, candidate.Anchor.Path);

        var precedent = Precedent(candidate);
        Assert.Equal((4, 4), (precedent.Supporting, precedent.PeerCount));
        Assert.All(precedent.Examples, e => Assert.Contains("throw new ProviderOperationException", e.Snippet, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreExistingDeviationInModifiedTypeIsNotIntroducedByTheChange()
    {
        var head = WorkerRepo();
        var path = WorkerPath(NewWorker);
        var before = DbWorker(NewWorker);
        var after = DbWorker(NewWorker, "\n            await Task.Delay(10, stoppingToken);");
        head[path] = after;

        var result = await Detect(head, RewrittenFile(path, before, after), new Dictionary<string, string> { [path] = before });

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Dependency:QueueClaimService", FactsOf(candidate).Missing.Key);
        Assert.False(candidate.Signals.IntroducedByChange);
        Assert.Equal("type-modified", candidate.Signals.ChangeKind);
        Assert.Equal(ChangeKind.TypeModified, Assert.Single(candidate.ChangeUnits).Kind);
    }

    [Fact]
    public async Task RemovingATraitTheTypeHadAtBaseIsIntroducedByTheChange()
    {
        var head = WorkerRepo();
        var path = WorkerPath(NewWorker);
        var before = QueueWorker(NewWorker);
        var after = DbWorker(NewWorker);
        head[path] = after;

        var result = await Detect(head, RewrittenFile(path, before, after), new Dictionary<string, string> { [path] = before });

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Dependency:QueueClaimService", FactsOf(candidate).Missing.Key);
        Assert.True(candidate.Signals.IntroducedByChange);
        Assert.Equal("type-modified", candidate.Signals.ChangeKind);

        // The type's own base version is never one of its peers.
        Assert.Equal((4, 4), (candidate.Signals.Supporting, candidate.Signals.PeerCount));
        Assert.DoesNotContain(FactsOf(candidate).Following, p => p.Name == NewWorker);
    }

    [Fact]
    public async Task PrecedentIsReadFromBaseForPeersChangedInThisPullRequest()
    {
        // ReportWorker only starts using QueueClaimService in this PR; at base it polled the database.
        var head = WorkerRepo();
        var reportPath = WorkerPath("ReportWorker");
        var reportBefore = DbWorker("ReportWorker");
        var newPath = WorkerPath(NewWorker);
        var newText = DbWorker(NewWorker);
        head[newPath] = newText;

        var diff = RewrittenFile(reportPath, reportBefore, head[reportPath]) + AddedFile(newPath, newText);
        var result = await Detect(head, diff, new Dictionary<string, string> { [reportPath] = reportBefore });

        var candidate = Assert.Single(result.Candidates);
        var facts = FactsOf(candidate);
        Assert.Equal("Dependency:QueueClaimService", facts.Missing.Key);
        Assert.Equal(NewWorker, Assert.Single(facts.Affected).Type.Name);

        var precedent = Precedent(candidate);
        Assert.Equal(3, precedent.Supporting);
        Assert.Equal(4, precedent.PeerCount);
        Assert.DoesNotContain(facts.Following, p => p.Name == "ReportWorker");
        Assert.Equal("ReportWorker", Assert.Single(facts.NotFollowing).Name);
        Assert.Contains(candidate.Evidence, e => e.IsCounterEvidence);
    }

    [Fact]
    public async Task PeersThatOnlyGainedTheTraitInThisPullRequestDoNotCountAsSupport()
    {
        // Two of the four peers adopt QueueClaimService in this PR, leaving only two with it at base: too thin.
        var head = WorkerRepo();
        var baseSources = new Dictionary<string, string>();
        var diff = "";
        foreach (var name in new[] { "SubscriptionCleanupWorker", "ReportWorker" })
        {
            var path = WorkerPath(name);
            baseSources[path] = DbWorker(name);
            diff += RewrittenFile(path, baseSources[path], head[path]);
        }

        var newPath = WorkerPath(NewWorker);
        head[newPath] = DbWorker(NewWorker);
        diff += AddedFile(newPath, head[newPath]);

        var result = await Detect(head, diff, baseSources);

        Assert.DoesNotContain(result.Candidates, c => FactsOf(c).Missing.Key == "Dependency:QueueClaimService");
    }

    [Fact]
    public async Task EachChangedTypeIsReportedAtMostOncePerMissingTrait()
    {
        // InvoiceRetryWorker is a BackgroundService *Worker; LedgerSyncWorker is a *Worker that isn't a BackgroundService.
        // Both lack QueueClaimService, visible through the "base:BackgroundService" and "suffix:Worker" roles.
        var head = WorkerRepo();
        var invoicePath = WorkerPath(NewWorker);
        head[invoicePath] = DbWorker(NewWorker);
        var ledgerPath = WorkerPath("LedgerSyncWorker");
        head[ledgerPath] = DbWorker("LedgerSyncWorker").Replace(" : BackgroundService", "", StringComparison.Ordinal);

        var result = await Detect(head, AddedFile(invoicePath, head[invoicePath]) + AddedFile(ledgerPath, head[ledgerPath]));

        var pairs = result.Candidates
            .SelectMany(c => FactsOf(c).Affected.Select(a => $"{a.Type.Name} / {FactsOf(c).Missing.Key}"))
            .ToList();
        Assert.Contains($"{NewWorker} / Dependency:QueueClaimService", pairs);
        Assert.Contains("LedgerSyncWorker / Dependency:QueueClaimService", pairs);
        Assert.Equal(pairs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), pairs.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TooFewPeersProduceNoCandidate()
    {
        var head = WorkerRepo(["EmailWorker", "PaymentRetryWorker"]);
        var path = WorkerPath(NewWorker);
        head[path] = DbWorker(NewWorker);

        var result = await Detect(head, AddedFile(path, head[path]));

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task TypesInTestFilesAreNotPeers()
    {
        var head = WorkerRepo(root: "tests/Acme.Workers.Tests/Fakes");
        var path = WorkerPath(NewWorker);
        head[path] = DbWorker(NewWorker);

        var result = await Detect(head, AddedFile(path, head[path]));

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task TypesInTestFilesAreNotAnalysed()
    {
        var head = WorkerRepo();
        var path = WorkerPath(NewWorker, "tests/Acme.Workers.Tests");
        head[path] = DbWorker(NewWorker);

        var result = await Detect(head, AddedFile(path, head[path]));

        Assert.Same(DetectionResult.Empty, result);
    }

    [Fact]
    public async Task MechanicalFilesAreNotAnalysed()
    {
        var head = WorkerRepo();
        var path = "src/Acme/Workers/Generated/InvoiceRetryWorker.g.cs";
        head[path] = DbWorker(NewWorker);

        var result = await Detect(head, AddedFile(path, head[path]));

        Assert.Same(DetectionResult.Empty, result);
    }

    [Fact]
    public async Task ConformingNewWorkerProducesNoCandidate()
    {
        var head = WorkerRepo();
        var path = WorkerPath(NewWorker);
        head[path] = QueueWorker(NewWorker);

        var result = await Detect(head, AddedFile(path, head[path]));

        Assert.Empty(result.Candidates);
    }

    private static Task<DetectionResult> DetectWith(ReviewSettings settings, Dictionary<string, string> head, string diff) =>
        PeerPatternDetector.DetectAsync(RepositoryTypeIndex.FromSources(head), Snapshot(diff), BaseReader(NoBase), settings, CancellationToken.None);

    [Fact]
    public async Task QuietNeedsMorePeersThanTheRepositoryHas()
    {
        var (head, diff) = NewDbWorkerScenario();

        var result = await DetectWith(new ReviewSettings { Sensitivity = ReviewSensitivity.Quiet }, head, diff);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task AnIgnoredTypeIsNeverAConventionNorAreCallsOnIt()
    {
        var (head, diff) = NewDbWorkerScenario();

        var result = await DetectWith(new ReviewSettings { IgnoredNames = ["QueueClaimService"] }, head, diff);

        Assert.DoesNotContain(result.Candidates, c => FactsOf(c).Missing.Subject.StartsWith("QueueClaimService", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Conventions, c => c.Statement.Contains("QueueClaimService", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangesUnderASkippedPathAreNotFlagged()
    {
        var (head, diff) = NewDbWorkerScenario();

        var result = await DetectWith(new ReviewSettings { SkippedPaths = ["src/Acme/Workers/InvoiceRetry*.cs"] }, head, diff);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task ExamplesFollowTheSensitivity()
    {
        var (head, diff) = NewDbWorkerScenario();

        var result = await DetectWith(new ReviewSettings { Sensitivity = ReviewSensitivity.Balanced with { MaxExamples = 1 } }, head, diff);

        var candidate = Assert.Single(result.Candidates);
        Assert.Single(Precedent(candidate).Examples);
    }
}
