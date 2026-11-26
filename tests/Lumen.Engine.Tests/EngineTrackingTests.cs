using Grpc.Core;
using Lumen.Contracts;
using Lumen.Domain;
using Lumen.Engine.Overhaul;
using Lumen.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumen.Engine.Tests;

/// <summary>
/// Phase 6 end to end on the fixture as a local branch: reviewed marks keyed on fingerprints, a push clearing only what
/// it changed, progress, since-my-last-look against each side's merge-base, the rewritten-history fallback and
/// Fixed / New flags.
/// </summary>
public sealed class EngineTrackingTests : IAsyncLifetime
{
    private const string EditPage = "src/Shop.Web/Pages/Orders/Edit.cshtml";
    private const string OrderService = "src/Shop.Web/Services/OrderService.cs";
    private const string Dashboard = "src/Shop.Web/Services/DashboardService.cs";

    private readonly string _pipe = $"lumen-test-{Guid.NewGuid():N}";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"lumen-test-{Guid.NewGuid():N}");
    private readonly FakeGitHub _gitHub = new();
    private readonly FixtureRepository _repo = FixtureRepository.Create();
    private Microsoft.AspNetCore.Builder.WebApplication? _engine;
    private Grpc.Net.Client.GrpcChannel? _channel;

    private ReviewEngine.ReviewEngineClient Client => new(_channel!);

    private LocalBranchRef Branch => new() { ClonePath = _repo.Root, BaseRef = "main", HeadRef = FixtureRepository.HeadBranch };

    public async Task InitializeAsync()
    {
        _engine = EngineHost.Build(
            new EngineOptions { PipeName = _pipe, DataDirectory = _dataDir },
            services =>
            {
                services.RemoveAll<IGitHubClient>();
                services.AddSingleton<IGitHubClient>(_gitHub);
                services.RemoveAll<Lumen.Storage.ISecretStore>();
                services.AddSingleton<Lumen.Storage.ISecretStore>(new Lumen.Storage.UnavailableSecretStore());
            });
        await _engine.StartAsync();
        _channel = EngineEndpoint.CreateChannel(_pipe);
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        if (_engine is not null)
        {
            await _engine.StopAsync();
            await _engine.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _repo.Dispose();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task MarkingASliceStoresFingerprintsAndAPushThatChangesOneFileClearsOnlyThatFile()
    {
        var first = await WatchAsync(refresh: false);
        var slices = first.Single(e => e.SlicesReady is not null).SlicesReady;
        var pr = first.First(e => e.Snapshot is not null).Snapshot.PullRequest;
        var edit = slices.Slices.Single(s => s.SeedPath == EditPage);
        var units = slices.Units.Where(u => edit.Files.Any(f => f.Path == u.Path)).ToList();

        var marked = await Client.SetMarkedAsync(new SetMarkedRequest { PullRequest = pr, Marked = true, Units = { units.Select(u => u.Unit) } });

        Assert.Equal(units.Select(u => (u.Unit, u.Fingerprint)).Order(), marked.Marks.Select(m => (m.Unit, m.Fingerprint)).Order());
        Assert.Empty(marked.ViewedPaths);
        Assert.Empty(_gitHub.Viewed);
        Assert.Equal(1, (await LocalProgressAsync()).SlicesReviewed);

        // A push changes one file of the slice: its fingerprint moves on, every other mark still matches.
        var changed = edit.Files.First(f => f.Path.EndsWith(".cs", StringComparison.Ordinal)).Path;
        Commit(changed, text => text + "\n// Reviewed note: totals are recalculated on save.\n");
        var second = await WatchAsync(refresh: true);
        var now = second.Single(e => e.SlicesReady is not null).SlicesReady.Units.ToDictionary(u => u.Unit, u => u.Fingerprint);
        var stored = second.Last(e => e.MarksUpdated is not null).MarksUpdated.Marks;

        Assert.All(stored.Where(m => units.Single(u => u.Unit == m.Unit).Path != changed), m => Assert.Equal(now[m.Unit], m.Fingerprint));
        Assert.All(stored.Where(m => units.Single(u => u.Unit == m.Unit).Path == changed), m => Assert.NotEqual(now[m.Unit], m.Fingerprint));
        Assert.Equal(0, (await LocalProgressAsync()).SlicesReviewed);

        // Clearing a mark removes it.
        var cleared = await Client.SetMarkedAsync(new SetMarkedRequest { PullRequest = pr, Marked = false, Units = { units[0].Unit } });
        Assert.DoesNotContain(cleared.Marks, m => m.Unit == units[0].Unit);
    }

    [Fact]
    public async Task MarkingSomethingOutsideTheChangeIsRefused()
    {
        var events = await WatchAsync(refresh: false);
        var pr = events.First(e => e.Snapshot is not null).Snapshot.PullRequest;

        var error = await Assert.ThrowsAsync<RpcException>(() => Client.SetMarkedAsync(new SetMarkedRequest { PullRequest = pr, Marked = true, Units = { "src/Nowhere.cs" } }).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task SinceMyLastLookListsOnlyTheFilesThatChangedWithNewAndFixedFlags()
    {
        var first = await WatchAsync(refresh: false);
        var pr = first.First(e => e.Snapshot is not null).Snapshot.PullRequest;
        var before = first.Single(e => e.FlagsReady is not null).FlagsReady.Flags;
        var none = await Client.GetSinceLastLookAsync(new GetSinceLastLookRequest { PullRequest = pr });
        Assert.Equal("You haven't looked at this change before.", none.Note);

        // One commit adds a flag in OrderService and fixes the IgnoreQueryFilters flag in DashboardService.
        Commit(OrderService, text => text.Replace("namespace Shop.Web.Services;", "namespace Shop.Web.Services;\n\n#pragma warning disable CS0168", StringComparison.Ordinal));
        Commit(Dashboard, text => text.Replace(".IgnoreQueryFilters()", "", StringComparison.Ordinal));
        var second = await WatchAsync(refresh: true);
        var after = second.Single(e => e.FlagsReady is not null).FlagsReady.Flags;

        var since = await Client.GetSinceLastLookAsync(new GetSinceLastLookRequest { PullRequest = pr });

        Assert.False(since.HistoryRewritten);
        Assert.Equal([Dashboard, OrderService], since.Files.Select(f => f.Path).Order());
        var added = Assert.Single(since.NewFlagIds);
        Assert.Equal(Contracts.FlagKind.WarningsSilenced, after.Single(f => f.Id == added).Kind);
        var fixedFlag = Assert.Single(since.FixedFlags);
        Assert.Equal(Contracts.FlagKind.QuerySafety, fixedFlag.Kind);
        Assert.Contains(before, f => f.Id == fixedFlag.Id);
    }

    [Fact]
    public async Task ARebaseOntoANewerBaseDoesNotFloodSinceMyLastLook()
    {
        var first = await WatchAsync(refresh: false);
        var pr = first.First(e => e.Snapshot is not null).Snapshot.PullRequest;

        // main moves on with someone else's change; the branch is rebased onto it without changing its own patch.
        _repo.Git("checkout", "-q", "main");
        _repo.Write("docs/release-notes.md", "# Release notes\n\n- Faster checkout.\n");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-q", "-m", "Release notes");
        _repo.Git("checkout", "-q", FixtureRepository.HeadBranch);
        _repo.Git("rebase", "-q", "main");
        await WatchAsync(refresh: true);

        var since = await Client.GetSinceLastLookAsync(new GetSinceLastLookRequest { PullRequest = pr });

        Assert.NotEqual(since.OldHeadSha, since.NewHeadSha);
        Assert.False(since.HistoryRewritten);
        Assert.Empty(since.Files);
    }

    [Fact]
    public async Task WhenTheOldHeadIsGoneItComparesChangedLinesAndSaysHistoryWasRewritten()
    {
        var first = await WatchAsync(refresh: false);
        var pr = first.First(e => e.Snapshot is not null).Snapshot.PullRequest;

        // Amend the head commit and drop the old one from the clone entirely, as a force-push would.
        _repo.Write(OrderService, File.ReadAllText(_repo.PathOf(OrderService)) + "\n// Amended.\n");
        _repo.Git("add", "-A");
        _repo.Git("commit", "-q", "--amend", "--no-edit");
        _repo.Git("reflog", "expire", "--expire=now", "--all");
        _repo.Git("gc", "-q", "--prune=now");
        await WatchAsync(refresh: true);

        var since = await Client.GetSinceLastLookAsync(new GetSinceLastLookRequest { PullRequest = pr });

        Assert.True(since.HistoryRewritten);
        Assert.Contains("history was rewritten", since.Note, StringComparison.Ordinal);
        Assert.Equal([OrderService], since.Files.Select(f => f.Path));
    }

    // ---------------------------------------------------------------- Viewed sync

    private static readonly string[] Paths = ["Shared.cs", "A.cs", "B.cs"];

    [Fact]
    public void AFileIsViewedOnlyWhenEverySliceHoldingItIsReviewedAtItsCurrentFingerprint()
    {
        static Domain.Slice Slice(string id, params string[] paths) =>
            new(id, id, Domain.SliceKind.Feature, paths[0], [.. paths.Select((p, i) => new Domain.SliceFile(p, i, "why"))]);
        var result = new SliceResult([Slice("a", "Shared.cs", "A.cs"), Slice("b", "Shared.cs", "B.cs")], []);
        var units = Paths.ToDictionary(p => p, p => new Domain.UnitFingerprint(p, p, "fp-" + p, 10), StringComparer.Ordinal);
        Domain.ReviewMark Mark(string unit, string? fingerprint = null) => new(unit, fingerprint ?? units[unit].Fingerprint, DateTimeOffset.UnixEpoch);

        // Slice a reviewed: A.cs is done, but Shared.cs still waits for slice b.
        var sliceA = Tracking.FilesNowReviewed(result, units, [Mark("Shared.cs"), Mark("A.cs")], ["Shared.cs", "A.cs"]);
        Assert.Equal(["A.cs"], sliceA);

        // Slice b reviewed too: Shared.cs and B.cs tick.
        var both = Tracking.FilesNowReviewed(result, units, [Mark("Shared.cs"), Mark("A.cs"), Mark("B.cs")], ["B.cs", "Shared.cs"]);
        Assert.Equal(["B.cs", "Shared.cs"], both.Order());

        // A mark on an old fingerprint doesn't count.
        Assert.Empty(Tracking.FilesNowReviewed(result, units, [Mark("Shared.cs"), Mark("A.cs", "stale")], ["A.cs"]));
    }

    // ---------------------------------------------------------------- helpers

    private void Commit(string path, Func<string, string> edit)
    {
        _repo.Write(path, edit(File.ReadAllText(_repo.PathOf(path))));
        _repo.Git("add", "-A");
        _repo.Git("commit", "-q", "-m", "Follow-up to " + Path.GetFileName(path));
    }

    private async Task<ReviewProgress> LocalProgressAsync() =>
        (await Client.ListLocalBranchesAsync(new ListLocalBranchesRequest())).Items.Single().Progress;

    private async Task<List<PullRequestEvent>> WatchAsync(bool refresh)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var call = Client.WatchPullRequest(new WatchPullRequestRequest { Local = Branch, Refresh = refresh }, cancellationToken: cts.Token);
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
