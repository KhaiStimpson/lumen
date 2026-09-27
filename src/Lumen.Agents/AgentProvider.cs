using System.Text.Json;
using System.Threading.Channels;

namespace Lumen.Agents;

[Flags]
public enum AgentProviderCapabilities
{
    None = 0,
    InteractiveSessions = 1,
    StructuredOutput = 2,
    ToolUse = 4,
    RepositoryAccess = 8,
    WorktreeAccess = 16,
    SubscriptionAuthentication = 32,
    ApiKeyAuthentication = 64,
    UsageStatus = 128,
    ModelSelection = 256,
}

public enum AgentConnectionStatus
{
    NotInstalled,
    SignedOut,
    SignedIn,
    Unknown,
}

/// <summary>Included subscription usage versus metered API usage — never confused in the UI (TDD §39).</summary>
public enum AgentBilling
{
    Unknown,
    Subscription,
    Metered,
}

public sealed record AgentAuthenticationState(
    AgentConnectionStatus Status,
    AgentBilling Billing,
    string Detail,
    string? Version = null,
    string? Plan = null)
{
    public bool IsUsable => Status == AgentConnectionStatus.SignedIn;
}

/// <summary>One bounded, headless agent task. Read-only unless the working directory is an isolated worktree.</summary>
public sealed record AgentSessionRequest
{
    public required string Role { get; init; }

    public required string Prompt { get; init; }

    /// <summary>Appended to the agent's own system prompt.</summary>
    public string? Instructions { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>Built-in tools the agent may use; everything else is unavailable.</summary>
    public required IReadOnlyList<string> Tools { get; init; }

    /// <summary>JSON Schema the final answer must satisfy.</summary>
    public string? OutputSchema { get; init; }

    public string? Model { get; init; }

    public required TimeSpan Timeout { get; init; }

    public long MaxOutputBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Only an explicit user opt-in may route work to metered billing (TDD §37.5).</summary>
    public bool AllowMeteredUsage { get; init; }
}

public enum AgentEventKind
{
    Started,
    Text,
    ToolUse,
    Retry,
}

public sealed record AgentEvent(AgentEventKind Kind, string Summary);

public sealed record AgentSessionResult
{
    public required bool Succeeded { get; init; }

    public string? Text { get; init; }

    public JsonElement? StructuredOutput { get; init; }

    public string? Model { get; init; }

    public AgentBilling Billing { get; init; }

    /// <summary>What the provider reported, for diagnostics. For subscription usage this is not a charge.</summary>
    public double? ReportedCostUsd { get; init; }

    public string? Error { get; init; }

    public TimeSpan Duration { get; init; }
}

/// <summary>A running agent task: progress events while it runs, and a single typed result (TDD §37.1).</summary>
public sealed class AgentSession(string id, ChannelReader<AgentEvent> events, Task<AgentSessionResult> completion, Action cancel)
{
    public string Id { get; } = id;

    public IAsyncEnumerable<AgentEvent> Events(CancellationToken cancellationToken = default) => events.ReadAllAsync(cancellationToken);

    public Task<AgentSessionResult> Completion { get; } = completion;

    public void Cancel() => cancel();
}

/// <summary>A coding agent reached through its official, supported surface — never scraped or impersonated (TDD §37).</summary>
public interface IAgentProvider
{
    string Id { get; }

    AgentProviderCapabilities Capabilities { get; }

    Task<AgentAuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken);

    Task<AgentSession> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken);
}

public sealed class AgentUnavailableException(string message) : Exception(message);
