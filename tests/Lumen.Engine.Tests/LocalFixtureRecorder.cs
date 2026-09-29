using System.Diagnostics;
using Google.Protobuf;
using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumen.Engine.Tests;

/// <summary>
/// Records a fixture from one commit of a local clone, through the real engine, with no network: GitHub is faked and
/// the checkout is the commit's tree from <c>git archive</c>. Used for UI fixtures that need triage groups.
/// Opt-in: LUMEN_RECORD_LOCAL=&lt;output directory&gt;, LUMEN_TRIAGE_REPO=&lt;clone&gt;, LUMEN_LOCAL_COMMIT=&lt;sha&gt;,
/// optional LUMEN_LOCAL_TITLE and LUMEN_LOCAL_PR (owner/repo#n, used only as a label).
/// </summary>
public sealed class LocalFixtureRecorder
{
    [Fact]
    public async Task RecordLocalCommit()
    {
        var output = Environment.GetEnvironmentVariable("LUMEN_RECORD_LOCAL");
        var repo = Environment.GetEnvironmentVariable("LUMEN_TRIAGE_REPO");
        var commit = Environment.GetEnvironmentVariable("LUMEN_LOCAL_COMMIT");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(commit))
        {
            return;
        }

        var head = (await Git(repo, "rev-parse", commit))!.Trim();
        var parent = (await Git(repo, "rev-parse", head + "~1"))!.Trim();
        var title = Environment.GetEnvironmentVariable("LUMEN_LOCAL_TITLE") ?? (await Git(repo, "log", "-1", "--format=%s", head))!.Trim();
        Assert.True(PullRequestKey.TryParse(Environment.GetEnvironmentVariable("LUMEN_LOCAL_PR") ?? "local/fixture#1", out var key));

        var work = Path.Combine(Path.GetTempPath(), $"lumen-local-{Guid.NewGuid():N}");
        var tree = Path.Combine(work, "tree");
        Directory.CreateDirectory(tree);
        await ExtractAsync(repo, head, tree);

        var pipe = $"lumen-record-{Guid.NewGuid():N}";
        await using var engine = EngineHost.Build(
            new EngineOptions { PipeName = pipe, DataDirectory = Path.Combine(work, "data") },
            services =>
            {
                services.RemoveAll<IGitHubClient>();
                services.AddSingleton<IGitHubClient>(new LocalGitHub(key, title, parent, head));
                services.RemoveAll<IRepositoryWorkspace>();
                services.AddSingleton<IRepositoryWorkspace>(new FakeWorkspace(new ArchiveCheckout(repo, tree, parent, head)));
                services.RemoveAll<ISecretStore>();
                services.AddSingleton<ISecretStore>(new Lumen.Storage.UnavailableSecretStore());
            });
        await engine.StartAsync();

        using var channel = EngineEndpoint.CreateChannel(pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);
        var pr = new PullRequestRef { Owner = key.Repository.Owner, Name = key.Repository.Name, Number = key.Number };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var events = new List<PullRequestEvent>();
        using (var call = client.WatchPullRequest(new WatchPullRequestRequest { PullRequest = pr }, cancellationToken: cts.Token))
        {
            await foreach (var evt in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                if (evt.EventCase is not PullRequestEvent.EventOneofCase.InvestigationStatus)
                {
                    events.Add(evt);
                }

                if (evt.Complete is not null || evt.Failed is not null)
                {
                    break;
                }
            }
        }

        Assert.DoesNotContain(events, e => e.Failed is not null);
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static async Task ExtractAsync(string repo, string sha, string target)
    {
        var archive = Path.Combine(target, "..", "tree.tar");
        Assert.NotNull(await Git(repo, "archive", "--format=tar", "-o", Path.GetFullPath(archive), sha));
        var psi = new ProcessStartInfo("tar", ["-xf", Path.GetFullPath(archive), "-C", target]) { RedirectStandardError = true };
        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task<string?> Git(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git", ["-C", repo, .. args]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(psi)!;
        var text = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? text : null;
    }

    private sealed class LocalGitHub(PullRequestKey key, string title, string baseSha, string headSha) : IGitHubClient
    {
        public Task<string> GetViewerLoginAsync(CancellationToken cancellationToken) => Task.FromResult("reviewer");

        public Task<PullRequestInfo> GetPullRequestAsync(PullRequestKey k, CancellationToken cancellationToken) =>
            Task.FromResult(new PullRequestInfo(
                key,
                new PullRequestMetadata(title, "author", "open", false, "main", "refactor", $"https://github.com/{key.Repository.Owner}/{key.Repository.Name}/pull/{key.Number}", null, DateTimeOffset.UnixEpoch),
                baseSha,
                headSha));

        public Task<IReadOnlyList<ReviewThread>> GetReviewCommentsAsync(PullRequestKey k, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReviewThread>>([]);

        public Task<PostedComment> PostReviewCommentAsync(NewReviewComment comment, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<string>> GetViewedFilesAsync(PullRequestKey k, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task SetFileViewedAsync(PullRequestKey k, string path, bool viewed, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>The commit's tree on disk (for the repository index) with base/head reads and the diff from git.</summary>
    private sealed class ArchiveCheckout(string repo, string tree, string baseSha, string headSha) : IPullRequestCheckout
    {
        public string RootPath => tree;

        public string BaseSha => baseSha;

        public string HeadSha => headSha;

        public string MergeBaseSha => baseSha;

        public async Task<string> GetDiffAsync(CancellationToken cancellationToken) =>
            await Git(repo, "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-relative", "--src-prefix=a/", "--dst-prefix=b/", "-M", "-U3", baseSha, headSha, "--")
            ?? throw new InvalidOperationException("git diff failed");

        public Task<string?> ReadBaseFileAsync(string path, CancellationToken cancellationToken) => Git(repo, "show", $"{baseSha}:{path}");

        public Task<string?> ReadHeadFileAsync(string path, CancellationToken cancellationToken) => Git(repo, "show", $"{headSha}:{path}");
    }
}
