using System.Net;
using Lumen.Analysis;
using Lumen.Domain;

namespace Lumen.Jev.Tests;

internal static class Support
{
    public static readonly PullRequestKey Pr = new(new RepositoryRef("acme", "billing"), 42);

    public const string SecretPath = "src/Billing/Secret/InvoiceRetryWorker.cs";

    public static AnalysisContext Context() => new(
        new PullRequestSnapshot(
            Pr,
            "base",
            "head",
            "merge-base",
            new PullRequestMetadata("Add retrying", "sarah", "open", false, "main", "feature", "https://github.com/acme/billing/pull/42", null, DateTimeOffset.UnixEpoch),
            [
                new ChangedFile(SecretPath, null, FileChangeKind.Added, 40, 0, false, MechanicalClassification.None, []),
                new ChangedFile("tests/Billing.Tests/InvoiceRetryWorkerTests.cs", null, FileChangeKind.Added, 20, 0, false, MechanicalClassification.None, []),
                new ChangedFile("src/Migrations/0001_Init.Designer.cs", null, FileChangeKind.Added, 900, 0, false, new MechanicalClassification(true, "EF designer"), []),
            ],
            []),
        new NoCheckout());

    public static Candidate Candidate(string key, string changeUnit = "cu-1", int peers = 8, int supporting = 8, double lift = 3, bool introduced = true) => new()
    {
        Key = key,
        DetectorId = "peer-pattern/v1",
        Type = ReviewPointType.PatternDeviation,
        ChangeUnits =
        [
            new ChangeUnit
            {
                Id = changeUnit,
                Title = "InvoiceRetryWorker added",
                Kind = ChangeKind.TypeAdded,
                Symbols = ["Billing.InvoiceRetryWorker"],
                Locations = [new CodeLocation(SecretPath, 1, 40, "InvoiceRetryWorker")],
            },
        ],
        Anchor = new CodeLocation(SecretPath, 12, 12, "InvoiceRetryWorker"),
        Evidence =
        [
            new RepositoryPrecedentEvidence
            {
                Id = $"{key}-precedent",
                Source = EvidenceSource.RepositoryPrecedent,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Summary = "8 of 8 queue workers take QueueClaimService",
                Producer = "peer-pattern/v1",
                Convention = "BackgroundWorker subclasses take QueueClaimService",
                Supporting = supporting,
                PeerCount = peers,
                Examples = [new PrecedentExample(new CodeLocation("src/Billing/ChargeWorker.cs", 9, 9, "ChargeWorker"), "ChargeWorker", "private readonly QueueClaimService _claims; // secret-snippet", 5)],
            },
        ],
        Signals = new AttentionSignals
        {
            ChangeKind = "type-added",
            IntroducedByChange = introduced,
            PeerCount = peers,
            Supporting = supporting,
            Lift = lift,
            Category = "dependency",
        },
        Facts = new object(),
    };

    public static SystemOneAnswer Noul(string id, double p) =>
        new(id, p >= 0.5 ? "true" : "false", new Dictionary<string, double> { ["true"] = p, ["false"] = 1 - p });

    private sealed class NoCheckout : IPullRequestCheckout
    {
        public string RootPath => "/repo";

        public string BaseSha => "base";

        public string HeadSha => "head";

        public string MergeBaseSha => "merge-base";

        public Task<string> GetDiffAsync(CancellationToken cancellationToken) => Task.FromResult("");

        public Task<string?> ReadBaseFileAsync(string path, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ReadHeadFileAsync(string path, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}

internal sealed class MemorySecrets(string? key) : ISecretStore
{
    public bool IsAvailable => true;

    public string? Read(string name) => name == SecretNames.OpenRouterApiKey ? key : null;

    public void Write(string name, string secret) => throw new NotSupportedException();

    public bool Delete(string name) => false;
}

internal sealed class MemoryEvaluationStore : IAttentionEvaluationStore
{
    public List<AttentionEvaluationRecord> Records { get; } = [];

    public Task RecordEvaluationsAsync(IReadOnlyList<AttentionEvaluationRecord> records, CancellationToken cancellationToken)
    {
        lock (Records)
        {
            Records.AddRange(records);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AttentionEvaluationRecord>> GetEvaluationsAsync(PullRequestKey key, string headSha, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AttentionEvaluationRecord>>(Records);
}

internal sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
