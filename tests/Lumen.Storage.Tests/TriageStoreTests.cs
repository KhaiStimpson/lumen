using Lumen.Domain;
using Microsoft.Data.Sqlite;

namespace Lumen.Storage.Tests;

public sealed class TriageStoreTests : IDisposable
{
    private static readonly PullRequestKey Pr = new(new RepositoryRef("octo", "lumen"), 7);

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

    private static TriageResult Result(string path)
    {
        var hunk = new HunkTriage
        {
            Path = path,
            OldStart = 3,
            OldEnd = 4,
            NewStart = 3,
            NewEnd = 4,
            Class = ChangeClass.Rename,
            Tier = TriageTier.Skip,
            Reasons = ["pure rename `Foo`→`Bar`"],
            ChangedLines = 4,
            GroupId = "g0",
        };
        return new TriageResult([hunk], [new TriageGroup { Id = "g0", Class = ChangeClass.Rename, Title = "Renamed `Foo`→`Bar`", Members = [hunk] }]);
    }

    [Fact]
    public async Task TriageRoundTripsPerHeadAndVersion()
    {
        await using var store = new SqliteReviewStore(DatabasePath);
        await store.SaveTriageAsync(Pr, "head-1", "v1", Result("a.cs"), Ct);

        var found = await store.FindTriageAsync(Pr, "head-1", "v1", Ct);

        var hunk = Assert.Single(found!.Hunks);
        Assert.Equal(("a.cs", ChangeClass.Rename, TriageTier.Skip, 4, "g0"), (hunk.Path, hunk.Class, hunk.Tier, hunk.ChangedLines, hunk.GroupId));
        Assert.Equal(["pure rename `Foo`→`Bar`"], hunk.Reasons);
        Assert.Equal("Renamed `Foo`→`Bar`", Assert.Single(found.Groups).Title);
    }

    [Fact]
    public async Task OtherHeadsVersionsAndPullRequestsMiss()
    {
        await using var store = new SqliteReviewStore(DatabasePath);
        await store.SaveTriageAsync(Pr, "head-1", "v1", Result("a.cs"), Ct);

        Assert.Null(await store.FindTriageAsync(Pr, "head-2", "v1", Ct));
        Assert.Null(await store.FindTriageAsync(Pr, "head-1", "v2", Ct));
        Assert.Null(await store.FindTriageAsync(Pr with { Number = 8 }, "head-1", "v1", Ct));
    }

    [Fact]
    public async Task SavingAgainReplacesAndSurvivesReopening()
    {
        await using (var store = new SqliteReviewStore(DatabasePath))
        {
            await store.SaveTriageAsync(Pr, "head-1", "v1", Result("a.cs"), Ct);
            await store.SaveTriageAsync(Pr, "head-1", "v1", Result("b.cs"), Ct);
        }

        await using var reopened = new SqliteReviewStore(DatabasePath);
        var found = await reopened.FindTriageAsync(Pr, "head-1", "v1", Ct);

        Assert.Equal("b.cs", Assert.Single(found!.Hunks).Path);
    }
}
