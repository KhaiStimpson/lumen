namespace Lumen.Models;

/// <summary>
/// One single-shot completion (plan v2 M7). When <see cref="JsonSchema"/> is set the model is asked for JSON matching
/// it; the caller still validates, because a model can ignore a schema.
/// </summary>
public sealed record ModelRequest
{
    /// <summary>What it's for, e.g. "walkthrough/file". Logs only.</summary>
    public required string Purpose { get; init; }

    public required string System { get; init; }

    public required string User { get; init; }

    public string? SchemaName { get; init; }

    /// <summary>A JSON Schema object, as text.</summary>
    public string? JsonSchema { get; init; }

    public int MaxOutputTokens { get; init; } = 1500;

    public double Temperature { get; init; } = 0.1;
}

public sealed record ModelCompletion(string Text, string Model, int? InputTokens, int? OutputTokens, TimeSpan Latency);

public enum ModelFailure
{
    /// <summary>No credentials stored.</summary>
    NotConnected,

    /// <summary>The provider rejected the credentials.</summary>
    Unauthorized,

    RateLimited,

    /// <summary>The daily request cap in Cloud AI limits was reached.</summary>
    CapReached,

    Timeout,

    /// <summary>Network trouble or a 5xx.</summary>
    Unavailable,

    /// <summary>The provider refused the request (e.g. too large, unknown model).</summary>
    Rejected,
}

public sealed class ModelException : Exception
{
    public ModelException()
    {
    }

    public ModelException(string message)
        : base(message)
    {
    }

    public ModelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ModelException(ModelFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public ModelFailure Failure { get; }
}

/// <summary>A free check of the connection (no completion is spent).</summary>
public sealed record ModelCheck(bool Valid, string Detail);

/// <summary>
/// A fast, single-shot model (plan v2 M7). Distinct from <c>IAgentProvider</c>, which runs tools in a checkout: this
/// only ever sees the text it is sent.
/// </summary>
public interface IModelProvider
{
    /// <summary>e.g. "cloudflare-workers-ai".</summary>
    string Name { get; }

    /// <summary>The model requests go to, e.g. "@cf/zai-org/glm-5.3-flash".</summary>
    string Model { get; }

    /// <summary>Credentials are present (no network).</summary>
    bool HasCredentials { get; }

    Task<ModelCheck> CheckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs one completion. <paramref name="progress"/>, when given, receives the text so far as it streams.
    /// </summary>
    /// <exception cref="ModelException">The request failed; <see cref="ModelException.Failure"/> says how.</exception>
    Task<ModelCompletion> CompleteAsync(ModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>Counts requests against the daily cap in Cloud AI limits.</summary>
public interface IModelUsageMeter
{
    /// <summary>Takes one request from today's allowance; false when the cap is reached.</summary>
    bool TryConsume();

    int UsedToday { get; }
}

/// <summary>No cap: for tests and tools.</summary>
public sealed class UnlimitedUsageMeter : IModelUsageMeter
{
    private int _used;

    public int UsedToday => _used;

    public bool TryConsume()
    {
        Interlocked.Increment(ref _used);
        return true;
    }
}
