using System.Net;
using System.Text;
using Lumen.Domain;
using Lumen.Models;

namespace Lumen.Models.Tests;

/// <summary>Answers by purpose ("walkthrough/file", …); records every request.</summary>
internal sealed class ScriptedModel : IModelProvider
{
    private readonly Func<ModelRequest, string> _answer;

    public ScriptedModel(Func<ModelRequest, string> answer) => _answer = answer;

    public List<ModelRequest> Requests { get; } = [];

    public Func<ModelRequest, Exception?> FailWhen { get; set; } = _ => null;

    public TimeSpan Delay { get; set; }

    public string Name => "scripted";

    public string Model => "@cf/test/glm-test";

    public bool HasCredentials => true;

    public Task<ModelCheck> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(new ModelCheck(true, "ok"));

    public async Task<ModelCompletion> CompleteAsync(ModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (FailWhen(request) is { } failure)
        {
            throw failure;
        }

        return new ModelCompletion(_answer(request), Model, 10, 10, TimeSpan.FromMilliseconds(5));
    }
}

internal static class Files
{
    public static ChangedFile File(string path, string[] added, bool mechanical = false, int startLine = 1) => new(
        path,
        null,
        FileChangeKind.Added,
        added.Length,
        0,
        false,
        mechanical ? new MechanicalClassification(true, "Generated") : MechanicalClassification.None,
        [new DiffHunk(0, 0, startLine, added.Length, $"@@ -0,0 +{startLine},{added.Length} @@", [.. added.Select((t, i) => new DiffLine(DiffLineKind.Added, null, startLine + i, t))])]);
}

internal sealed record Recorded(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<Recorded> Requests { get; } = [];

    public FakeHandler Respond(HttpStatusCode status, string body, string contentType = "application/json")
    {
        _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new Recorded(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
        return _responses.Count > 0 ? _responses.Dequeue()() : throw new HttpRequestException("No more responses");
    }
}

internal sealed class MemorySecrets(Dictionary<string, string>? values = null) : ISecretStore
{
    private readonly Dictionary<string, string> _values = values ?? [];

    public bool IsAvailable => true;

    public string? Read(string name) => _values.GetValueOrDefault(name);

    public void Write(string name, string secret) => _values[name] = secret;

    public bool Delete(string name) => _values.Remove(name);
}

internal sealed class CappedMeter(int cap) : IModelUsageMeter
{
    public int UsedToday { get; private set; }

    public bool TryConsume()
    {
        if (UsedToday >= cap)
        {
            return false;
        }

        UsedToday++;
        return true;
    }
}
