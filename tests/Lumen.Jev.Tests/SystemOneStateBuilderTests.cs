using Lumen.Analysis;
using Lumen.Domain;
using static Lumen.Jev.Tests.Support;

namespace Lumen.Jev.Tests;

public sealed class SystemOneStateBuilderTests
{
    private static readonly AttentionDecision Rule = new(AttentionAction.Surface, ReviewSeverity.Medium, 20, "8/8 peers");

    [Fact]
    public void DefaultStateCarriesNoSourcePathsOrIdentifiers()
    {
        var json = SystemOneStateBuilder.Build(Context(), [("c0", Candidate("pp-secret-key"), Rule)], new PrivacySettings()).ToJsonString();

        foreach (var forbidden in new[] { "InvoiceRetryWorker", "QueueClaimService", "ChargeWorker", "secret-snippet", "Secret", "src/", ".cs", "pp-secret-key", "cu-1", "Add retrying", "acme" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StateDescribesTheCandidateWithNumbersAndCategories()
    {
        var state = SystemOneStateBuilder.Build(Context(), [("c0", Candidate("a", peers: 8, supporting: 6, lift: 2.456), Rule)], new PrivacySettings()).Json;

        Assert.Equal(3, state["pullRequest"]!["filesChanged"]!.GetValue<int>());
        Assert.Equal(1, state["pullRequest"]!["mechanicalFiles"]!.GetValue<int>());
        Assert.Equal(1, state["pullRequest"]!["testFilesChanged"]!.GetValue<int>());

        var candidate = state["candidates"]![0]!;
        Assert.Equal("c0", candidate["id"]!.GetValue<string>());
        Assert.Equal("dependency", candidate["category"]!.GetValue<string>());
        Assert.Equal(8, candidate["repositoryPrecedent"]!["peers"]!.GetValue<int>());
        Assert.Equal(2, candidate["repositoryPrecedent"]!["notFollowing"]!.GetValue<int>());
        Assert.Equal(0.75, candidate["repositoryPrecedent"]!["support"]!.GetValue<double>());
        Assert.Equal(2.46, candidate["repositoryPrecedent"]!["lift"]!.GetValue<double>());
        Assert.Equal("surface", candidate["ruleDecision"]!["action"]!.GetValue<string>());
    }

    [Fact]
    public void ConventionWordingOnlyWithExplicitConsent()
    {
        var json = SystemOneStateBuilder.Build(Context(), [("c0", Candidate("a"), Rule)], new PrivacySettings { AllowCodeSnippetsToJev = true }).ToJsonString();

        Assert.Contains("BackgroundWorker subclasses take QueueClaimService", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-snippet", json, StringComparison.Ordinal);
    }
}
