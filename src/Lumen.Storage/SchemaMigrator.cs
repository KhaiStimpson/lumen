using Dapper;
using Microsoft.Data.Sqlite;

namespace Lumen.Storage;

/// <summary>Forward-only migrator: script N (1-based) upgrades the schema to version N.</summary>
internal static class SchemaMigrator
{
    public static IReadOnlyList<string> Migrations { get; } =
    [
        """
        CREATE TABLE ReviewInteractions (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Repository TEXT NOT NULL,
            PullRequest INTEGER NOT NULL,
            HeadSha TEXT NOT NULL,
            ReviewPointId TEXT NOT NULL,
            Action TEXT NOT NULL,
            At TEXT NOT NULL,
            Detail TEXT NULL);

        CREATE INDEX IX_ReviewInteractions_Point
            ON ReviewInteractions (Repository, PullRequest, ReviewPointId);

        CREATE TABLE CacheEntries (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL);
        """,
        """
        CREATE TABLE AttentionEvaluations (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Repository TEXT NOT NULL,
            PullRequest INTEGER NOT NULL,
            HeadSha TEXT NOT NULL,
            ChangeUnitId TEXT NOT NULL,
            CandidateKey TEXT NOT NULL,
            QuestionId TEXT NOT NULL,
            QuestionSchemaVersion TEXT NOT NULL,
            Choice TEXT NOT NULL,
            Probabilities TEXT NULL,
            Model TEXT NOT NULL,
            ResolvedModel TEXT NULL,
            Provider TEXT NOT NULL,
            State TEXT NOT NULL,
            LatencyMs INTEGER NOT NULL,
            At TEXT NOT NULL);

        CREATE INDEX IX_AttentionEvaluations_Head
            ON AttentionEvaluations (Repository, PullRequest, HeadSha);

        CREATE TABLE Investigations (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Repository TEXT NOT NULL,
            PullRequest INTEGER NOT NULL,
            HeadSha TEXT NOT NULL,
            CandidateKey TEXT NOT NULL,
            Type TEXT NOT NULL,
            Outcome TEXT NOT NULL,
            Producer TEXT NOT NULL,
            Result TEXT NOT NULL,
            At TEXT NOT NULL);

        CREATE INDEX IX_Investigations_Candidate
            ON Investigations (Repository, PullRequest, HeadSha, CandidateKey, Type);
        """,
        """
        CREATE TABLE TriageResults (
            Repository TEXT NOT NULL,
            PullRequest INTEGER NOT NULL,
            HeadSha TEXT NOT NULL,
            Version TEXT NOT NULL,
            Result TEXT NOT NULL,
            At TEXT NOT NULL,
            PRIMARY KEY (Repository, PullRequest, HeadSha, Version));
        """,
    ];

    public static int LatestVersion => Migrations.Count;

    public static async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // BEGIN IMMEDIATE (deferred: false) serializes concurrent migrators, including other processes.
        using var transaction = connection.BeginTransaction(deferred: false);

        await connection.ExecuteAsync(new CommandDefinition(
                "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL);",
                transaction: transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var current = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;",
                transaction: transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        for (var version = current + 1; version <= Migrations.Count; version++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    Migrations[version - 1],
                    transaction: transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO SchemaVersion (Version) VALUES (@Version);",
                    new { Version = version },
                    transaction: transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        transaction.Commit();
    }
}
