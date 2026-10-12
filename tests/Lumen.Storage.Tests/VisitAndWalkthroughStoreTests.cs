using Lumen.Domain;
using Microsoft.Data.Sqlite;

namespace Lumen.Storage.Tests;

public sealed class VisitAndWalkthroughStoreTests : IDisposable
{
    private static readonly PullRequestKey Pr = new(new RepositoryRef("octo", "lumen"), 7);
    private static readonly PullRequestKey Other = new(new RepositoryRef("octo", "tools"), 3);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumen-storage-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "lumen.db");

    private static CancellationToken Ct => CancellationToken.None;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task OpenedAndReviewedAreKeptSeparatelyAndOverwritten()
    {
        await using var store = new SqliteReviewStore(DatabasePath);
        var t1 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddHours(3);

        Assert.Null(await store.GetVisitAsync(Pr, Ct));

        await store.RecordOpenedAsync(Pr, "head-1", t1, Ct);
        await store.RecordReviewedAsync(Pr, "head-1", t1.AddMinutes(5), Ct);
        await store.RecordOpenedAsync(Pr, "head-2", t2, Ct);
        await store.RecordOpenedAsync(Other, "x", t1, Ct);

        var visit = await store.GetVisitAsync(Pr, Ct);
        Assert.Equal(new PullRequestVisit(Pr, t2, "head-2", t1.AddMinutes(5), "head-1"), visit);

        var all = await store.GetVisitsAsync(Ct);
        Assert.Equal(2, all.Count);
        Assert.Null(all[Other].LastReviewedAt);
    }

    [Fact]
    public async Task WalkthroughsAreCachedPerHeadScopeAndSettings()
    {
        await using var store = new SqliteReviewStore(DatabasePath);

        await store.SaveWalkthroughAsync(Pr, "head-1", "full", "h1", "{\"a\":1}", Ct);
        await store.SaveWalkthroughAsync(Pr, "head-1", "full", "h1", "{\"a\":2}", Ct);
        await store.SaveWalkthroughAsync(Pr, "head-1", "since:abc", "h1", "{\"b\":1}", Ct);

        Assert.Equal("{\"a\":2}", await store.FindWalkthroughAsync(Pr, "head-1", "full", "h1", Ct));
        Assert.Equal("{\"b\":1}", await store.FindWalkthroughAsync(Pr, "head-1", "since:abc", "h1", Ct));
        Assert.Null(await store.FindWalkthroughAsync(Pr, "head-1", "full", "h2", Ct));
        Assert.Null(await store.FindWalkthroughAsync(Pr, "head-2", "full", "h1", Ct));
    }
}
