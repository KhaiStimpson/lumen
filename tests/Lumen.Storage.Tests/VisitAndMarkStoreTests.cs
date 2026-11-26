using Lumen.Domain;
using Microsoft.Data.Sqlite;

namespace Lumen.Storage.Tests;

public sealed class VisitAndMarkStoreTests : IDisposable
{
    private static readonly PullRequestTarget Pr = new(new PullRequestKey(new RepositoryRef("octo", "lumen"), 7));
    private static readonly PullRequestTarget Other = new(new PullRequestKey(new RepositoryRef("octo", "tools"), 3));
    private static readonly LocalBranchTarget Local = new("C:/src/shop", "main", LocalHeadKind.WorkingTree);

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
        await store.RecordOpenedAsync(Local, "snapshot-tree", t2, Ct);

        var visit = await store.GetVisitAsync(Pr, Ct);
        Assert.Equal(new PullRequestVisit(Pr, t2, "head-2", t1.AddMinutes(5), "head-1"), visit);

        var all = await store.GetVisitsAsync(Ct);
        Assert.Equal(3, all.Count);
        Assert.Null(all[Other.Key].LastReviewedAt);
        Assert.Equal(Local, all[Local.Key].Target);
        Assert.Equal("snapshot-tree", all[Local.Key].LastOpenedHead);
    }

    [Fact]
    public async Task MarksAreStoredPerTargetAndUnitWithTheirFingerprint()
    {
        await using var store = new SqliteReviewStore(DatabasePath);
        var at = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

        await store.SetMarkAsync(Pr, new ReviewMark("src/A.cs", "fp-1", at), Ct);
        await store.SetMarkAsync(Pr, new ReviewMark("src/B.cs::Big.Run", "fp-2", at), Ct);
        await store.SetMarkAsync(Pr, new ReviewMark("src/A.cs", "fp-3", at.AddHours(1)), Ct);
        await store.SetMarkAsync(Local, new ReviewMark("src/A.cs", "local", at), Ct);

        var marks = (await store.GetMarksAsync(Pr, Ct)).OrderBy(m => m.Unit, StringComparer.Ordinal).ToList();
        Assert.Equal([("src/A.cs", "fp-3"), ("src/B.cs::Big.Run", "fp-2")], marks.Select(m => (m.Unit, m.Fingerprint)));
        Assert.Equal(at.AddHours(1), marks[0].At);
        Assert.Equal("local", Assert.Single(await store.GetMarksAsync(Local, Ct)).Fingerprint);

        await store.RemoveMarkAsync(Pr, "src/A.cs", Ct);
        Assert.Equal("src/B.cs::Big.Run", Assert.Single(await store.GetMarksAsync(Pr, Ct)).Unit);
        Assert.Empty(await store.GetMarksAsync(Other, Ct));
    }
}
