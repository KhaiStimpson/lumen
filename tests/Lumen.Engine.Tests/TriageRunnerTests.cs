using Lumen.Analysis;
using Lumen.Domain;
using Lumen.Engine.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Engine.Tests;

public class TriageRunnerTests
{
    private static readonly PullRequestKey Key = new(new RepositoryRef("acme", "billing"), 42);

    private static PullRequestSnapshot Snapshot() => new(
        Key,
        WorkerScenario.BaseSha,
        WorkerScenario.HeadSha,
        WorkerScenario.BaseSha,
        new PullRequestMetadata("t", "a", "open", false, "main", "f", "https://example", null, DateTimeOffset.UnixEpoch),
        ChangedFiles.FromUnifiedDiff("diff --git a/a.cs b/a.cs\nindex 1..2 100644\n--- a/a.cs\n+++ b/a.cs\n@@ -1,1 +1,1 @@\n-class A { }\n+class A {  }\n"),
        []);

    private sealed class MemoryStore : ITriageStore
    {
        public Dictionary<string, TriageResult> Saved { get; } = [];

        public Exception? FailWith { get; init; }

        public Task<TriageResult?> FindTriageAsync(PullRequestKey key, string headSha, string version, CancellationToken cancellationToken) =>
            FailWith is not null ? throw FailWith : Task.FromResult(Saved.GetValueOrDefault($"{headSha}|{version}"));

        public Task SaveTriageAsync(PullRequestKey key, string headSha, string version, TriageResult result, CancellationToken cancellationToken)
        {
            Saved[$"{headSha}|{version}"] = result;
            return Task.CompletedTask;
        }
    }

    private sealed class Checkout(string baseText, string headText) : IPullRequestCheckout
    {
        public int Reads { get; private set; }

        public string RootPath => ".";

        public string BaseSha => WorkerScenario.BaseSha;

        public string HeadSha => WorkerScenario.HeadSha;

        public string MergeBaseSha => WorkerScenario.BaseSha;

        public Task<string> GetDiffAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> ReadBaseFileAsync(string path, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<string?>(baseText);
        }

        public Task<string?> ReadHeadFileAsync(string path, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<string?>(headText);
        }
    }

    [Fact]
    public async Task ComputesWithSourcesThenServesFromTheStore()
    {
        var store = new MemoryStore();
        var runner = new TriageRunner(store, NullLogger<TriageRunner>.Instance);
        var checkout = new Checkout("class A { }\n", "class A {  }\n");

        var first = await runner.RunAsync(Snapshot(), checkout, ReviewSettings.Default, CancellationToken.None);
        var readsAfterFirst = checkout.Reads;
        var second = await runner.RunAsync(Snapshot(), checkout, ReviewSettings.Default, CancellationToken.None);

        Assert.Equal(ChangeClass.Formatting, Assert.Single(first!.Hunks).Class);
        Assert.Same(store.Saved.Values.Single(), second);
        Assert.Equal(readsAfterFirst, checkout.Reads);
    }

    [Fact]
    public async Task ChangedMechanicalPathRulesAreADifferentCacheEntry()
    {
        var store = new MemoryStore();
        var runner = new TriageRunner(store, NullLogger<TriageRunner>.Instance);
        var checkout = new Checkout("class A { }\n", "class A {  }\n");

        await runner.RunAsync(Snapshot(), checkout, ReviewSettings.Default, CancellationToken.None);
        await runner.RunAsync(Snapshot(), checkout, ReviewSettings.Default with { MechanicalPaths = ["gen/**"] }, CancellationToken.None);

        Assert.Equal(2, store.Saved.Count);
    }

    [Fact]
    public async Task AFailureNeverBreaksTheReview()
    {
        var runner = new TriageRunner(new MemoryStore { FailWith = new IOException("disk full") }, NullLogger<TriageRunner>.Instance);

        var result = await runner.RunAsync(Snapshot(), new Checkout("", ""), ReviewSettings.Default, CancellationToken.None);

        Assert.Null(result);
    }
}
