using Lumen.Domain;
using Microsoft.Data.Sqlite;

namespace Lumen.Storage.Tests;

public sealed class AttentionAndInvestigationStoreTests : IDisposable
{
    private static readonly PullRequestKey Pr = new(new RepositoryRef("octo", "lumen"), 7);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumen-storage-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "lumen.db");

    private static CancellationToken Ct => CancellationToken.None;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluationsRoundTripPerHeadWithProbabilitiesAndModel()
    {
        await using var store = new SqliteReviewStore(DatabasePath);
        await store.RecordEvaluationsAsync(
        [
            Evaluation("head-1", "mechanical", "no", new Dictionary<string, double> { ["yes"] = 0.02, ["no"] = 0.98 }),
            Evaluation("head-1", "human_judgement", "yes", null),
            Evaluation("head-2", "mechanical", "yes", null),
        ], Ct);

        var head1 = await store.GetEvaluationsAsync(Pr, "head-1", Ct);

        Assert.Equal(["mechanical", "human_judgement"], head1.Select(e => e.QuestionId));
        var first = head1[0];
        Assert.Equal("no", first.Choice);
        Assert.Equal(0.98, first.Probabilities!["no"]);
        Assert.Equal("attention/v1", first.QuestionSchemaVersion);
        Assert.Equal("test/model-1", first.Model);
        Assert.Equal("test/model-1-2026", first.ResolvedModel);
        Assert.Equal("""{"changeKind":"type-added"}""", first.StateJson);
        Assert.Null(head1[1].Probabilities);
        Assert.Single(await store.GetEvaluationsAsync(Pr, "head-2", Ct));
    }

    [Fact]
    public async Task InvestigationResultRoundTripsWithPolymorphicEvidence()
    {
        await using var store = new SqliteReviewStore(DatabasePath);
        var result = new InvestigationResult
        {
            InvestigationId = "inv-1",
            Type = InvestigationType.RepositoryPattern,
            Claim = "Providers should throw ProviderOperationException",
            Outcome = InvestigationOutcome.Supported,
            Summary = "Five providers wrap failures the same way.",
            Producer = "claude-code · pattern-investigator/v1",
            RecommendReviewPoint = true,
            Evidence =
            [
                new AgentInvestigationEvidence
                {
                    Id = "inv-1-e0",
                    Source = EvidenceSource.AgentInvestigation,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    Summary = "MailgunClient wraps HttpRequestException",
                    Producer = "claude-code · pattern-investigator/v1",
                    InvestigationId = "inv-1",
                    Location = new CodeLocation("src/Mail/MailgunClient.cs", 42, 44),
                },
            ],
            CounterEvidence =
            [
                new AgentInvestigationEvidence
                {
                    Id = "inv-1-c0",
                    Source = EvidenceSource.AgentInvestigation,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    Summary = "AzureAiProvider returns null on failure",
                    Producer = "claude-code · pattern-investigator/v1",
                    InvestigationId = "inv-1",
                    IsCounterEvidence = true,
                },
            ],
        };

        await store.SaveInvestigationAsync(Pr, "head-1", "pp-1", result, Ct);

        var found = await store.FindInvestigationAsync(Pr, "head-1", "pp-1", InvestigationType.RepositoryPattern, Ct);
        Assert.NotNull(found);
        Assert.Equal(InvestigationOutcome.Supported, found.Outcome);
        var evidence = Assert.IsType<AgentInvestigationEvidence>(Assert.Single(found.Evidence));
        Assert.Equal(42, evidence.Location!.StartLine);
        Assert.True(Assert.Single(found.CounterEvidence).IsCounterEvidence);

        Assert.Null(await store.FindInvestigationAsync(Pr, "head-2", "pp-1", InvestigationType.RepositoryPattern, Ct));
        Assert.Null(await store.FindInvestigationAsync(Pr, "head-1", "pp-1", InvestigationType.Correctness, Ct));
        Assert.Equal(1, await store.CountInvestigationsAsync(Pr, "head-1", Ct));
        Assert.Equal(0, await store.CountInvestigationsAsync(Pr, "head-2", Ct));
    }

    private static AttentionEvaluationRecord Evaluation(string head, string question, string choice, Dictionary<string, double>? probabilities) => new()
    {
        Key = Pr,
        HeadSha = head,
        ChangeUnitId = "cu-1",
        CandidateKey = "pp-1",
        QuestionId = question,
        QuestionSchemaVersion = "attention/v1",
        Choice = choice,
        Probabilities = probabilities,
        Model = "test/model-1",
        ResolvedModel = "test/model-1-2026",
        Provider = "openrouter",
        StateJson = """{"changeKind":"type-added"}""",
        LatencyMs = 120,
        At = DateTimeOffset.UtcNow,
    };
}
