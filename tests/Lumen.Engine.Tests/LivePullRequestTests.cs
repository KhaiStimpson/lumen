using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Xunit.Abstractions;

namespace Lumen.Engine.Tests;

/// <summary>
/// Opt-in smoke test against real GitHub and git. Set LUMEN_LIVE_PR to a pull request (e.g.
/// "owner/repo#58"); uses the gh CLI token. Read-only: never posts comments.
/// </summary>
public sealed class LivePullRequestTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LoadsAndAnalysesRealPullRequest()
    {
        if (!PullRequestKey.TryParse(Environment.GetEnvironmentVariable("LUMEN_LIVE_PR"), out var key))
        {
            return;
        }

        var pipe = $"lumen-live-{Guid.NewGuid():N}";
        var dataDir = Environment.GetEnvironmentVariable("LUMEN_LIVE_DATA_DIR") ?? Path.Combine(Path.GetTempPath(), "lumen-live");
        await using var engine = EngineHost.Build(new EngineOptions { PipeName = pipe, DataDirectory = dataDir });
        await engine.StartAsync();

        using var channel = EngineEndpoint.CreateChannel(pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var started = DateTime.UtcNow;

        using var call = client.WatchPullRequest(
            new WatchPullRequestRequest
            {
                PullRequest = new PullRequestRef { Owner = key.Repository.Owner, Name = key.Repository.Name, Number = key.Number },
            },
            cancellationToken: cts.Token);

        await foreach (var evt in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            var t = (DateTime.UtcNow - started).TotalMilliseconds;
            switch (evt.EventCase)
            {
                case PullRequestEvent.EventOneofCase.Progress:
                    output.WriteLine($"{t,7:0} ms  {evt.Progress.Message}");
                    break;
                case PullRequestEvent.EventOneofCase.Snapshot:
                    output.WriteLine($"{t,7:0} ms  snapshot: {evt.Snapshot.Title} ({evt.Snapshot.Files.Count} files, viewer {evt.Snapshot.ViewerLogin})");
                    break;
                case PullRequestEvent.EventOneofCase.ReviewPointAdded:
                    output.WriteLine($"{t,7:0} ms  point: [{evt.ReviewPointAdded.Severity}] {evt.ReviewPointAdded.Title} @ {evt.ReviewPointAdded.Anchor.Path}:{evt.ReviewPointAdded.Anchor.StartLine}");
                    break;
                case PullRequestEvent.EventOneofCase.Complete:
                    output.WriteLine($"{t,7:0} ms  complete: {evt.Complete.ReviewPointCount} points");
                    return;
                case PullRequestEvent.EventOneofCase.Failed:
                    Assert.Fail(evt.Failed.Message);
                    break;
            }
        }

        await engine.StopAsync();
    }
}
