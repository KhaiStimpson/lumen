using System.Globalization;
using Dapper;
using Lumen.Domain;
using Microsoft.Data.Sqlite;

namespace Lumen.Storage;

/// <summary>SQLite-backed <see cref="IReviewStore"/> (TDD §32–§33). Opens a pooled connection per operation.</summary>
public sealed class SqliteReviewStore : IReviewStore, IAsyncDisposable
{
    private const int BusyTimeoutMilliseconds = 5000;

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

    public async Task RecordInteractionAsync(ReviewInteraction interaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        const string sql = """
            INSERT INTO ReviewInteractions (Repository, PullRequest, HeadSha, ReviewPointId, Action, At, Detail)
            VALUES (@Repository, @PullRequest, @HeadSha, @ReviewPointId, @Action, @At, @Detail);
            """;

        var parameters = new
        {
            Repository = interaction.Key.Repository.FullName,
            PullRequest = interaction.Key.Number,
            interaction.HeadSha,
            interaction.ReviewPointId,
            Action = interaction.Action.ToString(),
            At = interaction.At.ToString("O", CultureInfo.InvariantCulture),
            interaction.Detail,
        };

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyDictionary<string, ReviewAction>> GetLatestActionsAsync(
        PullRequestKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        const string sql = """
            SELECT ReviewPointId, Action
            FROM ReviewInteractions
            WHERE Id IN (
                SELECT MAX(Id)
                FROM ReviewInteractions
                WHERE Repository = @Repository AND PullRequest = @PullRequest
                GROUP BY ReviewPointId);
            """;

        var parameters = new { Repository = key.Repository.FullName, PullRequest = key.Number };

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection
                .QueryAsync<(string ReviewPointId, string Action)>(
                    new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            var result = new Dictionary<string, ReviewAction>(StringComparer.Ordinal);
            foreach (var (reviewPointId, action) in rows)
            {
                if (Enum.TryParse<ReviewAction>(action, ignoreCase: false, out var parsed))
                {
                    result[reviewPointId] = parsed;
                }
            }

            return result;
        }
    }

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
}
