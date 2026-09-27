using System.Diagnostics;

namespace Lumen.Repository.Tests;

public sealed class GitRunnerTests
{
    private readonly GitRunner git = new();

    [Fact]
    public async Task NonZeroExitThrowsWithCommandAndStandardError()
    {
        var error = await Assert.ThrowsAsync<GitCommandException>(() =>
            git.RunAsync(Path.GetTempPath(), ["rev-parse", "--verify", "definitely-not-a-ref"], CancellationToken.None));

        Assert.Equal(128, error.ExitCode);
        Assert.Contains("rev-parse --verify definitely-not-a-ref", error.Command, StringComparison.Ordinal);
        Assert.Contains("core.quotepath=false", error.Command, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(error.StandardError));
    }

    [Fact]
    public async Task ThrowOnErrorFalseReturnsResult()
    {
        var result = await git.RunAsync(Path.GetTempPath(), ["rev-parse", "--verify", "nope"], CancellationToken.None, throwOnError: false);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.StandardError);
    }

    [Fact]
    public async Task EnvironmentConfigIsVisibleToGit()
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "lumen.probe",
            ["GIT_CONFIG_VALUE_0"] = "hello",
        };

        var result = await git.RunAsync(Path.GetTempPath(), ["config", "--get", "lumen.probe"], CancellationToken.None, env);

        Assert.Equal("hello", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task LargeOutputDoesNotDeadlock()
    {
        using var remote = await TestRemote.CreateAsync();

        var result = await git.RunAsync(
            remote.RepoPath,
            ["log", "-p", "--all", "--stat", "-U100000"],
            CancellationToken.None,
            timeout: TimeSpan.FromMinutes(1));

        Assert.True(result.StandardOutput.Length > 200_000);
    }

    [Fact]
    public async Task CancellationKillsProcess()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            git.RunAsync(Path.GetTempPath(), ["-c", "alias.wait=!sleep 30", "wait"], cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task TimeoutThrowsTimeoutException()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            git.RunAsync(Path.GetTempPath(), ["-c", "alias.wait=!sleep 30", "wait"], CancellationToken.None, timeout: TimeSpan.FromMilliseconds(300)));
    }
}
