using Google.Protobuf;
using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;

namespace Lumen.Engine.Tests;

/// <summary>
/// Records a live engine session into a fixture the UI tests replay. Opt-in: set LUMEN_LIVE_PR and
/// LUMEN_RECORD_FIXTURE (output directory). Diffs of mechanical files are omitted to keep fixtures small. Also records
/// the review client's data (plan v2): threads, timeline, checks and the walkthrough (v2-events.jsonl), details
/// (details.json), the files each commit touched (commits.json) and the inbox (../inbox/inbox.json). The walkthrough is
/// recorded only if the fast model is connected and allowed to see code.
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
        var v2 = new Dictionary<PullRequestEvent.EventOneofCase, PullRequestEvent>();
        using (var call = client.WatchPullRequest(new WatchPullRequestRequest { PullRequest = pr }, cancellationToken: cts.Token))
        {
            var complete = false;
            await foreach (var evt in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                switch (evt.EventCase)
                {
                    case PullRequestEvent.EventOneofCase.ThreadsUpdated or PullRequestEvent.EventOneofCase.TimelineUpdated
                        or PullRequestEvent.EventOneofCase.ChecksUpdated or PullRequestEvent.EventOneofCase.PendingReviewUpdated
                        or PullRequestEvent.EventOneofCase.WalkthroughUpdated:
                        // The fixture replays the latest state of each after the recording.
                        v2[evt.EventCase] = evt;
                        break;
                    default:
                        events.Add(evt);
                        break;
                }

                complete |= evt.Complete is not null || evt.Failed is not null;
                var walkthroughSettled = v2.TryGetValue(PullRequestEvent.EventOneofCase.WalkthroughUpdated, out var w)
                    && w.WalkthroughUpdated.Walkthrough.Status != Contracts.WalkthroughStatus.Running;
                if (complete && walkthroughSettled && v2.ContainsKey(PullRequestEvent.EventOneofCase.ChecksUpdated))
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

        var details = await client.GetPullRequestDetailsAsync(new GetPullRequestDetailsRequest { PullRequest = pr });
        var commits = new Dictionary<string, List<string>>();
        var previous = snapshot.MergeBaseSha;
        foreach (var commit in details.Commits)
        {
            var range = await client.GetDiffRangeAsync(new GetDiffRangeRequest
            {
                PullRequest = pr,
                Range = new CommitRange { FromSha = previous, ToSha = commit.Sha },
            });
            commits[commit.Sha] = [.. range.Files.Select(f => f.Path)];
            previous = commit.Sha;
        }

        var inbox = await client.ListInboxAsync(new ListInboxRequest { Refresh = true });

        Directory.CreateDirectory(output);
        var formatter = new JsonFormatter(JsonFormatter.Settings.Default);
        await File.WriteAllLinesAsync(Path.Combine(output, "events.jsonl"), events.Select(e => formatter.Format(e)));
        await File.WriteAllLinesAsync(Path.Combine(output, "diffs.jsonl"), diffs.Select(d => formatter.Format(d)));
        await File.WriteAllLinesAsync(Path.Combine(output, "v2-events.jsonl"), v2.Values.Select(e => formatter.Format(e)));
        await File.WriteAllTextAsync(Path.Combine(output, "details.json"), formatter.Format(details));
        await File.WriteAllTextAsync(Path.Combine(output, "commits.json"), System.Text.Json.JsonSerializer.Serialize(commits));
        var inboxDirectory = Path.Combine(output, "..", "inbox");
        Directory.CreateDirectory(inboxDirectory);
        await File.WriteAllTextAsync(Path.Combine(inboxDirectory, "inbox.json"), formatter.Format(inbox));
        await engine.StopAsync();
    }
}
