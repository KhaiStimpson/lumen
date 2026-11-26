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
    public void RepositoryPatternsMarkFilesMechanical()
    {
        var result = MechanicalClassifier.Classify("src/Contracts/Generated/Order.cs", [], ["src/Contracts/Generated/**"]);

        Assert.True(result.IsMechanical);
        Assert.Equal("Marked mechanical in settings", result.Reason);
        Assert.False(MechanicalClassifier.Classify("src/Contracts/Order.cs", [], ["src/Contracts/Generated/**"]).IsMechanical);
    }
}
