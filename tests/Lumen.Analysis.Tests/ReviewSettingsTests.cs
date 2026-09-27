namespace Lumen.Analysis.Tests;

public sealed class ReviewSettingsTests
{
    [Theory]
    [InlineData("*.g.cs", "src/Api/Client.g.cs", true)]
    [InlineData("*.g.cs", "Client.g.cs", true)]
    [InlineData("*.g.cs", "src/Api/Client.cs", false)]
    [InlineData("src/Contracts/Generated/**", "src/Contracts/Generated/V1/Order.cs", true)]
    [InlineData("src/Contracts/Generated/**", "lib/src/Contracts/Generated/Order.cs", false)]
    [InlineData("src/Contracts/Generated/", "src/Contracts/Generated/Order.cs", true)]
    [InlineData("**/Legacy/*.cs", "src/Billing/Legacy/Old.cs", true)]
    [InlineData("**/Legacy/*.cs", "Legacy/Old.cs", true)]
    [InlineData("**/Legacy/*.cs", "src/Legacy/Deeper/Old.cs", false)]
    [InlineData("Legacy/", "src/Legacy/Deeper/Old.cs", false)]
    [InlineData("src/*.cs", "src/Program.cs", true)]
    [InlineData("src/*.cs", "src/App/Program.cs", false)]
    [InlineData("Program.?s", "src/Program.cs", true)]
    [InlineData(@"src\Generated\**", "src/Generated/A.cs", true)]
    [InlineData("SRC/generated/**", "src/Generated/A.cs", true)]
    [InlineData("   ", "src/A.cs", false)]
    [InlineData("a+b(c).cs", "src/a+b(c).cs", true)]
    public void PathPatternsMatchLikeGitignore(string pattern, string path, bool expected) =>
        Assert.Equal(expected, PathPattern.Matches(pattern, path));

    [Fact]
    public void PresetsAreRecognisedByTheirNumbers()
    {
        Assert.Equal(SensitivityPreset.Balanced, new ReviewSensitivity().Preset);
        Assert.Equal(SensitivityPreset.Quiet, (ReviewSensitivity.Balanced with { MinimumPeers = 5, MinimumSupport = 0.85, MinimumLift = 2.0, MaxPointsPerType = 1, MaxExamples = 3 }).Preset);
        Assert.Equal(SensitivityPreset.Custom, (ReviewSensitivity.Balanced with { MaxExamples = 5 }).Preset);
        Assert.Same(ReviewSensitivity.Thorough, ReviewSensitivity.For(SensitivityPreset.Thorough));
    }

    [Fact]
    public void BalancedKeepsTheDetectorsOriginalPreFilter()
    {
        Assert.Equal(0.6, ReviewSensitivity.Balanced.CandidateSupport, 3);
        Assert.Equal(0.5, ReviewSensitivity.Thorough.CandidateSupport, 3);
        Assert.Equal(0.5, (ReviewSensitivity.Balanced with { MinimumSupport = 0.55 }).CandidateSupport, 3);
    }

    [Fact]
    public void ClampingPullsHandEditedValuesIntoRange()
    {
        var clamped = new ReviewSensitivity { MinimumPeers = 0, MinimumSupport = 1.4, MinimumLift = 0.2, MaxPointsPerType = 99, MaxExamples = -1 }.Clamped();

        Assert.Equal(2, clamped.MinimumPeers);
        Assert.Equal(1.0, clamped.MinimumSupport);
        Assert.Equal(1.0, clamped.MinimumLift);
        Assert.Equal(10, clamped.MaxPointsPerType);
        Assert.Equal(1, clamped.MaxExamples);
    }

    [Fact]
    public void RepositoryPatternsMarkFilesMechanical()
    {
        var result = MechanicalClassifier.Classify("src/Contracts/Generated/Order.cs", [], ["src/Contracts/Generated/**"]);

        Assert.True(result.IsMechanical);
        Assert.Equal("Marked mechanical in settings", result.Reason);
        Assert.False(MechanicalClassifier.Classify("src/Contracts/Order.cs", [], ["src/Contracts/Generated/**"]).IsMechanical);
    }
}
