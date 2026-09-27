using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Lumen.Agents.Execution;

namespace Lumen.Agents.ClaudeCode;

/// <summary>
/// The user's own, unmodified Claude Code CLI, signed in with their own subscription (TDD §37.2). Lumen never reads,
/// stores or forwards Claude credentials, never passes <c>--bare</c> (which bypasses subscription sign-in), and runs
/// the CLI with a scrubbed environment so a stray <c>ANTHROPIC_API_KEY</c> cannot silently switch billing (§37.5).
/// </summary>
public sealed class ClaudeCodeProvider(IProcessRunner runner, string stateDirectory) : IAgentProvider
{
    public const string ProviderId = "claude-code";
    public const string Executable = "claude";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);

    public string Id => ProviderId;

    public AgentProviderCapabilities Capabilities =>
        AgentProviderCapabilities.StructuredOutput |
        AgentProviderCapabilities.ToolUse |
        AgentProviderCapabilities.RepositoryAccess |
        AgentProviderCapabilities.WorktreeAccess |
        AgentProviderCapabilities.SubscriptionAuthentication |
        AgentProviderCapabilities.ModelSelection;

    public async Task<AgentAuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken)
    {
        if (runner.Resolve(Executable) is null)
        {
            return new AgentAuthenticationState(AgentConnectionStatus.NotInstalled, AgentBilling.Unknown, "Claude Code is not installed");
        }

        Directory.CreateDirectory(stateDirectory);
        var version = (await RunForTextAsync(["--version"], cancellationToken).ConfigureAwait(false)).Text?.Trim();
        var (result, text) = await RunForTextAsync(["auth", "status", "--json"], cancellationToken).ConfigureAwait(false);
        if (text is null || !result.Succeeded && !text.TrimStart().StartsWith('{'))
        {
            return new AgentAuthenticationState(AgentConnectionStatus.Unknown, AgentBilling.Unknown, "Could not read Claude Code sign-in status", version);
        }

        return ParseAuthStatus(text, version);
    }

    internal static AgentAuthenticationState ParseAuthStatus(string json, string? version)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var loggedIn = root.TryGetProperty("loggedIn", out var l) && l.ValueKind == JsonValueKind.True;
            var method = Read(root, "authMethod");
            var provider = Read(root, "apiProvider");
            var plan = Read(root, "subscriptionType");

            if (!loggedIn)
            {
                return new AgentAuthenticationState(AgentConnectionStatus.SignedOut, AgentBilling.Unknown, "Not signed in — run `claude` and sign in", version);
            }

            // Only a claude.ai login on Anthropic's own service counts as included subscription usage.
            var subscription = string.Equals(method, "claude.ai", StringComparison.OrdinalIgnoreCase) &&
                               (provider is null || string.Equals(provider, "firstParty", StringComparison.OrdinalIgnoreCase));
            return subscription
                ? new AgentAuthenticationState(AgentConnectionStatus.SignedIn, AgentBilling.Subscription, $"Claude {Capitalize(plan) ?? "subscription"} via Claude Code", version, plan)
                : new AgentAuthenticationState(AgentConnectionStatus.SignedIn, AgentBilling.Metered, $"Signed in via {method ?? "API key"} ({provider ?? "unknown provider"}) — metered", version, plan);
        }
        catch (JsonException)
        {
            return new AgentAuthenticationState(AgentConnectionStatus.Unknown, AgentBilling.Unknown, "Unrecognised Claude Code status output", version);
        }
    }

    public async Task<AgentSession> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var auth = await GetAuthenticationStateAsync(cancellationToken).ConfigureAwait(false);
        if (!auth.IsUsable)
        {
            throw new AgentUnavailableException(auth.Detail);
        }

        if (auth.Billing != AgentBilling.Subscription && !request.AllowMeteredUsage)
        {
            throw new AgentUnavailableException("Claude Code is not using a Claude subscription; metered usage needs an explicit opt-in.");
        }

        var command = new SandboxedCommand
        {
            Executable = Executable,
            Arguments = BuildArguments(request),
            WorkingDirectory = request.WorkingDirectory,
            StandardInput = request.Prompt,
            Timeout = request.Timeout,
            MaxOutputBytes = request.MaxOutputBytes,
        };

        var id = $"{ProviderId}-{Guid.NewGuid():N}"[..24];
        var events = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = Task.Run(() => RunAsync(command, request, auth, events.Writer, cts), CancellationToken.None);
        return new AgentSession(id, events.Reader, completion, () => cts.Cancel());
    }

    internal static List<string> BuildArguments(AgentSessionRequest request)
    {
        var tools = string.Join(',', request.Tools);
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            // Confines file tools to the working directory, removes code-running tools and ignores user settings.
            "--restricted",
            "--strict-mcp-config",
            "--no-session-persistence",
            "--permission-mode", "dontAsk",
            "--tools", tools,
            "--allowedTools", tools,
        };

        if (request.OutputSchema is not null)
        {
            args.Add("--json-schema");
            args.Add(request.OutputSchema);
        }

        if (request.Model is not null)
        {
            args.Add("--model");
            args.Add(request.Model);
        }

        if (request.Instructions is not null)
        {
            args.Add("--append-system-prompt");
            args.Add(request.Instructions);
        }

        return args;
    }

    private async Task<AgentSessionResult> RunAsync(
        SandboxedCommand command,
        AgentSessionRequest request,
        AgentAuthenticationState auth,
        ChannelWriter<AgentEvent> events,
        CancellationTokenSource cts)
    {
        var stopwatch = Stopwatch.StartNew();
        var stream = new ClaudeStream();
        string? refusal = null;
        try
        {
            var process = await runner.RunAsync(
                command,
                line =>
                {
                    foreach (var evt in stream.Accept(line))
                    {
                        events.TryWrite(evt);
                    }

                    if (stream.UsesApiKey && !request.AllowMeteredUsage && refusal is null)
                    {
                        refusal = $"Claude Code reported an API key ({stream.ApiKeySource}); stopped to avoid metered billing.";
                        cts.Cancel();
                    }

                    return ValueTask.CompletedTask;
                },
                cts.Token).ConfigureAwait(false);

            var billing = stream.UsesApiKey ? AgentBilling.Metered : auth.Billing;
            if (refusal is not null)
            {
                return Failed(refusal, stream, billing, stopwatch.Elapsed);
            }

            if (process.End != ProcessEnd.Exited)
            {
                return Failed(process.End switch
                {
                    ProcessEnd.TimedOut => $"Timed out after {command.Timeout.TotalSeconds:0}s",
                    ProcessEnd.OutputLimitExceeded => "Stopped: output limit exceeded",
                    _ => "Cancelled",
                }, stream, billing, stopwatch.Elapsed);
            }

            if (!stream.SawResult || stream.IsError)
            {
                var detail = stream.ResultText ?? stream.Subtype ?? LastLine(process.StandardErrorTail) ?? $"exit code {process.ExitCode}";
                return Failed($"Claude Code failed: {detail}", stream, billing, stopwatch.Elapsed);
            }

            return new AgentSessionResult
            {
                Succeeded = true,
                Text = stream.ResultText ?? stream.LastText,
                StructuredOutput = stream.StructuredOutput,
                Model = stream.Model,
                Billing = billing,
                ReportedCostUsd = stream.CostUsd,
                Duration = stopwatch.Elapsed,
            };
        }
        catch (CommandRejectedException ex)
        {
            return Failed(ex.Message, stream, auth.Billing, stopwatch.Elapsed);
        }
        finally
        {
            events.TryComplete();
            cts.Dispose();
        }
    }

    private static AgentSessionResult Failed(string error, ClaudeStream stream, AgentBilling billing, TimeSpan duration) => new()
    {
        Succeeded = false,
        Error = error,
        Model = stream.Model,
        Billing = billing,
        ReportedCostUsd = stream.CostUsd,
        Duration = duration,
    };

    private async Task<(ProcessResult Result, string? Text)> RunForTextAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var output = new System.Text.StringBuilder();
        try
        {
            var result = await runner.RunAsync(
                new SandboxedCommand
                {
                    Executable = Executable,
                    Arguments = arguments,
                    WorkingDirectory = stateDirectory,
                    Timeout = StatusTimeout,
                    MaxOutputBytes = 256 * 1024,
                },
                line =>
                {
                    output.AppendLine(line);
                    return ValueTask.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);
            return (result, output.Length == 0 ? null : output.ToString());
        }
        catch (CommandRejectedException)
        {
            return (new ProcessResult(ProcessEnd.Exited, -1, "", 0), null);
        }
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    private static string? Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Capitalize(string? s) => string.IsNullOrEmpty(s) ? null : char.ToUpperInvariant(s[0]) + s[1..];
}
