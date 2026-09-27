using Lumen.Domain;
using Microsoft.Data.Sqlite;

namespace Lumen.Storage.Tests;

public sealed class SqliteReviewStoreTests : IDisposable
{
    private static readonly PullRequestKey Pr1 = new(new RepositoryRef("octo", "lumen"), 1);
    private static readonly PullRequestKey Pr2 = new(new RepositoryRef("octo", "lumen"), 2);
    private static readonly PullRequestKey OtherRepoPr1 = new(new RepositoryRef("octo", "other"), 1);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumen-storage-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "nested", "lumen.db");

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
    public async Task CreatesDirectoryAndDatabaseOnFirstUse()
    {
        await using var store = new SqliteReviewStore(DatabasePath);

        Assert.True(Directory.Exists(Path.GetDirectoryName(DatabasePath)));
        Assert.Empty(await store.GetLatestActionsAsync(Pr1, Ct));
        Assert.True(File.Exists(DatabasePath));
    }

    [Fact]
    public async Task MigrationIsIdempotentAcrossReopens()
    {
        await using (var first = new SqliteReviewStore(DatabasePath))
        {
            await first.RecordInteractionAsync(Interaction(Pr1, "p1", ReviewAction.Dismissed), Ct);
        }

        await using (var second = new SqliteReviewStore(DatabasePath))
        {
            var actions = await second.GetLatestActionsAsync(Pr1, Ct);
            Assert.Equal(ReviewAction.Dismissed, actions["p1"]);
        }

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync(Ct);
        using var versions = connection.CreateCommand();
        versions.CommandText = "SELECT COUNT(*), MAX(Version) FROM SchemaVersion;";
        using var reader = await versions.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));

        using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)await journal.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task LatestActionWinsPerReviewPoint()
    {
        await using var store = new SqliteReviewStore(DatabasePath);

        await store.RecordInteractionAsync(Interaction(Pr1, "p1", ReviewAction.Dismissed), Ct);
        await store.RecordInteractionAsync(Interaction(Pr1, "p1", ReviewAction.Restored, headSha: "def"), Ct);
        await store.RecordInteractionAsync(Interaction(Pr1, "p2", ReviewAction.Examined), Ct);
        await store.RecordInteractionAsync(Interaction(Pr1, "p2", ReviewAction.Commented, detail: "https://example/c/1"), Ct);

        var actions = await store.GetLatestActionsAsync(Pr1, Ct);

        Assert.Equal(2, actions.Count);
        Assert.Equal(ReviewAction.Restored, actions["p1"]);
        Assert.Equal(ReviewAction.Commented, actions["p2"]);
    }

    [Fact]
    public async Task LatestIsByInsertionOrderNotTimestamp()
    {
        await using var store = new SqliteReviewStore(DatabasePath);
        var now = DateTimeOffset.UtcNow;

        await store.RecordInteractionAsync(Interaction(Pr1, "p1", ReviewAction.Dismissed, at: now), Ct);
        await store.RecordInteractionAsync(Interaction(Pr1, "p1", ReviewAction.Restored, at: now.AddHours(-1)), Ct);

        Assert.Equal(ReviewAction.Restored, (await store.GetLatestActionsAsync(Pr1, Ct))["p1"]);
    }

    [Fact]
    public async Task InteractionsAreIsolatedBetweenPullRequests()
    {
        await using var store = new SqliteReviewStore(DatabasePath);

        await store.RecordInteractionAsync(Interaction(Pr1, "p1", ReviewAction.Dismissed), Ct);
        await store.RecordInteractionAsync(Interaction(Pr2, "p1", ReviewAction.Commented), Ct);
        await store.RecordInteractionAsync(Interaction(OtherRepoPr1, "p9", ReviewAction.Examined), Ct);

        var pr1 = await store.GetLatestActionsAsync(Pr1, Ct);
        var pr2 = await store.GetLatestActionsAsync(Pr2, Ct);
        var other = await store.GetLatestActionsAsync(OtherRepoPr1, Ct);

        Assert.Equal(ReviewAction.Dismissed, Assert.Single(pr1).Value);
        Assert.Equal(ReviewAction.Commented, Assert.Single(pr2).Value);
        Assert.Equal("p9", Assert.Single(other).Key);
    }

    [Fact]
    public async Task StoresRepositoryAsFullNameAndRoundTripTimestamp()
    {
        await using var store = new SqliteReviewStore(DatabasePath);
        var at = new DateTimeOffset(2026, 9, 27, 10, 30, 15, 123, TimeSpan.FromHours(10));

        await store.RecordInteractionAsync(Interaction(Pr1, "p1", ReviewAction.Dismissed, at: at), Ct);

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync(Ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Repository, Action, At FROM ReviewInteractions;";
        using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal("octo/lumen", reader.GetString(0));
        Assert.Equal("Dismissed", reader.GetString(1));
        Assert.Equal(at, DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task CacheSetOverwriteAndMissing()
    {
        await using var store = new SqliteReviewStore(DatabasePath);

        Assert.Null(await store.GetCacheAsync("missing", Ct));

        await store.SetCacheAsync("k", "v1", Ct);
        Assert.Equal("v1", await store.GetCacheAsync("k", Ct));

        await store.SetCacheAsync("k", "v2", Ct);
        Assert.Equal("v2", await store.GetCacheAsync("k", Ct));
        Assert.Null(await store.GetCacheAsync("K", Ct));
    }

    [Fact]
    public async Task ConcurrentWritesFromManyTasksAreAllPersisted()
    {
        await using var store = new SqliteReviewStore(DatabasePath);
        const int writers = 8;
        const int perWriter = 25;

        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(
            async () =>
            {
                for (var i = 0; i < perWriter; i++)
                {
                    await store.RecordInteractionAsync(Interaction(Pr1, $"w{w}-p{i}", ReviewAction.Examined), Ct);
                    await store.SetCacheAsync($"w{w}", i.ToString(System.Globalization.CultureInfo.InvariantCulture), Ct);
                }
            },
            Ct));

        await Task.WhenAll(tasks);

        var actions = await store.GetLatestActionsAsync(Pr1, Ct);
        Assert.Equal(writers * perWriter, actions.Count);
        for (var w = 0; w < writers; w++)
        {
            Assert.Equal((perWriter - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), await store.GetCacheAsync($"w{w}", Ct));
        }
    }

    [Fact]
    public async Task ConcurrentFirstUseMigratesOnce()
    {
        var stores = Enumerable.Range(0, 4).Select(_ => new SqliteReviewStore(DatabasePath)).ToList();
        try
        {
            await Task.WhenAll(stores.Select(s => Task.Run(() => s.SetCacheAsync("k", "v", Ct), Ct)));
        }
        finally
        {
            foreach (var store in stores)
            {
                await store.DisposeAsync();
            }
        }

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync(Ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SchemaVersion;";
        Assert.Equal(2L, (long?)await command.ExecuteScalarAsync(Ct));
    }

    private static ReviewInteraction Interaction(
        PullRequestKey key,
        string reviewPointId,
        ReviewAction action,
        string headSha = "abc",
        DateTimeOffset? at = null,
        string? detail = null) =>
        new(key, headSha, reviewPointId, action, at ?? DateTimeOffset.UtcNow, detail);
}
