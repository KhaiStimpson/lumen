using System.Diagnostics;
using Google.Protobuf;
using Lumen.Analysis;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Engine.Mapping;
using Lumen.Engine.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Engine.Tests;

/// <summary>
/// Adds (or refreshes) the TriageReady event in a recorded fixture without going to GitHub: the snapshot's commits are
/// read from a local clone, triaged exactly as the engine does, and the event is spliced in right after the snapshot.
/// Opt-in: LUMEN_RECORD_TRIAGE=&lt;fixture directory&gt; and LUMEN_TRIAGE_REPO=&lt;local clone with the PR's commits&gt;.
/// </summary>
public sealed class FixtureTriageRecorder
{
    [Fact]
    public async Task AddsTriageToRecordedFixture()
    {
        var fixture = Environment.GetEnvironmentVariable("LUMEN_RECORD_TRIAGE");
        var repo = Environment.GetEnvironmentVariable("LUMEN_TRIAGE_REPO");
        if (string.IsNullOrEmpty(fixture) || string.IsNullOrEmpty(repo))
        {
            return;
        }

        var path = Path.Combine(fixture, "events.jsonl");
        var parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
        var events = File.ReadAllLines(path).Where(l => l.Length > 0).Select(parser.Parse<PullRequestEvent>).ToList();
        events.RemoveAll(e => e.EventCase == PullRequestEvent.EventOneofCase.TriageReady);
        var index = events.FindIndex(e => e.EventCase == PullRequestEvent.EventOneofCase.Snapshot);
        var proto = events[index].Snapshot;

        var checkout = new GitShowCheckout(repo, proto.BaseSha, proto.HeadSha, proto.MergeBaseSha);
        var key = ProtoMapper.ToKey(proto.PullRequest);
        var snapshot = new Domain.PullRequestSnapshot(
            key, proto.BaseSha, proto.HeadSha, proto.MergeBaseSha,
            new PullRequestMetadata(proto.Title, proto.Author, proto.State, proto.IsDraft, proto.BaseRef, proto.HeadRef, proto.Url, proto.Body, proto.UpdatedAt.ToDateTimeOffset()),
            ChangedFiles.FromUnifiedDiff(await checkout.GetDiffAsync(CancellationToken.None)),
            []);
        Assert.Equal(proto.Files.Select(f => f.Path).Order(StringComparer.Ordinal), snapshot.Files.Select(f => f.Path).Order(StringComparer.Ordinal));

        var runner = new TriageRunner(new NoStore(), NullLogger<TriageRunner>.Instance);
        var triage = await runner.RunAsync(snapshot, checkout, ReviewSettings.Default, CancellationToken.None);
        Assert.NotNull(triage);

        var viewed = proto.Files.Where(f => f.IsViewed).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        events.Insert(index + 1, new PullRequestEvent { TriageReady = ProtoMapper.ToProto(triage, snapshot, viewed) });

        var formatter = new JsonFormatter(JsonFormatter.Settings.Default);
        await File.WriteAllLinesAsync(path, events.Select(e => formatter.Format(e)));
    }

    private sealed class NoStore : ITriageStore
    {
        public Task<TriageResult?> FindTriageAsync(PullRequestKey key, string headSha, string version, CancellationToken cancellationToken) =>
            Task.FromResult<TriageResult?>(null);

        public Task SaveTriageAsync(PullRequestKey key, string headSha, string version, TriageResult result, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>A checkout that only reads objects from a local clone: never fetches, never touches a working tree.</summary>
    private sealed class GitShowCheckout(string repo, string baseSha, string headSha, string mergeBaseSha) : IPullRequestCheckout
    {
        public string RootPath => repo;

        public string BaseSha => baseSha;

        public string HeadSha => headSha;

        public string MergeBaseSha => mergeBaseSha;

        public async Task<string> GetDiffAsync(CancellationToken cancellationToken) =>
            await GitAsync(cancellationToken, "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-relative", "--src-prefix=a/", "--dst-prefix=b/", "-M", "-U3", mergeBaseSha, headSha, "--") ?? throw new InvalidOperationException("git diff failed");

        public Task<string?> ReadBaseFileAsync(string path, CancellationToken cancellationToken) => GitAsync(cancellationToken, "show", $"{mergeBaseSha}:{path}");

        public Task<string?> ReadHeadFileAsync(string path, CancellationToken cancellationToken) => GitAsync(cancellationToken, "show", $"{headSha}:{path}");

        private async Task<string?> GitAsync(CancellationToken cancellationToken, params string[] args)
        {
            var psi = new ProcessStartInfo("git", ["-C", repo, .. args]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(psi)!;
            var text = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? text : null;
        }
    }
}
