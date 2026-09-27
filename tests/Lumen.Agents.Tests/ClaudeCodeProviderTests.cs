using Lumen.Agents.ClaudeCode;
using Lumen.Agents.Execution;

namespace Lumen.Agents.Tests;

public sealed class ClaudeCodeProviderTests
{
    private const string SubscriptionStatus = """{"loggedIn": true, "authMethod": "claude.ai", "apiProvider": "firstParty", "subscriptionType": "pro"}""";

    private static readonly string[] SuccessStream =
    [
        """{"type":"system","subtype":"init","session_id":"s1","model":"claude-sonnet-5","tools":["Read","Grep","Glob"]}""",
        """{"type":"assistant","message":{"model":"claude-sonnet-5","content":[{"type":"text","text":"Looking at the providers."},{"type":"tool_use","name":"Grep","input":{"pattern":"ProviderOperationException"}}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","content":"..."}]}}""",
        """{"type":"result","subtype":"success","is_error":false,"result":"done","total_cost_usd":0.12,"structured_output":{"outcome":"supported"}}""",
    ];

    private static readonly string StateDirectory = Path.Combine(Path.GetTempPath(), "lumen-agent-state");

    private static AgentSessionRequest Request(bool allowMetered = false) => new()
    {
        Role = "pattern-investigator",
        Prompt = "Investigate.",
        WorkingDirectory = "C:/data/checkout",
        Tools = ["Read", "Grep", "Glob"],
        OutputSchema = """{"type":"object"}""",
        Timeout = TimeSpan.FromMinutes(3),
        AllowMeteredUsage = allowMetered,
    };

    [Fact]
    public async Task ReportsNotInstalledWithoutRunningAnything()
    {
        var runner = new FakeProcessRunner { Installed = false };

        var state = await new ClaudeCodeProvider(runner, StateDirectory).GetAuthenticationStateAsync(CancellationToken.None);

        Assert.Equal(AgentConnectionStatus.NotInstalled, state.Status);
        Assert.Empty(runner.Commands);
    }

    [Theory]
    [InlineData(SubscriptionStatus, AgentConnectionStatus.SignedIn, AgentBilling.Subscription)]
    [InlineData("""{"loggedIn": false}""", AgentConnectionStatus.SignedOut, AgentBilling.Unknown)]
    [InlineData("""{"loggedIn": true, "authMethod": "api_key", "apiProvider": "firstParty"}""", AgentConnectionStatus.SignedIn, AgentBilling.Metered)]
    [InlineData("""{"loggedIn": true, "authMethod": "claude.ai", "apiProvider": "bedrock"}""", AgentConnectionStatus.SignedIn, AgentBilling.Metered)]
    [InlineData("not json", AgentConnectionStatus.Unknown, AgentBilling.Unknown)]
    public void ClassifiesSignInAndBilling(string json, AgentConnectionStatus status, AgentBilling billing)
    {
        var state = ClaudeCodeProvider.ParseAuthStatus(json, "2.1.257 (Claude Code)");

        Assert.Equal(status, state.Status);
        Assert.Equal(billing, state.Billing);
    }

    [Fact]
    public async Task ReadsSignInThroughTheOfficialStatusCommand()
    {
        var runner = new FakeProcessRunner()
            .On("--version", ["2.1.257 (Claude Code)"])
            .On("auth status --json", [SubscriptionStatus]);

        var state = await new ClaudeCodeProvider(runner, StateDirectory).GetAuthenticationStateAsync(CancellationToken.None);

        Assert.Equal(AgentBilling.Subscription, state.Billing);
        Assert.Equal("pro", state.Plan);
        Assert.Equal("2.1.257 (Claude Code)", state.Version);
    }

    [Fact]
    public void RunsHeadlessReadOnlyAndNeverBare()
    {
        var args = ClaudeCodeProvider.BuildArguments(Request());

        Assert.Equal("-p", args[0]);
        Assert.Contains("--restricted", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.Equal("dontAsk", args[args.IndexOf("--permission-mode") + 1]);
        Assert.Equal("Read,Grep,Glob", args[args.IndexOf("--tools") + 1]);
        Assert.Equal("stream-json", args[args.IndexOf("--output-format") + 1]);
        Assert.DoesNotContain("--bare", args);
        Assert.DoesNotContain(args, a => a.Contains("dangerously", StringComparison.Ordinal) || a == "bypassPermissions");
    }

    [Fact]
    public async Task StreamsProgressAndReturnsStructuredOutput()
    {
        var runner = new FakeProcessRunner()
            .On("auth status", [SubscriptionStatus])
            .On("-p", SuccessStream);
        var session = await new ClaudeCodeProvider(runner, StateDirectory).StartSessionAsync(Request(), CancellationToken.None);

        var events = new List<AgentEvent>();
        await foreach (var evt in session.Events())
        {
            events.Add(evt);
        }

        var result = await session.Completion;

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("supported", result.StructuredOutput!.Value.GetProperty("outcome").GetString());
        Assert.Equal("claude-sonnet-5", result.Model);
        Assert.Equal(AgentBilling.Subscription, result.Billing);
        Assert.Equal([AgentEventKind.Started, AgentEventKind.Text, AgentEventKind.ToolUse], events.Select(e => e.Kind));
        Assert.Equal("Grep ProviderOperationException", events[2].Summary);

        var command = runner.Commands.Last();
        Assert.Equal("Investigate.", command.StandardInput);
        Assert.Equal("C:/data/checkout", command.WorkingDirectory);
    }

    [Fact]
    public async Task RefusesToStartWhenBillingIsMetered()
    {
        var runner = new FakeProcessRunner().On("auth status", ["""{"loggedIn": true, "authMethod": "api_key"}"""]);

        await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            new ClaudeCodeProvider(runner, StateDirectory).StartSessionAsync(Request(), CancellationToken.None));
        Assert.DoesNotContain(runner.Commands, c => c.Arguments.Contains("-p"));
    }

    [Fact]
    public async Task RefusesToStartWhenSignedOut()
    {
        var runner = new FakeProcessRunner().On("auth status", ["""{"loggedIn": false}"""]);

        var ex = await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            new ClaudeCodeProvider(runner, StateDirectory).StartSessionAsync(Request(), CancellationToken.None));
        Assert.Contains("sign in", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopsWhenTheSessionReportsAnApiKeySource()
    {
        var runner = new FakeProcessRunner()
            .On("auth status", [SubscriptionStatus])
            .On("-p", ["""{"type":"system","subtype":"init","model":"claude-sonnet-5","apiKeySource":"ANTHROPIC_API_KEY"}""", .. SuccessStream[1..]]);

        var session = await new ClaudeCodeProvider(runner, StateDirectory).StartSessionAsync(Request(), CancellationToken.None);
        var result = await session.Completion;

        Assert.False(result.Succeeded);
        Assert.Contains("metered", result.Error, StringComparison.Ordinal);
        Assert.Equal(AgentBilling.Metered, result.Billing);
    }

    [Fact]
    public async Task ReportsErrorResultsAndTimeouts()
    {
        var failing = new FakeProcessRunner()
            .On("auth status", [SubscriptionStatus])
            .On("-p", ["""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"rate limited"}"""], new ProcessResult(ProcessEnd.Exited, 1, "", 0));
        var failed = await (await new ClaudeCodeProvider(failing, StateDirectory).StartSessionAsync(Request(), CancellationToken.None)).Completion;
        Assert.False(failed.Succeeded);
        Assert.Contains("rate limited", failed.Error, StringComparison.Ordinal);

        var slow = new FakeProcessRunner()
            .On("auth status", [SubscriptionStatus])
            .On("-p", SuccessStream[..2], new ProcessResult(ProcessEnd.TimedOut, null, "", 0));
        var timedOut = await (await new ClaudeCodeProvider(slow, StateDirectory).StartSessionAsync(Request(), CancellationToken.None)).Completion;
        Assert.False(timedOut.Succeeded);
        Assert.StartsWith("Timed out", timedOut.Error, StringComparison.Ordinal);
    }
}
