using Google.Protobuf;
using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;

namespace Lumen.Engine.Tests;

/// <summary>
/// Records a live engine session into a fixture the UI tests replay. Opt-in: set LUMEN_LIVE_PR and
/// LUMEN_RECORD_FIXTURE (output directory). Diffs of mechanical files are omitted to keep fixtures small.
/// </summary>
public sealed class FixtureRecorder
{
    [Fact]
    public async Task RecordLivePullRequest()
    {
        var output = Environment.GetEnvironmentVariable("LUMEN_RECORD_FIXTURE");
        if (string.IsNullOrEmpty(output) || !PullRequestKey.TryParse(Environment.GetEnvironmentVariable("LUMEN_LIVE_PR"), out var key))
        {
            return;
        }

        var pipe = $"lumen-record-{Guid.NewGuid():N}";
        var dataDir = Environment.GetEnvironmentVariable("LUMEN_LIVE_DATA_DIR") ?? Path.Combine(Path.GetTempPath(), "lumen-live");
        await using var engine = EngineHost.Build(new EngineOptions { PipeName = pipe, DataDirectory = dataDir });
        await engine.StartAsync();

        using var channel = EngineEndpoint.CreateChannel(pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);
        var pr = new PullRequestRef { Owner = key.Repository.Owner, Name = key.Repository.Name, Number = key.Number };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var events = new List<PullRequestEvent>();
        using (var call = client.WatchPullRequest(new WatchPullRequestRequest { PullRequest = pr }, cancellationToken: cts.Token))
        {
            await foreach (var evt in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                events.Add(evt);
                if (evt.Complete is not null || evt.Failed is not null)
                {
                    break;
                }
            }
        }

        var snapshot = events.First(e => e.Snapshot is not null).Snapshot;
        var diffs = new List<FileDiff>();
        foreach (var file in snapshot.Files.Where(f => !f.IsMechanical))
        {
            diffs.Add(await client.GetFileDiffAsync(new GetFileDiffRequest { PullRequest = pr, Path = file.Path }));
        }

        Directory.CreateDirectory(output);
        var formatter = new JsonFormatter(JsonFormatter.Settings.Default);
        await File.WriteAllLinesAsync(Path.Combine(output, "events.jsonl"), events.Select(e => formatter.Format(e)));
        await File.WriteAllLinesAsync(Path.Combine(output, "diffs.jsonl"), diffs.Select(d => formatter.Format(d)));
        await engine.StopAsync();
    }
}
