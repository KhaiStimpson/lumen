using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Lumen.Domain;
using Microsoft.Data.Sqlite;

namespace Lumen.Storage;

/// <summary>SQLite-backed stores (TDD §32–§33). Opens a pooled connection per operation. Never holds credentials.</summary>
public sealed class SqliteReviewStore : IReviewStore, IAttentionEvaluationStore, IInvestigationStore, ITriageStore, IAsyncDisposable
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

    public async Task RecordEvaluationsAsync(IReadOnlyList<AttentionEvaluationRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        const string sql = """
            INSERT INTO AttentionEvaluations (Repository, PullRequest, HeadSha, ChangeUnitId, CandidateKey, QuestionId,
                QuestionSchemaVersion, Choice, Probabilities, Model, ResolvedModel, Provider, State, LatencyMs, At)
            VALUES (@Repository, @PullRequest, @HeadSha, @ChangeUnitId, @CandidateKey, @QuestionId,
                @QuestionSchemaVersion, @Choice, @Probabilities, @Model, @ResolvedModel, @Provider, @State, @LatencyMs, @At);
            """;

        var rows = records.Select(r => new
        {
            Repository = r.Key.Repository.FullName,
            PullRequest = r.Key.Number,
            r.HeadSha,
            r.ChangeUnitId,
            r.CandidateKey,
            r.QuestionId,
            r.QuestionSchemaVersion,
            r.Choice,
            Probabilities = r.Probabilities is null ? null : JsonSerializer.Serialize(r.Probabilities),
            r.Model,
            r.ResolvedModel,
            r.Provider,
            State = r.StateJson,
            r.LatencyMs,
            At = r.At.ToString("O", CultureInfo.InvariantCulture),
        }).ToList();

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var transaction = connection.BeginTransaction();
            await connection.ExecuteAsync(new CommandDefinition(sql, rows, transaction, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            transaction.Commit();
        }
    }

    public async Task<IReadOnlyList<AttentionEvaluationRecord>> GetEvaluationsAsync(
        PullRequestKey key,
        string headSha,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        const string sql = """
            SELECT HeadSha, ChangeUnitId, CandidateKey, QuestionId, QuestionSchemaVersion, Choice, Probabilities,
                   Model, ResolvedModel, Provider, State, LatencyMs, At
            FROM AttentionEvaluations
            WHERE Repository = @Repository AND PullRequest = @PullRequest AND HeadSha = @HeadSha
            ORDER BY Id;
            """;

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<EvaluationRow>(new CommandDefinition(
                    sql,
                    new { Repository = key.Repository.FullName, PullRequest = key.Number, HeadSha = headSha },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            return [.. rows.Select(r => new AttentionEvaluationRecord
            {
                Key = key,
                HeadSha = r.HeadSha,
                ChangeUnitId = r.ChangeUnitId,
                CandidateKey = r.CandidateKey,
                QuestionId = r.QuestionId,
                QuestionSchemaVersion = r.QuestionSchemaVersion,
                Choice = r.Choice,
                Probabilities = r.Probabilities is null ? null : JsonSerializer.Deserialize<Dictionary<string, double>>(r.Probabilities),
                Model = r.Model,
                ResolvedModel = r.ResolvedModel,
                Provider = r.Provider,
                StateJson = r.State,
                LatencyMs = r.LatencyMs,
                At = DateTimeOffset.Parse(r.At, CultureInfo.InvariantCulture),
            })];
        }
    }

    public async Task SaveInvestigationAsync(
        PullRequestKey key,
        string headSha,
        string candidateKey,
        InvestigationResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(result);

        const string sql = """
            INSERT INTO Investigations (Repository, PullRequest, HeadSha, CandidateKey, Type, Outcome, Producer, Result, At)
            VALUES (@Repository, @PullRequest, @HeadSha, @CandidateKey, @Type, @Outcome, @Producer, @Result, @At);
            """;

        var parameters = new
        {
            Repository = key.Repository.FullName,
            PullRequest = key.Number,
            HeadSha = headSha,
            CandidateKey = candidateKey,
            Type = result.Type.ToString(),
            Outcome = result.Outcome.ToString(),
            result.Producer,
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

    public async Task<InvestigationResult?> FindInvestigationAsync(
        PullRequestKey key,
        string headSha,
        string candidateKey,
        InvestigationType type,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        const string sql = """
            SELECT Result FROM Investigations
            WHERE Repository = @Repository AND PullRequest = @PullRequest AND HeadSha = @HeadSha
              AND CandidateKey = @CandidateKey AND Type = @Type
            ORDER BY Id DESC LIMIT 1;
            """;

        var parameters = new
        {
            Repository = key.Repository.FullName,
            PullRequest = key.Number,
            HeadSha = headSha,
            CandidateKey = candidateKey,
            Type = type.ToString(),
        };

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var json = await connection
                .QuerySingleOrDefaultAsync<string?>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return json is null ? null : JsonSerializer.Deserialize<InvestigationResult>(json, ResultJson);
        }
    }

    public async Task<int> CountInvestigationsAsync(PullRequestKey key, string headSha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM Investigations WHERE Repository = @Repository AND PullRequest = @PullRequest AND HeadSha = @HeadSha;",
                    new { Repository = key.Repository.FullName, PullRequest = key.Number, HeadSha = headSha },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    public async Task<TriageResult?> FindTriageAsync(PullRequestKey key, string headSha, string version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        const string sql = """
            SELECT Result FROM TriageResults
            WHERE Repository = @Repository AND PullRequest = @PullRequest AND HeadSha = @HeadSha AND Version = @Version;
            """;

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var json = await connection
                .QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                    sql,
                    new { Repository = key.Repository.FullName, PullRequest = key.Number, HeadSha = headSha, Version = version },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return json is null ? null : JsonSerializer.Deserialize<TriageResult>(json, ResultJson);
        }
    }

    public async Task SaveTriageAsync(PullRequestKey key, string headSha, string version, TriageResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(result);

        const string sql = """
            INSERT INTO TriageResults (Repository, PullRequest, HeadSha, Version, Result, At)
            VALUES (@Repository, @PullRequest, @HeadSha, @Version, @Result, @At)
            ON CONFLICT(Repository, PullRequest, HeadSha, Version) DO UPDATE SET Result = excluded.Result, At = excluded.At;
            """;

        var parameters = new
        {
            Repository = key.Repository.FullName,
            PullRequest = key.Number,
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

    private sealed record EvaluationRow(
        string HeadSha,
        string ChangeUnitId,
        string CandidateKey,
        string QuestionId,
        string QuestionSchemaVersion,
        string Choice,
        string? Probabilities,
        string Model,
        string? ResolvedModel,
        string Provider,
        string State,
        long LatencyMs,
        string At);
}
