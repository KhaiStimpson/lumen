using System.Net;
using System.Text;

namespace Lumen.GitHub.Tests;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>Replays queued responses in order and records every request it sees.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeHttpMessageHandler Respond(HttpStatusCode status, string json, string? link = null)
    {
        _responses.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (link is not null)
            {
                response.Headers.Add("Link", link);
            }

            return response;
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (request.Content?.Headers.ContentType is { } contentType)
        {
            headers["Content-Type"] = contentType.ToString();
        }

        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));

        return _responses.Count > 0
            ? _responses.Dequeue()()
            : throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
    }
}

internal sealed class StaticTokenSource(string token) : Lumen.Domain.IGitHubTokenSource
{
    public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
}
