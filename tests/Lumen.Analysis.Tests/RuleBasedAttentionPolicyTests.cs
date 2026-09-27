using Lumen.Domain;

namespace Lumen.Analysis.Tests;

public sealed class RuleBasedAttentionPolicyTests
{
    private static Candidate Make(
        int supporting,
        int peers,
        double lift = 3,
        string? category = "usage",
        bool introduced = true,
        ReviewPointType type = ReviewPointType.PatternDeviation,
        int locations = 1) => new()
        {
            Key = "pp-test",
            DetectorId = "test",
            Type = type,
            ChangeUnits = [],
            Anchor = new CodeLocation("src/A.cs", 1, 1),
            Evidence = [],
            Facts = new object(),
            Signals = new AttentionSignals
            {
                ChangeKind = "type-added",
                IntroducedByChange = introduced,
                Supporting = supporting,
                PeerCount = peers,
                Lift = lift,
                Category = category,
                AffectedLocations = locations,
            },
        };

    private static async Task<AttentionDecision> Decide(Candidate candidate, RuleBasedAttentionPolicy? policy = null) =>
        await (policy ?? new RuleBasedAttentionPolicy()).DecideAsync(candidate, CancellationToken.None);

    [Fact]
    public async Task SuppressesDeviationsThatPredateTheChange()
    {
        var decision = await Decide(Make(4, 4, introduced: false));

        Assert.Equal(AttentionAction.Suppress, decision.Action);
        Assert.Equal("Deviation predates this change", decision.Reason);
    }

    [Fact]
    public async Task SuppressesPreExistingEvenForNonPatternTypes()
    {
        Assert.Equal(AttentionAction.Suppress, (await Decide(Make(0, 0, introduced: false, type: ReviewPointType.ErrorHandling))).Action);
    }

    [Fact]
    public async Task SuppressesWhenTooFewPeers()
    {
        var decision = await Decide(Make(2, 2));

        Assert.Equal(AttentionAction.Suppress, decision.Action);
        Assert.Contains("2 peers", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MinimumPeersIsConfigurable()
    {
        Assert.Equal(AttentionAction.Surface, (await Decide(Make(2, 2), new RuleBasedAttentionPolicy { MinimumPeers = 2 })).Action);
    }

    [Theory]
    [InlineData(2, 3)] // 0.67
    [InlineData(3, 5)] // 0.60
    [InlineData(4, 6)] // 0.67 with fewer than 5 supporting
    public async Task SuppressesLowSupport(int supporting, int peers)
    {
        Assert.Equal(AttentionAction.Suppress, (await Decide(Make(supporting, peers))).Action);
    }

    [Theory]
    [InlineData(3, 4)] // 0.75, exactly the default minimum
    [InlineData(4, 4)]
    public async Task SurfacesAtOrAboveMinimumSupport(int supporting, int peers)
    {
        Assert.Equal(AttentionAction.Surface, (await Decide(Make(supporting, peers))).Action);
    }

    [Theory]
    [InlineData(5, 7)]  // 0.714: below 0.75 but above the relaxed 0.70
    [InlineData(7, 10)] // exactly 0.70
    public async Task RelaxesSupportThresholdWhenFiveOrMorePeersAgree(int supporting, int peers)
    {
        Assert.Equal(AttentionAction.Surface, (await Decide(Make(supporting, peers))).Action);
    }

    [Fact]
    public async Task RelaxedThresholdStillRejectsWeakMajorities()
    {
        Assert.Equal(AttentionAction.Suppress, (await Decide(Make(6, 9))).Action); // 0.67
    }

    [Fact]
    public async Task RelaxationAppliesOnlyFromFiveSupporting()
    {
        var policy = new RuleBasedAttentionPolicy { MinimumSupport = 0.8 };

        Assert.Equal(AttentionAction.Suppress, (await Decide(Make(3, 4), policy)).Action);  // 0.75 < 0.80
        Assert.Equal(AttentionAction.Surface, (await Decide(Make(7, 9), policy)).Action);   // 0.78 >= relaxed 0.75
    }

    [Theory]
    [InlineData(1.4, "usage", AttentionAction.Suppress)]
    [InlineData(1.5, "usage", AttentionAction.Surface)]
    [InlineData(1.5, "framework-dependency", AttentionAction.Surface)]
    [InlineData(2.0, "dependency", AttentionAction.Suppress)]
    [InlineData(2.4, "dependency", AttentionAction.Suppress)]
    [InlineData(2.5, "dependency", AttentionAction.Surface)]
    public async Task RequiresLiftAndAStricterLiftForDependencies(double lift, string category, AttentionAction expected)
    {
        Assert.Equal(expected, (await Decide(Make(4, 4, lift, category))).Action);
    }

    [Fact]
    public async Task NonPatternCandidatesSkipPeerThresholds()
    {
        var decision = await Decide(Make(0, 0, lift: 0, category: "security", type: ReviewPointType.SecurityRisk));

        Assert.Equal(AttentionAction.Surface, decision.Action);
        Assert.Equal(ReviewSeverity.Low, decision.Severity);
    }

    [Theory]
    [InlineData("error-handling", 9, 10, ReviewSeverity.High)]
    [InlineData("error-handling", 4, 4, ReviewSeverity.High)]
    [InlineData("error-handling", 4, 5, ReviewSeverity.Medium)]
    [InlineData("concurrency", 4, 4, ReviewSeverity.High)]
    [InlineData("concurrency", 3, 4, ReviewSeverity.Medium)]
    [InlineData("dependency", 4, 4, ReviewSeverity.Medium)]
    [InlineData("framework-dependency", 10, 10, ReviewSeverity.Low)]
    [InlineData("usage", 5, 5, ReviewSeverity.Medium)]
    [InlineData("usage", 4, 4, ReviewSeverity.Low)]
    [InlineData("usage", 19, 20, ReviewSeverity.Medium)]
    [InlineData("usage", 9, 10, ReviewSeverity.Low)]
    [InlineData(null, 3, 4, ReviewSeverity.Low)]
    public async Task MapsSeverity(string? category, int supporting, int peers, ReviewSeverity expected)
    {
        var decision = await Decide(Make(supporting, peers, lift: 3, category: category));

        Assert.Equal(AttentionAction.Surface, decision.Action);
        Assert.Equal(expected, decision.Severity);
    }

    [Fact]
    public async Task PriorityCombinesSeveritySupportLiftAndLocations()
    {
        // Medium (1) * 10 + support 1.0 * 5 + min(lift, 5) + min(locations, 5)
        Assert.Equal(10 + 5 + 3 + 2, (await Decide(Make(4, 4, lift: 3, category: "dependency", locations: 2))).Priority, 6);
        Assert.Equal(10 + 5 + 5 + 5, (await Decide(Make(4, 4, lift: 9, category: "dependency", locations: 8))).Priority, 6);
    }

    [Fact]
    public async Task SuppressedDecisionsHaveZeroPriority()
    {
        var decision = await Decide(Make(1, 1));

        Assert.Equal((ReviewSeverity.Low, 0d), (decision.Severity, decision.Priority));
    }

    [Fact]
    public async Task TheRepositorysSensitivitySetsTheThresholds()
    {
        var candidate = Make(4, 5, lift: 1.8);

        Assert.Equal(AttentionAction.Surface, (await RuleBasedAttentionPolicy.DecideAsync(candidate, ReviewSensitivity.Balanced)).Action);
        Assert.Equal(AttentionAction.Suppress, (await RuleBasedAttentionPolicy.DecideAsync(candidate, ReviewSensitivity.Quiet)).Action);
        Assert.Equal(AttentionAction.Surface, (await RuleBasedAttentionPolicy.DecideAsync(Make(2, 3, lift: 1.3), ReviewSensitivity.Thorough)).Action);
    }
}
