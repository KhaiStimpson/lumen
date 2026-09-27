using System.Net;
using System.Text.Json.Nodes;
using Lumen.Domain;

namespace Lumen.Jev.Tests;

public sealed class OpenRouterSystemOneEvaluatorTests
{
    /// <summary>Shape of the documented Decisions API response (openapi.yaml, /api/alpha/decisions example).</summary>
    private const string SpecResponse = """
        {
          "id": "gen-dec-1789738314-X5e5eKGQdvR9rblyX250",
          "model": "typesafe/jev-1.13-20260917",
          "provider": "TypeSafe",
          "answers": {
            "c0_mechanical": { "type": "noul", "noul": 0.02 },
            "c0_human_judgement": { "type": "noul", "noul": 0.91 },
            "c0_unexpected": { "type": "noul", "noul": 0.5 }
          },
          "usage": { "cost": 0.000019992, "input_tokens": 476, "output_tokens": 70 }
        }
        """;

    private static readonly SystemOneState State = new(new JsonObject { ["candidates"] = new JsonArray() });

    private static IReadOnlyList<SystemOneQuestion> Questions => AttentionQuestions.For(["c0"]);

    private static (OpenRouterSystemOneEvaluator Evaluator, StubHandler Handler) Create(
        Func<HttpRequestMessage, string, HttpResponseMessage> respond,
        string? key = "sk-or-test",
        OpenRouterOptions? options = null)
    {
        var handler = new StubHandler(respond);
        return (new OpenRouterSystemOneEvaluator(new HttpClient(handler), new MemorySecrets(key), options ?? new OpenRouterOptions()), handler);
    }

    [Fact]
    public async Task SendsOneBatchedDecisionsRequestWithPrivacyRouting()
    {
        var (evaluator, handler) = Create((_, _) => StubHandler.Json(HttpStatusCode.OK, SpecResponse));

        await evaluator.EvaluateAsync(State, Questions, CancellationToken.None);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://openrouter.ai/api/alpha/decisions", request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-or-test", request.Headers.Authorization.Parameter);

        var json = JsonNode.Parse(body)!;
        Assert.Equal("typesafe/jev-1.13", json["model"]!.GetValue<string>());
        Assert.Equal("deny", json["provider"]!["data_collection"]!.GetValue<string>());
        Assert.True(json["provider"]!["zdr"]!.GetValue<bool>());
        Assert.False(json["provider"]!["allow_fallbacks"]!.GetValue<bool>());
        Assert.NotNull(json["state"]!["candidates"]);

        var questions = json["questions"]!.AsObject();
        Assert.Equal(AttentionQuestions.Ids.Count, questions.Count);
        var mechanical = questions["c0_mechanical"]!;
        Assert.Equal("noul", mechanical["type"]!.GetValue<string>());
        Assert.NotNull(mechanical["criteria"]!["true"]);
        Assert.NotNull(mechanical["criteria"]!["false"]);
        Assert.Contains("c0", mechanical["instructions"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParsesProbabilitiesModelAndCost()
    {
        var (evaluator, _) = Create((_, _) => StubHandler.Json(HttpStatusCode.OK, SpecResponse));

        var result = await evaluator.EvaluateAsync(State, Questions, CancellationToken.None);

        Assert.Equal(2, result.Answers.Count);
        Assert.Equal("false", result.Answers["c0_mechanical"].Choice);
        Assert.Equal(0.02, result.Answers["c0_mechanical"].PTrue, 3);
        Assert.Equal(0.91, result.Answers["c0_human_judgement"].PTrue, 3);
        Assert.Equal("typesafe/jev-1.13", result.Model);
        Assert.Equal("typesafe/jev-1.13-20260917", result.ResolvedModel);
        Assert.Equal("TypeSafe", result.Provider);
        Assert.Equal(0.000019992, result.Cost!.Value, 9);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SystemOneFailure.Unauthorized, true)]
    [InlineData(HttpStatusCode.PaymentRequired, SystemOneFailure.InsufficientCredits, true)]
    [InlineData(HttpStatusCode.TooManyRequests, SystemOneFailure.RateLimited, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SystemOneFailure.Unavailable, false)]
    [InlineData(HttpStatusCode.BadRequest, SystemOneFailure.InvalidResponse, false)]
    public async Task MapsErrorsToFailures(HttpStatusCode status, SystemOneFailure failure, bool persistent)
    {
        var (evaluator, _) = Create((_, _) => StubHandler.Json(status, """{"error":{"code":0,"message":"nope"}}"""));

        var ex = await Assert.ThrowsAsync<SystemOneUnavailableException>(() => evaluator.EvaluateAsync(State, Questions, CancellationToken.None));

        Assert.Equal(failure, ex.Failure);
        Assert.Equal(persistent, ex.IsPersistent);
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsUnreadableResponses()
    {
        var (evaluator, _) = Create((_, _) => StubHandler.Json(HttpStatusCode.OK, "<html>gateway</html>"));

        var ex = await Assert.ThrowsAsync<SystemOneUnavailableException>(() => evaluator.EvaluateAsync(State, Questions, CancellationToken.None));
        Assert.Equal(SystemOneFailure.InvalidResponse, ex.Failure);
    }

    [Fact]
    public async Task NeverCallsOutWithoutAKey()
    {
        var (evaluator, handler) = Create((_, _) => StubHandler.Json(HttpStatusCode.OK, SpecResponse), key: null);

        Assert.False(evaluator.IsConfigured);
        var ex = await Assert.ThrowsAsync<SystemOneUnavailableException>(() => evaluator.EvaluateAsync(State, Questions, CancellationToken.None));
        Assert.Equal(SystemOneFailure.NotConfigured, ex.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TimesOutAsAFailureNotAHang()
    {
        var handler = new SlowHandler();
        var evaluator = new OpenRouterSystemOneEvaluator(new HttpClient(handler), new MemorySecrets("k"), new OpenRouterOptions { RequestTimeout = TimeSpan.FromMilliseconds(50) });

        var ex = await Assert.ThrowsAsync<SystemOneUnavailableException>(() => evaluator.EvaluateAsync(State, Questions, CancellationToken.None));
        Assert.Equal(SystemOneFailure.Timeout, ex.Failure);
    }

    [Fact]
    public async Task ChecksTheKeyWithoutSpending()
    {
        var (evaluator, handler) = Create((_, _) => StubHandler.Json(HttpStatusCode.OK, """{"data":{"label":"lumen","limit_remaining":4.5}}"""));

        var status = await evaluator.CheckKeyAsync(CancellationToken.None);

        Assert.True(status.Valid);
        Assert.Contains("4.5", status.Detail, StringComparison.Ordinal);
        var (request, _) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://openrouter.ai/api/v1/key", request.RequestUri!.ToString());
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
