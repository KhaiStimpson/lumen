namespace Lumen.Domain;

/// <summary>
/// One answered JEV question (TDD §38.1): the choice, the probabilities behind it when the model exposed them, the
/// model that answered and the question schema version, so thresholds can be calibrated and evaluations replayed.
/// </summary>
public sealed record AttentionEvaluationRecord
{
    public required PullRequestKey Key { get; init; }

    public required string HeadSha { get; init; }

    public required string ChangeUnitId { get; init; }

    public required string CandidateKey { get; init; }

    public required string QuestionId { get; init; }

    public required string QuestionSchemaVersion { get; init; }

    public required string Choice { get; init; }

    /// <summary>Probability per choice; null when the model returned no token probabilities.</summary>
    public IReadOnlyDictionary<string, double>? Probabilities { get; init; }

    /// <summary>The model requested, e.g. a pinned OpenRouter id.</summary>
    public required string Model { get; init; }

    /// <summary>The model that actually answered, as reported by the provider.</summary>
    public string? ResolvedModel { get; init; }

    public required string Provider { get; init; }

    /// <summary>The exact source-free state that was sent, for replay.</summary>
    public required string StateJson { get; init; }

    public long LatencyMs { get; init; }

    public required DateTimeOffset At { get; init; }
}

public interface IAttentionEvaluationStore
{
    Task RecordEvaluationsAsync(IReadOnlyList<AttentionEvaluationRecord> records, CancellationToken cancellationToken);

    Task<IReadOnlyList<AttentionEvaluationRecord>> GetEvaluationsAsync(
        PullRequestKey key,
        string headSha,
        CancellationToken cancellationToken);
}

/// <summary>Investigation results are cached per head commit so reopening a pull request never spends usage twice.</summary>
public interface IInvestigationStore
{
    Task SaveInvestigationAsync(
        PullRequestKey key,
        string headSha,
        string candidateKey,
        InvestigationResult result,
        CancellationToken cancellationToken);

    Task<InvestigationResult?> FindInvestigationAsync(
        PullRequestKey key,
        string headSha,
        string candidateKey,
        InvestigationType type,
        CancellationToken cancellationToken);

    Task<int> CountInvestigationsAsync(PullRequestKey key, string headSha, CancellationToken cancellationToken);
}
