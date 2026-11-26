using Lumen.Domain;
using Microsoft.Data.Sqlite;

namespace Lumen.Storage.Tests;

public sealed class SqliteReviewStoreTests : IDisposable
{
    private static readonly PullRequestTarget Pr1 = new(new PullRequestKey(new RepositoryRef("octo", "lumen"), 1));

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

        Assert.Null(await store.GetCacheAsync("missing", Ct));
        Assert.True(File.Exists(DatabasePath));
    }

    [Fact]
    public async Task MigrationIsIdempotentAcrossReopens()
    {
        await using (var first = new SqliteReviewStore(DatabasePath))
        {
            await first.SetCacheAsync("k", "v", Ct);
        }

        await using var second = new SqliteReviewStore(DatabasePath);
        Assert.Equal("v", await second.GetCacheAsync("k", Ct));
        Assert.Equal(SchemaVersions(), [1L, 2L, 3L, 4L, 5L]);
    }

    [Fact]
    public async Task CacheSetOverwriteAndMissing()
    {
        await using var store = new SqliteReviewStore(DatabasePath);

        await store.SetCacheAsync("a", "1", Ct);
        await store.SetCacheAsync("a", "2", Ct);

        Assert.Equal("2", await store.GetCacheAsync("a", Ct));
        Assert.Null(await store.GetCacheAsync("b", Ct));
    }

    [Fact]
    public async Task ConcurrentWritesFromManyTasksAreAllPersisted()
    {
        await using var store = new SqliteReviewStore(DatabasePath);
        const int writers = 8;
        const int perWriter = 25;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                await store.SetCacheAsync($"w{w}", i.ToString(System.Globalization.CultureInfo.InvariantCulture), Ct);
                await store.SetMarkAsync(Pr1, new ReviewMark($"w{w}-{i}", "fp", DateTimeOffset.UnixEpoch), Ct);
            }
        }, Ct)));

        for (var w = 0; w < writers; w++)
        {
            Assert.Equal((perWriter - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), await store.GetCacheAsync($"w{w}", Ct));
        }

        Assert.Equal(writers * perWriter, (await store.GetMarksAsync(Pr1, Ct)).Count);
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

        Assert.Equal([1L, 2L, 3L, 4L, 5L], SchemaVersions());
    }

    /// <summary>
    /// The overhaul's migration drops the AI tables (JEV evaluations, investigations, walkthroughs, review-point
    /// interactions) and re-keys visits on review target, keeping what the reviewer had.
    /// </summary>
    [Fact]
    public async Task ADatabaseFromBeforeTheOverhaulKeepsItsVisitsAndLosesTheAiTables()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            await connection.OpenAsync(Ct);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE SchemaVersion (Version INTEGER NOT NULL);
                INSERT INTO SchemaVersion VALUES (1), (2), (3), (4);
                CREATE TABLE ReviewInteractions (Id INTEGER PRIMARY KEY, Repository TEXT, PullRequest INTEGER, HeadSha TEXT, ReviewPointId TEXT, Action TEXT, At TEXT, Detail TEXT);
                CREATE TABLE CacheEntries (Key TEXT PRIMARY KEY, Value TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                CREATE TABLE AttentionEvaluations (Id INTEGER PRIMARY KEY);
                CREATE TABLE Investigations (Id INTEGER PRIMARY KEY);
                CREATE TABLE Walkthroughs (Repository TEXT);
                CREATE TABLE TriageResults (Repository TEXT, PullRequest INTEGER, HeadSha TEXT, Version TEXT, Result TEXT, At TEXT);
                CREATE TABLE PullRequestVisits (Repository TEXT NOT NULL, PullRequest INTEGER NOT NULL, LastOpenedAt TEXT NULL,
                    LastOpenedHead TEXT NULL, LastReviewedAt TEXT NULL, LastReviewedHead TEXT NULL, PRIMARY KEY (Repository, PullRequest));
                INSERT INTO PullRequestVisits VALUES ('octo/lumen', 1, '2026-10-01T09:00:00.0000000+00:00', 'head-1', NULL, NULL);
                INSERT INTO CacheEntries VALUES ('k', 'kept', '2026-10-01');
                """;
            await command.ExecuteNonQueryAsync(Ct);
        }

        SqliteConnection.ClearAllPools();
        await using var store = new SqliteReviewStore(DatabasePath);

        Assert.Equal("head-1", (await store.GetVisitAsync(Pr1, Ct))?.LastOpenedHead);
        Assert.Equal("kept", await store.GetCacheAsync("k", Ct));
        var tables = Tables();
        Assert.DoesNotContain("AttentionEvaluations", tables);
        Assert.DoesNotContain("Investigations", tables);
        Assert.DoesNotContain("Walkthroughs", tables);
        Assert.DoesNotContain("ReviewInteractions", tables);
        Assert.DoesNotContain("PullRequestVisits", tables);
        Assert.Contains("Marks", tables);
        Assert.Contains("Visits", tables);
    }

    private List<long> SchemaVersions()
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM SchemaVersion ORDER BY Version;";
        using var reader = command.ExecuteReader();
        var versions = new List<long>();
        while (reader.Read())
        {
            versions.Add(reader.GetInt64(0));
        }

        return versions;
    }

    private HashSet<string> Tables()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
