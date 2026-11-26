using Google.Protobuf;
using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumen.Engine.Tests;

/// <summary>
/// Records the Razor Pages fixture (Phase 0) for the app, through the real engine, with no network:
/// <list type="bullet">
/// <item><c>razor-pages-app-pr</c>: the change as pull request contoso/shop#42.</item>
/// <item><c>razor-pages-app-local</c>: the same branch reviewed locally after a follow-up commit, with one slice
/// marked before it, and <c>since.json</c> from since-my-last-look.</item>
/// <item><c>inbox/local-branches.json</c>: the branch as the inbox lists it, with its progress.</item>
/// </list>
/// The clone's temporary path is replaced with <c>C:/work/contoso-shop</c> in everything written.
/// Opt-in: LUMEN_RECORD_RAZOR=&lt;tests/fixtures directory&gt;.
/// </summary>
public sealed class RazorPagesFixtureRecorder
{
    private const string NeutralClone = "C:/work/contoso-shop";

    [Fact]
    public async Task RecordRazorPagesFixtures()
    {
        var fixtures = Environment.GetEnvironmentVariable("LUMEN_RECORD_RAZOR");
        if (string.IsNullOrEmpty(fixtures))
        {
            return;
        }

        using var created = FixtureRepository.Create();
        var parent = Path.Combine(Path.GetTempPath(), $"lumen-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        var clone = Path.Combine(parent, "contoso-shop");
        Directory.Move(created.Root, clone);
        try
        {
            Assert.True(PullRequestKey.TryParse("contoso/shop#42", out var key));
            await LocalFixtureRecorder.RecordAsync(Path.Combine(fixtures, "razor-pages-app-pr"), clone, created.HeadSha, key,
                "Invoices export, customer notes, bulk order edit and the dashboard", FixtureRepository.HeadBranch);
            await RecordLocalAsync(fixtures, clone);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(parent, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(parent, recursive: true);
        }
    }

    private static async Task RecordLocalAsync(string fixtures, string clone)
    {
        var data = Path.Combine(Path.GetTempPath(), $"lumen-rec-data-{Guid.NewGuid():N}");
        var pipe = $"lumen-record-{Guid.NewGuid():N}";
        await using var engine = EngineHost.Build(
            new EngineOptions { PipeName = pipe, DataDirectory = data },
            services =>
            {
                services.RemoveAll<IGitHubClient>();
                services.AddSingleton<IGitHubClient>(new FakeGitHub());
                services.RemoveAll<Lumen.Storage.ISecretStore>();
                services.AddSingleton<Lumen.Storage.ISecretStore>(new Lumen.Storage.UnavailableSecretStore());
            });
        await engine.StartAsync();
        using var channel = EngineEndpoint.CreateChannel(pipe);
        var client = new ReviewEngine.ReviewEngineClient(channel);
        var branch = new LocalBranchRef { ClonePath = clone, BaseRef = "main", HeadRef = FixtureRepository.HeadBranch };

        // First look: mark the Orders / Edit slice reviewed.
        var first = await WatchAsync(client, branch, refresh: false);
        var pr = first.First(e => e.Snapshot is not null).Snapshot.PullRequest;
        var slices = first.Single(e => e.SlicesReady is not null).SlicesReady;
        var edit = slices.Slices.Single(s => s.SeedPath == "src/Shop.Web/Pages/Orders/Edit.cshtml");
        await client.SetMarkedAsync(new SetMarkedRequest { PullRequest = pr, Marked = true, Units = { slices.Units.Where(u => edit.Files.Any(f => f.Path == u.Path)).Select(u => u.Unit) } });

        // The agent's follow-up: puts back the null guard and drops IgnoreQueryFilters (two flags fixed), and touches
        // the reviewed slice with a catch that swallows a concurrency error (one new flag; the slice changed after review).
        Edit(clone, "src/Shop.Web/Services/OrderService.cs", text => text.Replace(
            "    public async Task SaveAsync(Order order, CancellationToken cancellationToken = default)\n    {\n",
            "    public async Task SaveAsync(Order order, CancellationToken cancellationToken = default)\n    {\n        ArgumentNullException.ThrowIfNull(order);\n\n",
            StringComparison.Ordinal));
        Edit(clone, "src/Shop.Web/Services/DashboardService.cs", text => text.Replace(".IgnoreQueryFilters()", "", StringComparison.Ordinal));
        Edit(clone, "src/Shop.Web/Pages/Orders/Edit.cshtml.cs", text => text.Replace(
            "            return AjaxResults.Fail(\"This order was changed by someone else. Reload and try again.\", StatusCodes.Status409Conflict);\n        }\n",
            "            return AjaxResults.Fail(\"This order was changed by someone else. Reload and try again.\", StatusCodes.Status409Conflict);\n        }\n        catch (InvalidOperationException)\n        {\n            return AjaxResults.Fail(\"The order could not be saved.\", StatusCodes.Status400BadRequest);\n        }\n",
            StringComparison.Ordinal));
        await LocalFixtureRecorder.Git(clone, "add", "-A");
        await LocalFixtureRecorder.Git(clone, "commit", "-q", "-m", "Address review: restore the order guard, drop IgnoreQueryFilters");

        var second = await WatchAsync(client, branch, refresh: true);
        var snapshot = second.First(e => e.Snapshot is not null).Snapshot;
        var diffs = new List<FileDiff>();
        foreach (var file in snapshot.Files.Where(f => !f.IsMechanical))
        {
            diffs.Add(await client.GetFileDiffAsync(new GetFileDiffRequest { PullRequest = pr, Path = file.Path }));
        }

        var since = await client.GetSinceLastLookAsync(new GetSinceLastLookRequest { PullRequest = pr });
        var listed = await client.ListLocalBranchesAsync(new ListLocalBranchesRequest());

        var formatter = new JsonFormatter(JsonFormatter.Settings.Default.WithIndentation());
        var line = new JsonFormatter(JsonFormatter.Settings.Default);
        // The engine normalises the path (forward slashes, a lower-case drive), so match without case.
        string Neutral(string json) => json
            .Replace(clone.Replace("\\", "\\\\", StringComparison.Ordinal), NeutralClone, StringComparison.OrdinalIgnoreCase)
            .Replace(clone.Replace('\\', '/'), NeutralClone, StringComparison.OrdinalIgnoreCase)
            .Replace(clone, NeutralClone, StringComparison.OrdinalIgnoreCase);

        var output = Path.Combine(fixtures, "razor-pages-app-local");
        Directory.CreateDirectory(output);
        await File.WriteAllLinesAsync(Path.Combine(output, "events.jsonl"), second.Select(e => Neutral(line.Format(e))));
        await File.WriteAllLinesAsync(Path.Combine(output, "diffs.jsonl"), diffs.Select(d => Neutral(line.Format(d))));
        await File.WriteAllTextAsync(Path.Combine(output, "since.json"), Neutral(formatter.Format(since)) + "\n");

        // The inbox's local-branches list: this branch first, recorded; the other entry stays an unopened example.
        var inboxPath = Path.Combine(fixtures, "inbox", "local-branches.json");
        var inbox = JsonParser.Default.Parse<LocalBranches>(await File.ReadAllTextAsync(inboxPath));
        var recorded = listed.Items.Single();
        recorded.LastOpenedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 11, 27, 8, 40, 0, TimeSpan.Zero));
        var others = inbox.Items.Where(i => !i.Ref.ClonePath.EndsWith("contoso-shop", StringComparison.Ordinal)).ToList();
        inbox.Items.Clear();
        inbox.Items.Add(recorded);
        inbox.Items.AddRange(others);
        await File.WriteAllTextAsync(inboxPath, Neutral(formatter.Format(inbox)) + "\n");

        await engine.StopAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void Edit(string clone, string path, Func<string, string> change)
    {
        var full = Path.Combine(clone, path.Replace('/', Path.DirectorySeparatorChar));
        var before = File.ReadAllText(full);
        var after = change(before);
        Assert.NotEqual(before, after);
        File.WriteAllText(full, after);
    }

    private static async Task<List<PullRequestEvent>> WatchAsync(ReviewEngine.ReviewEngineClient client, LocalBranchRef branch, bool refresh)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var call = client.WatchPullRequest(new WatchPullRequestRequest { Local = branch, Refresh = refresh }, cancellationToken: cts.Token);
        var events = new List<PullRequestEvent>();
        await foreach (var evt in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            events.Add(evt);
            if (evt.Complete is not null || evt.Failed is not null)
            {
                break;
            }
        }

        Assert.DoesNotContain(events, e => e.Failed is not null);
        return events;
    }
}
