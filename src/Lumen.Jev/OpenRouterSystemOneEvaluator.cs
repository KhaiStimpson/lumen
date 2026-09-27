using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lumen.Domain;

namespace Lumen.Jev;

public sealed record OpenRouterOptions
{
    /// <summary>Pinned for reproducible evaluations (TDD §38); "~typesafe/jev-latest" is an explicit opt-in.</summary>
    public string Model { get; init; } = "typesafe/jev-1.13";

    /// <summary>The Decisions API. Alpha: breaking changes fail closed into the rule-based fallback (§38.2).</summary>
    public Uri DecisionsEndpoint { get; init; } = new("https://openrouter.ai/api/alpha/decisions");

    public Uri KeyEndpoint { get; init; } = new("https://openrouter.ai/api/v1/key");

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Route only to endpoints that retain nothing and never train on requests.</summary>
    public bool RequireZeroDataRetention { get; init; } = true;
}

public sealed record OpenRouterKeyStatus(bool Valid, string Detail);

/// <summary>
/// JEV over OpenRouter's Decisions API (TDD §38): one request carries every question for a batch, and each answer
/// comes back with its probabilities. The key is read from the platform credential store on each call.
/// </summary>
public sealed class OpenRouterSystemOneEvaluator(HttpClient http, ISecretStore secrets, OpenRouterOptions options) : ISystemOneEvaluator
{
    public const string ProviderName = "openrouter";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ReadKey());

    public string Model => options.Model;

    public async Task<SystemOneResult> EvaluateAsync(
        SystemOneState state,
        IReadOnlyList<SystemOneQuestion> questions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);

        var key = ReadKey() ?? throw new SystemOneUnavailableException(SystemOneFailure.NotConfigured, "No OpenRouter API key is stored.");
        using var request = new HttpRequestMessage(HttpMethod.Post, options.DecisionsEndpoint)
        {
            Content = JsonContent.Create(BuildRequest(state, questions)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add("X-Title", "Lumen");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SystemOneUnavailableException(SystemOneFailure.Timeout, $"JEV did not answer within {options.RequestTimeout.TotalSeconds:0}s", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SystemOneUnavailableException(SystemOneFailure.Unavailable, $"OpenRouter unreachable: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response.StatusCode, body);
            }

            return ParseResponse(body, questions, options.Model, stopwatch.Elapsed);
        }
    }

    /// <summary>Validates the stored key against OpenRouter's key-info endpoint, which costs nothing.</summary>
    public async Task<OpenRouterKeyStatus> CheckKeyAsync(CancellationToken cancellationToken)
    {
        var key = ReadKey();
        if (key is null)
        {
            return new OpenRouterKeyStatus(false, "No key stored");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, options.KeyEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new OpenRouterKeyStatus(false, $"OpenRouter rejected the key ({(int)response.StatusCode})");
            }

            var info = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))?["data"];
            var remaining = info?["limit_remaining"]?.ToString();
            return new OpenRouterKeyStatus(true, remaining is null ? "Key valid" : $"Key valid · {remaining} credit remaining");
        }
        catch (HttpRequestException ex)
        {
            return new OpenRouterKeyStatus(false, $"OpenRouter unreachable: {ex.Message}");
        }
    }

    internal JsonObject BuildRequest(SystemOneState state, IReadOnlyList<SystemOneQuestion> questions)
    {
        var questionMap = new JsonObject();
        foreach (var q in questions)
        {
            var criteria = new JsonObject();
            foreach (var (name, text) in q.Criteria)
            {
                criteria[name] = text;
            }

            questionMap[q.Id] = new JsonObject
            {
                ["type"] = q.Kind switch
                {
                    SystemOneQuestionKind.Noul => "noul",
                    SystemOneQuestionKind.Choice => "choice",
                    _ => "score",
                },
                ["instructions"] = q.Instructions,
                ["criteria"] = q.Kind == SystemOneQuestionKind.Score ? new JsonArray([.. q.Criteria.Values.Select(v => (JsonNode)v)]) : criteria,
            };
        }

        var provider = new JsonObject { ["data_collection"] = "deny" };
        if (options.RequireZeroDataRetention)
        {
            provider["zdr"] = true;
            provider["allow_fallbacks"] = false;
        }

        return new JsonObject
        {
            ["model"] = options.Model,
            ["state"] = state.Json.DeepClone(),
            ["questions"] = questionMap,
            ["provider"] = provider,
        };
    }

    internal static SystemOneResult ParseResponse(string body, IReadOnlyList<SystemOneQuestion> questions, string model, TimeSpan latency)
    {
        try
        {
            var root = JsonNode.Parse(body)?.AsObject()
                ?? throw new SystemOneUnavailableException(SystemOneFailure.InvalidResponse, "Empty JEV response");
            var answers = root["answers"]?.AsObject()
                ?? throw new SystemOneUnavailableException(SystemOneFailure.InvalidResponse, "JEV response has no answers");

            var result = new Dictionary<string, SystemOneAnswer>(StringComparer.Ordinal);
            foreach (var question in questions)
            {
                if (answers[question.Id] is JsonObject answer && ParseAnswer(question, answer) is { } parsed)
                {
                    result[question.Id] = parsed;
                }
            }

            return new SystemOneResult(
                result,
                model,
                root["model"]?.GetValue<string>(),
                root["provider"]?.GetValue<string>() ?? ProviderName,
                root["usage"]?["cost"]?.GetValue<double>(),
                latency);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new SystemOneUnavailableException(SystemOneFailure.InvalidResponse, $"Unreadable JEV response: {ex.Message}", ex);
        }
    }

    private static SystemOneAnswer? ParseAnswer(SystemOneQuestion question, JsonObject answer)
    {
        var confidence = answer["confidence"]?.GetValue<double>();
        switch (question.Kind)
        {
            case SystemOneQuestionKind.Noul:
                if (answer["noul"] is not JsonValue value || !value.TryGetValue<double>(out var p) || p is < 0 or > 1)
                {
                    return null;
                }

                return new SystemOneAnswer(
                    question.Id,
                    p >= 0.5 ? "true" : "false",
                    new Dictionary<string, double> { ["true"] = p, ["false"] = 1 - p },
                    confidence);

            default:
                var choice = question.Kind == SystemOneQuestionKind.Choice
                    ? answer["choice"]?.GetValue<string>()
                    : answer["score"] is JsonValue score && score.TryGetValue<double>(out var s) ? Math.Round(s).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                if (choice is null)
                {
                    return null;
                }

                var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
                if (answer["probabilities"] is JsonObject map)
                {
                    foreach (var (name, node) in map)
                    {
                        if (node is JsonValue v && v.TryGetValue<double>(out var probability))
                        {
                            probabilities[name] = probability;
                        }
                    }
                }

                return new SystemOneAnswer(question.Id, choice, probabilities, confidence);
        }
    }

    private static SystemOneUnavailableException Failure(HttpStatusCode status, string body)
    {
        var message = TryReadError(body) ?? status.ToString();
        return (int)status switch
        {
            401 or 403 => new(SystemOneFailure.Unauthorized, $"OpenRouter rejected the key: {message}"),
            402 => new(SystemOneFailure.InsufficientCredits, $"OpenRouter credits exhausted: {message}"),
            429 => new(SystemOneFailure.RateLimited, $"OpenRouter rate limit: {message}"),
            400 or 404 or 422 => new(SystemOneFailure.InvalidResponse, $"OpenRouter refused the request ({(int)status}): {message}"),
            _ => new(SystemOneFailure.Unavailable, $"OpenRouter unavailable ({(int)status}): {message}"),
        };
    }

    private static string? TryReadError(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private string? ReadKey()
    {
        try
        {
            var key = secrets.Read(SecretNames.OpenRouterApiKey);
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
