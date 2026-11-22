using System.Net;
using System.Text.Json;
using Lumen.Domain;
using Lumen.Models.Cloudflare;

namespace Lumen.Models.Tests;

public sealed class CloudflareWorkersAiProviderTests : IDisposable
{
    private readonly FakeHandler _handler = new();
    private readonly HttpClient _http;
    private readonly MemorySecrets _secrets = new(new()
    {
        [SecretNames.CloudflareAccountId] = "acct123",
        [SecretNames.CloudflareApiToken] = "s3cr3t",
    });

    public CloudflareWorkersAiProviderTests() => _http = new HttpClient(_handler);

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    private CloudflareWorkersAiProvider Provider(CloudflareOptions? options = null, IModelUsageMeter? meter = null, Func<string, string?>? env = null) =>
        new(_http, _secrets, () => options ?? new CloudflareOptions(), meter ?? new UnlimitedUsageMeter(), env ?? (_ => null));

    [Fact]
    public async Task CompletionPostsToTheOpenAiCompatibleEndpointWithTheSchema()
    {
        _handler.Respond(HttpStatusCode.OK, """{"model":"@cf/zai-org/glm-5.3-flash","choices":[{"message":{"content":"{\"intent\":\"x\"}"}}],"usage":{"prompt_tokens":12,"completion_tokens":3}}""");

        var completion = await Provider().CompleteAsync(
            new ModelRequest { Purpose = "t", System = "sys", User = "usr", SchemaName = "intent", JsonSchema = """{"type":"object"}""", MaxOutputTokens = 50 },
            null,
            CancellationToken.None);

        Assert.Equal("{\"intent\":\"x\"}", completion.Text);
        Assert.Equal((12, 3), (completion.InputTokens!.Value, completion.OutputTokens!.Value));
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://api.cloudflare.com/client/v4/accounts/acct123/ai/v1/chat/completions", request.Uri.ToString());
        Assert.Equal("Bearer s3cr3t", request.Authorization);
        var body = JsonDocument.Parse(request.Body!).RootElement;
        Assert.Equal("@cf/zai-org/glm-5.3-flash", body.GetProperty("model").GetString());
        Assert.Equal("system", body.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("usr", body.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("object", body.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task GatewayRoutesThroughAiGateway()
    {
        Assert.Equal(
            "https://gateway.ai.cloudflare.com/v1/acct123/my-gw/workers-ai/v1/chat/completions",
            CloudflareWorkersAiProvider.Endpoint("acct123", "my-gw").ToString());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task StreamingReportsTextAsItArrives()
    {
        _handler.Respond(
            HttpStatusCode.OK,
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\ndata: [DONE]\n\n",
            "text/event-stream");
        var seen = new List<string>();

        var completion = await Provider().CompleteAsync(
            new ModelRequest { Purpose = "t", System = "s", User = "u" },
            new SyncProgress(seen.Add),
            CancellationToken.None);

        Assert.Equal("Hello", completion.Text);
        Assert.Equal(["Hel", "Hello"], seen);
        Assert.True(JsonDocument.Parse(_handler.Requests[0].Body!).RootElement.GetProperty("stream").GetBoolean());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ModelFailure.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, ModelFailure.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, ModelFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, ModelFailure.Rejected)]
    public async Task FailuresAreClassified(HttpStatusCode status, ModelFailure expected)
    {
        _handler.Respond(status, """{"errors":[{"message":"nope"}]}""");

        var ex = await Assert.ThrowsAsync<ModelException>(() =>
            Provider().CompleteAsync(new ModelRequest { Purpose = "t", System = "s", User = "u" }, null, CancellationToken.None));

        Assert.Equal(expected, ex.Failure);
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDailyCapStopsRequestsBeforeTheyAreSent()
    {
        var meter = new CappedMeter(0);

        var ex = await Assert.ThrowsAsync<ModelException>(() =>
            Provider(meter: meter).CompleteAsync(new ModelRequest { Purpose = "t", System = "s", User = "u" }, null, CancellationToken.None));

        Assert.Equal(ModelFailure.CapReached, ex.Failure);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task WithoutCredentialsNothingIsSent()
    {
        var provider = new CloudflareWorkersAiProvider(_http, new MemorySecrets(), () => new CloudflareOptions(), new UnlimitedUsageMeter(), _ => null);

        Assert.False(provider.HasCredentials);
        var ex = await Assert.ThrowsAsync<ModelException>(() =>
            provider.CompleteAsync(new ModelRequest { Purpose = "t", System = "s", User = "u" }, null, CancellationToken.None));
        Assert.Equal(ModelFailure.NotConnected, ex.Failure);
        Assert.False((await provider.CheckAsync(CancellationToken.None)).Valid);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void EnvironmentVariablesStandInWhereThereIsNoCredentialStore()
    {
        var env = new Dictionary<string, string>
        {
            [CloudflareWorkersAiProvider.AccountIdVariable] = "a",
            [CloudflareWorkersAiProvider.ApiTokenVariable] = "t",
        };
        var provider = new CloudflareWorkersAiProvider(_http, new MemorySecrets(), () => new CloudflareOptions(), new UnlimitedUsageMeter(), env.GetValueOrDefault);

        Assert.True(provider.HasCredentials);
    }

    [Fact]
    public async Task CheckUsesTheFreeTokenVerifyEndpoint()
    {
        _handler.Respond(HttpStatusCode.OK, """{"result":{"status":"active"},"success":true}""");

        var check = await Provider().CheckAsync(CancellationToken.None);

        Assert.True(check.Valid);
        Assert.Equal("https://api.cloudflare.com/client/v4/user/tokens/verify", _handler.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task CheckFallsBackToAccountOwnedTokens()
    {
        _handler
            .Respond(HttpStatusCode.Unauthorized, """{"success":false}""")
            .Respond(HttpStatusCode.OK, """{"result":{"status":"active"},"success":true}""");

        var check = await Provider().CheckAsync(CancellationToken.None);

        Assert.True(check.Valid);
        Assert.Equal("https://api.cloudflare.com/client/v4/accounts/acct123/tokens/verify", _handler.Requests[1].Uri.ToString());
    }

    [Fact]
    public async Task SlowAnswersTimeOut()
    {
        var slow = new SlowHandler();
        using var http = new HttpClient(slow);
        var provider = new CloudflareWorkersAiProvider(http, _secrets, () => new CloudflareOptions { Timeout = TimeSpan.FromMilliseconds(50) }, new UnlimitedUsageMeter(), _ => null);

        var ex = await Assert.ThrowsAsync<ModelException>(() =>
            provider.CompleteAsync(new ModelRequest { Purpose = "t", System = "s", User = "u" }, null, CancellationToken.None));

        Assert.Equal(ModelFailure.Timeout, ex.Failure);
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
