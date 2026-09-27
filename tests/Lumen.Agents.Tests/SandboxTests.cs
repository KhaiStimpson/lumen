using Lumen.Agents.Execution;

namespace Lumen.Agents.Tests;

public sealed class SandboxTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "lumen-agent-root"));

    private static CommandPolicy Policy(params string[] roots) => new()
    {
        AllowedExecutables = new HashSet<string>(StringComparer.Ordinal) { "git", "claude" },
        AllowedWorkingRoots = roots.Length == 0 ? [Root] : roots,
    };

    private static SandboxedCommand Command(string executable = "git", string? directory = null, TimeSpan? timeout = null, long maxOutput = 1024) => new()
    {
        Executable = executable,
        Arguments = ["--version"],
        WorkingDirectory = directory ?? Path.Combine(Root, "worktrees", "a"),
        Timeout = timeout ?? TimeSpan.FromSeconds(30),
        MaxOutputBytes = maxOutput,
    };

    [Fact]
    public void AcceptsAllowedExecutableInsideARoot() => Policy().Validate(Command());

    [Theory]
    [InlineData("powershell")]
    [InlineData("cmd")]
    [InlineData("claude.exe")]
    public void RejectsExecutablesOffTheAllowlist(string executable) =>
        Assert.Throws<CommandRejectedException>(() => Policy().Validate(Command(executable)));

    [Fact]
    public void RejectsWorkingDirectoriesOutsideTheRoots()
    {
        var policy = Policy();
        Assert.Throws<CommandRejectedException>(() => policy.Validate(Command(directory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
        Assert.Throws<CommandRejectedException>(() => policy.Validate(Command(directory: Root + "-sibling")));
        Assert.Throws<CommandRejectedException>(() => policy.Validate(Command(directory: Path.Combine(Root, "..", "escape"))));
        Assert.Throws<CommandRejectedException>(() => policy.Validate(Command(directory: "relative/path")));
    }

    [Fact]
    public void RejectsUnboundedTimeoutsAndOutput()
    {
        Assert.Throws<CommandRejectedException>(() => Policy().Validate(Command(timeout: TimeSpan.FromHours(2))));
        Assert.Throws<CommandRejectedException>(() => Policy().Validate(Command(maxOutput: long.MaxValue)));
    }

    [Fact]
    public void EnvironmentKeepsOnlyTheAllowlistPlusOverrides()
    {
        var current = new Dictionary<string, string>
        {
            ["PATH"] = "C:/bin",
            ["USERPROFILE"] = "C:/Users/me",
            ["ANTHROPIC_API_KEY"] = "sk-ant-secret",
            ["ANTHROPIC_AUTH_TOKEN"] = "token",
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
            ["GITHUB_TOKEN"] = "ghp_secret",
            ["LUMEN_GITHUB_TOKEN"] = "ghp_secret",
            ["OPENAI_API_KEY"] = "sk-secret",
        };

        var env = Policy().BuildEnvironment(current, new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });

        Assert.Equal(["GIT_TERMINAL_PROMPT", "PATH", "USERPROFILE"], env.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RunsARealProcessAndStreamsItsOutput()
    {
        var runner = new SandboxedProcessRunner(Policy(Path.GetTempPath()));
        if (runner.Resolve("git") is null)
        {
            return;
        }

        var lines = new List<string>();
        var result = await runner.RunAsync(
            Command(directory: Path.GetTempPath(), timeout: TimeSpan.FromSeconds(30)),
            line =>
            {
                lines.Add(line);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardErrorTail);
        Assert.StartsWith("git version", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopsAProcessThatExceedsTheOutputLimit()
    {
        var runner = new SandboxedProcessRunner(Policy(Path.GetTempPath()));
        if (runner.Resolve("git") is null)
        {
            return;
        }

        var result = await runner.RunAsync(
            Command(directory: Path.GetTempPath(), maxOutput: 5) with { Arguments = ["help", "-a"] },
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(ProcessEnd.OutputLimitExceeded, result.End);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RefusesToStartADisallowedCommand()
    {
        var runner = new SandboxedProcessRunner(Policy(Path.GetTempPath()));
        Assert.Null(runner.Resolve("powershell"));
        await Assert.ThrowsAsync<CommandRejectedException>(() =>
            runner.RunAsync(Command("powershell", Path.GetTempPath()), _ => ValueTask.CompletedTask, CancellationToken.None));
    }
}
