using System.Text.Json.Nodes;

namespace Lumen.Jev;

/// <summary>The three answer shapes the OpenRouter Decisions API supports.</summary>
public enum SystemOneQuestionKind
{
    /// <summary>A yes/no question answered with P(true).</summary>
    Noul,

    /// <summary>One of several named candidates, with a probability per candidate.</summary>
    Choice,

    /// <summary>A rung on an ordered scale, with a probability per rung.</summary>
    Score,
}

/// <summary>A typed, bounded question (TDD §10.2). Ids are lowercase snake_case so they are valid wire keys.</summary>
public sealed record SystemOneQuestion(
    string Id,
    SystemOneQuestionKind Kind,
    string Instructions,
    IReadOnlyDictionary<string, string> Criteria);

/// <summary>
/// One answer. <see cref="Choice"/> is always set ("true"/"false" for noul questions); <see cref="Probabilities"/>
/// holds whatever distribution the model returned.
/// </summary>
public sealed record SystemOneAnswer(
    string QuestionId,
    string Choice,
    IReadOnlyDictionary<string, double> Probabilities,
    double? Confidence = null)
{
    /// <summary>P(true) for a noul question.</summary>
    public double PTrue => Probabilities.GetValueOrDefault("true");
}

/// <summary>Source-free structured state (TDD §10.1). Built only by <see cref="SystemOneStateBuilder"/>.</summary>
public sealed record SystemOneState(JsonObject Json)
{
    public string ToJsonString() => Json.ToJsonString();
}

public sealed record SystemOneResult(
    IReadOnlyDictionary<string, SystemOneAnswer> Answers,
    string Model,
    string? ResolvedModel,
    string Provider,
    double? Cost,
    TimeSpan Latency);

/// <summary>
/// A "System 1" evaluator (TDD §38): fast typed classification, never a reviewer. Related questions are batched
/// into one call (§38.1), so this takes a question set rather than one generic question.
/// </summary>
public interface ISystemOneEvaluator
{
    /// <summary>Whether a credential is present; says nothing about whether the service is reachable.</summary>
    bool IsConfigured { get; }

    string Model { get; }

    Task<SystemOneResult> EvaluateAsync(
        SystemOneState state,
        IReadOnlyList<SystemOneQuestion> questions,
        CancellationToken cancellationToken);
}

public enum SystemOneFailure
{
    NotConfigured,
    Unauthorized,
    InsufficientCredits,
    RateLimited,
    Unavailable,
    InvalidResponse,
    Timeout,
}

public sealed class SystemOneUnavailableException(SystemOneFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public SystemOneFailure Failure { get; } = failure;

    /// <summary>Failures that will not fix themselves within a review: stop calling for a while.</summary>
    public bool IsPersistent => Failure is SystemOneFailure.NotConfigured or SystemOneFailure.Unauthorized or SystemOneFailure.InsufficientCredits;
}
