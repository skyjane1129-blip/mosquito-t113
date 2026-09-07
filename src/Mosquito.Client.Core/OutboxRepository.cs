using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Mosquito.Client.Core;

public sealed class OutboxRepository
{
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public OutboxRepository(string localDataRoot)
    {
        Directory.CreateDirectory(localDataRoot);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(localDataRoot, "outbox.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS capture_outbox (
                capture_id TEXT PRIMARY KEY,
                state TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NULL,
                updated_at_utc TEXT NOT NULL,
                artifact_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_capture_outbox_state
                ON capture_outbox (state, updated_at_utc);
            """;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAsync(CaptureArtifact artifact, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO capture_outbox
                (capture_id, state, attempts, last_error, updated_at_utc, artifact_json)
            VALUES
                ($id, $state, 0, $error, $updated, $json)
            ON CONFLICT(capture_id) DO UPDATE SET
                state = excluded.state,
                last_error = excluded.last_error,
                updated_at_utc = excluded.updated_at_utc,
                artifact_json = excluded.artifact_json
            """;
        await ExecuteWriteAsync(sql, artifact, incrementAttempts: false, cancellationToken);
    }

    public async Task MarkAsync(
        CaptureArtifact artifact,
        CaptureState state,
        string? error,
        bool incrementAttempts,
        CancellationToken cancellationToken)
    {
        var updated = artifact with { State = state, LastError = error };
        const string sql = """
            UPDATE capture_outbox SET
                state = $state,
                attempts = attempts + $attempt_increment,
                last_error = $error,
                updated_at_utc = $updated,
                artifact_json = $json
            WHERE capture_id = $id
            """;
        await ExecuteWriteAsync(sql, updated, incrementAttempts, cancellationToken);
    }

    public async Task<IReadOnlyList<CaptureArtifact>> GetPendingAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT artifact_json FROM capture_outbox
            WHERE state IN ('PendingUpload', 'Failed')
              AND attempts < 5
            ORDER BY updated_at_utc
            """;
        var captures = new List<CaptureArtifact>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var artifact = JsonSerializer.Deserialize<CaptureArtifact>(reader.GetString(0), _jsonOptions);
            if (artifact is not null)
            {
                captures.Add(artifact);
            }
        }
        return captures;
    }

    private async Task ExecuteWriteAsync(
        string sql,
        CaptureArtifact artifact,
        bool incrementAttempts,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", artifact.CaptureId.ToString("D"));
        command.Parameters.AddWithValue("$state", artifact.State.ToString());
        command.Parameters.AddWithValue("$attempt_increment", incrementAttempts ? 1 : 0);
        command.Parameters.AddWithValue("$error", (object?)artifact.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(artifact, _jsonOptions));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"Outbox record {artifact.CaptureId} was not found.");
        }
    }
}
