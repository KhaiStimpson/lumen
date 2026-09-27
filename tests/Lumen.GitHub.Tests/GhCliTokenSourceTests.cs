namespace Lumen.GitHub.Tests;

public sealed class GhCliTokenSourceTests
{
    private const string MissingGh = "lumen-nonexistent-gh-executable";

    [Fact]
    public async Task UsesEnvironmentVariableAndTrims()
    {
        var reads = 0;
        using var source = new GhCliTokenSource(
            name =>
            {
                reads++;
                return name == GhCliTokenSource.EnvironmentVariableName ? "  env-token\n" : null;
            },
            MissingGh);

        Assert.Equal("env-token", await source.GetTokenAsync(CancellationToken.None));
        Assert.Equal("env-token", await source.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task NoEnvironmentVariableAndNoGhThrowsWithLoginHint()
    {
        using var source = new GhCliTokenSource(_ => "   ", MissingGh);

        var ex = await Assert.ThrowsAsync<GitHubAuthenticationException>(
            () => source.GetTokenAsync(CancellationToken.None));

        Assert.Contains("gh auth login", ex.Message, StringComparison.Ordinal);
        Assert.Contains(GhCliTokenSource.EnvironmentVariableName, ex.Message, StringComparison.Ordinal);
    }
}
