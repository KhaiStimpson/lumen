using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Lumen.Domain;
using Microsoft.Data.Sqlite;

namespace Lumen.Storage;

/// <summary>SQLite-backed stores (TDD §32–§33). Opens a pooled connection per operation. Never holds credentials.</summary>
public sealed class SqliteReviewStore : IReviewStore, ITriageStore, IPullRequestVisitStore, IReviewMarkStore, IAsyncDisposable
{
    private const int BusyTimeoutMilliseconds = 5000;

    private static readonly JsonSerializerOptions ResultJson = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;
    private bool _disposed;

    public SqliteReviewStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>%LOCALAPPDATA%/Lumen/lumen.db; the directory is created if needed.</summary>
    public static string GetDefaultDatabasePath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "Lumen");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "lumen.db");
    }

    public static SqliteReviewStore CreateDefault() => new(GetDefaultDatabasePath());

    public async Task<string?> GetCacheAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection
                .QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                    "SELECT Value FROM CacheEntries WHERE Key = @Key;",
                    new { Key = key },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    public async Task SetCacheAsync(string key, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        const string sql = """
            INSERT INTO CacheEntries (Key, Value, UpdatedAt) VALUES (@Key, @Value, @UpdatedAt)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value, UpdatedAt = excluded.UpdatedAt;
            """;

        var parameters = new
        {
            Key = key,
            Value = value,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    public async Task<TriageResult?> FindTriageAsync(ReviewTarget target, string headSha, string version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        const string sql = """
            SELECT Result FROM TriageResults
            WHERE Target = @Target AND HeadSha = @HeadSha AND Version = @Version;
            """;

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var json = await connection
                .QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                    sql,
                    new { Target = target.Key, HeadSha = headSha, Version = version },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return json is null ? null : JsonSerializer.Deserialize<TriageResult>(json, ResultJson);
        }
    }

    public async Task SaveTriageAsync(ReviewTarget target, string headSha, string version, TriageResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(result);

        const string sql = """
            INSERT INTO TriageResults (Target, HeadSha, Version, Result, At)
            VALUES (@Target, @HeadSha, @Version, @Result, @At)
            ON CONFLICT(Target, HeadSha, Version) DO UPDATE SET Result = excluded.Result, At = excluded.At;
            """;

        var parameters = new
        {
            Target = target.Key,
            HeadSha = headSha,
            Version = version,
            Result = JsonSerializer.Serialize(result, ResultJson),
            At = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    public Task RecordOpenedAsync(ReviewTarget target, string headSha, DateTimeOffset at, CancellationToken cancellationToken) =>
        UpsertVisitAsync(target, "LastOpenedAt", "LastOpenedHead", headSha, at, cancellationToken);

    public Task RecordReviewedAsync(ReviewTarget target, string headSha, DateTimeOffset at, CancellationToken cancellationToken) =>
        UpsertVisitAsync(target, "LastReviewedAt", "LastReviewedHead", headSha, at, cancellationToken);

    private async Task UpsertVisitAsync(
        ReviewTarget target,
        string atColumn,
        string headColumn,
        string headSha,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(headSha);

        // Column names are constants chosen above, never input.
        var sql = $"""
            INSERT INTO Visits (Target, {atColumn}, {headColumn}) VALUES (@Target, @At, @Head)
            ON CONFLICT(Target) DO UPDATE SET {atColumn} = excluded.{atColumn}, {headColumn} = excluded.{headColumn};
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    sql,
                    new { Target = target.Key, At = at.ToString("O", CultureInfo.InvariantCulture), Head = headSha },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    public async Task<PullRequestVisit?> GetVisitAsync(ReviewTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var row = await connection.QuerySingleOrDefaultAsync<VisitRow>(new CommandDefinition(
                    "SELECT * FROM Visits WHERE Target = @Target;",
                    new { Target = target.Key },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return row is null ? null : ToVisit(row);
        }
    }

    public async Task<IReadOnlyDictionary<string, PullRequestVisit>> GetVisitsAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<VisitRow>(new CommandDefinition("SELECT * FROM Visits;", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            var visits = new Dictionary<string, PullRequestVisit>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (ToVisit(row) is { } visit)
                {
                    visits[visit.Target.Key] = visit;
                }
            }

            return visits;
        }
    }

    private static PullRequestVisit? ToVisit(VisitRow row)
    {
        if (!ReviewTarget.TryParse(row.Target, out var target))
        {
            return null;
        }

        static DateTimeOffset? Parse(string? value) =>
            value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new PullRequestVisit(target, Parse(row.LastOpenedAt), row.LastOpenedHead, Parse(row.LastReviewedAt), row.LastReviewedHead);
    }

    public async Task<IReadOnlyList<ReviewMark>> GetMarksAsync(ReviewTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<MarkRow>(new CommandDefinition(
                    "SELECT Unit, Fingerprint, At FROM Marks WHERE Target = @Target;",
                    new { Target = target.Key },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return [.. rows.Select(r => new ReviewMark(r.Unit, r.Fingerprint, DateTimeOffset.Parse(r.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)))];
        }
    }

    public async Task SetMarkAsync(ReviewTarget target, ReviewMark mark, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(mark);
        const string sql = """
            INSERT INTO Marks (Target, Unit, Fingerprint, At) VALUES (@Target, @Unit, @Fingerprint, @At)
            ON CONFLICT(Target, Unit) DO UPDATE SET Fingerprint = excluded.Fingerprint, At = excluded.At;
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    sql,
                    new { Target = target.Key, mark.Unit, mark.Fingerprint, At = mark.At.ToString("O", CultureInfo.InvariantCulture) },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    public async Task RemoveMarkAsync(ReviewTarget target, string unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM Marks WHERE Target = @Target AND Unit = @Unit;",
                    new { Target = target.Key, Unit = unit },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        using (var connection = new SqliteConnection(_connectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        _initLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await OpenRawAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                    $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};",
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var connection = await OpenRawAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                        "PRAGMA journal_mode = WAL;",
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                await SchemaMigrator.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private sealed record VisitRow(
        string Target,
        string? LastOpenedAt,
        string? LastOpenedHead,
        string? LastReviewedAt,
        string? LastReviewedHead);

    private sealed record MarkRow(string Unit, string Fingerprint, string At);
}
