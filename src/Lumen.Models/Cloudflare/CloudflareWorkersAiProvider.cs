using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lumen.Domain;

namespace Lumen.Models.Cloudflare;

public sealed record CloudflareOptions
{
    /// <summary>Workers AI model id. GLM 5.3 Flash is <c>@cf/zai-org/glm-5.3-flash</c>.</summary>
    public string Model { get; init; } = "@cf/zai-org/glm-5.3-flash";

    /// <summary>AI Gateway id; empty calls Workers AI directly.</summary>
    public string Gateway { get; init; } = "";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// GLM on Cloudflare Workers AI through its OpenAI-compatible chat completions endpoint, directly or through AI
/// Gateway. The account id and API token come from the platform credential store (or, for development on a machine
/// without one, <c>LUMEN_CLOUDFLARE_ACCOUNT_ID</c> / <c>LUMEN_CLOUDFLARE_API_TOKEN</c>). The token is never logged.
/// </summary>
public sealed class CloudflareWorkersAiProvider : IModelProvider
{
    public const string AccountIdVariable = "LUMEN_CLOUDFLARE_ACCOUNT_ID";
    public const string ApiTokenVariable = "LUMEN_CLOUDFLARE_API_TOKEN";

    private static readonly Uri ApiBase = new("https://api.cloudflare.com/client/v4/");
    private static readonly Uri GatewayBase = new("https://gateway.ai.cloudflare.com/v1/");

    private readonly HttpClient _http;
    private readonly ISecretStore _secrets;
    private readonly Func<CloudflareOptions> _options;
    private readonly IModelUsageMeter _usage;
    private readonly Func<string, string?> _environment;

    public CloudflareWorkersAiProvider(
        HttpClient http,
        ISecretStore secrets,
        Func<CloudflareOptions> options,
        IModelUsageMeter usage,
        Func<string, string?>? environment = null)
    {
        _http = http;
        _secrets = secrets;
        _options = options;
        _usage = usage;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public string Name => "cloudflare-workers-ai";

    public string Model => _options().Model;

    public bool HasCredentials => Credentials() is not null;

    /// <summary>Free: Cloudflare's token verify endpoint spends no Workers AI usage.</summary>
    public async Task<ModelCheck> CheckAsync(CancellationToken cancellationToken)
    {
        if (Credentials() is not { } credentials)
        {
            return new ModelCheck(false, "No Cloudflare account id and API token");
        }

        try
        {
            // User tokens verify here; account-owned tokens under the account.
            foreach (var path in new[] { "user/tokens/verify", $"accounts/{Uri.EscapeDataString(credentials.AccountId)}/tokens/verify" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, path));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                    var status = body?["result"]?["status"]?.GetValue<string>() ?? "active";
                    return status == "active"
                        ? new ModelCheck(true, $"Token active · {Model}")
                        : new ModelCheck(false, $"Token is {status}");
                }
            }

            return new ModelCheck(false, "Cloudflare rejected the API token");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return new ModelCheck(false, "Couldn't reach Cloudflare");
        }
    }

    public async Task<ModelCompletion> CompleteAsync(ModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var credentials = Credentials() ?? throw new ModelException(ModelFailure.NotConnected, "Connect Cloudflare Workers AI in Connections first.");
        if (!_usage.TryConsume())
        {
            throw new ModelException(ModelFailure.CapReached, "Today's fast-model request cap is reached (Settings → Cloud AI limits).");
        }

        var options = _options();
        var stream = progress is not null;
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.System },
                new JsonObject { ["role"] = "user", ["content"] = request.User }),
            ["max_tokens"] = request.MaxOutputTokens,
            ["temperature"] = request.Temperature,
            ["stream"] = stream,
        };
        if (request.JsonSchema is not null)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.SchemaName ?? "result",
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(request.JsonSchema),
                },
            };
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint(credentials.AccountId, options.Gateway))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw await FailureAsync(response, timeout.Token).ConfigureAwait(false);
            }

            return stream
                ? await ReadStreamAsync(response, options.Model, progress!, stopwatch, timeout.Token).ConfigureAwait(false)
                : await ReadWholeAsync(response, options.Model, stopwatch, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelException(ModelFailure.Timeout, $"The fast model didn't answer within {options.Timeout.TotalSeconds:0} s.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelException(ModelFailure.Unavailable, "Couldn't reach Cloudflare.", ex);
        }
    }

    internal static Uri Endpoint(string accountId, string gateway) =>
        gateway.Length > 0
            ? new Uri(GatewayBase, $"{Uri.EscapeDataString(accountId)}/{Uri.EscapeDataString(gateway)}/workers-ai/v1/chat/completions")
            : new Uri(ApiBase, $"accounts/{Uri.EscapeDataString(accountId)}/ai/v1/chat/completions");

    private static async Task<ModelCompletion> ReadWholeAsync(HttpResponseMessage response, string model, Stopwatch stopwatch, CancellationToken ct)
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var text = Text(json?["choices"]?[0]?["message"]?["content"]);
        return new ModelCompletion(
            text,
            Text(json?["model"]) is { Length: > 0 } served ? served : model,
            Int(json?["usage"]?["prompt_tokens"]),
            Int(json?["usage"]?["completion_tokens"]),
            stopwatch.Elapsed);
    }

    /// <summary>Server-sent events: <c>data: {chunk}</c> lines, ending with <c>data: [DONE]</c>.</summary>
    private static async Task<ModelCompletion> ReadStreamAsync(
        HttpResponseMessage response,
        string model,
        IProgress<string> partial,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var text = new StringBuilder();
        int? input = null, output = null;
        var served = model;
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    break;
                }

                JsonNode? chunk;
                try
                {
                    chunk = JsonNode.Parse(data);
                }
                catch (JsonException)
                {
                    continue;
                }

                served = Text(chunk?["model"]) is { Length: > 0 } m ? m : served;
                input = Int(chunk?["usage"]?["prompt_tokens"]) ?? input;
                output = Int(chunk?["usage"]?["completion_tokens"]) ?? output;
                var delta = Text(chunk?["choices"]?[0]?["delta"]?["content"]);
                if (delta.Length > 0)
                {
                    text.Append(delta);
                    partial.Report(text.ToString());
                }
            }
        }

        return new ModelCompletion(text.ToString(), served, input, output, stopwatch.Elapsed);
    }

    private static async Task<ModelException> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? detail = null;
        try
        {
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            detail = Text(json?["errors"]?[0]?["message"]) is { Length: > 0 } cf ? cf : Text(json?["error"]?["message"]);
        }
        catch (JsonException)
        {
        }

        var status = (int)response.StatusCode;
        var suffix = string.IsNullOrWhiteSpace(detail) ? "" : $": {detail}";
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ModelException(ModelFailure.Unauthorized, $"Cloudflare rejected the API token ({status}){suffix}"),
            HttpStatusCode.TooManyRequests => new ModelException(ModelFailure.RateLimited, $"Cloudflare is rate limiting requests{suffix}"),
            >= HttpStatusCode.InternalServerError => new ModelException(ModelFailure.Unavailable, string.Create(CultureInfo.InvariantCulture, $"Cloudflare returned {status}{suffix}")),
            _ => new ModelException(ModelFailure.Rejected, string.Create(CultureInfo.InvariantCulture, $"Cloudflare refused the request ({status}){suffix}")),
        };
    }

    private (string AccountId, string Token)? Credentials()
    {
        string? account = null, token = null;
        try
        {
            if (_secrets.IsAvailable)
            {
                account = _secrets.Read(SecretNames.CloudflareAccountId);
                token = _secrets.Read(SecretNames.CloudflareApiToken);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
        }

        account = string.IsNullOrWhiteSpace(account) ? _environment(AccountIdVariable) : account;
        token = string.IsNullOrWhiteSpace(token) ? _environment(ApiTokenVariable) : token;
        return string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(token) ? null : (account.Trim(), token.Trim());
    }

    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
